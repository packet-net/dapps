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
/// Tests for the AODV-flavoured passive-learning algorithm. The
/// contract: every inbound message whose F1 originator is reachable
/// via the link source teaches the receiver "to reach <c>originator</c>,
/// send via the neighbour <c>linkSource</c>." The static resolver's
/// precedence is preserved - manual neighbour / discovered peer /
/// route hint always win; learned routes only fill in where those
/// give up.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class PassiveLearningAlgorithmTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseRoutingContext context = null!;
    private PassiveLearningAlgorithm algorithm = null!;

    private const string OurCallsign = "G0US-1";
    private const string OurBaseCallsign = "G0US";
    private const string DirectNeighbour = "G0NB-1";          // direct neighbour
    private const string DirectNeighbourBase = "G0NB";
    private const string DistantOriginator = "G0OR-1";        // multi-hop away from us
    private const string DistantOriginatorBase = "G0OR";

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-passlearn-{Guid.NewGuid():N}.db");
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
            // Pre-wire DirectNeighbour as a manual neighbour so the
            // algorithm has somewhere to send messages it learns about.
            c.Insert(new DbNeighbour
            {
                Callsign = DirectNeighbour,
                UdpEndpoint = "127.0.0.1:65535",
            });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = OurCallsign,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        context = new DatabaseRoutingContext(database, optionsMonitor);
        var staticAlgo = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        algorithm = new PassiveLearningAlgorithm(staticAlgo, NullLogger<PassiveLearningAlgorithm>.Instance);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static BackhaulMessage InboundFrom(string originator) => new(
        Id: "0000001",
        Destination: $"chat@{OurCallsign}",   // arbitrary; ObserveInbound doesn't gate on destination
        Salt: 1L,
        Ttl: 600,
        Payload: "x"u8.ToArray(),
        Originator: originator);

    private static DbMessage OutboundFor(string destinationBaseCallsign) => new()
    {
        Id = "0000002",
        Destination = $"chat@{destinationBaseCallsign}-1",
        Payload = "x"u8.ToArray(),
    };

    // ── ObserveInbound learning rules ──────────────────────────────

    [Fact]
    public async Task ObserveInbound_TeachesReverseRoute_WhenOriginatorAndLinkSourceDiffer()
    {
        await algorithm.ObserveInboundAsync(
            InboundFrom(DistantOriginator), DirectNeighbour, context, TestContext.Current.CancellationToken);

        var learned = await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        learned.Should().NotBeNull();
        learned!.NextHopCallsign.Should().Be(DirectNeighbour);
    }

    [Fact]
    public async Task ObserveInbound_IgnoresPreF1Senders_WithNoOriginator()
    {
        var noOriginator = InboundFrom(originator: "") with { Originator = null };

        await algorithm.ObserveInboundAsync(
            noOriginator, DirectNeighbour, context, TestContext.Current.CancellationToken);

        // No row created - we don't know who originated, so we
        // can't claim to have learned a route to anyone.
        var rows = await database.GetLearnedRoutesAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_IgnoresSelfAsOriginator()
    {
        // A message claiming we originated it shouldn't teach us
        // a route to ourselves via some other station.
        await algorithm.ObserveInboundAsync(
            InboundFrom(OurCallsign), DirectNeighbour, context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_IgnoresSingleHopSends_WhereOriginatorIsLinkSource()
    {
        // Direct neighbour sent us a message they originated. Learning
        // a "route to G0NB via G0NB-1" would just duplicate the existing
        // direct-neighbour entry. Static resolution handles direct
        // neighbours; no need to clutter the learned table.
        await algorithm.ObserveInboundAsync(
            InboundFrom(DirectNeighbour), DirectNeighbour, context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_IgnoresLinkSourceThatIsntANeighbour()
    {
        // Hypothetical: bearer somehow delivered from a station we
        // don't have configured. The next-hop wouldn't be usable
        // anyway, so skip the observation.
        await algorithm.ObserveInboundAsync(
            InboundFrom(DistantOriginator), "G0UNK-1", context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveInbound_NewerObservationWithDifferentNextHop_ResetsFailureCounter()
    {
        // Initial learn sets up the route, then we record some failures.
        await algorithm.ObserveInboundAsync(
            InboundFrom(DistantOriginator), DirectNeighbour, context, TestContext.Current.CancellationToken);
        await context.RecordLearnedRouteFailureAsync(DistantOriginatorBase, invalidationThreshold: 99, TestContext.Current.CancellationToken);
        (await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))!
            .ConsecutiveFailures.Should().Be(1);

        // Add a second direct neighbour, then observe an inbound from
        // the same originator but via that other neighbour.
        var newNeighbour = "G0NC-1";
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = newNeighbour, UdpEndpoint = "127.0.0.1:65534" });
        }
        await algorithm.ObserveInboundAsync(
            InboundFrom(DistantOriginator), newNeighbour, context, TestContext.Current.CancellationToken);

        var refreshed = await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        refreshed!.NextHopCallsign.Should().Be(newNeighbour);
        refreshed.ConsecutiveFailures.Should().Be(0,
            "a fresh path observation is fresh evidence of liveness; the old path's failure history is irrelevant to the new one");
    }

    // ── Resolve precedence: static wins, learned fills in ──────────

    [Fact]
    public async Task Resolve_PrefersStaticManualNeighbour_OverLearnedRoute()
    {
        // The destination has BOTH a manual neighbour AND a learned
        // route. The manual entry must win - operator override beats
        // anything we inferred.
        const string explicitCallsign = "G0EX-1";
        const string explicitBase = "G0EX";
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = explicitCallsign, UdpEndpoint = "127.0.0.1:65532" });
        }
        await context.UpsertLearnedRouteAsync(explicitBase, DirectNeighbour, TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(explicitBase), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>();
        ((RouteDecision.NextHop)decision).Route.Callsign.Should().Be(explicitCallsign,
            "manual neighbour wins; learned only fills in when static is Unreachable");
    }

    [Fact]
    public async Task Resolve_FallsThroughToLearnedRoute_WhenStaticIsUnreachable()
    {
        // No manual neighbour, no discovered peer, no hint - just a
        // learned route. The fallback should kick in.
        await context.UpsertLearnedRouteAsync(DistantOriginatorBase, DirectNeighbour, TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(DistantOriginatorBase), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(DirectNeighbour);
    }

    [Fact]
    public async Task Resolve_DiscardsLearnedRoute_WhenNextHopNeighbourDisappeared()
    {
        // Learned a route to a callsign whose next-hop neighbour has
        // since been removed. Algorithm should NOT return a route
        // pointing at a phantom neighbour - drop and report Unreachable.
        await context.UpsertLearnedRouteAsync(DistantOriginatorBase, "G0GONE-1", TestContext.Current.CancellationToken);

        var decision = await algorithm.ResolveAsync(OutboundFor(DistantOriginatorBase), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
        // The dangling row gets cleaned up so it doesn't keep
        // misleading the resolver on subsequent calls.
        (await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Resolve_NoStaticAndNoLearnedRoute_ReturnsUnreachable()
    {
        var decision = await algorithm.ResolveAsync(
            OutboundFor("G0XYZ"), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    // ── ObserveForwardOutcome: failure counting + invalidation ─────

    [Fact]
    public async Task ObserveForwardOutcome_SuccessResetsFailureCounter_ForLearnedRoute()
    {
        await context.UpsertLearnedRouteAsync(DistantOriginatorBase, DirectNeighbour, TestContext.Current.CancellationToken);
        await context.RecordLearnedRouteFailureAsync(DistantOriginatorBase, 99, TestContext.Current.CancellationToken);
        await context.RecordLearnedRouteFailureAsync(DistantOriginatorBase, 99, TestContext.Current.CancellationToken);

        await algorithm.ObserveForwardOutcomeAsync(
            OutboundFor(DistantOriginatorBase),
            new BackhaulRoute(DirectNeighbour, UdpEndpoint: "127.0.0.1:65535"),
            BackhaulSendResult.Ok(),
            context, TestContext.Current.CancellationToken);

        (await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))!
            .ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task ObserveForwardOutcome_FailureCountIncrements_AndInvalidatesAtThreshold()
    {
        await context.UpsertLearnedRouteAsync(DistantOriginatorBase, DirectNeighbour, TestContext.Current.CancellationToken);

        for (var i = 1; i <= PassiveLearningAlgorithm.InvalidationThreshold; i++)
        {
            await algorithm.ObserveForwardOutcomeAsync(
                OutboundFor(DistantOriginatorBase),
                new BackhaulRoute(DirectNeighbour, UdpEndpoint: "127.0.0.1:65535"),
                BackhaulSendResult.Fail("timeout"),
                context, TestContext.Current.CancellationToken);
        }

        // Threshold hit on the third failure - row deleted.
        (await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task ObserveForwardOutcome_IgnoresFailures_WhenTheAttemptedRouteIsntTheLearnedOne()
    {
        // Forward failed, but it went via a different route (e.g. a
        // manual neighbour with the same destination base callsign).
        // The learned-route entry shouldn't get penalised for someone
        // else's failure.
        await context.UpsertLearnedRouteAsync(DistantOriginatorBase, DirectNeighbour, TestContext.Current.CancellationToken);

        await algorithm.ObserveForwardOutcomeAsync(
            OutboundFor(DistantOriginatorBase),
            new BackhaulRoute("G0OTHER-1", UdpEndpoint: "127.0.0.1:65530"),
            BackhaulSendResult.Fail("not via learned path"),
            context, TestContext.Current.CancellationToken);

        (await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken))!
            .ConsecutiveFailures.Should().Be(0);
    }

    // ── ObserveProbeOutcome - B6.1 follow-up ───────────────────────

    private static dapps.client.DappsProtocolClient.DiscoveredPeerInfo Peer(string callsign, string source = "n", int? bearerPort = null)
        => new(callsign, source, bearerPort);

    [Fact]
    public async Task ObserveProbeOutcome_TeachesLearnedRoute_ForEachReportedPeer()
    {
        var peers = new[]
        {
            Peer(DistantOriginator),
            Peer("G0OTH-7"),
        };

        await algorithm.ObserveProbeOutcomeAsync(
            DirectNeighbour, peers, context, TestContext.Current.CancellationToken);

        var learnedDistant = await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        learnedDistant.Should().NotBeNull();
        learnedDistant!.NextHopCallsign.Should().Be(DirectNeighbour);

        var learnedOther = await context.GetLearnedRouteAsync("G0OTH", TestContext.Current.CancellationToken);
        learnedOther.Should().NotBeNull();
        learnedOther!.NextHopCallsign.Should().Be(DirectNeighbour);
    }

    [Fact]
    public async Task ObserveProbeOutcome_SkipsAskedPeerThatIsntANeighbour()
    {
        // Hypothetical: the probe scheduler probed via a discovered
        // peer that doesn't have a manual DbNeighbour row. Resolver
        // would discard the learned route at use-time anyway, so we
        // skip the insertion to avoid dead rows.
        await algorithm.ObserveProbeOutcomeAsync(
            "G0NONEIGH-1",
            new[] { Peer(DistantOriginator) },
            context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveProbeOutcome_SkipsSelfReportedAsPeer()
    {
        // The asked peer always reports us as a peer (we just talked
        // to them). Recording "to reach myself, send via the asked
        // peer" would be wrong.
        await algorithm.ObserveProbeOutcomeAsync(
            DirectNeighbour,
            new[] { Peer(OurCallsign) },
            context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveProbeOutcome_SkipsPeerThatIsTheAskedPeer()
    {
        // Peer's base callsign matches the asked peer's base. Single-
        // hop self-loop - the direct-neighbour entry already covers
        // this; learned-route would just duplicate.
        await algorithm.ObserveProbeOutcomeAsync(
            DirectNeighbour,
            new[] { Peer(DirectNeighbour) },
            context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveProbeOutcome_EmptyPeers_NoOp()
    {
        await algorithm.ObserveProbeOutcomeAsync(
            DirectNeighbour,
            Array.Empty<dapps.client.DappsProtocolClient.DiscoveredPeerInfo>(),
            context, TestContext.Current.CancellationToken);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveProbeOutcome_OverwritesPreviousLearnedRoute_FreshEvidence()
    {
        // First, learn a route via a passive observation.
        var altNeighbour = "G0ALT-1";
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = altNeighbour, UdpEndpoint = "127.0.0.1:65531" });
        }
        await algorithm.ObserveInboundAsync(
            InboundFrom(DistantOriginator), altNeighbour, context, TestContext.Current.CancellationToken);

        // Probe via DirectNeighbour returns the same originator as a
        // peer. That's fresher evidence (we just talked to
        // DirectNeighbour) - the next-hop should switch.
        await algorithm.ObserveProbeOutcomeAsync(
            DirectNeighbour,
            new[] { Peer(DistantOriginator) },
            context, TestContext.Current.CancellationToken);

        var learned = await context.GetLearnedRouteAsync(DistantOriginatorBase, TestContext.Current.CancellationToken);
        learned!.NextHopCallsign.Should().Be(DirectNeighbour,
            "an active probe is fresher evidence than a passive observation; the next-hop should switch");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
