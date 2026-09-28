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

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static DbMessage OutboundFor(string destBaseCallsign, byte? floodHopsRemaining = null, string sourceCallsign = "") => new()
    {
        Id = "0000001",
        Destination = $"chat@{destBaseCallsign}-1",
        Payload = "x"u8.ToArray(),
        SourceCallsign = sourceCallsign,
        FloodHopsRemaining = floodHopsRemaining,
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
