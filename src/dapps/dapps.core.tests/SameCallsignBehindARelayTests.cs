using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Server;

namespace dapps.core.tests;

/// <summary>
/// Two DAPPS nodes under one callsign, G6FCI-7 and G6FCI-8, that are not
/// neighbours: each has only a relay, M0RLY-1, between them (#242). Three
/// in-process nodes on the default passive-flood stack, each with its own
/// database, passing what they send straight to the inbox of the node it
/// was for. Before #242 the relay never passed G6FCI-7's flood on to
/// G6FCI-8 (it shares G6FCI-7's base callsign), and neither SSID could
/// learn a route to the other, so the two could not reach each other.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class SameCallsignBehindARelayTests : IAsyncLifetime
{
    private const string Seven = "G6FCI-7";
    private const string Eight = "G6FCI-8";
    private const string Relay = "M0RLY-1";

    private readonly Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken ct;

    public async ValueTask InitializeAsync()
    {
        ct = TestContext.Current.CancellationToken;
        await AddNodeAsync(Seven, Relay);
        await AddNodeAsync(Relay, Seven, Eight);
        await AddNodeAsync(Eight, Relay);
    }

    public async ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        foreach (var node in nodes.Values)
        {
            await node.Mqtt.StopAsync();
            node.Mqtt.Dispose();
            try { File.Delete(node.DbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TwoSsidsOfOneCallsign_ReachEachOtherThroughTheRelay()
    {
        var there = await SubmitAsync(Seven, Eight, "Hello World");
        await PumpAsync();

        (await InboxAsync(Eight)).Should().Equal(there);
        (await InboxAsync(Relay)).Should().BeEmpty("the relay only passes it on");

        var back = await SubmitAsync(Eight, Seven, "Hello yourself");
        await PumpAsync();

        (await InboxAsync(Seven)).Should().Equal(back);
        nodes[Eight].Sent.Where(s => s.Message.Id == back).Should().ContainSingle()
            .Which.Message.FloodHopsRemaining.Should().BeNull("G6FCI-8 learned the way to G6FCI-7 from its flood");

        var again = await SubmitAsync(Seven, Eight, "And again");
        await PumpAsync();

        (await InboxAsync(Eight)).Should().Equal(there, again);
        nodes[Seven].Sent.Where(s => s.Message.Id == again).Should().ContainSingle()
            .Which.Message.FloodHopsRemaining.Should().BeNull("G6FCI-7 learned the way to its sibling from the reply");
    }

    private async Task<string> SubmitAsync(string from, string to, string text)
    {
        var node = Use(from);
        return await node.Database.SubmitOutboundMessage("chat", to, System.Text.Encoding.UTF8.GetBytes(text), ttlSeconds: 600);
    }

    private async Task<string[]> InboxAsync(string callsign)
    {
        var node = Use(callsign);
        return (await node.Database.GetUnacknowledgedLocalMessagesForApp("chat"))
            .OrderBy(m => m.CreatedAt).Select(m => m.Id).ToArray();
    }

    /// <summary>Run every node's forwarder and deliver what it sent, until
    /// nothing more moves.</summary>
    private async Task PumpAsync()
    {
        for (var round = 0; round < 10; round++)
        {
            var moved = false;
            foreach (var node in nodes.Values)
            {
                Use(node.Callsign);
                var before = node.Sent.Count;
                await node.Outbound.DoRun(ct);
                foreach (var (message, route) in node.Sent.Skip(before).ToList())
                {
                    var to = Use(route.Callsign);
                    await to.Inbox.DeliverAsync(message, node.Callsign, ct);
                    moved = true;
                }
            }
            if (!moved) return;
        }
    }

    private Node Use(string callsign)
    {
        var node = nodes[callsign];
        DbInfo.OverridePath = node.DbPath;
        return node;
    }

    private async Task AddNodeAsync(string callsign, params string[] neighbours)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dapps-relay-{callsign}-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbFragment>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
            c.CreateTable<DbLearnedRoute>();
            c.CreateTable<DbFloodSeen>();
            c.CreateTable<DbDiscoveredPath>();
            // Every link is a stand-in UDP endpoint: the capturing backhaul
            // takes any route with one and never opens a socket.
            foreach (var n in neighbours) c.Insert(new DbNeighbour { Callsign = n, UdpEndpoint = "127.0.0.1:65535" });
        }

        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = callsign });
        var database = new Database(NullLogger<Database>.Instance, options);
        var context = new DatabaseRoutingContext(database, options);
        var algorithm = new FloodFallbackAlgorithm(
            new PassiveLearningAlgorithm(new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
                NullLogger<PassiveLearningAlgorithm>.Instance),
            NullLogger<FloodFallbackAlgorithm>.Instance);
        // A broker with no endpoint, so local delivery has somewhere to go.
        var mqtt = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build());
        await mqtt.StartAsync();
        var broker = new MqttBrokerService(NullLogger<MqttBrokerService>.Instance, options, database,
            new AppTokenStore(NullLogger<AppTokenStore>.Instance), mqtt);
        var inbox = new DatabaseAndMqttInbox(database, broker, new InboundEventBus(), options,
            algorithm, context, TimeProvider.System, NullLogger<DatabaseAndMqttInbox>.Instance);
        var capture = new CapturingBackhaul();
        var outbound = new OutboundMessageManager(database, NullLoggerFactory.Instance, options,
            [capture], algorithm, context);
        nodes[callsign] = new Node(callsign, dbPath, database, inbox, outbound, mqtt, capture.Sent);
    }

    private sealed record Node(
        string Callsign, string DbPath, Database Database, DatabaseAndMqttInbox Inbox,
        OutboundMessageManager Outbound, MqttServer Mqtt, List<(BackhaulMessage Message, BackhaulRoute Route)> Sent);

    private sealed class CapturingBackhaul : IDappsBackhaul
    {
        public List<(BackhaulMessage Message, BackhaulRoute Route)> Sent { get; } = [];

        public bool CanHandle(BackhaulRoute route) => route.UdpEndpoint is not null;

        public Task<BackhaulSendResult> SendAsync(BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
        {
            Sent.Add((message, route));
            return Task.FromResult(BackhaulSendResult.Ok());
        }
    }
}
