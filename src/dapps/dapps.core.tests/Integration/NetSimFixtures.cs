using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Two packet nodes on a simulated radio channel: net-sim runs real modems
/// (Dire Wolf for AFSK, pdn-soundmodem for its FM modes such as QPSK 3600),
/// gives each an FM radio (a Tait TM8100 at 25 W by default) and puts a
/// physical FM channel between them; each node attaches to one simulated
/// radio over KISS, as it would to a real TNC. Unlike AXIP or AXUDP, frames
/// take real airtime, with TX delay, turnarounds and a shared channel where
/// both ends can transmit at once. The radios are half duplex: one that is
/// transmitting hears nothing, so when both transmit at once, neither hears
/// the other.
///
///     DAPPS A -- node A -KISS- [net-sim: modem ~ channel ~ modem] -KISS- node B -- DAPPS B
///
/// The nodes share net-sim's network namespace, so each dials its KISS port
/// on 127.0.0.1, and net-sim publishes the ports the tests and daemons use.
/// The nodes are BPQ (<see cref="NetSimTwoBpqFixture"/>) or pdn
/// (<see cref="NetSimTwoPdnFixture"/>).
/// </summary>
public abstract class NetSimTwoNodeFixture : IDappsScenarioBed, IAsyncLifetime
{
    /// <summary>net-sim v0.4.0 (the physical FM channel) with
    /// pdn-soundmodem 0.80.0, pinned so a new build can't change results
    /// unnoticed. Refresh: pull ghcr.io/packet-net/net-sim at the release
    /// tag and take its digest.</summary>
    public const string NetSimImage = "ghcr.io/packet-net/net-sim@sha256:634f1cd0e4835330817f4b4e6d0a904123b09518226d37f6f7cb3ab8d45255f3";

    private const int InsideWebPort = 8080;
    protected const int KissPortA = 18201;
    protected const int KissPortB = 18202;

    public string Host => "127.0.0.1";
    public int NetSimWebPort { get; private set; }
    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-3";
    public string ApplCallB => "N0BBB-3";

    /// <summary>What's on the channel, for reports: e.g. "QPSK 3600 (pdn-soundmodem)".</summary>
    public abstract string ChannelName { get; }

    /// <summary>The modem, for report file names: e.g. "qpsk3600".</summary>
    protected string Modem => ChannelName.Split(' ')[0].ToLowerInvariant() + ChannelName.Split(' ')[1];

    public virtual string ReportTag => Modem;

    public abstract string RadioSettings { get; }

    public abstract NodeAttachment NodeA { get; }
    public abstract NodeAttachment NodeB { get; }

    /// <summary>The net-sim port settings for both radios: TNC, modem and radio.</summary>
    protected abstract string PortYaml { get; }

    /// <summary>
    /// RF path loss each way, dB. With net-sim's default radios (25 W, a
    /// residential site's noise) 120 is a strong local link, and 1200
    /// baud goes from every frame to none between about 156 and 159.
    /// </summary>
    protected virtual double DefaultPathLossDb => 120;

    /// <summary><see cref="DefaultPathLossDb"/>, or DAPPS_NETSIM_PATH_LOSS
    /// to try another link without a rebuild.</summary>
    public double PathLossDb =>
        double.TryParse(Environment.GetEnvironmentVariable("DAPPS_NETSIM_PATH_LOSS"), System.Globalization.CultureInfo.InvariantCulture, out var db)
            ? db
            : DefaultPathLossDb;

    /// <summary>False for a fixture whose tests only run on request (the
    /// soak), so skipped tests don't start containers.</summary>
    protected virtual bool Wanted => true;

    /// <summary>Ports inside the shared network namespace that net-sim
    /// publishes to the host for the nodes (AGW, RHPv2, web).</summary>
    protected abstract IReadOnlyList<int> NodePorts { get; }

    /// <summary>Where one of <see cref="NodePorts"/> is on the host.</summary>
    protected int MappedPort(int inside) => netSim!.Container.GetMappedPublicPort(inside);

