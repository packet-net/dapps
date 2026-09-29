using System.Net;
using AwesomeAssertions;
using System.Net.Sockets;
using System.Text;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Pieces the DAPPSv1 session tests share: a loopback socket pair, the
/// far end of a link driven line by line, batches and inboxes that record
/// what happened.
/// </summary>
internal static class ExchangeTestKit
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A connected TCP loopback pair. Both ends stay open until
    /// <paramref name="ct"/> is cancelled, which closes them. A test mustn't
    /// leave either end to the garbage collector: a socket finalised without
    /// being closed goes with a reset, and the session on the other end then
    /// fails with "connection reset by peer" at whatever moment a collection
    /// runs. In a Release build a test's last use of its peer ends the
    /// peer's life there, so a busy runner's collection could land while the
    /// session was still waiting for its prompt (#202).
    /// </summary>
    public static async Task<(NetworkStream Ours, NetworkStream Theirs)> LoopbackPairAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient { NoDelay = true };
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            server.NoDelay = true;
            await connecting;
            var ours = client.GetStream();
            var theirs = server.GetStream();
            ct.Register(() =>
            {
                ours.Dispose();
                theirs.Dispose();
            });
            return (ours, theirs);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static BackhaulMessage Message(string text, string destination, long salt = 1, int? ttl = 600)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return new BackhaulMessage(DappsMessage.ComputeHash(payload, salt)[..7], destination, salt, ttl, payload);
    }

    /// <summary>A WPS replication post in the short-key form it sends
    /// today: compresses well with the dictionary.</summary>
    public const string WpsPost =
        """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""";

    /// <summary>The <c>msg</c> or <c>ihave</c> line for a message, as a
    /// peer would send it.</summary>
    public static string Line(string verb, BackhaulMessage m) => OfferLine.Build(verb, m, "p", null);
}

/// <summary>
/// A fake clock for sessions under test, moved on only by the test. It
/// counts the timers set on it: an exchange session sets one each time it
/// goes back to waiting for its peer, and cancels it when the peer's data
/// wakes it. So a test can move the clock once the session has read what
/// the peer sent and is waiting again, and a session's timers can't run
/// out early on a busy runner, or late (#202).
/// </summary>
internal sealed class WatchedClock : FakeTimeProvider
{
    private int armed;
    private int pending;

    /// <summary>Timers set so far.</summary>
    public int Armed => Volatile.Read(ref armed);

    /// <summary>One-shot timers set and not yet run out or cancelled: 0
    /// while a session is busy with what the peer sent, 1 while it waits.</summary>
    public int Pending => Volatile.Read(ref pending);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref armed);
        Interlocked.Increment(ref pending);
        var timer = new Watched(this);
        timer.Inner = base.CreateTimer(s =>
        {
            timer.Finished();
            callback(s);
        }, state, dueTime, period);
        return timer;
    }

    /// <summary>Until <paramref name="count"/> timers have been set in all.</summary>
    public Task WaitForArmedAsync(int count, CancellationToken ct) =>
        WaitForAsync(() => Armed >= count, () => $"only {Armed} of {count} timers were set", ct);

    /// <summary>Until a timer is waiting to run out.</summary>
    public Task WaitUntilPendingAsync(CancellationToken ct) =>
        WaitForAsync(() => Pending > 0, () => "no timer was set", ct);

    /// <summary>Until no timer is waiting: the session has taken what the peer sent.</summary>
    public Task WaitUntilNonePendingAsync(CancellationToken ct) =>
        WaitForAsync(() => Pending == 0, () => $"{Pending} timers are still waiting", ct);

    private static async Task WaitForAsync(Func<bool> condition, Func<string> failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ExchangeTestKit.Patience;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5, ct);
        if (!condition()) throw new TimeoutException(failure());
    }

    private sealed class Watched(WatchedClock clock) : ITimer
    {
        private int finished;

        public ITimer Inner { get; set; } = null!;

        public void Finished()
        {
            if (Interlocked.Exchange(ref finished, 1) == 0) Interlocked.Decrement(ref clock.pending);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => Inner.Change(dueTime, period);

        public void Dispose()
        {
            Finished();
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Finished();
            return Inner.DisposeAsync();
        }
    }
}

/// <summary>The far end of a link, driven line by line.</summary>
internal sealed class LinePeer(Stream stream)
{
    public Task WriteLineAsync(string line, CancellationToken ct) => WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

    public async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Hangs up: the other end sees the link close.</summary>
    public void Close() => stream.Dispose();

    /// <summary>A <c>msg</c> line and its payload, in one write.</summary>
    public Task SendMessageAsync(BackhaulMessage m, CancellationToken ct) =>
        WriteAsync([.. Encoding.UTF8.GetBytes(ExchangeTestKit.Line("msg", m)), .. m.Payload], ct);

