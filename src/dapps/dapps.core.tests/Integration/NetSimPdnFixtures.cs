using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two packet.net nodes (pdn) on the simulated radio channel, each on its
/// simulated radio over KISS-TCP and with its DAPPS daemon on RHPv2:
///
///     DAPPS A -RHPv2- pdn-A -KISS- [net-sim: modem ~ channel ~ modem] -KISS- pdn-B -RHPv2- DAPPS B
///
/// As with BPQ (<see cref="NetSimTwoBpqFixture"/>), both nodes share
/// net-sim's network namespace, and net-sim publishes their web and RHPv2
/// ports. Each node's frame feed is its monitor. The radio ports are set
/// as close to the BPQ fixtures' as pdn allows, so the two compare (see
/// each channel's fixture for the settings and why);
/// <c>DAPPS_NETSIM_PDN_RADIO</c> tries others without a rebuild, e.g.
/// <c>T1=10000,WINDOW=2,ACKMODE=1,T1FROMTX=1</c> (see <see cref="PdnRadio.With"/>).
/// </summary>
public abstract class NetSimTwoPdnFixture : NetSimTwoNodeFixture
{
    private const int InsideHttpA = 18301, InsideHttpB = 18302;
    private const int InsideRhpA = 18401, InsideRhpB = 18402;

    private IContainer? pdnA;
    private IContainer? pdnB;

    public int HttpPortA => MappedPort(InsideHttpA);
    public int HttpPortB => MappedPort(InsideHttpB);
    public int RhpPortA => MappedPort(InsideRhpA);
    public int RhpPortB => MappedPort(InsideRhpB);

    /// <summary>The radio is each node's first port: "1" to RHPv2, 0 to DAPPS.</summary>
    public override NodeAttachment NodeA => NodeAttachment.Rhp(Host, RhpPortA, 0);
    public override NodeAttachment NodeB => NodeAttachment.Rhp(Host, RhpPortB, 0);

    protected override IReadOnlyList<int> NodePorts => [InsideHttpA, InsideHttpB, InsideRhpA, InsideRhpB];

    public override string ReportTag => "pdn-" + Modem;

    /// <summary>pdn's port tuning for this channel.</summary>
    protected abstract PdnRadio DefaultRadio { get; }

    /// <summary><see cref="DefaultRadio"/> with any overrides from DAPPS_NETSIM_PDN_RADIO.</summary>
    protected PdnRadio Radio => DefaultRadio.With(Environment.GetEnvironmentVariable("DAPPS_NETSIM_PDN_RADIO"));

    public override string RadioSettings => "pdn " + Radio;

    public override async Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct) =>
        await AirMonitor.StartAsync([AirMonitor.Tap.PdnNode("A", Host, HttpPortA), AirMonitor.Tap.PdnNode("B", Host, HttpPortB)], ct);

    protected override async Task StartNodesAsync()
    {
        pdnA = await PdnNode.StartAsync(PdnNode.Config(CallsignA, "AAA", RadioPort(KissPortA), InsideHttpA, InsideRhpA), NetSimId, []);
        pdnB = await PdnNode.StartAsync(PdnNode.Config(CallsignB, "BBB", RadioPort(KissPortB), InsideHttpB, InsideRhpB), NetSimId, []);
    }

    private string RadioPort(int kissPort) => $"""
        - id: "{PdnNode.PortId}"
          transport:
            kind: kiss-tcp
            host: 127.0.0.1
            port: {kissPort}

        """ + string.Concat(Radio.Yaml().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "  " + l + "\n"));

    protected override async Task StopNodesAsync()
    {
        if (pdnB is not null) await pdnB.DisposeAsync();
        if (pdnA is not null) await pdnA.DisposeAsync();
        pdnA = pdnB = null;
    }

    /// <summary>
    /// Both nodes afresh, as after a reboot, waiting only until their web
    /// ports answer (for the monitor), not for their radio ports.
    /// </summary>
    public override async Task RestartNodesColdAsync()
    {
        await base.RestartNodesColdAsync();
        await PdnNode.WaitUntilUpAsync(Host, HttpPortA, CallsignA, TimeSpan.FromMinutes(1), portsUp: false);
        await PdnNode.WaitUntilUpAsync(Host, HttpPortB, CallsignB, TimeSpan.FromMinutes(1), portsUp: false);
    }

    /// <summary>Wait until both nodes' radio ports are up and each has heard the other.</summary>
    public override async Task WaitUntilReadyAsync()
    {
        await PdnNode.WaitUntilUpAsync(Host, HttpPortA, CallsignA, TimeSpan.FromMinutes(1));
        await PdnNode.WaitUntilUpAsync(Host, HttpPortB, CallsignB, TimeSpan.FromMinutes(1));
        await PdnNode.WaitUntilHeardAsync(Host, RhpPortA, CallsignA + "-15", HttpPortB, ChannelName);
        await PdnNode.WaitUntilHeardAsync(Host, RhpPortB, CallsignB + "-15", HttpPortA, ChannelName);
    }
}

