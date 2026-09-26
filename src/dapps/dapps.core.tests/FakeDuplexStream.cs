namespace dapps.core.tests;

/// <summary>
/// Test stream that has a pre-loaded readable side and a separate writable
/// side that captures everything the SUT writes. Useful for driving
/// protocol clients in isolation: pre-load the canned response, run the
/// SUT, inspect WriteCapture afterwards.
/// </summary>
internal sealed class FakeDuplexStream(byte[] preloadedReadable) : Stream
{
    private readonly MemoryStream readBuffer = new(preloadedReadable);

    public MemoryStream WriteCapture { get; } = new();

    /// <summary>Each write call's bytes, separately: what a bearer
    /// that frames per write (AGW) would turn into one frame each.</summary>
    public List<byte[]> Writes { get; } = [];

    public override int Read(byte[] buffer, int offset, int count)
        => readBuffer.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => readBuffer.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => readBuffer.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Writes.Add(buffer[offset..(offset + count)]);
        WriteCapture.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Writes.Add(buffer[offset..(offset + count)]);
        return WriteCapture.WriteAsync(buffer, offset, count, ct);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Writes.Add(buffer.ToArray());
        return WriteCapture.WriteAsync(buffer, ct);
    }

    public override void Flush() => WriteCapture.Flush();
    public override Task FlushAsync(CancellationToken ct) => WriteCapture.FlushAsync(ct);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
