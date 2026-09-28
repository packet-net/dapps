using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Store-and-forward relay across three real DAPPS daemons on three real
/// BPQs: A and C aren't linked at all, so a message from A to C only
/// arrives if B (also running DAPPS, not just a bare packet node) picks it
/// up and forwards it on. See docs/discovery-and-routing.md's "route
/// hints" and "passive-flood" sections for the two ways a node picks a
/// next hop it doesn't have a direct neighbour for.
///
///     DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B
///                              -AXIP- BPQ-C -AGW- DAPPS C
///
/// Covers the first item on docs-internal/end-to-end-tests.md's "Not
/// covered yet" list: the existing end-to-end fixtures only ever wire up
/// two BPQs.
/// </summary>
[Collection("Linbpq three-instance integration")]
[Trait("Category", "Integration")]
public sealed class RelayIntegrationTests(ThreeInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(60);
    private readonly List<IAsyncDisposable> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        // Let the BPQs release their AGW registrations before the next
        // test in the collection reuses the same callsigns.
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AMessage_RelaysThroughAnIntermediateNode_ArrivesAtCExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        // A has no neighbour for C at all - only a route hint pointing at
        // B, the same way an operator would steer traffic at a peer
        // that's reachable only indirectly (docs/discovery-and-routing.md,
        // "Route hints").
        var a = await StartNodeAsync("relayA", fixture.ApplCallA, fixture.AgwPortA,
            [new(fixture.ApplCallB, fixture.AxipPortIndex)],
            [new(fixture.CallsignC, fixture.ApplCallB)], ct);
        var b = await StartNodeAsync("relayB", fixture.ApplCallB, fixture.AgwPortB,
            [new(fixture.ApplCallA, fixture.AxipPortIndex), new(fixture.ApplCallC, fixture.AxipPortIndex)],
            null, ct);
        var c = await StartNodeAsync("relayC", fixture.ApplCallC, fixture.AgwPortC,
            [new(fixture.ApplCallB, fixture.AxipPortIndex)],
            null, ct);

        var payload = Encoding.UTF8.GetBytes("GM, this is A relaying to C via B");
        var id = await a.SubmitAsync("relay", c.Callsign, payload, ct, ttl: 600);

        var got = (await c.WaitForInboundAsync("relay", 1, Delivery, ct)).Single();
        got.Id.Should().Be(id);
        got.Payload.Should().Equal(payload);
        got.OriginatorCallsign.Should().Be(a.Callsign, "src= carries the originator across both hops");

        // B is only a waypoint: it never hands the relayed message to its
        // own app, and it shouldn't come back to A either.
        (await b.InboundAsync("relay", ct)).Should().BeEmpty(Transcript(a, b, c));
        (await a.InboundAsync("relay", ct)).Should().BeEmpty(Transcript(a, b, c));

        // Give a second copy every chance to turn up before declaring
        // "exactly once".
        await Task.Delay(5000, ct);
        (await c.InboundAsync("relay", ct)).Should().ContainSingle(Transcript(a, b, c));
    }

    [Fact]
    public async Task AMessage_WithNoKnownRoute_FloodsThroughAnIntermediateNode_AndArrivesOnce()
    {
        // Same topology, but A has no route hint either - nothing at all
        // says how to reach C. The default passive-flood algorithm's
        // cold-start fallback (FloodFallbackAlgorithm) has to flood to
        // its only neighbour (B), which floods on to its neighbours other
        // than A (i.e. C), covering "and floods" from the issue.
        var ct = TestContext.Current.CancellationToken;
        var a = await StartNodeAsync("floodA", fixture.ApplCallA, fixture.AgwPortA,
            [new(fixture.ApplCallB, fixture.AxipPortIndex)], null, ct);
        var b = await StartNodeAsync("floodB", fixture.ApplCallB, fixture.AgwPortB,
            [new(fixture.ApplCallA, fixture.AxipPortIndex), new(fixture.ApplCallC, fixture.AxipPortIndex)],
            null, ct);
        var c = await StartNodeAsync("floodC", fixture.ApplCallC, fixture.AgwPortC,
            [new(fixture.ApplCallB, fixture.AxipPortIndex)], null, ct);

        var payload = Encoding.UTF8.GetBytes("cold start, no route, flood and hope");
        var id = await a.SubmitAsync("relay", c.Callsign, payload, ct, ttl: 600);

        var got = (await c.WaitForInboundAsync("relay", 1, Delivery, ct)).Single();
        got.Id.Should().Be(id);
        got.Payload.Should().Equal(payload);

        a.Log.Should().Contain("initiating bounded flood", "A has no route to C at all, so it must fall back to flooding\n" + a.Tail());
        (await b.InboundAsync("relay", ct)).Should().BeEmpty(Transcript(a, b, c));

        await Task.Delay(5000, ct);
        (await c.InboundAsync("relay", ct)).Should().ContainSingle(Transcript(a, b, c));
    }

    private string Transcript(DappsDaemon a, DappsDaemon b, DappsDaemon c) =>
        string.Join("\n", new[] { a, b, c }.Select(d => d.Tail(40)));

    private async Task<DappsDaemon> StartNodeAsync(
        string name, string callsign, int agwPort, DappsDaemon.Neighbour[] neighbours,
        DappsDaemon.RouteHint[]? routeHints, CancellationToken ct)
    {
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var node = await DappsDaemon.StartAsync(
            name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex, neighbours, settings, ct, routeHints);
        running.Add(node);
        return node;
    }
}
