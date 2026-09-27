using System.Net;
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
/// answered <c>ack</c> without its payload, the offering side counts that
/// as delivered, and the memory itself records each message once, forgets
/// it when asked and sweeps it when it expires. The inbox's own refusal to
/// deliver a repeat is in <see cref="DatabaseAndMqttInboxTests"/>.
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
            c.CreateTable<DbOffer>();
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

        var had = Encoding.UTF8.GetBytes("delivered before the sender restarted");
        var hadId = DappsMessage.ComputeHash(had, 7L)[..7];
        var now = DateTime.UtcNow;
        (await database.TryRecordReceivedAsync(DbReceived.MakeKey(hadId, 7L, had.Length), now, now.AddHours(1), Them)).Should().BeTrue();

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

        var payload = Encoding.UTF8.GetBytes("hello");
        var id = DappsMessage.ComputeHash(payload, 9L)[..7];
        var now = DateTime.UtcNow;
        await database.TryRecordReceivedAsync(DbReceived.MakeKey(id, 1L, payload.Length), now, now.AddHours(1), Them);

        await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p s=9 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {id}");
    }

    [Fact]
    public async Task ThePushingSide_CountsAnAckedOffer_AsDelivered_WithoutSendingThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var peer = new LinePeer(theirs);
        var payload = Encoding.UTF8.GetBytes("the peer already has this one");
        var message = new BackhaulMessage(DappsMessage.ComputeHash(payload, 5L)[..7], $"app@{Them}", 5L, 600, payload);

        var farEnd = Task.Run(async () =>
        {
            await peer.WriteLineAsync("DAPPSv1>", ct);
            var offer = await peer.ReadLineAsync(ct);
            await peer.WriteLineAsync($"ack {message.Id}", ct);
            return offer;
        }, ct);

        var client = new DappsProtocolClient(ours, NullLoggerFactory.Instance);
        (await client.ReadInitialPromptAsync(ct)).Should().BeTrue();
        var outcome = await client.PushAsync(message, compress: true, ct);

        outcome.Should().Be(DappsProtocolClient.PushOutcome.Accepted);
        (await farEnd).Should().StartWith($"ihave {message.Id} ");
        (await peer.NothingMoreArrivesWithinAsync(TimeSpan.FromMilliseconds(300), ct)).Should().BeTrue("the payload wasn't sent");
    }

    [Fact]
    public async Task TheMemory_RecordsEachMessageOnce_UntilItExpiresOrIsForgotten()
    {
        var now = DateTime.UtcNow;
        var key = DbReceived.MakeKey("abc1234", 42L, 10);

        (await database.TryRecordReceivedAsync(key, now, now.AddMinutes(10), Them)).Should().BeTrue();
        (await database.TryRecordReceivedAsync(key, now, now.AddMinutes(10), Them)).Should().BeFalse("it's a repeat");
        (await database.HasReceivedAsync(key, now)).Should().BeTrue();

        // Storing it failed: forget it, so the sender's retry is accepted.
        await database.ForgetReceivedAsync(key);
        (await database.TryRecordReceivedAsync(key, now, now.AddMinutes(10), Them)).Should().BeTrue();

        // Once its memory has run out it counts as new again, swept or not.
        var later = now.AddMinutes(11);
        (await database.HasReceivedAsync(key, later)).Should().BeFalse();
        (await database.TryRecordReceivedAsync(key, later, later.AddMinutes(10), Them)).Should().BeTrue();
        (await database.SweepReceivedAsync(later.AddMinutes(11))).Should().Be(1);
        (await database.HasReceivedAsync(key, later)).Should().BeFalse();
    }

    private static async Task<(NetworkStream Ours, NetworkStream Theirs)> LoopbackPairAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            await connecting;
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

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

        public async Task<bool> NothingMoreArrivesWithinAsync(TimeSpan wait, CancellationToken ct)
        {
            await Task.Delay(wait, ct);
            return !stream.DataAvailable;
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
