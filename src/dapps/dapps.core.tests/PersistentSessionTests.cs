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
/// Held sessions (#187 proposal 9), end to end with two nodes: ours
/// (N0CALL) dials the far end (N0DEST) over a loopback socket, and the
/// far end is the real <see cref="InboundConnectionHandler"/> plus its
/// own forwarder. They share one SQLite file, as the other two-ended
/// tests here do; each forwarder only sees messages that aren't
/// addressed to its own callsign, so the queues don't overlap.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class PersistentSessionTests : IAsyncLifetime
{
    private const string Us = "N0CALL";
    private const string Them = "N0DEST";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private string dbPath = null!;
    private Database ourDatabase = null!;
    private Database farDatabase = null!;
    private TestOptionsMonitor<SystemOptions> ourOptions = null!;
    private TestOptionsMonitor<SystemOptions> farOptions = null!;
    private readonly RecordingInbox ourInbox = new();
    private readonly RecordingInbox farInbox = new();
    private readonly InboundSessionDirectory farDirectory = new();
    private readonly PeerSessionRegistry farRegistry = new();
    private readonly PeerSessionRegistry ourRegistry = new();

    /// <summary>Stops the held sessions and far-end handlers each test leaves running.</summary>
    private readonly CancellationTokenSource testLifetime = new();

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-held-test-{Guid.NewGuid():N}.db");
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
            c.Insert(new DbNeighbour { Callsign = Us, BearerPort = 0 });
        }
        ourOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Us });
        farOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Them });
        ourDatabase = new Database(NullLogger<Database>.Instance, ourOptions);
        farDatabase = new Database(NullLogger<Database>.Instance, farOptions);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        testLifetime.Cancel();
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AfterItsTraffic_TheLinkStaysOpen_AndTheNextMessageGoesOnIt()
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, testLifetime.Token).Token;
        var (backhaul, transport, forwarder) = MakeOurEnd(ourTail: 60, farTail: 60);

        Queue("first", $"app@{Them}");
        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await WaitForAsync(() => farInbox.Texts.Count == 1);
        backhaul.HeldPeers.Should().Contain(Them, "both ends allow a hold, so the link stays up");

        Queue("second", $"app@{Them}");
        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await WaitForAsync(() => farInbox.Texts.Count == 2);

        farInbox.Texts.Should().Equal("first", "second");
        transport.Connects.Should().Be(1, "the second message went on the held link, not a new connection");
    }

    [Fact]
    public async Task MailTheFarEndHasForUs_ComesOverTheHeldLink_WithoutItDialling()
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, testLifetime.Token).Token;
        var (backhaul, transport, forwarder) = MakeOurEnd(ourTail: 60, farTail: 60);
        Queue("hello", $"app@{Them}");
        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await WaitForAsync(() => backhaul.HeldPeers.Count == 1);

        // The far end's reply, queued at the far end for us.
        Queue("reply", $"app@{Us}");
        var (farForwarder, farDials) = MakeFarForwarder();
        await farForwarder.DoRun(ct).WaitAsync(Patience, ct);

        await WaitForAsync(() => ourInbox.Texts.Count == 1);
        ourInbox.Texts.Should().Equal("reply");
        farDials.Count.Should().Be(0, "the far end handed it to our session and said pending; it didn't dial us");
        transport.Connects.Should().Be(1);
        (await farDatabase.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task AQuietLink_IsClosedWhenTheAgreedHoldRunsOut()
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, testLifetime.Token).Token;
        var (backhaul, transport, forwarder) = MakeOurEnd(ourTail: 1, farTail: 60);
        Queue("only", $"app@{Them}");
        await forwarder.DoRun(ct).WaitAsync(Patience, ct);
        await WaitForAsync(() => backhaul.HeldPeers.Count == 1);

        await WaitForAsync(() => backhaul.HeldPeers.Count == 0 && transport.FarSessionsOpen == 0);

        backhaul.HeldPeers.Should().BeEmpty("one quiet second, the lower of the two settings, has passed");
        transport.FarSessionsOpen.Should().Be(0, "our quit ended the far end's session too");
    }

    [Fact]
    public async Task TheFarEndCanDeclineToHold()
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, testLifetime.Token).Token;
        var (backhaul, transport, forwarder) = MakeOurEnd(ourTail: 60, farTail: 0);
        Queue("only", $"app@{Them}");

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);

        farInbox.Texts.Should().Equal("only");
        backhaul.HeldPeers.Should().BeEmpty();
        await WaitForAsync(() => transport.FarSessionsOpen == 0);
        transport.FarSessionsOpen.Should().Be(0);
    }

    [Fact]
    public async Task WithoutAHoldSetting_TheSessionEndsAsItAlwaysDid()
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, testLifetime.Token).Token;
        var (backhaul, transport, forwarder) = MakeOurEnd(ourTail: 0, farTail: 60);
        Queue("only", $"app@{Them}");

        await forwarder.DoRun(ct).WaitAsync(Patience, ct);

        backhaul.HeldPeers.Should().BeEmpty();
        transport.Connects.Should().Be(1);
    }

    private (Dappsv1SessionBackhaul Backhaul, LoopbackToFarEnd Transport, OutboundMessageManager Forwarder) MakeOurEnd(int ourTail, int farTail)
    {
        var transport = new LoopbackToFarEnd(this, farTail);
        var backhaul = new Dappsv1SessionBackhaul(
            transport, NullLoggerFactory.Instance,
            opportunisticInbox: ourInbox,
            opportunisticEnabled: () => true,
            tailFor: (_, _) => Task.FromResult(ourTail));
        var forwarder = new OutboundMessageManager(
            ourDatabase, NullLoggerFactory.Instance, ourOptions, [backhaul],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(ourDatabase, ourOptions),
            peerSessions: ourRegistry);
        return (backhaul, transport, forwarder);
    }

    /// <summary>The far end's forwarder, with a bearer that records any
    /// dial instead of making it.</summary>
    private (OutboundMessageManager Forwarder, List<string> Dials) MakeFarForwarder()
    {
        var dials = new List<string>();
        var backhaul = new Dappsv1SessionBackhaul(new RecordingTransport(dials), NullLoggerFactory.Instance);
        var forwarder = new OutboundMessageManager(
            farDatabase, NullLoggerFactory.Instance, farOptions, [backhaul],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(farDatabase, farOptions),
            peerSessions: farRegistry, inboundSessions: farDirectory);
        return (forwarder, dials);
    }

    private static void Queue(string text, string destination)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var createdAt = DateTime.UtcNow;
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Each connect is a fresh loopback socket with the far end's real
    /// handler on it, registered the way the inbound services do: a lease
    /// in the far end's registry and an entry in its session directory.
    /// </summary>
    private sealed class LoopbackToFarEnd(PersistentSessionTests test, int farTail) : IDappsOutboundTransport
    {
        private int connects;
        private int farSessionsOpen;

        public int Connects => connects;
        public int FarSessionsOpen => farSessionsOpen;

        public async Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref connects);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stoppingToken);
                var server = await listener.AcceptTcpClientAsync(stoppingToken);
                await connecting;
                var handler = new InboundConnectionHandler(
                    server.GetStream(), localCallsign, NullLoggerFactory.Instance, test.farDatabase, test.farInbox,
                    directory: test.farDirectory,
                    tailFor: (_, _) => Task.FromResult(farTail));
                Interlocked.Increment(ref farSessionsOpen);
                _ = Task.Run(async () =>
                {
                    using (test.farRegistry.Acquire(localCallsign, "inbound"))
                    {
                        try { await handler.Handle(stoppingToken); }
                        finally { Interlocked.Decrement(ref farSessionsOpen); }
                    }
                }, stoppingToken);
                // Our end's lease on the link, as the real transport takes
                // one: the held link keeps it, so the forwarder must hand
                // work to the link rather than wait for it to go.
                return new Connection(client, test.ourRegistry.Acquire(remoteCallsign, "outbound"));
            }
            finally
            {
                listener.Stop();
            }
        }

        private sealed class Connection(TcpClient client, IDisposable lease) : IDappsConnection
        {
            public Stream Stream => client.GetStream();
            public ValueTask DisposeAsync()
            {
                client.Dispose();
                lease.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingTransport(List<string> dials) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            lock (dials) dials.Add(remoteCallsign);
            return Task.FromException<IDappsConnection>(new InvalidOperationException("the far end should not dial here"));
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

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
