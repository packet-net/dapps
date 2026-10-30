using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using dapps.client.Transport.Agw;
using DotNet.Testcontainers.Builders;

namespace dapps.core.tests.Integration.Rig425.RigB;

/// <summary>
/// How the nodes in Rig B route, which is what an arm needs from the rig.
/// </summary>
/// <param name="NetRomOnRf">NET/ROM on the MB7NPW to CALNE radio link (and
/// between CALNE and the peers), so CALNE is in MB7NPW's node table and
/// LinBPQ turns a connect to it into a NET/ROM circuit (Rig A).</param>
/// <param name="PeerAppsAdvertised">Each peer's LinBPQ advertises its DAPPS
/// call on NET/ROM (APPLICATION quality 200), so MB7NPW's LinBPQ makes a
/// NET/ROM circuit straight to it.</param>
public sealed record RigBProfile(string Id, bool NetRomOnRf, bool PeerAppsAdvertised)
{
    /// <summary>No NET/ROM anywhere: every connect is plain L2.</summary>
    public static readonly RigBProfile L2 = new("l2", NetRomOnRf: false, PeerAppsAdvertised: false);

    /// <summary>NET/ROM between the nodes; the DAPPS calls stay off it, as DAPPS docs say today.</summary>
    public static readonly RigBProfile NetRom = new("netrom", NetRomOnRf: true, PeerAppsAdvertised: false);

    /// <summary>NET/ROM, and the peers' DAPPS calls advertised on it.</summary>
    public static readonly RigBProfile NetRomApps = new("netrom-apps", NetRomOnRf: true, PeerAppsAdvertised: true);

    public static RigBProfile Parse(string id) => new[] { L2, NetRom, NetRomApps }.Single(p => p.Id == id);
}

/// <summary>One LinBPQ node in Rig B. <see cref="DappsCall"/> is null on CALNE, which runs no DAPPS.</summary>
public sealed record RigBNode(string Role, int Index, string Call, string Alias, string? DappsCall)
{
    /// <summary>LinBPQ's AGW port inside net-sim's network namespace.</summary>
    public int InsideAgwPort => 18300 + Index;
    public int InsideTelnetPort => 18310 + Index;
    public int InsideHttpPort => 18320 + Index;

    /// <summary>The UDP port this node's AXIP port listens on (CALNE and the peers).</summary>
    public int AxipUdpPort => 19300 + Index;

    public override string ToString() => $"{Role} ({Call})";
}

/// <summary>
/// Rig B for packet-net/dapps#425: Kevin M0AHN's layout with real LinBPQ
/// nodes and real DAPPS daemons, the first hop on a simulated radio channel.
///
///     DAPPS -AGW- MB7NPW -KISS- [net-sim: AFSK 1200 ~ FM channel ~ AFSK 1200] -KISS- CALNE -AXUDP- PEER1 -AGW- DAPPS
///                                                                                         \-AXUDP- PEER2 -AGW- DAPPS
///                                                                                          \-AXUDP- PEER3 -AGW- DAPPS
///
/// The MB7NPW to CALNE hop is the shared channel being measured: Dire Wolf
/// AFSK 1200 on net-sim v0.4.0's physical FM channel, as in
/// <see cref="NetSimAfsk1200Fixture"/> (same BPQ radio settings, 120 dB path
/// loss, squelch open). CALNE to each peer is AX.25 over UDP (BPQAXIP), so
/// it takes no airtime: what differs between arms is how the shared hop is
/// used. CALNE runs no DAPPS. Every LinBPQ shares net-sim's network
/// namespace, so AXUDP is on 127.0.0.1.
///
/// Ports: MB7NPW 1 Telnet, 2 Radio. CALNE 1 Telnet, 2 Radio, 3 AXIP.
/// Peers 1 Telnet, 2 AXIP. AGW port bytes are one less.
/// </summary>
public sealed class RigBNetwork : IAsyncDisposable
{
    private const int InsideWebPort = 8080;
    private const int KissPortA = 18201;
    private const int KissPortB = 18202;

    /// <summary>The BPQ radio-port settings of <see cref="NetSimAfsk1200Fixture"/>.</summary>
    public static readonly NetSimTwoBpqFixture.BpqRadio Radio =
        new(Speed: 1200, TxDelayMs: 150, Paclen: 120, Maxframe: 4, FrackMs: 7000, RespTimeMs: 1000, Retries: 10, Persist: 64, SlotTimeMs: 100);

