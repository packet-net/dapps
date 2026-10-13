using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Routing files what it learns under the full callsign, SSID included
/// (#242): G6FCI-7 and G6FCI-8 are two stations, each with its own
/// gossiped route, learned route and discovered path. Before, all three
/// were keyed by base callsign, so one SSID's route overwrote the
/// other's, floods from one SSID were never offered to another, and
/// two SSIDs of one callsign behind a relay could not reach each other.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class RoutesByFullCallsignTests : IAsyncLifetime
{
    private const string Seven = "G6FCI-7";
    private const string Eight = "G6FCI-8";
    private const string RelayOne = "M0RLY-1";
    private const string RelayTwo = "M0RLY-2";

    private string dbPath = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;
    private Database database = null!;
    private DatabaseRoutingContext context = null!;
    private CancellationToken ct;

    public ValueTask InitializeAsync()
    {
        ct = TestContext.Current.CancellationToken;
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-fullcall-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbLearnedRoute>();
            c.CreateTable<DbDiscoveredPath>();
            c.CreateTable<DbFloodSeen>();
            c.CreateTable<DbRouteGossipState>();
        }
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "M0US-1" });
        database = new Database(NullLogger<Database>.Instance, options);
        context = new DatabaseRoutingContext(database, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    // -- routes gossip --

    [Fact]
    public async Task Gossip_AdvertisesEachSsidOnItsOwnLine()
    {
        await database.UpsertNeighbour(Seven, bearerPort: 1);
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        await database.UpsertLearnedRouteAsync("M0FAR-3", Seven, DateTime.UtcNow);
        await database.RecordLearnedRouteSuccessAsync("M0FAR-3", DateTime.UtcNow);

        var lines = (await RoutesAnswerAsync()).Split('\n');

        lines.Should().Contain($"route {Seven} hops=1");
        lines.Should().Contain($"route {Eight} hops=1");
        lines.Should().Contain(l => l.StartsWith("route M0FAR-3 hops=2 "));
        lines.Should().NotContain(l => l.StartsWith("route G6FCI "));
    }

    [Fact]
    public async Task Gossip_WritesSsidZeroAsTheBareCallsign()
    {
        await database.UpsertNeighbour("m0bar-0", bearerPort: 1);

        (await RoutesAnswerAsync()).Split('\n').Should().Contain("route M0BAR hops=1");
    }

    [Fact]
    public async Task Gossip_RoundTrip_KeepsBothSsidsApart_AndRoutesEachTheRightWay()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        var port = new RouteGossipPort(database, options, TimeProvider.System, NullLogger<RouteGossipPort>.Instance);

        await port.ImportAsync(RelayOne, [DappsProtocolClient.ParseRouteLine($"route {Seven} hops=1")!], ct);
        await port.ImportAsync(RelayTwo, [DappsProtocolClient.ParseRouteLine($"route {Eight} hops=1")!], ct);

        (await database.GetLearnedRoutesAsync()).Select(r => (r.DestinationCallsign, r.NextHopCallsign))
            .Should().BeEquivalentTo([(Seven, RelayOne), (Eight, RelayTwo)]);
        (await NextHopAsync(Passive(), $"chat@{Seven}")).Should().Be(RelayOne);
        (await NextHopAsync(Passive(), $"chat@{Eight}")).Should().Be(RelayTwo);
    }

    [Fact]
    public async Task Gossip_ARouteToUs_IsNotImported()
    {
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        var port = new RouteGossipPort(database, options, TimeProvider.System, NullLogger<RouteGossipPort>.Instance);

        await port.ImportAsync(RelayOne,
            [DappsProtocolClient.ParseRouteLine($"route {Seven} hops=1")!, DappsProtocolClient.ParseRouteLine($"route {Eight} hops=1")!], ct);

        (await database.GetLearnedRoutesAsync()).Select(r => r.DestinationCallsign).Should().Equal(Eight);
    }

    // -- learned routes --

    [Fact]
    public async Task LearnedRoutes_ForTwoSsids_DoNotOverwriteEachOther()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From(Seven), RelayOne, context, ct);
        await passive.ObserveInboundAsync(From(Eight), RelayTwo, context, ct);

        (await NextHopAsync(passive, $"chat@{Seven}")).Should().Be(RelayOne);
        (await NextHopAsync(passive, $"chat@{Eight}")).Should().Be(RelayTwo);
    }

    [Fact]
    public async Task LearnedRoutes_AreFoundWhateverTheCaseOrSsidZeroSpelling()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From("m0abc-0"), RelayOne, context, ct);

        (await database.GetLearnedRoutesAsync()).Select(r => r.DestinationCallsign).Should().Equal("M0ABC");
        (await NextHopAsync(passive, "chat@M0ABC")).Should().Be(RelayOne);
        (await NextHopAsync(passive, "chat@M0ABC-1")).Should().BeNull("a route to M0ABC is a route to SSID 0 only");
    }

    [Fact]
    public async Task Learning_TakesASiblingOfOurs_LikeAnyoneElse()
    {
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From(Eight), RelayOne, context, ct);
        await passive.ObserveInboundAsync(From(Seven), RelayOne, context, ct);

        (await database.GetLearnedRoutesAsync()).Select(r => r.DestinationCallsign).Should().Equal(Eight);
        (await NextHopAsync(passive, $"chat@{Eight}")).Should().Be(RelayOne);
    }

    [Fact]
    public async Task Learning_FromAProbe_TakesASiblingOfOurs_ButNeverUs()
    {
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);

        await Passive().ObserveProbeOutcomeAsync(RelayOne,
            [new(Seven, "n", 1), new(Eight, "n", 1), new(RelayOne, "n", 1)], context, ct);

        (await database.GetLearnedRoutesAsync()).Select(r => (r.DestinationCallsign, r.NextHopCallsign))
            .Should().Equal((Eight, RelayOne));
    }

    [Fact]
    public async Task Learning_SkipsOnlyASingleHopFromTheExactStation()
    {
        // G6FCI-8 handed us mail G6FCI-5 wrote: that is a route to G6FCI-5
        // through G6FCI-8, not a single hop. (Not G6FCI-7: 7 and 8 are
        // also how BPQ presents each other, which the next tests cover.)
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From(Eight), Eight, context, ct);
        await passive.ObserveInboundAsync(From("G6FCI-5"), Eight, context, ct);

        (await database.GetLearnedRoutesAsync()).Select(r => (r.DestinationCallsign, r.NextHopCallsign))
            .Should().Equal(("G6FCI-5", Eight));
    }

    [Fact]
    public async Task Learning_FromALinkThroughANode_GoesViaTheNeighbourRowItCameFrom()
    {
        // A BPQ node makes a downlink under the caller's callsign with the
        // SSID inverted (15 - n), so mail from neighbour M0NBR-9, sent by
        // a connect script through a node, arrives from M0NBR-6.
        await database.UpsertNeighbour("M0NBR-9", bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From("G0FAR-7"), "M0NBR-6", context, ct);
        await passive.ObserveInboundAsync(From("M0NBR-9"), "M0NBR-6", context, ct);

        (await database.GetLearnedRoutesAsync()).Select(r => (r.DestinationCallsign, r.NextHopCallsign))
            .Should().Equal(("G0FAR-7", "M0NBR-9"));
    }

    [Fact]
    public async Task Learning_NeverTakesASiblingOfOursAsTheLinksNeighbourRow()
    {
        // A link source under our own callsign maps only to its exact row,
        // as in routing: a sibling is no stand-in for another SSID of ours.
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(Eight, bearerPort: 1);

        await Passive().ObserveInboundAsync(From("G0FAR-7"), "G6FCI-6", context, ct);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Learning_AtARelay_TakesASsidInvertedLinkSourceAsTheOriginatorItself()
    {
        // The relay has G6FCI-7 as a neighbour but not G6FCI-8. G6FCI-8
        // calls through a BPQ node, which inverts its SSID (15 - 8 = 7), so
        // its own mail arrives from "G6FCI-7". That is a single hop from
        // G6FCI-8, not a route to it via G6FCI-7: learning (and gossiping)
        // one would send G6FCI-8's mail to G6FCI-7 and back, as a repeat.
        await database.UpsertNeighbour(Seven, bearerPort: 1);
        var passive = Passive();

        await passive.ObserveInboundAsync(From(Eight), Seven, context, ct);

        (await database.GetLearnedRoutesAsync()).Should().BeEmpty();
    }

    // -- route hints --

    [Fact]
    public async Task RouteHint_WithoutAnSsid_CoversEverySsidOfTheCallsign()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        InsertHint("G6FCI", RelayOne);

        (await NextHopAsync(Static(), $"chat@{Eight}")).Should().Be(RelayOne);
        (await NextHopAsync(Static(), "chat@G6FCI")).Should().Be(RelayOne);
        (await NextHopAsync(Static(), "chat@G6FCIX-8")).Should().BeNull();
    }

    [Fact]
    public async Task RouteHint_WithAnSsid_CoversThatStationOnly_AndBeatsABareOne()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        InsertHint("G6FCI", RelayOne);
        InsertHint(Eight, RelayTwo);

        (await NextHopAsync(Static(), $"chat@{Eight}")).Should().Be(RelayTwo);
        (await NextHopAsync(Static(), "chat@g6fci-8")).Should().Be(RelayTwo);
        (await NextHopAsync(Static(), $"chat@{Seven}")).Should().Be(RelayOne);
    }

    [Fact]
    public async Task RouteHint_WithAnSsid_DoesNotCoverItsSiblings()
    {
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        InsertHint(Eight, RelayTwo);

        (await NextHopAsync(Static(), $"chat@{Seven}")).Should().BeNull();
        (await NextHopAsync(Static(), "chat@G6FCI")).Should().BeNull();
    }

    [Fact]
    public async Task RouteHint_Wildcard_StillCatchesTheRest()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        InsertHint("*", RelayOne);
        InsertHint(Eight, RelayTwo);

        (await NextHopAsync(Static(), $"chat@{Eight}")).Should().Be(RelayTwo);
        (await NextHopAsync(Static(), $"chat@{Seven}")).Should().Be(RelayOne);
    }

    // -- floods --

    [Fact]
    public async Task Flood_FromG6fci7_IsPassedOnToG6fci8()
    {
        await database.UpsertNeighbour(Seven, bearerPort: 1);
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        await database.UpsertNeighbour("M0OTH-1", bearerPort: 1);
        var flood = new FloodFallbackAlgorithm(Passive(), NullLogger<FloodFallbackAlgorithm>.Instance);

        var decision = await flood.ResolveAsync(InFlood("chat@M0FAR-1", linkSource: Seven), context, ct);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>()
            .Which.Routes.Select(r => r.Callsign).Should().BeEquivalentTo([Eight, "M0OTH-1"]);
    }

    [Fact]
    public async Task Flood_FromANeighbourEnteredWithoutItsSsid_StillDoesNotGoBack()
    {
        // The link source is the caller's DAPPS callsign; a neighbour row
        // entered without the right SSID stands in for it, as in routing.
        await database.UpsertNeighbour("M0NBR", bearerPort: 1);
        await database.UpsertNeighbour("M0OTH-1", bearerPort: 1);
        var flood = new FloodFallbackAlgorithm(Passive(), NullLogger<FloodFallbackAlgorithm>.Instance);

        var decision = await flood.ResolveAsync(InFlood("chat@M0FAR-1", linkSource: "M0NBR-7"), context, ct);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>()
            .Which.Routes.Select(r => r.Callsign).Should().Equal("M0OTH-1");
    }

    [Fact]
    public async Task Flood_FromASibling_ThroughABpqNode_IsNotSentBackToIt()
    {
        // G6FCI-8's flood reaches us, G6FCI-7, through a BPQ node, which
        // inverts its SSID: the link source is our own callsign. The flood
        // still mustn't go back to its originator.
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        await database.UpsertNeighbour("M0OTH-1", bearerPort: 1);
        var flood = new FloodFallbackAlgorithm(Passive(), NullLogger<FloodFallbackAlgorithm>.Instance);

        var decision = await flood.ResolveAsync(InFlood("chat@M0FAR-1", linkSource: Seven, originator: Eight), context, ct);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>()
            .Which.Routes.Select(r => r.Callsign).Should().Equal("M0OTH-1");
    }

    [Fact]
    public async Task MeshCoreFlood_IsNotSentBackToItsOriginator()
    {
        // The originator starts a flood with nothing traversed, so the first
        // node it reaches has only the originator to keep it from.
        options.CurrentValue.Callsign = Seven;
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        await database.UpsertNeighbour("M0OTH-1", bearerPort: 1);

        var decision = await MeshCore().ResolveAsync(
            InFlood("chat@M0FAR-1", linkSource: Seven, traversed: "", originator: Eight), context, ct);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>()
            .Which.Routes.Select(r => r.Callsign).Should().Equal("M0OTH-1");
    }

    [Fact]
    public async Task MeshCoreFlood_ThroughG6fci7_IsPassedOnToG6fci8()
    {
        await database.UpsertNeighbour(Seven, bearerPort: 1);
        await database.UpsertNeighbour(Eight, bearerPort: 1);
        var mesh = MeshCore();

        var decision = await mesh.ResolveAsync(InFlood("chat@M0FAR-1", linkSource: Seven, traversed: Seven), context, ct);

        decision.Should().BeOfType<RouteDecision.FloodToNeighbours>()
            .Which.Routes.Select(r => r.Callsign).Should().Equal(Eight);
    }

    [Fact]
    public async Task MeshCoreFlood_FromASibling_TeachesThePathToIt()
    {
        options.CurrentValue.Callsign = Eight;
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        var mesh = MeshCore();

        await mesh.ObserveInboundAsync(From(Seven) with { TraversedHops = [RelayOne] }, RelayOne, context, ct);

        var path = await database.GetDiscoveredPathAsync(Seven);
        path.Should().NotBeNull();
        path!.GetIntermediates().Should().Equal(RelayOne);
    }

    [Fact]
    public async Task DiscoveredPaths_ForTwoSsids_DoNotOverwriteEachOther()
    {
        await database.UpsertNeighbour(RelayOne, bearerPort: 1);
        await database.UpsertNeighbour(RelayTwo, bearerPort: 1);
        var mesh = MeshCore();

        await mesh.ObserveInboundAsync(From(Seven) with { TraversedHops = [RelayOne] }, RelayOne, context, ct);
        await mesh.ObserveInboundAsync(From(Eight) with { TraversedHops = [RelayTwo] }, RelayTwo, context, ct);

        (await NextHopAsync(mesh, $"chat@{Seven}")).Should().Be(RelayOne);
        (await NextHopAsync(mesh, $"chat@{Eight}")).Should().Be(RelayTwo);
    }

    // -- helpers --

    private PassiveLearningAlgorithm Passive() =>
        new(Static(), NullLogger<PassiveLearningAlgorithm>.Instance);

    private static StaticRoutingAlgorithm Static() => new(NullLogger<StaticRoutingAlgorithm>.Instance);

    private static MeshCoreLikeRoutingAlgorithm MeshCore() =>
        new(Static(), NullLogger<MeshCoreLikeRoutingAlgorithm>.Instance);

    private async Task<string?> NextHopAsync(IRoutingAlgorithm algorithm, string destination)
    {
        var decision = await algorithm.ResolveAsync(
            new DbMessage { Id = "0000001", Destination = destination, Payload = "x"u8.ToArray() }, context, ct);
        return decision is RouteDecision.NextHop next ? next.Route.Callsign : null;
    }

    private static BackhaulMessage From(string originator) =>
        new("abcdef0", "chat@M0US-1", 1L, 600, "x"u8.ToArray(), Originator: originator);

    private static DbMessage InFlood(string destination, string linkSource, string? traversed = null, string originator = "") => new()
    {
        TraversedHopsCsv = traversed,
        OriginatorCallsign = originator,
        Id = "f100d01",
        Destination = destination,
        Payload = "x"u8.ToArray(),
        SourceCallsign = linkSource,
        FloodHopsRemaining = 3,
    };

    private static void InsertHint(string destination, string nextHop)
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbRouteHint { Destination = destination, NextHop = nextHop });
    }

    private async Task<string> RoutesAnswerAsync()
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("routes\nq\n"));
        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: "N0THEM-9", NullLoggerFactory.Instance, database, new RecordingInbox());
        await handler.Handle(CancellationToken.None);
        return Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
    }
}
