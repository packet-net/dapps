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

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mqtt-auth-test-{Guid.NewGuid():N}.db");
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
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            MqttPort = brokerPort,
            AuthRequired = true,
        });
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

    private MqttClientOptions BuildClientOpts(string? user, string? pass)
    {
        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..6])
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
