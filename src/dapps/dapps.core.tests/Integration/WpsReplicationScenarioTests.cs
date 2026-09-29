using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Kevin M0AHN's WPS replication trace from #187, replayed between two real
/// DAPPS daemons over a simulated radio channel (<see cref="NetSimTwoNodeFixture"/>, BPQ or pdn),
/// with the same timing: DPSTST posts four times about half a second apart,
/// MB7NPW posts four times starting four seconds later, and each side's WPS
/// acknowledges cumulatively a few seconds after the first post it hasn't
/// acked. Before 0.40.0 that exchange took 77 s, six connections and about
/// 150 frames at 1200 baud.
///
/// The test checks every message arrives exactly once (reading both inboxes
/// until neither daemon has anything left to send, so a late re-offer
/// counts), and bounds connections and frames; it writes a report with the
/// real on-air timings
/// (net-sim runs in real time) to scenario-reports/ beside the test build,
/// which CI keeps as an artifact.
/// </summary>
public abstract class WpsReplicationScenarioTests(NetSimTwoNodeFixture fixture) : IAsyncLifetime
{
    private const string App = "wps-repl";
    private static readonly TimeSpan AckDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    // Offsets of each post from the trace's s= timestamps.
    private static readonly double[] DpststPosts = [0, 0.556, 1.134, 1.692];
    private static readonly double[] Mb7npwPosts = [4.100, 4.484, 4.888, 5.585];

    private readonly List<IAsyncDisposable> running = [];
    private IAirMonitor? air;
    private ChannelLog? channel;

    public async ValueTask InitializeAsync()
    {
        air = await fixture.StartAirMonitorAsync(TestContext.Current.CancellationToken);
        channel = await fixture.StartChannelLogAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        if (channel is not null) await channel.DisposeAsync();
        await Task.Delay(3000);
        // A node that died is replaced for the next test.
        if (fixture.Died is not null) await fixture.WaitUntilReadyAsync();
    }

    [Fact]
    public async Task KevinsTrace_Replayed_EveryMessageArrivesOnce_OnFewConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartNodeAsync("dpststA", fixture.ApplCallA, fixture.NodeA, fixture.ApplCallB, ct);
        var b = await StartNodeAsync("mb7npwB", fixture.ApplCallB, fixture.NodeB, fixture.ApplCallA, ct);
        var clock = new Stopwatch();
        var sideA = new WpsSide(a, b.Callsign, clock, origin: "DPSTST", author: "G5ALF", firstSeq: 21);
        var sideB = new WpsSide(b, a.Callsign, clock, origin: "MB7NPW", author: "M0AHN", firstSeq: 535);
        sideA.Peer = sideB;
        sideB.Peer = sideA;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = DateTime.UtcNow;
        clock.Start();
        var pumps = new[] { sideA.PumpAsync(stop.Token), sideB.PumpAsync(stop.Token) };
        var posting = Task.WhenAll(sideA.PostAtAsync(DpststPosts, ct), sideB.PostAtAsync(Mb7npwPosts, ct));

