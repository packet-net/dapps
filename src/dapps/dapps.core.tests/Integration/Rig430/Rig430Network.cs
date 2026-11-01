using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using dapps.core.tests.Integration.Rig425;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>The node software one replica node runs.</summary>
public enum Rig430Kind { Bpq, Xrouter }

/// <summary>
/// One node of the MB7NPW replica. <see cref="Real"/> is the station it
/// stands in for; the calls are test calls (G9...) with the real aliases.
/// <see cref="DappsCall"/> is null where the real node runs no DAPPS.
/// </summary>
public sealed record Rig430Node(string Role, string Real, Rig430Kind Kind, int Index, string Call, string Alias, string? DappsCall)
{
    /// <summary>Inside net-sim's network namespace, which every container shares.</summary>
    public int AgwPort => 18600 + Index;
    public int RhpPort => 18620 + Index;
    public int TelnetPort => 18640 + Index;
    public int HttpPort => 18660 + Index;

    /// <summary>A LinBPQ node's one AXIP port listens here for every neighbour.</summary>
    public int BpqAxipPort => 19500 + Index;

    public override string ToString() => $"{Role} ({Real}, {Kind} {Call})";
}

/// <summary>An AX.25-over-UDP link between two nodes. <see cref="Quality"/> 0: no NET/ROM over it.</summary>
public sealed record Rig430UdpLink(Rig430Node A, Rig430Node B, int QualityA, int QualityB)
{
    public Rig430Node Other(Rig430Node n) => n == A ? B : A;
    public int QualityAt(Rig430Node n) => n == A ? QualityA : QualityB;
}

/// <summary>One simulated radio channel: its net-sim port id and the nodes on it.</summary>
public sealed record Rig430Channel(string Id, string Name, IReadOnlyList<Rig430Node> Nodes, double PathLossDb);

/// <summary>
/// The lab replica of MB7NPW's network for packet-net/dapps#430, from the
/// 2026-10-31 surveys (#425, #433). Real node software on every node, real
/// DAPPS daemons on MB7NPW and the three peers, the radio links on net-sim
/// v0.4.0's physical FM channel with pdn-soundmodem's fsk4800-il2p (the
/// NinoTNC's 4800 baud GFSK with IL2P and CRC, as the real 4 m and 70 cm
/// links run):
///
///     DAPPS-AGW-MB7NPW(LinBPQ) =NPR= CALNE(XRouter) -AXUDP- G8PZT(XRouter) -AXUDP- EI7KX(LinBPQ)-AGW-DAPPS
///                  |                    |
///             4 m 4k8 RF           70 cm 4k8 RF (shared, user traffic)
///                  |                    |
///                  +------ GB7BDH (XRouter) -AXUDP- GB7AGM(XRouter)-RHPv2-DAPPS
///                                 |
///                               AXUDP
///                                 |
///                          CSNODE (XRouter) -AXUDP- GB7LOX(LinBPQ)-AGW-DAPPS
///
/// NPR is AXUDP through a relay that holds every datagram 250 ms each way
/// (0.5 s round trip, as surveyed) with no loss or rate limit. The 4 m
/// channel carries MB7NPW and GB7BDH; the 70 cm channel CALNE, GB7BDH and a
/// user station sending UI frames now and then. A receive-only monitor on
/// each channel hears every frame for the measurements.
///
/// Assumptions where the surveys stop: EI7KX sits behind G8PZT over AXUDP
/// and isn't on NET/ROM; CSNODE's links are AXUDP; G8PZT has no link to
/// GB7BDH. Port numbers follow the real nodes where known (CALNE 1 NPR,
/// 2 70 cm, 5 AXUDP to G8PZT).
/// </summary>
public sealed class Rig430Network : IAsyncDisposable
{
    private const int InsideWebPort = 8080;

    public string Host => "127.0.0.1";

    // ------------------------------------------------------------ topology

    public Rig430Node Npw { get; } = new("NPW", "MB7NPW", Rig430Kind.Bpq, 0, "G9NPW", "PEWSEY", "G9NPW-3");
    public Rig430Node Cal { get; } = new("CAL", "CALNE", Rig430Kind.Xrouter, 1, "G9CAL", "CALNE", null);
    public Rig430Node Bdh { get; } = new("BDH", "GB7BDH", Rig430Kind.Xrouter, 2, "G9BDH", "SWINDN", null);
    public Rig430Node Ndh { get; } = new("NDH", "GB7NDH-2", Rig430Kind.Xrouter, 3, "G9NDH-2", "CSNODE", null);
    public Rig430Node Agm { get; } = new("AGM", "GB7AGM", Rig430Kind.Xrouter, 4, "G9AGM", "AGMNOD", "G9AGM-3");
    public Rig430Node Lox { get; } = new("LOX", "GB7LOX", Rig430Kind.Bpq, 5, "G9LOX", "LOXLEY", "G9LOX-6");
    public Rig430Node Pzt { get; } = new("PZT", "G8PZT", Rig430Kind.Xrouter, 6, "G9PZT", "KIDDER", null);
    public Rig430Node Eik { get; } = new("EIK", "EI7KX", Rig430Kind.Bpq, 7, "G9EIK", "EIKX", "G9EIK-3");

