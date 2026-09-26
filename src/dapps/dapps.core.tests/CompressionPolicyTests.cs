using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace dapps.core.tests;

[Collection(SqliteOverridePathCollection.Name)]
public sealed class CompressionPolicyTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private SystemOptions systemOptions = null!;
    private CompressionPolicy policy = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-compression-policy-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection()) c.CreateTable<DbNeighbour>();
        systemOptions = new SystemOptions { Callsign = "N0CALL" };
        var monitor = new TestOptionsMonitor<SystemOptions>(systemOptions);
        database = new Database(NullLogger<Database>.Instance, monitor);
        policy = new CompressionPolicy(database, monitor);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task TheNeighboursOwnSetting_WinsOverTheSystemOne(bool systemSetting, bool? neighbourSetting, bool expected)
    {
        systemOptions.CompressionEnabled = systemSetting;
        await database.UpsertNeighbour("G5ALF-3", bearerPort: 0, compressionEnabled: neighbourSetting);

        (await policy.ShouldCompressToAsync("g5alf-3", TestContext.Current.CancellationToken)).Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APeerWithNoNeighbourRow_GetsTheSystemSetting(bool systemSetting)
    {
        systemOptions.CompressionEnabled = systemSetting;

        (await policy.ShouldCompressToAsync("M0AHN-3", TestContext.Current.CancellationToken)).Should().Be(systemSetting);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
