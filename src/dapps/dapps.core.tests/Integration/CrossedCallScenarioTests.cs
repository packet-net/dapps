using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Both nodes have mail for each other within a second or two, so both dial:
/// the calls cross on the air. Two real DAPPS daemons on a pair of nodes
/// (<see cref="IDappsScenarioBed"/>: BPQ or pdn on the simulated radio
/// channel, or pdn over AXUDP), a few rounds of it, each round starting
/// from no link. Every message must arrive exactly
/// once, promptly, with at most two dials a round between them. Writes a
/// report with the on-air timings to scenario-reports/ beside the test
/// build.
///
/// Dials, not SABMs, are what's bounded: when two calls cross, BPQ itself
/// sends three or four SABMs to set up the one link (see the crossed-call
/// experiments in docs-internal/end-to-end-tests.md). And the crossing has
/// to be spotted: no round may fall back to waiting out the prompt.
///
/// A second test does one such round straight after both nodes restart,
/// as after a reboot, when BPQ holds its first connects for a while.
/// </summary>
public abstract class CrossedCallScenarioTests(IDappsScenarioBed fixture) : IAsyncLifetime
{
    private const string App = "crossed";
    private const int Rounds = 3;
    private const int MessagesEach = 2;

    private const string Spotted = "the calls crossed";
    private const string LinkWasUp = "was already up, so no prompt is coming";
    private const string Fallback = "assuming it dialled us at the same moment";

    private readonly List<IAsyncDisposable> running = [];
    private IAirMonitor? air;
    private ChannelLog? channel;
    private bool restartedNodes;

    /// <summary>How long a round may take, first submit to last delivery.</summary>
    protected abstract TimeSpan RoundLimit { get; }

    /// <summary>Most dials a round may take between the two nodes.</summary>
    protected virtual int MaxDials => 2;

