using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests for the bounded-flood fallback. Decorates an inner algorithm
/// (here the static-only resolver, to keep the tests focused on flood
/// behaviour rather than learning interactions). Three concerns:
///
/// <list type="number">
/// <item>When the inner algorithm has no route AND there are
///   neighbours, initiate a flood with the default hop budget.</item>
/// <item>When a message arrives in flood mode, propagate the flood
///   to all direct neighbours EXCEPT the one it came from, with
///   <c>HopBudget - 1</c>. At budget zero, drop without re-flooding.</item>
/// <item>When the inner algorithm DOES have a route, use it (cheaper
///   than a flood); flood is the last resort.</item>
/// </list>
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class FloodFallbackAlgorithmTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseRoutingContext context = null!;
    private FloodFallbackAlgorithm algorithm = null!;
    private FloodFallbackAlgorithm defaultStack = null!;

    private const string OurCallsign = "G0US-1";

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-flood-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
            c.CreateTable<DbLearnedRoute>();
            c.CreateTable<DbFloodSeen>();
            // Three direct neighbours so the flood-routes list isn't trivially empty.
            c.Insert(new DbNeighbour { Callsign = "G0NA-1", UdpEndpoint = "127.0.0.1:65501" });
            c.Insert(new DbNeighbour { Callsign = "G0NB-1", UdpEndpoint = "127.0.0.1:65502" });
            c.Insert(new DbNeighbour { Callsign = "G0NC-1", UdpEndpoint = "127.0.0.1:65503" });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = OurCallsign });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        context = new DatabaseRoutingContext(database, optionsMonitor);
        var staticAlgo = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        algorithm = new FloodFallbackAlgorithm(staticAlgo, NullLogger<FloodFallbackAlgorithm>.Instance);
        // The default stack as Program.cs builds it, for the split-horizon
        // tests that need the learned-route tier.
        defaultStack = new FloodFallbackAlgorithm(
            new PassiveLearningAlgorithm(staticAlgo, NullLogger<PassiveLearningAlgorithm>.Instance),
            NullLogger<FloodFallbackAlgorithm>.Instance);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static DbMessage OutboundFor(string destBaseCallsign, byte? floodHopsRemaining = null, string sourceCallsign = "", string originatorCallsign = "") => new()
    {
        Id = "0000001",
        Destination = $"chat@{destBaseCallsign}-1",
        Payload = "x"u8.ToArray(),
        SourceCallsign = sourceCallsign,
        OriginatorCallsign = originatorCallsign,
        FloodHopsRemaining = floodHopsRemaining,
    };

    /// <summary>A message passing through: it came over a session from
    /// <paramref name="from"/> (no flood state, as AGW and RHP carry none),
    /// started at G0ORG-1.</summary>
    private static DbMessage TransitFor(string destination, string from) => new()
    {
        Id = "0000009",
        Destination = destination,
        Payload = "x"u8.ToArray(),
        SourceCallsign = from,
        OriginatorCallsign = "G0ORG-1",
    };

    [Fact]
    public async Task Resolve_InnerUnreachable_InitiatesFloodToAllNeighbours_WithDefaultBudget()
    {
        // No static neighbour for G0XYZ; inner returns Unreachable; we
        // should flood to all 3 direct neighbours with the default budget.
        var decision = await algorithm.ResolveAsync(OutboundFor("G0XYZ"), context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(FloodFallbackAlgorithm.DefaultHopBudget);
        flood.Routes.Should().HaveCount(3);
        flood.Routes.Select(r => r.Callsign).Should().BeEquivalentTo(new[] { "G0NA-1", "G0NB-1", "G0NC-1" });
    }

    [Fact]
    public async Task Resolve_InnerHasRoute_UsesIt_NoFlood()
    {
        // Static manual neighbour matches the destination - inner returns
        // NextHop, flood doesn't kick in.
        var decision = await algorithm.ResolveAsync(OutboundFor("G0NA"), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be("G0NA-1");
    }

    [Fact]
    public async Task Resolve_InFlightFlood_ReFloodsExcludingSource_WithDecrementedBudget()
    {
        // A message arrived in flood mode from G0NA-1 with budget 4 left.
        // Re-flood to neighbours EXCEPT G0NA-1, with budget 3.
        var msg = OutboundFor("G0XYZ", floodHopsRemaining: 4, sourceCallsign: "G0NA-1");

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(3);
        flood.Routes.Select(r => r.Callsign).Should().BeEquivalentTo(new[] { "G0NB-1", "G0NC-1" },
            "the link source must not appear in the re-flood - that would just bounce the message right back");
    }

    [Fact]
    public async Task Resolve_InFlightFloodAtZeroHops_DoesNotReFlood()
    {
        // Hop budget exhausted at this node - drop, don't re-flood.
        var msg = OutboundFor("G0XYZ", floodHopsRemaining: 0, sourceCallsign: "G0NA-1");

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_InFlightFlood_IgnoresInnerEvenIfRouteAvailable()
    {
        // A flood for G0NA arrives in flight. G0NA IS in our static
        // neighbours table, but in flood mode the flood mechanism owns
        // the routing - we re-flood (excluding the link source), not
        // route to the destination. This pins the "flood-mode messages
        // continue as floods" behaviour: we don't covertly switch
        // floods to direct routing mid-propagation.
        var msg = OutboundFor("G0NA", floodHopsRemaining: 3, sourceCallsign: "G0NB-1");

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>();
    }

    [Fact]
    public async Task Resolve_LocallySubmitted_NoRoute_Floods()
    {
        // SubmitOutboundMessage stores our own callsign as both the link
        // source and the originator: ours to flood (#295).
        var msg = OutboundFor("G0XYZ", sourceCallsign: OurCallsign, originatorCallsign: OurCallsign);

        var decision = await defaultStack.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(FloodFallbackAlgorithm.DefaultHopBudget);
        flood.Routes.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("G0ORG-1")]
    [InlineData("")]
    public async Task Resolve_Transit_NoRoute_StaysQueued_DoesNotFlood(string originator)
    {
        // #295: a relay that got a message over a session, with no route
        // on, used to start a fresh full flood to every neighbour, the one
        // it came from included. Only the originator floods now; the relay
        // waits for a route. With or without src= (an older or other
        // implementation may leave it out), a message from a neighbour is
        // not ours.
        var msg = OutboundFor("G0XYZ", sourceCallsign: "G0NA-1", originatorCallsign: originator);

        var decision = await defaultStack.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>("a relay with no route keeps the message queued");
    }

    [Fact]
    public async Task Resolve_Transit_WithARoute_StillRoutes()
    {
        // Not flooding doesn't mean not forwarding: a route elsewhere is used.
        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0NB-1", from: "G0NA-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("G0NB-1");
    }

    [Fact]
    public async Task Resolve_Transit_NeverGoesBackToTheLinkSource_ByNeighbour()
    {
        // #297: the destination's own neighbour row is the link source.
        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0NA-1", from: "G0NA-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_Transit_NeverGoesBackToTheLinkSource_ByStandIn()
    {
        // G0NA-5 has no row; G0NA-1 would stand in for it under the same
        // callsign, but G0NA-1 is where the message came from.
        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0NA-5", from: "G0NA-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_Transit_SkippedTierFallsThroughToTheNext()
    {
        // The stand-in is the link source, but a route hint gives another
        // way: passing over one tier lets the next have its turn.
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbRouteHint { Destination = "G0NA-5", NextHop = "G0NB-1" });

        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0NA-5", from: "G0NA-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("G0NB-1");
    }

    [Fact]
    public async Task Resolve_Transit_NeverGoesBackToTheLinkSource_ByDiscoveredPeer()
    {
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("G0XYZ-1", "udp", "k"),
                Callsign = "G0XYZ-1", Bearer = "udp", ChannelKey = "k",
                UdpEndpoint = "127.0.0.1:65509", TtlSeconds = 3600, LastSeen = DateTime.UtcNow,
            });

        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0XYZ-1", from: "G0XYZ-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_Transit_NeverGoesBackToTheLinkSource_ByRouteHint()
    {
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbRouteHint { Destination = "G0XYZ-1", NextHop = "G0NA-1" });

        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0XYZ-1", from: "G0NA-1"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_Transit_NeverGoesBackToTheLinkSource_ByLearnedRoute()
    {
        // The #297 black hole: a learned (or gossiped) route to the
        // destination via the neighbour the message came from. It stays
        // queued, and the route itself is kept for traffic from elsewhere.
        var ct = TestContext.Current.CancellationToken;
        await context.UpsertLearnedRouteAsync("G0XYZ-1", "G0NA-1", ct);

        var decision = await defaultStack.ResolveAsync(TransitFor("chat@G0XYZ-1", from: "G0NA-1"), context, ct);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
        (await context.GetLearnedRouteAsync("G0XYZ-1", ct)).Should().NotBeNull("the route is still good for traffic that didn't come from G0NA-1");

        var fromElsewhere = await defaultStack.ResolveAsync(TransitFor("chat@G0XYZ-1", from: "G0NB-1"), context, ct);
        fromElsewhere.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("G0NA-1");
    }

    [Fact]
    public async Task Resolve_Transit_FromAnotherSsid_StillUsesTheRoute()
    {
        // The exact call+SSID only: G0NA-2 is another station from G0NA-1.
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbRouteHint { Destination = "G0XYZ-1", NextHop = "G0NA-1" });

        var decision = await defaultStack.ResolveAsync(
            TransitFor("chat@G0XYZ-1", from: "G0NA-2"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("G0NA-1");
    }

    [Fact]
    public async Task Resolve_LocallySubmitted_AnyTierIsFine()
    {
        // Nothing to avoid for our own message: the hint is used.
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbRouteHint { Destination = "G0XYZ-1", NextHop = "G0NA-1" });

        var decision = await defaultStack.ResolveAsync(
            OutboundFor("G0XYZ", sourceCallsign: OurCallsign, originatorCallsign: OurCallsign),
            context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("G0NA-1");
    }

    [Fact]
    public async Task Resolve_InFlightDatagramFlood_StillContinues_OnTheDefaultStack()
    {
        // UDP and MeshCore carry the hop budget: a flood that arrived with
        // hops left carries on, on the default stack too.
        var msg = OutboundFor("G0XYZ", floodHopsRemaining: 2, sourceCallsign: "G0NA-1", originatorCallsign: "G0ORG-1");

        var decision = await defaultStack.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(1);
        flood.Routes.Select(r => r.Callsign).Should().BeEquivalentTo(new[] { "G0NB-1", "G0NC-1" });
    }

    [Fact]
    public async Task Resolve_Unreachable_AndNoNeighbours_StaysUnreachable()
    {
        // Wipe neighbours so the flood has no destinations. Without
        // anyone to send to, flooding is pointless; return Unreachable.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("delete from neighbours");
        }

        var decision = await algorithm.ResolveAsync(OutboundFor("G0XYZ"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task FloodSeen_Dedup_FirstObservationRecorded_SecondReturnsTrue()
    {
        const string id = "0000001";
        const string fromCall = "G0NA-1";

        (await context.HasSeenFloodAsync(id, fromCall, TestContext.Current.CancellationToken)).Should().BeFalse();

        await context.RecordFloodSeenAsync(id, fromCall, TestContext.Current.CancellationToken);

        (await context.HasSeenFloodAsync(id, fromCall, TestContext.Current.CancellationToken)).Should().BeTrue();
        // Different upstream - separate dedup row.
        (await context.HasSeenFloodAsync(id, "G0NB-1", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task FloodSeen_RecordIsIdempotent()
    {
        const string id = "0000002";
        const string fromCall = "G0NA-1";

        await context.RecordFloodSeenAsync(id, fromCall, TestContext.Current.CancellationToken);
        // Should not throw on second insert.
        await context.RecordFloodSeenAsync(id, fromCall, TestContext.Current.CancellationToken);

        (await context.HasSeenFloodAsync(id, fromCall, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
