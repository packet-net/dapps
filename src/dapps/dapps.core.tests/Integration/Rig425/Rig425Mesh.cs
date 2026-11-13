using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration.Rig425;

/// <summary>The packet node software a rig node runs.</summary>
public enum RigKind { Bpq, Xrouter, Pdn }

/// <summary>
/// One node of the #425 rig. <see cref="AppCall"/> is the call of the echo
/// application CALNE and the peers host, not advertised on NET/ROM, as a
/// DAPPS daemon's call is today (null on LOCAL). On a BPQ peer the same app
/// also answers <see cref="NetRomAppCall"/>, which BPQ advertises on
/// NET/ROM so a circuit can end at it.
/// </summary>
public sealed record RigNode(string Role, RigKind Kind, int Index, string Call, string Alias, string? AppCall, int Base = 18500)
{
    public string? NetRomAppCall => AppCall is not null && Role.StartsWith("PEER", StringComparison.Ordinal) && Kind == RigKind.Bpq
        ? Call + "-5"
        : null;

    public IEnumerable<string> AppCalls => new[] { AppCall, NetRomAppCall }.OfType<string>();

    public int AgwPort => Base + Index;
    public int RhpPort => Base + 10 + Index;
    public int HttpPort => Base + 20 + Index;
    public int TelnetPort => Base + 30 + Index;

    /// <summary>BPQ's one AXIP port listens here for every neighbour.</summary>
    public int BpqAxipPort => 19500 + Index;

    public override string ToString() => $"{Role} ({Kind} {Call})";
}

/// <summary>
/// Rig A for packet-net/dapps#425: four real nodes linked by AX.25 over UDP,
/// with NET/ROM between them, and no radio:
///
///     LOCAL --- CALNE --- PEER1
///                  \----- PEER2
///
/// The applications under test attach to LOCAL. Each peer hosts an echo
/// application (its own call with SSID 3) that says which callsign called
/// it and echoes lines back. Every node shares LOCAL's network namespace,
/// so links are UDP on 127.0.0.1 and LOCAL publishes every node's ports.
///
/// BPQ has one AXIP port with a MAP per neighbour (AUTOADDQUIET learns the
/// rest); XRouter and pdn have one AXUDP port per neighbour, numbered in the
/// order LOCAL, PEER1, PEER2 (CALNE) or just CALNE (the others).
///
/// Nothing starts unless DAPPS_RIG_425 is set: this is an experiment, not
/// a check normal CI runs.
/// </summary>
public abstract class Rig425Mesh : IAsyncLifetime
{
    public static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_RIG_425") is { Length: > 0 };

    public const string BpqImage = LinbpqIntegrationFixture.Image;
    public const string XrouterImage = XrouterIntegrationFixture.PinnedImage;

    /// <summary>ghcr.io/packet-net/packet.net:0.68.0, the first release whose
    /// RHPv2 open with no port routes over NET/ROM (packet.net#932). The rest of
    /// the DAPPS tests stay on their own pin.</summary>
    public const string PdnImage = "ghcr.io/packet-net/packet.net@sha256:1a85d181219e2ca65d3a8ddd453e62f8fc7b0f52c62a7e9de7315b2e8b264440";

    public string Host => "127.0.0.1";

    /// <summary>The app callsigns LOCAL's applications use.</summary>
    public const string App3 = "G9APP-3";
    public const string App4 = "G9APP-4";
    public const string App5 = "G9APP-5";

    protected abstract RigKind LocalKind { get; }
    protected abstract RigKind CalneKind { get; }
    protected abstract RigKind Peer1Kind { get; }
    protected abstract RigKind Peer2Kind { get; }

    public abstract string Name { get; }

    /// <summary>First host port this rig publishes (it uses 40 from here), so rigs can run side by side.</summary>
    protected abstract int PortBase { get; }

    public RigNode Local { get; private set; } = null!;
    public RigNode Calne { get; private set; } = null!;
    public RigNode Peer1 { get; private set; } = null!;
    public RigNode Peer2 { get; private set; } = null!;
    public IReadOnlyList<RigNode> Nodes => [Local, Calne, Peer1, Peer2];

