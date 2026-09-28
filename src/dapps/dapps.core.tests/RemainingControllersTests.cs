using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Smoke tests for the controllers that aren't covered by their own
/// dedicated test file. The corresponding <see cref="Database"/> /
/// <see cref="OutboundMessageManager"/> / etc. methods are tested
/// elsewhere; these tests exist to catch routing / wiring regressions
/// in the thin controllers themselves.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class RemainingControllersTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private TestOptionsMonitor<SystemOptions> optionsMonitor = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-rest-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbSystemOption>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
        }

        optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            NodeHost = "localhost",
            AgwPort = 8000,
            DefaultBearerPort = 0,
            MqttPort = 1883,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    // ── /Neighbours ────────────────────────────────────────────────

    [Fact]
    public async Task NeighboursController_PostListDelete_RoundTrip()
    {
        var ctrl = new NeighboursController(database);

        // POST a neighbour with both bearer hints set.
        var post = await ctrl.Upsert(new NeighbourModel("g7xyz-9", BearerPort: 1, UdpEndpoint: "10.0.0.1:1880"));
        post.Should().BeOfType<NoContentResult>();

        var list = (await ctrl.List()).ToList();
        list.Should().ContainSingle();
        list[0].Callsign.Should().Be("G7XYZ-9", "callsigns are upper-cased server-side");
        list[0].BearerPort.Should().Be(1);
        list[0].UdpEndpoint.Should().Be("10.0.0.1:1880");

        var del = await ctrl.Remove("G7XYZ-9");
        del.Should().BeOfType<NoContentResult>();
        (await ctrl.List()).Should().BeEmpty();
    }

    [Fact]
    public async Task NeighboursController_EmptyCallsign_BadRequest()
    {
        var ctrl = new NeighboursController(database);
        var result = await ctrl.Upsert(new NeighbourModel("", null));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task NeighboursController_DeleteAbsent_NotFound()
    {
        var ctrl = new NeighboursController(database);
        var result = await ctrl.Remove("NOWHERE");
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task NeighboursController_PostBlankUdpEndpoint_TreatedAsNull()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("g7xyz-9", BearerPort: 1, UdpEndpoint: "   "));

        var list = (await ctrl.List()).ToList();
        list.Single().UdpEndpoint.Should().BeNull(
            "blank UdpEndpoint must round-trip as null, not as whitespace");
    }

    // ── /Config ───────────────────────────────────────────────────

    [Fact]
    public async Task ConfigController_GetReturnsCurrentOptions()
    {
        // Pre-seed the systemoptions table the way DbStartup would.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-9" });
            c.Insert(new DbSystemOption { Option = "NodeHost", Value = "bpq.local" });
            c.Insert(new DbSystemOption { Option = "AgwPort", Value = "8001" });
            c.Insert(new DbSystemOption { Option = "DefaultBearerPort", Value = "2" });
            c.Insert(new DbSystemOption { Option = "MqttPort", Value = "1884" });
        }
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        var got = ctrl.Get().Value!;

        got.Callsign.Should().Be("M0LTE-9");
        got.NodeHost.Should().Be("bpq.local");
        got.AgwPort.Should().Be(8001);
        got.DefaultBearerPort.Should().Be(2);
        got.MqttPort.Should().Be(1884);
    }

    [Fact]
    public async Task ConfigController_PostPersistsAndGetRoundtrips()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        var post = await ctrl.Post(new SystemOptions
        {
            Callsign = "M0LTE-2",
            NodeHost = "127.0.0.1",
            AgwPort = 8000,
            DefaultBearerPort = 0,
            MqttPort = 1883,
        });
        post.Should().BeOfType<OkResult>();

        var got = ctrl.Get().Value!;
        got.Callsign.Should().Be("M0LTE-2");
    }

    // ── /Message ─────────────────────────────────────────────────

    [Fact]
    public async Task MessageController_PostMessage_Persists()
    {
        // OutboundMessageManager isn't exercised here - just the
        // direct-write path in MessageController.
        var transport = new ThrowingTransport();
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var omm = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [transport], routingAlgorithm, routingContext);
        var ctrl = new MessageController(database, NullLogger<MessageController>.Instance, omm);

        var result = await ctrl.Post(new DappsMessageModel
        {
            Destination = "myapp@N0DEST",
            TextPayload = "hello",
        });

        result.Should().BeOfType<OkResult>();

        using var c = DbInfo.GetConnection();
        var rows = c.Query<DbMessage>("select * from messages");
        rows.Should().ContainSingle();
        Encoding.UTF8.GetString(rows[0].Payload).Should().Be("hello");
        rows[0].Destination.Should().Be("myapp@N0DEST");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Backhaul that should never be invoked in this test
    /// (MessageController.Post just persists; the manager isn't run).
    /// Used to satisfy the OMM constructor.</summary>
    private sealed class ThrowingTransport : dapps.client.Backhaul.IDappsBackhaul
    {
        public bool CanHandle(dapps.client.Backhaul.BackhaulRoute route) => false;
        public Task<dapps.client.Backhaul.BackhaulSendResult> SendAsync(
            dapps.client.Backhaul.BackhaulMessage message,
            dapps.client.Backhaul.BackhaulRoute route,
            string localCallsign,
            CancellationToken ct)
            => throw new InvalidOperationException("unexpected backhaul call");
    }
}
