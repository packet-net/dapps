using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives the inbox seam (Plan A0 inbound counterpart) end-to-end:
/// <see cref="DatabaseAndMqttInbox.DeliverAsync"/> persists the message
/// and conditionally pushes it onto the MQTT broker depending on whether
/// the destination is local. Bearer-specific receive code (today
/// <see cref="InboundConnectionHandler"/>) is the only caller in
/// production; covering the inbox in isolation here means a future
/// MeshCore receive bearer gets the same delivery semantics for free.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DatabaseAndMqttInboxTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private Database database = null!;
    private MqttServer mqttServer = null!;
    private MqttBrokerService broker = null!;
    private DatabaseAndMqttInbox inbox = null!;

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-inbox-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            MqttPort = brokerPort,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        var tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(brokerPort).Build());
        await mqttServer.StartAsync();
        broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens, mqttServer);
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        inbox = new DatabaseAndMqttInbox(database, broker, new InboundEventBus(),
            optionsMonitor, routingAlgorithm, routingContext, TimeProvider.System,
            NullLogger<DatabaseAndMqttInbox>.Instance);

        await broker.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await broker.StopAsync(CancellationToken.None);
        await mqttServer.StopAsync();
        mqttServer.Dispose();
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task DeliverAsync_LocalDestination_PersistsAndPushesToMqtt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();

        var received = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            received.TrySetResult(e.ApplicationMessage);
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/myapp",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var payload = "from-inbox-test"u8.ToArray();
        var bm = new BackhaulMessage(
            Id: "inbx001",
            Destination: "myapp@N0CALL",
            Salt: 1L,
            Ttl: 600,
            Payload: payload);

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        // DB row.
        using var c = DbInfo.GetConnection();
        var row = c.Find<DbMessage>("inbx001");
        row.Should().NotBeNull();
        row!.SourceCallsign.Should().Be("G7XYZ-3");
        row.Ttl.Should().Be(600);

        // MQTT delivery.
        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        msg.PayloadSegment.ToArray().Should().Equal(payload);
        msg.UserProperties.Single(p => p.Name == "dapps-id").Value.Should().Be("inbx001");
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be("G7XYZ-3");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task DeliverAsync_RemoteDestination_PersistsButSkipsMqtt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();

        var received = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            received.TrySetResult(e.ApplicationMessage);
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/myapp",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var bm = new BackhaulMessage(
            Id: "inbx002",
            Destination: "myapp@N0OTHER",
            Salt: 1L,
            Ttl: 600,
            Payload: "for-someone-else"u8.ToArray());

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        // Persisted as outbound (forwarded=0, destination matches no local rule).
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("inbx002").Should().NotBeNull();

        // No MQTT delivery - the destination is remote.
        var winner = await Task.WhenAny(
            received.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        winner.Should().NotBeSameAs(received.Task,
            "remote-destined messages MUST NOT be injected into local MQTT topics");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task DeliverAsync_TheSameMessageAgain_AfterTheAppTookIt_IsNotDeliveredAgain()
    {
        // The sender restarted before seeing our ack, and offers it again.
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0001", Destination: "myapp@N0CALL", Salt: 7L, Ttl: 600, Payload: "once"u8.ToArray());

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);
        await database.MarkLocallyDelivered("dup0001");
        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().BeEmpty("the app already has it");
    }

    [Fact]
    public async Task DeliverAsync_ARelayedMessageAgain_AfterItWasPassedOn_IsNotQueuedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0002", Destination: "myapp@N0OTHER", Salt: 7L, Ttl: 600, Payload: "relay me"u8.ToArray());

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);
        await database.MarkMessageAsForwarded("dup0002");
        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-4", ct);

        (await database.CountPendingOutbound()).Should().Be(0, "it was passed on already, whichever neighbour sends it again");
    }

    [Fact]
    public async Task DeliverAsync_WhenStoringFails_TheSendersRetryIsStillAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0003", Destination: "myapp@N0CALL", Salt: 7L, Ttl: 600, Payload: "keep me"u8.ToArray());
        using (var c = DbInfo.GetConnection()) c.DropTable<DbMessage>();

        var first = () => inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);
        await first.Should().ThrowAsync<Exception>();

        using (var c = DbInfo.GetConnection()) c.CreateTable<DbMessage>();
        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().ContainSingle("a message is never lost to the memory");
    }

    [Fact]
    public async Task DeliverAsync_PreservesHeadersAsJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var headers = new Dictionary<string, string>
        {
            ["priority"] = "high",
            ["src"] = "G0ORIG",
        };
        var bm = new BackhaulMessage(
            Id: "inbx003",
            Destination: "myapp@N0OTHER",
            Salt: null,
            Ttl: null,
            Payload: "hi"u8.ToArray(),
            Headers: headers);

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbMessage>("inbx003")!;
        row.AdditionalProperties.Should().Contain("priority").And.Contain("high");
        row.AdditionalProperties.Should().Contain("src").And.Contain("G0ORIG");
    }

    private async Task<IMqttClient> ConnectClient()
    {
        var client = new MqttFactory().CreateMqttClient();
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true)
            .Build();
        await client.ConnectAsync(opts);
        return client;
    }

    private static int PickFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
