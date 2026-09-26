using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace dapps.core.tests;

/// <summary>
/// Everything queued for one neighbour goes out on one session (PR #187's
/// proposals 1 and 2). Drives the real forwarder and the real
/// <see cref="Dappsv1SessionBackhaul"/> against the real
/// <see cref="InboundConnectionHandler"/> at the far end of a loopback
/// socket, and counts how many times the forwarder dialled.
///
/// Both ends share one SQLite file, as they do in the other tests that
/// run a handler: the far end only touches offers and the messages
/// addressed to our callsign, which our own forwarder treats as local.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class BatchedSessionTests : IAsyncLifetime
{
    private const string Us = "N0CALL";
    private const string Them = "N0DEST";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private string dbPath = null!;
    private Database database = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-batched-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.Insert(new DbNeighbour { Callsign = Them, BearerPort = 0 });
        }
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Us });
        database = new Database(NullLogger<Database>.Instance, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ThreeQueuedMessages_OneConnection_AllDeliveredInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var transport = new LoopbackPeerTransport(database, farInbox);
        var forwarder = MakeForwarder(transport, ourInbox: null);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        Queue("one", t0);
        Queue("two", t0.AddSeconds(1));
        Queue("three", t0.AddSeconds(2));

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);

        transport.Connects.Should().Be(1, "the trace in PR #187 had one connection per message; all three share one now");
        farInbox.Texts.Should().Equal("one", "two", "three");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task AReplyQueuedDuringTheRevDrain_GoesOutBeforeHangingUp()
    {
        // The WPS pattern: the far end has a post waiting for us, our app
        // answers it the moment it lands, and the answer should ride the
        // same session rather than a new one a few seconds later.
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var transport = new LoopbackPeerTransport(database, farInbox);
        var ourInbox = new RecordingInbox
        {
            OnDelivered = m => Queue("reply to " + Encoding.UTF8.GetString(m.Payload), DateTime.UtcNow),
        };
        var forwarder = MakeForwarder(transport, ourInbox);
        Queue("hello", DateTime.UtcNow.AddSeconds(-10));
        QueueAtTheFarEndForUs("their post", DateTime.UtcNow.AddSeconds(-5));

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);

        transport.Connects.Should().Be(1);
        ourInbox.Texts.Should().Equal("their post");
        farInbox.Texts.Should().Equal("hello", "reply to their post");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    private OutboundMessageManager MakeForwarder(IDappsOutboundTransport transport, IBackhaulInbox? ourInbox)
    {
        var backhaul = new Dappsv1SessionBackhaul(
            transport, NullLoggerFactory.Instance,
            opportunisticInbox: ourInbox,
            opportunisticEnabled: () => ourInbox is not null);
        return new OutboundMessageManager(
            database, NullLoggerFactory.Instance, options, [backhaul],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(database, options));
    }

    private static void Queue(string text, DateTime createdAt) => Insert(text, $"app@{Them}", createdAt);

    /// <summary>Mail addressed to us: the far end's rev drain offers it.</summary>
    private static void QueueAtTheFarEndForUs(string text, DateTime createdAt) => Insert(text, $"app@{Us}", createdAt);

    private static void Insert(string text, string destination, DateTime createdAt)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var salt = createdAt.Ticks;
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = dapps.client.DappsMessage.ComputeHash(payload, salt)[..7],
            Payload = payload,
            Salt = salt,
            Destination = destination,
            SourceCallsign = Us,
            AdditionalProperties = "{}",
            CreatedAt = createdAt,
        });
    }

    /// <summary>
    /// Each connect is a fresh loopback socket with the real receiver on
    /// the far end, serving the session as the peer would.
    /// </summary>
    private sealed class LoopbackPeerTransport(Database database, IBackhaulInbox farInbox) : IDappsOutboundTransport
    {
        public int Connects;

        public async Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref Connects);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stoppingToken);
                var server = await listener.AcceptTcpClientAsync(stoppingToken);
                await connecting;
                var handler = new InboundConnectionHandler(
                    server.GetStream(), localCallsign, NullLoggerFactory.Instance, database, farInbox);
                _ = Task.Run(() => handler.Handle(stoppingToken), stoppingToken);
                return new Connection(client);
            }
            finally
            {
                listener.Stop();
            }
        }

        private sealed class Connection(TcpClient client) : IDappsConnection
        {
            public Stream Stream => client.GetStream();
            public ValueTask DisposeAsync()
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        private readonly List<string> texts = [];

        public Action<BackhaulMessage>? OnDelivered { get; init; }

        public IReadOnlyList<string> Texts
        {
            get { lock (texts) return [.. texts]; }
        }

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            lock (texts) texts.Add(Encoding.UTF8.GetString(message.Payload));
            OnDelivered?.Invoke(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
