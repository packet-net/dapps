using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// A long run on a marginal channel: two DAPPS daemons trade random
/// bursts both ways over AFSK 1200 with path loss and noise, so frames
/// are lost and retried, with now and then a long message of a few KB.
/// A third of the way in, the simulator stops for a minute (both modems
/// off, BPQ loses its KISS link); two thirds in, B's daemon is restarted
/// as for an upgrade. Then traffic stops and the test waits for the
/// queues to drain.
///
/// Every message must arrive exactly once with its bytes intact. The
/// report (scenario-reports/soak.md, with both daemons' logs and the air
/// transcript beside it) gives delivery times, what the channel did, and
/// how long each disruption took to recover from.
///
/// Only runs when DAPPS_SOAK_MINUTES is set (the traffic phase, in
/// minutes); DAPPS_SOAK_SEED picks the traffic (default 187). Not part of
/// CI: e.g. <c>DAPPS_SOAK_MINUTES=30 ./dapps.core.tests --filter-class
/// dapps.core.tests.Integration.NetSimSoakTests</c>.
/// </summary>
[Collection("net-sim noisy AFSK 1200")]
[Trait("Category", "Soak")]
public sealed class NetSimSoakTests(NetSimNoisyAfsk1200Fixture fixture) : IAsyncLifetime
{
    private const string App = "soak";
    private static readonly TimeSpan Outage = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromMinutes(20);

    private readonly List<DappsDaemon> running = [];
    private readonly Stopwatch clock = new();
    private readonly Dictionary<string, Sent> sent = [];
    private readonly Dictionary<string, TimeSpan> delivered = [];
    private readonly List<(TimeSpan At, string What)> events = [];
    private readonly HashSet<string> seenIds = [];
    private readonly HashSet<string> ackedIds = [];
    private readonly HashSet<string> resubmitted = [];
    private readonly HashSet<string> abandoned = [];
    private readonly List<string> duplicateNotes = [];
    private int duplicates;
    private int resubmittedTwice;
    private int corrupt;
    private AirMonitor? air;
    private ChannelLog? channel;
    private StreamWriter? progress;

    private sealed record Sent(string From, byte[] Payload, TimeSpan At, bool Long);

