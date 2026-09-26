using System.Threading.Channels;

namespace dapps.client.Transport;

/// <summary>
/// Wraps a connection's stream so that a session can wait for the peer
/// to say something without committing to a read. A background pump
/// reads the inner stream into a queue; <see cref="WaitForDataAsync"/>
/// waits until the queue has bytes (or the stream has ended) and can be
/// cancelled at any time without losing anything, where cancelling a
/// read on the inner stream could drop half a line.
///
/// That is what a held session needs (#187 proposal 9): while the link
/// is idle it waits for whichever comes first of the peer's
/// <c>pending</c>, new work from the forwarder, or the end of the tail.
///
/// Writes, flushes and disposal of the inner stream belong to its
/// owner; this only takes over reading.
/// </summary>
public sealed class PumpedReadStream : Stream
{
    private readonly Stream inner;
    private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stop = new();
    private byte[] current = [];
    private int offset;

    public PumpedReadStream(Stream inner)
    {
        this.inner = inner;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[1024];
        var token = stop.Token;
        try
        {
            while (true)
            {
                var n = await inner.ReadAsync(buffer, token);
                if (n == 0) break;
                chunks.Writer.TryWrite(buffer[..n]);
            }
            chunks.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            chunks.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Readers see it as the stream failing, the same as they would
            // have reading the inner stream directly.
            chunks.Writer.TryComplete(new IOException("link read failed: " + ex.Message, ex));
        }
    }

    /// <summary>
    /// True once there are bytes to read, false once the stream has
    /// ended with nothing left. Consumes nothing.
    /// </summary>
    public async ValueTask<bool> WaitForDataAsync(CancellationToken ct)
    {
        if (offset < current.Length) return true;
        return await chunks.Reader.WaitToReadAsync(ct);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        if (offset >= current.Length)
        {
            if (!await chunks.Reader.WaitToReadAsync(ct)) return 0;
            if (!chunks.Reader.TryRead(out var next)) return 0;
            current = next;
            offset = 0;
        }
        var n = Math.Min(buffer.Length, current.Length - offset);
        current.AsMemory(offset, n).CopyTo(buffer);
        offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.WriteAsync(buffer, offset, count, ct);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stop.Cancel();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
