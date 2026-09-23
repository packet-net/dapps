using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two m0lte/linbpq containers wired together over AXIP-UDP.
///
/// Topology (shared with the linbpq native two-instance test pattern):
///
///     SUT-side AGW client ─AGW─ BPQ-A ─AXIP-UDP─ BPQ-B ─AGW─ Receiver
///
/// Both BPQs are configured with an APPLICATION binding so the dialled
/// callsign (APPL1CALL) is dispatched to a registered AGW listener on
/// the receiving side. AXIP MAP entries on each side route SABMs for
/// the peer's NODECALL and APPL1CALL over UDP to the peer.
///
/// Networking: both containers share container A's network namespace
/// (<c>--network=container:&lt;a&gt;</c>) so they reach each other on the
/// same loopback - equivalent to the two-process-on-127.0.0.1 setup
/// the upstream linbpq test runs on natively. Bridge networking with
/// distinct container IPs has not been verified to work for AGW
/// dispatch and is avoided.
///
/// Closes #5 - same Testcontainers migration as
/// <see cref="LinbpqIntegrationFixture"/>. Container B uses
/// <c>WithNetworkMode("container:&lt;A's id&gt;")</c> so its loopback is
/// A's, and its AGW port is published via A's port mappings.
/// </summary>
public sealed class TwoInstanceLinbpqFixture : IAsyncLifetime
{
    private const string Image = "m0lte/linbpq:latest";

    private const int InsideAgwPortA = 18001;
    private const int InsideAgwPortB = 18002;
    private const int InsideAxipPortA = 19001;
    private const int InsideAxipPortB = 19002;

    public string Host => "127.0.0.1";
    public int AgwPortA { get; private set; }
    public int AgwPortB { get; private set; }

    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-9";
    public string ApplCallB => "N0BBB-9";

    /// <summary>AGW port byte (0-indexed) that points at the AXIP carrier
    /// port on either BPQ - port 1 is Telnet, port 2 is AXIP, hence index 1.
    /// Use this when the SUT issues a connect that needs to reach the
    /// peer instance.</summary>
    public int AxipPortIndex => 1;

    private IContainer? _containerA;
    private IContainer? _containerB;

    public async ValueTask InitializeAsync()
    {
        var configA = Encoding.UTF8.GetBytes(RenderConfig(
            nodeCall: CallsignA, nodeAlias: "AAA",
            applCall: ApplCallA, applAlias: "APPLA",
            agwInsidePort: InsideAgwPortA, axipInsidePort: InsideAxipPortA,
            peerCall: CallsignB, peerApplCall: ApplCallB, peerAxipInsidePort: InsideAxipPortB,
            telnetInsidePort: 18011, httpInsidePort: 18021,
            netromInsidePort: 18031, fbbInsidePort: 18041, apiInsidePort: 18051));

        var configB = Encoding.UTF8.GetBytes(RenderConfig(
            nodeCall: CallsignB, nodeAlias: "BBB",
            applCall: ApplCallB, applAlias: "APPLB",
            agwInsidePort: InsideAgwPortB, axipInsidePort: InsideAxipPortB,
            peerCall: CallsignA, peerApplCall: ApplCallA, peerAxipInsidePort: InsideAxipPortA,
            telnetInsidePort: 18012, httpInsidePort: 18022,
            netromInsidePort: 18032, fbbInsidePort: 18042, apiInsidePort: 18052));

        // A publishes both AGW ports. Because B shares A's netns, B's
        // AGW port is also reachable on the host via A's port mapping.
        _containerA = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configA, "/data/bpq32.cfg")
            .WithPortBinding(InsideAgwPortA, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwPortB, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPortA))
            .Build();

        await _containerA.StartAsync();
        AgwPortA = _containerA.GetMappedPublicPort(InsideAgwPortA);
        AgwPortB = _containerA.GetMappedPublicPort(InsideAgwPortB);

        // B shares A's netns. No port bindings here - A's already
        // publish them. Testcontainers.NET doesn't expose
        // `--network=container:X` directly, so we drop into the
        // create-parameter modifier and set HostConfig.NetworkMode.
        // The wait strategy runs from inside B's filesystem; pinning
        // it on B's AGW port works because that port is bound inside
        // the shared netns.
        var aId = _containerA.Id;
        _containerB = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configB, "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{aId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPortB))
            .Build();

        await _containerB.StartAsync();

        // Tiny grace period: linbpq's AGW listener accepts immediately but
        // the AXIP UDP socket binds a moment later.
        await Task.Delay(2000);

        // Sanity check from the host side - both AGW ports should be
        // reachable. The wait strategies above polled from inside the
        // container; this confirms the host-side mapping landed.
        await WaitForTcp(Host, AgwPortA, TimeSpan.FromSeconds(5));
        await WaitForTcp(Host, AgwPortB, TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose B first - sharing A's netns means killing A pulls B's
        // network out from under it. Reverse-order shutdown is the
        // graceful path.
        if (_containerB is not null) await _containerB.DisposeAsync();
        if (_containerA is not null) await _containerA.DisposeAsync();
    }

    private static string RenderConfig(
        string nodeCall, string nodeAlias,
        string applCall, string applAlias,
        int agwInsidePort, int axipInsidePort,
        string peerCall, string peerApplCall, int peerAxipInsidePort,
        int telnetInsidePort, int httpInsidePort,
        int netromInsidePort, int fbbInsidePort, int apiInsidePort) =>
        $"""
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
         MAP {peerCall} 127.0.0.1 UDP {peerAxipInsidePort} B
         MAP {peerApplCall} 127.0.0.1 UDP {peerAxipInsidePort} B
        ENDPORT

        ROUTES:
        {peerCall},200,2
        ***

        """;

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

[CollectionDefinition("Linbpq two-instance integration", DisableParallelization = true)]
public class TwoInstanceLinbpqCollection : ICollectionFixture<TwoInstanceLinbpqFixture> { }
