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
    public async Task SoftDelete_KeepsTheOriginator()
    {
        await database.SaveMessage("abc1235", "relayed"u8.ToArray(), salt: 1L,
            destination: "app@N0DEST", sourceCallsign: "G6CCC-7",
            additionalProperties: "{}", ttl: 60, originatorCallsign: "G6AAA-7");

        await database.SoftDeleteMessage("abc1235", "ttl-expired");

        using var c = DbInfo.GetConnection();
        var dropped = c.Find<DbDroppedMessage>("abc1235")!;
        dropped.SourceCallsign.Should().Be("G6CCC-7");
        dropped.OriginatorCallsign.Should().Be("G6AAA-7");
        dropped.FromCallsign.Should().Be("G6AAA-7");
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

    [Fact]
    public async Task SoftDelete_AnIdAlreadyDropped_ReplacesTheOldRecord()
    {
        // #284: ids are 28 bits and dropped_messages is never purged, so
        // an unrelated later message can share an id with an old drop.
        await database.SaveMessage("dup0001", "first"u8.ToArray(), salt: 1L,
            destination: "app@N0DEST", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 60);
        await database.SoftDeleteMessage("dup0001", "ttl-expired");

        await database.SaveMessage("dup0001", "second"u8.ToArray(), salt: 2L,
            destination: "app@N0OTHER", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 60);
        var act = async () => await database.SoftDeleteMessage("dup0001", "stream-stale");
        await act.Should().NotThrowAsync();

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("dup0001").Should().BeNull("the row must not be left behind in messages");
        var dropped = c.Find<DbDroppedMessage>("dup0001")!;
        dropped.Reason.Should().Be("stream-stale", "the newer drop wins");
        dropped.Payload.Should().BeEquivalentTo("second"u8.ToArray());
        c.Table<DbDroppedMessage>().Count().Should().Be(1);
    }

    [Fact]
    public async Task SoftDelete_WhenTheCopyFails_LeavesTheMessageQueued()
    {
        // The copy and the delete are one transaction: a failed copy
        // must not lose the message.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("create trigger poison before insert on dropped_messages begin select raise(abort, 'poisoned'); end");
        }
        await database.SaveMessage("atomic1", "x"u8.ToArray(), salt: 1L,
            destination: "app@N0DEST", sourceCallsign: "G7XYZ",
            additionalProperties: "{}", ttl: 60);

        var act = async () => await database.SoftDeleteMessage("atomic1", "ttl-expired");
        await act.Should().ThrowAsync<SQLiteException>();

        using var check = DbInfo.GetConnection();
        check.Find<DbMessage>("atomic1").Should().NotBeNull();
        check.Find<DbDroppedMessage>("atomic1").Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpired_CarriesOnPastARowThatFails()
    {
        // #284: one failing row used to abort every sweep from then on.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("create trigger poison before insert on dropped_messages when new.Id = 'poison1' begin select raise(abort, 'poisoned'); end");
            foreach (var id in new[] { "aaaa001", "poison1", "zzzz001" })
            {
                c.Insert(new DbMessage
                {
                    Id = id,
                    Payload = "stale"u8.ToArray(),
                    Destination = "app@N0DEST",
                    SourceCallsign = "G7XYZ",
                    AdditionalProperties = "{}",
                    Ttl = 60,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                });
            }
        }

        var actioned = await database.DeleteExpired(DateTime.UtcNow);

        actioned.Should().Be(2);
        using var check = DbInfo.GetConnection();
        check.Find<DbMessage>("aaaa001").Should().BeNull();
        check.Find<DbMessage>("zzzz001").Should().BeNull();
        check.Find<DbMessage>("poison1").Should().NotBeNull("its drop failed and was rolled back");
        check.Find<DbDroppedMessage>("aaaa001").Should().NotBeNull();
        check.Find<DbDroppedMessage>("zzzz001").Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteExpired_ForwardedOrDeliveredRows_AreNotReportedAsDropped()
    {
        // #285: a row that already went on, or that its app has acked,
        // did not expire. It goes, but not into dropped_messages.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "fwd0001", Payload = "x"u8.ToArray(), Destination = "app@N0DEST",
                SourceCallsign = "G7XYZ", AdditionalProperties = "{}", Ttl = 60,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5), Forwarded = true,
            });
            c.Insert(new DbMessage
            {
                Id = "dlv0001", Payload = "x"u8.ToArray(), Destination = "app@N0CALL",
                SourceCallsign = "G7XYZ", AdditionalProperties = "{}", Ttl = 60,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5), LocallyDelivered = true,
            });
            c.Insert(new DbMessage
            {
                Id = "que0001", Payload = "x"u8.ToArray(), Destination = "app@N0DEST",
                SourceCallsign = "G7XYZ", AdditionalProperties = "{}", Ttl = 60,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        }

        var actioned = await database.DeleteExpired(DateTime.UtcNow);

        actioned.Should().Be(3);
        using var check = DbInfo.GetConnection();
        check.Table<DbMessage>().Count().Should().Be(0);
        check.Find<DbDroppedMessage>("fwd0001").Should().BeNull();
        check.Find<DbDroppedMessage>("dlv0001").Should().BeNull();
        check.Find<DbDroppedMessage>("que0001")!.Reason.Should().Be("ttl-expired",
            "a row that expired while still queued is still reported");
        (await database.GetRecentDroppedMessages()).Select(d => d.Id).Should().Equal("que0001");
    }

    private sealed class OptMon(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }
}
