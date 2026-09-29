using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// A pdn node and a BPQ node linked over AXUDP, pdn's AXUDP port facing
/// BPQ's AXIP port, each with a DAPPS daemon: pdn's on RHPv2, BPQ's on AGW.
///
///     app -> DAPPS A -RHPv2- pdn-A -AXUDP- BPQ-B -AGW- DAPPS B -> app
///
/// pdn shares BPQ's network namespace (as the BPQ pairs do), and BPQ
/// publishes its AGW port and pdn's web and RHPv2 ports. The monitors are
/// pdn's frame feed on A and BPQ's AGW monitor on B. BPQ's AXIP port has
/// QUALITY=0, so no NET/ROM: what goes over the link is DAPPS's own traffic.
///
/// BPQ maps only DAPPS's callsign on pdn, not pdn's node callsign as well:
/// with two MAP lines for one address BPQ sends every frame twice. pdn copes
/// with that since node-v0.56.0 (before, the second UA put it into a loop of
/// link resets, packet.net#842), but the tests count frames on the link.
///
/// pdn's port dials plain v2.0 (<c>link: dial: v20</c>), as pdn's docs say
/// for a BPQ neighbour: many BPQ builds ignore a v2.2 SABME rather than
/// refuse it. <c>DAPPS_PDN_RADIO</c> tries other port settings, e.g.
/// <c>DIAL=auto</c> (see <see cref="PdnRadio.With"/>).
/// </summary>
public sealed class PdnBpqFixture : IDappsNodePair, IAsyncLifetime
{
    private const string BpqImage = "m0lte/linbpq:latest";

    private const int InsideAgwB = 18002;
    private const int InsideHttpA = 18301;
    private const int InsideRhpA = 18401;
    private const int AxudpPdn = 19001;
    private const int AxipBpq = 19002;

    private IContainer? bpq;
    private IContainer? pdn;

    public string Host => "127.0.0.1";
    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-3";
    public string ApplCallB => "N0BBB-9";
    public int AgwPortB { get; private set; }
    public int HttpPortA { get; private set; }
    public int RhpPortA { get; private set; }

    /// <summary>pdn's AXUDP port is its first ("1" to RHPv2); BPQ's AXIP is its second (AGW port byte 1).</summary>
    public NodeAttachment NodeA => NodeAttachment.Rhp(Host, RhpPortA, 0);
    public NodeAttachment NodeB => NodeAttachment.Agw(Host, AgwPortB, 1);

    public string ChannelName => "pdn and BPQ, AXUDP to AXIP";

    private static PdnRadio Radio => new PdnRadio(Dial: "v20").With(Environment.GetEnvironmentVariable("DAPPS_PDN_RADIO"));

    public async ValueTask InitializeAsync()
    {
        bpq = new ContainerBuilder()
            .WithImage(BpqImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig()), "/data/bpq32.cfg")
            .WithPortBinding(InsideAgwB, assignRandomHostPort: true)
            .WithPortBinding(InsideHttpA, assignRandomHostPort: true)
            .WithPortBinding(InsideRhpA, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwB))
            .Build();
        await bpq.StartAsync();
        AgwPortB = bpq.GetMappedPublicPort(InsideAgwB);
        HttpPortA = bpq.GetMappedPublicPort(InsideHttpA);
        RhpPortA = bpq.GetMappedPublicPort(InsideRhpA);

        var port = $"""
            - id: axudp
              transport:
                kind: axudp
                host: 127.0.0.1
                port: {AxipBpq}
                localPort: {AxudpPdn}

            """ + string.Concat(Radio.Yaml().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "  " + l + "\n"));
        pdn = await PdnNode.StartAsync(PdnNode.Config(CallsignA, "AAA", port, InsideHttpA, InsideRhpA), bpq.Id, []);
        await PdnNode.WaitUntilUpAsync(Host, HttpPortA, CallsignA, TimeSpan.FromMinutes(1));
        // BPQ's AGW listener accepts at once, but its AXIP socket binds a
        // moment later.
        await Task.Delay(2000);
    }

    public async Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct) =>
        await AirMonitor.StartAsync([AirMonitor.Tap.PdnNode("A", Host, HttpPortA), AirMonitor.Tap.Bpq("B", Host, AgwPortB)], ct);

    private string BpqConfig() => $"""
        SIMPLE=1
        NODECALL={CallsignB}
        NODEALIAS=BBB
        LOCATOR=NONE
        IDINTERVAL=0
        AGWPORT={InsideAgwB}
        AGWSESSIONS=10
        AGWMASK=1
        APPLICATIONS=APPLB
        APPL1CALL={ApplCallB}
        APPL1ALIAS=APPLB

        PORT
         ID=Telnet
         DRIVER=Telnet
         CONFIG
         TCPPORT=18012
         HTTPPORT=18022
         MAXSESSIONS=10
         USER=test,test,{CallsignB},,SYSOP
        ENDPORT

        PORT
         ID=AXIP
         DRIVER=BPQAXIP
         QUALITY=0
         CONFIG
         UDP {AxipBpq}
         MAP {ApplCallA} 127.0.0.1 UDP {AxudpPdn}
        ENDPORT

        """;

    public async ValueTask DisposeAsync()
    {
        // pdn lives in BPQ's network namespace; stop it first.
        if (pdn is not null) await pdn.DisposeAsync();
        if (bpq is not null) await bpq.DisposeAsync();
    }
}

[CollectionDefinition("pdn and BPQ", DisableParallelization = true)]
public class PdnBpqCollection : ICollectionFixture<PdnBpqFixture> { }

/// <summary>
/// A pair seen from the other end: its B is this one's A. The shared
/// exchange cases mostly have A dial, so this runs them with the other
/// node calling.
/// </summary>
public sealed class SwappedPair(IDappsNodePair pair) : IDappsNodePair
{
    public string ChannelName => pair.ChannelName + ", the other way";
    public string ApplCallA => pair.ApplCallB;
    public string ApplCallB => pair.ApplCallA;
    public NodeAttachment NodeA => pair.NodeB;
    public NodeAttachment NodeB => pair.NodeA;

    public async Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct) => new Swapped(await pair.StartAirMonitorAsync(ct));

    private sealed class Swapped(IAirMonitor air) : IAirMonitor
    {
        private static string Other(string side) => side == "A" ? "B" : "A";

        public IReadOnlyList<string> SentBy(string from) => air.SentBy(Other(from));

        public int CountSentBy(string from, string contains) => air.CountSentBy(Other(from), contains);

        public Task<bool> WaitForAsync(string from, string contains, TimeSpan timeout, CancellationToken ct) =>
            air.WaitForAsync(Other(from), contains, timeout, ct);

        public string Transcript() => air.Transcript();

        public ValueTask DisposeAsync() => air.DisposeAsync();
    }
}