    public IReadOnlyList<Rig430Node> Nodes => [Npw, Cal, Bdh, Ndh, Agm, Lox, Pzt, Eik];

    /// <summary>The three DAPPS peers MB7NPW sends to, all behind CALNE.</summary>
    public IReadOnlyList<Rig430Node> Peers => [Agm, Lox, Eik];

    /// <summary>The AXUDP links (not NPR). CALNE to G8PZT is quality 80, as surveyed; EI7KX is off NET/ROM.</summary>
    public IReadOnlyList<Rig430UdpLink> UdpLinks =>
    [
        new(Bdh, Agm, 200, 200),
        new(Bdh, Ndh, 200, 200),
        new(Ndh, Lox, 200, 200),
        new(Cal, Pzt, 80, 200),
        new(Pzt, Eik, 0, 0),
    ];

    /// <summary>NPR one-way delay, ms.</summary>
    public static int NprDelayMs => int.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_430_NPR_DELAY_MS"), out var d) && d >= 0 ? d : 250;

    public static double PathLossDb =>
        double.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_430_PATH_LOSS_DB"), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 130;

    public Rig430Channel FourMetre => new("m4", "4 m", [Npw, Bdh], PathLossDb);
    public Rig430Channel SeventyCm => new("c70", "70 cm", [Cal, Bdh], PathLossDb);
    public IReadOnlyList<Rig430Channel> Channels => [FourMetre, SeventyCm];

    /// <summary>The user station on 70 cm (net-sim node "usr"), and the receive-only monitors ("mon").</summary>
    public const string UserCall = "G9USR-1";

    /// <summary>net-sim's modem on every radio port.</summary>
    public const string Modem = "fsk4800-il2p";

    /// <summary>A port's KISS TCP port on net-sim (inside the namespace).</summary>
    public static int KissPort(string simNode, string channel) => (simNode, channel) switch
    {
        ("npw", "m4") => 18401,
        ("bdh", "m4") => 18402,
        ("mon", "m4") => 18403,
        ("cal", "c70") => 18411,
        ("bdh", "c70") => 18412,
        ("usr", "c70") => 18413,
        ("mon", "c70") => 18414,
        _ => throw new ArgumentException($"no radio port {simNode}.{channel}"),
    };

    public static string SimNode(Rig430Node n) => n.Role.ToLowerInvariant();

    /// <summary>The radio port settings of every node, LinBPQ and XRouter alike.</summary>
    public sealed record RadioSettings(int TxDelayMs = 150, int Persist = 64, int SlotTimeMs = 100, int FrackMs = 7000,
        int RespTimeMs = 1500, int Retries = 10, int Maxframe = 4, int Paclen = 236);

    public static readonly RadioSettings Radio = new();

    /// <summary>NODES broadcast interval on radio ports (minutes); the rig sends the first rounds itself.</summary>
    public const int RfNodesMinutes = 30;

    /// <summary>On AXUDP and NPR (no airtime).</summary>
    public const int UdpNodesMinutes = 10;

    // Port numbers as each node's own prompt and DAPPS count them.
    // MB7NPW (LinBPQ): 1 Telnet, 2 NPR, 3 4 m. AGW port bytes are one less.
    public const int NpwNprPort = 2;
    public const int NpwRfPort = 3;

    // CALNE (XRouter): 1 NPR, 2 70 cm, 5 AXUDP to G8PZT, as the real node.
    public const int CalNprPort = 1;
    public const int CalRfPort = 2;
    public const int CalPztPort = 5;

    // GB7BDH: 1 4 m, 2 70 cm, 3 AXUDP to GB7AGM, 4 AXUDP to CSNODE.
    public const int BdhAgmPort = 3;

    // G8PZT: 1 AXUDP to CALNE, 2 AXUDP to EI7KX.
    public const int PztEikPort = 2;

    /// <summary>A peer's port byte for DAPPS: LinBPQ 2 (AXIP) less one; XRouter PORT=1 less one.</summary>
    public static int PeerBearerPort(Rig430Node peer) => peer.Kind == Rig430Kind.Bpq ? 1 : 0;

    /// <summary>The XRouter ports of a node in order: (number, what, interface).</summary>
    public IReadOnlyList<XrPort> XrPorts(Rig430Node n)
    {
        var list = new List<XrPort>();
        if (n == Cal)
        {
            list.Add(new XrPort(CalNprPort, "NPR to PEWSEY", XrPortKind.Npr, null, 180));
            list.Add(new XrPort(CalRfPort, "70cm 4k8 to SWINDN", XrPortKind.Rf, SeventyCm, 180));
            list.Add(new XrPort(CalPztPort, "AXUDP to KIDDER", XrPortKind.Udp, null, 80, UdpLinks.Single(l => l.A == Cal && l.B == Pzt)));
            return list;
        }
        if (n == Bdh)
        {
            list.Add(new XrPort(1, "4m 4k8 to PEWSEY", XrPortKind.Rf, FourMetre, 180));
            list.Add(new XrPort(2, "70cm 4k8 to CALNE", XrPortKind.Rf, SeventyCm, 180));
        }
        var number = list.Count;
        foreach (var l in UdpLinks.Where(l => l.A == n || l.B == n))
        {
            number++;
            list.Add(new XrPort(number, $"AXUDP to {l.Other(n).Alias}", XrPortKind.Udp, null, l.QualityAt(n), l));
        }
        return list;
    }