        // Done when each side holds all four of the other's posts and an
        // ack from the other side covering its own last post. Then keep
        // reading until neither daemon has anything left to send: a
        // message re-offered after a lost ack would arrive then.
        var elapsed = Deadline;
        Exception? failure = null;
        try
        {
            while (clock.Elapsed < Deadline && !(sideA.Complete && sideB.Complete) && !pumps.Any(p => p.IsFaulted) && fixture.Died is null)
            {
                await Task.Delay(200, ct);
            }
            elapsed = clock.Elapsed;
            await posting;
            if (sideA.Complete && sideB.Complete) await SettleAsync([a, b], ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failure = e;
        }
        await stop.CancelAsync();
        foreach (var pump in pumps)
        {
            try { await pump; }
            catch (OperationCanceledException) { }
            catch (Exception e) { failure ??= e; }
        }

        var report = Report(sideA, sideB, elapsed, channel!.Summarise(started, started + elapsed), channel!.Timeline(started, started + elapsed));
        WriteReport(report);

        await fixture.ThrowIfDiedAsync($"{report}\n{Diagnostics()}");
        failure.Should().BeNull($"the test's own posting and inbox reading should work\n{report}\n{Diagnostics()}");
        sideA.Complete.Should().BeTrue($"everything should arrive within {Deadline.TotalMinutes:F0} minutes\n{report}\n{Diagnostics()}");
        sideB.Complete.Should().BeTrue($"{report}\n{Diagnostics()}");
        (sideA.Duplicates + sideB.Duplicates).Should().Be(0, "every message should arrive exactly once\n" + report);
        (Connects("A") + Connects("B")).Should().BeLessThanOrEqualTo(MaxConnections,
            $"Kevin's trace took six connections; the exchange needs {MaxConnections - 1}, {MaxConnections} if a SABM or UA is lost\n" + report);
        var messages = sideA.PostsReceived + sideB.PostsReceived + sideA.AcksReceived + sideB.AcksReceived;
        (Frames("A") + Frames("B")).Should().BeLessThanOrEqualTo(FramesPerMessage * messages,
            $"Kevin's trace took about 14 frames a message; the exchange takes {FramesPerMessage - 2} or fewer here\n" + report);
        elapsed.Should().BeLessThanOrEqualTo(TimeLimit,
            $"Kevin's trace took 77 s at 1200 baud; the exchange takes about {TimeLimit.TotalSeconds / 2:F0} s here\n" + report);
    }

    /// <summary>Most frames a message may take, with room for a slow
    /// runner (about two more than the exchange takes on this channel).</summary>
    protected abstract int FramesPerMessage { get; }

    /// <summary>Most connections (SABMs) the exchange may take: one, and
    /// one more if a SABM or UA is lost.</summary>
    protected virtual int MaxConnections => 2;

    /// <summary>Longest the exchange may take, first post to last ack:
    /// about twice what it takes on this channel.</summary>
    protected abstract TimeSpan TimeLimit { get; }

    /// <summary>Wait (up to a minute) until neither daemon has anything left to send, then a few seconds more.</summary>
    private async Task SettleAsync(DappsDaemon[] nodes, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < until && fixture.Died is null)
        {
            var pending = 0;
            foreach (var n in nodes) pending += await n.PendingOutboundAsync(ct);
            if (pending == 0) break;
            await Task.Delay(500, ct);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }

