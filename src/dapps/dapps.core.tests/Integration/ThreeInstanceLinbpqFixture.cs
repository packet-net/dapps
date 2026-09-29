using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Three m0lte/linbpq containers wired A-B-C over AXIP-UDP, with no direct
/// link between A and C: reaching C from A has to go through B.
///
/// Topology:
///
///     DAPPS-A ─AGW─ BPQ-A ─AXIP─ BPQ-B ─AXIP─ BPQ-C ─AGW─ DAPPS-C
///                                   │
///                                  AGW
///                                   │
///                               DAPPS-B
///
/// BPQ-B's single AXIP port carries two MAP pairs, one to each side -
/// that's how one BPQ AXIP driver fans out to several peers, the same
/// way <see cref="TwoInstanceLinbpqFixture"/> does it for two. A has no
/// MAP entry for C's callsigns and vice versa, so an AGW connect from A
/// straight to C is refused; DAPPS has to route the message to B first.
///
/// Networking: all three containers share container A's network
/// namespace (<c>--network=container:&lt;a&gt;</c>), the same trick
/// <see cref="TwoInstanceLinbpqFixture"/> uses, so every AGW port is
/// reachable on the host via A's port mappings.
/// </summary>
public sealed class ThreeInstanceLinbpqFixture : IAsyncLifetime
{
    private const string Image = LinbpqIntegrationFixture.Image;

    private const int InsideAgwPortA = 18001;
    private const int InsideAgwPortB = 18002;
    private const int InsideAgwPortC = 18003;
    private const int InsideAxipPortA = 19001;
    private const int InsideAxipPortB = 19002;
    private const int InsideAxipPortC = 19003;

    public string Host => "127.0.0.1";
    public int AgwPortA { get; private set; }
    public int AgwPortB { get; private set; }
    public int AgwPortC { get; private set; }

    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string CallsignC => "N0CCC";
    public string ApplCallA => "N0AAA-9";
    public string ApplCallB => "N0BBB-9";
    public string ApplCallC => "N0CCC-9";

    /// <summary>AGW port byte (0-indexed) that points at the AXIP carrier
    /// port on any of the three BPQs - port 1 is Telnet, port 2 is AXIP,
    /// hence index 1. Same convention as <see cref="TwoInstanceLinbpqFixture"/>.</summary>
    public int AxipPortIndex => 1;

    private IContainer? _containerA;
    private IContainer? _containerB;
    private IContainer? _containerC;