    public enum XrPortKind { Npr, Rf, Udp }

    public sealed record XrPort(int Number, string Id, XrPortKind Kind, Rig430Channel? Channel, int Quality, Rig430UdpLink? Link = null);

    /// <summary>The UDP port <paramref name="node"/> listens on for AXUDP from <paramref name="from"/>.</summary>
    public static int UdpListen(Rig430Node node, Rig430Node from) =>
        node.Kind == Rig430Kind.Bpq ? node.BpqAxipPort : 19600 + 10 * node.Index + from.Index;

    // NPR relay: MB7NPW sends to 19900, CALNE to 19901.
    public const int RelayNpwSide = 19900;
    public const int RelayCalSide = 19901;

    // ------------------------------------------------------------ images

    public const string XrouterBase = XrouterIntegrationFixture.PinnedImage;

    /// <summary>XRouter plus socat, so its ASYNC KISS interfaces reach net-sim's KISS TCP ports.</summary>
    public const string XrouterKissImage = "rig430-xrouter-kiss:505p";

    /// <summary>LinBPQ for MB7NPW, GB7LOX and EI7KX: the DAPPS test pin, or DAPPS_RIG_430_BPQ_IMAGE (the fork, say).</summary>
    public static string BpqImage => Environment.GetEnvironmentVariable("DAPPS_RIG_430_BPQ_IMAGE") is { Length: > 0 } i ? i : LinbpqIntegrationFixture.Image;

    /// <summary>For the results file: null for the stock pin.</summary>
    public static string? BpqLabel => BpqImage == LinbpqIntegrationFixture.Image ? null : BpqImage;

    // ------------------------------------------------------------ state

    private readonly Action<string> note;
    private WatchedContainer? netSim;
    private WatchedContainer? relay;
    private WatchedContainer? kissBridge;
    private readonly List<WatchedContainer> containers = [];
    private readonly Dictionary<Rig430Node, WatchedContainer> byNode = [];

    public int NetSimWebPort { get; private set; }

    public Rig430Network(Action<string> note) => this.note = note;

    public int Mapped(int insidePort) => netSim!.Container.GetMappedPublicPort(insidePort);
    public int AgwPort(Rig430Node n) => Mapped(n.AgwPort);
    public int RhpPort(Rig430Node n) => Mapped(n.RhpPort);
    public int TelnetPort(Rig430Node n) => Mapped(n.TelnetPort);
    /// <summary>A KISS port as the host reaches it: pdn listens on loopback only, so through the bridge (+100).</summary>
    public int MappedKiss(string simNode, string channel) => Mapped(KissPort(simNode, channel) + KissBridgeOffset);

    public const int KissBridgeOffset = 100;

    /// <summary>The KISS ports the rig itself uses: the monitors and the user station.</summary>
    private static readonly (string Node, string Channel)[] HostKiss = [("mon", "m4"), ("mon", "c70"), ("usr", "c70")];

    /// <summary>The DAPPS attachment of a node: AGW on LinBPQ, RHPv2 on XRouter.</summary>
    public NodeAttachment Attachment(Rig430Node n, int bearerPort) =>
        n.Kind == Rig430Kind.Bpq ? NodeAttachment.Agw(Host, AgwPort(n), bearerPort) : NodeAttachment.Rhp(Host, RhpPort(n), bearerPort);

    public string? Died => new[] { netSim, relay, kissBridge }.Concat(containers).OfType<WatchedContainer>().Select(c => c.Died).FirstOrDefault(d => d is not null);

    public async Task<string> LogsAsync()
    {
        var sb = new StringBuilder();
        foreach (var c in new[] { netSim, relay, kissBridge }.Concat(containers).OfType<WatchedContainer>()) sb.AppendLine(await c.LogTailAsync(40));
        return sb.ToString();
    }

    /// <summary>The relay's log: one line per datagram, "unix-seconds A>B|B>A hex" (A is MB7NPW), whole seconds.</summary>
    public async Task<string> RelayLogAsync()
    {
        if (relay is null) return "";
        var (stdout, _) = await relay.Container.GetLogsAsync(timestampsEnabled: false);
        return stdout;
    }