    private async Task<DappsDaemon> StartNodeAsync(string name, string callsign, NodeAttachment on, string neighbour, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, on, [new(neighbour, on.BearerPort)], settings: null, ct);
        running.Add(node);
        return node;
    }

    private int Connects(string side) =>
        air!.CountSentBy(side, $"Fm {Call(side)} To {Call(side == "A" ? "B" : "A")} <C C");

    private string Call(string side) => side == "A" ? fixture.ApplCallA : fixture.ApplCallB;

    /// <summary>Frames between the two DAPPS callsigns, either way.</summary>
    private int Frames(string side, string kind = "") =>
        air!.SentBy(side).Count(f => f.Contains($"Fm {Call(side)} To {Call(side == "A" ? "B" : "A")} <{kind}", StringComparison.Ordinal));

    private string Report(WpsSide a, WpsSide b, TimeSpan elapsed, ChannelLog.Summary onAir, string timeline)
    {
        var latencies = a.Latencies.Concat(b.Latencies).Order().ToList();
        var gaps = onAir.Gaps(within: TimeSpan.FromSeconds(3));
        var sb = new StringBuilder();
        sb.AppendLine($"# WPS replication scenario: {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine("Kevin M0AHN's #187 trace replayed between two DAPPS daemons on a simulated channel.");
        sb.AppendLine("Each side acks 5 s after the first post it hasn't acked, so the number of acks depends on how fast posts arrive.");
        sb.AppendLine();
        sb.AppendLine($"Radio port: {fixture.RadioSettings}");
        sb.AppendLine();
        sb.AppendLine("| | Kevin's trace, before 0.40.0 (1200 baud) | This run |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Messages, about 200 bytes each | 11 | {a.PostsReceived + b.PostsReceived + a.AcksReceived + b.AcksReceived} ({a.PostsReceived + b.PostsReceived} posts, {a.AcksReceived + b.AcksReceived} acks) |");
        sb.AppendLine($"| Time, first post to last ack | 77 s | {elapsed.TotalSeconds:F1} s |");
        sb.AppendLine($"| Connections | 6 | {Connects("A") + Connects("B")} |");
        sb.AppendLine($"| Frames between the two stations | about 150 | {Frames("A") + Frames("B")} |");
        sb.AppendLine($"| Of which I-frames | | {Frames("A", "I ") + Frames("B", "I ")} |");
        sb.AppendLine($"| Transmissions (key-ups) | | {onAir.Count} (A {onAir.CountBy("A")}, B {onAir.CountBy("B")}) |");
        sb.AppendLine($"| Channel in use | | {onAir.Busy.TotalSeconds:F1} s ({onAir.BusyPercent:F0}%) |");
        sb.AppendLine($"| Quiet gap between transmissions: median | | {Percentile([.. gaps], 0.5):F2} s |");
        sb.AppendLine($"| Quiet gaps in total | | {gaps.Sum():F1} s |");
        sb.AppendLine($"| Both sides transmitting at once | | {onAir.Doubles} |");
        sb.AppendLine($"| Post, submitted to delivered: median | | {Percentile(latencies, 0.5):F1} s |");
        sb.AppendLine($"| Post, submitted to delivered: worst | | {(latencies.Count > 0 ? latencies[^1] : double.NaN):F1} s |");
        sb.AppendLine();
        sb.AppendLine("<details><summary>What went over the air</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(Printable(air!.Transcript()));
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        sb.AppendLine();
        sb.AppendLine("<details><summary>Every transmission, from the first post</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.Append(timeline);
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    /// <summary>The transcript as plain ASCII: compressed payloads show as dots.</summary>
    private static string Printable(string text) =>
        new([.. text.Select(c => c == '\n' || (c >= ' ' && c <= '~') ? c : '.')]);

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];

    private void WriteReport(string report)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wps-" + fixture.ReportTag + ".md"), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
    }

    private string Diagnostics() => string.Join("\n", running.OfType<DappsDaemon>().Select(d => d.Tail(60)));

    /// <summary>
    /// One WPS instance's replication, as far as DAPPS sees it: posts go
    /// out as short-key envelopes on the <c>wps-repl</c> app, about 200
    /// bytes like Kevin's; posts that arrive are acked cumulatively
    /// <see cref="AckDelay"/> after the first unacked one, with the highest
    /// seq seen. A duplicate is a post arriving twice, or any message
    /// coming back after this side had acked it from the inbox.
    /// </summary>
    private sealed class WpsSide(DappsDaemon node, string peer, Stopwatch clock, string origin, string author, int firstSeq)
    {
        private readonly HashSet<string> seenIds = [];
        private readonly HashSet<string> ackedIds = [];
        private readonly HashSet<int> seenPosts = [];
        private readonly Dictionary<int, TimeSpan> submittedAt = [];
        private int highestReceived = -1;
        private TimeSpan? firstUnacked;

        public WpsSide? Peer { get; set; }
        public string Origin => origin;
        public int PostsReceived { get; private set; }
        public int AcksReceived { get; private set; }
        public int AcksSent { get; private set; }
        public int HighestAckReceived { get; private set; } = -1;
        public int Duplicates { get; private set; }
        public List<double> Latencies { get; } = [];

        /// <summary>All four of the other side's posts here, and its ack for our last post.</summary>
        public bool Complete => PostsReceived == 4 && HighestAckReceived >= firstSeq + 3;

        public async Task PostAtAsync(double[] offsets, CancellationToken ct)
        {
            for (var i = 0; i < offsets.Length; i++)
            {
                var wait = TimeSpan.FromSeconds(offsets[i]) - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                var seq = firstSeq + i;
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var text = $"Post {i + 1} from {author}: replication test, the quick brown fox jumps over the lazy dog";
                var post = $$$"""{"v":1,"o":"{{{origin}}}","s":{{{seq}}},"e":1,"ts":{{{ts}}},"a":"p.i","data":{"t":"cp","cid":1,"fc":"{{{author}}}","ts":{{{ts - 60}}},"p":"{{{text}}}","dts":{{{ts}}}}}""";
                lock (submittedAt) submittedAt[seq] = clock.Elapsed;
                await node.SubmitAsync(App, peer, Encoding.UTF8.GetBytes(post), ct);
            }
        }

        private void Take(DappsDaemon.Inbound m)
        {
            var json = JsonDocument.Parse(m.Payload).RootElement;
            var seq = json.GetProperty("s").GetInt32();
            if (json.GetProperty("a").GetString() == "ack")
            {
                AcksReceived++;
                HighestAckReceived = Math.Max(HighestAckReceived, seq);
                return;
            }
            if (!seenPosts.Add(seq))
            {
                Duplicates++;
                return;
            }
            PostsReceived++;
            highestReceived = Math.Max(highestReceived, seq);
            if (Peer!.SubmittedAt(seq) is { } sent) Latencies.Add((clock.Elapsed - sent).TotalSeconds);
            firstUnacked ??= clock.Elapsed;
        }

        private TimeSpan? SubmittedAt(int seq)
        {
            lock (submittedAt) return submittedAt.TryGetValue(seq, out var at) ? at : null;
        }

        /// <summary>Poll this node's inbox as WPS's inbox pump does, and ack as it does.</summary>
        public async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var m in await node.InboundAsync(App, ct))
                {
                    if (seenIds.Add(m.Id)) Take(m);
                    else if (ackedIds.Contains(m.Id)) Duplicates++;
                    (await node.Http.PostAsync($"AppApi/inbound/{App}/{m.Id}/ack", null, ct)).EnsureSuccessStatusCode();
                    ackedIds.Add(m.Id);
                }

                if (firstUnacked is { } since && clock.Elapsed - since >= AckDelay)
                {
                    var ack = $$"""{"a":"ack","o":"{{Peer!.Origin}}","s":{{highestReceived}},"by":"{{node.Callsign}}"}""";
                    await node.SubmitAsync(App, peer, Encoding.UTF8.GetBytes(ack), ct);
                    AcksSent++;
                    firstUnacked = null;
                }
                await Task.Delay(100, ct);
            }
        }
    }
}

