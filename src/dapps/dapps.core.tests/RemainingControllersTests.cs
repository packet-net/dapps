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
            DefaultPort = 1,
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
        var post = await ctrl.Upsert(new NeighbourModel("g7xyz-9", Port: 1, UdpEndpoint: "10.0.0.1:1880"));
        post.Should().BeOfType<NoContentResult>();

        var list = (await ctrl.List()).ToList();
        list.Should().ContainSingle();
        list[0].Callsign.Should().Be("G7XYZ-9", "callsigns are upper-cased server-side");
        list[0].Port.Should().Be(1);
        list[0].UdpEndpoint.Should().Be("10.0.0.1:1880");

        var del = await ctrl.Remove("G7XYZ-9");
        del.Should().BeOfType<NoContentResult>();
        (await ctrl.List()).Should().BeEmpty();
    }

    [Fact]
    public async Task NeighboursController_PreviousCallsign_RenamesTheNeighbour()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("g7xyz-9", Port: 1, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: "GB7BDH"));

        (await ctrl.Upsert(new NeighbourModel("g7xyz-3", Port: 2, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: "GB7BDH", PreviousCallsign: "g7xyz-9")))
            .Should().BeOfType<NoContentResult>();

        var list = (await ctrl.List()).ToList();
        list.Should().ContainSingle();
        list[0].Callsign.Should().Be("G7XYZ-3");
        list[0].Port.Should().Be(2);
        list[0].ConnectVia.Should().Be("GB7BDH");
    }

    [Fact]
    public async Task NeighboursController_RenameOntoAnotherNeighbour_IsRefused()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1));
        await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 2));

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 1, PreviousCallsign: "G7XYZ-9")))
            .Should().BeOfType<ConflictObjectResult>();

        var list = (await ctrl.List()).ToList();
        list.Should().HaveCount(2, "the refused rename changes neither row");
        list.Single(n => n.Callsign == "G7XYZ-3").Port.Should().Be(2);
    }

    [Fact]
    public async Task NeighboursController_RenameOfAMissingNeighbour_IsNotFound()
    {
        var ctrl = new NeighboursController(database);

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 1, PreviousCallsign: "G7XYZ-9")))
            .Should().BeOfType<NotFoundObjectResult>();
        (await ctrl.List()).Should().BeEmpty();
    }

    [Fact]
    public async Task NeighboursController_RenameOfAMissingNeighbour_LeavesTheOthersAlone()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 2, CompressionEnabled: false));

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 5, PreviousCallsign: "G7XYZ-9")))
            .Should().BeOfType<NotFoundObjectResult>("a 404 MUST NOT fall back to updating the row named by Callsign");

        var row = (await ctrl.List()).Should().ContainSingle().Subject;
        row.Callsign.Should().Be("G7XYZ-3");
        row.Port.Should().Be(2);
        row.CompressionEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task NeighboursController_RefusedRename_LeavesTheSourceRowAlone()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: "GB7BDH", SessionTailSeconds: 30));
        await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 2));

        (await ctrl.Upsert(new NeighbourModel("g7xyz-3", Port: 7, PreviousCallsign: "g7xyz-9")))
            .Should().BeOfType<ConflictObjectResult>();

        var source = (await ctrl.List()).Single(n => n.Callsign == "G7XYZ-9");
        source.Port.Should().Be(1);
        source.ConnectVia.Should().Be("GB7BDH");
        source.SessionTailSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("g7xyz-9")]
    [InlineData(" G7XYZ-9 ")]
    public async Task NeighboursController_NoOrSamePreviousCallsign_IsThePlainUpsert(string? previous)
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1));

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 4, PreviousCallsign: previous)))
            .Should().BeOfType<NoContentResult>();

        var row = (await ctrl.List()).Should().ContainSingle().Subject;
        row.Callsign.Should().Be("G7XYZ-9");
        row.Port.Should().Be(4);
    }

    [Fact]
    public async Task NeighboursController_BlankPreviousCallsign_StillAddsANewNeighbour()
    {
        var ctrl = new NeighboursController(database);

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1, PreviousCallsign: "")))
            .Should().BeOfType<NoContentResult>();

        (await ctrl.List()).Should().ContainSingle().Which.Callsign.Should().Be("G7XYZ-9");
    }

    [Fact]
    public async Task NeighboursController_RenamedAwayCallsign_CanBeAddedAgain()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1));
        await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 1, PreviousCallsign: "G7XYZ-9"));

        (await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 3))).Should().BeOfType<NoContentResult>();

        (await ctrl.List()).Select(n => n.Callsign).Should().BeEquivalentTo("G7XYZ-3", "G7XYZ-9");
    }

    [Fact]
    public async Task NeighboursController_RenamingAPortPendingRow_ChecksThatRow()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "G7XYZ-9", BearerPort = 0, PortPending = true });
        }
        var ctrl = new NeighboursController(database);

        // The port shown as null, posted back under a new callsign, is
        // still the pending row's port: refused, and nothing changes.
        (await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: null, PreviousCallsign: "G7XYZ-9")))
            .Should().BeOfType<ConflictObjectResult>();
        var row = (await ctrl.List()).Should().ContainSingle().Subject;
        row.Callsign.Should().Be("G7XYZ-9");
        row.PortPending.Should().BeTrue();

        // A real port renames it and clears the pending flag.
        (await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 2, PreviousCallsign: "G7XYZ-9")))
            .Should().BeOfType<NoContentResult>();
        row = (await ctrl.List()).Should().ContainSingle().Subject;
        row.Callsign.Should().Be("G7XYZ-3");
        row.Port.Should().Be(2);
        row.PortPending.Should().BeFalse();
    }

    [Fact]
    public async Task NeighboursController_List_DoesNotSendPreviousCallsign()
    {
        var ctrl = new NeighboursController(database);
        await ctrl.Upsert(new NeighbourModel("G7XYZ-9", Port: 1));

        System.Text.Json.JsonSerializer.Serialize((await ctrl.List()).Single(), System.Text.Json.JsonSerializerOptions.Web)
            .Should().NotContain("previousCallsign", "older clients see the same GET shape as before");
    }

    [Fact]
    public async Task NeighboursController_EmptyCallsign_BadRequest()
    {
        var ctrl = new NeighboursController(database);
        var result = await ctrl.Upsert(new NeighbourModel("", null));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("DAPPS\rC GB7XXX|DAPPSv1>")]
    [InlineData("C\tGB7XXX|Connected\nDAPPS|DAPPSv1>")]
    [InlineData("C GB7XXX\u0003|Connected\nDAPPS|DAPPSv1>")]
    [InlineData("C GB7ÄXX|Connected\nDAPPS|DAPPSv1>")]
    public async Task NeighboursController_ConnectScriptLineThatIsNotOneCommand_IsRefused(string script)
    {
        // Each send is typed at a node prompt then a CR (#309): a CR or
        // other control character inside it would start another command.
        var ctrl = new NeighboursController(database);

        var result = await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 1, ConnectScript: script));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().StartWith("Connect script:");
        (await ctrl.List()).Should().BeEmpty();
    }

    [Fact]
    public async Task NeighboursController_ConnectViaThatIsNotACallsign_IsRefused()
    {
        var ctrl = new NeighboursController(database);

        var result = await ctrl.Upsert(new NeighbourModel("G7XYZ-3", Port: 1, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: "G7XYZ\rC GB7XXX"));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().StartWith("Connect via:");
        (await ctrl.List()).Should().BeEmpty();
    }

    [Fact]
    public async Task NeighboursController_ConnectVia_RoundTrips_AndNeedsAScript()
    {
        var ctrl = new NeighboursController(database);

        (await ctrl.Upsert(new NeighbourModel("g7xyz-3", Port: 1, ConnectVia: "g7xyz")))
            .Should().BeOfType<BadRequestObjectResult>("without a script DAPPS would sit at the node's prompt");

        (await ctrl.Upsert(new NeighbourModel("g7xyz-3", Port: 1, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: " g7xyz ")))
            .Should().BeOfType<NoContentResult>();
        (await ctrl.List()).Single().ConnectVia.Should().Be("G7XYZ");

        await ctrl.Upsert(new NeighbourModel("g7xyz-3", Port: 1, ConnectScript: "DAPPS|DAPPSv1>", ConnectVia: ""));
        (await ctrl.List()).Single().ConnectVia.Should().BeNull("a blank Connect via dials the Callsign itself");
    }

    // A script typed the way it's typed by hand starts with the connect to
    // the first node. DAPPS makes that connect itself (#220), so it's
    // stored as Connect via.

    [Fact]
    public async Task NeighboursController_ScriptStartingAtTheFirstHop_BecomesConnectVia()
    {
        var ctrl = new NeighboursController(database);

        (await ctrl.Upsert(new NeighbourModel("gb7agm-3", Port: 1,
            ConnectScript: "C GB7BDH|Connected\nC 14 GB7AGM-3|Connected\nDAPPS|DAPPSv1>")))
            .Should().BeOfType<NoContentResult>();

        var row = (await ctrl.List()).Single();
        row.ConnectVia.Should().Be("GB7BDH");
        row.ConnectScript.Should().Be("C 14 GB7AGM-3|Connected\nDAPPS|DAPPSv1>");
    }

    [Fact]
    public async Task NeighboursController_ExplicitConnectVia_LeavesTheScriptAlone()
    {
        var ctrl = new NeighboursController(database);

        await ctrl.Upsert(new NeighbourModel("gb7agm-3", Port: 1, ConnectVia: "GB7BDH",
            ConnectScript: "C G0NODE3|Connected\nDAPPS|DAPPSv1>"));

        var row = (await ctrl.List()).Single();
        row.ConnectVia.Should().Be("GB7BDH");
        row.ConnectScript.Should().Be("C G0NODE3|Connected\nDAPPS|DAPPSv1>",
            "with Connect via given, a C line is typed at that node's prompt");
    }

    [Theory]
    [InlineData("C 2 MB7NPW|Connected\nDAPPS|DAPPSv1>")] // a port number: typed at a node prompt
    [InlineData("SWITCH|SWITCH\nDAPPS|DAPPSv1>")]
    public async Task NeighboursController_OtherFirstSteps_AreKept(string script)
    {
        var ctrl = new NeighboursController(database);

        await ctrl.Upsert(new NeighbourModel("mb7npw-3", Port: 1, ConnectScript: script));

        var row = (await ctrl.List()).Single();
        row.ConnectVia.Should().BeNull();
        row.ConnectScript.Should().Be(script);
    }

    [Fact]
    public async Task NeighboursController_FirstStepConnectingToThePeer_IsDropped()
    {
        var ctrl = new NeighboursController(database);

        await ctrl.Upsert(new NeighbourModel("mb7npw", Port: 1, ConnectScript: "C MB7NPW|Connected\nDAPPS|DAPPSv1>"));

        var row = (await ctrl.List()).Single();
        row.ConnectVia.Should().BeNull("the peer is dialled anyway");
        row.ConnectScript.Should().Be("DAPPS|DAPPSv1>");
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
        await ctrl.Upsert(new NeighbourModel("g7xyz-9", Port: 1, UdpEndpoint: "   "));

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
            c.Insert(new DbSystemOption { Option = "DefaultPort", Value = "3" });
            c.Insert(new DbSystemOption { Option = "MqttPort", Value = "1884" });
        }
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        var got = ctrl.Get().Value!;

        got.Callsign.Should().Be("M0LTE-9");
        got.NodeHost.Should().Be("bpq.local");
        got.AgwPort.Should().Be(8001);
        got.DefaultPort.Should().Be(3);
        got.MqttPort.Should().Be(1884);
    }

    [Fact]
    public async Task ConfigController_PostPersistsAndGetRoundtrips()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        var post = await ctrl.Post(new System.Text.Json.Nodes.JsonObject
        {
            ["Callsign"] = "M0LTE-2",
            ["NodeHost"] = "127.0.0.1",
            ["AgwPort"] = 8000,
            ["DefaultPort"] = 2,
            ["MqttPort"] = 1883,
        });
        post.Should().BeOfType<OkResult>();

        var got = ctrl.Get().Value!;
        got.Callsign.Should().Be("M0LTE-2");
        got.DefaultPort.Should().Be(2);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public async Task ConfigController_TheOldDefaultBearerPort_IsTakenAsCountingFromZero(int counted, int expected)
    {
        // #233: up to 0.52 it was DefaultBearerPort, counting from 0. An
        // old caller's value still means the same radio port. Deprecated.
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        (await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["DefaultBearerPort"] = counted })).Should().BeOfType<OkResult>();

        ctrl.Get().Value!.DefaultPort.Should().Be(expected);
    }

    [Fact]
    public async Task ConfigController_DefaultPortWins_OverTheOldName_AndZeroIsRefused()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        (await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["DefaultPort"] = 5, ["DefaultBearerPort"] = 0 })).Should().BeOfType<OkResult>();
        ctrl.Get().Value!.DefaultPort.Should().Be(5);

        (await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["DefaultPort"] = 0 })).Should().BeOfType<BadRequestObjectResult>();
        ctrl.Get().Value!.DefaultPort.Should().Be(5);
    }

    [Fact]
    public void ConfigMerge_WithNothingToChange_KeepsEverySetting()
    {
        // Every setting must survive the JSON round trip the merge does;
        // one that didn't (say a later [JsonIgnore] or init-only property)
        // would quietly change on every save.
        var original = new SystemOptions();
        foreach (var p in typeof(SystemOptions).GetProperties().Where(p => p.CanWrite))
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object value = t == typeof(string) ? "x-" + p.Name
                : t == typeof(int) ? 7
                : t == typeof(double) ? 0.1 + 0.2
                : t == typeof(bool) ? !(bool)(p.GetValue(original) ?? false)
                : t.IsEnum ? Enum.GetValues(t).Cast<object>().Last()
                : throw new InvalidOperationException($"add a test value for {t} ({p.Name})");
            p.SetValue(original, value);
        }

        var merged = ConfigController.Merge(original, new System.Text.Json.Nodes.JsonObject());

        foreach (var p in typeof(SystemOptions).GetProperties().Where(p => p.CanWrite))
        {
            p.GetValue(merged).Should().Be(p.GetValue(original), p.Name);
        }
    }

    [Theory]
    [InlineData("{\"MqttPort\":null}")]
    [InlineData("{\"MqttPort\":\"abc\"}")]
    [InlineData("{\"MqttPort\":1.5}")]
    [InlineData("{\"ProbeStrategy\":\"nope\"}")]
    [InlineData("{\"Callsign\":null}")]
    public async Task ConfigController_Post_BadValue_Is400_AndChangesNothing(string body)
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));
        var before = store.CurrentValue.MqttPort;

        var post = await ctrl.Post(System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject());

        post.Should().BeOfType<BadRequestObjectResult>();
        store.CurrentValue.MqttPort.Should().Be(before);
    }

    [Fact]
    public async Task ConfigController_Post_KeepsSettingsItWasNotSent()
    {
        // The Settings page doesn't send TxEnabled, RoutingAlgorithm and
        // others. Saving it must not put them back to their defaults: an
        // operator's TX stop stays stopped.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbSystemOption { Option = "TxEnabled", Value = "False" });
            c.Insert(new DbSystemOption { Option = "RoutingAlgorithm", Value = "meshcore" });
            c.Insert(new DbSystemOption { Option = "TransmissionAuditRetentionDays", Value = "7" });
            c.Insert(new DbSystemOption { Option = "RouteGossipStalenessHours", Value = "0" });
            c.Insert(new DbSystemOption { Option = "ProbeStrategy", Value = "Overnight" });
        }
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        // As the Settings page sends it: PascalCase, a subset, an enum as a string.
        var post = await ctrl.Post(new System.Text.Json.Nodes.JsonObject
        {
            ["Callsign"] = "M0LTE-3",
            ["MqttPort"] = 1885,
            ["ProbingEnabled"] = true,
            ["NotASetting"] = "ignored",
        });
        post.Should().BeOfType<OkResult>();

        var got = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance).CurrentValue;
        got.Callsign.Should().Be("M0LTE-3");
        got.MqttPort.Should().Be(1885);
        got.ProbingEnabled.Should().BeTrue();
        got.TxEnabled.Should().BeFalse("the TX stop wasn't on the page, so it stays stopped");
        got.RoutingAlgorithm.Should().Be("meshcore");
        got.TransmissionAuditRetentionDays.Should().Be(7);
        got.RouteGossipStalenessHours.Should().Be(0, "0 turns gossip pulls off and must survive a load and a save");
        got.ProbeStrategy.Should().Be(ProbeStrategy.Overnight);
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
