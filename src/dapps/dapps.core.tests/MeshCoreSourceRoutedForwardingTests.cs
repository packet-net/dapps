using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Acceptance test for the meshcore stack's inbound→re-forward seam:
/// when a relay receives a source-routed <see cref="BackhaulMessage"/>
/// with the relay listed as the next hop, the inbox persists the
/// remaining route and the OMM re-forwards along it. The unit tests
/// in <see cref="MeshCoreLikeRoutingAlgorithmTests"/> cover the
/// algorithm's decisions in isolation; this test proves that the
/// OMM + inbox + algorithm compose end-to-end without dropping
/// the SourceRoute or TraversedHops fields between hops.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MeshCoreSourceRoutedForwardingTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseAndMqttInbox inbox = null!;
    private OutboundMessageManager outbound = null!;
    private CapturingBackhaul capture = null!;

    private const string RelayCallsign = "G0SIB-1";
    private const string OriginatorCallsign = "G0SIA-1";
    private const string DownstreamCallsign = "G0SIC-1";
    private const string FinalDestCallsign = "G0SID-1";

    public async ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mc-fwd-{Guid.NewGuid():N}.db");
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
            // Pre-wire the relay's neighbour table so it can reach
            // the next-hop in the source route. The capturing
            // backhaul handles any UdpEndpoint route.
            c.Insert(new DbNeighbour { Callsign = DownstreamCallsign, UdpEndpoint = "127.0.0.1:65535" });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = RelayCallsign,
            RoutingAlgorithm = "meshcore",
        });

        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        capture = new CapturingBackhaul();

        var tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        var mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build());
        var broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens, mqttServer);
        var events = new InboundEventBus();
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var staticAlg = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var meshcore = new MeshCoreLikeRoutingAlgorithm(staticAlg, NullLogger<MeshCoreLikeRoutingAlgorithm>.Instance);

        inbox = new DatabaseAndMqttInbox(
            database, broker, events, optionsMonitor,
            meshcore, routingContext, TimeProvider.System,
            NullLogger<DatabaseAndMqttInbox>.Instance);

        outbound = new OutboundMessageManager(
            database,
            new NullLoggerFactory(),
            optionsMonitor,
            new IDappsBackhaul[] { capture },
            meshcore, routingContext);

        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RelayPreservesSourceRoute_StripsHeadAndForwards()
    {
        // Source route as it arrives at the relay: [G0SIC-1, G0SID-1]
        // - meaning "next hop after me is G0SIC-1, then G0SID-1, then
        // the destination". Sender already stripped the relay itself
        // from the head before transmitting, so the relay sees the
        // remaining path.
        var inbound = new BackhaulMessage(
            Id: "mcacc01",
            Destination: $"chat@{FinalDestCallsign}",
            Salt: 1L,
            Ttl: 600,
            Payload: "hello"u8.ToArray(),
            Originator: OriginatorCallsign,
            SourceRoute: new[] { DownstreamCallsign, FinalDestCallsign });

        await inbox.DeliverAsync(inbound, OriginatorCallsign, TestContext.Current.CancellationToken);

        // Persisted DbMessage carries the remaining source route as
        // received (the OMM strips the head when re-forwarding).
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Query<DbMessage>("select * from messages where Id=?", "mcacc01").Single();
            row.SourceRouteCsv.Should().Be($"{DownstreamCallsign},{FinalDestCallsign}");
        }

        await outbound.DoRun(TestContext.Current.CancellationToken);

        capture.Sent.Should().HaveCount(1);
        var outboundMsg = capture.Sent[0].Message;
        outboundMsg.Originator.Should().Be(OriginatorCallsign,
            "F1 originator must round-trip even when the algorithm is meshcore");
        capture.Sent[0].Route.Callsign.Should().Be(DownstreamCallsign,
            "next hop should be the head of the inbound source route");
        outboundMsg.SourceRoute.Should().NotBeNull();
        outboundMsg.SourceRoute!.Should().Equal(new[] { FinalDestCallsign },
            "after the relay strips its next hop, only the remainder should be on the wire");
    }

    [Fact]
    public async Task RelayPropagatesFloodDiscovery_AppendingItselfToTraversedHops()
    {
        // Inbound flood-discovery from G0SIA-1 with TraversedHops = []
        // (originator was the only node before us). Destination is
        // G0SID-1, not local. The relay should:
        //   - persist with TraversedHopsCsv = "" and FloodHopsRemaining = 5
        //   - re-flood with TraversedHops = [RelayCallsign] (we appended
        //     ourselves) and FloodHopsRemaining = 4
        var inbound = new BackhaulMessage(
            Id: "mcacc02",
            Destination: $"chat@{FinalDestCallsign}",
            Salt: 2L,
            Ttl: 600,
            Payload: "discovering"u8.ToArray(),
            Originator: OriginatorCallsign,
            FloodHopsRemaining: 5,
            TraversedHops: Array.Empty<string>());

        await inbox.DeliverAsync(inbound, OriginatorCallsign, TestContext.Current.CancellationToken);

        // Check persistence captured the empty traversal record (not
        // null - empty means "in-flight flood, no intermediates yet").
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Query<DbMessage>("select * from messages where Id=?", "mcacc02").Single();
            row.FloodHopsRemaining.Should().Be(5);
            row.TraversedHopsCsv.Should().Be("",
                "empty CSV means 'flood-discovery in transit, no intermediates yet'");
        }

        await outbound.DoRun(TestContext.Current.CancellationToken);

        // Re-flood went out to all (one) downstream neighbours that
        // weren't in TraversedHops. Originator isn't a configured
        // neighbour at this relay, so excluding it from the flood
        // didn't shrink the candidate set.
        capture.Sent.Should().HaveCount(1);
        var outboundMsg = capture.Sent[0].Message;
        outboundMsg.FloodHopsRemaining.Should().Be(4,
            "hop budget decrements at each re-flood");
        outboundMsg.TraversedHops.Should().NotBeNull();
        outboundMsg.TraversedHops!.Should().Equal(new[] { RelayCallsign },
            "relay appends itself to the accumulated traversal record");
    }

    [Fact]
    public async Task FloodDiscoveryArrival_PopulatesDiscoveredPath_BackToOriginator()
    {
        // Originator G0SIA-1 emits a flood that arrived at us via
        // intermediate G0HOP1-1. The destination is local, so we
        // deliver - but we ALSO learn that to reach G0SIA-1 from us,
        // we should source-route through [G0HOP1-1].
        var inbound = new BackhaulMessage(
            Id: "mcacc03",
            Destination: $"chat@{RelayCallsign}",
            Salt: 3L,
            Ttl: 600,
            Payload: "found-you"u8.ToArray(),
            Originator: OriginatorCallsign,
            FloodHopsRemaining: 4,
            TraversedHops: new[] { "G0HOP1-1" });

        await inbox.DeliverAsync(inbound, "G0HOP1-1", TestContext.Current.CancellationToken);

        var path = await database.GetDiscoveredPathAsync("G0SIA");
        path.Should().NotBeNull(
            "the algorithm must observe inbound floods and store the reverse path");
        path!.GetIntermediates().Should().Equal(new[] { "G0HOP1-1" },
            "single-intermediate flood reverses to a single-intermediate path back");
    }

    private sealed class CapturingBackhaul : IDappsBackhaul
    {
        public List<(BackhaulMessage Message, BackhaulRoute Route, string LocalCallsign)> Sent { get; } = [];

        public bool CanHandle(BackhaulRoute route) => route.UdpEndpoint is not null;

        public Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message,
            BackhaulRoute route,
            string localCallsign,
            CancellationToken ct)
        {
            Sent.Add((message, route, localCallsign));
            return Task.FromResult(BackhaulSendResult.Ok());
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
