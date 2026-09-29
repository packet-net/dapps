using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// The received-message memory (<see cref="DbReceived"/>) on the wire and
/// in the database: an offer for a message the node already has is
/// answered <c>ack</c> without its payload, and the memory itself records
/// each message once, forgets it when asked and sweeps it when it
/// expires. The offering side counting that <c>ack</c> as delivered is in
/// <see cref="ExchangeSessionTests"/>; the inbox's own refusal to deliver a
/// repeat is in <see cref="DatabaseAndMqttInboxTests"/>.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DuplicateOfferTests : IAsyncLifetime
{
    private const string Us = "N0CALL";
    private const string Them = "N0DEST";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-duplicate-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
        }
        database = new Database(NullLogger<Database>.Instance,
            new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Us }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnOfferForAMessageWeHave_IsAnsweredAck_AndItsPayloadNeverComes()
    {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirs, Them, NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var peer = new LinePeer(ours);
        (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");

        var had = Encoding.UTF8.GetBytes("delivered before the sender restarted");
        var hadId = DappsMessage.ComputeHash(had, 7L)[..7];
        await StoredAsync(DbReceived.MakeKey(hadId, 7L, had.Length));

        await peer.WriteLineAsync($"ihave {hadId} len={had.Length} fmt=p s=7 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"ack {hadId}", "we already have it, so the payload needn't cross the air again");

        // The session carries on as normal for a message we haven't seen.
        var fresh = Encoding.UTF8.GetBytes("a new one");
        var freshId = DappsMessage.ComputeHash(fresh, 8L)[..7];
        await peer.WriteLineAsync($"ihave {freshId} len={fresh.Length} fmt=p s=8 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {freshId}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {freshId}\n"), .. fresh], ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"ack {freshId}");

        inbox.Texts.Should().Equal("a new one");
    }

    [Fact]
    public async Task AnOfferWithTheSameIdButAnotherSalt_IsANewMessage()
    {
        // The id is only 28 bits of hash: remembering by it alone would
        // one day drop a genuinely new message. The salt tells them apart.
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirs, Them, NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var peer = new LinePeer(ours);
        (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");

        var payload = Encoding.UTF8.GetBytes("hello");
        var id = DappsMessage.ComputeHash(payload, 9L)[..7];
        await StoredAsync(DbReceived.MakeKey(id, 1L, payload.Length));

        await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p s=9 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {id}");
    }

    [Fact]
    public async Task AMessageLeftBeingStored_ByANodeThatDied_IsTakenAgain()
    {
        // The node claimed the message, then died before storing it. The
        // sender never saw an ack and offers it again: answering "already
        // got it" would lose it.
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirs, Them, NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var peer = new LinePeer(ours);
        (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");

        var payload = Encoding.UTF8.GetBytes("claimed, never stored");
        var id = DappsMessage.ComputeHash(payload, 3L)[..7];
        (await database.ClaimReceivedAsync(DbReceived.MakeKey(id, 3L, payload.Length), DateTime.UtcNow.AddHours(1), Them)).Should().BeNull();

        await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p s=3 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {id}");
    }

    [Fact]
    public async Task AnUnsaltedOffer_IsNeverAnsweredAck()
    {
        // Without s= a message can't be told from a later one with the same
        // content, so it isn't remembered, and its payload is always taken.
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirs, Them, NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var peer = new LinePeer(ours);
        (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");
        var payload = Encoding.UTF8.GetBytes("no salt");
        var id = DappsMessage.ComputeHash(payload, null)[..7];

        for (var i = 0; i < 2; i++)
        {
            await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p dst=app@{Us}", ct);
            (await peer.ReadLineAsync(ct)).Should().Be($"send {id}");
            await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {id}\n"), .. payload], ct);
            (await peer.ReadLineAsync(ct)).Should().Be($"ack {id}");
        }
    }

    [Fact]
    public async Task TheMemory_CountsAMessageOnceStored_UntilItExpires()
    {
        var now = DateTime.UtcNow;
        var key = DbReceived.MakeKey("abc1234", 42L, 10);

        (await database.ClaimReceivedAsync(key, now.AddMinutes(10), Them)).Should().BeNull("the first copy is ours to store");
        (await database.HasReceivedAsync(key)).Should().BeFalse("it isn't stored yet");

        // Storing it failed: the claim goes, and the retry is ours again.
        await database.ForgetReceivedAsync(key);
        (await database.ClaimReceivedAsync(key, now.AddMinutes(10), Them)).Should().BeNull();

        // A claim left behind (the node died before storing it) is taken over.
        (await database.ClaimReceivedAsync(key, now.AddMinutes(10), Them)).Should().BeNull();

        await database.CommitReceivedAsync(key);
        (await database.HasReceivedAsync(key)).Should().BeTrue();
        (await database.ClaimReceivedAsync(key, now.AddMinutes(10), "N0OTHR"))!.LinkSourceCallsign.Should().Be(Them, "it's a repeat of the first copy");
        await database.ForgetReceivedAsync(key);
        (await database.HasReceivedAsync(key)).Should().BeTrue("a stored message is never forgotten early");

        (await database.SweepReceivedAsync(now.AddMinutes(11))).Should().Be(1);
        (await database.HasReceivedAsync(key)).Should().BeFalse();
    }

    private async Task StoredAsync(string key)
    {
        (await database.ClaimReceivedAsync(key, DateTime.UtcNow.AddHours(1), Them)).Should().BeNull();
        await database.CommitReceivedAsync(key);
    }

    /// <summary>The shared loopback pair, kept open until <paramref name="ct"/> is cancelled (see <see cref="ExchangeTestKit.LoopbackPairAsync"/>).</summary>
    private static async Task<(NetworkStream Ours, NetworkStream Theirs)> LoopbackPairAsync(CancellationToken ct) => await ExchangeTestKit.LoopbackPairAsync(ct);

    private sealed class LinePeer(NetworkStream stream)
    {
        public Task WriteLineAsync(string line, CancellationToken ct) => WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

        public async Task WriteAsync(byte[] bytes, CancellationToken ct)
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Patience);
            var line = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one, cts.Token);
                if (n == 0) throw new EndOfStreamException("the other end closed the link");
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray());
                line.Add(one[0]);
            }
        }

    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        private readonly List<string> texts = [];

        public IReadOnlyList<string> Texts
        {
            get { lock (texts) return [.. texts]; }
        }

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            lock (texts) texts.Add(Encoding.UTF8.GetString(message.Payload));
            return Task.CompletedTask;
        }
    }
}
