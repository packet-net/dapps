using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Both nodes have mail for each other at the same moment, so both dial:
/// the calls cross on the air. Two real DAPPS daemons on the simulated
/// radio channel (<see cref="NetSimTwoBpqFixture"/>), a few rounds of it,
/// each round starting from no link. Every message must arrive exactly
/// once, promptly, with at most two dials a round between them. Writes a
/// report with the on-air timings to scenario-reports/ beside the test
/// build.
///
/// Dials, not SABMs, are what's bounded: when two calls cross, BPQ itself
/// sends three or four SABMs to set up the one link (see the crossed-call
/// experiments in docs-internal/end-to-end-tests.md).
/// </summary>
public abstract class CrossedCallScenarioTests(NetSimTwoBpqFixture fixture) : IAsyncLifetime
{
    private const string App = "crossed";
    private const int Rounds = 3;
    private const int MessagesEach = 2;

    private readonly List<IAsyncDisposable> running = [];
    private AirMonitor? air;
    private ChannelLog? channel;

    /// <summary>How long a round may take, first submit to last delivery.</summary>
    protected abstract TimeSpan RoundLimit { get; }

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
            var connectsBefore = Connects();
            var dialsBefore = Dials(a) + Dials(b);
            var framesBefore = Frames();
            var clock = Stopwatch.StartNew();
            await Task.WhenAll(
                SubmitAsync(a, b.Callsign, $"round {round} from A", ct),
                SubmitAsync(b, a.Callsign, $"round {round} from B", ct));

            var arrived = await Task.WhenAll(
                WaitForRoundAsync(b, round, "from A", RoundLimit, ct),
                WaitForRoundAsync(a, round, "from B", RoundLimit, ct));
            var elapsed = clock.Elapsed;

            // Let the link go before the next round: no hold, so the caller
            // hangs up after its quiet spell.
            await WaitForNoLinkAsync([a, b], ct);
            results.Add(new RoundResult(round, arrived.All(x => x), elapsed, Dials(a) + Dials(b) - dialsBefore,
                Connects() - connectsBefore, Frames() - framesBefore));
        }

        var duplicates = await DuplicatesAsync(a, b, ct);
        var report = Report(results, duplicates, channel!.Summarise(started, DateTime.UtcNow));
        WriteReport(report);

        results.Should().AllSatisfy(r => r.Complete.Should().BeTrue($"round {r.Round}'s messages should all arrive within {RoundLimit.TotalSeconds:F0}s\n{report}\n{Diagnostics()}"));
        duplicates.Should().Be(0, "every message should arrive exactly once\n" + report);
        results.Should().AllSatisfy(r => r.Dials.Should().BeLessThanOrEqualTo(2, $"round {r.Round}: two dials at most, one from each node\n{report}"));
    }

    private sealed record RoundResult(int Round, bool Complete, TimeSpan Elapsed, int Dials, int Connects, int Frames);

    /// <summary>Calls the daemon has asked its node to make.</summary>
    private static int Dials(DappsDaemon node) =>
        System.Text.RegularExpressions.Regex.Count(node.Log, "AGW: requesting ");

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

    private string Report(List<RoundResult> results, int duplicates, ChannelLog.Summary onAir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Crossed-call scenario: {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine($"Both nodes submit {MessagesEach} messages to each other at the same moment, so both dial. {Rounds} rounds, each from no link.");
        sb.AppendLine();
        sb.AppendLine($"BPQ radio port: {fixture.RadioSettings}");
        sb.AppendLine();
        sb.AppendLine("| Round | All arrived | Time to last delivery | Dials | SABMs on air | Frames |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Round} | {(r.Complete ? "yes" : "no")} | {r.Elapsed.TotalSeconds:F1} s | {r.Dials} | {r.Connects} | {r.Frames} |");
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

    private void WriteReport(string report)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var name = "crossed-" + fixture.ChannelName.Split(' ')[0].ToLowerInvariant() + fixture.ChannelName.Split(' ')[1] + ".md";
        File.WriteAllText(Path.Combine(dir, name), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
    }

    private string Diagnostics() => string.Join("\n", running.OfType<DappsDaemon>().Select(d => d.Tail(80)));
}

[Collection("net-sim AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioAfsk1200Tests(NetSimAfsk1200Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(90);
}

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class CrossedCallScenarioQpsk3600Tests(NetSimQpsk3600Fixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(60);
}