/// <summary>
/// AFSK 1200 on Dire Wolf, as <see cref="NetSimAfsk1200Fixture"/>, with pdn nodes.
/// </summary>
public sealed class NetSimPdnAfsk1200Fixture : NetSimTwoPdnFixture
{
    public override string ChannelName => "AFSK 1200 (Dire Wolf), pdn";
    protected override string PortYaml => AfskPort;

    /// <summary>
    /// The BPQ fixture's settings in pdn's terms: T1 7 s as BPQ's FRACK,
    /// T2 1 s as its RESPTIME (pdn's default is 3 s, which holds every
    /// RR back that long), 10 retries, a window of 4 and 120-byte frames,
    /// 150 ms TX delay, PERSIST 64 with a 100 ms slot. pdn always sends
    /// the TNC a TX tail, 0 unless set, where BPQ sends none; Dire Wolf's
    /// own default, 100 ms, is what the BPQ runs had, so 10 here. pdn
    /// dials v2.2 first (SABME), and the other end is pdn too, so the
    /// links are v2.2.
    /// </summary>
    internal static readonly PdnRadio Afsk = new(
        T1Ms: 7000, T2Ms: 1000, N2: 10, Window: 4, N1: 120, TxDelay: 15, Persistence: 64, SlotTime: 10, TxTail: 10);

    protected override PdnRadio DefaultRadio => Afsk;
}

/// <summary>
/// QPSK 3600 on pdn-soundmodem, as <see cref="NetSimQpsk3600Fixture"/>, with pdn nodes.
/// </summary>
public sealed class NetSimPdnQpsk3600Fixture : NetSimTwoPdnFixture
{
    public override string ChannelName => "QPSK 3600 (pdn-soundmodem), pdn";
    protected override string PortYaml => QpskPort;

    // The BPQ fixture's settings in pdn's terms, as at AFSK 1200: T1 4 s
    // as BPQ's FRACK, T2 1 s, a window of 7 and 236-byte frames. TX tail
    // 20 ms, pdn-soundmodem's own default, which the BPQ runs had.
    protected override PdnRadio DefaultRadio => new(
        T1Ms: 4000, T2Ms: 1000, N2: 10, Window: 7, N1: 236, TxDelay: 15, Persistence: 64, SlotTime: 10, TxTail: 2);
}

/// <summary>
/// The soak's marginal AFSK 1200 link, as <see cref="NetSimNoisyAfsk1200Fixture"/>,
/// with pdn nodes. Only started when the soak is asked for (DAPPS_SOAK_MINUTES).
/// </summary>
public sealed class NetSimPdnNoisyAfsk1200Fixture : NetSimTwoPdnFixture
{
    public override string ChannelName => "AFSK 1200 (Dire Wolf), noisy, pdn";
    protected override string PortYaml => AfskPort;
    protected override double DefaultPathLossDb => 156.5;

    // As at AFSK 1200, with BPQ's 300 ms of TX delay for the edge of range.
    protected override PdnRadio DefaultRadio => NetSimPdnAfsk1200Fixture.Afsk with { TxDelay = 30 };
    protected override bool Wanted => SoakSettings.Requested;
}

[CollectionDefinition("net-sim pdn AFSK 1200", DisableParallelization = true)]
public class NetSimPdnAfsk1200Collection : ICollectionFixture<NetSimPdnAfsk1200Fixture> { }

[CollectionDefinition("net-sim pdn QPSK 3600", DisableParallelization = true)]
public class NetSimPdnQpsk3600Collection : ICollectionFixture<NetSimPdnQpsk3600Fixture> { }

[CollectionDefinition("net-sim pdn noisy AFSK 1200", DisableParallelization = true)]
public class NetSimPdnNoisyAfsk1200Collection : ICollectionFixture<NetSimPdnNoisyAfsk1200Fixture> { }
