using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;

namespace dapps.core.tests;

/// <summary>
/// #398: a message on a strict ordered stream that expires by its TTL
/// while it is parked will never be delivered, so the stream steps over
/// its seq the way it does for an operator drop (#389) instead of
/// stalling every later message behind it.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class InboxTtlExpiryTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private FakeTimeProvider clock = null!;
    private Database database = null!;
    private MqttServer mqttServer = null!;
    private MqttBrokerService broker = null!;
    private DatabaseAndMqttInbox inbox = null!;

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ttlexp-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbStreamSendState>();
            c.CreateTable<DbStreamRecvState>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbFragment>();
        }

        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
            MqttPort = brokerPort,
            FragmentThresholdBytes = 0,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor, clock);
        mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(brokerPort).Build());
        await mqttServer.StartAsync();
        broker = new MqttBrokerService(NullLogger<MqttBrokerService>.Instance, optionsMonitor, database,
            new AppTokenStore(NullLogger<AppTokenStore>.Instance), mqttServer);
        inbox = new DatabaseAndMqttInbox(database, broker, new InboundEventBus(),
            optionsMonitor, new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(database, optionsMonitor), clock,
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ExpireAsync(TimeSpan by)
    {
        clock.Advance(by);
        await database.DeleteExpired(clock.GetUtcNow().UtcDateTime);
    }

    private async Task<(IMqttClient Client, List<uint?> Received)> SubscribeAsync()
    {
        var client = await ConnectClient();
        var received = new List<uint?>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (received) received.Add(SeqOf(e));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, Ct);
        return (client, received);
    }

    private static async Task WaitForAsync(List<uint?> received, int count)
    {
        for (var i = 0; i < 50; i++)
        {
            lock (received) if (received.Count >= count) break;
            await Task.Delay(50, Ct);
        }
        await Task.Delay(150, Ct);
    }

    [Fact]
    public async Task StrictStream_AParkedMessageExpiring_DoesNotStallTheOnesAfterIt()
    {
        var (client, received) = await SubscribeAsync();

        // sn=2 and sn=3 park behind the missing sn=1; sn=2 runs out of TTL.
        await inbox.DeliverAsync(Stream("ttlx0003", 3, "three"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttlx0002", 2, "two", ttl: 60), "G0HOP", Ct);
        await ExpireAsync(TimeSpan.FromMinutes(2));
        (await database.GetDroppedMessage("ttlx0002"))!.Reason.Should().Be("ttl-expired");

        await inbox.DeliverAsync(Stream("ttlx0001", 1, "one"), "G0HOP", Ct);
        await WaitForAsync(received, 2);

        received.Should().Equal(1u, 3u);
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(4u);
        (await database.GetMessage("ttlx0003"))!.PendingInOrder.Should().BeFalse();
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_TheOnlyParkedMessageExpiring_LetsTheNextOneThroughLater()
    {
        // Nothing parked behind the expired seq when it goes: the cursor
        // must still step over it once the gap before it fills.
        var (client, received) = await SubscribeAsync();

        await inbox.DeliverAsync(Stream("ttly0002", 2, "two", ttl: 60), "G0HOP", Ct);
        await ExpireAsync(TimeSpan.FromMinutes(2));
        await inbox.DeliverAsync(Stream("ttly0001", 1, "one"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttly0003", 3, "three"), "G0HOP", Ct);
        await WaitForAsync(received, 2);

        received.Should().Equal(1u, 3u);
        (await database.GetMessage("ttly0003"))!.PendingInOrder.Should().BeFalse();
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_SeveralParkedMessagesExpiring_AreAllSteppedOver()
    {
        var (client, received) = await SubscribeAsync();

        await inbox.DeliverAsync(Stream("ttlz0004", 4, "four"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttlz0003", 3, "three", ttl: 60), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttlz0002", 2, "two", ttl: 60), "G0HOP", Ct);
        await ExpireAsync(TimeSpan.FromMinutes(2));
        await inbox.DeliverAsync(Stream("ttlz0001", 1, "one"), "G0HOP", Ct);
        await WaitForAsync(received, 2);

        received.Should().Equal(1u, 4u);
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(5u);
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_ARelayedMessageExpiring_DoesNotSkipTheSameSeqHere()
    {
        // Seqs are per destination: sn=2 of the same sender's stream on
        // its way to another node expiring here says nothing about sn=2
        // for this node, which may still come.
        var (client, received) = await SubscribeAsync();
        await database.SaveMessage("relay002", "x"u8.ToArray(), 1L, "chat@G0ELSE", "G0HOP", "", ttl: 60,
            originatorCallsign: "G0FROM", streamId: "s1", streamSeq: 2, streamGapTimeoutSeconds: 0);
        await ExpireAsync(TimeSpan.FromMinutes(2));
        (await database.GetDroppedMessage("relay002"))!.Reason.Should().Be("ttl-expired");

        await inbox.DeliverAsync(Stream("ttlr0003", 3, "three"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttlr0001", 1, "one"), "G0HOP", Ct);
        await WaitForAsync(received, 1);
        received.Should().Equal(1u);
        (await database.GetMessage("ttlr0003"))!.PendingInOrder.Should().BeTrue();

        await inbox.DeliverAsync(Stream("ttlr0002", 2, "two"), "G0HOP", Ct);
        await WaitForAsync(received, 3);
        received.Should().Equal(1u, 2u, 3u);
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_AnotherSendersStreamWithTheSameId_IsNotSteppedOver()
    {
        // The cursor is per (sender, stream): G0OTHR's sn=2 on "s1"
        // expiring says nothing about G0FROM's sn=2 on "s1".
        var (client, received) = await SubscribeAsync();
        await inbox.DeliverAsync(Stream("ttlo0002", 2, "theirs", ttl: 60) with { Originator = "G0OTHR" }, "G0HOP", Ct);
        await ExpireAsync(TimeSpan.FromMinutes(2));
        (await database.GetDroppedMessage("ttlo0002"))!.Reason.Should().Be("ttl-expired");

        await inbox.DeliverAsync(Stream("ttlo1003", 3, "three"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("ttlo1001", 1, "one"), "G0HOP", Ct);
        await WaitForAsync(received, 1);

        received.Should().Equal(1u);
        (await database.GetMessage("ttlo1003"))!.PendingInOrder.Should().BeTrue();
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(2u);
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_AfterACrashBetweenTheDropAndTheDrain_TheNextArrivalReleasesWhatIsParked()
    {
        // Cursor at 5; sn=5 was recorded as expired but the drain never
        // ran (a crash), so sn=6 is still parked. When sn=7 arrives, 6
        // and 7 both go, in order.
        var (client, received) = await SubscribeAsync();
        await database.UpsertStreamRecvStateAsync(new DbStreamRecvState
        {
            LocalCallsign = "N0SELF", SenderCallsign = "G0FROM", StreamId = "s1",
            NextExpectedSeq = 5, LastReceivedAt = clock.GetUtcNow().UtcDateTime, GapDeadline = DateTime.MinValue,
        });
        await database.SaveMessage("crsh0005", "five"u8.ToArray(), 1L, "chat@N0SELF", "G0HOP", "", ttl: 60,
            originatorCallsign: "G0FROM", streamId: "s1", streamSeq: 5, streamGapTimeoutSeconds: 0, pendingInOrder: true);
        await database.SaveMessage("crsh0006", "six"u8.ToArray(), 1L, "chat@N0SELF", "G0HOP", "", ttl: 3600,
            originatorCallsign: "G0FROM", streamId: "s1", streamSeq: 6, streamGapTimeoutSeconds: 0, pendingInOrder: true);
        clock.Advance(TimeSpan.FromMinutes(2));
        await database.DropExpiredAsync("crsh0005");

        await inbox.DeliverAsync(Stream("crsh0007", 7, "seven"), "G0HOP", Ct);
        await WaitForAsync(received, 2);

        received.Should().Equal(6u, 7u);
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(8u);
        (await database.GetMessage("crsh0006"))!.PendingInOrder.Should().BeFalse();
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task Stream_ADeliveredButUnackedMessageExpiring_LeavesTheCursorAlone()
    {
        await inbox.DeliverAsync(Stream("ttld0001", 1, "one", ttl: 60), "G0HOP", Ct);
        await ExpireAsync(TimeSpan.FromMinutes(2));

        (await database.GetMessage("ttld0001")).Should().BeNull();
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(2u);
    }

    // -- helpers --

    private static BackhaulMessage Plain(string id, string body) => new(
        Id: id, Destination: "chat@N0SELF", Salt: 1L, Ttl: 600,
        Payload: System.Text.Encoding.UTF8.GetBytes(body), Originator: "G0FROM");

    private static BackhaulMessage Stream(string id, uint sn, string body, int ttl = 3600) => Plain(id, body) with
    {
        Ttl = ttl,
        StreamId = "s1",
        StreamSeq = sn,
        StreamGapTimeoutSeconds = 0,
    };

    private static uint? SeqOf(MqttApplicationMessageReceivedEventArgs e)
    {
        var p = e.ApplicationMessage.UserProperties?.FirstOrDefault(u => u.Name == "dapps-stream-seq");
        return p is not null && uint.TryParse(p.Value, out var s) ? s : null;
    }

    private async Task<IMqttClient> ConnectClient()
    {
        var client = new MqttFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true)
            .Build(), Ct);
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
