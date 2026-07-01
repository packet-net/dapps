using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using Xunit;

namespace dapps.core.tests;

/// <summary>
/// Guards the MeshCore config round-trip through <see cref="SystemOptionsStore"/>
/// (#154 review): every DAPPS_MESHCORE_* option must survive Save -> reload, so
/// the bearer can actually be enabled and configured via the persisted table
/// (regression guard for the earlier gap where Parse/SaveAsync ignored them).
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MeshCoreConfigTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mc-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = new SQLiteConnection(dbPath);
        c.CreateTable<DbSystemOption>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MeshCoreOptions_RoundTripThroughStore()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var opts = store.CurrentValue;
        opts.MeshCoreEnabled = true;
        opts.MeshCorePort = "/dev/ttyUSB9";
        opts.MeshCoreRegion = "custom";
        opts.MeshCoreCustomPreset = "freq=867.1;bw=125;sf=9;cr=6;pwr=20";
        opts.MeshCoreFloodScopeKey = "dapps-uk";
        opts.MeshCoreChannelIndex = 3;
        opts.MeshCoreChannelName = "testch";
        opts.MeshCoreChannelPsk = "3135135fd198029d689b64f45df2aae9";
        opts.MeshCoreNodeName = "GB7TST-1";
        opts.MeshCoreTxPowerDbm = 14;
        opts.MeshCoreAirtimeBudgetSecondsPerHour = 45;
        opts.MeshCoreCompress = false;
        opts.MeshCoreCongestionBackoffFraction = 0.25;
        opts.MeshCoreLbtGuardMs = 250;
        opts.MeshCoreReliableDelivery = false;

        await store.SaveAsync(opts);

        // A fresh store reads the persisted rows via Parse.
        var reloaded = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance).CurrentValue;
        reloaded.MeshCoreEnabled.Should().BeTrue();
        reloaded.MeshCorePort.Should().Be("/dev/ttyUSB9");
        reloaded.MeshCoreRegion.Should().Be("custom");
        reloaded.MeshCoreCustomPreset.Should().Be("freq=867.1;bw=125;sf=9;cr=6;pwr=20");
        reloaded.MeshCoreFloodScopeKey.Should().Be("dapps-uk");
        reloaded.MeshCoreChannelIndex.Should().Be(3);
        reloaded.MeshCoreChannelName.Should().Be("testch");
        reloaded.MeshCoreChannelPsk.Should().Be("3135135fd198029d689b64f45df2aae9");
        reloaded.MeshCoreNodeName.Should().Be("GB7TST-1");
        reloaded.MeshCoreTxPowerDbm.Should().Be(14);
        reloaded.MeshCoreAirtimeBudgetSecondsPerHour.Should().Be(45);
        reloaded.MeshCoreCompress.Should().BeFalse();
        reloaded.MeshCoreCongestionBackoffFraction.Should().Be(0.25);
        reloaded.MeshCoreLbtGuardMs.Should().Be(250);
        reloaded.MeshCoreReliableDelivery.Should().BeFalse();
    }
}