    /// <summary>How long the round straight after the nodes restart may take.</summary>
    private TimeSpan ColdLimit => RoundLimit + TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far apart, in ms, the two nodes submit: 0.3 to 2 s on the
    /// simulated channel (a node takes over 10 s to answer at 1200 baud),
    /// so the calls still cross without starting in the same millisecond;
    /// see <see cref="RoundAsync"/>.
    /// </summary>
    protected virtual (int From, int To) StaggerMs => (300, 2001);

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
        // Leave the nodes warm for the next test.
        if (restartedNodes) await fixture.WaitUntilReadyAsync();
    }

    [Fact]
    public async Task BothNodesDialAtOnce_EveryMessageArrivesOnce_OnOneOrTwoConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        // No hold, so each round starts with no link and both have to dial.
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var a = await StartNodeAsync("crossA", fixture.ApplCallA, fixture.NodeA, fixture.ApplCallB, settings, ct);
        var b = await StartNodeAsync("crossB", fixture.ApplCallB, fixture.NodeB, fixture.ApplCallA, settings, ct);
        var results = new List<RoundResult>();
        var started = DateTime.UtcNow;

        for (var round = 1; round <= Rounds; round++)
        {
            results.Add(await RoundAsync(a, b, round, RoundLimit, ct));
        }

        var duplicates = await DuplicatesAsync(a, b, ct);
        var report = Report(results, duplicates, channel?.Summarise(started, DateTime.UtcNow),
            $"Both nodes submit {MessagesEach} messages to each other within {StaggerMs.From / 1000.0:0.###} to {(StaggerMs.To - 1) / 1000.0:0.###} s, so both dial. {Rounds} rounds, each from no link.");
        WriteReport(report, "crossed-");

        results.Should().AllSatisfy(r => r.Complete.Should().BeTrue($"round {r.Round}'s messages should all arrive within {RoundLimit.TotalSeconds:F0}s\n{report}\n{Diagnostics()}"));
        duplicates.Should().Be(0, "every message should arrive exactly once\n" + report);
        results.Should().AllSatisfy(r => r.Dials.Should().BeLessThanOrEqualTo(MaxDials, $"round {r.Round}: {MaxDials} dials at most\n{report}"));
        // Where the node tells DAPPS of a crossing (AGW, or pdn over RHPv2)
        // no round should wait out the prompt. Elsewhere a round where both
        // nodes dialled waits it out, as the crossing can't be spotted; a
        // round with one dial never should.
        results.Where(r => SpotsCrossings || r.Dials <= 1).Should().AllSatisfy(r => r.Fallbacks.Should().Be(0, $"round {r.Round}: no session should have had to wait out the prompt\n{report}"));
        if (SpotsCrossings)
        {
            results.Where(r => r.Dials == 2).Should().AllSatisfy(r => r.Spotted.Should().BePositive(
                $"round {r.Round}: both nodes dialled, so one of them should have seen the calls cross or the link already up\n{report}"));
        }
    }

    [Fact]
    public async Task StraightAfterTheNodesRestart_BothDialAtOnce_EveryMessageArrivesOnce()
    {
        // As after a reboot: each BPQ holds its first connects until its
        // KISS link is up, so both calls go out late, together.
        var ct = TestContext.Current.CancellationToken;
        restartedNodes = true;
        await fixture.RestartNodesColdAsync();
        // The monitor's sockets went with the old nodes.
        await air!.DisposeAsync();
        air = await fixture.StartAirMonitorAsync(ct);
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var a = await StartNodeAsync("coldA", fixture.ApplCallA, fixture.NodeA, fixture.ApplCallB, settings, ct);
        var b = await StartNodeAsync("coldB", fixture.ApplCallB, fixture.NodeB, fixture.ApplCallA, settings, ct);
        var started = DateTime.UtcNow;

        var result = await RoundAsync(a, b, 1, ColdLimit, ct);

        var duplicates = await DuplicatesAsync(a, b, ct);
        var report = Report([result], duplicates, channel?.Summarise(started, DateTime.UtcNow),
            $"Both nodes restarted, then both DAPPS daemons submit {MessagesEach} messages to each other within {StaggerMs.From / 1000.0:0.###} to {(StaggerMs.To - 1) / 1000.0:0.###} s.");
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
        var stagger = TimeSpan.FromMilliseconds(Random.Shared.Next(StaggerMs.From, StaggerMs.To));
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

    /// <summary>Calls the daemon has asked its node to make, over AGW or RHPv2.</summary>
    private static int Dials(DappsDaemon node) =>
        System.Text.RegularExpressions.Regex.Count(node.Log, "AGW: requesting |RHP: open active ");

    /// <summary>
    /// Whether a node can tell its call crossed the other's: over AGW it
    /// sees the peer's SABM on the node's monitor. RHPv2 has no monitor,
    /// so there it's up to the node to say, in its open reply: pdn does
    /// (node-v0.57.0 on) and its classes turn this on; XRouter doesn't.
    /// Where nobody says, both calls just connect (the node makes one link
    /// of them, as BPQ does), neither end sends a prompt, and each waits
    /// 10 s for one before sending its exchange anyway.
    /// </summary>
    protected virtual bool SpotsCrossings => !fixture.NodeA.IsRhp;

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

    private async Task<DappsDaemon> StartNodeAsync(string name, string callsign, NodeAttachment on, string neighbour,
        IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, on, [new(neighbour, on.BearerPort)], settings, ct);
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

    private string Report(List<RoundResult> results, int duplicates, ChannelLog.Summary? onAir, string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Crossed-call scenario: {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine(what);
        sb.AppendLine();
        sb.AppendLine($"Radio port: {fixture.RadioSettings}");
        sb.AppendLine();
        sb.AppendLine("| Round | All arrived | Time to last delivery | Dials | Crossing spotted | Waited out the prompt | SABMs on air | Frames |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Round} | {(r.Complete ? "yes" : "no")} | {r.Elapsed.TotalSeconds:F1} s | {r.Dials} | {(r.Spotted > 0 ? "yes" : "no")} | {(r.Fallbacks > 0 ? "yes" : "no")} | {r.Connects} | {r.Frames} |");
        }
        sb.AppendLine();
        sb.AppendLine(onAir is null
            ? $"Duplicates: {duplicates}."
            : $"Duplicates: {duplicates}. Transmissions (key-ups): {onAir.Count} (A {onAir.CountBy("A")}, B {onAir.CountBy("B")}); both sides transmitting at once: {onAir.Doubles}.");
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
        File.WriteAllText(Path.Combine(dir, prefix + fixture.ReportTag + ".md"), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
    }

    /// <summary>Both daemons' logs beside the report, for a round that goes wrong.</summary>
    private void WriteLogs(string prefix, DappsDaemon a, DappsDaemon b)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        File.WriteAllText(Path.Combine(dir, $"{prefix}{fixture.ReportTag}-a.log"), a.Log);
        File.WriteAllText(Path.Combine(dir, $"{prefix}{fixture.ReportTag}-b.log"), b.Log);
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
    // and about 70 s). The cold round met that timing in 3 of 5 runs.
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(45);
}

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioQpsk3600Tests(NetSimQpsk3600Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(30);
}

// On pdn the two calls make one link, so the default 2 dials, and pdn's
// open reply says the calls crossed (node-v0.57.0 on), so no round waits
// out the prompt: 11.6 to 16.5 s a round at AFSK 1200 and 7.4 to 11.1 s
// at QPSK 3600 in 3 runs each (21 to 27 s and 17 to 29 s when every round
// paid the 10 s wait), so about twice that. One round at each speed lost
// data to a link reset when one pdn was still dialling; see "What pdn
// showed" in docs-internal/end-to-end-tests.md.
[Collection("net-sim pdn AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioPdnAfsk1200Tests(NetSimPdnAfsk1200Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(30);
    protected override bool SpotsCrossings => true;
}

[Collection("net-sim pdn QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioPdnQpsk3600Tests(NetSimPdnQpsk3600Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(20);
    protected override bool SpotsCrossings => true;
}
