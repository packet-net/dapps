using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two linbpq nodes on a simulated radio channel: net-sim runs real
/// modems (samoyed for AFSK, pdn-soundmodem for its FM modes such as
/// QPSK 3600) and an audio router between them, and each BPQ attaches to
/// one simulated radio over KISS, as it would to a real TNC. Unlike the
/// AXIP fixture, frames take real airtime, with TX delay, turnarounds and
/// a shared channel where both ends can transmit at once.
///
///     DAPPS A -AGW- BPQ-A -KISS- [net-sim: modem ~ channel ~ modem] -KISS- BPQ-B -AGW- DAPPS B
///
/// Networking follows <see cref="TwoInstanceLinbpqFixture"/>: both BPQs
/// share net-sim's network namespace, so each dials its KISS port on
/// 127.0.0.1, and net-sim publishes both AGW ports to the host.
///
/// BPQ's NET/ROM and ID broadcasts are off (QUALITY=0, IDINTERVAL=0), so
/// what goes on air is DAPPS's own traffic, and the timings are DAPPS's.
/// </summary>
public abstract class NetSimTwoBpqFixture : IAsyncLifetime
{
    /// <summary>net-sim with pdn-soundmodem 0.80.0, pinned so a new build
    /// can't change results unnoticed. Refresh: pull
    /// ghcr.io/packet-net/net-sim:main and take its digest.</summary>
    public const string NetSimImage = "ghcr.io/packet-net/net-sim@sha256:b0f78c6fd4f65c7d21e3d5f2148cb58be8951ceb386cdadc3e06e7a048444c76";
    private const string BpqImage = "m0lte/linbpq:latest";

    private const int InsideWebPort = 8080;
    private const int InsideAgwPortA = 18101;
    private const int InsideAgwPortB = 18102;
    private const int KissPortA = 18201;
    private const int KissPortB = 18202;

    public string Host => "127.0.0.1";
    public int AgwPortA { get; private set; }
    public int AgwPortB { get; private set; }
    public int NetSimWebPort { get; private set; }
    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-3";
    public string ApplCallB => "N0BBB-3";

    /// <summary>AGW port index of the radio port (port 1 is Telnet).</summary>
    public int RadioPortIndex => 1;

    /// <summary>What's on the channel, for reports: e.g. "QPSK 3600 (pdn-soundmodem)".</summary>
    public abstract string ChannelName { get; }

    /// <summary>The net-sim port settings for both radios: modem, and TNC if not samoyed.</summary>
    protected abstract string PortYaml { get; }

    /// <summary>Each direction's path: loss, and noise if any.</summary>
    protected virtual string LinkYaml => "loss_db: 10";

    /// <summary>BPQ's KISS port tuning for this channel.</summary>
    protected abstract BpqRadio DefaultRadio { get; }