    /// <summary>Links as (from, to), each once.</summary>
    public IReadOnlyList<(RigNode A, RigNode B)> Links => [(Local, Calne), (Calne, Peer1), (Calne, Peer2)];

    private readonly List<IContainer> containers = [];
    private readonly Dictionary<RigNode, IContainer> byNode = [];

    /// <summary>Null when LOCAL learnt CALNE and both peers over NET/ROM; otherwise what it had.</summary>
    public string? NetRomIncomplete { get; private set; }

    /// <summary>How long NET/ROM took to reach LOCAL, for the report.</summary>
    public TimeSpan SettleTime { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;
        Local = new RigNode("LOCAL", LocalKind, 0, "G9LOC", "LOC", null, PortBase);
        Calne = new RigNode("CALNE", CalneKind, 1, "G9CAL", "CAL", "G9CAL-3", PortBase);
        Peer1 = new RigNode("PEER1", Peer1Kind, 2, "G9PRA", "PRA", "G9PRA-3", PortBase);
        Peer2 = new RigNode("PEER2", Peer2Kind, 3, "G9PRB", "PRB", "G9PRB-3", PortBase);

        string? netns = null;
        foreach (var node in Nodes)
        {
            var c = Build(node, netns);
            await c.StartAsync();
            containers.Add(c);
            byNode[node] = c;
            netns ??= c.Id;
        }
        var started = DateTime.UtcNow;
        foreach (var node in Nodes) await WaitForServiceAsync(node);
        await WaitForNetRomAsync();
        SettleTime = DateTime.UtcNow - started;
    }

    public async ValueTask DisposeAsync()
    {
        // The others live in LOCAL's network namespace: LOCAL goes last.
        for (var i = containers.Count - 1; i >= 0; i--) await containers[i].DisposeAsync();
    }

    public async Task<string> LogsAsync(RigNode node)
    {
        var (stdout, stderr) = await byNode[node].GetLogsAsync();
        return stdout + stderr;
    }

    /// <summary>Run a shell command inside a node's container.</summary>
    public async Task<string> ExecAsync(RigNode node, string command)
    {
        var r = await byNode[node].ExecAsync(["sh", "-c", command]);
        return r.Stdout + r.Stderr;
    }

    public IEnumerable<RigNode> Neighbours(RigNode node) =>
        Links.Where(l => l.A == node || l.B == node).Select(l => l.A == node ? l.B : l.A);

    /// <summary>
    /// The port number a node's own console and RHPv2 use for its link to
    /// <paramref name="neighbour"/>, counted from 1. BPQ: port 2 (port 1 is
    /// Telnet). XRouter and pdn: one port per neighbour, in link order.
    /// </summary>
    public int PortTo(RigNode node, RigNode neighbour) => node.Kind switch
    {
        RigKind.Bpq => 2,
        _ => Neighbours(node).ToList().IndexOf(neighbour) + 1,
    };

    /// <summary>The AGW port byte for <see cref="PortTo"/>: one less.</summary>
    public byte AgwPortTo(RigNode node, RigNode neighbour) => (byte)(PortTo(node, neighbour) - 1);

    /// <summary>The UDP port <paramref name="node"/> listens on for frames from <paramref name="from"/>.</summary>
    public int UdpListen(RigNode node, RigNode from) =>
        node.Kind == RigKind.Bpq ? node.BpqAxipPort : 19600 + 10 * node.Index + from.Index;