    /// <summary>net-sim's container, whose network namespace the nodes join.</summary>
    protected string NetSimId => netSim!.Container.Id;

    /// <summary>Start recording when each radio transmits.</summary>
    public async Task<ChannelLog?> StartChannelLogAsync(CancellationToken ct) => await ChannelLog.StartAsync(Host, NetSimWebPort, ct);

    public abstract Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct);

    private WatchedContainer? netSim;

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;
        if (await StartUntilReadyAsync() is { } failed) await RetryOnNewContainersAsync(failed);
    }

    /// <summary>Start all three containers; null once each node has heard
    /// the other, otherwise what went wrong.</summary>
    private async Task<string?> StartUntilReadyAsync()
    {
        try
        {
            await StartAllAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"{ChannelName}: the containers didn't start. {e.Message}";
        }
        return await TryUntilReadyAsync();
    }

    /// <summary>net-sim, then both nodes in its network namespace.</summary>
    private async Task StartAllAsync()
    {
        var builder = new ContainerBuilder()
            .WithImage(NetSimImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(NetworkYaml()), "/etc/sim/network.yaml")
            .WithPortBinding(InsideWebPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(p => p.HostConfig.CapAdd = ["SYS_NICE"])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(InsideWebPort)
                .ForPath("/api/status")
                .ForResponseMessageMatching(async m => (await m.Content.ReadAsStringAsync()).Contains("\"running\":true")),
                o => o.WithTimeout(TimeSpan.FromMinutes(2))));
        foreach (var port in NodePorts) builder = builder.WithPortBinding(port, assignRandomHostPort: true);
        netSim = new WatchedContainer("net-sim", builder.Build());
        await netSim.StartAsync();
        NetSimWebPort = netSim.Container.GetMappedPublicPort(InsideWebPort);

        await StartNodesAsync();
    }

    private async Task StopAllAsync()
    {
        await StopNodesAsync();
        if (netSim is not null) await netSim.DisposeAsync();
        netSim = null;
    }

    /// <summary>Start both nodes in net-sim's network namespace.</summary>
    protected abstract Task StartNodesAsync();

    /// <summary>Stop both nodes.</summary>
    protected abstract Task StopNodesAsync();

    /// <summary>Both nodes' containers, while they're up.</summary>
    private protected abstract IEnumerable<WatchedContainer?> NodeContainers { get; }

    private IEnumerable<WatchedContainer> Containers => new[] { netSim }.Concat(NodeContainers).OfType<WatchedContainer>();

    /// <summary>
    /// Restart both nodes, as after a reboot, and don't wait for them to be
    /// heard. Call <see cref="WaitUntilReadyAsync"/> afterwards, so later
    /// tests start warm.
    /// </summary>
    public virtual async Task RestartNodesColdAsync()
    {
        await StopNodesAsync();
        await StartNodesAsync();
    }

    /// <summary>
    /// Which container has stopped by itself, with its exit code, or null
    /// while net-sim and both nodes are running. Nothing more gets through
    /// once one has.
    /// </summary>
    public string? Died => Containers.Select(c => c.Died).FirstOrDefault(d => d is not null);

    /// <summary>
    /// Throws, with the containers' logs and <paramref name="detail"/>, if
    /// net-sim or a node has stopped by itself: nothing more can get
    /// through, so a test waiting for traffic should stop there rather than
    /// run out its time. The logs also go to scenario-reports/.
    /// </summary>
    public async Task ThrowIfDiedAsync(string? detail = null)
    {
        if (Died is not { } died) return;
        var logs = await LogsAsync();
        WriteFixtureLog("died", $"{ChannelName}: {died}.\n\n{logs}");
        throw new InvalidOperationException($"{ChannelName}: {died}, so nothing more can get through.\n\n{logs}\n{detail}");
    }

    /// <summary>
    /// Wait until each node has been heard by the other. If a container has
    /// stopped, or that takes too long (2 minutes a way), the containers'
    /// logs go to scenario-reports/, all three are recreated, and it tries
    /// once more; only a second failure throws, with both sets of logs.
    /// linbpq has been seen to exit about 10 s after it starts (#201).
    /// </summary>
    public async Task WaitUntilReadyAsync()
    {
        if (await TryUntilReadyAsync() is { } failed) await RetryOnNewContainersAsync(failed);
    }

    /// <summary>Wait until each node has been heard by the other, or throw
    /// a <see cref="TimeoutException"/> saying which wasn't.</summary>
    protected abstract Task WaitUntilEachHearsTheOtherAsync(CancellationToken ct);

    /// <summary>Null once each node has heard the other; otherwise what went wrong.</summary>
    private async Task<string?> TryUntilReadyAsync()
    {
        using var stop = new CancellationTokenSource();
        var hearing = WaitUntilEachHearsTheOtherAsync(stop.Token);
        var exited = Task.WhenAny(Containers.Select(c => (Task)c.Exited));
        if (await Task.WhenAny(hearing, exited) == exited)
        {
            await stop.CancelAsync();
            try { await hearing; } catch (Exception) { /* stopped */ }
            return $"{ChannelName}: {Died}.";
        }
        try
        {
            await hearing;
            return Died is { } died ? $"{ChannelName}: {died}." : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return e.Message.StartsWith(ChannelName, StringComparison.Ordinal) ? e.Message : $"{ChannelName}: {e.Message}";
        }
    }

    private async Task RetryOnNewContainersAsync(string first)
    {
        var firstLogs = await LogsAsync();
        WriteFixtureLog("retried", $"{first}\n\nRecreating the containers and trying again.\n\n{firstLogs}");
        await StopAllAsync();
        if (await StartUntilReadyAsync() is not { } second) return;
        var secondLogs = await LogsAsync();
        WriteFixtureLog("failed", $"{second}\n\nThe second try, on new containers, failed too.\n\n{secondLogs}");
        throw new TimeoutException($"{second}\nIt failed the same way on new containers.\n\nFirst try: {first}\n\n{firstLogs}\n\nSecond try:\n\n{secondLogs}");
    }

    /// <summary>The end of each container's log, for a failure message.</summary>
    public async Task<string> LogsAsync()
    {
        var sb = new StringBuilder();
        foreach (var c in Containers) sb.AppendLine(await c.LogTailAsync(80));
        return sb.ToString();
    }

    /// <summary>A readiness failure or a container that stopped, kept beside
    /// the scenario reports (which CI keeps) even when the retry works.</summary>
    private void WriteFixtureLog(string what, string text)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var name = $"fixture-{ReportTag}-{what}-{DateTime.UtcNow:HHmmss}.log";
        File.WriteAllText(Path.Combine(dir, name), text);
        TestContext.Current.SendDiagnosticMessage($"{ChannelName}: {what}; see scenario-reports/{name}");
    }

    /// <summary>
    /// Take the channel down for <paramref name="outage"/>, then bring it
    /// back: net-sim stops its router and modems, as if both radios were
    /// switched off, so each node loses its KISS link and has to reconnect.
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

    public async ValueTask DisposeAsync() => await StopAllAsync();

    private string NetworkYaml() => $"""
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
            path_loss_db: {PathLossDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}
          - from: b.radio
            to: a.radio
            path_loss_db: {PathLossDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}

        """;

    private static string Indent(string yaml, int spaces) =>
        string.Join('\n', yaml.Split('\n').Select(l => new string(' ', spaces) + l));

    /// <summary>AFSK 1200 on Dire Wolf with the squelch open.</summary>
    internal const string AfskPort = "tnc: direwolf\nmodem: { mode: afsk1200 }";

    /// <summary>
    /// QPSK 3600 on pdn-soundmodem. A 5 kHz deviation mode, so a wide
    /// (25 kHz) channel, as pdn's mode table says. Squelch closed: on an
    /// open-squelch receiver pdn's qpsk receiver loses frames (net-sim's
    /// docs/fm-channel.md).
    /// </summary>
    internal const string QpskPort = "tnc: pdn\nmodem: { mode: qpsk3600 }\nradio: { channel: wide, squelch: hard }";
}