    /// <summary>
    /// <see cref="DefaultRadio"/> with any overrides from
    /// DAPPS_NETSIM_RADIO, e.g. <c>PERSIST=64,SLOTTIME=100,MAXFRAME=7</c>,
    /// for trying other tunings without a rebuild.
    /// </summary>
    protected BpqRadio Radio
    {
        get
        {
            var radio = DefaultRadio;
            foreach (var pair in (Environment.GetEnvironmentVariable("DAPPS_NETSIM_RADIO") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                var v = int.Parse(kv[1]);
                radio = kv[0].ToUpperInvariant() switch
                {
                    "TXDELAY" => radio with { TxDelayMs = v },
                    "PERSIST" => radio with { Persist = v },
                    "SLOTTIME" => radio with { SlotTimeMs = v },
                    "MAXFRAME" => radio with { Maxframe = v },
                    "PACLEN" => radio with { Paclen = v },
                    "FRACK" => radio with { FrackMs = v },
                    "RESPTIME" => radio with { RespTimeMs = v },
                    "RETRIES" => radio with { Retries = v },
                    _ => throw new ArgumentException($"DAPPS_NETSIM_RADIO: unknown setting {kv[0]}"),
                };
            }
            return radio;
        }
    }

    /// <summary>False for a fixture whose tests only run on request (the
    /// soak), so skipped tests don't start containers.</summary>
    protected virtual bool Wanted => true;

    public sealed record BpqRadio(
        int Speed, int TxDelayMs, int Paclen, int Maxframe, int FrackMs, int RespTimeMs, int Retries, int Persist, int SlotTimeMs)
    {
        public override string ToString() =>
            $"TXDELAY={TxDelayMs} PERSIST={Persist} SLOTTIME={SlotTimeMs} MAXFRAME={Maxframe} PACLEN={Paclen} FRACK={FrackMs} RESPTIME={RespTimeMs} RETRIES={Retries}";
    }

    /// <summary>The BPQ radio-port settings in use, for reports.</summary>
    public string RadioSettings => Radio.ToString();

    /// <summary>Start recording when each radio transmits.</summary>
    internal Task<ChannelLog> StartChannelLogAsync(CancellationToken ct) => ChannelLog.StartAsync(Host, NetSimWebPort, ct);

    private IContainer? netSim;
    private IContainer? bpqA;
    private IContainer? bpqB;

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;

        netSim = new ContainerBuilder()
            .WithImage(NetSimImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(NetworkYaml()), "/etc/sim/network.yaml")
            .WithPortBinding(InsideWebPort, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwPortA, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwPortB, assignRandomHostPort: true)
            .WithCreateParameterModifier(p => p.HostConfig.CapAdd = ["SYS_NICE"])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(InsideWebPort)
                .ForPath("/api/status")
                .ForResponseMessageMatching(async m => (await m.Content.ReadAsStringAsync()).Contains("\"running\":true")),
                o => o.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await netSim.StartAsync();
        NetSimWebPort = netSim.GetMappedPublicPort(InsideWebPort);
        AgwPortA = netSim.GetMappedPublicPort(InsideAgwPortA);
        AgwPortB = netSim.GetMappedPublicPort(InsideAgwPortB);

        bpqA = await StartBpqAsync(CallsignA, "AAA", ApplCallA, "APPLA", InsideAgwPortA, KissPortA, 18111);
        bpqB = await StartBpqAsync(CallsignB, "BBB", ApplCallB, "APPLB", InsideAgwPortB, KissPortB, 18112);

        // Give each BPQ a moment to open its KISS link to the simulator.
        await Task.Delay(3000);
    }

    private async Task<IContainer> StartBpqAsync(
        string nodeCall, string nodeAlias, string applCall, string applAlias, int agwPort, int kissPort, int telnetPort)
    {
        var bpq = new ContainerBuilder()
            .WithImage(BpqImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig(nodeCall, nodeAlias, applCall, applAlias, agwPort, kissPort, telnetPort)), "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{netSim!.Id}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(agwPort))
            .Build();
        await bpq.StartAsync();
        return bpq;
    }

    /// <summary>
    /// Take the channel down for <paramref name="outage"/>, then bring it
    /// back: net-sim stops its router and modems, as if both radios were
    /// switched off, so each BPQ loses its KISS link and has to reconnect.
    /// <paramref name="log"/>, if given, is told the channel went quiet.
    /// </summary>
    internal async Task ChannelOutageAsync(TimeSpan outage, CancellationToken ct, ChannelLog? log = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Host}:{NetSimWebPort}/") };
        (await http.PostAsync("api/stop", null, ct)).EnsureSuccessStatusCode();
        log?.ChannelStopped(DateTime.UtcNow);
        await Task.Delay(outage, ct);
        (await http.PostAsync("api/start", null, ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (bpqB is not null) await bpqB.DisposeAsync();
        if (bpqA is not null) await bpqA.DisposeAsync();
        if (netSim is not null) await netSim.DisposeAsync();
    }

    private string NetworkYaml() => $"""
        mixer_mode: fm_capture
        capture_db: 6.0
        collision_mode: silence
        time_scale: 1
        nodes:
          - id: a
            ports:
              - id: radio
        {Indent(PortYaml, 8)}
                kiss_port: {KissPortA}
          - id: b
            ports:
              - id: radio
        {Indent(PortYaml, 8)}
                kiss_port: {KissPortB}
        links:
          - from: a.radio
            to: b.radio
        {Indent(LinkYaml, 4)}
          - from: b.radio
            to: a.radio
        {Indent(LinkYaml, 4)}

        """;

    private static string Indent(string yaml, int spaces) =>
        string.Join('\n', yaml.Split('\n').Select(l => new string(' ', spaces) + l));

    private string BpqConfig(string nodeCall, string nodeAlias, string applCall, string applAlias, int agwPort, int kissPort, int telnetPort) => $"""
        SIMPLE=1
        NODECALL={nodeCall}
        NODEALIAS={nodeAlias}
        LOCATOR=NONE
        IDINTERVAL=0
        AGWPORT={agwPort}
        AGWSESSIONS=10
        AGWMASK=1
        APPLICATIONS={applAlias}
        APPL1CALL={applCall}
        APPL1ALIAS={applAlias}

        PORT
         ID=Telnet
         DRIVER=Telnet
         CONFIG
         TCPPORT={telnetPort}
         HTTPPORT={telnetPort + 100}
         MAXSESSIONS=10
         USER=test,test,{nodeCall},,SYSOP
        ENDPORT

        PORT
         ID=Radio
         TYPE=ASYNC
         PROTOCOL=KISS
         IPADDR=127.0.0.1
         TCPPORT={kissPort}
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
         QUALITY=0
        ENDPORT

        """;
}

/// <summary>AFSK 1200 on samoyed: the channel in Kevin M0AHN's analysed trace.</summary>
public sealed class NetSimAfsk1200Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "AFSK 1200 (samoyed)";

