using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan F3b - scheduled poll. Tests the enumeration, opt-out
/// handling, and persistence shape of <see cref="PollSchedulerService"/>
/// against a real Database. The actual on-the-wire poll is mocked via
/// a fake transport whose canned reply yields zero or more messages.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class F3PollSchedulerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-f3b-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbPolledNode>();
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
        }
        database = new Database(NullLogger<Database>.Instance,
            new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 0 }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EnumerateTargets_NoNeighbours_ReturnsEmpty()
    {
        var sched = MakeScheduler(new RecordingPollTransport(), new RecordingInbox());
        (await sched.EnumerateTargetsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_AgwNeighbour_Included()
    {
        await database.UpsertNeighbour("N0THEM-9", bearerPort: 1);
        var sched = MakeScheduler(new RecordingPollTransport(), new RecordingInbox());

        var targets = await sched.EnumerateTargetsAsync();
        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PollSchedulerService.PollTarget("N0THEM-9", 1));
    }

    [Fact]
    public async Task EnumerateTargets_UdpOnlyNeighbour_Skipped()
    {
        // Plan F3 is AGW-only by design - UDP-only neighbours don't
        // have a session-based protocol to poll over.
        await database.UpsertNeighbour("N0UDP-9", bearerPort: null, udpEndpoint: "127.0.0.1:1880");
        var sched = MakeScheduler(new RecordingPollTransport(), new RecordingInbox());

        (await sched.EnumerateTargetsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_OptOutRow_Excluded()
    {
        await database.UpsertNeighbour("N0OPT-9", bearerPort: 1);
        await database.UpsertPolledNode(new DbPolledNode { Callsign = "N0OPT-9", OptOut = true });

        var sched = MakeScheduler(new RecordingPollTransport(), new RecordingInbox());
        (await sched.EnumerateTargetsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PollAndRecord_SuccessNoMessages_RecordsCleanRow()
    {
        await database.UpsertNeighbour("N0EMPTY-9", bearerPort: 1);
        // Server: just emits the prompt, then immediate drained-prompt for rev.
        var transport = new RecordingPollTransport(("N0EMPTY-9", "DAPPSv1>\nDAPPSv1>\n"));
        var inbox = new RecordingInbox();

        var sched = MakeScheduler(transport, inbox);
        var row = await sched.PollAndRecordAsync("N0US", "N0EMPTY-9", 1, CancellationToken.None);

        row.LastSuccessAt.Should().NotBeNull();
        row.ConsecutiveFailures.Should().Be(0);
        row.MessagesDrained.Should().Be(0);
        row.LastError.Should().BeEmpty();
        inbox.Delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAndRecord_OneQueuedMessage_DrainsAndIncrementsCount()
    {
        await database.UpsertNeighbour("N0HASMAIL-9", bearerPort: 2);
        // Build server bytes: prompt → ihave → data + payload → drained-prompt.
        var payload = "queued"u8.ToArray();
        long salt = 555;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        AppendUtf8(serverBytes, $"ihave {id} len={payload.Length} fmt=p dst=app@N0US s={salt}\n");
        AppendUtf8(serverBytes, $"data {id}\n");
        serverBytes.Write(payload);
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        var transport = new RecordingPollTransport(("N0HASMAIL-9", Encoding.UTF8.GetString(serverBytes.ToArray())));
        var inbox = new RecordingInbox();

        var sched = MakeScheduler(transport, inbox);
        var row = await sched.PollAndRecordAsync("N0US", "N0HASMAIL-9", 2, CancellationToken.None);

        row.LastSuccessAt.Should().NotBeNull();
        row.MessagesDrained.Should().Be(1);
        inbox.Delivered.Should().ContainSingle();
        inbox.Delivered[0].Id.Should().Be(id);
        inbox.Delivered[0].Payload.Should().Equal(payload);
    }

    [Fact]
    public async Task PollAndRecord_SuccessAfterFailure_ResetsCounter()
    {
        await database.UpsertNeighbour("N0FLAP-9", bearerPort: 1);
        // First call: server doesn't emit prompt - fails. Second call:
        // emits prompt + immediate drained - success.
        var transport = new RecordingPollTransport(
            ("N0FLAP-9", ""),                       // EOF before prompt
            ("N0FLAP-9", "DAPPSv1>\nDAPPSv1>\n"));   // empty drain
        var sched = MakeScheduler(transport, new RecordingInbox());

        await sched.PollAndRecordAsync("N0US", "N0FLAP-9", 1, CancellationToken.None);
        var fail = await database.GetPolledNode("N0FLAP-9");
        fail!.ConsecutiveFailures.Should().Be(1);

        await sched.PollAndRecordAsync("N0US", "N0FLAP-9", 1, CancellationToken.None);
        var success = await database.GetPolledNode("N0FLAP-9");
        success!.ConsecutiveFailures.Should().Be(0);
        success.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAndRecord_PreservesOptOutAcrossUpdate()
    {
        await database.UpsertNeighbour("N0OPT-9", bearerPort: 1);
        await database.UpsertPolledNode(new DbPolledNode { Callsign = "N0OPT-9", OptOut = true });
        var transport = new RecordingPollTransport(("N0OPT-9", "DAPPSv1>\nDAPPSv1>\n"));
        var sched = MakeScheduler(transport, new RecordingInbox());

        await sched.PollAndRecordAsync("N0US", "N0OPT-9", 1, CancellationToken.None);

        var row = await database.GetPolledNode("N0OPT-9");
        row.Should().NotBeNull();
        row!.OptOut.Should().BeTrue("opt-out is operator state and survives result updates");
        row.LastSuccessAt.Should().NotBeNull();
    }

    private PollSchedulerService MakeScheduler(IDappsOutboundTransport transport, IBackhaulInbox inbox)
    {
        var poller = new NodePoller(transport, inbox, TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<NodePoller>.Instance);
        var opts = new TestOptionsMonitor<SystemOptions>(
            new SystemOptions { Callsign = "N0US", DefaultBearerPort = 0, ScheduledPollEnabled = true });
        return new PollSchedulerService(poller, database, opts, TimeProvider.System,
            NullLogger<PollSchedulerService>.Instance)
        {
            StartupDelay = TimeSpan.Zero,
            MinInterPollDelay = TimeSpan.Zero,
            MaxInterPollDelay = TimeSpan.FromMilliseconds(1),
        };
    }

    private static void AppendUtf8(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes, 0, bytes.Length);
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        public List<BackhaulMessage> Delivered { get; } = new();
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>FakeDuplexStream-backed transport with per-callsign canned
    /// receiver bytes, mirroring the pattern in
    /// <see cref="ProbeSchedulerServiceTests"/>'s RecordingTransport.</summary>
    private sealed class RecordingPollTransport(params (string Remote, string Reply)[] cannedByCallsign) : IDappsOutboundTransport
    {
        private readonly Queue<(string Remote, string Reply)> _queue = new(cannedByCallsign);
        public List<(string Remote, int Port)> Connects { get; } = new();

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Connects.Add((remoteCallsign, bearerPort));
            string reply = "";
            if (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                reply = string.Equals(next.Remote, remoteCallsign, StringComparison.OrdinalIgnoreCase)
                    ? next.Reply
                    : $"no canned reply for {remoteCallsign}";
            }
            return Task.FromResult<IDappsConnection>(new FakeConnection(new FakeDuplexStream(Encoding.UTF8.GetBytes(reply))));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
