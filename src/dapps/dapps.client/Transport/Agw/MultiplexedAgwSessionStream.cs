using System.Buffers;
using System.IO.Pipelines;

namespace dapps.client.Transport.Agw;

/// <summary>
/// Stream view of one AGW connected session when many sessions share a
/// single AGW socket (the inbound case - the AGW frame stream is read
/// by a single dispatcher loop, which routes 'D' / 'd' frames to the
/// matching session by callfrom/callto).
///
/// Reads pull bytes from an internal pipe fed by the dispatcher via
/// <see cref="PushIncoming"/>; writes serialise bytes into a 'D' frame
/// and hand them to the supplied write callback so the dispatcher's
/// shared <see cref="AgwFrameTransport"/> stays the only place frames
/// hit the wire. <see cref="SignalRemoteDisconnect"/> closes the pipe
/// from the writer side; subsequent reads return 0 (EOF).
///
/// Contrast <see cref="AgwSessionStream"/>, which assumes one session
/// per AGW socket and pulls frames directly. That's correct for the
/// outbound code path; this class is for the inbound code path where
/// many sessions multiplex.
/// </summary>
public sealed class MultiplexedAgwSessionStream : Stream
{
    private const int Open = 0;
    private const int RemotelyClosed = 1;
    private const int Disposed = 2;

    private readonly Pipe incoming = new();
    private readonly Func<byte[], CancellationToken, Task> writeOutgoing;
    private readonly Func<CancellationToken, Task> sendRemoteDisconnect;
    private int closeState = Open;

    private bool IsDisposed => Volatile.Read(ref closeState) == Disposed;

    public MultiplexedAgwSessionStream(
        Func<byte[], CancellationToken, Task> writeOutgoing,
        Func<CancellationToken, Task> sendRemoteDisconnect)
    {
        this.writeOutgoing = writeOutgoing;
        this.sendRemoteDisconnect = sendRemoteDisconnect;
    }

    /// <summary>Push received bytes (typically a 'D' frame's payload)
    /// onto the read side of the stream. Called by the dispatcher.</summary>
    public async ValueTask PushIncoming(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (IsDisposed || data.IsEmpty) return;
        await incoming.Writer.WriteAsync(data, ct);
    }

    /// <summary>Mark the stream as EOF on the read side. Idempotent.
    /// Called by the dispatcher when a 'd' frame for this session
    /// arrives, or when the AGW socket itself drops.</summary>
    public void SignalRemoteDisconnect()
    {
        if (Interlocked.CompareExchange(ref closeState, RemotelyClosed, Open) == Open)
            CompleteIncoming();
    }

    private void CompleteIncoming()
    {
        try { incoming.Writer.Complete(); } catch { /* already completed */ }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;

        var result = await incoming.Reader.ReadAsync(ct);
        if (result.IsCanceled) return 0;
        if (result.Buffer.IsEmpty && result.IsCompleted) return 0;

        var available = result.Buffer;
        var toCopy = (int)Math.Min(available.Length, buffer.Length);
        available.Slice(0, toCopy).CopyTo(buffer.Span);
        incoming.Reader.AdvanceTo(available.GetPosition(toCopy));
        return toCopy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(new Memory<byte>(buffer, offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return;
        if (IsDisposed) throw new IOException("session disposed");
        await writeOutgoing(buffer.ToArray(), ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }

    public override async ValueTask DisposeAsync()
    {
        var previousState = Interlocked.Exchange(ref closeState, Disposed);
        if (previousState == Disposed) return;

        CompleteIncoming();
        // Only the open-to-disposed winner may send 'd'; AGW reuses callsign pairs.
        if (previousState == Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await sendRemoteDisconnect(cts.Token);
            }
            catch
            {
                // Best-effort: AGW socket may already be gone, the session
                // may already be torn down on the BPQ side. Don't throw out
                // of Dispose.
            }
        }
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { _ = DisposeAsync().AsTask(); }
        base.Dispose(disposing);
    }

    public override bool CanRead => !IsDisposed;
    public override bool CanWrite => !IsDisposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
