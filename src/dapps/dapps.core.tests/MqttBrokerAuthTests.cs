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
    /// <summary>MqttBrokerService assigns a ClientId to an id-less client
    /// through MQTTnet's private ValidatingConnectionEventArgs._connectPacket
    /// field (#340). If an MQTTnet upgrade renames or removes it, this fails
    /// straight away with a clear message instead of only the behaviour tests.</summary>
    [Fact]
    public void MqttnetStillHasTheConnectPacketFieldUsedToAssignClientIds()
    {
        var field = typeof(ValidatingConnectionEventArgs).GetField(
            "_connectPacket",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull(
            "MqttBrokerService sets the ClientId of an id-less client through this private MQTTnet field; "
            + "after an MQTTnet upgrade, find the new way to assign it");
    }

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
            c.CreateTable<DbStreamSendState>();
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
    public async Task RepeatedWrongPasswords_FromOneAddress_LockThatAddressOut()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        broker.AddressResolver = _ => IPAddress.Parse("203.0.113.9");

        for (var i = 0; i < AuthThrottle.FreeFailures + 1; i++)
        {
            var bad = new MqttFactory().CreateMqttClient();
            var act = async () => await bad.ConnectAsync(BuildClientOpts("myapp", "wrong"), ct);
            await act.Should().ThrowAsync<Exception>();
        }

        var locked = new MqttFactory().CreateMqttClient();
        var lockedAct = async () => await locked.ConnectAsync(BuildClientOpts("myapp", token), ct);
        await lockedAct.Should().ThrowAsync<Exception>("even the right token is refused while locked out");

        broker.AddressResolver = _ => IPAddress.Parse("203.0.113.10");
        var other = new MqttFactory().CreateMqttClient();
        await other.ConnectAsync(BuildClientOpts("myapp", token), ct);
        other.IsConnected.Should().BeTrue("another address is not affected");
        await other.DisconnectAsync(cancellationToken: ct);
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

    // ---- Malformed app, callsign or stream id (#313) ----

    [Theory]
    [InlineData("dapps/out/chat/G1AAA\nquit")]
    [InlineData("dapps/out/chat/G1AAA\r\nquit")]
    [InlineData("dapps/out/chat/G1AAA src=G0EVIL")]
    [InlineData("dapps/out/chat/G1AAA/P")]
    public async Task Publish_BadDestination_RefusedAsTopicNameInvalid(string topic)
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        var result = await chat.PublishAsync(Qos1(topic, "x"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.TopicNameInvalid);
        result.ReasonString.Should().StartWith("destination:");
        await Task.Delay(200, ct);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("dapps/out/ch\nat/G1AAA")]
    [InlineData("dapps/out/chat src=G0EVIL/G1AAA")]
    public async Task Publish_BadApp_RefusedAsTopicNameInvalid(string topic)
    {
        // With auth on, an app that isn't the token's is refused first;
        // with it off, the name itself must still be checked.
        systemOptions.AuthRequired = false;
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectAsync(null, null, ct);

        var result = await client.PublishAsync(Qos1(topic, "x"), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.TopicNameInvalid);
        result.ReasonString.Should().StartWith("app:");
        await Task.Delay(200, ct);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("c1\nquit")]
    [InlineData("c1\rquit")]
    [InlineData("c 1")]
    [InlineData("c=1")]
    public async Task Publish_BadStreamId_Refused(string streamId)
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/chat/G1AAA")
            .WithPayload("x"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithUserProperty("dapps-stream", streamId)
            .Build();

        var result = await chat.PublishAsync(message, ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.ImplementationSpecificError);
        result.ReasonString.Should().StartWith("dapps-stream:");
        await Task.Delay(200, ct);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_ValidStreamIdAndSsid_Queued()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/chat/G1AAA-3")
            .WithPayload("x"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithUserProperty("dapps-stream", "chat:tom.2")
            .WithUserProperty("dapps-stream-gap-timeout", "0")
            .Build();

        var result = await chat.PublishAsync(message, ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        var queued = (await database.GetPendingOutboundMessages()).Single();
        queued.Destination.Should().Be("chat@G1AAA-3");
        queued.StreamId.Should().Be("chat:tom.2");
    }

    // #401: the app must choose the gap policy whenever it sets a stream
    // id; there is no default. A value that isn't 0 or a whole number of
    // seconds is refused too, rather than read as missing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("1.5")]
    public async Task Publish_StreamIdWithoutValidGapTimeout_RefusedAndNothingStoredOrMinted(string? gapTimeout)
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/chat/G1AAA")
            .WithPayload("x"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithUserProperty("dapps-stream", "c1");
        if (gapTimeout is not null) builder = builder.WithUserProperty("dapps-stream-gap-timeout", gapTimeout);

        var result = await chat.PublishAsync(builder.Build(), ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.ImplementationSpecificError);
        result.ReasonString.Should().StartWith("dapps-stream-gap-timeout:");
        await Task.Delay(200, ct);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        (await database.GetStreamSendStatesAsync()).Should().BeEmpty("no seq may be minted for a refused publish");
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("600", 600u)]
    public async Task Publish_StreamIdWithGapTimeout_Queued(string gapTimeout, uint expected)
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/chat/G1AAA")
            .WithPayload("x"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithUserProperty("dapps-stream", "c1")
            .WithUserProperty("dapps-stream-gap-timeout", gapTimeout)
            .Build();

        var result = await chat.PublishAsync(message, ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        var queued = (await database.GetPendingOutboundMessages()).Single();
        queued.StreamId.Should().Be("c1");
        queued.StreamSeq.Should().Be(1u);
        queued.StreamGapTimeoutSeconds.Should().Be(expected);
    }

    [Fact]
    public async Task Publish_GapTimeoutWithoutStreamId_IgnoredAndSentUnordered()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/chat/G1AAA")
            .WithPayload("x"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithUserProperty("dapps-stream-gap-timeout", "600")
            .Build();

        var result = await chat.PublishAsync(message, ct);

        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        var queued = (await database.GetPendingOutboundMessages()).Single();
        queued.StreamId.Should().BeNull();
        queued.StreamGapTimeoutSeconds.Should().BeNull();
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

    // ---- Review follow-ups (#350) ----

    [Theory]
    [InlineData("dapps/in/#", false)]
    [InlineData("dapps/in/+", false)]
    [InlineData("dapps/#", true)]
    [InlineData("#", true)]
    public async Task WildcardSubscribe_InTopicWildcardsRefused_BroaderOnesAllowed(string filter, bool granted)
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync("chat", token, ct);

        var sub = await c.SubscribeAsync(filter, cancellationToken: ct);

        sub.Items.Single().ResultCode.Should().Be(granted
            ? MqttClientSubscribeResultCode.GrantedQoS0
            : MqttClientSubscribeResultCode.NotAuthorized);
    }

    [Fact]
    public async Task RefusedSubscribe_Mqtt311_GetsFailureCode()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync("chat", token, ct, MQTTnet.Formatter.MqttProtocolVersion.V311);

        var sub = await c.SubscribeAsync("dapps/in/other", cancellationToken: ct);

        sub.Items.Single().ResultCode.Should().Be(MqttClientSubscribeResultCode.UnspecifiedError,
            "MQTT 3.1.1 SUBACK has a 0x80 failure code, so a refused subscription is not silent");
    }

    [Fact]
    public async Task RefusedAck_Mqtt311_StaysConnected_AndRowUntouched()
    {
        await tokens.CreateOrRotateAsync("chat");
        var evilToken = await tokens.CreateOrRotateAsync("evil");
        await SaveInbound("abc9999", "chat@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var evil = await ConnectAsync("evil", evilToken, ct, MQTTnet.Formatter.MqttProtocolVersion.V311);

        await evil.PublishAsync(Qos1("dapps/ack/evil", "abc9999"), ct);

        evil.IsConnected.Should().BeTrue();
        DbInfo.GetConnection().Find<DbMessage>("abc9999").LocallyDelivered.Should().BeFalse();
    }

    [Fact]
    public async Task WillMessageOnHeartbeat_NotPublished()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var evil = new MqttFactory().CreateMqttClient();
        openClients.Add(evil);
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("will-client").WithCleanSession(true)
            .WithCredentials("chat", token)
            .WithWillTopic("dapps/metrics/heartbeat").WithWillPayload("{\"spoofed\":true}"u8.ToArray())
            .WithWillRetain(true)
            .Build();
        await evil.ConnectAsync(opts, ct);
        await mqttServer.DisconnectClientAsync("will-client", MQTTnet.Protocol.MqttDisconnectReasonCode.UnspecifiedError);
        await Task.Delay(500, ct);

        var monitor = await ConnectAsync("chat", token, ct);
        var seen = 0;
        monitor.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref seen); return Task.CompletedTask; };
        await monitor.SubscribeAsync("dapps/metrics/#", cancellationToken: ct);
        await Task.Delay(500, ct);
        seen.Should().Be(0, "a will on a DAPPS-owned topic must not become the retained heartbeat");
    }

    [Fact]
    public async Task Ack_TransitId_Refused_AndStillForwards()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        await SaveInbound("trn0001", "chat@G1AAA");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        var r = await chat.PublishAsync(Qos1("dapps/ack/chat", "trn0001"), ct);

        r.ReasonCode.Should().Be(MqttClientPublishReasonCode.NotAuthorized);
        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Contain("trn0001");
    }

    [Fact]
    public async Task Ack_TopicAppInOtherCase_OwnMessage_Works()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        await SaveInbound("case001", "chat@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        var r = await chat.PublishAsync(Qos1("dapps/ack/CHAT", "case001"), ct);

        r.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        DbInfo.GetConnection().Find<DbMessage>("case001").LocallyDelivered.Should().BeTrue();
    }

    [Fact]
    public async Task Ack_SsidZeroForm_Works()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        await SaveInbound("ssid0a1", "chat@n0call-0");
        var ct = TestContext.Current.CancellationToken;
        var chat = await ConnectAsync("chat", token, ct);

        var r = await chat.PublishAsync(Qos1("dapps/ack/chat", "ssid0a1"), ct);

        r.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
    }

    [Fact]
    public async Task AuthOff_NoAppClient_OutAndAckStillWork()
    {
        systemOptions.AuthRequired = false;
        await SaveInbound("off0001", "mail@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync(null, null, ct);

        var o = await c.PublishAsync(Qos1("dapps/out/anyapp/G1AAA", "x"), ct);
        var a = await c.PublishAsync(Qos1("dapps/ack/mail", "off0001"), ct);

        o.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        a.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        var row = DbInfo.GetConnection().Find<DbMessage>("off0001");
        row.LocallyDelivered.Should().BeTrue();
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InternalPublishes_StillDelivered()
    {
        systemOptions.AuthRequired = false;
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync(null, null, ct);
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        c.ApplicationMessageReceivedAsync += e => { got.Add(e.ApplicationMessage.Topic); return Task.CompletedTask; };
        await c.SubscribeAsync("dapps/#", cancellationToken: ct);

        (await broker.PublishRetainedAsync("dapps/metrics/heartbeat", "{}"u8.ToArray(), ct)).Should().BeTrue();
        (await broker.PublishAsync("dapps/audit/tx", "{}"u8.ToArray(), ct)).Should().BeTrue();
        await broker.InjectInboundMessage(new DbMessage { Id = "inj0001", Destination = "chat@N0CALL", Payload = "x"u8.ToArray() });

        await Eventually(() => Task.FromResult(got.Count >= 3));
        got.Should().Contain(["dapps/metrics/heartbeat", "dapps/audit/tx", "dapps/in/chat"]);
    }

    [Fact]
    public async Task DestinationWithTwoAts_DeliveredOnTheTopicItCanBeAckedOn()
    {
        var token = await tokens.CreateOrRotateAsync("a@b");
        await SaveInbound("twoat01", "a@b@N0CALL");
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync("a@b", token, ct);
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        c.ApplicationMessageReceivedAsync += e => { got.Add(e.ApplicationMessage.Topic); return Task.CompletedTask; };
        await c.SubscribeAsync("#", cancellationToken: ct);

        await broker.InjectInboundMessage(DbInfo.GetConnection().Find<DbMessage>("twoat01"));
        await Eventually(() => Task.FromResult(!got.IsEmpty));
        got.Should().Contain("dapps/in/a@b");

        var r = await c.PublishAsync(Qos1("dapps/ack/a@b", "twoat01"), ct);
        r.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
    }

    [Fact]
    public async Task ClientEntries_TrackLiveClientsExactly()
    {
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 30; i++)
        {
            var c = await ConnectAsync("chat", token, ct);
            await c.DisconnectAsync(cancellationToken: ct);
        }
        for (var i = 0; i < 10; i++)
        {
            await ConnectAsync("chat", token, ct, clientId: "same-id");
        }
        for (var i = 0; i < 5; i++)
        {
            var bad = new MqttFactory().CreateMqttClient();
            try { await bad.ConnectAsync(BuildClientOpts("chat", "wrong"), ct); } catch { /* expected */ }
            bad.Dispose();
        }
        await ConnectAsync("chat", token, ct, clientId: "drop-id");
        await mqttServer.DisconnectClientAsync("drop-id", MQTTnet.Protocol.MqttDisconnectReasonCode.UnspecifiedError);
        await Task.Delay(500, ct);

        var live = (await mqttServer.GetClientsAsync()).Select(x => x.Id).ToHashSet();
        ClientEntryIds().Should().BeEquivalentTo(live, "entries must track live clients exactly");
        ClientEntryIds().Should().ContainSingle();
    }

    [Fact]
    public async Task RevokeDuringConnectTokenCheck_ConnectionDoesNotSurvive()
    {
        // Mint chat last so the token check hashes every other row first,
        // widening the window between reading the rows and registering.
        for (var i = 0; i < 15; i++) await tokens.CreateOrRotateAsync("pad" + i);
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        (await tokens.VerifyAsync(token)).Should().Be("chat");
        var verifyMs = sw.ElapsedMilliseconds;

        var client = new MqttFactory().CreateMqttClient();
        openClients.Add(client);
        var connect = client.ConnectAsync(BuildClientOpts("chat", token), ct);
        await Task.Delay((int)(verifyMs / 3), ct);
        await tokens.RevokeAsync("chat");
        try { await connect; } catch { /* refused at CONNECT is also fine */ }
        await Task.Delay(1000, ct);

        client.IsConnected.Should().BeFalse($"the token was revoked mid-connect (the check takes {verifyMs} ms)");
    }

    [Fact]
    public async Task LateOrdinaryDisconnectOfOldConnection_KeepsNewConnectionsEntry()
    {
        // Stand-in for the takeover gap: the new connection has written its
        // entry, then the old socket for the same ClientId dies with an
        // ordinary (not Takeover) disconnect. Only the old connection's own
        // entry may be removed.
        var token = await tokens.CreateOrRotateAsync("chat");
        var ct = TestContext.Current.CancellationToken;
        var c = await ConnectAsync("chat", token, ct, clientId: "chat-client");

        var handler = typeof(MqttBrokerService).GetMethod("OnClientDisconnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)handler.Invoke(broker, [new ClientDisconnectedEventArgs(
            "chat-client", null!, MqttClientDisconnectType.NotClean, "127.0.0.1:1", new Dictionary<object, object>())])!;

        var result = await c.PublishAsync(Qos1("dapps/out/chat/G1AAA", "still me"), ct);
        result.ReasonCode.Should().NotBe(MqttClientPublishReasonCode.NotAuthorized);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        (await database.GetPendingOutboundMessages()).Should().ContainSingle();
    }

    // ---- Refused CONNECT reason codes, and clients with no client id (#340) ----

    // mosquitto_pub and mosquitto_sub send an empty client id unless given
    // -i. MQTTnet on its own refuses that as "identifier rejected", which
    // also hid the real reason (no token) behind the wrong return code.
    // The MQTT 3.1.1 cases write the CONNECT by hand, as mosquitto_pub
    // does, and read the CONNACK return code straight off the wire.

    [Theory]
    [InlineData("", null, null)]
    [InlineData("", "myapp", "not-the-token")]
    [InlineData("given-id", null, null)]
    [InlineData("given-id", "myapp", "not-the-token")]
    public async Task Refused_Mqtt311_ReturnCodeIsBadUserNameOrPassword(string clientId, string? user, string? pass)
    {
        await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;

        var code = await RawConnectReturnCode("MQTT", 4, cleanSession: true, clientId, user, pass, ct);

        code.Should().Be((byte)0x04, "a missing or wrong token is a bad user name or password, not a bad client id");
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("", "myapp", "not-the-token")]
    [InlineData("given-id", null, null)]
    [InlineData("given-id", "myapp", "not-the-token")]
    public async Task Refused_Mqtt5_ReasonCodeIsBadUserNameOrPassword(string clientId, string? user, string? pass)
    {
        await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        openClients.Add(client);

        var act = async () => await client.ConnectAsync(BuildClientOpts(user, pass, clientId: clientId), ct);

        var thrown = await act.Should().ThrowAsync<MQTTnet.Adapter.MqttConnectingFailedException>();
        thrown.Which.ResultCode.Should().Be(MqttClientConnectResultCode.BadUserNameOrPassword);
    }

    [Fact]
    public async Task EmptyClientId_Mqtt311_WithToken_AcceptedAndCanPublish()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;

        var c = await ConnectAsync("myapp", token, ct, MQTTnet.Formatter.MqttProtocolVersion.V311, clientId: "");
        await c.PublishAsync(Qos1("dapps/out/myapp/N0DEST-1", "hello"), ct);

        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        (await database.GetPendingOutboundMessages()).Should().ContainSingle();
        ClientEntryIds().Should().ContainSingle(id => id.StartsWith("dapps-"),
            "the broker gives the client an id of its own and tracks it under that");
    }

    [Fact]
    public async Task EmptyClientId_Mqtt5_GetsAssignedClientIdentifier()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();
        openClients.Add(client);

        var result = await client.ConnectAsync(BuildClientOpts("myapp", token, clientId: ""), ct);
        var publish = await client.PublishAsync(Qos1("dapps/out/myapp/N0DEST-1", "hello"), ct);

        result.ResultCode.Should().Be(MqttClientConnectResultCode.Success);
        result.AssignedClientIdentifier.Should().StartWith("dapps-");
        publish.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        ClientEntryIds().Should().Equal(result.AssignedClientIdentifier);
    }

    [Fact]
    public async Task EmptyClientIds_EachGetTheirOwn()
    {
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;

        var a = await ConnectAsync("myapp", token, ct, MQTTnet.Formatter.MqttProtocolVersion.V311, clientId: "");
        var b = await ConnectAsync("myapp", token, ct, MQTTnet.Formatter.MqttProtocolVersion.V311, clientId: "");
        await Task.Delay(300, ct);

        a.IsConnected.Should().BeTrue("a second client with no id must not take over the first one's session");
        b.IsConnected.Should().BeTrue();
        ClientEntryIds().Should().HaveCount(2);
    }

    [Fact]
    public async Task AuthOff_EmptyClientId_Mqtt311_AcceptedAndCanPublish()
    {
        // The reporter's exact case: mosquitto_pub with no -i, -u or -P
        // against a node with app tokens off.
        systemOptions.AuthRequired = false;
        var ct = TestContext.Current.CancellationToken;

        var c = await ConnectAsync(null, null, ct, MQTTnet.Formatter.MqttProtocolVersion.V311, clientId: "");
        await c.PublishAsync(Qos1("dapps/out/hello/G7XYZ-1", "hello from M0LTE"), ct);
        await Eventually(async () => (await database.GetPendingOutboundMessages()).Count == 1);
        (await database.GetPendingOutboundMessages()).Should().ContainSingle();
    }

    [Fact]
    public async Task EmptyClientId_Mqtt311_PersistentSession_IdentifierRejected()
    {
        // MQTT 3.1.1 only lets the server assign an id for a clean
        // session [MQTT-3.1.3-8]. MQTTnet's decoder drops this CONNECT
        // before the broker sees it, closing the connection rather than
        // answering 0x02; either way it must not be let in.
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;

        var code = await RawConnectReturnCode("MQTT", 4, cleanSession: false, "", "myapp", token, ct);

        code.Should().NotBe((byte)0x00);
    }

    [Fact]
    public async Task EmptyClientId_Mqtt31_IdentifierRejected()
    {
        // MQTT 3.1 needs a client id of 1 to 23 characters.
        var token = await tokens.CreateOrRotateAsync("myapp");
        var ct = TestContext.Current.CancellationToken;

        var code = await RawConnectReturnCode("MQIsdp", 3, cleanSession: true, "", "myapp", token, ct);

        code.Should().Be((byte)0x02);
    }

    /// <summary>Send a hand-built MQTT 3.x CONNECT and return the CONNACK
    /// return code byte, or null if the broker closed the connection
    /// without one.</summary>
    private async Task<byte?> RawConnectReturnCode(string protocolName, byte level, bool cleanSession,
        string clientId, string? user, string? pass, CancellationToken ct)
    {
        var body = new List<byte>();
        void Str(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            body.Add((byte)(bytes.Length >> 8));
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }
        Str(protocolName);
        body.Add(level);
        byte flags = 0;
        if (cleanSession) flags |= 0x02;
        if (user is not null) flags |= 0x80;
        if (pass is not null) flags |= 0x40;
        body.Add(flags);
        body.Add(0); body.Add(60); // keep-alive 60 s
        Str(clientId);
        if (user is not null) Str(user);
        if (pass is not null) Str(pass);

        var packet = new List<byte> { 0x10 };
        var remaining = body.Count;
        do
        {
            var digit = (byte)(remaining % 128);
            remaining /= 128;
            if (remaining > 0) digit |= 0x80;
            packet.Add(digit);
        } while (remaining > 0);
        packet.AddRange(body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, brokerPort, timeout.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync(packet.ToArray(), timeout.Token);
        var connack = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(connack, timeout.Token);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        connack[0].Should().Be(0x20, "the broker answers a CONNECT with a CONNACK");
        return connack[3];
    }

    private IEnumerable<string> ClientEntryIds()
    {
        var field = typeof(MqttBrokerService).GetField("clientApps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)field.GetValue(broker)!;
        return dict.Keys.Cast<string>().ToList();
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
