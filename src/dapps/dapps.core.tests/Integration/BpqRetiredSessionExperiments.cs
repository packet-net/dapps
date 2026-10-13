using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// Exploratory: what BPQ does with a session DAPPS retires (#213).
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class BpqRetiredSessionExperiments(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_BPQ_EXPERIMENTS") is { Length: > 0 };

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> log = [];
    private readonly List<Probe> sockets = [];
    private AirMonitor? air;

    private byte Port => (byte)fixture.AxipPortIndex;

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Wanted) return;
        foreach (var s in sockets) await s.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        await Task.Delay(3000);
    }

    [Fact]
    public async Task X1_SharedLink_ThenTheLinkEnds_ThenThePeerCallsAgain()
    {
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerA = await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await WaitForAsync(() => listenerB.Saw('C'), ct);
        Note("B's listener has A's call and holds its prompt; B calls A");
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await WaitForAsync(() => callerB.Saw('C') || callerB.Saw('d'), ct);
        await Task.Delay(1000, ct);
        Note("B retires its listener session (sends nothing). Callers talk.");
        await SendAndWatchAsync(callerB, "exchange from B caller", ct);
        await SendAndWatchAsync(callerA, "exchange from A caller", ct);
        Note("A's caller hangs up");
        await callerA.HangUpAsync(ct);
        await Task.Delay(2000, ct);
        Note("B's caller closes its socket (no 'd': it had one)");
        await callerB.AbandonAsync();
        await Task.Delay(2000, ct);
        Note("A calls B again");
        var callerA2 = await OpenAsync("A caller 2", fixture.AgwPortA, fixture.ApplCallA, ct);
        await callerA2.CallAsync(fixture.ApplCallB, ct);
        await Task.Delay(3000, ct);
        await SendAndWatchAsync(listenerB, "prompt from B listener", ct, to: fixture.ApplCallA);
        await SendAndWatchAsync(callerA2, "exchange from A caller 2", ct);
        await callerA2.HangUpAsync(ct);
        await Task.Delay(2000, ct);
        Write("x1");
    }

    [Fact]
    public async Task X2_AnOutboundSessionRetired_WhenThePeersNewCallReachesTheListener()
    {
        // As in the soaks: B's call to A is up, A's node has lost the link
        // (here: A's listener hung up while B's caller was... ) and A calls B.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerA = await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);

        await callerB.CallAsync(fixture.ApplCallA, ct);
        await WaitForAsync(() => listenerA.Saw('C'), ct);
        await SendAndWatchAsync(listenerA, "prompt from A listener", ct, to: fixture.ApplCallB);
        await SendAndWatchAsync(callerB, "exchange from B caller", ct);
        Note("A calls B over the live link");
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);
        await callerA.CallAsync(fixture.ApplCallB, ct);
        await Task.Delay(3000, ct);
        Note("B retires its outbound session: closes its socket without a 'd'");
        await callerB.AbandonAsync();
        await Task.Delay(3000, ct);
        await SendAndWatchAsync(callerA, "exchange from A caller", ct);
        await SendAndWatchAsync(listenerB, "prompt from B listener", ct, to: fixture.ApplCallA);
        await callerA.HangUpAsync(ct);
        await Task.Delay(2000, ct);
        Write("x2");
    }

    [Fact]
    public async Task X3_ASharedLink_WhereTheCallerThatTookItIsRetired()
    {
        // #205 at B, then the far end's next call retires B's caller.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerA = await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await WaitForAsync(() => listenerB.Saw('C'), ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await WaitForAsync(() => callerB.Saw('C') || callerB.Saw('d'), ct);
        await Task.Delay(1000, ct);
        await SendAndWatchAsync(callerB, "exchange from B caller", ct);
        await SendAndWatchAsync(callerA, "exchange from A caller", ct);
        Note("A's caller closes without a 'd' (its node keeps the link), and A calls B again");
        await callerA.AbandonAsync();
        await Task.Delay(2000, ct);
        var callerA2 = await OpenAsync("A caller 2", fixture.AgwPortA, fixture.ApplCallA, ct);
        await callerA2.CallAsync(fixture.ApplCallB, ct);
        await Task.Delay(3000, ct);
        Note("B retires its caller: closes its socket without a 'd'");
        await callerB.AbandonAsync();
        await Task.Delay(3000, ct);
        await SendAndWatchAsync(listenerB, "prompt from B listener", ct, to: fixture.ApplCallA);
        await SendAndWatchAsync(callerA2, "exchange from A caller 2", ct);
        await callerA2.HangUpAsync(ct);
        await Task.Delay(2000, ct);
        Write("x3");
    }

    private async Task<Probe> OpenAsync(string name, int agwPort, string callsign, CancellationToken ct)
    {
        var probe = await Probe.OpenAsync(name, fixture.Host, agwPort, callsign, Port, Note, ct);
        sockets.Add(probe);
        return probe;
    }

    private async Task SendAndWatchAsync(Probe from, string label, CancellationToken ct, string? to = null)
    {
        Note($"sending '{label}' on {from.Name}");
        await from.SendAsync(label + "\r", ct, to);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
    }

    private void Note(string text)
    {
        lock (log) log.Add($"{clock.Elapsed.TotalSeconds,7:F2}  {text}");
    }

    private void Write(string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# BPQ experiment: {name}, {fixture.ChannelName}");
        sb.AppendLine("```");
        lock (log) foreach (var line in log) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine("```");
        sb.AppendLine(air!.Transcript());
        sb.AppendLine("```");
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"bpq-retired-{name}.md"), sb.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(sb.ToString());
    }

    private sealed class Probe : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly AgwFrameTransport agw;
        private readonly byte port;
        private readonly string callsign;
        private readonly Action<string> note;
        private readonly CancellationTokenSource stop = new();
        private readonly List<char> kinds = [];
        private string? peer;
        private bool closed;

        private Probe(string name, TcpClient tcp, byte port, string callsign, Action<string> note)
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

        public static async Task<Probe> OpenAsync(string name, string host, int agwPort, string callsign, byte port, Action<string> note, CancellationToken ct)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, agwPort, ct);
            var probe = new Probe(name, tcp, port, callsign, note);
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

        public async Task HangUpAsync(CancellationToken ct)
        {
            note($"{Name} sends 'd'");
            await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, callsign, peer!, []), ct);
        }

        /// <summary>Closes the socket without a 'd', as DAPPS does with a retired session.</summary>
        public ValueTask AbandonAsync()
        {
            note($"{Name} closes its socket without a 'd'");
            return DisposeAsync();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    lock (kinds) kinds.Add(frame.Kind);
                    if (frame.Kind is 'G' or 'R' or 'X') continue;
                    var text = Encoding.Latin1.GetString(frame.Payload).Replace("\r", "|").Replace("\n", "|").Replace("\0", "");
                    note($"{Name} got '{frame.Kind}' port {frame.Port} {frame.CallFrom}->{frame.CallTo}: {text}");
                }
            }
            catch
            {
                if (!closed) note($"{Name}: socket closed");
            }
        }

        public ValueTask DisposeAsync()
        {
            closed = true;
            stop.Cancel();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