    public async Task<string> ExecAsync(Rig430Node n, string command, CancellationToken ct)
    {
        var r = await byNode[n].Container.ExecAsync(["sh", "-c", command], ct);
        return r.Stdout + r.Stderr;
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = containers.Count - 1; i >= 0; i--)
        {
            try { await containers[i].DisposeAsync(); } catch (Exception) { /* best effort */ }
        }
        if (relay is not null) { try { await relay.DisposeAsync(); } catch (Exception) { } }
        if (kissBridge is not null) { try { await kissBridge.DisposeAsync(); } catch (Exception) { } }
        if (netSim is not null) await netSim.DisposeAsync();
    }

    // ------------------------------------------------------------ start

    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureXrouterImageAsync(ct);

        var builder = new ContainerBuilder()
            .WithImage(NetSimTwoNodeFixture.NetSimImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(NetworkYaml()), "/etc/sim/network.yaml")
            .WithPortBinding(InsideWebPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(p => p.HostConfig.CapAdd = ["SYS_NICE"])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(InsideWebPort)
                .ForPath("/api/status")
                .ForResponseMessageMatching(async m => (await m.Content.ReadAsStringAsync()).Contains("\"running\":true")),
                o => o.WithTimeout(TimeSpan.FromMinutes(3))));
        foreach (var n in Nodes)
        {
            builder = builder.WithPortBinding(n.TelnetPort, assignRandomHostPort: true);
            if (n.Kind == Rig430Kind.Bpq) builder = builder.WithPortBinding(n.AgwPort, assignRandomHostPort: true);
            if (n.Kind == Rig430Kind.Xrouter && n.DappsCall is not null) builder = builder.WithPortBinding(n.RhpPort, assignRandomHostPort: true);
        }
        foreach (var k in HostKiss)
        {
            builder = builder.WithPortBinding(KissPort(k.Node, k.Channel) + KissBridgeOffset, assignRandomHostPort: true);
        }
        netSim = new WatchedContainer("net-sim", builder.Build());
        await netSim.StartAsync();
        NetSimWebPort = netSim.Container.GetMappedPublicPort(InsideWebPort);
        note($"net-sim up (web on {NetSimWebPort}), {Modem} on 4 m and 70 cm, {PathLossDb} dB");

        relay = new WatchedContainer("NPR relay", new ContainerBuilder()
            .WithImage(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(RelayScript), "/relay.pl")
            .WithEntrypoint("perl", "/relay.pl", RelayNpwSide.ToString(), RelayCalSide.ToString(),
                UdpListen(Npw, Cal).ToString(), UdpListen(Cal, Npw).ToString(), (NprDelayMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim.Container.Id}")
            .Build());
        await relay.StartAsync();

        var bridges = string.Join(" & ", HostKiss.Select(k => KissPort(k.Node, k.Channel))
            .Select(p => $"socat TCP-LISTEN:{p + KissBridgeOffset},fork,reuseaddr TCP:127.0.0.1:{p}"));
        kissBridge = new WatchedContainer("KISS bridge", new ContainerBuilder()
            .WithImage(XrouterKissImage)
            .WithEntrypoint("sh", "-c", bridges + " & wait")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim.Container.Id}")
            .Build());
        await kissBridge.StartAsync();

        foreach (var n in Nodes)
        {
            var c = n.Kind == Rig430Kind.Bpq ? BuildBpq(n) : BuildXrouter(n);
            var w = new WatchedContainer($"{n.Kind} {n.Real}", c);
            await w.StartAsync();
            containers.Add(w);
            byNode[n] = w;
        }
        foreach (var n in Nodes) await WaitForTelnetAsync(n, ct);
        note($"{Nodes.Count} nodes up (LinBPQ {BpqImage}, XRouter 505p)");
        await FillNodeTablesAsync(ct);
    }

    private DotNet.Testcontainers.Containers.IContainer BuildBpq(Rig430Node n) => new ContainerBuilder()
        .WithImage(BpqImage)
        .WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig(n)), "/data/bpq32.cfg")
        .WithResourceMapping(Array.Empty<byte>(), "/data/logs/.keep")
        .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim!.Container.Id}")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(n.AgwPort))
        .Build();

    private DotNet.Testcontainers.Containers.IContainer BuildXrouter(Rig430Node n)
    {
        var rf = XrPorts(n).Where(p => p.Kind == XrPortKind.Rf).ToList();
        var links = string.Join(' ', rf.Select((p, i) => $"/dev/kiss{i + 1}=127.0.0.1:{KissPort(SimNode(n), p.Channel!.Id)}"));
        return new ContainerBuilder()
            .WithImage(XrouterKissImage)
            .WithEnvironment("KISS_LINKS", links)
            .WithResourceMapping(Encoding.UTF8.GetBytes(XrConfig(n)), "/data/XROUTER.CFG")
            // Trusted (8) so the rig's console can trigger NODES broadcasts; valid callsigns, no password (1).
            .WithResourceMapping(Encoding.UTF8.GetBytes("0.0.0.0/0 9\n"), "/data/ACCESS.SYS")
            .WithResourceMapping(Encoding.UTF8.GetBytes("G9TST test\n"), "/data/USERPASS.SYS")
            .WithResourceMapping(Encoding.UTF8.GetBytes("G9TST test\n"), "/data/PASSWORD.SYS")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim!.Container.Id}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(n.TelnetPort))
            .Build();
    }

    private static readonly SemaphoreSlim ImageLock = new(1, 1);

    /// <summary>Build <see cref="XrouterKissImage"/> once per host (XRouter's only KISS interface wants a serial device).</summary>
    private async Task EnsureXrouterImageAsync(CancellationToken ct)
    {
        await ImageLock.WaitAsync(ct);
        try
        {
            var probe = new System.Diagnostics.ProcessStartInfo("docker", ["image", "inspect", XrouterKissImage]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(probe)!)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0) return;
            }
            var dir = Path.Combine(Path.GetTempPath(), $"rig430-xr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), $"""
                FROM {XrouterBase}
                RUN apt-get update && apt-get install -y --no-install-recommends socat && rm -rf /var/lib/apt/lists/*
                COPY rig430-entry.sh /usr/local/bin/rig430-entry.sh
                RUN chmod 0755 /usr/local/bin/rig430-entry.sh
                ENTRYPOINT ["/usr/local/bin/rig430-entry.sh"]

                """, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "rig430-entry.sh"), XrouterEntry.Replace("\r", ""), ct);
            note($"building {XrouterKissImage}");
            var build = new System.Diagnostics.ProcessStartInfo("docker", ["build", "-t", XrouterKissImage, dir]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var b = System.Diagnostics.Process.Start(build)!;
            var stdout = b.StandardOutput.ReadToEndAsync(ct);
            var stderr = b.StandardError.ReadToEndAsync(ct);
            await b.WaitForExitAsync(ct);
            if (b.ExitCode != 0) throw new InvalidOperationException($"docker build {XrouterKissImage} failed:\n{await stdout}\n{await stderr}");
        }
        finally
        {
            ImageLock.Release();
        }
    }

    /// <summary>socat bridges each /dev/kissN to its net-sim KISS port before XRouter opens it.</summary>
    private const string XrouterEntry = """
        #!/bin/sh
        set -e
        for pair in $KISS_LINKS; do
            dev=${pair%%=*}
            tcp=${pair#*=}
            socat "pty,link=$dev,raw,echo=0,waitslave" "tcp:$tcp,forever,intervall=1" &
        done
        for pair in $KISS_LINKS; do
            dev=${pair%%=*}
            i=0
            while [ ! -e "$dev" ]; do
                i=$((i + 1))
                if [ "$i" -gt 200 ]; then echo "rig430: $dev never appeared" >&2; exit 1; fi
                sleep 0.1
            done
            echo "rig430: $dev is up"
        done
        exec /usr/local/bin/xrouter-entrypoint.sh "$@"

        """;

    /// <summary>
    /// The NPR stand-in: holds every datagram for the given delay each way,
    /// no loss, no rate limit, in order, and logs it (whole seconds,
    /// direction, hex) so the rig can count what crossed. Plain perl-base,
    /// as the LinBPQ image has (no Time::HiRes): each datagram gets a child
    /// that waits out the delay and sends it. Children racing each other
    /// used to deliver back-to-back datagrams out of order (about 1 in 70),
    /// which real NPR doesn't, and the nodes answered with REJs, link
    /// resets and NET/ROM resends (packet-net/dapps#425). So each child
    /// also waits for the one before it in the same direction to have
    /// sent: it holds the write end of a pipe the next child reads, and
    /// closes it once it has sent.
    /// </summary>
    private const string RelayScript = """
        use strict; use warnings;
        use IO::Socket::INET; use IO::Select; use Socket qw(pack_sockaddr_in inet_aton);
        $| = 1;
        $SIG{CHLD} = 'IGNORE';
        my ($pa, $pb, $toA, $toB, $delay) = @ARGV;
        my $sa = IO::Socket::INET->new(LocalAddr => '127.0.0.1', LocalPort => $pa, Proto => 'udp') or die "bind $pa: $!";
        my $sb = IO::Socket::INET->new(LocalAddr => '127.0.0.1', LocalPort => $pb, Proto => 'udp') or die "bind $pb: $!";
        my $destA = pack_sockaddr_in($toA, inet_aton('127.0.0.1'));
        my $destB = pack_sockaddr_in($toB, inet_aton('127.0.0.1'));
        my $sel = IO::Select->new($sa, $sb);
        my %before;
        print "relay up: $pa <-> $pb, delay $delay s, in order\n";
        while (1) {
            for my $s ($sel->can_read()) {
                my $buf;
                defined($s->recv($buf, 8192)) or next;
                my ($out, $dest, $way) = $s == $sa ? ($sb, $destB, 'A>B') : ($sa, $destA, 'B>A');
                printf "%d %s %s\n", time, $way, unpack('H*', $buf);
                pipe(my $gone, my $going) or die "pipe: $!";
                my $prev = $before{$way};
                my $pid = fork();
                if (defined $pid && $pid == 0) {
                    close $gone;
                    select(undef, undef, undef, $delay);
                    if ($prev) { my $x; sysread($prev, $x, 1); }
                    $out->send($buf, 0, $dest);
                    close $going;
                    exit 0;
                }
                close $going;
                close $prev if $prev;
                $before{$way} = $gone;
            }
        }

        """;

    // ------------------------------------------------------------ config

    private string NetworkYaml()
    {
        var radio = "radio: { squelch: hard }";
        var sb = new StringBuilder("time_scale: 1\nnodes:\n");
        var simNodes = new (string Id, (string Ch, int Kiss)[] Ports)[]
        {
            ("npw", [("m4", KissPort("npw", "m4"))]),
            ("bdh", [("m4", KissPort("bdh", "m4")), ("c70", KissPort("bdh", "c70"))]),
            ("cal", [("c70", KissPort("cal", "c70"))]),
            ("usr", [("c70", KissPort("usr", "c70"))]),
            ("mon", [("m4", KissPort("mon", "m4")), ("c70", KissPort("mon", "c70"))]),
        };
        foreach (var (id, ports) in simNodes)
        {
            sb.Append($"  - id: {id}\n    ports:\n");
            foreach (var (ch, kiss) in ports)
            {
                sb.Append($"      - id: {ch}\n        tnc: pdn\n        modem: {{ mode: {Modem} }}\n        kiss_port: {kiss}\n        {radio}\n");
            }
        }
        sb.Append("links:\n");
        var loss = PathLossDb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var userLoss = (PathLossDb + 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        void Link(string from, string to, string l) => sb.Append($"  - {{ from: {from}, to: {to}, path_loss_db: {l} }}\n");
        // 4 m: MB7NPW and GB7BDH, and the monitor hearing both.
        Link("npw.m4", "bdh.m4", loss); Link("bdh.m4", "npw.m4", loss);
        Link("npw.m4", "mon.m4", loss); Link("bdh.m4", "mon.m4", loss);
        // 70 cm: CALNE, GB7BDH and the user station all hear each other; the monitor hears all three.
        Link("cal.c70", "bdh.c70", loss); Link("bdh.c70", "cal.c70", loss);
        foreach (var n in new[] { "cal", "bdh" }) { Link("usr.c70", $"{n}.c70", userLoss); Link($"{n}.c70", "usr.c70", userLoss); }
        foreach (var n in new[] { "cal", "bdh", "usr" }) Link($"{n}.c70", "mon.c70", loss);
        return sb.ToString();
    }

    private string BpqConfig(Rig430Node n)
    {
        var sb = new StringBuilder($"""
            SIMPLE=1
            NODECALL={n.Call}
            NODEALIAS={n.Alias}
            LOCATOR=NONE
            IDINTERVAL=0
            BTINTERVAL=0
            NODESINTERVAL={(n == Npw ? RfNodesMinutes : UdpNodesMinutes)}
            MINQUAL=1
            AGWPORT={n.AgwPort}
            AGWSESSIONS=20
            AGWMASK=255
            APPLICATION 1,DAPPS,,{n.DappsCall},{n.Alias[..3]}DAP,0

            PORT
             ID=Telnet
             DRIVER=Telnet
             CONFIG
             TCPPORT={n.TelnetPort}
             HTTPPORT={n.HttpPort}
             MAXSESSIONS=10
             USER=test,test,{n.Call},,SYSOP
            ENDPORT

            """);
        var maps = new StringBuilder();
        int quality;
        if (n == Npw)
        {
            maps.Append($" MAP {Cal.Call} 127.0.0.1 UDP {RelayNpwSide} B\n");
            quality = 200;
        }
        else
        {
            var link = UdpLinks.Single(l => l.A == n || l.B == n);
            var far = link.Other(n);
            quality = link.QualityAt(n);
            maps.Append($" MAP {far.Call} 127.0.0.1 UDP {UdpListen(far, n)}{(quality > 0 ? " B" : "")}\n");
        }
        var id = n == Npw ? "NPR to CALNE" : "AXIP";
        sb.Append($"""
            PORT
             ID={id}
             DRIVER=BPQAXIP
             QUALITY={quality}
             MINQUAL=1
             FRACK=3000
             RESPTIME=200
             MAXFRAME=7
             PACLEN=236
             CONFIG
             UDP {n.BpqAxipPort}
             AUTOADDQUIET
            {(quality > 0 ? " BROADCAST NODES\n" : "")}{maps}ENDPORT

            """);
        if (n == Npw)
        {
            var r = Radio;
            sb.Append($"""
                PORT
                 ID=4m 4k8 to SWINDN
                 TYPE=ASYNC
                 PROTOCOL=KISS
                 IPADDR=127.0.0.1
                 TCPPORT={KissPort("npw", "m4")}
                 CHANNEL=A
                 SPEED=4800
                 TXDELAY={r.TxDelayMs}
                 PERSIST={r.Persist}
                 SLOTTIME={r.SlotTimeMs}
                 FRACK={r.FrackMs}
                 RESPTIME={r.RespTimeMs}
                 RETRIES={r.Retries}
                 MAXFRAME={r.Maxframe}
                 PACLEN={r.Paclen}
                 INTERLOCK=0
                 MHEARD=Y
                 QUALITY=180
                 MINQUAL=1
                ENDPORT

                """);
        }
        return sb.ToString();
    }

    private string XrConfig(Rig430Node n)
    {
        var baseCall = n.Call.Split('-')[0];
        var sb = new StringBuilder()
            .Append("DNS=8.8.8.8\n")
            .Append($"NODECALL={n.Call}\n")
            .Append($"NODEALIAS={n.Alias}\n")
            .Append("LOCATOR=IO91PM\n")
            .Append($"CONSOLECALL={baseCall}-15\n")
            .Append($"CHATCALL={baseCall}-8\n")
            .Append($"CHATALIAS={n.Alias[..Math.Min(3, n.Alias.Length)]}CHT\n")
            .Append($"IPADDRESS=44.0.0.{10 + n.Index}\n")
            .Append($"TELNETPORT={n.TelnetPort}\n")
            .Append("HTTPPORT=0\nFTPPORT=0\n")
            .Append($"NODESINTERVAL={UdpNodesMinutes}\n");
        if (n.DappsCall is not null) sb.Append($"RHPPORT={n.RhpPort}\n");
        var ports = XrPorts(n);
        sb.Append("INTERFACE=1\n\tTYPE=AXUDP\n\tMTU=256\nENDINTERFACE\n");
        var rfIndex = 0;
        var ifaceOf = new Dictionary<XrPort, int>();
        foreach (var p in ports.Where(p => p.Kind == XrPortKind.Rf))
        {
            rfIndex++;
            sb.Append($"INTERFACE={rfIndex + 1}\n\tTYPE=ASYNC\n\tCOM=/dev/kiss{rfIndex}\n\tSPEED=9600\n\tPROTOCOL=KISS\n\tMTU=256\nENDINTERFACE\n");
            ifaceOf[p] = rfIndex + 1;
        }
        foreach (var p in ports)
        {
            sb.Append($"PORT={p.Number}\n\tID=\"{p.Id}\"\n\tQUALITY={p.Quality}\n\tMINQUAL=1\n");
            switch (p.Kind)
            {
                case XrPortKind.Npr:
                    sb.Append("\tINTERFACENUM=1\n\tIPLINK=127.0.0.1\n")
                      .Append($"\tUDPLOCAL={UdpListen(Cal, Npw)}\n\tUDPREMOTE={RelayCalSide}\n")
                      .Append($"\tNODESINTERVAL={UdpNodesMinutes}\n\tFRACK=3000\n\tRESPTIME=200\n\tMAXFRAME=7\n");
                    break;
                case XrPortKind.Udp:
                    var far = p.Link!.Other(n);
                    sb.Append("\tINTERFACENUM=1\n\tIPLINK=127.0.0.1\n")
                      .Append($"\tUDPLOCAL={UdpListen(n, far)}\n\tUDPREMOTE={UdpListen(far, n)}\n")
                      .Append($"\tNODESINTERVAL={(p.Quality > 0 ? UdpNodesMinutes : 0)}\n\tFRACK=3000\n\tRESPTIME=200\n\tMAXFRAME=7\n");
                    break;
                case XrPortKind.Rf:
                    var r = Radio;
                    sb.Append($"\tINTERFACENUM={ifaceOf[p]}\n")
                      .Append($"\tTXDELAY={r.TxDelayMs}\n\tPERSIST={r.Persist}\n\tSLOTTIME={r.SlotTimeMs}\n")
                      .Append($"\tFRACK={r.FrackMs}\n\tRESPTIME={r.RespTimeMs}\n\tRETRIES={r.Retries}\n")
                      .Append($"\tMAXFRAME={r.Maxframe}\n\tPACLEN={r.Paclen}\n\tNODESINTERVAL={RfNodesMinutes}\n");
                    break;
            }
            sb.Append("ENDPORT\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ consoles and NET/ROM

    private async Task WaitForTelnetAsync(Rig430Node n, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(Host, TelnetPort(n), ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>One command at a node's console (telnet, logged in as the sysop); what came back.</summary>
    public async Task<string> CommandAsync(Rig430Node n, string command, CancellationToken ct, int waitMs = 2500)
    {
        await using var c = await Rig430Console.OpenAsync(n.Kind, Host, TelnetPort(n), ct);
        return await c.CommandAsync(command, waitMs, ct);
    }

    /// <summary>A node's NODES table as text.</summary>
    public Task<string> NodesAsync(Rig430Node n, CancellationToken ct) => CommandAsync(n, "N", ct);

    /// <summary>Send NODES now on every port that carries NET/ROM.</summary>
    private async Task BroadcastAsync(Rig430Node n, CancellationToken ct)
    {
        if (n.Kind == Rig430Kind.Bpq)
        {
            await CommandAsync(n, "SENDNODES", ct, 1500);
            return;
        }
        await using var c = await Rig430Console.OpenAsync(n.Kind, Host, TelnetPort(n), ct);
        foreach (var p in XrPorts(n).Where(p => p.Quality > 0)) await c.CommandAsync($"N BC {p.Number}", 800, ct);
    }

    /// <summary>What each node must know before a run: the routes the connect scripts use.</summary>
    private IEnumerable<(Rig430Node Node, Rig430Node Dest)> NeededRoutes =>
    [
        (Npw, Cal),
        (Cal, Bdh), (Cal, Lox), (Cal, Pzt),
        (Bdh, Cal),
        (Lox, Cal),
    ];

    /// <summary>Whether a NODES listing holds a node's call (XRouter shows nodes learnt by INP3 alone without their alias).</summary>
    public static bool Lists(string table, Rig430Node dest) =>
        System.Text.RegularExpressions.Regex.IsMatch(table, $@"(^|[\s:]){System.Text.RegularExpressions.Regex.Escape(dest.Call)}(\s|$)", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Fill the NET/ROM tables without waiting for the hourly broadcasts:
    /// rounds of broadcasts from every node, farthest first, until each
    /// node knows the routes the scripts use. Their airtime falls before
    /// any run starts.
    /// </summary>
    private async Task FillNodeTablesAsync(CancellationToken ct)
    {
        // Routes flow one hop per broadcast, so each round goes out from
        // MB7NPW to the far nodes and then back again.
        var outward = new[] { Npw, Cal, Pzt, Bdh, Agm, Ndh, Lox };
        var order = outward.Concat(Enumerable.Reverse(outward)).ToArray();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(12);
        var round = 0;
        List<string> missing = [];
        while (DateTime.UtcNow < deadline)
        {
            round++;
            foreach (var n in order)
            {
                try { await BroadcastAsync(n, ct); }
                catch (Exception e) { note($"NODES broadcast from {n.Real} failed: {e.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            missing = [];
            foreach (var (node, dest) in NeededRoutes)
            {
                var table = await NodesAsync(node, ct);
                if (!Lists(table, dest)) missing.Add($"{node.Real} lacks {dest.Real} ({RigLog.Clean(table)})");
            }
            note($"NET/ROM round {round}: {(missing.Count == 0 ? "all routes known" : string.Join(", ", missing))}");
            if (missing.Count == 0)
            {
                foreach (var n in new[] { Npw, Cal, Bdh, Lox }) note($"{n.Real} nodes: {RigLog.Clean(await NodesAsync(n, ct))}");
                note($"CALNE routes to GB7LOX: {RigLog.Clean(await CommandAsync(Cal, $"N {Lox.Call}", ct))}");
                return;
            }
        }
        throw new TimeoutException($"Rig 430: NET/ROM never converged: {string.Join(", ", missing)}\n{await LogsAsync()}");
    }
}

/// <summary>A node console over telnet, logged in as the sysop (LinBPQ: test/test then PASSWORD; XRouter: G9TST/test then SYSOP).</summary>
public sealed class Rig430Console : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly StringBuilder received = new();
    private readonly CancellationTokenSource stop = new();
    private Task pump = Task.CompletedTask;

    private Rig430Console(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    public string Text { get { lock (received) return received.ToString(); } }

    public static async Task<Rig430Console> OpenAsync(Rig430Kind kind, string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        var c = new Rig430Console(tcp);
        c.pump = c.PumpAsync();
        if (kind == Rig430Kind.Bpq)
        {
            await RigLog.WaitAsync(() => c.Text.Contains("user", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(8), ct);
            await c.WriteAsync("test", ct);
            await RigLog.WaitAsync(() => c.Text.Contains("password", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(8), ct);
            await c.WriteAsync("test", ct);
            await Task.Delay(1200, ct);
            await c.WriteAsync("PASSWORD", ct);
            await Task.Delay(800, ct);
        }
        else
        {
            // "Callsign:", then the sysop password in plain text (a secure uplink, see @(1)).
            await RigLog.WaitAsync(() => c.Text.Contains("allsign", StringComparison.Ordinal), TimeSpan.FromSeconds(8), ct);
            await c.WriteAsync("G9TST", ct);
            await RigLog.WaitAsync(() => c.Text.Contains('}'), TimeSpan.FromSeconds(8), ct);
            await c.CommandAsync("@ test", 600, ct);
        }
        return c;
    }

    private async Task WriteAsync(string s, CancellationToken ct) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s + "\r\n"), ct);

    public async Task<string> CommandAsync(string command, int waitMs, CancellationToken ct)
    {
        var before = Text.Length;
        await WriteAsync(command, ct);
        await Task.Delay(waitMs, ct);
        return Text[before..];
    }

    private async Task PumpAsync()
    {
        var buf = new byte[4096];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf, stop.Token);
                if (n == 0) return;
                var sb = new StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] == 255 && i + 2 < n) { i += 2; continue; }
                    sb.Append((char)buf[i]);
                }
                lock (received) received.Append(sb);
            }
        }
        catch (Exception)
        {
            // closed
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await WriteAsync("B", CancellationToken.None); } catch (Exception) { }
        await stop.CancelAsync();
        tcp.Dispose();
        try { await pump; } catch (Exception) { }
    }
}
