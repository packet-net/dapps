using AwesomeAssertions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace dapps.core.tests.Integration;

/// <summary>
/// The MQTT app interface end to end: two real DAPPS daemons on real BPQ,
/// linked over AXIP, but driven through their embedded MQTT brokers
/// instead of the REST app API. An app on A publishes to
/// <c>dapps/out/chat/&lt;B&gt;</c>; the message crosses the air the same
/// way every other end-to-end test proves it does; an app on B, subscribed
/// to <c>dapps/in/chat</c>, gets it, and acks it on <c>dapps/ack/chat</c>.
///
/// <see cref="MqttBrokerRoundTripTests"/> already covers each MQTT topic
/// against the broker and database directly, with no daemon process, no
/// bearer and no peer; this test doesn't repeat that - it proves the same
/// broker, wired into a real running dapps.core process, actually gets an
/// app's message to another node and back, the way <see cref="DappsEndToEndTests"/>
/// proves the REST app API does.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class MqttAppInterfaceIntegrationTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AMessage_SubmittedAndReceivedOverMqtt_CrossesRealBpqAndCanBeAcked()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartNodeAsync("mqttA", fixture.ApplCallA, fixture.AgwPortA, fixture.ApplCallB, ct);
        var b = await StartNodeAsync("mqttB", fixture.ApplCallB, fixture.AgwPortB, fixture.ApplCallA, ct);

        using var aClient = await ConnectAsync(a.MqttPort, ct);
        using var bClient = await ConnectAsync(b.MqttPort, ct);

        var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bClient.ApplicationMessageReceivedAsync += e =>
        {
            received.TrySetResult(e.ApplicationMessage);
            return Task.CompletedTask;
        };
        await bClient.SubscribeAsync("dapps/in/chat", MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var payload = "hello over mqtt, this is A"u8.ToArray();
        await aClient.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"dapps/out/chat/{b.Callsign}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        msg.Topic.Should().Be("dapps/in/chat");
        msg.PayloadSegment.ToArray().Should().Equal(payload);
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be(a.Callsign,
            "the source callsign rides along as an MQTT user property, not just in the payload");
        var id = msg.UserProperties.Single(p => p.Name == "dapps-id").Value;

        // Still unacknowledged until the app acks it - the same REST view
        // DappsEndToEndTests reads elsewhere.
        (await b.InboundAsync("chat", ct)).Should().ContainSingle(m => m.Id == id);

        await bClient.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/ack/chat")
            .WithPayload(id)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await Eventually(async () => (await b.InboundAsync("chat", ct)).Count == 0, TimeSpan.FromSeconds(10), ct);
        (await b.InboundAsync("chat", ct)).Should().BeEmpty("acking on dapps/ack/chat must clear it from the unacknowledged list");

        await aClient.DisconnectAsync(cancellationToken: ct);
        await bClient.DisconnectAsync(cancellationToken: ct);
    }

    private static async Task<IMqttClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = new MqttFactory().CreateMqttClient();
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", port)
            .WithClientId("e2e-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true)
            .Build();
        await client.ConnectAsync(opts, ct);
        return client;
    }

    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200, ct);
        }
    }

    private async Task<DappsDaemon> StartNodeAsync(string name, string callsign, int agwPort, string neighbour, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex,
            [new(neighbour, fixture.AxipPortIndex)], settings: null, ct);
        running.Add(node);
        return node;
    }
}
