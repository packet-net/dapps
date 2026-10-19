using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Pages;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// #313: an app name, callsign or stream id holding a space, CR, LF or
/// control character went straight into the DAPPSv1 offer line, where
/// the far end read the rest as extra keys or a separate command. These
/// pin the one shared rule (<see cref="MessageFields"/>), that the
/// dashboard forms and the old /Message endpoint apply it, and that the
/// offer line itself refuses such values whatever the source.
/// </summary>
public sealed class MessageFieldsTests
{
    [Theory]
    [InlineData("chat")]
    [InlineData("Chat")]
    [InlineData("wps-repl")]
    [InlineData("wps_repl")]
    [InlineData("a@b")]
    [InlineData("o'brien")]
    public void AppName_Valid(string app) => MessageFields.AppNameError(app).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chat\nquit")]
    [InlineData("chat\r")]
    [InlineData("chat src=G0EVIL")]
    [InlineData("chat\t")]
    [InlineData("chat\u0000")]
    [InlineData("a=b")]
    [InlineData("chat/room")]
    [InlineData("chat+")]
    [InlineData("chat#")]
    [InlineData("café")]
    public void AppName_Invalid(string? app) => MessageFields.AppNameError(app).Should().NotBeNull();

    [Theory]
    [InlineData("G1AAA")]
    [InlineData("g1aaa")]
    [InlineData("G1AAA-0")]
    [InlineData("G1AAA-15")]
    [InlineData("G0CALLER")]
    [InlineData("N0DEST-7")]
    public void Callsign_Valid(string call) => MessageFields.CallsignError(call).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("G1AAA\nquit")]
    [InlineData("G1AAA\r\nquit")]
    [InlineData("G1AAA src=G0EVIL")]
    [InlineData(" G1AAA")]
    [InlineData("G1AAA-16")]
    [InlineData("G1AAA-")]
    [InlineData("-1")]
    [InlineData("G1AAA-1-2")]
    [InlineData("G1AAA/P")]
    [InlineData("chat@G1AAA")]
    [InlineData("G1AAA=")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("N0DEST-07")]
    [InlineData("N0DEST-00")]
    public void Callsign_Invalid(string? call) => MessageFields.CallsignError(call).Should().NotBeNull();

    [Theory]
    [InlineData("chat@G1AAA", true)]
    [InlineData("a@b@G1AAA-3", true)]
    [InlineData("chat@G1AAA\nquit", false)]
    [InlineData("chat@G1AAA src=G0EVIL", false)]
    [InlineData("chat", false)]
    [InlineData("@G1AAA", false)]
    [InlineData("chat@", false)]
    public void Destination(string destination, bool valid)
        => (MessageFields.DestinationError(destination) is null).Should().Be(valid);

    [Theory]
    [InlineData("c1", true)]
    [InlineData("chat:tom.2", true)]
    [InlineData("café", true)]
    [InlineData("", false)]
    [InlineData("c 1", false)]
    [InlineData("c1\n", false)]
    [InlineData("c1\rquit", false)]
    [InlineData("c\t1", false)]
    [InlineData("c=1", false)]
    [InlineData("c\u00851", false)]
    public void StreamId(string sid, bool valid)
        => (MessageFields.StreamIdError(sid) is null).Should().Be(valid);

    [Fact]
    public void StreamId_Over255Bytes_Invalid()
    {
        MessageFields.StreamIdError(new string('a', 255)).Should().BeNull();
        MessageFields.StreamIdError(new string('a', 256)).Should().NotBeNull();
        MessageFields.StreamIdError(new string('é', 128)).Should().NotBeNull("256 bytes of UTF-8");
    }

    [Fact]
    public void AppName_Over64Characters_Invalid()
    {
        // An 8000-character name once put a line too long for the
        // receiver's buffer on the air.
        MessageFields.AppNameError(new string('a', MessageFields.MaxAppNameLength)).Should().BeNull();
        MessageFields.AppNameError(new string('a', MessageFields.MaxAppNameLength + 1)).Should().NotBeNull();
        MessageFields.AppNameError(new string('a', 8200)).Should().NotBeNull();
        MessageFields.DestinationError(new string('a', 8200) + "@G1AAA").Should().NotBeNull();
    }

    [Fact]
    public void AppTokens_FollowTheSameAppNameRule()
    {
        AppTokenStore.TryNormalizeAppName("a=b", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        AppTokenStore.TryNormalizeAppName("a@b", out var ok, out _).Should().BeTrue();
        ok.Should().Be("a@b");
    }
}