    public async ValueTask InitializeAsync()
    {
        if (!SoakSettings.Requested) return;
        var ct = TestContext.Current.CancellationToken;
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, ct);
        channel = await fixture.StartChannelLogAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        if (channel is not null) await channel.DisposeAsync();
        progress?.Dispose();
    }

    [Fact]
    public async Task RandomTrafficBothWays_ThroughAnOutageAndARestart_ArrivesExactlyOnce()
    {
        Assert.SkipUnless(SoakSettings.Requested, "Set DAPPS_SOAK_MINUTES to run the soak.");
        var ct = TestContext.Current.CancellationToken;
        var minutes = SoakSettings.Minutes!.Value;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("DAPPS_SOAK_SEED"), out var s) ? s : 187;
        var traffic = TimeSpan.FromMinutes(minutes);
        var reports = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(reports);
        progress = new StreamWriter(Path.Combine(reports, "soak-progress.log")) { AutoFlush = true };

        var a = await StartNodeAsync("soakA", fixture.ApplCallA, fixture.AgwPortA, fixture.ApplCallB, ct);
        var b = await StartNodeAsync("soakB", fixture.ApplCallB, fixture.AgwPortB, fixture.ApplCallA, ct);
        var started = DateTime.UtcNow;
        clock.Start();
        Note("traffic starts");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new[] { PumpAsync(a, stop.Token), PumpAsync(b, stop.Token) };
        var reporter = ProgressAsync(stop.Token);
        Exception? failure = null;
        try
        {
            await Task.WhenAll(
                SendAsync("A", a, b.Callsign, new Random(seed), traffic, ct),
                SendAsync("B", b, a.Callsign, new Random(seed + 1), traffic, ct),
                DisruptAsync(b, traffic, ct));
            Note("traffic stops; draining");
            var drainDeadline = clock.Elapsed + DrainLimit;
            while (clock.Elapsed < drainDeadline && Missing().Count > 0) await Task.Delay(1000, ct);
            Note(Missing().Count == 0 ? "all delivered" : $"gave up with {Missing().Count} undelivered");

            // Keep reading until neither daemon has anything left to send:
            // a message re-offered after a lost ack would arrive then.
            while (clock.Elapsed < drainDeadline && await PendingAsync(ct) > 0) await Task.Delay(1000, ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            Note("queues empty");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failure = e;
            Note($"stopped by an error: {e.GetType().Name}: {e.Message}");
        }
        var elapsed = clock.Elapsed;
        await stop.CancelAsync();
        try { await Task.WhenAll([.. pumps, reporter]); } catch (OperationCanceledException) { }

        var report = Report(seed, traffic, elapsed, channel!.Summarise(started, started + elapsed));
        await File.WriteAllTextAsync(Path.Combine(reports, "soak.md"), report, ct);
        await File.WriteAllTextAsync(Path.Combine(reports, "soak-air.txt"), air!.Transcript(), ct);
        foreach (var d in running) await File.WriteAllTextAsync(Path.Combine(reports, $"soak-{d.Name}.log"), d.Log, ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report);

        failure.Should().BeNull($"the soak's own sending, disruptions and reading should work\n{report}");
        Missing().Should().BeEmpty($"every message should arrive\n{report}");
        duplicates.Should().Be(0, $"no message should arrive twice\n{report}");
        corrupt.Should().Be(0, $"every message should arrive intact\n{report}");
    }

    private async Task<int> PendingAsync(CancellationToken ct)
    {
        var pending = 0;
        foreach (var d in running)
        {
            try { pending += await d.PendingOutboundAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { pending++; }
        }
        return pending;
    }

    private async Task<DappsDaemon> StartNodeAsync(string name, string callsign, int agwPort, string neighbour, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.RadioPortIndex,
            [new(neighbour, fixture.RadioPortIndex)], settings: null, ct);
        running.Add(node);
        return node;
    }

    /// <summary>
    /// Bursts of one to four messages, a random 5 to 120 s apart (40 s on
    /// average). Most are 150 to 400 bytes; one in twelve is 2 to 5 KB.
    /// A submit that fails (its daemon restarting) is retried, as an app would.
    /// </summary>
    private async Task SendAsync(string side, DappsDaemon node, string peer, Random random, TimeSpan until, CancellationToken ct)
    {
        var n = 0;
        while (clock.Elapsed < until)
        {
            var wait = Math.Clamp(-Math.Log(1 - random.NextDouble()) * 40, 5, 120);
            await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            if (clock.Elapsed >= until) break;
            var burst = random.Next(1, 5);
            for (var i = 0; i < burst; i++)
            {
                var isLong = random.Next(12) == 0;
                var tag = $"{side}-{++n:D5}";
                var payload = Payload(tag, isLong ? random.Next(2000, 5001) : random.Next(150, 401), random);
                while (true)
                {
                    try
                    {
                        lock (sent) sent[tag] = new Sent(side, payload, clock.Elapsed, isLong);
                        await node.SubmitAsync(App, peer, payload, ct, ttl: 3600);
                        break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        // Refused (InvalidOperationException) means not queued.
                        // No answer at all means it may have been, so sending
                        // again may deliver it twice, which is then the app's doing.
                        lock (sent)
                        {
                            if (e is not InvalidOperationException) resubmitted.Add(tag);
                            if (clock.Elapsed > until + TimeSpan.FromMinutes(2))
                            {
                                // Its daemon isn't coming back; stop trying.
                                abandoned.Add(tag);
                                break;
                            }
                        }
                        await Task.Delay(2000, ct);
                    }
                }
            }
        }
    }

    private static readonly string[] Words =
        ["net", "check", "tonight", "repeater", "packet", "node", "weather", "rain", "wind", "north", "south", "meeting",
         "club", "portable", "battery", "antenna", "signal", "report", "copy", "thanks", "tomorrow", "morning", "73"];

    /// <summary>Text a little like chat or a bulletin: tagged, then words to length.</summary>
    private static byte[] Payload(string tag, int length, Random random)
    {
        var sb = new StringBuilder($"{{\"tag\":\"{tag}\",\"text\":\"");
        while (sb.Length < length - 2) sb.Append(Words[random.Next(Words.Length)]).Append(' ');
        sb.Length = length - 2;
        sb.Append("\"}");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Drain a node's inbox as an app would, checking each message against
    /// what was sent. A message still there because our ack didn't land
    /// (its daemon restarting) is acked again, not counted twice; one that
    /// comes back after its ack landed is a duplicate.
    /// </summary>
    private async Task PumpAsync(DappsDaemon node, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var m in await node.InboundAsync(App, ct))
                {
                    var text = Encoding.ASCII.GetString(m.Payload);
                    var tag = text.Length > 17 ? text[8..15] : "";
                    lock (sent)
                    {
                        if (seenIds.Add(m.Id))
                        {
                            if (!sent.TryGetValue(tag, out var original) || !original.Payload.AsSpan().SequenceEqual(m.Payload)) corrupt++;
                            else if (!delivered.TryAdd(tag, clock.Elapsed))
                            {
                                if (resubmitted.Contains(tag)) resubmittedTwice++;
                                else { duplicates++; duplicateNotes.Add($"{tag} again as {m.Id} at {clock.Elapsed:hh\\:mm\\:ss}"); }
                            }
                        }
                        else if (ackedIds.Contains(m.Id))
                        {
                            duplicates++;
                            duplicateNotes.Add($"{tag} ({m.Id}) again after the app had it, at {clock.Elapsed:hh\\:mm\\:ss}");
                        }
                    }
                    var ack = await node.Http.PostAsync($"AppApi/inbound/{App}/{m.Id}/ack", null, ct);
                    if (ack.IsSuccessStatusCode) lock (sent) ackedIds.Add(m.Id);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Daemon restarting (refused, cut off mid-reply); try again.
            }
            await Task.Delay(250, ct);
        }
    }

    private async Task DisruptAsync(DappsDaemon b, TimeSpan traffic, CancellationToken ct)
    {
        await Task.Delay(traffic / 3, ct);
        Note($"channel outage: net-sim stopped for {Outage.TotalSeconds:F0} s");
        await fixture.ChannelOutageAsync(Outage, ct, channel);
        Note("channel back");

        var wait = traffic * 2 / 3 - clock.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        Note("restarting B's daemon");
        var killed = await b.RestartAsync(ct);
        Note(killed ? "B's daemon back (it didn't stop within 15 s and was killed)" : "B's daemon back");
    }

    private async Task ProgressAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            int total, done;
            lock (sent) (total, done) = (sent.Count, delivered.Count);
            progress!.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} sent {total}, delivered {done}, duplicates {duplicates}, corrupt {corrupt}");
        }
    }

    private void Note(string what)
    {
        lock (events) events.Add((clock.Elapsed, what));
        progress?.WriteLine($"{clock.Elapsed:hh\\:mm\\:ss} {what}");
    }

    private List<string> Missing()
    {
        lock (sent) return [.. sent.Keys.Where(k => !delivered.ContainsKey(k) && !abandoned.Contains(k)).Order()];
    }

    private string Call(string side) => side == "A" ? fixture.ApplCallA : fixture.ApplCallB;

    private int Frames(string side, string kind = "") =>
        air!.SentBy(side).Count(f => f.Contains($"Fm {Call(side)} To {Call(side == "A" ? "B" : "A")} <{kind}", StringComparison.Ordinal));

    private string Report(int seed, TimeSpan traffic, TimeSpan elapsed, ChannelLog.Summary onAir)
    {
        List<(Sent Sent, double Latency)> done;
        lock (sent) done = [.. delivered.Select(d => (sent[d.Key], (d.Value - sent[d.Key].At).TotalSeconds))];
        var shortLatency = done.Where(d => !d.Sent.Long).Select(d => d.Latency).Order().ToList();
        var longLatency = done.Where(d => d.Sent.Long).Select(d => d.Latency).Order().ToList();
        List<Sent> all;
        lock (sent) all = [.. sent.Values];

        var sb = new StringBuilder();
        sb.AppendLine($"# DAPPS soak: {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine($"{traffic.TotalMinutes:F0} minutes of random traffic both ways (seed {seed}), then a wait for the queues to drain.");
        sb.AppendLine($"A third of the way in net-sim stops for {Outage.TotalSeconds:F0} s; two thirds in, B's daemon restarts.");
        sb.AppendLine();
        sb.AppendLine($"BPQ radio port: {fixture.RadioSettings}");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Messages sent | {all.Count} (A {all.Count(x => x.From == "A")}, B {all.Count(x => x.From == "B")}; {all.Count(x => x.Long)} long) |");
        sb.AppendLine($"| Bytes sent | {all.Sum(x => x.Payload.Length):N0} |");
        sb.AppendLine($"| Delivered | {done.Count} |");
        sb.AppendLine($"| Undelivered | {Missing().Count} {string.Join(' ', Missing().Take(20))} |");
        sb.AppendLine($"| Duplicates | {duplicates} {string.Join("; ", duplicateNotes)} |");
        sb.AppendLine($"| Sent twice by the test (a submit got no answer), arrived twice | {resubmittedTwice} of {resubmitted.Count} |");
        sb.AppendLine($"| Given up by the test (its daemon never answered) | {abandoned.Count} |");
        sb.AppendLine($"| Corrupt or unknown | {corrupt} |");
        sb.AppendLine($"| Delivery time, short messages: median, 95th percentile, worst | {Pct(shortLatency, 0.5):F0} s, {Pct(shortLatency, 0.95):F0} s, {Pct(shortLatency, 1):F0} s |");
        sb.AppendLine($"| Delivery time, long messages: median, worst | {Pct(longLatency, 0.5):F0} s, {Pct(longLatency, 1):F0} s |");
        sb.AppendLine($"| Total time, including the drain | {elapsed.TotalMinutes:F1} min |");
        sb.AppendLine($"| Connections (A, B) | {Frames("A", "C C")}, {Frames("B", "C C")} |");
        sb.AppendLine($"| Frames between the two stations | {Frames("A") + Frames("B")} ({Frames("A", "I ") + Frames("B", "I ")} I-frames, {Frames("A", "REJ") + Frames("B", "REJ")} REJ) |");
        sb.AppendLine($"| Transmissions (key-ups) | {onAir.Count} (A {onAir.CountBy("A")}, B {onAir.CountBy("B")}) |");
        sb.AppendLine($"| Channel in use | {onAir.Busy.TotalMinutes:F1} min ({onAir.BusyPercent:F0}%) |");
        sb.AppendLine($"| Both sides transmitting at once | {onAir.Doubles} |");
        sb.AppendLine();
        sb.AppendLine("## Timeline");
        sb.AppendLine();
        sb.AppendLine("Recovery is the time from an event to the next delivery.");
        sb.AppendLine();
        lock (events)
        {
            foreach (var (at, what) in events)
            {
                var next = done.Select(d => d.Sent.At + TimeSpan.FromSeconds(d.Latency)).Where(t => t > at).DefaultIfEmpty().Min();
                var recovery = next > at ? $" (next delivery {(next - at).TotalSeconds:F0} s later)" : "";
                sb.AppendLine($"- {at:hh\\:mm\\:ss} {what}{recovery}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("The air transcript and both daemons' logs are beside this file.");
        return sb.ToString();
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
}
