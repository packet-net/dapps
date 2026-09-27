using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// D2 follow-up - /Events/payload/{id}: paint the operator-facing
/// behaviours that drive the /Inbound page's click-to-expand flow:
/// 404 on missing rows, valid-UTF8 vs binary disambiguation,
/// truncation at the 4 KiB cap, and that the metadata round-trip
/// keeps source/dest available so the page caption stays accurate
/// even after the SSE event has scrolled out of view.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class EventsControllerPayloadTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private EventsController controller = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-events-payload-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbFragment>();
            c.CreateTable<DbSystemOption>();
        }
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero));
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0SELF" });
        database = new Database(NullLogger<Database>.Instance, options, clock);
        controller = new EventsController(
            new InboundEventBus(),
            database,
            options,
            new OperationalMetrics(clock),
            CreateUpdateChecker());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetPayload_UnknownId_Returns404()
    {
        var result = await controller.GetPayload("nope");
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPayload_TextPayload_ReturnsTextValid()
    {
        var id = await database.SubmitOutboundMessage("hello", "N0DEST", "hello, world"u8.ToArray(), ttlSeconds: 60);

        var preview = (await controller.GetPayload(id)).Value!;

        preview.Id.Should().Be(id);
        preview.ByteLength.Should().Be("hello, world".Length);
        preview.Truncated.Should().BeFalse();
        preview.TextValid.Should().BeTrue();
        preview.Text.Should().Be("hello, world");
        preview.Hex.Should().Be(Convert.ToHexString("hello, world"u8.ToArray()));
        preview.Destination.Should().Be("hello@N0DEST");
        preview.SourceCallsign.Should().Be("N0SELF");
    }

    [Fact]
    public async Task GetPayload_BinaryPayload_FlagsTextInvalid()
    {
        // 0xC3 0x28 is the canonical "invalid UTF-8" pair (lead byte
        // expecting a continuation byte that never arrives).
        var bytes = new byte[] { 0xC3, 0x28, 0x00, 0xFF };
        var id = await database.SubmitOutboundMessage("bin", "N0DEST", bytes, ttlSeconds: 60);

        var preview = (await controller.GetPayload(id)).Value!;

        preview.TextValid.Should().BeFalse();
        preview.Text.Should().BeNull();
        preview.Hex.Should().Be("C328" + "00" + "FF");
        preview.ByteLength.Should().Be(4);
        preview.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetPayload_OversizePayload_TruncatesAndFlags()
    {
        // Disable fragmentation so the 5 KiB row stores as a single
        // DbMessage instead of four; we want to exercise the
        // preview-cap path on a single row.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero));
        var noFragOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
            FragmentThresholdBytes = 0,
        });
        var noFragDb = new Database(NullLogger<Database>.Instance, noFragOptions, clock);
        var noFragController = new EventsController(
            new InboundEventBus(), noFragDb, noFragOptions,
            new OperationalMetrics(clock), CreateUpdateChecker());

        var bytes = Encoding.ASCII.GetBytes(new string('A', 5 * 1024));
        var id = await noFragDb.SubmitOutboundMessage("blob", "N0DEST", bytes, ttlSeconds: 60);

        var preview = (await noFragController.GetPayload(id)).Value!;

        preview.ByteLength.Should().Be(5 * 1024);
        preview.Truncated.Should().BeTrue();
        preview.TextValid.Should().BeTrue();
        preview.Text.Should().Be(new string('A', 4 * 1024));
        // Hex preview is also bounded - we never want to ship 10 KiB of
        // hex string back to the page when the binary itself is huge.
        preview.Hex.Length.Should().Be(4 * 1024 * 2);
    }

    private static UpdateChecker CreateUpdateChecker()
    {
        var noop = new TestOptionsMonitor<SystemOptions>(new SystemOptions());
        return new UpdateChecker(new NoopHttpClientFactory(), noop, TimeProvider.System, NullLogger<UpdateChecker>.Instance);
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
