using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="AppApiController"/> directly with a synthetic
/// <see cref="HttpContext"/>. Covers the scope-check matrix:
///
///   - open mode (no auth): every action proceeds.
///   - closed mode + matching app: every action proceeds.
///   - closed mode + mismatched app: every action returns 403.
///
/// The middleware itself is unit-tested in <see cref="BearerAuthMiddlewareTests"/>;
/// these tests pin down what the controller does *given* the authenticated
/// app the middleware would have stamped.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AppApiControllerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private AppApiController controller = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-appapi-ctrl-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbStreamSendState>();
        }
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        controller = new AppApiController(database)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SubmitOutbound_ValidatesRequiredFields()
    {
        (await controller.SubmitOutbound(new OutboundRequest("", "N0DEST", [1])))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.SubmitOutbound(new OutboundRequest("myapp", "", [1])))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.SubmitOutbound(new OutboundRequest("myapp", "N0DEST", [])))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // #313: these go on the air in the offer line, where a space, CR or
    // LF would add keys or a whole command on the far end.
    [Theory]
    [InlineData("myapp", "G1AAA\nquit", null, "DestCallsign")]
    [InlineData("myapp", "G1AAA\r\nquit", null, "DestCallsign")]
    [InlineData("myapp", "G1AAA src=G0EVIL", null, "DestCallsign")]
    [InlineData("my\napp", "G1AAA", null, "App")]
    [InlineData("myapp src=G0EVIL", "G1AAA", null, "App")]
    [InlineData("myapp", "G1AAA", "c1\nquit", "StreamId")]
    [InlineData("myapp", "G1AAA", "c1\rquit", "StreamId")]
    [InlineData("myapp", "G1AAA", "c 1", "StreamId")]
    public async Task SubmitOutbound_RefusesWhatCannotGoOnTheOfferLine(string app, string dest, string? streamId, string field)
    {
        var result = await controller.SubmitOutbound(new OutboundRequest(app, dest, [1], StreamId: streamId));

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<string>().Which.Should().StartWith(field + ":");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    // #401: the app must choose the gap policy whenever it sets a stream
    // id; there is no default.
    [Fact]
    public async Task SubmitOutbound_StreamIdWithoutGapTimeout_RefusedAndNothingStoredOrMinted()
    {
        var result = await controller.SubmitOutbound(new OutboundRequest("myapp", "G1AAA", [1], StreamId: "c1"));

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<string>().Which.Should().StartWith("StreamGapTimeoutSeconds:");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        (await database.GetStreamSendStatesAsync()).Should().BeEmpty("no seq may be minted for a refused submission");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(600u)]
    public async Task SubmitOutbound_StreamIdWithGapTimeout_Queued(uint gapTimeout)
    {
        var result = await controller.SubmitOutbound(new OutboundRequest("myapp", "G1AAA", [1],
            StreamId: "c1", StreamGapTimeoutSeconds: gapTimeout));

        result.Result.Should().BeOfType<OkObjectResult>();
        var queued = (await database.GetPendingOutboundMessages()).Single();
        queued.StreamId.Should().Be("c1");
        queued.StreamSeq.Should().Be(1u);
        queued.StreamGapTimeoutSeconds.Should().Be(gapTimeout);
    }

    [Fact]
    public async Task SubmitOutbound_GapTimeoutWithoutStreamId_IgnoredAndSentUnordered()
    {
        var result = await controller.SubmitOutbound(new OutboundRequest("myapp", "G1AAA", [1],
            StreamGapTimeoutSeconds: 600));

        result.Result.Should().BeOfType<OkObjectResult>();
        var queued = (await database.GetPendingOutboundMessages()).Single();
        queued.StreamId.Should().BeNull();
        queued.StreamGapTimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public async Task SubmitOutbound_OpenMode_Persists()
    {
        // No AuthenticatedAppKey set -> open mode -> goes through.
        var result = await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray()));

        result.Result.Should().BeOfType<OkObjectResult>();
        (await database.GetPendingOutboundMessages()).Should().ContainSingle();
    }

    [Fact]
    public async Task SubmitOutbound_AuthMatchesPathApp_Persists()
    {
        controller.HttpContext.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "myapp";

        var result = await controller.SubmitOutbound(
            new OutboundRequest("myapp", "N0DEST", "hi"u8.ToArray()));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SubmitOutbound_AuthMismatchesPathApp_Returns403()
    {
        controller.HttpContext.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "myapp";

        var result = await controller.SubmitOutbound(
            new OutboundRequest("yourapp", "N0DEST", "hi"u8.ToArray()));

        result.Result.Should().BeOfType<ForbidResult>();
        (await database.GetPendingOutboundMessages()).Should().BeEmpty(
            "scope mismatch must short-circuit before persistence");
    }

    [Fact]
    public async Task GetInbound_ReturnsRowsForCallingApp()
    {
        await database.SaveMessage("inbx001", "hello"u8.ToArray(), salt: 1L,
            destination: "myapp@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);

        var result = await controller.GetInbound("myapp");
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeOfType<List<InboundMessage>>().Subject;
        list.Should().ContainSingle();
        list[0].Id.Should().Be("inbx001");
        Encoding.UTF8.GetString(list[0].Payload).Should().Be("hello");
    }

    [Fact]
    public async Task GetInbound_RelayedMessage_SourceIsTheOriginator_ViaIsTheLastHop()
    {
        await database.SaveMessage("inbx002", "relayed"u8.ToArray(), salt: 1L,
            destination: "myapp@N0CALL", sourceCallsign: "G6CCC-7", additionalProperties: "{}", ttl: null,
            originatorCallsign: "G6AAA-7");

        var result = await controller.GetInbound("myapp");
        var list = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<List<InboundMessage>>().Subject;
        var m = list.Single();
        m.SourceCallsign.Should().Be("G6AAA-7", "an app replies to who the message is from");
        m.ViaCallsign.Should().Be("G6CCC-7");
        m.OriginatorCallsign.Should().Be("G6AAA-7");
    }

    [Fact]
    public async Task GetInbound_OriginatorUnknown_SourceIsTheLastHop()
    {
        await database.SaveMessage("inbx003", "direct"u8.ToArray(), salt: 1L,
            destination: "myapp@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);

        var result = await controller.GetInbound("myapp");
        var m = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<List<InboundMessage>>().Subject.Single();
        m.SourceCallsign.Should().Be("G0SRC");
        m.ViaCallsign.Should().Be("G0SRC");
        m.OriginatorCallsign.Should().BeNull();
    }

    [Fact]
    public async Task GetInbound_AuthMismatchesPathApp_Returns403()
    {
        controller.HttpContext.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "yourapp";

        var result = await controller.GetInbound("myapp");

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Ack_OpenMode_MarksDelivered()
    {
        await database.SaveMessage("xyz9999", "x"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);

        var result = await controller.Ack("myapp", "xyz9999");

        result.Should().BeOfType<NoContentResult>();
        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().BeEmpty();
    }

    [Fact]
    public async Task Ack_AuthMismatchesPathApp_Returns403_AndLeavesUnacked()
    {
        await database.SaveMessage("xyz9999", "x"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);
        controller.HttpContext.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "yourapp";

        var result = await controller.Ack("myapp", "xyz9999");

        result.Should().BeOfType<ForbidResult>();
        (await database.GetUnacknowledgedLocalMessagesForApp("myapp")).Should().HaveCount(1,
            "the row must remain un-acked when the caller isn't authorised for myapp");
    }

    [Fact]
    public async Task Ack_RepeatOfOwnMessage_StillNoContent()
    {
        await database.SaveMessage("xyz9999", "x"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);

        (await controller.Ack("myapp", "xyz9999")).Should().BeOfType<NoContentResult>();
        (await controller.Ack("myapp", "xyz9999")).Should().BeOfType<NoContentResult>("acks are idempotent");
    }

    [Fact]
    public async Task Ack_OtherAppsMessage_Returns404_AndLeavesUnacked()
    {
        await database.SaveMessage("xyz9999", "x"u8.ToArray(), salt: null,
            destination: "chat@N0CALL", sourceCallsign: "G0SRC", additionalProperties: "{}", ttl: null);
        controller.HttpContext.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "evil";

        var result = await controller.Ack("evil", "xyz9999");

        result.Should().BeOfType<NotFoundResult>();
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().ContainSingle(
            "one app must not be able to ack another app's message");
    }

    [Fact]
    public async Task Ack_QueuedOutboundId_Returns404_AndStillForwards()
    {
        var id = await database.SubmitOutboundMessage("myapp", "G1AAA", "hello"u8.ToArray());

        var result = await controller.Ack("myapp", id);

        result.Should().BeOfType<NotFoundResult>();
        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Contain(id,
            "acking an outbound id must not cancel its forwarding");
    }

    [Fact]
    public async Task Ack_UnknownId_Returns404()
    {
        (await controller.Ack("myapp", "nothere")).Should().BeOfType<NotFoundResult>();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