    public async Task<string> ReadLineAsync(CancellationToken ct, TimeSpan? timeout = null) =>
        await TryReadLineAsync(timeout ?? ExchangeTestKit.Patience, ct)
        ?? throw new TimeoutException("no line from dapps in time");

    /// <summary>The next line, or null when nothing at all arrived within
    /// <paramref name="timeout"/>. A line cut off by the timeout is an
    /// error, not a null.</summary>
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
                if (n == 0) throw new EndOfStreamException("dapps closed the link" + (line.Count > 0 ? $" mid-line: '{Encoding.UTF8.GetString(line.ToArray())}'" : ""));
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

    public async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExchangeTestKit.Patience);
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    /// <summary>Reads the line that a <c>msg</c> or <c>data</c> starts
    /// with and the payload after it.</summary>
    public async Task<(string Line, byte[] Payload)> ReadWithPayloadAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        int length;
        if (line.StartsWith("msg ", StringComparison.Ordinal))
        {
            IHaveValidator.TryGetWireLength(line, out _, out length).Should().BeTrue(line);
        }
        else
        {
            throw new InvalidOperationException($"expected a msg line, got '{line}'");
        }
        return (line, await ReadBytesAsync(length, ct));
    }
}

/// <summary>
/// A fixed list of messages handed out one at a time, recording each
/// outcome. <see cref="SecondWave"/> stands in for traffic queued while
/// the session was open: handed out once the first list has run dry and
/// the session asks again.
/// </summary>
internal sealed class RecordingBatch(params BackhaulMessage[] messages) : IBackhaulBatch
{
    private readonly Queue<BackhaulMessage> queue = new(messages);
    private readonly List<(string Id, BackhaulSendResult Result)> outcomes = [];
    private bool ranDry;

    public List<BackhaulMessage> SecondWave { get; init; } = [];

    public IReadOnlyList<(string Id, BackhaulSendResult Result)> Outcomes
    {
        get { lock (outcomes) return [.. outcomes]; }
    }

    public int Remaining => queue.Count;

    public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
    {
        if (queue.TryDequeue(out var next)) return ValueTask.FromResult<BackhaulMessage?>(next);
        if (ranDry && SecondWave.Count > 0)
        {
            next = SecondWave[0];
            SecondWave.RemoveAt(0);
            return ValueTask.FromResult<BackhaulMessage?>(next);
        }
        ranDry = true;
        return ValueTask.FromResult<BackhaulMessage?>(null);
    }

    public ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
    {
        lock (outcomes) outcomes.Add((message.Id, result));
        return ValueTask.CompletedTask;
    }

    public async Task WaitForOutcomesAsync(int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ExchangeTestKit.Patience;
        while (Outcomes.Count < count && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        if (Outcomes.Count < count) throw new TimeoutException($"only {Outcomes.Count} of {count} outcomes arrived");
    }
}

/// <summary>An inbox that records what it's given, and can claim to
/// hold some messages already.</summary>
internal sealed class RecordingInbox : IBackhaulInbox
{
    private readonly List<(BackhaulMessage Message, string Source)> delivered = [];

    public HashSet<string> AlreadyHeld { get; } = [];
    public Action<BackhaulMessage>? OnDelivered { get; init; }

    public IReadOnlyList<BackhaulMessage> Messages
    {
        get { lock (delivered) return [.. delivered.Select(d => d.Message)]; }
    }

    public IReadOnlyList<string> Texts => [.. Messages.Select(m => Encoding.UTF8.GetString(m.Payload))];

    public IReadOnlyList<string> Sources
    {
        get { lock (delivered) return [.. delivered.Select(d => d.Source)]; }
    }

    public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
    {
        lock (delivered) delivered.Add((message, sourceCallsign));
        OnDelivered?.Invoke(message);
        return Task.CompletedTask;
    }

    public Task<bool> HasAsync(string id, long? salt, int length, CancellationToken ct) => Task.FromResult(AlreadyHeld.Contains(id));

    public async Task WaitForAsync(int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ExchangeTestKit.Patience;
        while (Messages.Count < count && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
    }
}

/// <summary>Records each write to the inner stream separately: what a
/// bearer that frames per write (AGW) would send as one frame each.</summary>
internal sealed class WriteRecordingStream(Stream inner) : Stream
{
    private readonly List<byte[]> writes = [];

    public IReadOnlyList<string> Writes
    {
        get { lock (writes) return [.. writes.Select(w => Encoding.Latin1.GetString(w))]; }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        lock (writes) writes.Add(buffer.ToArray());
        return inner.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A transport that hands out one already-connected stream.</summary>
internal sealed class OneStreamTransport(Stream stream) : IDappsOutboundTransport
{
    public int Connects { get; private set; }

    public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
    {
        Connects++;
        return Task.FromResult<IDappsConnection>(new Connection(stream));
    }

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
