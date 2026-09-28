using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="Database.DeleteExpired"/> against a real SQLite file.
/// We don't run <see cref="TtlSweeperService"/> itself here - that's just
/// the timer wrapper - but the sweeper's only behaviour is "call
/// DeleteExpired", so this covers the contract.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class TtlSweeperTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ttl-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
        }

        database = new Database(NullLogger<Database>.Instance,
            new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" }));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DeleteExpired_LeavesNonExpiringRowsAlone()
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = "noexp01",
            Destination = "x@y",
            Ttl = null,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        });

        var deleted = await database.DeleteExpired(DateTime.UtcNow);

        deleted.Should().Be(0);
        c.Find<DbMessage>("noexp01").Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteExpired_LeavesUnexpiredRowsAlone()
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = "fresh01",
            Destination = "x@y",
            Ttl = 3600,
            CreatedAt = DateTime.UtcNow.AddSeconds(-30),
        });

        var deleted = await database.DeleteExpired(DateTime.UtcNow);

        deleted.Should().Be(0);
        c.Find<DbMessage>("fresh01").Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteExpired_RemovesExpiredMessages()
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = "stale01",
            Destination = "x@y",
            Ttl = 60,
            CreatedAt = DateTime.UtcNow.AddSeconds(-120),
        });

        var deleted = await database.DeleteExpired(DateTime.UtcNow);

        deleted.Should().Be(1);
        c.Find<DbMessage>("stale01").Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpired_HandlesMixedRows()
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = "keep1",
            Destination = "x@y",
            Ttl = 3600,
            CreatedAt = DateTime.UtcNow,
        });
        c.Insert(new DbMessage
        {
            Id = "drop1",
            Destination = "x@y",
            Ttl = 30,
            CreatedAt = DateTime.UtcNow.AddSeconds(-60),
        });

        var deleted = await database.DeleteExpired(DateTime.UtcNow);

        deleted.Should().Be(1);
        c.Find<DbMessage>("keep1").Should().NotBeNull();
        c.Find<DbMessage>("drop1").Should().BeNull();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