    public async ValueTask InitializeAsync()
    {
        var configA = Encoding.UTF8.GetBytes(RenderConfig(
            nodeCall: CallsignA, nodeAlias: "AAA", applCall: ApplCallA, applAlias: "APPLA",
            agwInsidePort: InsideAgwPortA, axipInsidePort: InsideAxipPortA,
            telnetInsidePort: 18011, httpInsidePort: 18021, netromInsidePort: 18031, fbbInsidePort: 18041, apiInsidePort: 18051,
            peers: [(CallsignB, ApplCallB, InsideAxipPortB)]));

        var configB = Encoding.UTF8.GetBytes(RenderConfig(
            nodeCall: CallsignB, nodeAlias: "BBB", applCall: ApplCallB, applAlias: "APPLB",
            agwInsidePort: InsideAgwPortB, axipInsidePort: InsideAxipPortB,
            telnetInsidePort: 18012, httpInsidePort: 18022, netromInsidePort: 18032, fbbInsidePort: 18042, apiInsidePort: 18052,
            peers: [(CallsignA, ApplCallA, InsideAxipPortA), (CallsignC, ApplCallC, InsideAxipPortC)]));

        var configC = Encoding.UTF8.GetBytes(RenderConfig(
            nodeCall: CallsignC, nodeAlias: "CCC", applCall: ApplCallC, applAlias: "APPLC",
            agwInsidePort: InsideAgwPortC, axipInsidePort: InsideAxipPortC,
            telnetInsidePort: 18013, httpInsidePort: 18023, netromInsidePort: 18033, fbbInsidePort: 18043, apiInsidePort: 18053,
            peers: [(CallsignB, ApplCallB, InsideAxipPortB)]));

        // A publishes all three AGW ports. B and C share A's netns, so
        // their AGW ports are reachable on the host via A's mappings too.
        _containerA = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configA, "/data/bpq32.cfg")
            .WithPortBinding(InsideAgwPortA, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwPortB, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwPortC, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPortA))
            .Build();

        await _containerA.StartAsync();
        AgwPortA = _containerA.GetMappedPublicPort(InsideAgwPortA);
        AgwPortB = _containerA.GetMappedPublicPort(InsideAgwPortB);
        AgwPortC = _containerA.GetMappedPublicPort(InsideAgwPortC);

        var aId = _containerA.Id;
        _containerB = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configB, "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{aId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPortB))
            .Build();
        await _containerB.StartAsync();

        _containerC = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configC, "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{aId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPortC))
            .Build();
        await _containerC.StartAsync();

        // Tiny grace period: linbpq's AGW listener accepts immediately but
        // the AXIP UDP socket binds a moment later.
        await Task.Delay(2000);

        await WaitForTcp(Host, AgwPortA, TimeSpan.FromSeconds(5));
        await WaitForTcp(Host, AgwPortB, TimeSpan.FromSeconds(5));
        await WaitForTcp(Host, AgwPortC, TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        // B and C first - sharing A's netns means killing A pulls their
        // network out from under them.
        if (_containerC is not null) await _containerC.DisposeAsync();
        if (_containerB is not null) await _containerB.DisposeAsync();
        if (_containerA is not null) await _containerA.DisposeAsync();
    }

    private static string RenderConfig(
        string nodeCall, string nodeAlias, string applCall, string applAlias,
        int agwInsidePort, int axipInsidePort,
        int telnetInsidePort, int httpInsidePort, int netromInsidePort, int fbbInsidePort, int apiInsidePort,
        IReadOnlyList<(string Call, string ApplCall, int AxipInsidePort)> peers)
    {
        var maps = new StringBuilder();
        var routes = new StringBuilder();
        foreach (var (call, applCallPeer, peerAxipInsidePort) in peers)
        {
            maps.Append($" MAP {call} 127.0.0.1 UDP {peerAxipInsidePort} B\n");
            maps.Append($" MAP {applCallPeer} 127.0.0.1 UDP {peerAxipInsidePort} B\n");
            routes.Append($"{call},200,2\n");
        }

        return $"""
        SIMPLE=1
        NODECALL={nodeCall}
        NODEALIAS={nodeAlias}
        LOCATOR=NONE
        NODESINTERVAL=1
        AGWPORT={agwInsidePort}
        AGWSESSIONS=10
        AGWMASK=1
        APPLICATIONS={applAlias}
        APPL1CALL={applCall}
        APPL1ALIAS={applAlias}

        PORT
         ID=Telnet
         DRIVER=Telnet
         CONFIG
         TCPPORT={telnetInsidePort}
         HTTPPORT={httpInsidePort}
         NETROMPORT={netromInsidePort}
         FBBPORT={fbbInsidePort}
         APIPORT={apiInsidePort}
         MAXSESSIONS=20
         USER=test,test,{nodeCall},,SYSOP
        ENDPORT

        PORT
         ID=AXIP
         DRIVER=BPQAXIP
         QUALITY=200
         MINQUAL=1
         CONFIG
         UDP {axipInsidePort}
         BROADCAST NODES
        {maps}ENDPORT

        ROUTES:
        {routes}***

        """;
    }

    private static async Task WaitForTcp(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"linbpq AGW port {port} did not open within {timeout} (last error: {last?.Message})");
    }
}

[CollectionDefinition("Linbpq three-instance integration", DisableParallelization = true)]
public class ThreeInstanceLinbpqCollection : ICollectionFixture<ThreeInstanceLinbpqFixture> { }