    private IContainer Build(RigNode node, string? netns)
    {
        var b = new ContainerBuilder();
        switch (node.Kind)
        {
            case RigKind.Bpq:
                b = b.WithImage(BpqImage).WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig(node)), "/data/bpq32.cfg");
                break;
            case RigKind.Xrouter:
                b = b.WithImage(XrouterImage)
                    .WithResourceMapping(Encoding.UTF8.GetBytes(XrConfig(node)), "/data/XROUTER.CFG")
                    .WithResourceMapping(Encoding.UTF8.GetBytes("0.0.0.0/0 1\n"), "/data/ACCESS.SYS")
                    .WithResourceMapping(Encoding.UTF8.GetBytes("G9TST test\n"), "/data/USERPASS.SYS");
                break;
            case RigKind.Pdn:
                b = b.WithImage(PdnImage).WithResourceMapping(Encoding.UTF8.GetBytes(PdnConfig(node)), "/etc/packetnet/packetnet.yaml");
                break;
        }
        if (netns is null)
        {
            foreach (var n in Nodes)
            {
                b = b.WithPortBinding(n.AgwPort, n.AgwPort)
                    .WithPortBinding(n.RhpPort, n.RhpPort)
                    .WithPortBinding(n.HttpPort, n.HttpPort)
                    .WithPortBinding(n.TelnetPort, n.TelnetPort);
            }
        }
        else
        {
            b = b.WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netns}");
        }
        return b.Build();
    }

    private string BpqConfig(RigNode node)
    {
        var maps = new StringBuilder();
        var routes = new StringBuilder();
        foreach (var n in Neighbours(node))
        {
            var udp = UdpListen(n, node);
            maps.Append($" MAP {n.Call} 127.0.0.1 UDP {udp} B\n");
            foreach (var app in n.AppCalls) maps.Append($" MAP {app} 127.0.0.1 UDP {udp}\n");
            if (n == Local)
                foreach (var app in new[] { App3, App4, App5 }) maps.Append($" MAP {app} 127.0.0.1 UDP {udp}\n");
            routes.Append($"{n.Call},200,2\n");
        }
        // LOCAL's applications are G9APP-3 and G9APP-4, as a DAPPS daemon's
        // would be (docs/connect/bpq.md), quality 0 as DAPPS sets it. The echo
        // app's -3 call is the same; a peer's -5 call is advertised on NET/ROM.
        var appls = node == Local
            ? "APPLICATION 1,APP3,,G9APP-3,APP3,0\nAPPLICATION 2,APP4,,G9APP-4,APP4,0\n"
            : (node.AppCall is not null ? $"APPLICATION 1,{node.Alias}APP,,{node.AppCall},{node.Alias}APP,0\n" : "")
              + (node.NetRomAppCall is not null ? $"APPLICATION 2,{node.Alias}NR,,{node.NetRomAppCall},{node.Alias}NR,200\n" : "");
        return $"""
        SIMPLE=1
        NODECALL={node.Call}
        NODEALIAS={node.Alias}
        LOCATOR=NONE
        NODESINTERVAL=1
        MINQUAL=1
        AGWPORT={node.AgwPort}
        AGWSESSIONS=20
        AGWMASK=255
        {appls}
        PORT
         ID=Telnet
         DRIVER=Telnet
         CONFIG
         TCPPORT={node.TelnetPort}
         HTTPPORT={node.HttpPort}
         MAXSESSIONS=20
         USER=test,test,{node.Call},,SYSOP
        ENDPORT

        PORT
         ID=AXIP
         DRIVER=BPQAXIP
         QUALITY=200
         MINQUAL=1
         FRACK=3000
         RESPTIME=200
         MAXFRAME=7
         CONFIG
         UDP {node.BpqAxipPort}
         AUTOADDQUIET
         BROADCAST NODES
        {maps}ENDPORT

        ROUTES:
        {routes}***

        """;
    }

    private string XrConfig(RigNode node)
    {
        var sb = new StringBuilder()
            .Append("DNS=8.8.8.8\n")
            .Append($"NODECALL={node.Call}\n")
            .Append($"NODEALIAS={node.Alias}\n")
            .Append("LOCATOR=IO91PM\n")
            .Append($"CONSOLECALL={node.Call}\n")
            .Append($"CHATCALL={node.Call}-8\n")
            .Append($"CHATALIAS={node.Alias}CHT\n")
            .Append($"IPADDRESS=44.0.0.{10 + node.Index}\n")
            .Append($"AGWPORT={node.AgwPort}\n")
            .Append($"RHPPORT={node.RhpPort}\n")
            .Append($"TELNETPORT={node.TelnetPort}\n")
            .Append("HTTPPORT=0\nFTPPORT=0\n")
            .Append("NODESINTERVAL=1\n")
            .Append("INTERFACE=1\n\tTYPE=AXUDP\n\tMTU=256\nENDINTERFACE\n");
        var i = 0;
        foreach (var n in Neighbours(node))
        {
            i++;
            sb.Append($"PORT={i}\n")
              .Append($"\tID=\"AXUDP to {n.Call}\"\n")
              .Append("\tINTERFACENUM=1\n")
              .Append("\tIPLINK=127.0.0.1\n")
              .Append($"\tUDPLOCAL={UdpListen(node, n)}\n")
              .Append($"\tUDPREMOTE={UdpListen(n, node)}\n")
              .Append("\tQUALITY=200\n")
              .Append("\tMINQUAL=1\n")
              .Append("\tNODESINTERVAL=1\n")
              .Append("\tFRACK=3000\n")
              .Append("\tRESPTIME=200\n")
              .Append("ENDPORT\n");
        }
        return sb.ToString();
    }

    private string PdnConfig(RigNode node)
    {
        var ports = new StringBuilder();
        foreach (var n in Neighbours(node))
        {
            ports.Append($"""
              - id: to-{n.Alias.ToLowerInvariant()}
                transport:
                  kind: axudp
                  host: 127.0.0.1
                  port: {UdpListen(n, node)}
                  localPort: {UdpListen(node, n)}
                netRomQuality: 200
                netRomMinQuality: 1
                nodesBroadcast: true

            """);
        }
        return $"""
        schemaVersion: 2
        identity:
          callsign: {node.Call}
          alias: {node.Alias}
        ports:
        {ports}
        management:
          telnet:
            enabled: true
            bind: 0.0.0.0
            port: {node.TelnetPort}
          http:
            bind: 0.0.0.0
            port: {node.HttpPort}
          auth:
            enabled: false
        rhp:
          enabled: true
          bind: 0.0.0.0
          port: {node.RhpPort}
          requireAuth: false
        netRom:
          enabled: true
          broadcast: true
          routing: transit
          minQuality: 1
          sweepIntervalSeconds: 60

        """;
    }

    private async Task WaitForServiceAsync(RigNode node)
    {
        var port = node.Kind == RigKind.Pdn ? node.HttpPort : node.AgwPort;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(Host, port).WaitAsync(TimeSpan.FromSeconds(1));
                break;
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
        if (node.Kind == RigKind.Pdn)
            await PdnNode.WaitUntilUpAsync(Host, node.HttpPort, node.Call, TimeSpan.FromMinutes(1));
        await Task.Delay(2000);
    }

    /// <summary>
    /// Wait until LOCAL's NET/ROM table holds both peers (at most 6 minutes).
    /// BPQ is asked over an AGW node session ('N'), pdn over its web API and
    /// XRouter over its telnet console. If they never arrive the rig carries
    /// on and says so in every report (<see cref="NetRomIncomplete"/>).
    /// </summary>
    private async Task WaitForNetRomAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(6);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = await NodesTableAsync(Local);
            if (last.Contains(Peer1.Alias, StringComparison.Ordinal) && last.Contains(Peer2.Alias, StringComparison.Ordinal)
                && last.Contains(Calne.Alias, StringComparison.Ordinal))
            {
                // Give the far nodes one more cycle to know LOCAL too.
                await Task.Delay(TimeSpan.FromSeconds(70));
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        // Carry on: rows that need the missing routes fail and say so.
        NetRomIncomplete = $"LOCAL never learnt both peers over NET/ROM in 6 minutes. Its table: {last.Replace("\r", " | ")}";
    }

    /// <summary>A node's NET/ROM destinations as text, best effort.</summary>
    public async Task<string> NodesTableAsync(RigNode node)
    {
        try
        {
            switch (node.Kind)
            {
                case RigKind.Bpq:
                    return await NodeCommandAsync(node, "N");
                case RigKind.Pdn:
                    using (var http = new HttpClient { BaseAddress = new Uri($"http://{Host}:{node.HttpPort}/") })
                        return await http.GetStringAsync("api/v1/netrom/routes");
                default:
                    return await NodeCommandAsync(node, "N");
            }
        }
        catch (Exception e)
        {
            return $"(no table: {e.Message})";
        }
    }

    /// <summary>
    /// Type one command at a node's prompt and return what comes back in a
    /// few seconds. BPQ: an AGW session to SWITCH. XRouter: its telnet
    /// console (XRouter's AGW has no way to its own prompt).
    /// </summary>
    public async Task<string> NodeCommandAsync(RigNode node, string command)
    {
        var ct = CancellationToken.None;
        if (node.Kind == RigKind.Bpq)
        {
            await using var agw = await AgwRig.OpenAsync($"{node.Role} probe", Host, node.AgwPort, _ => { }, ct);
            await agw.RegisterAsync("G9TST-9", ct);
            await agw.ConnectAsync(0, "G9TST-9", "SWITCH", ct);
            await Task.Delay(500, ct);
            await agw.SendAsync(0, "G9TST-9", "SWITCH", command + "\r", ct);
            await Task.Delay(2500, ct);
            var text = agw.DataText(0, "G9TST-9", "SWITCH");
            await agw.DisconnectAsync(0, "G9TST-9", "SWITCH", ct);
            return text;
        }
        await using var tel = await RigTelnet.OpenAsync(node, Host, ct);
        return await tel.CommandAsync(command, TimeSpan.FromSeconds(3), ct);
    }
}