[Collection("net-sim AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class WpsReplicationScenarioAfsk1200Tests(NetSimAfsk1200Fixture fixture) : WpsReplicationScenarioTests(fixture)
{
    protected override int FramesPerMessage => 7;
    protected override TimeSpan TimeLimit => TimeSpan.FromSeconds(75);
}

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class WpsReplicationScenarioQpsk3600Tests(NetSimQpsk3600Fixture fixture) : WpsReplicationScenarioTests(fixture)
{
    protected override int FramesPerMessage => 6;
    protected override TimeSpan TimeLimit => TimeSpan.FromSeconds(45);
}

// On pdn both nodes dial, every run: pdn sends an XID and waits for the
// answer before its SABME, so A's call isn't up at B until after B's first
// post, 4 s in, and B dials too. pdn makes one link of the two calls, but
// over RHPv2 neither DAPPS daemon can tell (no monitor), so each waits
// 10 s for a prompt before sending its exchange. That's two connections
// before anything is lost, and about 10 s of the time.
[Collection("net-sim pdn AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class WpsReplicationScenarioPdnAfsk1200Tests(NetSimPdnAfsk1200Fixture fixture) : WpsReplicationScenarioTests(fixture)
{
    protected override int FramesPerMessage => 7;
    protected override int MaxConnections => 3;
    protected override TimeSpan TimeLimit => TimeSpan.FromSeconds(75);
}

[Collection("net-sim pdn QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class WpsReplicationScenarioPdnQpsk3600Tests(NetSimPdnQpsk3600Fixture fixture) : WpsReplicationScenarioTests(fixture)
{
    protected override int FramesPerMessage => 6;
    protected override int MaxConnections => 3;
    protected override TimeSpan TimeLimit => TimeSpan.FromSeconds(45);
}
