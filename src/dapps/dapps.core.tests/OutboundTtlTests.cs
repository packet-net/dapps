using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// App-interface TTL plumbing on submit (REST + MQTT) and on inbound
/// delivery (MQTT user property). The forwarder-side TTL machinery
/// from A1 already drops expired rows; these tests cover the missing
/// piece - letting the *app* request a residual lifetime when
/// submitting, and surfacing it on delivery so apps can discriminate
/// near-expiry messages from fresh ones.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundTtlTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private Database database = null!;
    private MqttServer mqttServer = null!;
    private MqttBrokerService broker = null!;
    private AppApiController controller = null!;

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-outttl-test-{Guid.NewGuid():N}.db");
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
        controller = new AppApiController(database)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

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

    // ── REST ─────────────────────────────────────────────────────

    [Fact]
    public async Task RestSubmit_WithTtl_PersistsTtl()
    {
        var result = await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray(), Ttl: 600));
        result.Result.Should().BeOfType<OkObjectResult>();

        using var c = DbInfo.GetConnection();
        var row = c.Query<DbMessage>("select * from messages").Single();
        row.Ttl.Should().Be(600);
    }

    [Fact]
    public async Task RestSubmit_WithoutTtl_PersistsNullTtl()
    {
        await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray()));

        using var c = DbInfo.GetConnection();
        c.Query<DbMessage>("select * from messages").Single().Ttl.Should().BeNull();
    }

    [Fact]
    public async Task RestSubmit_NonPositiveTtl_BadRequest()
    {
        var act0 = await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray(), Ttl: 0));
        act0.Result.Should().BeOfType<BadRequestObjectResult>();
        var actNeg = await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray(), Ttl: -10));
        actNeg.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── MQTT submit ──────────────────────────────────────────────

    [Fact]
    public async Task MqttPublishWithDappsTtlUserProperty_PersistsTtl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/myapp/N0DEST")
            .WithPayload("hi"u8.ToArray())
            .WithUserProperty("dapps-ttl", "600")
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await Eventually(async () =>
        {
            var pending = await database.GetPendingOutboundMessages();
            return pending.Count == 1;
        });

        using var c = DbInfo.GetConnection();
        c.Query<DbMessage>("select * from messages").Single().Ttl.Should().Be(600);
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task MqttPublishWithoutDappsTtl_PersistsNullTtl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/myapp/N0DEST")
            .WithPayload("hi"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await Eventually(async () =>
        {
            var pending = await database.GetPendingOutboundMessages();
            return pending.Count == 1;
        });

        using var c = DbInfo.GetConnection();
        c.Query<DbMessage>("select * from messages").Single().Ttl.Should().BeNull();
        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task MqttPublishWithUnparseableTtl_TreatedAsNoTtl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/myapp/N0DEST")
            .WithPayload("hi"u8.ToArray())
            .WithUserProperty("dapps-ttl", "forever-please")  // bad input - fall through to null
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await Eventually(async () =>
        {
            var pending = await database.GetPendingOutboundMessages();
            return pending.Count == 1;
        });

        using var c = DbInfo.GetConnection();
        c.Query<DbMessage>("select * from messages").Single().Ttl.Should().BeNull(
            "malformed dapps-ttl should not crash; fall through to no-expiry");
        await client.DisconnectAsync(cancellationToken: ct);
    }

    // ── Inbound MQTT delivery surfaces dapps-ttl ─────────────────

    [Fact]
    public async Task InboundDelivery_WithStoredTtl_SurfacesResidualAsUserProperty()
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

        // Pre-load a message with TTL=600 created 30s ago - residual
        // ought to be 570 give or take.
        await database.SaveMessage("ttl0001", "hello"u8.ToArray(), salt: 1L,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 600);
        // Force CreatedAt back so residual is computable to a known range.
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Find<DbMessage>("ttl0001")!;
            c.Execute("update messages set CreatedAt=? where Id=?",
                DateTime.UtcNow.AddSeconds(-30).Ticks, "ttl0001");
        }

        // Replay-on-subscribe path doesn't fire for messages saved
        // before we subscribed - push it explicitly via the broker.
        var fresh = (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Single();
        await broker.InjectInboundMessage(fresh);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var ttlProp = msg.UserProperties.Single(p => p.Name == "dapps-ttl");
        var residual = int.Parse(ttlProp.Value);
        residual.Should().BeInRange(560, 600,
            "30s of dwell against a 600s ttl leaves about 570s; allow some slack for test latency");
        await client.DisconnectAsync(cancellationToken: ct);
    }

    // ── Inbound REST surfaces residual ttl ──────────────────────

    [Fact]
    public async Task RestGetInbound_WithStoredTtl_ReturnsResidual()
    {
        await database.SaveMessage("ttlrest", "hello"u8.ToArray(), salt: 2L,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 600);
        // Force CreatedAt back 30s so residual is computable to a known range.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update messages set CreatedAt=? where Id=?",
                DateTime.UtcNow.AddSeconds(-30).Ticks, "ttlrest");
        }

        var result = await controller.GetInbound("myapp");
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<InboundMessage>>().Subject;
        var msg = list.Single(m => m.Id == "ttlrest");
        msg.Ttl.Should().NotBeNull("REST should expose residual TTL on inbound");
        msg.Ttl!.Value.Should().BeInRange(560, 600,
            "30s of dwell against a 600s TTL leaves ~570s residual");
    }

    [Fact]
    public async Task RestGetInbound_WithoutStoredTtl_ReturnsNullTtl()
    {
        await database.SaveMessage("norestl", "hello"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: null);

        var result = await controller.GetInbound("myapp");
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<InboundMessage>>().Subject;
        list.Single(m => m.Id == "norestl").Ttl.Should().BeNull();
    }

    [Fact]
    public async Task InboundDelivery_WithoutStoredTtl_OmitsUserProperty()
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

        await database.SaveMessage("nottl01", "hello"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: null);
        var row = (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Single();
        await broker.InjectInboundMessage(row);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        msg.UserProperties.Should().NotContain(p => p.Name == "dapps-ttl",
            "messages with no stored TTL must not advertise one to apps");
        await client.DisconnectAsync(cancellationToken: ct);
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
