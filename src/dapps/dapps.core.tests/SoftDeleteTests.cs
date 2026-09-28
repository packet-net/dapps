using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

[Collection(SqliteOverridePathCollection.Name)]
public sealed class SoftDeleteTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-softdel-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
        }

        var opts = new OptMon(new SystemOptions { Callsign = "N0CALL" });
        database = new Database(NullLogger<Database>.Instance, opts);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SoftDelete_MovesRowAcrossWithReason()
    {
        await database.SaveMessage("abc1234", "hello"u8.ToArray(), salt: 1L,
            destination: "app@N0DEST", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 60);

        await database.SoftDeleteMessage("abc1234", "ttl-expired");

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("abc1234").Should().BeNull("hard-deleted from messages");
        var dropped = c.Find<DbDroppedMessage>("abc1234");
        dropped.Should().NotBeNull();
        dropped!.Reason.Should().Be("ttl-expired");
        dropped.SourceCallsign.Should().Be("G7XYZ");
        dropped.Payload.Should().BeEquivalentTo("hello"u8.ToArray());
        dropped.DroppedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SoftDelete_OnMissingRow_IsANoOp()
    {
        // Exists for the concurrent-drop case: forwarder picks up a
        // row, sweeper picks up the same row, both call SoftDelete.
        // Whichever runs second should silently no-op rather than
        // throwing.
        var act = async () => await database.SoftDeleteMessage("nonex01", "ttl-expired");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetRecentDroppedMessages_NewestFirst_LimitRespected()
    {
        for (var i = 0; i < 10; i++)
        {
            var id = $"msg{i:00}00";
            await database.SaveMessage(id, "x"u8.ToArray(), null,
                $"app@N{i}DEST", "G7XYZ", "{}", ttl: 60);
            await database.SoftDeleteMessage(id, "ttl-expired");
            await Task.Delay(15, TestContext.Current.CancellationToken); // ensure DroppedAt timestamps order
        }

        var page = await database.GetRecentDroppedMessages(limit: 3);
        page.Should().HaveCount(3);
        page[0].Id.Should().Be("msg0900");
        page[1].Id.Should().Be("msg0800");
        page[2].Id.Should().Be("msg0700");
    }

    [Fact]
    public async Task DeleteExpired_NowSoftDeletesMessages()
    {
        // CreatedAt 5 minutes ago, ttl 60s → expired.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "expired",
                Payload = "stale"u8.ToArray(),
                Destination = "app@N0DEST",
                SourceCallsign = "G7XYZ",
                AdditionalProperties = "{}",
                Ttl = 60,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        }

        var actioned = await database.DeleteExpired(DateTime.UtcNow);
        actioned.Should().Be(1);

        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbMessage>("expired").Should().BeNull();
            var d = c.Find<DbDroppedMessage>("expired");
            d.Should().NotBeNull();
            d!.Reason.Should().Be("ttl-expired");
        }
    }

    private sealed class OptMon(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }
}
