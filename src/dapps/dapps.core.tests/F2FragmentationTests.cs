using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan F2 - end-to-end multi-part: sender chunks above threshold,
/// receiver reassembles. Tests run against the real Database +
/// DatabaseAndMqttInbox machinery (with a stub MQTT broker) because
/// the reassembly path's correctness is mostly about data shape
/// across SaveMessage / fragments / reassembled DbMessage rows.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class F2FragmentationTests : IAsyncLifetime
{
    private string dbPath = null!;
    private FakeTimeProvider clock = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-f2-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbFragment>();
            c.CreateTable<DbSystemOption>();
        }
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero));
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
            FragmentThresholdBytes = 100,                // tiny - easy to test boundary
            FragmentReassemblyTimeoutSeconds = 7 * 24 * 3600,
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

    [Fact]
    public async Task SubmitOutboundMessage_BelowThreshold_NoFragmentation()
    {
        await database.SubmitOutboundMessage("hello", "N0DEST", "small payload"u8.ToArray(), ttlSeconds: 60);

        var rows = await database.GetRecentMessages(10);
        rows.Should().ContainSingle();
        rows[0].MasterId.Should().BeNull();
        rows[0].FragmentIndex.Should().BeNull();
        rows[0].FragmentTotal.Should().BeNull();
    }

    [Fact]
    public async Task SubmitOutboundMessage_AboveThreshold_ChunksIntoNRows()
    {
        // Threshold 100; submit 350 bytes → 4 fragments (100+100+100+50).
        var payload = new byte[350];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);

        var returnedId = await database.SubmitOutboundMessage(
            "bigapp", "N0DEST", payload, ttlSeconds: 60);

        var rows = (await database.GetRecentMessages(20)).OrderBy(m => m.FragmentIndex).ToList();
        rows.Should().HaveCount(4);
        rows.Should().AllSatisfy(r =>
        {
            r.MasterId.Should().NotBeNullOrEmpty();
            r.FragmentTotal.Should().Be(4);
            r.MasterId.Should().Be(returnedId, "the returned id IS the master id for fragmented submits");
        });
        rows.Select(r => r.FragmentIndex!.Value).Should().Equal(1, 2, 3, 4);
        // Concatenating the chunks back should yield the original payload.
        var concatenated = rows.SelectMany(r => r.Payload).ToArray();
        concatenated.Should().Equal(payload);
    }

    [Fact]
    public async Task SubmitOutboundMessage_ExactlyOnThreshold_NoFragmentation()
    {
        // Threshold is "strictly greater than" - payload of exactly the
        // threshold size should NOT split. (`payload.Length > threshold`)
        var payload = new byte[100];
        await database.SubmitOutboundMessage("app", "N0DEST", payload);

        var rows = await database.GetRecentMessages(10);
        rows.Should().ContainSingle();
        rows[0].MasterId.Should().BeNull();
    }

    [Fact]
    public async Task SubmitOutboundMessage_ThresholdZero_NeverFragments()
    {
        // Threshold 0 disables fragmentation outright.
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF", FragmentThresholdBytes = 0,
        });
        var noFragDb = new Database(NullLogger<Database>.Instance, options, clock);
        var payload = new byte[10_000];

        await noFragDb.SubmitOutboundMessage("app", "N0DEST", payload);

        var rows = await noFragDb.GetRecentMessages(10);
        rows.Should().ContainSingle();
        rows[0].Payload.Length.Should().Be(10_000);
    }

    [Fact]
    public async Task Inbox_FragmentsArrivingInOrder_ReassembleAndDeliver()
    {
        var inbox = MakeInbox();
        var (fragments, expectedAssembled, masterId) = MakeFragments(totalLen: 350, fragSize: 100);

        foreach (var f in fragments)
        {
            await inbox.DeliverAsync(f, sourceCallsign: "N0PEER", CancellationToken.None);
        }

        // Fragment rows should be cleaned up after the last fragment lands.
        (await database.GetAllFragments()).Should().BeEmpty();
        // Assembled message lives in DbMessage with id = masterId.
        var stored = await database.GetRecentMessages(10);
        stored.Should().ContainSingle();
        stored[0].Id.Should().Be(masterId);
        stored[0].Payload.Should().Equal(expectedAssembled);
    }

    [Fact]
    public async Task Inbox_FragmentsArrivingOutOfOrder_StillReassembleCorrectly()
    {
        var inbox = MakeInbox();
        var (fragments, expectedAssembled, masterId) = MakeFragments(totalLen: 350, fragSize: 100);
        // Reverse order of arrival.
        foreach (var f in fragments.AsEnumerable().Reverse())
        {
            await inbox.DeliverAsync(f, "N0PEER", CancellationToken.None);
        }

        var stored = await database.GetRecentMessages(10);
        stored.Should().ContainSingle();
        stored[0].Id.Should().Be(masterId);
        stored[0].Payload.Should().Equal(expectedAssembled,
            "reassembly orders by FragmentIndex regardless of arrival order");
    }

    [Fact]
    public async Task Inbox_FragmentReDelivery_Idempotent()
    {
        // A retransmit of an already-stored fragment must not produce a
        // duplicate row, and must not advance the count toward
        // completion when already-counted.
        var inbox = MakeInbox();
        var (fragments, _, _) = MakeFragments(totalLen: 350, fragSize: 100);
        await inbox.DeliverAsync(fragments[0], "N0PEER", CancellationToken.None);
        await inbox.DeliverAsync(fragments[0], "N0PEER", CancellationToken.None);
        await inbox.DeliverAsync(fragments[0], "N0PEER", CancellationToken.None);

        var pending = await database.GetAllFragments();
        pending.Should().HaveCount(1, "duplicate deliveries upsert by (mid, index) primary key");
        // No assembled message yet (only one of four).
        (await database.GetRecentMessages(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Inbox_IncompleteFragmentSet_LeavesPartialBuffer()
    {
        var inbox = MakeInbox();
        var (fragments, _, _) = MakeFragments(totalLen: 350, fragSize: 100);

        // Deliver 3 of 4.
        for (var i = 0; i < 3; i++)
        {
            await inbox.DeliverAsync(fragments[i], "N0PEER", CancellationToken.None);
        }

        (await database.GetAllFragments()).Should().HaveCount(3);
        (await database.GetRecentMessages(10)).Should().BeEmpty(
            "no assembled message produced until all fragments arrive");
    }

    [Fact]
    public async Task SweepStaleFragments_OldRows_Dropped()
    {
        var inbox = MakeInbox();
        var (fragments, _, _) = MakeFragments(totalLen: 350, fragSize: 100);
        await inbox.DeliverAsync(fragments[0], "N0PEER", CancellationToken.None);
        await inbox.DeliverAsync(fragments[1], "N0PEER", CancellationToken.None);

        // Advance the clock past the timeout (default 7 days).
        clock.Advance(TimeSpan.FromDays(8));

        var dropped = await database.SweepStaleFragments(clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(7));
        dropped.Should().Be(2);
        (await database.GetAllFragments()).Should().BeEmpty();
    }

    [Fact]
    public async Task Inbox_FragmentForRemoteDestination_NoReassemblyAttempted()
    {
        // If the destination isn't local (transit through this node),
        // the inbox just persists the fragment as a regular DbMessage
        // row for the OutboundMessageManager to pick up. NO reassembly
        // happens here - that's the destination's job.
        var inbox = MakeInbox();
        var (fragments, _, masterId) = MakeFragments(
            totalLen: 350, fragSize: 100, destination: "elsewhere@N0OTHER");

        foreach (var f in fragments)
        {
            await inbox.DeliverAsync(f, "N0PEER", CancellationToken.None);
        }

        // Fragments table is empty (we don't buffer transit fragments).
        (await database.GetAllFragments()).Should().BeEmpty();
        // Each fragment is stored as its own DbMessage row, with
        // MasterId / FragmentIndex / FragmentTotal preserved so the
        // forwarder re-emits them when sending to the next hop.
        var stored = await database.GetRecentMessages(10);
        stored.Should().HaveCount(4);
        stored.Should().AllSatisfy(r => r.MasterId.Should().Be(masterId));
        stored.Select(r => r.FragmentIndex!.Value).OrderBy(i => i).Should().Equal(1, 2, 3, 4);
    }

    private DatabaseAndMqttInbox MakeInbox()
    {
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
            FragmentThresholdBytes = 100,
        });
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build());
        var brokerStub = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance,
            optionsMonitor,
            database,
            new AppTokenStore(NullLogger<AppTokenStore>.Instance),
            mqttServer);
        // We never call StartAsync - InjectInboundMessage is a no-op
        // when the broker isn't started, which is exactly what these
        // shape tests want (they assert against DbMessage / DbFragment
        // contents, not MQTT delivery).
        return new DatabaseAndMqttInbox(
            database, brokerStub, new InboundEventBus(), optionsMonitor,
            routingAlgorithm, routingContext, clock,
            NullLogger<DatabaseAndMqttInbox>.Instance);
    }

    /// <summary>Build a sequence of fragments + the expected assembled
    /// payload. Each fragment is a complete <see cref="BackhaulMessage"/>
    /// with the F2 headers populated, ready to feed into the inbox.</summary>
    private static (List<BackhaulMessage> Fragments, byte[] Assembled, string MasterId) MakeFragments(
        int totalLen, int fragSize, string destination = "app@N0SELF")
    {
        var assembled = new byte[totalLen];
        for (var i = 0; i < totalLen; i++) assembled[i] = (byte)(i % 256);
        var total = (totalLen + fragSize - 1) / fragSize;
        var masterId = "abc1234";
        var fragments = new List<BackhaulMessage>();
        for (var i = 0; i < total; i++)
        {
            var offset = i * fragSize;
            var len = Math.Min(fragSize, totalLen - offset);
            var chunk = new byte[len];
            Buffer.BlockCopy(assembled, offset, chunk, 0, len);
            fragments.Add(new BackhaulMessage(
                Id: $"frag{i + 1:00}",
                Destination: destination,
                Salt: 1000 + i,
                Ttl: 600,
                Payload: chunk,
                MasterId: masterId,
                FragmentIndex: i + 1,
                FragmentTotal: total));
        }
        return (fragments, assembled, masterId);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
