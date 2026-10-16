using AwesomeAssertions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;

namespace dapps.core.uitests;

/// <summary>
/// MQTT-over-WebSocket smoke. The /mqtt route is mounted on Kestrel
/// alongside the dashboard / REST, served by the same broker
/// instance as the TCP listener on :MqttPort. Browsers can speak to
/// it directly with mqtt.js or paho-mqtt's WebSocket transport.
///
/// These tests run against the same subprocess fixture as SmokeTests
/// (UiCollection brings WebAppFixture along) but don't use Playwright;
/// they're WS-protocol-level round trips.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class MqttWebSocketTests(WebAppFixture app)
{
    [Fact]
    public async Task Connect_OverWebSocket_AuthDisabled_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();

        var wsUri = $"{app.BaseUrl.Replace("http://", "ws://")}/mqtt";
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithWebSocketServer(o => o.WithUri(wsUri))
            .WithClientId($"test-{Guid.NewGuid():N}")
            .Build();

        var result = await client.ConnectAsync(opts, ct);

        result.ResultCode.Should().Be(MqttClientConnectResultCode.Success);
        client.IsConnected.Should().BeTrue();

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task PublishToOutTopic_OverWebSocket_PersistsViaSameBrokerAsTcp()
    {
        // The broker is shared between /mqtt and TCP :MqttPort - publishing
        // from a WS client must hit the same DAPPS interceptors that gate
        // dapps/out/<app>/<dest> for queue persistence.
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();

        var wsUri = $"{app.BaseUrl.Replace("http://", "ws://")}/mqtt";
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithWebSocketServer(o => o.WithUri(wsUri))
            .WithClientId($"test-pub-{Guid.NewGuid():N}")
            .Build();

        await client.ConnectAsync(opts, ct);

        var pub = new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/wstest/N0DEST-1")
            .WithPayload("hello-from-ws"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var result = await client.PublishAsync(pub, ct);
        result.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success,
            "the broker should accept the publish; the DAPPS interceptor persists the message and suppresses fan-out");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SubscribeToInTopic_OverWebSocket_AcceptsSubscription()
    {
        // Without auth required, a WS client can subscribe to dapps/in/<app>
        // for any app slug (the auth-required mode constraints are tested
        // separately in MqttBrokerAuthTests over TCP - the same interceptor
        // runs for both transports).
        var ct = TestContext.Current.CancellationToken;
        var client = new MqttFactory().CreateMqttClient();

        var wsUri = $"{app.BaseUrl.Replace("http://", "ws://")}/mqtt";
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithWebSocketServer(o => o.WithUri(wsUri))
            .WithClientId($"test-sub-{Guid.NewGuid():N}")
            .Build();

        await client.ConnectAsync(opts, ct);

        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("dapps/in/wstest", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var result = await client.SubscribeAsync(sub, ct);
        result.Items.Should().ContainSingle();
        result.Items.First().ResultCode.Should().Be(MqttClientSubscribeResultCode.GrantedQoS1);

        await client.DisconnectAsync(cancellationToken: ct);
    }
}
