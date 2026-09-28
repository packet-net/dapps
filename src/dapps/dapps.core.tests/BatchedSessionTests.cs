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
/// proposals 1 and 2), and so does the traffic that comes back. Drives the
/// real forwarder and the real <see cref="Dappsv1SessionBackhaul"/> against
/// the real <see cref="InboundConnectionHandler"/> at the far end of a
/// loopback socket, and counts how many times the forwarder dialled.
///
/// Both ends share one SQLite file, as they do in the other tests that
/// run a handler: each forwarder only sees messages that aren't
/// addressed to its own callsign, so the queues don't overlap.
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
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
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
    public async Task AReplyToTheFarEndsPost_GoesOnTheSameConnection()
    {
        // The WPS pattern: the far end has a post waiting for us, our app
        // answers it the moment it lands, and the answer should ride the
        // same session rather than a new one a few seconds later.
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var farWakeup = new ForwarderWakeup();
        var farDirectory = new SessionDirectory(farWakeup);
        var transport = new LoopbackPeerTransport(database, farInbox, farDirectory);
        var ourWakeup = new ForwarderWakeup();
        var ourInbox = new RecordingInbox
        {
            OnDelivered = m =>
            {
                Queue("reply to " + Encoding.UTF8.GetString(m.Payload), DateTime.UtcNow);
                ourWakeup.Wake();
            },
        };
        var forwarder = MakeForwarder(transport, ourInbox);
        var farForwarder = MakeFarForwarder(farDirectory);
        Queue("hello", DateTime.UtcNow.AddSeconds(-10));
        QueueAtTheFarEndForUs("their post", DateTime.UtcNow.AddSeconds(-5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = RunWhenWokenAsync(farForwarder, farWakeup, stop.Token);
        _ = RunWhenWokenAsync(forwarder, ourWakeup, stop.Token);

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await WaitForAsync(() => farInbox.Texts.Count == 2);
        await stop.CancelAsync();

        transport.Connects.Should().Be(1);
        ourInbox.Texts.Should().Equal("their post");
        farInbox.Texts.Should().Equal("hello", "reply to their post");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    /// <summary>Runs a forwarder whenever it's woken, as the real node does.</summary>
    private static async Task RunWhenWokenAsync(OutboundMessageManager forwarder, ForwarderWakeup wakeup, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await wakeup.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, ct)) await forwarder.DoRun(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Test over.
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The far end's forwarder: it only ever hands its traffic to
    /// the session we open, never dials.</summary>
    private static OutboundMessageManager MakeFarForwarder(SessionDirectory farDirectory)
    {
        var farOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Them });
        var farDatabase = new Database(NullLogger<Database>.Instance, farOptions);
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = Us, BearerPort = 0 });
        }
        return new OutboundMessageManager(
            farDatabase, NullLoggerFactory.Instance, farOptions,
            [new Dappsv1SessionBackhaul(new NoDialTransport(), NullLoggerFactory.Instance)],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(farDatabase, farOptions),
            openSessions: farDirectory);
    }

    [Fact]
    public async Task ALinkThatDropsBeforeAnyAnswer_PutsTheNeighbourInCooldown_InsteadOfRedialling()
    {
        // The rules crossed, then the link went before our message was
        // answered (a marginal link, or the peer failing on it). Without a
        // failure, the session ending would have the forwarder dial
        // straight back, again and again.
        var ct = TestContext.Current.CancellationToken;
        var transport = new ScriptedFarEnd(async (peer, c) =>
        {
            await peer.WriteLineAsync($"DAPPSv1>\nexchange id=far001 hold=0 inline=256", c);
            await peer.ReadLineAsync(c);            // our rules
            await peer.ReadWithPayloadAsync(c);     // our message
            peer.Close();
        });
        var backoff = new OutboundDestinationBackoff();
        var forwarder = MakeForwarder(transport, ourInbox: null, backoff);
        Queue("doomed", DateTime.UtcNow.AddSeconds(-5));

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await forwarder.DoRun(ct).WaitAsync(Patience, ct);

        transport.Connects.Should().Be(1, "the second run is in the cooldown the failure started");
        backoff.IsInCooldown(Them, out _).Should().BeTrue();
        (await database.GetPendingOutboundMessages()).Should().ContainSingle("it's still queued for when the cooldown ends");
    }

    private OutboundMessageManager MakeForwarder(IDappsOutboundTransport transport, IBackhaulInbox? ourInbox, OutboundDestinationBackoff? backoff = null)
    {
        var backhaul = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: ourInbox);
        return new OutboundMessageManager(
            database, NullLoggerFactory.Instance, options, [backhaul],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(database, options),
            destinationBackoff: backoff);
    }

    /// <summary>Each connect is a loopback socket with a script playing the far end.</summary>
    private sealed class ScriptedFarEnd(Func<LinePeer, CancellationToken, Task> script) : IDappsOutboundTransport
    {
        public int Connects;

        public async Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref Connects);
            var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(stoppingToken);
            _ = Task.Run(() => script(new LinePeer(theirs), stoppingToken), stoppingToken);
            return new StreamConnection(ours);
        }

        private sealed class StreamConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static void Queue(string text, DateTime createdAt) => Insert(text, $"app@{Them}", createdAt);

    /// <summary>Mail addressed to us, queued at the far end.</summary>
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
    private sealed class LoopbackPeerTransport(Database database, IBackhaulInbox farInbox, SessionDirectory? farDirectory = null) : IDappsOutboundTransport
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
                    server.GetStream(), localCallsign, NullLoggerFactory.Instance, database, farInbox,
                    directory: farDirectory);
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

    private sealed class NoDialTransport : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken) =>
            Task.FromException<IDappsConnection>(new InvalidOperationException("the far end should not dial here"));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
