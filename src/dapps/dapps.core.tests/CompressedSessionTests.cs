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
/// zstd-with-dictionary payloads (<c>fmt=z1</c>) end to end: the real
/// forwarder and session backhaul against the real
/// <see cref="InboundConnectionHandler"/> over a loopback socket, with a
/// tap on our end of the link to see what actually went on the wire.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class CompressedSessionTests : IAsyncLifetime
{
    private const string Us = "N0CALL";
    private const string Them = "N0DEST";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // A WPS replication post in the short-key form it sends today.
    private const string WpsPost =
        """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""";

    private string dbPath = null!;
    private Database database = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-compressed-test-{Guid.NewGuid():N}.db");
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
    public async Task APost_GoesCompressed_AndArrivesIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var transport = new TappedLoopbackTransport(database, farInbox, farEndCompresses: false);
        Queue(WpsPost, $"app@{Them}", DateTime.UtcNow.AddSeconds(-5));

        await MakeForwarder(transport, compress: true, ourInbox: null).DoRun(ct).WaitAsync(Patience, ct);

        farInbox.Texts.Should().Equal(WpsPost);
        transport.Sent.Should().Contain(" fmt=z1 clen=");
        transport.Sent.Should().NotContain("\"Evening all", "the payload went compressed, not as readable JSON");
    }

    [Fact]
    public async Task CompressionOffForTheNeighbour_GoesPlain()
    {
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var transport = new TappedLoopbackTransport(database, farInbox, farEndCompresses: false);
        Queue(WpsPost, $"app@{Them}", DateTime.UtcNow.AddSeconds(-5));

        await MakeForwarder(transport, compress: false, ourInbox: null).DoRun(ct).WaitAsync(Patience, ct);

        farInbox.Texts.Should().Equal(WpsPost);
        transport.Sent.Should().Contain(" fmt=p ").And.NotContain("fmt=z1");
    }

    [Fact]
    public async Task MailInTheRevDrain_ComesBackCompressed_AndArrivesIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var farInbox = new RecordingInbox();
        var ourInbox = new RecordingInbox();
        var transport = new TappedLoopbackTransport(database, farInbox, farEndCompresses: true);
        Queue("hello", $"app@{Them}", DateTime.UtcNow.AddSeconds(-10));
        Queue(WpsPost, $"app@{Us}", DateTime.UtcNow.AddSeconds(-5));

        await MakeForwarder(transport, compress: false, ourInbox).DoRun(ct).WaitAsync(Patience, ct);

        ourInbox.Texts.Should().Equal(WpsPost);
        transport.Received.Should().Contain(" fmt=z1 clen=");
    }

    [Fact]
    public async Task TheReceiver_RefusesADictionaryItDoesntHold_AndABadPayload_ThenTakesEachPlain()
    {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirs, Them, NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var peer = new LinePeer(ours);
        (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");

        var first = Encoding.UTF8.GetBytes(WpsPost);
        var firstId = dapps.client.DappsMessage.ComputeHash(first, 1L)[..7];
        await peer.WriteLineAsync($"ihave {firstId} len={first.Length} fmt=z9 clen=10 s=1 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"error {firstId}", "this build doesn't hold dictionary 9");
        await PushPlainAsync(peer, firstId, first, ct);

        var second = Encoding.UTF8.GetBytes(WpsPost.Replace("tonight", "this evening"));
        var secondId = dapps.client.DappsMessage.ComputeHash(second, 2L)[..7];
        await peer.WriteLineAsync($"ihave {secondId} len={second.Length} fmt=z1 clen=12 s=2 dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {secondId}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {secondId}\n"), .. "not zstd at"u8.ToArray(), (byte)'!'], ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"bad {secondId}");
        await PushPlainAsync(peer, secondId, second, ct, salt: 2);

        inbox.Texts.Should().Equal(WpsPost, WpsPost.Replace("tonight", "this evening"));
    }

    [Fact]
    public async Task TwoSessionsOfferingTheSameMessage_EachReadsItsOwnEncoding()
    {
        // The stored offer is keyed by id alone. One neighbour offers it
        // plain, the other compressed, and the plain one's payload
        // arrives last: it must be read as plain, not with the other
        // session's clen.
        var ct = TestContext.Current.CancellationToken;
        var inbox = new RecordingInbox();
        var payload = Encoding.UTF8.GetBytes(WpsPost);
        var id = dapps.client.DappsMessage.ComputeHash(payload, 1L)[..7];
        var wire = dapps.client.Compression.PayloadCompression.TryCompress(payload)!.Value;

        var (oursA, theirsA) = await LoopbackPairAsync(ct);
        var (oursB, theirsB) = await LoopbackPairAsync(ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirsA, "N0AAA", NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        _ = Task.Run(() => new InboundConnectionHandler(theirsB, "N0BBB", NullLoggerFactory.Instance, database, inbox).Handle(ct), ct);
        var a = new LinePeer(oursA);
        var b = new LinePeer(oursB);
        (await a.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
        (await b.ReadLineAsync(ct)).Should().Be("DAPPSv1>");

        await a.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p s=1 dst=app@{Us}", ct);
        (await a.ReadLineAsync(ct)).Should().Be($"send {id}");
        await b.WriteLineAsync($"ihave {id} len={payload.Length} fmt={wire.Format} clen={wire.Bytes.Length} s=1 dst=app@{Us}", ct);
        (await b.ReadLineAsync(ct)).Should().Be($"send {id}");

        await a.WriteAsync([.. Encoding.UTF8.GetBytes($"data {id}\n"), .. payload], ct);
        (await a.ReadLineAsync(ct)).Should().Be($"ack {id}");
    }

    private static async Task PushPlainAsync(LinePeer peer, string id, byte[] payload, CancellationToken ct, long salt = 1)
    {
        await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p s={salt} dst=app@{Us}", ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"send {id}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {id}\n"), .. payload], ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"ack {id}");
    }

    private static async Task<(Stream Ours, Stream Theirs)> LoopbackPairAsync(CancellationToken ct)
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

    /// <summary>The far end of a link, driven line by line.</summary>
    private sealed class LinePeer(Stream stream)
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
                if (n == 0) throw new EndOfStreamException("the handler closed the link");
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray());
                line.Add(one[0]);
            }
        }
    }

    private OutboundMessageManager MakeForwarder(IDappsOutboundTransport transport, bool compress, IBackhaulInbox? ourInbox)
    {
        var backhaul = new Dappsv1SessionBackhaul(
            transport, NullLoggerFactory.Instance,
            opportunisticInbox: ourInbox,
            opportunisticEnabled: () => ourInbox is not null,
            compressTo: (_, _) => Task.FromResult(compress));
        return new OutboundMessageManager(
            database, NullLoggerFactory.Instance, options, [backhaul],
            new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance),
            new DatabaseRoutingContext(database, options));
    }

    private static void Queue(string text, string destination, DateTime createdAt)
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
    /// A fresh loopback socket per connect with the real receiver on the
    /// far end, and a record of every byte our end wrote and read.
    /// </summary>
    private sealed class TappedLoopbackTransport(Database database, IBackhaulInbox farInbox, bool farEndCompresses) : IDappsOutboundTransport
    {
        private readonly MemoryStream written = new();
        private readonly MemoryStream read = new();

        public string Sent { get { lock (written) return Encoding.Latin1.GetString(written.ToArray()); } }
        public string Received { get { lock (read) return Encoding.Latin1.GetString(read.ToArray()); } }

        public async Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
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
                    compressTo: (_, _) => Task.FromResult(farEndCompresses));
                _ = Task.Run(() => handler.Handle(stoppingToken), stoppingToken);
                return new Connection(client, new TapStream(client.GetStream(), written, read));
            }
            finally
            {
                listener.Stop();
            }
        }

        private sealed class Connection(TcpClient client, Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class TapStream(Stream inner, MemoryStream written, MemoryStream read) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var n = await inner.ReadAsync(buffer, ct);
                lock (read) read.Write(buffer.Span[..n]);
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                lock (written) written.Write(buffer.Span);
                return inner.WriteAsync(buffer, ct);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
            public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
            public override void Flush() => inner.Flush();
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
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
