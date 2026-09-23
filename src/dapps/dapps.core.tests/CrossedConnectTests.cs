using System.Net;
using System.Net.Sockets;
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
/// #178, the crossed-connect case: two neighbours dial each other within
/// one round trip, both links come up, and each node's BPQ attaches its
/// link to its own outbound session. Neither side is handed an inbound
/// connect, so neither sends <c>DAPPSv1&gt;</c>, and from each caller's
/// point of view the link is simply silent. The lower callsign breaks
/// the tie after <see cref="Dappsv1SessionBackhaul.GlareSilenceBudget"/>
/// by sending the prompt itself and serving the session; the higher
/// callsign keeps waiting and then carries on as the caller.
///
/// The peer here is the far end of a loopback socket, driven line by
/// line, so the served session is the real
/// <see cref="InboundConnectionHandler"/> over the real stream the
/// backhaul was handed - not a stand-in.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class CrossedConnectTests : IAsyncLifetime
{
    private const string Lower = "G5ALF-3";
    private const string Higher = "M0AHN-3";
    private static readonly TimeSpan SilenceBudget = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-crossed-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
        }
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Lower });
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
    public async Task LowerCallsign_AfterTheSilenceBudget_SendsThePromptAndServesTheCallersPush()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var inbox = new CapturingInbox();
        var backhaul = new Dappsv1SessionBackhaul(
            new OneShotTransport(ours), NullLoggerFactory.Instance,
            opportunisticInbox: null, opportunisticEnabled: null, routeGossip: null,
            serveOnGlare: (stream, peer, c) =>
                new InboundConnectionHandler(stream, peer, NullLoggerFactory.Instance, database, inbox).Handle(c))
        { GlareSilenceBudget = SilenceBudget };
        var peer = new LinePeer(theirs);

        var send = backhaul.SendAsync(OurMessage("ours001", Higher), new BackhaulRoute(Higher, BearerPort: 1), Lower, ct);

        // The peer dialled too, so it is waiting for a prompt as well and
        // says nothing. We give it the whole silence budget before acting.
        (await peer.TryReadLineAsync(SilenceBudget / 2, ct)).Should().BeNull("nothing is sent before the silence budget has elapsed");
        (await peer.ReadLineAsync(Patience, ct)).Should().Be("DAPPSv1>");

        // On seeing the prompt the peer does what any caller does.
        var payload = "hi"u8.ToArray();
        var id = DappsMessage.ComputeHash(payload, 7L)[..7];
        await peer.WriteLineAsync($"ihave {id} len={payload.Length} fmt=p dst=app@{Lower} s=7", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().Be($"send {id}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {id}\n"), .. payload], ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().Be($"ack {id}");
        await peer.WriteLineAsync("quit", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().Be("bye");

        var result = await send.WaitAsync(Patience, ct);
        result.Deferred.Should().BeTrue();
        result.Accepted.Should().BeFalse("our own message was not pushed on this session");
        var delivered = await inbox.WaitForFirstAsync(Patience, ct);
        delivered.SourceCallsign.Should().Be(Higher);
        delivered.Message.Id.Should().Be(id);
    }

    [Fact]
    public async Task HigherCallsign_KeepsWaiting_AndPushesWhenTheLatePromptArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var served = false;
        var backhaul = new Dappsv1SessionBackhaul(
            new OneShotTransport(ours), NullLoggerFactory.Instance,
            opportunisticInbox: null, opportunisticEnabled: null, routeGossip: null,
            serveOnGlare: (_, _, _) => { served = true; return Task.CompletedTask; })
        { GlareSilenceBudget = SilenceBudget };
        var peer = new LinePeer(theirs);
        var message = OurMessage("ours002", Lower);

        var send = backhaul.SendAsync(message, new BackhaulRoute(Lower, BearerPort: 1), Higher, ct);

        // Well past the silence budget and still nothing from us: the tie
        // is the other side's to break.
        (await peer.TryReadLineAsync(SilenceBudget * 3, ct)).Should().BeNull();
        served.Should().BeFalse();

        // The lower side's late prompt arrives and the push goes ahead.
        await peer.WriteLineAsync("DAPPSv1>", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().StartWith($"ihave {message.Id} ");
        await peer.WriteLineAsync($"send {message.Id}", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().Be($"data {message.Id}");
        await peer.ReadExactlyAsync(message.Payload.Length, ct);
        await peer.WriteLineAsync($"ack {message.Id}", ct);

        var result = await send.WaitAsync(Patience, ct);
        result.Accepted.Should().BeTrue();
        result.Deferred.Should().BeFalse();
        served.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutTheServeSeam_TheLowerCallsignWaitsAsItAlwaysDid()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var backhaul = new Dappsv1SessionBackhaul(new OneShotTransport(ours), NullLoggerFactory.Instance)
        { GlareSilenceBudget = SilenceBudget };
        var peer = new LinePeer(theirs);
        var message = OurMessage("ours003", Higher);

        var send = backhaul.SendAsync(message, new BackhaulRoute(Higher, BearerPort: 1), Lower, ct);

        (await peer.TryReadLineAsync(SilenceBudget * 3, ct)).Should().BeNull("with nothing to serve the session with, silence is just waited out");
        await peer.WriteLineAsync("DAPPSv1>", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().StartWith($"ihave {message.Id} ");
        await peer.WriteLineAsync($"send {message.Id}", ct);
        (await peer.ReadLineAsync(Patience, ct)).Should().Be($"data {message.Id}");
        await peer.ReadExactlyAsync(message.Payload.Length, ct);
        await peer.WriteLineAsync($"ack {message.Id}", ct);

        (await send.WaitAsync(Patience, ct)).Accepted.Should().BeTrue();
    }

    private static BackhaulMessage OurMessage(string id, string peer) =>
        new(id, $"app@{peer}", Salt: 1L, Ttl: 60, Payload: "hello"u8.ToArray());

    /// <summary>A connected TCP loopback pair: one end for the backhaul, one for the peer.</summary>
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

    private sealed class OneShotTransport(Stream stream) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            => Task.FromResult<IDappsConnection>(new Connection(stream));

        private sealed class Connection(Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>The far end of the link, driven line by line.</summary>
    private sealed class LinePeer(Stream stream)
    {
        public Task WriteLineAsync(string line, CancellationToken ct) => WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

        public async Task WriteAsync(byte[] bytes, CancellationToken ct)
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken ct) =>
            await TryReadLineAsync(timeout, ct) ?? throw new TimeoutException($"no line from dapps within {timeout.TotalSeconds:F1}s");

        /// <summary>The next line, or null when nothing at all arrived
        /// within <paramref name="timeout"/>. A line cut off by the
        /// timeout is an error, not a null: no test here expects one.</summary>
        public async Task<string?> TryReadLineAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var line = new List<byte>();
            var one = new byte[1];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(one, cts.Token);
                    if (n == 0) throw new EndOfStreamException("dapps closed the link");
                    if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray());
                    line.Add(one[0]);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (line.Count > 0)
                {
                    throw new TimeoutException($"partial line from dapps when the timeout hit: '{Encoding.UTF8.GetString(line.ToArray())}'");
                }
                return null;
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, ct);
            return buffer;
        }
    }

    private sealed record Delivered(BackhaulMessage Message, string SourceCallsign);

    private sealed class CapturingInbox : IBackhaulInbox
    {
        private readonly TaskCompletionSource<Delivered> first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            first.TrySetResult(new Delivered(message, sourceCallsign));
            return Task.CompletedTask;
        }

        public Task<Delivered> WaitForFirstAsync(TimeSpan timeout, CancellationToken ct) => first.Task.WaitAsync(timeout, ct);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
