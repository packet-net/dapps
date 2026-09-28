using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// End-to-end check that the TimeProvider injection works: a
/// <see cref="FakeTimeProvider"/> threaded through Database +
/// TtlSweeperService deterministically drives both the timestamps on
/// stored rows AND the cadence of the sweeper's <see cref="PeriodicTimer"/>.
/// Plan A polish - replaces the previous CI-flaky pattern of
/// <c>Task.Delay</c> + tunable test-only ctors.
///
/// Without TimeProvider injection this test couldn't be written: the
/// stored CreatedAt would be wall-clock and TTL "expired" would mean
/// "wait N real seconds." With injection, <c>Advance(31s)</c> is
/// instant and the row deterministically transitions to expired.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class TimeProviderInjectionTests : IAsyncLifetime
{
    private string dbPath = null!;
    private FakeTimeProvider clock = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-tp-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
        }

        clock = new FakeTimeProvider(
            startDateTime: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" });
        database = new Database(NullLogger<Database>.Instance, options, clock);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Database_SaveMessage_StampsInjectedTime()
    {
        // The stored CreatedAt should be the FakeTimeProvider's current
        // time, not the real wall clock. Tests that need to set up
        // "old" rows can now Advance() before saving.
        await database.SubmitOutboundMessage("hello", "N0DEST", "hi"u8.ToArray(), ttlSeconds: 60);

        var rows = await database.GetRecentMessages(1);
        rows.Should().ContainSingle();
        rows[0].CreatedAt.Should().Be(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            "the fake clock's time, not wall clock");
    }

    [Fact]
    public async Task Database_AdvanceClock_ChangesStampedTime()
    {
        await database.SubmitOutboundMessage("hello", "N0DEST", "first"u8.ToArray(), ttlSeconds: 60);
        clock.Advance(TimeSpan.FromMinutes(15));
        await database.SubmitOutboundMessage("hello", "N0DEST", "later"u8.ToArray(), ttlSeconds: 60);

        var rows = (await database.GetRecentMessages(2)).OrderBy(m => m.CreatedAt).ToList();
        rows.Should().HaveCount(2);
        (rows[1].CreatedAt - rows[0].CreatedAt).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task DeleteExpired_AdvanceBeyondTtl_RowSoftDeleted()
    {
        // Row created at T0 with TTL 60s. At T0+30s nothing expires.
        // At T0+90s the row is past its TTL and should be soft-deleted.
        // Pre-injection this test would have to use Task.Delay(60) - slow
        // and CI-flaky. Now it's deterministic via Advance().
        await database.SubmitOutboundMessage("hello", "N0DEST", "expires"u8.ToArray(), ttlSeconds: 60);
        var beforeExpiry = (await database.GetRecentMessages(10)).Count;
        beforeExpiry.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        var deleted = await database.DeleteExpired(clock.GetUtcNow().UtcDateTime);
        deleted.Should().Be(0, "still inside the TTL window");

        clock.Advance(TimeSpan.FromSeconds(60));   // T0+90s, past TTL
        deleted = await database.DeleteExpired(clock.GetUtcNow().UtcDateTime);
        deleted.Should().BeGreaterThan(0);

        (await database.GetRecentMessages(10)).Should().BeEmpty(
            "soft-deleted moves the row out of the active messages table");
        (await database.GetRecentDroppedMessages(10)).Should().HaveCount(1,
            "into the dropped table - keeps an audit trail of TTL expiries");
    }

    [Fact]
    public void OperationalMetrics_RecordForwardSuccess_StampsInjectedTime()
    {
        // OperationalMetrics is a hot path on every forward; fake time
        // gives tests a deterministic LastSuccessAt without the wall
        // clock leaking in.
        var metrics = new OperationalMetrics(clock);
        metrics.RecordForwardSuccess(id: "aaa1234", callsign: "N0PEER-9", bytes: 100);

        var snapshot = metrics.Take();
        var nb = snapshot.Neighbours.Single(n => n.Callsign == "N0PEER-9");
        nb.LastSuccessAt.Should().Be(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void OperationalMetrics_RecentEvents_StampedAtInjectedTime()
    {
        var metrics = new OperationalMetrics(clock);
        metrics.RecordForwardSuccess(id: "aaa1111", callsign: "N0A-9", bytes: 1);
        clock.Advance(TimeSpan.FromMinutes(5));
        metrics.RecordForwardFailure(id: "aaa2222", callsign: "N0B-9", bytes: 2, error: "boom");

        var events = metrics.Take().RecentEvents.OrderBy(e => e.At).ToList();
        events.Should().HaveCount(2);
        events[0].At.Should().Be(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        events[1].At.Should().Be(new DateTime(2026, 5, 1, 12, 5, 0, DateTimeKind.Utc));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
