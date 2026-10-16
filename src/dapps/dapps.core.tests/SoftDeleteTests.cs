using System.Collections.Concurrent;
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
        // #284: ids are 28 bits and dropped_messages keeps records for 90 days, so
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
    public async Task SoftDelete_TheSameIdManyTimes_NeverThrows()
    {
        for (var i = 0; i < 5; i++)
        {
            await database.SaveMessage("rep0001", new[] { (byte)i }, salt: i,
                destination: "app@N0DEST", sourceCallsign: "G7XYZ",
                additionalProperties: "{}", ttl: 60);
            var act = async () => await database.SoftDeleteMessage("rep0001", $"reason-{i}");
            await act.Should().NotThrowAsync();
        }

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("rep0001").Should().BeNull();
        c.Find<DbDroppedMessage>("rep0001")!.Reason.Should().Be("reason-4");
    }

    /// <summary>A crash between the copy and the delete leaves the
    /// message in both tables.</summary>
    private static void InsertInBothTables(string id, bool expired)
    {
        using var c = DbInfo.GetConnection();
        var createdAt = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow;
        c.Insert(new DbMessage
        {
            Id = id, Payload = "x"u8.ToArray(), Destination = "app@N0DEST",
            SourceCallsign = "G7XYZ", AdditionalProperties = "{}", Ttl = 60, CreatedAt = createdAt,
        });
        c.Insert(new DbDroppedMessage
        {
            Id = id, Payload = "x"u8.ToArray(), Destination = "app@N0DEST",
            SourceCallsign = "G7XYZ", AdditionalProperties = "{}", Ttl = 60, CreatedAt = createdAt,
            DroppedAt = DateTime.UtcNow.AddMinutes(-1), Reason = "refused: crashed half way",
        });
    }

    [Fact]
    public async Task SoftDelete_ARowLeftInBothTables_IsFinishedByTheNextDrop()
    {
        InsertInBothTables("both001", expired: false);

        var act = async () => await database.SoftDeleteMessage("both001", "refused: again");
        await act.Should().NotThrowAsync();

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("both001").Should().BeNull();
        c.Find<DbDroppedMessage>("both001")!.Reason.Should().Be("refused: again");
    }

    [Fact]
    public async Task DeleteExpired_ARowLeftInBothTables_IsFinished()
    {
        InsertInBothTables("both002", expired: true);

        var actioned = await database.DeleteExpired(DateTime.UtcNow);

        actioned.Should().Be(1);
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("both002").Should().BeNull();
        c.Find<DbDroppedMessage>("both002")!.Reason.Should().Be("ttl-expired");
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

    [Fact]
    public async Task SoftDelete_AlongsideOtherWrites_NeverHitsALockedDatabase()
    {
        // The pooled connection serialises every statement in the
        // process, so nothing here can ever see "database is locked".
        // A drop on a connection of its own would break that.
        const int n = 200;
        for (var i = 0; i < n; i++)
            await database.SaveMessage($"m{i:D6}", new byte[200], salt: i, destination: "app@N0DEST",
                sourceCallsign: "G7XYZ", additionalProperties: "{}", ttl: 600);

        var errors = new ConcurrentBag<Exception>();
        var stop = DateTime.UtcNow.AddSeconds(3);
        async Task Loop(Func<int, Task> body)
        {
            var r = new Random();
            while (DateTime.UtcNow < stop)
            {
                try { await body(r.Next(n)); }
                catch (Exception e) { errors.Add(e); }
            }
        }
        var tasks = new List<Task>();
        for (var k = 0; k < 3; k++) tasks.Add(Task.Run(() => Loop(i => database.SoftDeleteMessage($"m{i:D6}", "refused: x"))));
        tasks.Add(Task.Run(() => Loop(i => database.SaveMessage($"m{i:D6}", new byte[200], salt: i, destination: "app@N0DEST",
            sourceCallsign: "G7XYZ", additionalProperties: "{}", ttl: 600))));
        for (var k = 0; k < 2; k++) tasks.Add(Task.Run(() => Loop(i => database.MarkMessageAsForwarded($"m{i:D6}"))));
        await Task.WhenAll(tasks);

        errors.Select(e => e.Message).Distinct().Should().BeEmpty();
    }

    private sealed class OptMon(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }
}