public sealed class Rig425AllBpq : Rig425Mesh
{
    protected override int PortBase => 18500;
    protected override RigKind LocalKind => RigKind.Bpq;
    protected override RigKind CalneKind => RigKind.Bpq;
    protected override RigKind Peer1Kind => RigKind.Bpq;
    protected override RigKind Peer2Kind => RigKind.Bpq;
    public override string Name => "all LinBPQ";
}

public sealed class Rig425AllXrouter : Rig425Mesh
{
    protected override int PortBase => 18600;
    protected override RigKind LocalKind => RigKind.Xrouter;
    protected override RigKind CalneKind => RigKind.Xrouter;
    protected override RigKind Peer1Kind => RigKind.Xrouter;
    protected override RigKind Peer2Kind => RigKind.Xrouter;
    public override string Name => "all XRouter";
}

public sealed class Rig425AllPdn : Rig425Mesh
{
    protected override int PortBase => 18700;
    protected override RigKind LocalKind => RigKind.Pdn;
    protected override RigKind CalneKind => RigKind.Pdn;
    protected override RigKind Peer1Kind => RigKind.Pdn;
    protected override RigKind Peer2Kind => RigKind.Pdn;
    public override string Name => "all pdn";
}

/// <summary>The mixed chain: LinBPQ local, XRouter CALNE, a LinBPQ and a pdn peer.</summary>
public sealed class Rig425Mixed : Rig425Mesh
{
    protected override int PortBase => 18800;
    protected override RigKind LocalKind => RigKind.Bpq;
    protected override RigKind CalneKind => RigKind.Xrouter;
    protected override RigKind Peer1Kind => RigKind.Bpq;
    protected override RigKind Peer2Kind => RigKind.Pdn;
    public override string Name => "mixed (LinBPQ local, XRouter CALNE, LinBPQ and pdn peers)";
}

[CollectionDefinition("Rig425 all BPQ", DisableParallelization = true)]
public class Rig425AllBpqCollection : ICollectionFixture<Rig425AllBpq> { }

[CollectionDefinition("Rig425 all XRouter", DisableParallelization = true)]
public class Rig425AllXrouterCollection : ICollectionFixture<Rig425AllXrouter> { }

[CollectionDefinition("Rig425 all pdn", DisableParallelization = true)]
public class Rig425AllPdnCollection : ICollectionFixture<Rig425AllPdn> { }

[CollectionDefinition("Rig425 mixed", DisableParallelization = true)]
public class Rig425MixedCollection : ICollectionFixture<Rig425Mixed> { }
