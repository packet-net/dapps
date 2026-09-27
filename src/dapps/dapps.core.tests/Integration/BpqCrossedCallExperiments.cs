using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// What BPQ does when two AX.25 calls between the same pair of callsigns
/// cross, found by driving its AGW interface by hand on the simulated radio
/// channel, with no DAPPS in the way. These are experiments, not checks:
/// each writes what every AGW socket and the air saw to scenario-reports/,
/// and asserts nothing about BPQ. They only run when
/// DAPPS_BPQ_EXPERIMENTS is set. The findings are in
/// docs-internal/end-to-end-tests.md.
///
/// Each node has what a DAPPS daemon has: a listener socket registered for
/// its application callsign (where inbound connects arrive), and a fresh
/// socket per outbound call.
/// </summary>
public abstract class BpqCrossedCallExperiments(NetSimTwoBpqFixture fixture) : IAsyncLifetime
{
    private static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_BPQ_EXPERIMENTS") is { Length: > 0 };

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> log = [];
    private readonly List<AgwProbe> sockets = [];
    private AirMonitor? air;

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in sockets) await s.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AnOutboundCall_ForAPairAlreadyConnectedInbound()
    {
        // The #194 sighting: A calls B, B's listener gets the connect, then
        // B calls A as well.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await WaitForAsync(() => listenerB.Saw('C'), ct);
        Note("B's listener has A's connect; now B calls A");
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(listenerB, "B listener -> A", ct, to: fixture.ApplCallA);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerB, ct);
        Write("inbound-then-outbound");
    }

    [Fact]
    public async Task TwoCalls_MadeAtTheSameMoment()
    {
        // The crossed call: both nodes have traffic and dial together.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);

        await Task.WhenAll(callerA.CallAsync(fixture.ApplCallB, ct), callerB.CallAsync(fixture.ApplCallA, ct));
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerA, ct);
        Write("simultaneous");
    }

    [Fact]
    public async Task ASecondCall_JustAfterTheFirstIsOnAir()
    {
        // As in the WPS scenario at 1200 baud: B dials a moment after A,
        // before A's call has reached B's listener.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        if (listenerB.Saw('C')) Note("B's listener already had A's connect before B dialled");
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerA, ct);
        Write("just-after");
    }

    private async Task<AgwProbe> OpenAsync(string name, int agwPort, string callsign, CancellationToken ct)
    {
        var probe = await AgwProbe.OpenAsync(name, fixture.Host, agwPort, callsign, (byte)fixture.RadioPortIndex, Note, ct);
        sockets.Add(probe);
        return probe;
    }

    private async Task SendAndWatchAsync(AgwProbe from, string label, CancellationToken ct, string? to = null)
    {
        Note($"sending '{label}' on {from.Name}");
        await from.SendAsync(label + "\r", ct, to);
        await Task.Delay(TimeSpan.FromSeconds(6), ct);
    }

    private async Task HangUpAndWatchAsync(AgwProbe who, CancellationToken ct)
    {
        Note($"{who.Name} sends 'd'");
        await who.HangUpAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, ct);
    }

    private void Note(string text)
    {
        lock (log) log.Add($"{clock.Elapsed.TotalSeconds,7:F2}  {text}");
    }

    private void Write(string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# BPQ experiment: {name}, {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine("What each AGW socket received, and what the test did, in order:");
        sb.AppendLine();
        sb.AppendLine("```");
        lock (log) foreach (var line in log) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("What went over the air:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(air!.Transcript());
        sb.AppendLine("```");
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"bpq-{name}-{fixture.ChannelName.Split(' ')[0].ToLowerInvariant()}.md"), sb.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(sb.ToString());
    }

    /// <summary>One AGW socket, registered for a callsign, logging every frame it gets.</summary>
    private sealed class AgwProbe : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly AgwFrameTransport agw;
        private readonly byte port;
        private readonly string callsign;
        private readonly Action<string> note;
        private readonly CancellationTokenSource stop = new();
        private readonly List<char> kinds = [];
        private string? peer;

        private AgwProbe(string name, TcpClient tcp, byte port, string callsign, Action<string> note)
        {
            Name = name;
            this.tcp = tcp;
            agw = new AgwFrameTransport(tcp.GetStream());
            this.port = port;
            this.callsign = callsign;
            this.note = note;
        }

        public string Name { get; }

        public bool Saw(char kind)
        {
            lock (kinds) return kinds.Contains(kind);
        }

        public static async Task<AgwProbe> OpenAsync(string name, string host, int agwPort, string callsign, byte port, Action<string> note, CancellationToken ct)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, agwPort, ct);
            var probe = new AgwProbe(name, tcp, port, callsign, note);
            await probe.agw.WriteFrameAsync(new AgwFrame(0, 'X', 0, callsign, "", []), ct);
            _ = probe.PumpAsync();
            return probe;
        }

        public async Task CallAsync(string to, CancellationToken ct)
        {
            peer = to;
            note($"{Name} asks for a connect to {to}");
            await agw.WriteFrameAsync(new AgwFrame(port, 'C', 0xF0, callsign, to, []), ct);
        }

        public async Task SendAsync(string text, CancellationToken ct, string? to = null) =>
            await agw.WriteDataAsync(port, callsign, to ?? peer!, Encoding.ASCII.GetBytes(text), ct);

        public async Task HangUpAsync(CancellationToken ct) =>
            await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, callsign, peer!, []), ct);

        private async Task PumpAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    lock (kinds) kinds.Add(frame.Kind);
                    if (frame.Kind is 'G' or 'R') continue;
                    var text = Encoding.Latin1.GetString(frame.Payload).Replace("\r", "|").Replace("\n", "|").Replace("\0", "");
                    note($"{Name} got '{frame.Kind}' port {frame.Port} {frame.CallFrom}->{frame.CallTo}: {text}");
                }
            }
            catch
            {
                note($"{Name}: socket closed");
            }
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

[Collection("net-sim AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class BpqCrossedCallExperimentsAfsk1200(NetSimAfsk1200Fixture fixture) : BpqCrossedCallExperiments(fixture);

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class BpqCrossedCallExperimentsQpsk3600(NetSimQpsk3600Fixture fixture) : BpqCrossedCallExperiments(fixture);
