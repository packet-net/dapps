using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Both nodes have mail for each other within a second or two, so both dial:
/// the calls cross on the air. Two real DAPPS daemons on the simulated
/// radio channel (<see cref="NetSimTwoBpqFixture"/>), a few rounds of it,
/// each round starting from no link. Every message must arrive exactly
/// once, promptly, with at most two dials a round between them. Writes a
/// report with the on-air timings to scenario-reports/ beside the test
/// build.
///
/// Dials, not SABMs, are what's bounded: when two calls cross, BPQ itself
/// sends three or four SABMs to set up the one link (see the crossed-call
/// experiments in docs-internal/end-to-end-tests.md). And the crossing has
/// to be spotted: no round may fall back to waiting out the prompt.
///
/// A second test does one such round straight after both BPQs restart,
/// as after a reboot, when each holds its first connects for a while.
/// </summary>
public abstract class CrossedCallScenarioTests(NetSimTwoBpqFixture fixture) : IAsyncLifetime
{
    private const string App = "crossed";
    private const int Rounds = 3;
    private const int MessagesEach = 2;

    private const string Spotted = "the calls crossed";
    private const string LinkWasUp = "was already up, so no prompt is coming";
    private const string Fallback = "assuming it dialled us at the same moment";

    private readonly List<IAsyncDisposable> running = [];
    private AirMonitor? air;
    private ChannelLog? channel;
    private bool restartedBpqs;

    /// <summary>How long a round may take, first submit to last delivery.</summary>
    protected abstract TimeSpan RoundLimit { get; }

    /// <summary>Most dials a round may take between the two nodes.</summary>
    protected virtual int MaxDials => 2;

    /// <summary>How long the round straight after the BPQs restart may take.</summary>
    private TimeSpan ColdLimit => RoundLimit + TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync()
    {
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
        channel = await fixture.StartChannelLogAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        if (channel is not null) await channel.DisposeAsync();
        await Task.Delay(3000);
        // Leave the BPQs warm for the next test.
        if (restartedBpqs) await fixture.WaitUntilReadyAsync();
    }

    [Fact]
    public async Task BothNodesDialAtOnce_EveryMessageArrivesOnce_OnOneOrTwoConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        // No hold, so each round starts with no link and both have to dial.
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var a = await StartNodeAsync("crossA", fixture.ApplCallA, fixture.AgwPortA, fixture.ApplCallB, settings, ct);
        var b = await StartNodeAsync("crossB", fixture.ApplCallB, fixture.AgwPortB, fixture.ApplCallA, settings, ct);
        var results = new List<RoundResult>();
        var started = DateTime.UtcNow;

        for (var round = 1; round <= Rounds; round++)
        {
            results.Add(await RoundAsync(a, b, round, RoundLimit, ct));
        }

        var duplicates = await DuplicatesAsync(a, b, ct);
        var report = Report(results, duplicates, channel!.Summarise(started, DateTime.UtcNow),
            $"Both nodes submit {MessagesEach} messages to each other within 0.3 to 2 s, so both dial. {Rounds} rounds, each from no link.");
        WriteReport(report, "crossed-");

