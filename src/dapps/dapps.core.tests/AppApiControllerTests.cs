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

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
