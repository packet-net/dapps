using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// MQTT side of Plan A4: when <see cref="SystemOptions.AuthRequired"/>
/// is true, CONNECT requires a username + password matching an issued
/// app token, and publish/subscribe topics are scoped to the
/// authenticated app's slot. Drives the real embedded broker so we
/// exercise the MQTTnet hook wiring, not just our own code.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MqttBrokerAuthTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private Database database = null!;
    private AppTokenStore tokens = null!;
    private MqttServer mqttServer = null!;
    private MqttBrokerService broker = null!;
    private SystemOptions systemOptions = null!;

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mqtt-auth-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
        }

        systemOptions = new SystemOptions
        {
            Callsign = "N0CALL",
            MqttPort = brokerPort,
            AuthRequired = true,
        };
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(systemOptions);
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(brokerPort).Build());
        await mqttServer.StartAsync();
        broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens, mqttServer);

        await broker.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in openClients)
        {
            try { if (c.IsConnected) await c.DisconnectAsync(); } catch { /* ignore */ }
            c.Dispose();
        }
        await broker.StopAsync(CancellationToken.None);
        await mqttServer.StopAsync();
        mqttServer.Dispose();
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Connect_WithoutCredentials_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        var act = async () => await client.ConnectAsync(BuildClientOpts(user: null, pass: null), ct);

        await act.Should().ThrowAsync<Exception>();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_WithInvalidPassword_Rejected()
    {
        await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        var act = async () => await client.ConnectAsync(BuildClientOpts("myapp", "not-the-token"), ct);

        await act.Should().ThrowAsync<Exception>();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_WithValidCredentials_Accepted()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();

        await client.ConnectAsync(BuildClientOpts("myapp", token), ct);

        client.IsConnected.Should().BeTrue();
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task PublishToOutbound_OwnApp_Persists()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        await client.ConnectAsync(BuildClientOpts("myapp", token), ct);

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/myapp/N0DEST-1")
            .WithPayload("hello"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await Eventually(async () =>
        {
            var pending = await database.GetPendingOutboundMessages();
            return pending.Count == 1;
        });

        var pending = await database.GetPendingOutboundMessages();
        pending.Should().ContainSingle();
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task PublishToOutbound_OtherApp_Blocked()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        await client.ConnectAsync(BuildClientOpts("myapp", token), ct);

        // Try to publish on someone else's outbound topic.
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/yourapp/N0DEST-1")
            .WithPayload("not-mine"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        // Give the broker a moment to process; then assert nothing got
        // persisted.
        await Task.Delay(500, ct);
        var pending = await database.GetPendingOutboundMessages();
        pending.Should().BeEmpty(
            "scope check should block a client publishing as another app");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    // ---- Publishes to DAPPS-owned topics (#310, publish side) ----

    [Theory]
    [InlineData("dapps/metrics/heartbeat")]
    [InlineData("dapps/audit/tx")]
    [InlineData("dapps/in/myapp")]
    [InlineData("dapps/in/otherapp")]
    [InlineData("dapps/something-new")]
    public async Task PublishToDappsOwnedTopic_Refused_NotAuthorized(string topic)
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);

        var watcher = await ConnectAsync("myapp", token, ct);
        var seen = 0;
        watcher.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref seen); return Task.CompletedTask; };
        await watcher.SubscribeAsync("dapps/#", cancellationToken: ct);

        var result = await client.PublishAsync(Qos1(topic, "fake"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
        await Task.Delay(300, ct);
        seen.Should().Be(0, "a client publish on a DAPPS-owned topic must not reach anyone");
    }

    [Fact]
    public async Task RetainedPublishToMetrics_Refused_AndNotServedToLaterSubscribers()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/metrics/heartbeat")
            .WithPayload("{\"spoofed\":true}"u8.ToArray())
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);
        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);

        var monitor = await ConnectAsync("myapp", token, ct);
        var seen = 0;
        monitor.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref seen); return Task.CompletedTask; };
        await monitor.SubscribeAsync("dapps/metrics/#", cancellationToken: ct);
        await Task.Delay(400, ct);
        seen.Should().Be(0, "a refused retained publish must not be kept as the retained heartbeat");
    }

    [Fact]
    public async Task AuthOff_PublishToMetrics_StillRefused()
    {
        systemOptions.AuthRequired = false;
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync(null, null, ct);

        var result = await client.PublishAsync(Qos1("dapps/metrics/heartbeat", "fake"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
    }

    [Fact]
    public async Task NonDappsTopic_StillFlows()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);
        var got = 0;
        client.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref got); return Task.CompletedTask; };
        await client.SubscribeAsync("myapp/coordination", cancellationToken: ct);

        await client.PublishAsync(Qos1("myapp/coordination", "hi"), ct);

        await Eventually(() => Task.FromResult(Volatile.Read(ref got) == 1));
        got.Should().Be(1);
    }

    [Fact]
    public async Task SessionFromBeforeAuthRequired_CannotPublishOnDappsTopics()
    {
        systemOptions.AuthRequired = false;
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync(null, null, ct);
        systemOptions.AuthRequired = true;

        var outResult = await client.PublishAsync(Qos1("dapps/out/myapp/N0DEST-1", "x"), ct);
        var metricsResult = await client.PublishAsync(Qos1("dapps/metrics/heartbeat", "x"), ct);

        outResult.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
        metricsResult.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    // ---- Reason codes (#291) ----

    [Fact]
    public async Task PublishToOutbound_OtherApp_PubAckIsNotAuthorized()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);

        var result = await client.PublishAsync(Qos1("dapps/out/yourapp/N0DEST-1", "x"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
    }

    [Fact]
    public async Task SubscribeToOtherAppsInTopic_SubAckIsNotAuthorized()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);

        var own = await client.SubscribeAsync("dapps/in/myapp", cancellationToken: ct);
        var other = await client.SubscribeAsync("dapps/in/yourapp", cancellationToken: ct);

        own.Items.Single().ResultCode.Should().Be(MqttClientSubscribeResultCode.GrantedQoS0);
        other.Items.Single().ResultCode.Should().Be(MqttClientSubscribeResultCode.NotAuthorized);
    }

    [Fact]
    public async Task OutboundStoreFailure_Mqtt5_PubAckIsAnError()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct);
        BreakMessagesTable();

        var result = await client.PublishAsync(Qos1("dapps/out/myapp/N0DEST-1", "x"), ct);

        result.ReasonCode.Should().NotBe(MqttClientPublishReasonCode.Success,
            "the message was not stored, so the app must not be told it was");
    }

    [Fact]
    public async Task OutboundStoreFailure_Mqtt311_ConnectionClosedInsteadOfPubAck()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync("myapp", token, ct, MQTTnet.Formatter.MqttProtocolVersion.V311);
        BreakMessagesTable();

        var act = async () => await client.PublishAsync(Qos1("dapps/out/myapp/N0DEST-1", "x"), ct);

        await act.Should().ThrowAsync<Exception>(
            "an MQTT 3.1.1 PUBACK cannot carry an error, so the broker must not send one");
        await Eventually(() => Task.FromResult(!client.IsConnected));
        client.IsConnected.Should().BeFalse();
    }

    // ---- Ack scoping (#311) ----

    [Fact]
    public async Task Ack_OtherAppsMessage_Refused_AndLeftPending()
    {
        await tokens.CreateOrRotateAsync("chat");
        var evilToken = await tokens.CreateOrRotateAsync("evil");
        await SaveInbound("abc1234", "chat@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var evil = await ConnectAsync("evil", evilToken, ct);

        var result = await evil.PublishAsync(Qos1("dapps/ack/evil", "abc1234"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().ContainSingle(
            "one app must not be able to ack another app's message");
    }

    [Fact]
    public async Task Ack_OwnMessage_MarksDelivered()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        await SaveInbound("abc1234", "chat@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        var result = await chat.PublishAsync(Qos1("dapps/ack/chat", "abc1234"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        await Eventually(async () => (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Count == 0);
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().BeEmpty();
        DbInfo.GetConnection().Find<DbMessage>("abc1234").CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Ack_OwnMessageForAnotherSsidOfOurCall_Refused()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        await SaveInbound("ssid001", "chat@N0CALL-8");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        await chat.PublishAsync(Qos1("dapps/ack/chat", "ssid001"), ct);

        await Task.Delay(300, ct);
        DbInfo.GetConnection().Find<DbMessage>("ssid001").LocallyDelivered.Should().BeFalse(
            "mail for another SSID of our callsign is outbound, not ours to ack");
    }

    [Fact]
    public async Task Ack_QueuedOutboundId_DoesNotStopForwarding()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var outboundId = await database.SubmitOutboundMessage("chat", "G1AAA", "hello"u8.ToArray());
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        await chat.PublishAsync(Qos1("dapps/ack/chat", outboundId), ct);

        await Task.Delay(300, ct);
        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Contain(outboundId,
            "acking an outbound id must not cancel its forwarding");
    }

    // ---- Session takeover (#312) ----

    [Fact]
    public async Task SameClientIdReconnect_KeepsAuthMapping()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var first = await ConnectAsync("chat", token, ct, clientId: "chat-client");
        var second = await ConnectAsync("chat", token, ct, clientId: "chat-client");
        await Task.Delay(300, ct);

        var result = await second.PublishAsync(Qos1("dapps/out/chat/G1AAA", "after reconnect"), ct);
        var sub = await second.SubscribeAsync("dapps/in/chat", cancellationToken: ct);

        result.ReasonCode.Should().NotBe(MqttClientPublishReasonCode.NotAuthorized);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        (await database.GetPendingOutboundMessages()).Should().ContainSingle();
        sub.Items.Single().ResultCode.Should().Be(MqttClientSubscribeResultCode.GrantedQoS0);
    }

    // ---- Revoke and rotate ----

    [Fact]
    public async Task RevokingToken_DisconnectsLiveClient()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var otherToken = await tokens.CreateOrRotateAsync("other");
        var chat = await ConnectAsync("chat", token, ct);
        var other = await ConnectAsync("other", otherToken, ct);

        await tokens.RevokeAsync("chat");

        await Eventually(() => Task.FromResult(!chat.IsConnected));
        chat.IsConnected.Should().BeFalse("a revoked token must not keep working on an open connection");
        other.IsConnected.Should().BeTrue("only the revoked app is disconnected");
    }

    [Fact]
    public async Task RevokingToken_DisconnectsLegacyMixedCaseSession()
    {
        // A row written before names were normalised can carry "Chat";
        // the session then authenticates as "Chat", and revoking "chat"
        // must still drop it.
        var token = await tokens.CreateOrRotateAsync("Chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("Chat", token, ct);

        await tokens.RevokeAsync("chat");

        await Eventually(() => Task.FromResult(!chat.IsConnected));
        chat.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task RotatingToken_DisconnectsLiveClient_NewTokenConnects()
    {
        var oldToken = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", oldToken, ct);

        var newToken = await tokens.CreateOrRotateAsync("chat");

        await Eventually(() => Task.FromResult(!chat.IsConnected));
        chat.IsConnected.Should().BeFalse();
        var again = await ConnectAsync("chat", newToken, ct);
        again.IsConnected.Should().BeTrue();
    }

    private readonly List<IMqttClient> openClients = [];

    private async Task<IMqttClient> ConnectAsync(string? user, string? pass, CancellationToken ct,
        MQTTnet.Formatter.MqttProtocolVersion version = MQTTnet.Formatter.MqttProtocolVersion.V500,
        string? clientId = null)
    {
        var client = new MqttFactory().CreateMqttClient();
        openClients.Add(client);
        await client.ConnectAsync(BuildClientOpts(user, pass, version, clientId), ct);
        return client;
    }

    private static MqttApplicationMessage Qos1(string topic, string payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(System.Text.Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

    private Task SaveInbound(string id, string destination) =>
        database.SaveMessage(id, "x"u8.ToArray(), salt: null,
            destination: destination, sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);

    /// <summary>Make the next outbound insert throw, standing in for a
    /// locked database or the ordered-stream UNIQUE race.</summary>
    private static void BreakMessagesTable()
    {
        using var c = DbInfo.GetConnection();
        c.Execute("drop table messages");
    }

    private MqttClientOptions BuildClientOpts(string? user, string? pass,
        MQTTnet.Formatter.MqttProtocolVersion version = MQTTnet.Formatter.MqttProtocolVersion.V500,
        string? clientId = null)
    {
        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(version)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId(clientId ?? "test-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true);
        if (user is not null && pass is not null)
        {
            b = b.WithCredentials(user, pass);
        }
        return b.Build();
    }

    private static int PickFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