    public const double PathLossDb = 120;

    /// <summary>NODES every hour on every port, as a quiet real network would; the rig sends the first round itself.</summary>
    public const int NodesIntervalMinutes = 60;

    public string Host => "127.0.0.1";
    public RigBProfile Profile { get; }
    public int NetSimWebPort { get; private set; }

    public RigBNode Mb7npw { get; } = new("MB7NPW", 0, "N0NPW", "NPW", "N0NPW-3");
    public RigBNode Calne { get; } = new("CALNE", 1, "N0CAL", "CALNE", null);
    public IReadOnlyList<RigBNode> Peers { get; } =
    [
        new("PEER1", 2, "N0AGM", "AGM", "N0AGM-3"),
        new("PEER2", 3, "N0LOX", "LOX", "N0LOX-3"),
        new("PEER3", 4, "N0EIK", "EIK", "N0EIK-3"),
    ];

    public IEnumerable<RigBNode> Nodes => new[] { Mb7npw, Calne }.Concat(Peers);

    /// <summary>The AGW port byte of the radio port on MB7NPW and CALNE (BPQ port 2).</summary>
    public const int RfAgwPort = 1;

    /// <summary>CALNE's BPQ port number for its AXIP port, as typed at its prompt.</summary>
    public const int CalneAxipPort = 3;

    /// <summary>A peer's AGW port byte for its AXIP port (BPQ port 2).</summary>
    public const int PeerAgwPort = 1;

    private WatchedContainer? netSim;
    private readonly List<WatchedContainer> bpqs = [];
    private readonly Dictionary<RigBNode, WatchedContainer> bpqByNode = [];

    /// <summary>
    /// The LinBPQ image for all five nodes: the DAPPS test pin unless
    /// DAPPS_RIG_425_B_BPQ_IMAGE names another (a patched build, say).
    /// </summary>
    public static string BpqImage => Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_BPQ_IMAGE") is { Length: > 0 } i ? i : LinbpqIntegrationFixture.Image;

    /// <summary>
    /// DAPPS_RIG_425_B_BPQ_V20=1 sets OnlyVer2point0=1 on every node, so
    /// LinBPQ's node-to-node links open with SABM (AX.25 2.0, REJ) instead of
    /// XID (2.2, SREJ).
    /// </summary>
    public static bool OnlyV20 => Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_BPQ_V20") == "1";

    /// <summary>For the results file: the image when not the pin, plus " v2.0" with OnlyVer2point0; null for the stock pin on its defaults.</summary>
    public static string? BpqLabel =>
        (BpqImage == LinbpqIntegrationFixture.Image ? "" : BpqImage)
        + (OnlyV20 ? " v2.0" : "") is { Length: > 0 } l ? l.Trim() : null;

    /// <summary>LinBPQ's debug log (logs/NodeDebuglog_*.log) on one node, lines stamped from <paramref name="since"/> on (UTC, whole seconds).</summary>
    public async Task<string> DebugLogAsync(RigBNode node, DateTime since, CancellationToken ct)
    {
        if (!bpqByNode.TryGetValue(node, out var c)) return "";
        var r = await c.Container.ExecAsync(["sh", "-c", "cat /data/logs/NodeDebuglog_*.log 2>/dev/null"], ct);
        var from = since.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join('\n', r.Stdout.Replace("\r", "").Split('\n')
            .Where(l => l.Length > 8 && string.CompareOrdinal(l[..8], from) >= 0)
            .Where(l => !l.Contains("without semaphore", StringComparison.Ordinal)));
    }
    private readonly Action<string> note;

    public RigBNetwork(RigBProfile profile, Action<string> note)
    {
        Profile = profile;
        this.note = note;
    }

    /// <summary>Where a node's AGW port is on the host.</summary>
    public int AgwPort(RigBNode node) => netSim!.Container.GetMappedPublicPort(node.InsideAgwPort);

    public int TelnetPort(RigBNode node) => netSim!.Container.GetMappedPublicPort(node.InsideTelnetPort);

