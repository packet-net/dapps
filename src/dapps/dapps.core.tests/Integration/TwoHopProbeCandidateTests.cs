using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Services;

namespace dapps.core.tests.Integration;

/// <summary>
/// #249, G6FCI's chain after #246: A learns of C from B's peers list, but C
/// is two hops away (A-B-C over AXIP, no link between A and C), so a direct
/// probe of C never answers. Each try is connect attempts on air for
/// nothing, so the sweep gives up on C after a few, while an operator can
/// still probe it by hand, and mail to C still goes through B. Here A's
/// node has no AXIP mapping for C, so it refuses the connect at once where
/// G6FCI's XRouter tried and timed out; the count is the same either way.
/// </summary>
[Collection("Linbpq three-instance integration")]
[Trait("Category", "Integration")]
public sealed class TwoHopProbeCandidateTests(ThreeInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        // Let the BPQs release their AGW registrations before the next
        // test in the collection reuses the same callsigns.
        await Task.Delay(3000);
    }

    [Fact]
    public async Task CandidateTwoHopsAway_SweepGivesUpOnIt_ButItCanStillBeProbedByHand_AndMailGetsThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = fixture.AxipPortIndex;
        var a = await StartAsync("twohopA", fixture.ApplCallA, fixture.AgwPortA, [new(fixture.ApplCallB, port)], ct);
        var b = await StartAsync("twohopB", fixture.ApplCallB, fixture.AgwPortB,
            [new(fixture.ApplCallA, port), new(fixture.ApplCallC, port)], ct);
        var c = await StartAsync("twohopC", fixture.ApplCallC, fixture.AgwPortC, [new(fixture.ApplCallB, port)], ct);
        await a.SignInAsAdminAsync(ct);

        // A probes B and learns of C from B's peers list.
        (await ProbeAsync(a, fixture.ApplCallB, ct)).LastSuccessAt.Should().NotBeNull(a.Tail());
        Candidate(await ProbesAsync(a, ct)).Source.Should().Be($"via:{fixture.ApplCallB}");

        // B opted out, so each sweep probes C alone. C fails every time.
        (await a.Http.PostAsJsonAsync($"Probes/{fixture.ApplCallB}/optout", new { optOut = true }, ct)).EnsureSuccessStatusCode();
        for (var i = 1; i <= ProbeSchedulerService.UnansweredCandidateTries; i++)
        {
            (await a.Http.PostAsync("Probes/run", null, ct)).EnsureSuccessStatusCode();
            var row = Candidate(await ProbesAsync(a, ct));
            row.LastSuccessAt.Should().BeNull();
            row.ConsecutiveFailures.Should().Be(i, $"sweep {i} should have probed C: {row.LastError}\n{a.Tail()}");
        }

        // The next sweep leaves C alone.
        var before = Candidate(await ProbesAsync(a, ct)).LastProbedAt;
        (await a.Http.PostAsync("Probes/run", null, ct)).EnsureSuccessStatusCode();
        var after = Candidate(await ProbesAsync(a, ct));
        after.LastProbedAt.Should().Be(before);
        after.ConsecutiveFailures.Should().Be(ProbeSchedulerService.UnansweredCandidateTries);

        // By hand it is still a probe, on A's port to B, not "no route".
        var byHand = await a.Http.PostAsync($"Probes/run/{fixture.ApplCallC}", null, ct);
        byHand.EnsureSuccessStatusCode();
        var probed = (await byHand.Content.ReadFromJsonAsync<ProbedNodeModel>(ct))!;
        probed.LastPort.Should().Be(port + 1, "the node's own number for the port, one past the fixture's count from 0");
        probed.ConsecutiveFailures.Should().Be(ProbeSchedulerService.UnansweredCandidateTries + 1);

        // Mail to C still goes through B: B told A of C when A asked for its
        // peers, and A learned the route.
        var payload = Encoding.UTF8.GetBytes("To C, two hops away");
        var id = await a.SubmitAsync("chat", c.Callsign, payload, ct);
        var got = (await c.WaitForInboundAsync("chat", 1, TimeSpan.FromSeconds(90), ct)).Single();
        got.Id.Should().Be(id);
        got.Payload.Should().Equal(payload);
    }

    private ProbedNodeModel Candidate(IEnumerable<ProbedNodeModel> rows) =>
        rows.Single(r => r.Callsign == fixture.ApplCallC);

    private async Task<DappsDaemon> StartAsync(
        string name, string callsign, int agwPort, DappsDaemon.Neighbour[] neighbours, CancellationToken ct)
    {
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var node = await DappsDaemon.StartAsync(
            name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex, neighbours, settings, ct);
        running.Add(node);
        return node;
    }

    private static async Task<ProbedNodeModel> ProbeAsync(DappsDaemon from, string callsign, CancellationToken ct)
    {
        var response = await from.Http.PostAsync($"Probes/run/{callsign}", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProbedNodeModel>(ct))!;
    }

    private static async Task<IReadOnlyList<ProbedNodeModel>> ProbesAsync(DappsDaemon on, CancellationToken ct) =>
        (await on.Http.GetFromJsonAsync<List<ProbedNodeModel>>("Probes", ct))!;
}
