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
/// #389: the operator's Drop on the dashboard's local inbox. The message
/// moves to the dropped messages as dropped by the operator, is never
/// handed to its app again, takes its fragments with it, and on an
/// ordered stream never holds up the messages after it.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class InboxOperatorDropTests : IAsyncLifetime
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
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-drop-test-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task Drop_AnUnackedMessage_MovesItToDropped_AndTheAppNeverSeesItAgain()
    {
        await inbox.DeliverAsync(Plain("drop0001", "hello"), "G0HOP", Ct);
        await database.UpsertFragment(new DbFragment
        {
            MasterId = "drop0001", FragmentIndex = 1, FragmentTotal = 2,
            Payload = "x"u8.ToArray(), Destination = "chat@N0SELF", FirstSeenAt = clock.GetUtcNow().UtcDateTime,
        });

        (await inbox.DropByOperatorAsync("drop0001", Ct)).Should().Be(InboxDropOutcome.Dropped);

        (await database.GetMessage("drop0001")).Should().BeNull();
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().BeEmpty(
            "neither MQTT replay nor the REST inbox may hand it out again");
        (await database.GetFragmentsForMaster("drop0001")).Should().BeEmpty();
        var dropped = await database.GetDroppedMessage("drop0001");
        dropped!.Reason.Should().Be(Database.DroppedByOperatorReason);
        dropped.Payload.Should().Equal("hello"u8.ToArray());

        // Replay on subscribe: nothing comes.
        var client = await ConnectClient();
        var got = 0;
        client.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref got); return Task.CompletedTask; };
        await client.SubscribeAsync("dapps/in/chat", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, Ct);
        await Task.Delay(200, Ct);
        got.Should().Be(0);
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task Drop_AnUnknownId_IsNotFound()
    {
        (await inbox.DropByOperatorAsync("nosuch01", Ct)).Should().Be(InboxDropOutcome.NotFound);
        (await database.GetRecentDroppedMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task Drop_AMessageTheAppAcked_IsLeftAlone()
    {
        await inbox.DeliverAsync(Plain("acked001", "hi"), "G0HOP", Ct);
        (await database.MarkLocallyDeliveredForApp("chat", "acked001")).Should().BeTrue();

        (await inbox.DropByOperatorAsync("acked001", Ct)).Should().Be(InboxDropOutcome.AlreadyAcked);

        (await database.GetMessage("acked001"))!.LocallyDelivered.Should().BeTrue();
        (await database.GetRecentDroppedMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task Drop_WhenTheAppAcksPartWay_TheAckWins_AndNoDropRecordIsLeft()
    {
        await inbox.DeliverAsync(Plain("race0001", "hi"), "G0HOP", Ct);
        database.BeforeInboxDropDelete = async () =>
        {
            (await database.GetDroppedMessage("race0001")).Should().NotBeNull("the record is written before the delete");
            await database.MarkLocallyDeliveredForApp("chat", "race0001");
        };

        (await inbox.DropByOperatorAsync("race0001", Ct)).Should().Be(InboxDropOutcome.AlreadyAcked);

        (await database.GetMessage("race0001"))!.LocallyDelivered.Should().BeTrue();
        (await database.GetDroppedMessage("race0001")).Should().BeNull();
    }

    [Fact]
    public async Task Drop_AfterACrashLeftTheRecordAndTheMessage_FinishesTheJob()
    {
        // A crash between writing the record and deleting the message
        // leaves both; the operator's next Drop completes it.
        await inbox.DeliverAsync(Plain("crash001", "hi"), "G0HOP", Ct);
        database.BeforeInboxDropDelete = () => throw new IOException("crash");
        var act = () => inbox.DropByOperatorAsync("crash001", Ct);
        await act.Should().ThrowAsync<IOException>();
        (await database.GetMessage("crash001")).Should().NotBeNull();
        (await database.GetDroppedMessage("crash001")).Should().NotBeNull();

        database.BeforeInboxDropDelete = null;
        (await inbox.DropByOperatorAsync("crash001", Ct)).Should().Be(InboxDropOutcome.Dropped);

        (await database.GetMessage("crash001")).Should().BeNull();
        (await database.GetDroppedMessage("crash001"))!.Reason.Should().Be(Database.DroppedByOperatorReason);
    }

    [Fact]
    public async Task Drop_AMessageForAnotherNode_IsNotFound_AndStaysQueued()
    {
        await database.SaveMessage("relay001", "x"u8.ToArray(), 1L, "chat@G0ELSE", "G0HOP", "", ttl: 600);

        (await inbox.DropByOperatorAsync("relay001", Ct)).Should().Be(InboxDropOutcome.NotFound);

        (await database.GetMessage("relay001")).Should().NotBeNull();
    }

    [Fact]
    public async Task StrictStream_DroppingAParkedMessage_DoesNotStallTheOnesAfterIt()
    {
        var client = await ConnectClient();
        var received = new List<uint?>();
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (received)
            {
                received.Add(SeqOf(e));
                if (received.Count == 2) two.TrySetResult();
            }
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, Ct);

        // sn=2 and sn=3 park behind the missing sn=1; the operator drops sn=2.
        await inbox.DeliverAsync(Stream("strm0003", 3, "three"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("strm0002", 2, "two"), "G0HOP", Ct);
        (await inbox.DropByOperatorAsync("strm0002", Ct)).Should().Be(InboxDropOutcome.Dropped);

        await inbox.DeliverAsync(Stream("strm0001", 1, "one"), "G0HOP", Ct);
        await two.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(150, Ct);

        received.Should().Equal(1u, 3u);
        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(4u);
        (await database.GetDroppedMessage("strm0002"))!.Reason.Should().Be(Database.DroppedByOperatorReason);
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task StrictStream_DroppingTheOnlyParkedMessage_LetsTheNextOneThroughLater()
    {
        // Nothing parked behind the dropped seq when it goes: the cursor
        // must still step over it once the gap before it fills.
        var client = await ConnectClient();
        var received = new List<uint?>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (received) received.Add(SeqOf(e));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, Ct);

        await inbox.DeliverAsync(Stream("only0002", 2, "two"), "G0HOP", Ct);
        (await inbox.DropByOperatorAsync("only0002", Ct)).Should().Be(InboxDropOutcome.Dropped);
        await inbox.DeliverAsync(Stream("only0001", 1, "one"), "G0HOP", Ct);
        await inbox.DeliverAsync(Stream("only0003", 3, "three"), "G0HOP", Ct);
        await Task.Delay(300, Ct);

        received.Should().Equal(1u, 3u);
        (await database.GetMessage("only0003"))!.PendingInOrder.Should().BeFalse();
        await client.DisconnectAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task Stream_DroppingADeliveredButUnackedMessage_LeavesTheCursorAlone()
    {
        await inbox.DeliverAsync(Stream("dlvd0001", 1, "one"), "G0HOP", Ct);

        (await inbox.DropByOperatorAsync("dlvd0001", Ct)).Should().Be(InboxDropOutcome.Dropped);

        (await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "s1"))!.NextExpectedSeq.Should().Be(2u);
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().BeEmpty();
    }

    // -- helpers --

    private static BackhaulMessage Plain(string id, string body) => new(
        Id: id, Destination: "chat@N0SELF", Salt: 1L, Ttl: 600,
        Payload: System.Text.Encoding.UTF8.GetBytes(body), Originator: "G0FROM");

    private static BackhaulMessage Stream(string id, uint sn, string body) => Plain(id, body) with
    {
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