    // A two-station link, so send as soon as the channel is clear:
    // PERSIST 255 with a 10 ms slot. With a 100 ms slot the WPS scenario
    // took about 15% longer, as the modem waits a slot before every
    // transmission; MAXFRAME 7 and PACLEN 236 made no difference, as each
    // message fits in one or two frames.
    protected override string PortYaml => "modem: { mode: afsk1200 }";
    protected override BpqRadio DefaultRadio => new(Speed: 1200, TxDelayMs: 150, Paclen: 120, Maxframe: 4, FrackMs: 3000, RespTimeMs: 1000, Retries: 10, Persist: 255, SlotTimeMs: 10);
}

/// <summary>QPSK 3600 on pdn-soundmodem: 7200 bps in one FM channel, Kevin's "3K6" link.</summary>
public sealed class NetSimQpsk3600Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "QPSK 3600 (pdn-soundmodem)";
    protected override string PortYaml => "tnc: pdn\nmodem: { mode: qpsk3600 }";
    protected override BpqRadio DefaultRadio => new(Speed: 7200, TxDelayMs: 150, Paclen: 236, Maxframe: 7, FrackMs: 2000, RespTimeMs: 500, Retries: 10, Persist: 255, SlotTimeMs: 10);
}

/// <summary>
/// AFSK 1200 with the channel's hiss on, for the soak: frames get lost
/// and retried as on a marginal path. Only started when the soak is asked
/// for (DAPPS_SOAK_MINUTES).
/// </summary>
public sealed class NetSimNoisyAfsk1200Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "AFSK 1200 (samoyed), noisy";

    // Traffic starts at both ends independently here, so the usual
    // shared-channel PERSIST 64 and 100 ms slot rather than the
    // send-at-once tuning above.
    protected override string PortYaml => "modem: { mode: afsk1200 }";
    protected override string LinkYaml => "loss_db: 20\nnoise_db: 22";
    protected override BpqRadio DefaultRadio => new(Speed: 1200, TxDelayMs: 150, Paclen: 120, Maxframe: 4, FrackMs: 3000, RespTimeMs: 1000, Retries: 10, Persist: 64, SlotTimeMs: 100);
    protected override bool Wanted => SoakSettings.Requested;
}

/// <summary>The soak only runs when DAPPS_SOAK_MINUTES is set.</summary>
public static class SoakSettings
{
    public static bool Requested => Minutes is not null;

    public static int? Minutes =>
        int.TryParse(Environment.GetEnvironmentVariable("DAPPS_SOAK_MINUTES"), out var m) && m > 0 ? m : null;
}

// Real-time modems are sensitive to a busy host (lost audio ticks look
// like RF errors), so these run on their own, not alongside other tests.
[CollectionDefinition("net-sim AFSK 1200", DisableParallelization = true)]
public class NetSimAfsk1200Collection : ICollectionFixture<NetSimAfsk1200Fixture> { }

[CollectionDefinition("net-sim QPSK 3600", DisableParallelization = true)]
public class NetSimQpsk3600Collection : ICollectionFixture<NetSimQpsk3600Fixture> { }

[CollectionDefinition("net-sim noisy AFSK 1200", DisableParallelization = true)]
public class NetSimNoisyAfsk1200Collection : ICollectionFixture<NetSimNoisyAfsk1200Fixture> { }