/// <summary>The offer line refuses any field that would break the line,
/// whatever handed it the value (#313).</summary>
public sealed class OfferLineInjectionTests
{
    [Theory]
    [InlineData("chat@G1AAA\nquit")]
    [InlineData("chat@G1AAA\r")]
    [InlineData("chat@G1AAA src=G0EVIL")]
    [InlineData("chat@G1AAA\tx=y")]
    [InlineData("chat@G1AAA\u0000")]
    [InlineData("")]
    public void RefusesABadDestination(string destination)
    {
        var build = () => OfferLine.Build("ihave", "abc1234", 1L, "p", null, destination, 5);
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RefusesABadOriginatorStreamIdMasterIdIdOrFormat()
    {
        ((Action)(() => OfferLine.Build("ihave", "abc1234", 1L, "p", null, "chat@G1AAA", 5, originator: "G0X\nquit")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => OfferLine.Build("ihave", "abc1234", 1L, "p", null, "chat@G1AAA", 5,
                streamId: "c1\nquit", streamSeq: 1, streamGapTimeoutSeconds: 0)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => OfferLine.Build("ihave", "abc1234", 1L, "p", null, "chat@G1AAA", 5,
                masterId: "m 1", fragmentIndex: 1, fragmentTotal: 2)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => OfferLine.Build("ihave", "abc\n234", 1L, "p", null, "chat@G1AAA", 5)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => OfferLine.Build("ihave", "abc1234", 1L, "p x=1", null, "chat@G1AAA", 5)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AValidLine_StillBuildsAndParses()
    {
        var line = OfferLine.Build("ihave", "abc1234", 1L, "p", null, "chat@G1AAA-3", 5, ttl: 60,
            originator: "G0ORIG", streamId: "chat:tom.2", streamSeq: 1, streamGapTimeoutSeconds: 0);

        var parsed = IHaveValidator.Validate(line.TrimEnd('\n'));
        parsed.IsValid.Should().BeTrue(parsed.Error);
        parsed.Offer!.Destination.Should().Be("chat@G1AAA-3");
        parsed.Offer.StreamId.Should().Be("chat:tom.2");
    }

    [Theory]
    [InlineData("ihave abc1234 len=5 fmt=p dst=chat@G1AAA\tx=y")]
    [InlineData("ihave abc1234 len=5 fmt=p dst=chat@G1AAA\u0000")]
    [InlineData("ihave abc1234 len=5 fmt=p dst=chat@G1AAA src=G0X\u0085")]
    [InlineData("ihave abc1234 len=5 fmt=p dst=chat@G1AAA sid=c\u000b1 sn=1 gt=0")]
    public void TheReceiver_RefusesWhatTheSenderWouldNotSend(string line)
    {
        var result = IHaveValidator.Validate(line);
        result.IsValid.Should().BeFalse();
        result.Id.Should().Be("abc1234", "so the sender is told which message, with an error reply");
    }

    [Fact]
    public void TheReceiver_RefusesAControlCharacterInTheId()
        => IHaveValidator.Validate("ihave abc\u00001234 len=5 fmt=p dst=chat@G1AAA").IsValid.Should().BeFalse();

    [Fact]
    public void TheDatagramCodec_RefusesWhatALineCouldNotCarry()
    {
        var bad = new BackhaulMessage("abc1234", "chat@G1AAA\nquit", 1L, null, "x"u8.ToArray());
        var bytes = BackhaulMessageCodec.Encode(bad);

        var decode = () => BackhaulMessageCodec.Decode(bytes);
        decode.Should().Throw<InvalidDataException>();

        var good = BackhaulMessageCodec.Encode(bad with { Destination = "chat@G1AAA" });
        BackhaulMessageCodec.Decode(good).Destination.Should().Be("chat@G1AAA");
    }
}

/// <summary>The dashboard's Compose and Quick send forms and the old
/// /Message endpoint refuse what the offer line could not carry, and
/// store nothing (#313).</summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DashboardSendValidationTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-send-validation-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbSystemOption>();
        }
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" });
        database = new Database(NullLogger<Database>.Instance, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static int StoredCount()
    {
        using var c = DbInfo.GetConnection();
        return c.Table<DbMessage>().Count();
    }

    [Theory]
    [InlineData("chat", "G1AAA\nquit")]
    [InlineData("chat", "G1AAA\r\nquit")]
    [InlineData("chat", "G1AAA src=G0EVIL")]
    [InlineData("chat\nquit", "G1AAA")]
    [InlineData("chat src=G0EVIL", "G1AAA")]
    public async Task Compose_RefusesABadAppOrDestination(string app, string destination)
    {
        var page = new ComposeModel(database, options, NullLogger<ComposeModel>.Instance)
        {
            Form = new ComposeModel.ComposeForm { App = app, Destination = destination, Payload = "hi" },
        };

        await page.OnPostAsync();

        page.FlashError.Should().NotBeNullOrEmpty();
        page.FlashOk.Should().BeNull();
        StoredCount().Should().Be(0);
    }

    [Fact]
    public async Task Compose_AValidMessage_IsQueued()
    {
        var page = new ComposeModel(database, options, NullLogger<ComposeModel>.Instance)
        {
            Form = new ComposeModel.ComposeForm { App = "chat", Destination = "g1aaa-2", Payload = "hi" },
        };

        await page.OnPostAsync();

        page.FlashError.Should().BeNull();
        (await database.GetPendingOutboundMessages()).Single().Destination.Should().Be("chat@G1AAA-2");
    }

    [Theory]
    [InlineData("chat", "G1AAA\nquit")]
    [InlineData("chat src=G0EVIL", "G1AAA")]
    public async Task QuickSend_RefusesABadAppOrDestination(string app, string destination)
    {
        var page = new IndexModel(database, options, new SystemOptionsBackedTxGate(options, new OpenTxKillSwitchSignal()),
            NullLogger<IndexModel>.Instance)
        {
            Send = new IndexModel.SendForm { App = app, Destination = destination, Payload = "hi" },
        };

        await page.OnPostSendAsync();

        page.FlashError.Should().NotBeNullOrEmpty();
        StoredCount().Should().Be(0);
    }

    [Theory]
    [InlineData("G1AAA\nquit")]
    [InlineData("G1AAA src=G0EVIL")]
    [InlineData("G1AAA-07")]
    public async Task Config_RefusesABadNodeCallsign(string callsign)
    {
        // The node's callsign goes out as src= on every message it sends.
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        var result = await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["Callsign"] = callsign });

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().StartWith("Callsign:");
        store.CurrentValue.Callsign.Should().NotBe(callsign);
    }

    [Fact]
    public async Task Config_AcceptsAGoodCallsign_AndAnUnchangedOldOne()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var ctrl = new ConfigController(store, new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance));