    /// <summary>Start net-sim and the five nodes, and wait until the radio link works and (with NET/ROM) the tables are full.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var builder = new ContainerBuilder()
            .WithImage(NetSimTwoNodeFixture.NetSimImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(NetworkYaml()), "/etc/sim/network.yaml")
            .WithPortBinding(InsideWebPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(p => p.HostConfig.CapAdd = ["SYS_NICE"])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(InsideWebPort)
                .ForPath("/api/status")
                .ForResponseMessageMatching(async m => (await m.Content.ReadAsStringAsync()).Contains("\"running\":true")),
                o => o.WithTimeout(TimeSpan.FromMinutes(2))));
        foreach (var n in Nodes)
        {
            builder = builder.WithPortBinding(n.InsideAgwPort, assignRandomHostPort: true)
                .WithPortBinding(n.InsideTelnetPort, assignRandomHostPort: true);
        }
        netSim = new WatchedContainer("net-sim", builder.Build());
        await netSim.StartAsync();
        NetSimWebPort = netSim.Container.GetMappedPublicPort(InsideWebPort);
        note($"net-sim up, web on {NetSimWebPort}");

        foreach (var n in Nodes)
        {
            var bpq = new WatchedContainer($"BPQ {n.Role}", new ContainerBuilder()
                .WithImage(BpqImage)
                .WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig(n)), "/data/bpq32.cfg")
                // LinBPQ writes its debug log (Debugprintf) to logs/ only if the directory exists.
                .WithResourceMapping(Array.Empty<byte>(), "/data/logs/.keep")
                .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim.Container.Id}")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(n.InsideAgwPort))
                .Build());
            await bpq.StartAsync();
            bpqs.Add(bpq);
            bpqByNode[n] = bpq;
        }
        note("five LinBPQs up");

        await WaitUntilHeardAsync(Mb7npw, Calne, ct);
        await WaitUntilHeardAsync(Calne, Mb7npw, ct);
        note("MB7NPW and CALNE hear each other on the radio channel");
        if (Profile.NetRomOnRf) await FillNodeTablesAsync(ct);
    }

    /// <summary>A container that stopped by itself, or null.</summary>
    public string? Died => new[] { netSim }.Concat(bpqs).OfType<WatchedContainer>().Select(c => c.Died).FirstOrDefault(d => d is not null);

    public async Task<string> LogsAsync()
    {
        var sb = new StringBuilder();
        foreach (var c in new[] { netSim }.Concat(bpqs).OfType<WatchedContainer>()) sb.AppendLine(await c.LogTailAsync(60));
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = bpqs.Count - 1; i >= 0; i--)
        {
            try { await bpqs[i].DisposeAsync(); } catch (Exception) { /* best effort */ }
        }
        if (netSim is not null) await netSim.DisposeAsync();
    }

    // ------------------------------------------------------------ config

    private string NetworkYaml() => $"""
        time_scale: 1
        nodes:
          - id: a
            ports:
              - id: radio
                tnc: direwolf
                modem: {"{"} mode: afsk1200 {"}"}
                kiss_port: {KissPortA}
          - id: b
            ports:
              - id: radio
                tnc: direwolf
                modem: {"{"} mode: afsk1200 {"}"}
                kiss_port: {KissPortB}
        links:
          - from: a.radio
            to: b.radio
            path_loss_db: {PathLossDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}
          - from: b.radio
            to: a.radio
            path_loss_db: {PathLossDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}

        """;

    private string BpqConfig(RigBNode node)
    {
        var netRom = Profile.NetRomOnRf;
        var quality = netRom ? 200 : 0;
        var sb = new StringBuilder();
        sb.Append($"""
            SIMPLE=1
            NODECALL={node.Call}
            NODEALIAS={node.Alias}
            LOCATOR=NONE
            IDINTERVAL=0{(OnlyV20 ? "\nOnlyVer2point0=1" : "")}
            BTINTERVAL=0
            NODESINTERVAL={NodesIntervalMinutes}
            MINQUAL=1
            AGWPORT={node.InsideAgwPort}
            AGWSESSIONS=20
            AGWMASK=255

            """);
        if (node.DappsCall is { } dapps)
        {
            // The DAPPS call as DAPPS's docs set it up (docs/connect/bpq.md):
            // quality 0, off NET/ROM; a peer's is advertised in the
            // NetRomApps profile.
            var appQuality = Profile.PeerAppsAdvertised && node != Mb7npw ? 200 : 0;
            sb.Append($"APPLICATION 1,DAPPS,,{dapps},{node.Alias}DAP,{appQuality}\n");
        }
        sb.Append($"""

            PORT
             ID=Telnet
             DRIVER=Telnet
             CONFIG
             TCPPORT={node.InsideTelnetPort}
             HTTPPORT={node.InsideHttpPort}
             MAXSESSIONS=10
             USER=test,test,{node.Call},,SYSOP
            ENDPORT

            """);
        if (node == Mb7npw || node == Calne)
        {
            sb.Append($"""
                PORT
                 ID=Radio
                 TYPE=ASYNC
                 PROTOCOL=KISS
                 IPADDR=127.0.0.1
                 TCPPORT={(node == Mb7npw ? KissPortA : KissPortB)}
                 CHANNEL=A
                 SPEED={Radio.Speed}
                 TXDELAY={Radio.TxDelayMs}
                 PERSIST={Radio.Persist}
                 SLOTTIME={Radio.SlotTimeMs}
                 FRACK={Radio.FrackMs}
                 RESPTIME={Radio.RespTimeMs}
                 RETRIES={Radio.Retries}
                 MAXFRAME={Radio.Maxframe}
                 PACLEN={Radio.Paclen}
                 INTERLOCK=0
                 MHEARD=Y
                 QUALITY={quality}
                 MINQUAL=1
                ENDPORT

                """);
        }
        if (node != Mb7npw)
        {
            // AXIP: CALNE to each peer. The far end's node and DAPPS calls
            // are mapped; AUTOADDQUIET learns the rest (the SSID-flipped
            // calls of a downlink).
            var maps = new StringBuilder();
            foreach (var far in node == Calne ? Peers : [Calne])
            {
                maps.Append($" MAP {far.Call} 127.0.0.1 UDP {far.AxipUdpPort}{(netRom ? " B" : "")}\n");
                if (far.DappsCall is { } call) maps.Append($" MAP {call} 127.0.0.1 UDP {far.AxipUdpPort}\n");
            }
            sb.Append($"""
                PORT
                 ID=AXIP
                 DRIVER=BPQAXIP
                 QUALITY={quality}
                 MINQUAL=1
                 FRACK=3000
                 RESPTIME=200
                 MAXFRAME=7
                 PACLEN=236
                 CONFIG
                 UDP {node.AxipUdpPort}
                 AUTOADDQUIET
                {(netRom ? " BROADCAST NODES\n" : "")}{maps}ENDPORT

                """);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ readiness

    /// <summary>UI frames from one node until the other's monitor hears one (as <see cref="NetSimTwoBpqFixture"/>).</summary>
    private async Task WaitUntilHeardAsync(RigBNode from, RigBNode to, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var call = from.Call + "-15";
        while (true)
        {
            try
            {
                using var sender = new TcpClient();
                using var listener = new TcpClient();
                await sender.ConnectAsync(Host, AgwPort(from), cts.Token);
                await listener.ConnectAsync(Host, AgwPort(to), cts.Token);
                var send = new AgwFrameTransport(sender.GetStream());
                var hear = new AgwFrameTransport(listener.GetStream());
                await send.WriteFrameAsync(new AgwFrame(0, 'X', 0, call, "", []), cts.Token);
                await hear.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), cts.Token);
                var heard = Task.Run(async () =>
                {
                    while (true)
                    {
                        var f = await hear.ReadFrameAsync(cts.Token);
                        if (Encoding.Latin1.GetString(f.Payload).Contains($"Fm {call} To READY", StringComparison.Ordinal)) return;
                    }
                }, cts.Token);
                while (!heard.IsCompleted)
                {
                    await send.WriteFrameAsync(new AgwFrame(RfAgwPort, 'M', 0xF0, call, "READY", "ready"u8.ToArray()), cts.Token);
                    await Task.WhenAny(heard, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));
                }
                await heard;
                return;
            }
            catch (Exception e) when (e is IOException or SocketException && !cts.IsCancellationRequested)
            {
                await Task.Delay(2000, cts.Token);
            }
            catch (OperationCanceledException) when (!outer.IsCancellationRequested)
            {
                throw new TimeoutException($"Rig B: {from} sent UI frames for 2 minutes and {to} never heard one.\n{await LogsAsync()}");
            }
        }
    }

    /// <summary>
    /// Fill the NET/ROM tables without waiting an hour: once CALNE has heard
    /// every peer over AXIP (their first NODES go at start-up), CALNE sends
    /// NODES (sysop SENDNODES), so MB7NPW learns CALNE and what is behind it
    /// in one broadcast. Then the hourly broadcasts are the only NET/ROM
    /// traffic on air.
    /// </summary>
    private async Task FillNodeTablesAsync(CancellationToken ct)
    {
        var wanted = Peers.Select(p => Profile.PeerAppsAdvertised ? p.Alias + "DAP" : p.Alias).ToList();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        string table = "";
        while (DateTime.UtcNow < deadline)
        {
            table = await NodeCommandAsync(Calne, "N", ct);
            if (wanted.All(w => table.Contains(w + ":", StringComparison.Ordinal))) break;
            await Task.Delay(5000, ct);
        }
        note($"CALNE's nodes: {RigLog.Clean(table)}");
        deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            var sent = await SysopCommandAsync(Calne, "SENDNODES", ct);
            note($"CALNE SENDNODES: {RigLog.Clean(sent)}");
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            table = await NodeCommandAsync(Mb7npw, "N", ct);
            if (table.Contains("CALNE:", StringComparison.Ordinal) && wanted.All(w => table.Contains(w + ":", StringComparison.Ordinal)))
            {
                note($"MB7NPW's nodes: {RigLog.Clean(table)}");
                // MB7NPW's own first NODES may still be due; let it go now, not mid-run.
                await SysopCommandAsync(Mb7npw, "SENDNODES", ct);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return;
            }
        }
        throw new TimeoutException($"Rig B: MB7NPW never learnt CALNE and the peers over NET/ROM. Its table: {RigLog.Clean(table)}\n{await LogsAsync()}");
    }

    /// <summary>One command at a node's prompt over an AGW session to SWITCH; what came back in a few seconds.</summary>
    public async Task<string> NodeCommandAsync(RigBNode node, string command, CancellationToken ct)
    {
        await using var agw = await AgwRig.OpenAsync($"{node.Role} probe", Host, AgwPort(node), _ => { }, ct);
        const string me = "N0TST-9";
        await agw.RegisterAsync(me, ct);
        await agw.ConnectAsync(0, me, "SWITCH", ct);
        await Task.Delay(500, ct);
        await agw.SendAsync(0, me, "SWITCH", command + "\r", ct);
        await Task.Delay(2500, ct);
        var text = agw.DataText(0, me, "SWITCH");
        await agw.DisconnectAsync(0, me, "SWITCH", ct);
        return text;
    }

    /// <summary>A sysop command over the node's telnet port, logged in as the SYSOP user.</summary>
    public async Task<string> SysopCommandAsync(RigBNode node, string command, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Host, TelnetPort(node), ct);
        var stream = tcp.GetStream();
        var received = new StringBuilder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.Run(async () =>
        {
            var buf = new byte[2048];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buf, stop.Token);
                    if (n == 0) return;
                    lock (received)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            if (buf[i] == 255 && i + 2 < n) { i += 2; continue; }
                            received.Append((char)buf[i]);
                        }
                    }
                }
            }
            catch (Exception) { /* closed */ }
        }, CancellationToken.None);
        string Seen() { lock (received) return received.ToString(); }
        async Task Send(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s + "\r\n"), ct);
        await RigLog.WaitAsync(() => Seen().Contains("user", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5), ct);
        await Send("test");
        await RigLog.WaitAsync(() => Seen().Contains("password", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5), ct);
        await Send("test");
        await Task.Delay(1500, ct);
        // A SYSOP user's session is secure, but LinBPQ still wants
        // PASSWORD typed (it answers Ok without asking for one).
        await Send("PASSWORD");
        await Task.Delay(1000, ct);
        var before = Seen().Length;
        await Send(command);
        await Task.Delay(2000, ct);
        var reply = Seen()[before..];
        await stop.CancelAsync();
        tcp.Close();
        try { await pump; } catch (Exception) { /* closed */ }
        return reply;
    }

    /// <summary>Frames each radio port has put on air, by net-sim's count (Dire Wolf's own transmit log).</summary>
    public async Task<(int A, int B)> FramesSentAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Host}:{NetSimWebPort}/") };
        var doc = await http.GetFromJsonAsync<JsonElement>("api/observed", ct);
        int Count(string prefix)
        {
            var n = 0;
            if (!doc.TryGetProperty("ports", out var ports)) return 0;
            foreach (var p in ports.EnumerateObject())
            {
                if (!p.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!p.Value.TryGetProperty("calls", out var calls) || calls.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in calls.EnumerateArray()) n += c.GetProperty("frames").GetInt32();
            }
            return n;
        }
        return (Count("a."), Count("b."));
    }
}
