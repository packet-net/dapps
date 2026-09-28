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
/// F1 acceptance test for the inbound→re-forward seam: when a relay
/// node receives a <see cref="BackhaulMessage"/> with an originator set
/// and re-forwards it because the destination isn't local, the outbound
/// message MUST carry the same originator. The unit tests cover each
/// link in the chain (parser, emitter, codec, MQTT property); this is
/// the only test that proves they *compose*. If this passes, an A→B→C
/// path will surface A as <c>OriginatorCallsign</c> at C - which is
/// the entire point of F1.
///
/// Wire-format breakage between forward and re-forward (the failure
/// mode this test guards against) would let unit tests pass while
/// silently dropping or rewriting <c>src=</c> across a relay; the
/// shell-script multi-hop simulator catches it too, but only when
/// someone runs it.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class F1MultiHopForwardingTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private DatabaseAndMqttInbox inbox = null!;
    private OutboundMessageManager outbound = null!;
    private CapturingBackhaul capture = null!;

    private const string RelayCallsign = "G0SIB-1";
    private const string OriginatorCallsign = "G0SIA-1";
    private const string LinkSourceCallsign = "G0SIA-1-link";  // arbitrary; B's view of "who handed me this"
    private const string DestinationCallsign = "G0SIC-1";
    private const string DownstreamNeighbourUdp = "127.0.0.1:65535";

    public async ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-f1-mh-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();

            // Pre-wire the relay's neighbour table so the route resolver
            // picks the downstream link without going through discovery.
            // UdpEndpoint is a sentinel - the capturing backhaul handles
            // any route with a UdpEndpoint set; it never actually opens a
            // socket.
            c.Insert(new DbNeighbour
            {
                Callsign = DestinationCallsign,
                UdpEndpoint = DownstreamNeighbourUdp,
            });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = RelayCallsign,
        });

        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        capture = new CapturingBackhaul();

        // MqttBrokerService is wired even though no broker is started - the
        // inbox calls InjectInboundMessage only for local destinations, and
        // every test here uses a remote destination (the relay re-forwards).
        var tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        var mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build());
        var broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens, mqttServer);
        var events = new InboundEventBus();
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        inbox = new DatabaseAndMqttInbox(
            database, broker, events, optionsMonitor,
            routingAlgorithm, routingContext, TimeProvider.System,
            NullLogger<DatabaseAndMqttInbox>.Instance);

        outbound = new OutboundMessageManager(
            database,
            new NullLoggerFactory(),
            optionsMonitor,
            new IDappsBackhaul[] { capture },
            routingAlgorithm, routingContext);

        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RelayPreservesOriginator_FromInboundToOutbound()
    {
        // 1. Relay's inbox receives a message with src=A from upstream.
        //    The destination (G0SIC-1) is not the relay's own callsign,
        //    so the inbox saves it for the forwarder to pick up.
        var inbound = new BackhaulMessage(
            Id: "f1mhop1",
            Destination: $"chat@{DestinationCallsign}",
            Salt: 1L,
            Ttl: 600,
            Payload: "hello"u8.ToArray(),
            Originator: OriginatorCallsign);

        await inbox.DeliverAsync(inbound, LinkSourceCallsign, TestContext.Current.CancellationToken);

        // 2. The persisted DbMessage row carries the originator verbatim
        //    AND distinguishes link source from originator - these two
        //    columns must not collapse into one.
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Query<DbMessage>("select * from messages where Id=?", "f1mhop1").Single();
            row.OriginatorCallsign.Should().Be(OriginatorCallsign,
                "src=A on the wire must round-trip into DbMessage.OriginatorCallsign");
            row.SourceCallsign.Should().Be(LinkSourceCallsign,
                "link source is the immediate upstream peer; must remain separate from originator");
        }

        // 3. Run the forwarder. It picks the manual neighbour route to C,
        //    constructs a BackhaulMessage, and hands it to our capturing
        //    backhaul instead of a real one.
        await outbound.DoRun(TestContext.Current.CancellationToken);

        capture.Sent.Should().HaveCount(1,
            "the relay must re-forward the inbound message exactly once");

        var outboundMessage = capture.Sent[0].Message;
        outboundMessage.Id.Should().Be("f1mhop1");
        outboundMessage.Originator.Should().Be(OriginatorCallsign,
            "F1 acceptance: src=A inbound MUST become src=A outbound across a relay - " +
            "if this asserts to G0SIB-1 (relay's own callsign) the seam is broken and " +
            "all downstream apps will see the link source as the originator.");
        outboundMessage.Destination.Should().Be($"chat@{DestinationCallsign}");
        outboundMessage.Payload.Should().Equal(inbound.Payload);
    }

    [Fact]
    public async Task RelayWithoutInboundOriginator_OmitsOriginatorOnReforward()
    {
        // Pre-F1 sender (no src=) hands us a message. The relay does NOT
        // get to invent an originator - claiming the link source as the
        // originator would lie when the path is multi-hop. Outbound MUST
        // omit src= so the receiver knows the chain is unauthoritative.
        var inbound = new BackhaulMessage(
            Id: "f1mhop2",
            Destination: $"chat@{DestinationCallsign}",
            Salt: null,
            Ttl: 60,
            Payload: "x"u8.ToArray(),
            Originator: null);

        await inbox.DeliverAsync(inbound, LinkSourceCallsign, TestContext.Current.CancellationToken);
        await outbound.DoRun(TestContext.Current.CancellationToken);

        capture.Sent.Should().HaveCount(1);
        capture.Sent[0].Message.Originator.Should().BeNull(
            "an unknown originator must propagate as 'unknown,' not as the link source - " +
            "lying here would corrupt the dapps-origin user property at the receiver");
    }

    [Fact]
    public async Task LocalSubmissionStampsOriginator_WhichSurvivesForwarderHandoff()
    {
        // Round-trip the originating-node case: a local app submits, the
        // database stamps OriginatorCallsign with our own callsign, and
        // the outbound BackhaulMessage carries it forward.
        var id = await database.SubmitOutboundMessage(
            appName: "chat",
            destCallsign: DestinationCallsign,
            payload: "hi"u8.ToArray(),
            ttlSeconds: 600);

        using (var c = DbInfo.GetConnection())
        {
            var row = c.Query<DbMessage>("select * from messages where Id=?", id).Single();
            row.OriginatorCallsign.Should().Be(RelayCallsign,
                "local submission stamps the originator with our own callsign");
        }

        await outbound.DoRun(TestContext.Current.CancellationToken);

        capture.Sent.Should().HaveCount(1);
        capture.Sent[0].Message.Originator.Should().Be(RelayCallsign);
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