/// <summary>
/// Two linbpq nodes on the simulated radio channel, each with its DAPPS
/// daemon on AGW:
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
public abstract class NetSimTwoBpqFixture : NetSimTwoNodeFixture
{
    private const string BpqImage = "m0lte/linbpq:latest";

    private const int InsideAgwPortA = 18101;
    private const int InsideAgwPortB = 18102;

    public int AgwPortA => MappedPort(InsideAgwPortA);
    public int AgwPortB => MappedPort(InsideAgwPortB);

    /// <summary>AGW port index of the radio port (port 1 is Telnet).</summary>
    public int RadioPortIndex => 1;

    public override NodeAttachment NodeA => NodeAttachment.Agw(Host, AgwPortA, RadioPortIndex);
    public override NodeAttachment NodeB => NodeAttachment.Agw(Host, AgwPortB, RadioPortIndex);

    protected override IReadOnlyList<int> NodePorts => [InsideAgwPortA, InsideAgwPortB];

    public override async Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct) => await AirMonitor.StartAsync(Host, AgwPortA, AgwPortB, ct);

    /// <summary>BPQ's KISS port tuning for this channel.</summary>
    protected abstract BpqRadio DefaultRadio { get; }

    /// <summary>
    /// <see cref="DefaultRadio"/> with any overrides from
    /// DAPPS_NETSIM_RADIO, e.g. <c>PERSIST=255,SLOTTIME=10,MAXFRAME=7</c>
    /// or <c>ACKMODE=1</c>, for trying other tunings without a rebuild.
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
                    "ACKMODE" => radio with { AckMode = v != 0 },
                    _ => throw new ArgumentException($"DAPPS_NETSIM_RADIO: unknown setting {kv[0]}"),
                };
            }
            return radio;
        }
    }

    /// <param name="AckMode">KISSOPTIONS=ACKMODE: the TNC tells BPQ when
    /// each frame has actually gone, and BPQ's FRACK (T1) runs from then
    /// instead of from when it handed the frame to the TNC.</param>
    public sealed record BpqRadio(
        int Speed, int TxDelayMs, int Paclen, int Maxframe, int FrackMs, int RespTimeMs, int Retries, int Persist, int SlotTimeMs, bool AckMode = false)
    {
        public override string ToString() =>
            $"TXDELAY={TxDelayMs} PERSIST={Persist} SLOTTIME={SlotTimeMs} MAXFRAME={Maxframe} PACLEN={Paclen} FRACK={FrackMs} RESPTIME={RespTimeMs} RETRIES={Retries}"
            + (AckMode ? " KISSOPTIONS=ACKMODE" : "");
    }

    /// <summary>The BPQ radio-port settings in use, for reports.</summary>
    public override string RadioSettings => "BPQ " + Radio;

    private WatchedContainer? bpqA;
    private WatchedContainer? bpqB;

    private protected override IEnumerable<WatchedContainer?> NodeContainers => [bpqA, bpqB];

    protected override async Task StartNodesAsync()
    {
        bpqA = await StartBpqAsync(CallsignA, "AAA", ApplCallA, "APPLA", InsideAgwPortA, KissPortA, 18111);
        bpqB = await StartBpqAsync(CallsignB, "BBB", ApplCallB, "APPLB", InsideAgwPortB, KissPortB, 18112);
    }

    protected override async Task StopNodesAsync()
    {
        if (bpqB is not null) await bpqB.DisposeAsync();
        if (bpqA is not null) await bpqA.DisposeAsync();
        bpqA = bpqB = null;
    }

    /// <summary>
    /// Wait until each BPQ has been heard by the other. Each BPQ has to
    /// open its KISS link to the simulator before it can transmit; until
    /// then a connect request waits (linbpq dials its KISS port 10 s after
    /// it starts), and a test's first call goes out late enough to cross
    /// the other side's.
    /// </summary>
    protected override async Task WaitUntilEachHearsTheOtherAsync(CancellationToken ct)
    {
        await WaitUntilHeardAsync(AgwPortA, ApplCallA, AgwPortB, ct);
        await WaitUntilHeardAsync(AgwPortB, ApplCallB, AgwPortA, ct);
    }

    /// <summary>
    /// Sends a UI frame from one node until the other node's monitor hears
    /// it: that node can transmit and the other receive. A BPQ still
    /// starting up can drop the AGW socket, so that just means try again.
    /// </summary>
    private async Task WaitUntilHeardAsync(int fromAgwPort, string fromCall, int toAgwPort, CancellationToken ct)
    {
        var limit = TimeSpan.FromMinutes(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            while (true)
            {
                try
                {
                    await HearAsync(fromAgwPort, fromCall, toAgwPort, cts.Token);
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException && !cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{ChannelName}: the BPQ on AGW port {fromAgwPort} sent UI frames from {fromCall} for {limit.TotalMinutes:F0} minutes " +
                $"and the one on AGW port {toAgwPort} never heard them. Is net-sim running both modems, and did each BPQ open its KISS link?");
        }
    }

    private async Task HearAsync(int fromAgwPort, string fromCall, int toAgwPort, CancellationToken ct)
    {
        using var sender = new TcpClient();
        using var listener = new TcpClient();
        await sender.ConnectAsync(Host, fromAgwPort, ct);
        await listener.ConnectAsync(Host, toAgwPort, ct);
        var send = new AgwFrameTransport(sender.GetStream());
        var hear = new AgwFrameTransport(listener.GetStream());
        await send.WriteFrameAsync(new AgwFrame(0, 'X', 0, fromCall, "", []), ct);
        await hear.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
        var heard = Task.Run(async () =>
        {
            while (true)
            {
                var frame = await hear.ReadFrameAsync(ct);
                if (Encoding.Latin1.GetString(frame.Payload).Contains($"Fm {fromCall} To READY", StringComparison.Ordinal)) return;
            }
        }, ct);
        while (!heard.IsCompleted)
        {
            await send.WriteFrameAsync(new AgwFrame((byte)RadioPortIndex, 'M', 0xF0, fromCall, "READY", "ready"u8.ToArray()), ct);
            await Task.WhenAny(heard, Task.Delay(TimeSpan.FromSeconds(3), ct));
        }
        await heard;
    }

    private async Task<WatchedContainer> StartBpqAsync(
        string nodeCall, string nodeAlias, string applCall, string applAlias, int agwPort, int kissPort, int telnetPort)
    {
        var bpq = new WatchedContainer($"BPQ {nodeCall}", new ContainerBuilder()
            .WithImage(BpqImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BpqConfig(nodeCall, nodeAlias, applCall, applAlias, agwPort, kissPort, telnetPort)), "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{NetSimId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(agwPort))
            .Build());
        await bpq.StartAsync();
        return bpq;
    }

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
         PACLEN={Radio.Paclen}{(Radio.AckMode ? "\n KISSOPTIONS=ACKMODE" : "")}
         INTERLOCK=0
         MHEARD=Y
         QUALITY=0
        ENDPORT

        """;
}

/// <summary>AFSK 1200 on Dire Wolf: the channel in Kevin M0AHN's analysed trace.</summary>
public sealed class NetSimAfsk1200Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "AFSK 1200 (Dire Wolf)";

    // PERSIST 64 with a 100 ms slot, as on a shared channel, though only
    // two stations use this one. With PERSIST 255 two nodes that start at
    // the same moment (both dialling, as the crossed-call scenario makes
    // them) collide, and BPQ then retries both SABMs a FRACK apart, in
    // step, until they retry out: 1 to 4 dials a round and up to 90 s.
    // With 64 and 100 most rounds took 1 or 2 dials and 9 to 29 s, on
    // both modems (see CrossedCallScenarioTests for the rest). It costs
    // the WPS scenario about 5 s at 1200 baud.
    //
    // FRACK longer than a burst plus the answer, as docs/tune.md says:
    // four full frames take about 4 s at 1200 baud, and BPQ times FRACK
    // from when it hands a burst to the TNC. With FRACK 3000 the WPS
    // scenario took 52 to 65 s, as BPQ polled into the far end's answer
    // and lost both; with 7000 (BPQ's default), 38 to 39 s (both with
    // PERSIST 255). A longer RESPTIME saved the far end's RR in the middle
    // of each burst, but BPQ also waits RESPTIME, at least 3 s, before
    // each REJ, which costs on a marginal link.
    //
    // Squelch open (the radio's default here), as 1200 baud packet
    // stations usually run: the TNC's own carrier detect decides when the
    // channel is busy. Dire Wolf's looks for AFSK, not audio, so the hiss
    // between transmissions doesn't hold it off: on an idle open-squelch
    // channel a frame starts 21 ms after it reaches the TNC, every time.
    // A squelch would also shut out a weak station the TNC can still copy.
    //
    // Dire Wolf rather than samoyed: on this image samoyed's transmit
    // audio reaches the simulator over UDP faster than it is read, and
    // everything after the first 2 s or so of a transmission is dropped.
    // Two full I-frames in one transmission lose the second every time;
    // Dire Wolf's transmit audio goes through a pipe and all four arrive.
    protected override string PortYaml => AfskPort;
    protected override BpqRadio DefaultRadio => new(Speed: 1200, TxDelayMs: 150, Paclen: 120, Maxframe: 4, FrackMs: 7000, RespTimeMs: 1000, Retries: 10, Persist: 64, SlotTimeMs: 100);
}

/// <summary>QPSK 3600 on pdn-soundmodem: 7200 bps in one FM channel, Kevin's "3K6" link.</summary>
public sealed class NetSimQpsk3600Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "QPSK 3600 (pdn-soundmodem)";
    protected override string PortYaml => QpskPort;
    // FRACK longer than a burst plus the answer: seven 236-byte frames
    // take about 2 s at 7200 bps. With FRACK 2000 (and RESPTIME 500) the
    // WPS scenario took 28 s and BPQ sent whole bursts twice; with 4000,
    // 19 to 25 s. PERSIST and SLOTTIME as at 1200 baud, for the same
    // reason.
    protected override BpqRadio DefaultRadio => new(Speed: 7200, TxDelayMs: 150, Paclen: 236, Maxframe: 7, FrackMs: 4000, RespTimeMs: 1000, Retries: 10, Persist: 64, SlotTimeMs: 100);
}

/// <summary>
/// AFSK 1200 with the channel's hiss on, for the soak: frames get lost
/// and retried as on a marginal path. Only started when the soak is asked
/// for (DAPPS_SOAK_MINUTES).
/// </summary>
public sealed class NetSimNoisyAfsk1200Fixture : NetSimTwoBpqFixture
{
    public override string ChannelName => "AFSK 1200 (Dire Wolf), noisy";

    // 156.5 dB each way, just above the FM threshold, with the squelch
    // open (a hard squelch would never open for a signal this weak).
    // Measured over KISS between two Dire Wolfs on this image, 100 of
    // each: 13% of 136-byte frames lost (a full I-frame at PACLEN 120) and
    // 1% of 22-byte ones (an RR is 15). 156.75 dB lost 18% and 1%; with 40
    // of each, 156 dB lost 5% and none, 157 dB 40% and none: the edge is
    // that steep. At 156.75 dB BPQ's links broke every minute or two.
    //
    // TXDELAY 300 rather than 150: at the edge a receiver that has just
    // stopped transmitting needs more preamble. A full frame sent straight
    // back after hearing the other end was lost 32% of the time with
    // 150 ms and 23% with 300 ms (156.75 dB, 40 each).
    protected override string PortYaml => AfskPort;
    protected override double DefaultPathLossDb => 156.5;
    protected override BpqRadio DefaultRadio => new(Speed: 1200, TxDelayMs: 300, Paclen: 120, Maxframe: 4, FrackMs: 7000, RespTimeMs: 1000, Retries: 10, Persist: 64, SlotTimeMs: 100);
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
