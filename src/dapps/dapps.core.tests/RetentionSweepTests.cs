using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Retention (#286): forwarded and delivered messages go 30 days after
/// they finished, drop records 90 days after the drop, a batch at a time,
/// and nothing still waiting for something is ever touched. Also pins the
/// indexes the hot queries rely on, on a fresh and an upgraded database.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class RetentionSweepTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private string dbPath = null!;
    private FakeTimeProvider clock = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-retention-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        clock = new FakeTimeProvider(new DateTimeOffset(T0));
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            TransmissionAuditRetentionDays = 0,
        });
        database = new Database(NullLogger<Database>.Instance, options, clock);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private static void CreateTables()
    {
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbMessage>();
        c.CreateTable<DbDroppedMessage>();
        c.CreateTable<DbReceived>();
        c.CreateTable<DbFragment>();
        c.CreateTable<DbTransmission>();
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static DbMessage Row(string id, DateTime createdAt, bool forwarded = false, bool delivered = false,
        DateTime? completedAt = null, bool pendingInOrder = false, string destination = "app@N0DEST") => new()
    {
        Id = id,
        Destination = destination,
        Payload = new byte[64],
        CreatedAt = createdAt,
        Forwarded = forwarded,
        LocallyDelivered = delivered,
        CompletedAt = completedAt,
        PendingInOrder = pendingInOrder,
    };

    private static bool Exists(string id)
    {
        using var c = DbInfo.GetConnection();
        return c.Find<DbMessage>(id) is not null;
    }

    [Fact]
    public async Task FinishedMessages_OlderThan30Days_Deleted_YoungerKept()
    {
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(Row("fwdold1", T0.AddDays(-60), forwarded: true, completedAt: T0.AddDays(-31)));
            c.Insert(Row("ackold1", T0.AddDays(-60), delivered: true, completedAt: T0.AddDays(-31)));
            c.Insert(Row("fwdnew1", T0.AddDays(-60), forwarded: true, completedAt: T0.AddDays(-29)));
            c.Insert(Row("acknew1", T0.AddDays(-1), delivered: true, completedAt: T0.AddDays(-1)));
        }

        var deleted = await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000);

        deleted.Should().Be(2);
        Exists("fwdold1").Should().BeFalse();
        Exists("ackold1").Should().BeFalse();
        Exists("fwdnew1").Should().BeTrue("created 60 days ago but only forwarded 29 days ago");
        Exists("acknew1").Should().BeTrue();
    }

    [Fact]
    public async Task UnfinishedMessages_OfAnyAge_Kept()
    {
        CreateTables();
        var ancient = T0.AddYears(-3);
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(Row("queued1", ancient, destination: "app@N0FAR"));
            c.Insert(Row("unackd1", ancient, destination: "app@N0CALL"));
            c.Insert(Row("parked1", ancient, pendingInOrder: true, destination: "app@N0CALL"));
            // A parked row must survive even if something marked it done.
            c.Insert(Row("parked2", ancient, delivered: true, completedAt: ancient, pendingInOrder: true));
            c.Insert(new DbFragment
            {
                Key = DbFragment.MakeKey("mid0001", 1), MasterId = "mid0001", FragmentIndex = 1, FragmentTotal = 2,
                Payload = new byte[8], Destination = "app@N0CALL", FirstSeenAt = T0.AddDays(-1),
            });
        }

        var deleted = await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000);

        deleted.Should().Be(0);
        Exists("queued1").Should().BeTrue();
        Exists("unackd1").Should().BeTrue();
        Exists("parked1").Should().BeTrue();
        Exists("parked2").Should().BeTrue();
        using var c2 = DbInfo.GetConnection();
        c2.Table<DbFragment>().Count().Should().Be(1, "a fragment awaiting reassembly is not the retention sweep's to remove");
    }

    [Fact]
    public async Task MarkForwardedAndDelivered_StampCompletedAt_AndStartTheClock()
    {
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(Row("lateout", T0.AddDays(-100), destination: "app@N0FAR"));
            c.Insert(Row("lateack", T0.AddDays(-100), destination: "app@N0CALL"));
        }

        await database.MarkMessageAsForwarded("lateout");
        await database.MarkLocallyDelivered("lateack");
        clock.Advance(TimeSpan.FromDays(1));
        await database.MarkLocallyDelivered("lateack"); // a repeat ack keeps the first time

        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbMessage>("lateout")!.CompletedAt.Should().Be(T0);
            c.Find<DbMessage>("lateack")!.CompletedAt.Should().Be(T0);
        }

        // Queued 100 days, finished a day ago: kept.
        (await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000)).Should().Be(0);

        clock.Advance(TimeSpan.FromDays(30));
        (await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000)).Should().Be(2);
    }

    [Fact]
    public async Task FinishedRowsWithoutCompletedAt_FallBackToCreatedAt()
    {
        // Finished by a version that had no CompletedAt column.
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(Row("legacy1", T0.AddDays(-40), forwarded: true));
            c.Insert(Row("legacy2", T0.AddDays(-10), delivered: true));
        }

        var deleted = await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000);

        deleted.Should().Be(1);
        Exists("legacy1").Should().BeFalse();
        Exists("legacy2").Should().BeTrue("created 10 days ago");

        clock.Advance(TimeSpan.FromDays(21));
        (await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000)).Should().Be(1);
        Exists("legacy2").Should().BeFalse();
    }

    [Fact]
    public async Task LegacyRows_AreDeletedInBatchesToo_AndNeverRewritten()
    {
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            for (var i = 0; i < 5; i++) c.Insert(Row($"leg{i:D4}", T0.AddDays(-40), forwarded: true));
            c.Insert(Row("legyoung", T0.AddDays(-5), delivered: true));
        }

        var cutoff = Now - TtlSweeperService.FinishedMessageRetention;
        (await database.SweepFinishedMessagesAsync(cutoff, 2)).Should().Be(2);
        (await database.SweepFinishedMessagesAsync(cutoff, 2)).Should().Be(2);
        (await database.SweepFinishedMessagesAsync(cutoff, 2)).Should().Be(1);
        (await database.SweepFinishedMessagesAsync(cutoff, 2)).Should().Be(0);

        using var c2 = DbInfo.GetConnection();
        c2.Find<DbMessage>("legyoung")!.CompletedAt.Should().BeNull("the sweep only deletes, it never rewrites a row");
    }

    [Fact]
    public async Task DropRecords_OlderThan90Days_Deleted_YoungerKept()
    {
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbDroppedMessage { Id = "dropold", CreatedAt = T0.AddDays(-200), DroppedAt = T0.AddDays(-91), Reason = "ttl-expired" });
            c.Insert(new DbDroppedMessage { Id = "dropnew", CreatedAt = T0.AddDays(-200), DroppedAt = T0.AddDays(-89), Reason = "ttl-expired" });
        }

        var deleted = await database.SweepDroppedMessagesAsync(Now - TtlSweeperService.DroppedMessageRetention, 1000);

        deleted.Should().Be(1);
        using var c2 = DbInfo.GetConnection();
        c2.Find<DbDroppedMessage>("dropold").Should().BeNull();
        c2.Find<DbDroppedMessage>("dropnew").Should().NotBeNull();
    }

    [Fact]
    public async Task Sweeper_DeletesAtMostOneBatchPerTick()
    {
        CreateTables();
        using (var c = DbInfo.GetConnection())
        {
            for (var i = 0; i < 5; i++)
            {
                c.Insert(Row($"fin{i:D4}", T0.AddDays(-60), forwarded: true, completedAt: T0.AddDays(-40)));
                c.Insert(new DbDroppedMessage { Id = $"drp{i:D4}", DroppedAt = T0.AddDays(-100) });
            }
        }

        var sweeper = new TtlSweeperService(
            database, clock, options,
            new TransmissionAuditService(NullLogger<TransmissionAuditService>.Instance, options, null!, clock),
            NullLogger<TtlSweeperService>.Instance)
        {
            RetentionBatchSize = 2,
        };

        int Messages() { using var c = DbInfo.GetConnection(); return c.Table<DbMessage>().Count(); }
        int Drops() { using var c = DbInfo.GetConnection(); return c.Table<DbDroppedMessage>().Count(); }

        async Task WaitFor(int messages, int drops)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Messages() != messages || Drops() != drops) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Messages().Should().Be(messages);
            Drops().Should().Be(drops);
        }

        await sweeper.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // The sweep at start-up takes one batch of each.
            // Without the bound the first sweep would take all five and
            // the counts would never sit at 3; the fake clock means no
            // further tick happens until Advance.
            await WaitFor(3, 3);

            clock.Advance(sweeper.SweepInterval);
            await WaitFor(1, 1);
            clock.Advance(sweeper.SweepInterval);
            await WaitFor(0, 0);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    private static readonly string[] ExpectedIndexes =
    [
        "messages_state",
        "messages_CreatedAt",
        "messages_CompletedAt",
        "messages_stream",
        "dropped_messages_DroppedAt",
        "dropped_messages_stream",
    ];

    private static List<string> IndexNames()
    {
        using var c = DbInfo.GetConnection();
        return c.QueryScalars<string>("select name from sqlite_master where type = 'index' and name not like 'sqlite_%'");
    }

    [Fact]
    public void Indexes_ExistAfterStartup_OnAFreshDatabase()
    {
        DbStartup.EnsureSchemaAndSeed();

        IndexNames().Should().Contain(ExpectedIndexes);
    }

    [Fact]
    public async Task Indexes_ExistAfterStartup_OnAnUpgradedDatabase_AndAnOlderVersionStillWorks()
    {
        // A database as the previous release left it: messages without
        // CompletedAt, no indexes on either table.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<LegacyDbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.Execute("drop index dropped_messages_DroppedAt");
            c.Execute("drop index dropped_messages_stream");
            c.Insert(new LegacyDbMessage { Id = "oldfwd1", Destination = "app@N0FAR", Payload = new byte[64], Forwarded = true, CreatedAt = T0.AddDays(-45) });
            c.Insert(new LegacyDbMessage { Id = "oldque1", Destination = "app@N0FAR", Payload = new byte[64], CreatedAt = T0.AddDays(-45) });
        }
        IndexNames().Should().NotContain(ExpectedIndexes);

        DbStartup.EnsureSchemaAndSeed();

        IndexNames().Should().Contain(ExpectedIndexes);
        using (var c = DbInfo.GetConnection())
        {
            var columns = c.GetTableInfo("messages").Select(col => col.Name).ToList();
            columns.Should().StartWith(new[] { "Id", "Payload" }, "existing columns keep their order");
            columns.Last().Should().Be("CompletedAt", "the new column goes on the end");
        }

        // The rows the older version finished are swept by CreatedAt.
        (await database.SweepFinishedMessagesAsync(Now - TtlSweeperService.FinishedMessageRetention, 1000)).Should().Be(1);
        Exists("oldfwd1").Should().BeFalse();
        Exists("oldque1").Should().BeTrue();

        // Downgrade: the older version's start-up and its reads and writes
        // still work on the upgraded table.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<LegacyDbMessage>();
            c.Insert(new LegacyDbMessage { Id = "olddown", Destination = "app@N0FAR", Payload = new byte[4], CreatedAt = T0 });
            c.Execute("update messages set forwarded=1 where id=?", "olddown");
            c.Find<LegacyDbMessage>("olddown")!.Forwarded.Should().BeTrue();
            c.Table<LegacyDbMessage>().Count().Should().Be(2);
        }
    }

    [Fact]
    public async Task Fragments_GainStreamColumnsOnUpgrade_AndAnOlderVersionStillWorks()
    {
        // #384: fragments keep their stream fields now. A database from an
        // older version gets the columns on the end, its waiting fragments
        // survive, and the older version can still use the table.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<LegacyDbFragment>();
            c.Insert(new LegacyDbFragment { Key = "m1:1", MasterId = "m1", FragmentIndex = 1, FragmentTotal = 2, Payload = new byte[4], FirstSeenAt = T0 });
        }

        DbStartup.EnsureSchemaAndSeed();

        using (var c = DbInfo.GetConnection())
        {
            var columns = c.GetTableInfo("fragments").Select(col => col.Name).ToList();
            columns.Should().StartWith(new[] { "Key", "MasterId" }, "existing columns keep their order");
            columns.TakeLast(3).Should().Equal("StreamId", "StreamSeq", "StreamGapTimeoutSeconds");
        }
        await database.UpsertFragment(new DbFragment { MasterId = "m1", FragmentIndex = 2, FragmentTotal = 2, Payload = new byte[4], FirstSeenAt = T0, StreamId = "s", StreamSeq = 3, StreamGapTimeoutSeconds = 0 });
        var both = await database.GetFragmentsForMaster("m1");
        both.Should().HaveCount(2);
        both[0].StreamSeq.Should().BeNull();
        both[1].StreamSeq.Should().Be(3u);

        // Downgrade: the older version's start-up, reads and writes.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<LegacyDbFragment>();
            c.Insert(new LegacyDbFragment { Key = "m2:1", MasterId = "m2", FragmentIndex = 1, FragmentTotal = 1, Payload = new byte[4], FirstSeenAt = T0 });
            c.Find<LegacyDbFragment>("m1:2")!.FragmentTotal.Should().Be(2);
            c.Table<LegacyDbFragment>().Count().Should().Be(3);
            c.Execute("delete from fragments where MasterId=?", "m1");
            c.Table<LegacyDbFragment>().Count().Should().Be(1);
        }
    }

    /// <summary>The fragments table as releases before #384 define it.</summary>
    [Table("fragments")]
    private sealed class LegacyDbFragment
    {
        [PrimaryKey]
        public string Key { get; set; } = "";
        [Indexed]
        public string MasterId { get; set; } = "";
        public int FragmentIndex { get; set; }
        public int FragmentTotal { get; set; }
        public byte[] Payload { get; set; } = [];
        public string Destination { get; set; } = "";
        public string SourceCallsign { get; set; } = "";
        public string OriginatorCallsign { get; set; } = "";
        public string AdditionalProperties { get; set; } = "{}";
        public int? Ttl { get; set; }
        public DateTime FirstSeenAt { get; set; }
    }

    [Fact]
    public void HotQueries_UseTheIndexes()
    {
        CreateTables();
        using var c = DbInfo.GetConnection();
        string Plan(string sql, params object[] args) =>
            string.Join(" | ", c.Query<QueryPlanRow>("explain query plan " + sql, args).Select(r => r.detail));

        Plan("select * from messages where forwarded=0 and locallydelivered=0 and not (destination like ? or destination like ?) order by CreatedAt asc", "a%", "b%")
            .Should().Contain("SEARCH messages USING INDEX messages_state");
        Plan("select * from messages where locallydelivered=0 and (destination like ? or destination like ?)", "a%", "b%")
            .Should().Contain("USING INDEX messages_state");
        Plan("select * from messages order by CreatedAt desc limit ?", 50)
            .Should().Contain("messages_CreatedAt");
        Plan("select * from messages where PendingInOrder=1 and OriginatorCallsign=? and StreamId=? order by StreamSeq asc", "a", "b")
            .Should().Contain("messages_stream");
        var retention = Plan("select rowid from messages where PendingInOrder=0 and (" +
            "(CompletedAt < ? and (Forwarded=1 or LocallyDelivered=1)) or " +
            "(LocallyDelivered=1 and Forwarded=0 and CompletedAt is null and CreatedAt < ?) or " +
            "(LocallyDelivered=1 and Forwarded=1 and CompletedAt is null and CreatedAt < ?) or " +
            "(LocallyDelivered=0 and Forwarded=1 and CompletedAt is null and CreatedAt < ?)) limit ?", 1L, 1L, 1L, 1L, 10);
        retention.Should().Contain("MULTI-INDEX OR")
            .And.Contain("messages_CompletedAt (CompletedAt<?)")
            .And.Contain("messages_state (LocallyDelivered=? AND Forwarded=? AND CompletedAt=? AND CreatedAt<?)")
            .And.NotContain("SCAN");
        Plan("select rowid from dropped_messages where DroppedAt < ? limit ?", 1L, 10)
            .Should().Contain("dropped_messages_DroppedAt");
        Plan("select * from dropped_messages order by DroppedAt desc limit ?", 50)
            .Should().Contain("dropped_messages_DroppedAt");
    }

    private sealed class QueryPlanRow
    {
        public int id { get; set; }
        public int parent { get; set; }
        public int notused { get; set; }
        public string detail { get; set; } = "";
    }

    /// <summary>The messages table as the release before #286 declared it.</summary>
    [Table("messages")]
    private sealed class LegacyDbMessage
    {
        [PrimaryKey, NotNull]
        public string Id { get; set; } = "";
        public byte[] Payload { get; set; } = [];
        public long? Salt { get; set; }
        public string Destination { get; set; } = "";
        public string SourceCallsign { get; set; } = "";
        public string OriginatorCallsign { get; set; } = "";
        public string AdditionalProperties { get; set; } = "";
        public bool Forwarded { get; set; }
        public bool LocallyDelivered { get; set; }
        public int? Ttl { get; set; }
        public DateTime CreatedAt { get; set; }
        public byte? FloodHopsRemaining { get; set; }
        public string? SourceRouteCsv { get; set; }
        public string? TraversedHopsCsv { get; set; }
        public string? MasterId { get; set; }
        public int? FragmentIndex { get; set; }
        public int? FragmentTotal { get; set; }
        public string? StreamId { get; set; }
        public uint? StreamSeq { get; set; }
        public uint? StreamGapTimeoutSeconds { get; set; }
        public bool PendingInOrder { get; set; }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
