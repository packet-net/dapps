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
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbStreamSendState>();
            c.CreateTable<DbStreamRecvState>();
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
        // No originator on the wire: who it's from is the link source.
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be("G7XYZ-3");
        msg.UserProperties.Single(p => p.Name == "dapps-via").Value.Should().Be("G7XYZ-3");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task DeliverAsync_RelayedMessage_SourceIsTheOriginator_ViaIsTheLastHop()
    {
        // G6FCI's A-B-C-D chain: mail from A arrives at D from C. Apps
        // reply to dapps-source, so it must be A; dapps-via says C.
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
            Destination: "myapp@N0CALL",
            Salt: 1L,
            Ttl: 600,
            Payload: "relayed"u8.ToArray(),
            Originator: "G6AAA-7");

        await inbox.DeliverAsync(bm, sourceCallsign: "G6CCC-7", ct);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be("G6AAA-7");
        msg.UserProperties.Single(p => p.Name == "dapps-via").Value.Should().Be("G6CCC-7");
        msg.UserProperties.Single(p => p.Name == "dapps-origin").Value.Should().Be("G6AAA-7");

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbMessage>("inbx002");
        row!.SourceCallsign.Should().Be("G6CCC-7", "routing keeps the link source");
        row.FromCallsign.Should().Be("G6AAA-7");

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
    public async Task DeliverAsync_ForAnotherSsidOfOurCallsign_IsKeptToForward()
    {
        // We are N0CALL (SSID 0); N0CALL-3 is another node (#239).
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "sib0001", Destination: "myapp@N0CALL-3", Salt: 3L, Ttl: 600, Payload: "pass it on"u8.ToArray());

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Contain("sib0001");
        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().BeEmpty();
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
    public async Task DeliverAsync_TwoCopiesAtOnce_AreDeliveredOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0004", Destination: "myapp@N0CALL", Salt: 7L, Ttl: 600, Payload: "twice at once"u8.ToArray());

        await Task.WhenAll(
            inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct),
            inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-4", ct));
        await database.MarkLocallyDelivered("dup0004");
        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-5", ct);

        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeliverAsync_AnUnsaltedMessageAgain_IsDeliveredAgain()
    {
        // With no salt, the same text sent later has the same id and length:
        // it can't be told from a repeat, so it isn't remembered.
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0005", Destination: "myapp@N0CALL", Salt: null, Ttl: 600, Payload: "OK"u8.ToArray());

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);
        await database.MarkLocallyDelivered("dup0005");
        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverAsync_AMessageClaimedByANodeThatDied_IsStoredWhenItComesAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var bm = new BackhaulMessage(Id: "dup0006", Destination: "myapp@N0CALL", Salt: 7L, Ttl: 600, Payload: "claimed, never stored"u8.ToArray());
        await database.ClaimReceivedAsync(DbReceived.MakeKey("dup0006", 7L, bm.Payload.Length), DateTime.UtcNow.AddHours(1), "G7XYZ-3");

        await inbox.DeliverAsync(bm, sourceCallsign: "G7XYZ-3", ct);

        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().ContainSingle();
    }

    [Fact]
    public async Task AMessageDeliveredOverASession_IsAnsweredAck_WhenOfferedAgain()
    {
        // The whole path: the session hands the message to this inbox, and
        // the same message offered again is recognised at the offer.
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var server = await listener.AcceptTcpClientAsync(ct);
        await connecting;
        _ = Task.Run(() => new InboundConnectionHandler(server.GetStream(), "G7XYZ-3", NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var link = client.GetStream();
        var reader = new StreamReader(link, Encoding.ASCII);

        var payload = Encoding.UTF8.GetBytes("sent once, offered twice");
        var id = dapps.client.DappsMessage.ComputeHash(payload, 11L)[..7];
        var offer = $"ihave {id} len={payload.Length} fmt=p s=11 dst=myapp@N0CALL\n";

        (await reader.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await reader.ReadLineAsync(ct)).Should().StartWith("exchange ");
        await link.WriteAsync(Encoding.ASCII.GetBytes(offer), ct);
        (await reader.ReadLineAsync(ct)).Should().Be($"send {id}");
        await link.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes($"data {id}\n"), .. payload], ct);
        (await reader.ReadLineAsync(ct)).Should().Be($"ack {id}");

        await link.WriteAsync(Encoding.ASCII.GetBytes(offer), ct);
        (await reader.ReadLineAsync(ct)).Should().Be($"ack {id}", "we have it, so its payload needn't come again");

        // In an exchange, the same message sent unasked is acked and dropped.
        await link.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes($"exchange id=xyz003 hold=0 inline=256\nmsg {id} len={payload.Length} fmt=p s=11 dst=myapp@N0CALL\n"), .. payload], ct);
        (await reader.ReadLineAsync(ct)).Should().Be($"ack {id}");
        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().ContainSingle();
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

    [Fact]
    public async Task DeliverAsync_OurOwnMessageComingBack_IsARepeat_NotQueuedAgain()
    {
        // #295: we sent a message on, and a neighbour passed it back (a
        // flood, or a loop). Taking it overwrote our forwarded row with a
        // fresh queued one, so we sent or flooded it all over again.
        var ct = TestContext.Current.CancellationToken;
        var payload = "round trip"u8.ToArray();
        var id = await database.SubmitOutboundMessage("chat", "G0XYZ-1", payload, ttlSeconds: 600);
        await database.MarkMessageAsForwarded(id);
        DbMessage sent;
        using (var c = DbInfo.GetConnection()) sent = c.Find<DbMessage>(id);

        await inbox.DeliverAsync(new BackhaulMessage(
            Id: id, Destination: "chat@G0XYZ-1", Salt: sent.Salt, Ttl: 500,
            Payload: payload, Originator: "N0CALL"), sourceCallsign: "G7XYZ-3", ct);

        (await database.GetPendingOutboundMessages()).Should().BeEmpty("our own message coming back is a repeat, not one to send again");
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Find<DbMessage>(id);
            row.Should().NotBeNull();
            row!.Forwarded.Should().BeTrue();
            row.SourceCallsign.Should().Be("N0CALL", "the row is still the one we submitted");
        }
    }

    [Fact]
    public async Task DeliverAsync_FromAnotherSsidOfOurs_IsTakenAsUsual()
    {
        // Our exact callsign only: N0CALL-2 is a station like any other (#242).
        var ct = TestContext.Current.CancellationToken;
        await inbox.DeliverAsync(new BackhaulMessage(
            Id: "sib0001", Destination: "chat@G0XYZ-1", Salt: 5L, Ttl: 500,
            Payload: "from a sibling"u8.ToArray(), Originator: "N0CALL-2"), sourceCallsign: "N0CALL-2", ct);

        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Contain("sib0001");
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_IsPublishedLive_LikeAnArrival()
    {
        // #288: app@OWNCALL was stored but never published to the app's
        // MQTT topic. It must arrive as a message from another node does.
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var received = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            received.TrySetResult(e.ApplicationMessage);
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/hello",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var payload = "to myself"u8.ToArray();
        var id = await database.SubmitOutboundMessage("hello", "N0CALL", payload, ttlSeconds: 600);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        msg.PayloadSegment.ToArray().Should().Equal(payload);
        msg.UserProperties.Single(p => p.Name == "dapps-id").Value.Should().Be(id);
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be("N0CALL");

        (await database.GetUnacknowledgedLocalMessagesForApp("hello")).Select(m => m.Id).Should().Contain(id);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_BigPayload_IsOneMessage_NotFragments()
    {
        var payload = new byte[5000];
        new Random(1).NextBytes(payload);
        var id = await database.SubmitOutboundMessage("hello", "N0CALL", payload, ttlSeconds: 600);

        var rows = await database.GetUnacknowledgedLocalMessagesForApp("hello");
        rows.Should().ContainSingle(m => m.Id == id).Which.Payload.Should().Equal(payload);
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_WithStream_GoesThroughOrdering()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var got = new List<string>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (got) got.Add(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/hello",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        await database.SubmitOutboundMessage("hello", "N0CALL", "one"u8.ToArray(), 600, streamId: "s1");
        await database.SubmitOutboundMessage("hello", "N0CALL", "two"u8.ToArray(), 600, streamId: "s1");

        for (var i = 0; i < 50 && got.Count < 2; i++) await Task.Delay(100, ct);
        got.Should().Equal("one", "two");
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_20InParallelOnOneStream_AllArriveInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var got = new List<string>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (got) got.Add(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/hello",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        // Strict stream (no gap timeout): a lost or reordered message
        // would stall the rest, so 20 in order means no clobbered cursor.
        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => database.SubmitOutboundMessage("hello", "N0CALL", Encoding.UTF8.GetBytes($"m{i}"), 600, streamId: "par"), ct)));

        for (var i = 0; i < 100 && got.Count < 20; i++) await Task.Delay(100, ct);
        ids.Distinct().Should().HaveCount(20);
        got.Should().HaveCount(20);
        // Delivery order is the order seqs were minted, which is the
        // rows' stream order.
        using var c = DbInfo.GetConnection();
        var expected = c.Table<DbMessage>().OrderBy(m => m.StreamSeq).ToList()
            .Select(m => Encoding.UTF8.GetString(m.Payload)).ToList();
        got.Should().Equal(expected);
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_OrderedDeliveryFails_LaterSendsStillArrive()
    {
        // #384 item 3: the seq was minted but taking it into the inbox
        // failed. That seq can never arrive, so the next one mustn't wait
        // for it (for ever, on a strict stream).
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var got = new List<string>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (got) got.Add(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/hello",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        using (var c = DbInfo.GetConnection()) c.DropTable<DbStreamRecvState>();
        await database.SubmitOutboundMessage("hello", "N0CALL", "one"u8.ToArray(), 600, streamId: "s1");
        using (var c = DbInfo.GetConnection()) c.CreateTable<DbStreamRecvState>();

        await database.SubmitOutboundMessage("hello", "N0CALL", "two"u8.ToArray(), 600, streamId: "s1");
        await database.SubmitOutboundMessage("hello", "N0CALL", "three"u8.ToArray(), 600, streamId: "s1");

        for (var i = 0; i < 50 && got.Count < 2; i++) await Task.Delay(100, ct);
        got.Should().Equal("two", "three");
        (await database.GetUnacknowledgedLocalMessagesForApp("hello")).Select(m => Encoding.UTF8.GetString(m.Payload))
            .Should().Contain("one", "the one that failed is still listed for the app");
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SubmitOutbound_ToAnAppOnThisNode_ExistingStreamWithNoReceivePosition_IsDelivered()
    {
        // #384 item 3, after upgrade: a self-addressed stream whose send
        // counter is already past 1, but which never had a receive
        // position, mustn't wait for seqs from before the upgrade.
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var got = new List<string>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            lock (got) got.Add(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/hello",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbStreamSendState
            {
                Key = DbStreamSendState.MakeKey("N0CALL", "N0CALL", "old"),
                LocalCallsign = "N0CALL", RemoteCallsign = "N0CALL", StreamId = "old",
                NextSeq = 5, UpdatedAt = DateTime.UtcNow,
            });
        }

        await database.SubmitOutboundMessage("hello", "N0CALL", "five"u8.ToArray(), 600, streamId: "old");
        await database.SubmitOutboundMessage("hello", "N0CALL", "six"u8.ToArray(), 600, streamId: "old");

        for (var i = 0; i < 50 && got.Count < 2; i++) await Task.Delay(100, ct);
        got.Should().Equal("five", "six");
        await client.DisconnectAsync(cancellationToken: ct);
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
