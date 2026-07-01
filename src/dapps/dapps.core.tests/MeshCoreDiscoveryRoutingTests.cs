using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace dapps.core.tests;

/// <summary>
/// Passive MeshCore discovery (#27): a peer we hear over the MeshCore bearer is
/// recorded as a <see cref="DbDiscoveredPeer"/> with <c>Bearer="meshcore"</c> and a
/// non-null <see cref="DbDiscoveredPeer.MeshCoreChannel"/>. This test pins the
/// routing half of the feature — that <see cref="StaticRoutingAlgorithm"/> turns
/// such a row into a route the MeshCore bearer will claim (its <c>CanHandle</c>
/// keys purely on a non-empty <c>MeshCoreChannel</c>), so outbound to a
/// passively-discovered peer works with no manual neighbour configured.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MeshCoreDiscoveryRoutingTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseRoutingContext context = null!;
    private StaticRoutingAlgorithm algorithm = null!;

    private const string OurCallsign = "G0US-1";
    private const string PeerCallsign = "G0MC-1";
    private const string PeerBaseCallsign = "G0MC";
    private const string Channel = "dapps-soak";

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mcdisc-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
            c.CreateTable<DbLearnedRoute>();
            c.CreateTable<DbFloodSeen>();
            c.CreateTable<DbDiscoveredPath>();
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = OurCallsign });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        context = new DatabaseRoutingContext(database, optionsMonitor);
        algorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private Task Record(DbDiscoveredPeer peer) => database.UpsertDiscoveredPeer(peer);

    private static DbDiscoveredPeer MeshCorePeer(DateTime lastSeen) => new()
    {
        Callsign = PeerCallsign,
        Bearer = "meshcore",
        ChannelKey = Channel,
        LinkClass = LinkClass.MeshCore,
        CostHint = LinkClassDefaults.CostHint(LinkClass.MeshCore),
        Hops = 1,
        TtlSeconds = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.MeshCore),
        MeshCoreChannel = Channel,
        LastSeen = lastSeen,
    };

    private static DbMessage OutboundTo(string baseCallsign) => new()
    {
        Id = "0000002",
        Destination = $"chat@{baseCallsign}-1",
        Payload = "x"u8.ToArray(),
    };

    [Fact]
    public async Task FreshMeshCorePeer_RoutesWithChannelHint()
    {
        await Record(MeshCorePeer(DateTime.UtcNow));

        var decision = await algorithm.ResolveAsync(
            OutboundTo(PeerBaseCallsign), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.Callsign.Should().Be(PeerCallsign);
        nh.Route.MeshCoreChannel.Should().Be(Channel);
    }

    [Fact]
    public async Task StalePeer_AgedOut_IsUnreachable()
    {
        // Older than the MeshCore advertised TTL (10800s) → the freshness
        // filter drops it, so there's no route.
        await Record(MeshCorePeer(DateTime.UtcNow - TimeSpan.FromSeconds(LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.MeshCore) + 60)));

        var decision = await algorithm.ResolveAsync(
            OutboundTo(PeerBaseCallsign), context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task NonMeshCorePeer_HasNoChannelHint()
    {
        // A UDP-heard peer must not gain a MeshCore channel hint — otherwise the
        // MeshCore bearer's CanHandle (non-empty MeshCoreChannel) would wrongly
        // claim a route that should go out over UDP.
        await Record(new DbDiscoveredPeer
        {
            Callsign = PeerCallsign,
            Bearer = "udp",
            ChannelKey = "239.0.0.1:5000",
            LinkClass = LinkClass.LanMulticast,
            CostHint = LinkClassDefaults.CostHint(LinkClass.LanMulticast),
            Hops = 1,
            TtlSeconds = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.LanMulticast),
            UdpEndpoint = "127.0.0.1:5000",
            LastSeen = DateTime.UtcNow,
        });

        var decision = await algorithm.ResolveAsync(
            OutboundTo(PeerBaseCallsign), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.MeshCoreChannel.Should().BeNull();
        nh.Route.UdpEndpoint.Should().Be("127.0.0.1:5000");
    }

    [Fact]
    public async Task CheaperMeshCore_PreferredOverIpForSamePeer()
    {
        // Same peer heard on both a MeshCore channel (cost 3, RF-in-spirit) and a
        // UDP channel (cost 8). The router must pick the cheaper MeshCore row.
        await Record(new DbDiscoveredPeer
        {
            Callsign = PeerCallsign,
            Bearer = "udp",
            ChannelKey = "239.0.0.1:5000",
            LinkClass = LinkClass.LanMulticast,
            CostHint = LinkClassDefaults.CostHint(LinkClass.LanMulticast),
            Hops = 1,
            TtlSeconds = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.LanMulticast),
            UdpEndpoint = "127.0.0.1:5000",
            LastSeen = DateTime.UtcNow,
        });
        await Record(MeshCorePeer(DateTime.UtcNow));

        var decision = await algorithm.ResolveAsync(
            OutboundTo(PeerBaseCallsign), context, TestContext.Current.CancellationToken);

        var nh = decision.Should().BeOfType<RouteDecision.NextHop>().Subject;
        nh.Route.MeshCoreChannel.Should().Be(Channel);
    }
}
