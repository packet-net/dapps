using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// Presents an AGW connected session as a duplex .NET Stream:
///   - WriteAsync wraps the bytes into a 'D' frame and sends it.
///   - ReadAsync pulls 'D' frames from the wire and returns their payloads
///     to the caller, buffering any leftover when the caller's buffer is
///     smaller than the frame.
/// A 'd' (lowercase, remote-disconnected) frame is treated as EOF.
/// </summary>
internal sealed class AgwSessionStream(
    AgwFrameTransport framing,
    byte port,
    string callfrom,
    string callto,
    ILogger logger) : Stream
{
    private byte[] readBuffer = [];
    private int readPos;
    private bool disconnected;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (disconnected) return 0;

        while (readPos >= readBuffer.Length)
        {
            var frame = await framing.ReadFrameAsync(ct);
            switch (frame.Kind)
            {
                case 'D':
                    readBuffer = frame.Payload;
                    readPos = 0;
                    break;
                case 'd':
                    disconnected = true;
                    logger.LogInformation("AGW: remote disconnected ({0}↔{1})", frame.CallFrom, frame.CallTo);
                    return 0;
                default:
                    logger.LogDebug("AGW: ignoring frame kind '{0}' on session ({1}↔{2})", frame.Kind, callfrom, callto);
                    break;
            }
        }

        var available = readBuffer.Length - readPos;
        var toCopy = Math.Min(buffer.Length, available);
        readBuffer.AsSpan(readPos, toCopy).CopyTo(buffer.Span);
        readPos += toCopy;
        return toCopy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(new Memory<byte>(buffer, offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (disconnected) throw new IOException("AGW session disconnected");
        if (buffer.IsEmpty) return;

        await framing.WriteDataAsync(port, callfrom, callto, buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }

    public override bool CanRead => !disconnected;
    public override bool CanWrite => !disconnected;
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
