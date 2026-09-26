using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Database-level tests for the neighbour CRUD path that backs the
/// /Neighbours REST controller. Idempotency on upsert and graceful
/// behaviour on remove-when-absent are the behaviours sysop scripts
/// will lean on.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class NeighbourCrudTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-nbr-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbNeighbour>();
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
    public async Task UpsertNeighbour_FirstWrite_Inserts()
    {
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1);

        var rows = await database.GetNeighbours();
        rows.Should().ContainSingle();
        rows.Single().Callsign.Should().Be("N0BBB-9");
        rows.Single().BearerPort.Should().Be(1);
    }

    [Fact]
    public async Task UpsertNeighbour_SameCallsignTwice_UpdatesInPlace()
    {
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1);
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 2);

        var rows = await database.GetNeighbours();
        rows.Should().ContainSingle("upsert MUST NOT create a duplicate row for the same callsign");
        rows.Single().BearerPort.Should().Be(2);
    }

    [Fact]
    public async Task UpsertNeighbour_NullPort_RoundTripsAsNull()
    {
        await database.UpsertNeighbour("N0BBB-9", bearerPort: null);

        var rows = await database.GetNeighbours();
        rows.Single().BearerPort.Should().BeNull();
    }

    [Fact]
    public async Task UpsertNeighbour_CompressionEnabled_RoundTripsNullFalseTrue()
    {
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1);
        (await database.GetNeighbours()).Single().CompressionEnabled.Should().BeNull();

        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1, compressionEnabled: false);
        (await database.GetNeighbours()).Single().CompressionEnabled.Should().BeFalse();

        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1, compressionEnabled: true);
        (await database.GetNeighbours()).Single().CompressionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveNeighbour_Existing_ReturnsTrueAndDeletes()
    {
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 1);

        var deleted = await database.RemoveNeighbour("N0BBB-9");

        deleted.Should().BeTrue();
        (await database.GetNeighbours()).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveNeighbour_Absent_ReturnsFalse()
    {
        var deleted = await database.RemoveNeighbour("N0WHO");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task GetNeighbours_Empty_ReturnsEmptyCollection()
    {
        (await database.GetNeighbours()).Should().BeEmpty();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