        results.Should().AllSatisfy(r => r.Complete.Should().BeTrue($"round {r.Round}'s messages should all arrive within {RoundLimit.TotalSeconds:F0}s\n{report}\n{Diagnostics()}"));
        duplicates.Should().Be(0, "every message should arrive exactly once\n" + report);
        results.Should().AllSatisfy(r => r.Dials.Should().BeLessThanOrEqualTo(MaxDials, $"round {r.Round}: {MaxDials} dials at most\n{report}"));
        results.Should().AllSatisfy(r => r.Fallbacks.Should().Be(0, $"round {r.Round}: no session should have had to wait out the prompt\n{report}"));
        results.Where(r => r.Dials == 2).Should().AllSatisfy(r => r.Spotted.Should().BePositive(
            $"round {r.Round}: both nodes dialled, so one of them should have seen the calls cross or the link already up\n{report}"));
    }

    [Fact]
    public async Task StraightAfterTheNodesRestart_BothDialAtOnce_EveryMessageArrivesOnce()
    {
        // As after a reboot: each BPQ holds its first connects until its
        // KISS link is up, so both calls go out late, together.
        var ct = TestContext.Current.CancellationToken;
        restartedBpqs = true;
        await fixture.RestartBpqsColdAsync();
        // The monitor's sockets went with the old BPQs.
        await air!.DisposeAsync();
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, ct);
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var a = await StartNodeAsync("coldA", fixture.ApplCallA, fixture.AgwPortA, fixture.ApplCallB, settings, ct);
        var b = await StartNodeAsync("coldB", fixture.ApplCallB, fixture.AgwPortB, fixture.ApplCallA, settings, ct);
        var started = DateTime.UtcNow;

        var result = await RoundAsync(a, b, 1, ColdLimit, ct);

        var duplicates = await DuplicatesAsync(a, b, ct);
        var report = Report([result], duplicates, channel!.Summarise(started, DateTime.UtcNow),
            $"Both BPQs restarted, then both nodes submit {MessagesEach} messages to each other within 0.3 to 2 s.");
        WriteReport(report, "crossed-cold-");
        WriteLogs("crossed-cold-", a, b);

        result.Complete.Should().BeTrue($"the messages should all arrive within {ColdLimit.TotalSeconds:F0}s\n{report}\n{Diagnostics()}");
        duplicates.Should().Be(0, "every message should arrive exactly once\n" + report);
    }

    /// <summary>Both nodes submit, 0.3 to 2 s apart; wait for delivery, then for the link to go.</summary>
    private async Task<RoundResult> RoundAsync(DappsDaemon a, DappsDaemon b, int round, TimeSpan limit, CancellationToken ct)
    {
        var connectsBefore = Connects();
        var dialsBefore = Dials(a) + Dials(b);
        var spottedBefore = Count(a, b, Spotted) + Count(a, b, LinkWasUp);
        var fallbacksBefore = Count(a, b, Fallback);
        var framesBefore = Frames();
        var clock = Stopwatch.StartNew();
        // One node a random 0.3 to 2 s after the other: still a crossed
        // call (a node takes over 10 s to answer at 1200 baud), but not
        // the same millisecond. Two simulated Dire Wolfs never seed the
        // random numbers that pick their transmit slots, so calls made
        // together collide at every repeat until they retry out; real
        // TNCs don't draw the same numbers.
        var (first, second) = Random.Shared.Next(2) == 0 ? (a, b) : (b, a);
        var stagger = TimeSpan.FromMilliseconds(Random.Shared.Next(300, 2001));
        var firstSubmit = SubmitAsync(first, Other(first, a, b).Callsign, $"round {round} from {Side(first, a)}", ct);
        await Task.Delay(stagger, ct);
        await Task.WhenAll(firstSubmit, SubmitAsync(second, Other(second, a, b).Callsign, $"round {round} from {Side(second, a)}", ct));

        var arrived = await Task.WhenAll(
            WaitForRoundAsync(b, round, "from A", limit, ct),
            WaitForRoundAsync(a, round, "from B", limit, ct));
        var elapsed = clock.Elapsed;

        // Let the link go before the next round: no hold, so the caller
        // hangs up after its quiet spell.
        await WaitForNoLinkAsync([a, b], ct);
        return new RoundResult(round, arrived.All(x => x), elapsed, Dials(a) + Dials(b) - dialsBefore,
            Count(a, b, Spotted) + Count(a, b, LinkWasUp) - spottedBefore, Count(a, b, Fallback) - fallbacksBefore,
            Connects() - connectsBefore, Frames() - framesBefore);
    }

    private static DappsDaemon Other(DappsDaemon node, DappsDaemon a, DappsDaemon b) => ReferenceEquals(node, a) ? b : a;

    private static string Side(DappsDaemon node, DappsDaemon a) => ReferenceEquals(node, a) ? "A" : "B";

    private sealed record RoundResult(int Round, bool Complete, TimeSpan Elapsed, int Dials, int Spotted, int Fallbacks, int Connects, int Frames);

    /// <summary>Calls the daemon has asked its node to make.</summary>
    private static int Dials(DappsDaemon node) =>
        System.Text.RegularExpressions.Regex.Count(node.Log, "AGW: requesting ");

    private static int Count(DappsDaemon a, DappsDaemon b, string text) =>
        System.Text.RegularExpressions.Regex.Count(a.Log + b.Log, System.Text.RegularExpressions.Regex.Escape(text));

    private static async Task SubmitAsync(DappsDaemon node, string to, string label, CancellationToken ct)
    {
        for (var i = 1; i <= MessagesEach; i++)
        {
            await node.SubmitAsync(App, to, Encoding.UTF8.GetBytes($"{label}, message {i}: the quick brown fox jumps over the lazy dog"), ct);
        }
    }

    private static async Task<bool> WaitForRoundAsync(DappsDaemon node, int round, string from, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            var got = (await node.InboundAsync(App, ct)).Count(m => Encoding.UTF8.GetString(m.Payload).StartsWith($"round {round} {from}", StringComparison.Ordinal));
            if (got >= MessagesEach) return true;
            await Task.Delay(200, ct);
        }
        return false;
    }

    /// <summary>Wait until neither daemon has anything to send and no link has carried a frame for a while.</summary>
    private async Task WaitForNoLinkAsync(DappsDaemon[] nodes, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var frames = Frames();
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000, ct);
            var pending = 0;
            foreach (var n in nodes) pending += await n.PendingOutboundAsync(ct);
            if (Frames() != frames || pending > 0)
            {
                frames = Frames();
                quietSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - quietSince >= TimeSpan.FromSeconds(15))
            {
                return;
            }
        }
    }

    /// <summary>Messages that arrived more than once, by their text.</summary>
    private static async Task<int> DuplicatesAsync(DappsDaemon a, DappsDaemon b, CancellationToken ct)
    {
        var texts = (await a.InboundAsync(App, ct)).Concat(await b.InboundAsync(App, ct))
            .Select(m => Encoding.UTF8.GetString(m.Payload)).ToList();
        return texts.Count - texts.Distinct().Count();
    }

    private async Task<DappsDaemon> StartNodeAsync(string name, string callsign, int agwPort, string neighbour,
        IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.RadioPortIndex,
            [new(neighbour, fixture.RadioPortIndex)], settings, ct);
        running.Add(node);
        return node;
    }

    /// <summary>Connects (SABM) between the two DAPPS callsigns, either way.</summary>
    private int Connects() =>
        air!.CountSentBy("A", $"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <C C")
        + air.CountSentBy("B", $"Fm {fixture.ApplCallB} To {fixture.ApplCallA} <C C");

    private int Frames() =>
        air!.SentBy("A").Count(f => f.Contains($"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <", StringComparison.Ordinal))
        + air.SentBy("B").Count(f => f.Contains($"Fm {fixture.ApplCallB} To {fixture.ApplCallA} <", StringComparison.Ordinal));

    private string Report(List<RoundResult> results, int duplicates, ChannelLog.Summary onAir, string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Crossed-call scenario: {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine(what);
        sb.AppendLine();
        sb.AppendLine($"BPQ radio port: {fixture.RadioSettings}");
        sb.AppendLine();
        sb.AppendLine("| Round | All arrived | Time to last delivery | Dials | Crossing spotted | Waited out the prompt | SABMs on air | Frames |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Round} | {(r.Complete ? "yes" : "no")} | {r.Elapsed.TotalSeconds:F1} s | {r.Dials} | {(r.Spotted > 0 ? "yes" : "no")} | {(r.Fallbacks > 0 ? "yes" : "no")} | {r.Connects} | {r.Frames} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Duplicates: {duplicates}. Transmissions (key-ups): {onAir.Count} (A {onAir.CountBy("A")}, B {onAir.CountBy("B")}); both sides transmitting at once: {onAir.Doubles}.");
        sb.AppendLine();
        sb.AppendLine("<details><summary>What went over the air</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(new string([.. air!.Transcript().Select(c => c == '\n' || (c >= ' ' && c <= '~') ? c : '.')]));
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    private void WriteReport(string report, string prefix)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var name = prefix + fixture.ChannelName.Split(' ')[0].ToLowerInvariant() + fixture.ChannelName.Split(' ')[1] + ".md";
        File.WriteAllText(Path.Combine(dir, name), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
    }

    /// <summary>Both daemons' logs beside the report, for a round that goes wrong.</summary>
    private void WriteLogs(string prefix, DappsDaemon a, DappsDaemon b)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        var modem = fixture.ChannelName.Split(' ')[0].ToLowerInvariant() + fixture.ChannelName.Split(' ')[1];
        File.WriteAllText(Path.Combine(dir, $"{prefix}{modem}-a.log"), a.Log);
        File.WriteAllText(Path.Combine(dir, $"{prefix}{modem}-b.log"), b.Log);
    }

    private string Diagnostics() => string.Join("\n", running.OfType<DappsDaemon>().Select(d => d.Tail(80)));
}

[Collection("net-sim AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioAfsk1200Tests(NetSimAfsk1200Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    // 1 or 2 dials and 14 to 24 s, or up to 30 s when two transmissions
    // collide. The second node's call can land just as the first node's
    // connects; the first node now holds its prompt until its own call has
    // connected, so that is an ordinary crossing too (#205: it was 5 dials
    // and about 70 s). The cold round meets that timing nearly every time.
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(45);
}

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioQpsk3600Tests(NetSimQpsk3600Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(30);
}