        (await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["Callsign"] = "M0LTE-2" })).Should().BeOfType<OkResult>();
        store.CurrentValue.Callsign.Should().Be("M0LTE-2");

        // A value stored before the rule existed must not stop the
        // Settings page (which always sends the callsign) saving the rest.
        var legacy = store.CurrentValue;
        legacy.Callsign = "M0LTE/P";
        await store.SaveAsync(legacy);
        (await ctrl.Post(new System.Text.Json.Nodes.JsonObject { ["Callsign"] = "M0LTE/P", ["MqttPort"] = 1884 }))
            .Should().BeOfType<OkResult>();
        store.CurrentValue.MqttPort.Should().Be(1884);
    }

    private sealed class AlwaysValidAntiforgery : Microsoft.AspNetCore.Antiforgery.IAntiforgery
    {
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetAndStoreTokens(Microsoft.AspNetCore.Http.HttpContext httpContext) => throw new NotSupportedException();
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetTokens(Microsoft.AspNetCore.Http.HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(Microsoft.AspNetCore.Http.HttpContext httpContext) => Task.FromResult(true);
        public Task ValidateRequestAsync(Microsoft.AspNetCore.Http.HttpContext httpContext) => Task.CompletedTask;
        public void SetCookieTokenAndHeader(Microsoft.AspNetCore.Http.HttpContext httpContext) { }
    }

    [Theory]
    [InlineData("G1AAA SRC=G0EVIL")]
    [InlineData("G1AAA\nQUIT")]
    [InlineData("G1AAA-07")]
    public async Task Setup_RefusesABadNodeCallsign(string callsign)
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        // The bearer step only runs mid-setup: password set, callsign
        // still the placeholder.
        var passwords = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await passwords.SetAsync("hunter2hunter2");
        var placeholder = store.CurrentValue;
        placeholder.Callsign = DbStartup.PlaceholderCallsign;
        await store.SaveAsync(placeholder);
        var page = new SetupModel(passwords, store, new AlwaysValidAntiforgery())
        {
            Callsign = callsign,
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("test")),
                },
            },
        };

        await page.OnPostBearerAsync();

        page.Error.Should().NotBeNullOrEmpty();
        store.CurrentValue.Callsign.Should().NotBe(callsign.Trim().ToUpperInvariant());
    }

    [Theory]
    [InlineData("chat@G1AAA\nquit")]
    [InlineData("chat@G1AAA src=G0EVIL")]
    [InlineData("G1AAA")]
    public async Task MessageEndpoint_RefusesABadDestination(string destination)
    {
        var routingContext = new DatabaseRoutingContext(database, options);
        var omm = new OutboundMessageManager(database, NullLoggerFactory.Instance, options, [],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance), routingContext);
        var ctrl = new MessageController(database, NullLogger<MessageController>.Instance, omm);

        var result = await ctrl.Post(new DappsMessageModel { Destination = destination, TextPayload = "hi" });

        result.Should().BeOfType<BadRequestObjectResult>();
        StoredCount().Should().Be(0);
    }
}
