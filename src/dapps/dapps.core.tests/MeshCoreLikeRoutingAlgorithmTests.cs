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
/// Tests for the MeshCore-flavoured DSR-with-passive-discovery
/// algorithm. Two distinct contracts:
///
/// <list type="bullet">
/// <item>Discovery: every inbound flood-discovery message
///   (TraversedHops set) teaches the receiver "to reach
///   <c>originator</c>, send via [TraversedHops reversed]".</item>
/// <item>Source-routed delivery: when a discovered path exists, the
///   algorithm embeds it in the outbound NextHop's SourceRoute so
///   downstream forwarders relay along the prescribed route without
///   re-deciding.</item>
/// </list>
///
/// The static-precedence contract (manual neighbour wins) is
/// inherited from the wrapped inner algorithm and exercised here for
/// the meshcore stack.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MeshCoreLikeRoutingAlgorithmTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseRoutingContext context = null!;
    private MeshCoreLikeRoutingAlgorithm algorithm = null!;

    private const string OurCallsign = "G0US-1";
    private const string OurBaseCallsign = "G0US";
    private const string DirectNeighbour1 = "G0NB1-1";
    private const string DirectNeighbour1Base = "G0NB1";
    private const string DirectNeighbour2 = "G0NB2-1";
    private const string DirectNeighbour2Base = "G0NB2";
    private const string DistantOriginator = "G0OR-1";
    private const string DistantOriginatorBase = "G0OR";

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-meshcore-{Guid.NewGuid():N}.db");
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
            c.CreateTable<DbDiscoveredPath>();
            c.Insert(new DbNeighbour { Callsign = DirectNeighbour1, UdpEndpoint = "127.0.0.1:65535" });
            c.Insert(new DbNeighbour { Callsign = DirectNeighbour2, UdpEndpoint = "127.0.0.1:65534" });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = OurCallsign,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        context = new DatabaseRoutingContext(database, optionsMonitor);
        var staticAlgo = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        algorithm = new MeshCoreLikeRoutingAlgorithm(staticAlgo, NullLogger<MeshCoreLikeRoutingAlgorithm>.Instance);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static BackhaulMessage InboundFlood(string originator, IReadOnlyList<string> traversedHops, byte hopsRemaining = 5) => new(
        Id: "0000001",
        Destination: $"chat@{OurCallsign}",
        Salt: 1L,
        Ttl: 600,
        Payload: "x"u8.ToArray(),
        Originator: originator,
        FloodHopsRemaining: hopsRemaining,
        TraversedHops: traversedHops);

    private static DbMessage OutboundFor(string destinationBaseCallsign) => new()
    {
        Id = "0000002",
        Destination = $"chat@{destinationBaseCallsign}-1",
        Payload = "x"u8.ToArray(),
    };

    // ── ObserveInbound: path discovery from TraversedHops ──────────

    [Fact]
    public async Task ObserveInbound_StoresReversePath_FromTraversedHops()
    {
        // A flood originated by G0OR-1 reaches us after passing
        // through G0HOP1 then G0HOP2. The reverse path back to G0OR
        // from us is [G0HOP2, G0HOP1, G0OR] in terms of forward
        // routing - so intermediates = [G0HOP2, G0HOP1] and the
        // destination is G0OR.
        var traversed = new[] { "G0HOP1-1", "G0HOP2-1" };
        await algorithm.ObserveInboundAsync(
            InboundFlood(DistantOriginator, traversed),
            DirectNeighbour1, context, TestContext.Current.CancellationToken);

        var path = await context.GetDiscoveredPathAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        path.Should().NotBeNull();
        path!.GetIntermediates().Should().Equal("G0HOP2-1", "G0HOP1-1");
    }

    [Fact]
    public async Task ObserveInbound_DirectNeighbourOriginatedFlood_StoresEmptyIntermediates()
    {
        // Direct neighbour originated; no intermediates between us
        // and them. The discovered path's intermediates should be
        // empty (= "next hop is the destination itself").
        await algorithm.ObserveInboundAsync(
            InboundFlood(DirectNeighbour1, traversedHops: []),
            DirectNeighbour1, context, TestContext.Current.CancellationToken);

        var path = await context.GetDiscoveredPathAsync(DirectNeighbour1Base, TestContext.Current.CancellationToken);
        path.Should().NotBeNull();
        path!.GetIntermediates().Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_NonFloodMessage_DoesNotPopulatePathTable()
    {
        // A regular routed message (no TraversedHops) tells us
        // nothing about the originator's path - we have no record
        // of how it got here. Don't fabricate a discovered path.
        var routed = new BackhaulMessage(
            Id: "0000001",
            Destination: $"chat@{OurCallsign}",
            Salt: null,
            Ttl: 60,
            Payload: "x"u8.ToArray(),
            Originator: DistantOriginator);

        await algorithm.ObserveInboundAsync(routed, DirectNeighbour1, context, TestContext.Current.CancellationToken);

        (await database.GetDiscoveredPathsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_IgnoresFloodWithSelfAsOriginator()
    {
        await algorithm.ObserveInboundAsync(
            InboundFlood(OurCallsign, traversedHops: new[] { "G0X-1" }),
            DirectNeighbour1, context, TestContext.Current.CancellationToken);

        (await database.GetDiscoveredPathsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_FiltersOurOwnCallsign_OutOfReversePath()
    {
        // Pathological: a misbehaving peer included our callsign in
        // the TraversedHops. Defensively strip it from the reverse -
        // including ourselves in our own outbound source route would
        // loop the message right back.
        var traversed = new[] { "G0HOP1-1", OurCallsign, "G0HOP2-1" };

        await algorithm.ObserveInboundAsync(
            InboundFlood(DistantOriginator, traversed),
            DirectNeighbour1, context, TestContext.Current.CancellationToken);

        var path = await context.GetDiscoveredPathAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        path!.GetIntermediates().Should().Equal("G0HOP2-1", "G0HOP1-1");
    }

    // ── Resolve precedence: inner wins, discovered path fills in ──

    [Fact]
    public async Task Resolve_PrefersStaticManualNeighbour_OverDiscoveredPath()
    {
        // Both a manual neighbour and a discovered path exist. The
        // manual entry must win - operator override beats inferred.
        const string explicitCallsign = "G0EX-1";
        const string explicitBase = "G0EX";
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = explicitCallsign, UdpEndpoint = "127.0.0.1:65532" });
        }
        await context.UpsertDiscoveredPathAsync(
            explicitBase, new[] { DirectNeighbour1, "G0XX-1" }, TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(explicitBase), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(explicitCallsign);
        nh.SourceRoute.Should().BeNull("manual neighbour delivers direct; no source route");
    }

    [Fact]
    public async Task Resolve_EmbedsSourceRoute_WhenStaticUnreachableAndPathDiscovered()
    {
        // Stored discovered path: to reach G0OR, go via [G0NB1-1, G0X-1].
        // The next hop is G0NB1-1; the remaining-after-this-hop list
        // is [G0X-1].
        await context.UpsertDiscoveredPathAsync(
            DistantOriginatorBase,
            new[] { DirectNeighbour1, "G0X-1" },
            TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(DistantOriginatorBase), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(DirectNeighbour1);
        nh.SourceRoute.Should().NotBeNull();
        nh.SourceRoute!.Should().Equal("G0X-1");
    }

    [Fact]
    public async Task Resolve_EmptyDiscoveredPath_TreatsDestinationAsDirect()
    {
        // Path was learned with no intermediates (destination is a
        // direct neighbour from the originator's perspective). When
        // we relay, the next hop IS the destination.
        await context.UpsertDiscoveredPathAsync(
            DirectNeighbour1Base, intermediates: [], TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(DirectNeighbour1Base), context, TestContext.Current.CancellationToken);

        // Inner sees DirectNeighbour1 as a manual neighbour and wins
        // before discovered-path resolution kicks in. So the path
        // here is exercised by the static layer; assert that's what
        // happens (not a SourceRoute from the meshcore layer).
        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(DirectNeighbour1);
    }

    [Fact]
    public async Task Resolve_NoStaticAndNoPath_OriginatesFloodDiscovery()
    {
        var decision = await algorithm.ResolveAsync(OutboundFor("G0XYZ"), context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(MeshCoreLikeRoutingAlgorithm.DefaultHopBudget);
        flood.TraversedHops.Should().NotBeNull();
        flood.TraversedHops!.Should().BeEmpty(
            "originator emits an empty traversal record; transit nodes append themselves before re-flooding");
        flood.Routes.Select(r => r.Callsign).Should().BeEquivalentTo(new[] { DirectNeighbour1, DirectNeighbour2 });
    }

    [Fact]
    public async Task Resolve_DiscardsDiscoveredPath_WhenFirstHopNotInNeighbours()
    {
        // Discovered path's first intermediate isn't in our neighbour
        // table. Algorithm should flag the path as unusable and
        // either fall through to flood or report Unreachable. With
        // direct neighbours present, we'll get a flood-discovery.
        await context.UpsertDiscoveredPathAsync(
            DistantOriginatorBase, new[] { "G0GONE-1", "G0X-1" }, TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(DistantOriginatorBase), context, TestContext.Current.CancellationToken);

        // The dangling path should be invalidated so it doesn't keep
        // misleading the resolver. We don't return Unreachable from
        // here because a neighbour list exists for flood-discovery.
        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>(
            "first-hop missing → invalidate path → fall through to flood");
    }

    // ── Source-routed in transit ──────────────────────────────────

    [Fact]
    public async Task Resolve_SourceRoutedMessage_StripsHeadAndForwards()
    {
        // Persisted message carries SourceRouteCsv = "G0NB1-1,G0X-1".
        // Algorithm pulls G0NB1-1 as next hop, leaves G0X-1 as the
        // remainder for the receiver to follow.
        var msg = OutboundFor(DistantOriginatorBase);
        msg = new DbMessage
        {
            Id = msg.Id,
            Destination = msg.Destination,
            Payload = msg.Payload,
            SourceRouteCsv = $"{DirectNeighbour1},G0X-1",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(DirectNeighbour1);
        nh.SourceRoute.Should().NotBeNull();
        nh.SourceRoute!.Should().Equal("G0X-1");
    }

    [Fact]
    public async Task Resolve_SourceRoutedWithEmptyRoute_TreatsDestinationAsNextHop()
    {
        // Persisted with SourceRouteCsv = "" - message arrived with
        // an empty SourceRoute (last intermediate already stripped).
        // Algorithm should treat the destination's callsign as the
        // next hop. Manual neighbour for that callsign must exist.
        var msg = new DbMessage
        {
            Id = "0000002",
            Destination = $"chat@{DirectNeighbour1Base}-1",
            Payload = "x"u8.ToArray(),
            SourceRouteCsv = "",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(DirectNeighbour1);
        nh.SourceRoute.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_SourceRoutedNextHopMissing_ReturnsUnreachable()
    {
        var msg = new DbMessage
        {
            Id = "0000002",
            Destination = "chat@G0DEAD-1",
            Payload = "x"u8.ToArray(),
            SourceRouteCsv = "G0GONE-1,G0X-1",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    // ── In-flight flood propagation ───────────────────────────────

    [Fact]
    public async Task Resolve_InFlightFlood_AppendsLocalCallsignAndReFloodsToUntraversedNeighbours()
    {
        // Inbound flood persisted with TraversedHopsCsv = "G0OR-1"
        // (originator was the only node before us). We should:
        //   - decrement hop budget from 5 to 4
        //   - append OurCallsign to the outbound TraversedHops
        //   - re-flood to neighbours not yet visited
        var msg = new DbMessage
        {
            Id = "0000003",
            Destination = "chat@G0FAR-1",
            Payload = "x"u8.ToArray(),
            FloodHopsRemaining = 5,
            TraversedHopsCsv = DistantOriginator,
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.HopBudget.Should().Be(4);
        flood.TraversedHops.Should().NotBeNull();
        flood.TraversedHops!.Should().Equal(DistantOriginator, OurCallsign);
        flood.Routes.Select(r => r.Callsign).Should().BeEquivalentTo(
            new[] { DirectNeighbour1, DirectNeighbour2 },
            "neither direct neighbour is in the traversed set, so both get re-flooded");
    }

    [Fact]
    public async Task Resolve_InFlightFlood_ExcludesTraversedNeighboursFromNextWave()
    {
        // Inbound flood already visited G0NB1-1; we shouldn't re-flood
        // back to it. Only G0NB2-1 remains as a candidate.
        var msg = new DbMessage
        {
            Id = "0000003",
            Destination = "chat@G0FAR-1",
            Payload = "x"u8.ToArray(),
            FloodHopsRemaining = 4,
            TraversedHopsCsv = $"{DistantOriginator},{DirectNeighbour1}",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        var flood = decision.Should().BeOfType<RouteDecision.FloodToNeighbours>().Subject;
        flood.Routes.Select(r => r.Callsign).Should().Equal(DirectNeighbour2);
    }

    [Fact]
    public async Task Resolve_InFlightFlood_HopBudgetExhausted_ReturnsUnreachable()
    {
        var msg = new DbMessage
        {
            Id = "0000003",
            Destination = "chat@G0FAR-1",
            Payload = "x"u8.ToArray(),
            FloodHopsRemaining = 0,
            TraversedHopsCsv = $"{DistantOriginator}",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Resolve_InFlightFlood_AllNeighboursAlreadyTraversed_ReturnsUnreachable()
    {
        var msg = new DbMessage
        {
            Id = "0000003",
            Destination = "chat@G0FAR-1",
            Payload = "x"u8.ToArray(),
            FloodHopsRemaining = 4,
            TraversedHopsCsv = $"{DirectNeighbour1},{DirectNeighbour2}",
        };

        var decision = await algorithm.ResolveAsync(msg, context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    // ── ObserveForwardOutcome on discovered paths ─────────────────

    [Fact]
    public async Task ObserveForwardOutcome_SuccessAlongDiscoveredPath_ResetsFailureCounter()
    {
        await context.UpsertDiscoveredPathAsync(
            DistantOriginatorBase, new[] { DirectNeighbour1, "G0X-1" }, TestContext.Current.CancellationToken);
        await context.RecordDiscoveredPathFailureAsync(DistantOriginatorBase, 99, TestContext.Current.CancellationToken);
        await context.RecordDiscoveredPathFailureAsync(DistantOriginatorBase, 99, TestContext.Current.CancellationToken);

        await algorithm.ObserveForwardOutcomeAsync(
            OutboundFor(DistantOriginatorBase),
            new BackhaulRoute(DirectNeighbour1, UdpEndpoint: "127.0.0.1:65535"),
            BackhaulSendResult.Ok(),
            context, TestContext.Current.CancellationToken);

        (await context.GetDiscoveredPathAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))!
            .ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task ObserveForwardOutcome_FailuresInvalidatePathAtThreshold()
    {
        await context.UpsertDiscoveredPathAsync(
            DistantOriginatorBase, new[] { DirectNeighbour1, "G0X-1" }, TestContext.Current.CancellationToken);

        for (var i = 1; i <= MeshCoreLikeRoutingAlgorithm.InvalidationThreshold; i++)
        {
            await algorithm.ObserveForwardOutcomeAsync(
                OutboundFor(DistantOriginatorBase),
                new BackhaulRoute(DirectNeighbour1, UdpEndpoint: "127.0.0.1:65535"),
                BackhaulSendResult.Fail("timeout"),
                context, TestContext.Current.CancellationToken);
        }

        (await context.GetDiscoveredPathAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task ObserveForwardOutcome_IgnoresFailures_WhenAttemptedRouteIsntPathHead()
    {
        // Forward via a different next-hop (e.g. operator override
        // routed through a manual neighbour). Discovered-path stats
        // shouldn't get penalised for someone else's failure.
        await context.UpsertDiscoveredPathAsync(
            DistantOriginatorBase, new[] { DirectNeighbour1, "G0X-1" }, TestContext.Current.CancellationToken);

        await algorithm.ObserveForwardOutcomeAsync(
            OutboundFor(DistantOriginatorBase),
            new BackhaulRoute(DirectNeighbour2, UdpEndpoint: "127.0.0.1:65534"),
            BackhaulSendResult.Fail("not via discovered head"),
            context, TestContext.Current.CancellationToken);

        (await context.GetDiscoveredPathAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))!
            .ConsecutiveFailures.Should().Be(0);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
