using AwesomeAssertions;
using dapps.client;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Plan A3: a hung peer must not wedge a forwarder run forever. The
/// sender-side <see cref="DappsProtocolClient"/> applies a per-read
/// inactivity timeout (3 minutes by default) on every byte it reads
/// off the wire; if no data arrives in that window the read surfaces
/// as <see cref="TimeoutException"/>. Tests drive the client against
/// a stream that yields zero bytes per read but never returns 0
/// (i.e. doesn't EOF) - exactly the shape of a TCP socket whose
/// peer has gone silent. With the timeout dialed down the test
/// completes in milliseconds.
///
/// That dialled-down budget is a process-wide static, so this class
/// must not run alongside tests that genuinely wait on a protocol read
/// (<see cref="CrossedConnectTests"/> waits out a silence longer than
/// 100 ms on purpose). Sharing the SQLite collection is the simplest
/// way to keep them apart; nothing here touches the database.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DappsProtocolClientTimeoutTests : IDisposable
{
    private readonly TimeSpan _originalTimeout;

    public DappsProtocolClientTimeoutTests()
    {
        _originalTimeout = DappsProtocolClient.InactivityTimeout;
        DappsProtocolClient.InactivityTimeout = TimeSpan.FromMilliseconds(100);
    }

    public void Dispose()
    {
        DappsProtocolClient.InactivityTimeout = _originalTimeout;
    }

    [Fact]
    public async Task ReadInitialPromptAsync_PeerSilent_ThrowsTimeout()
    {
        var stream = new HangingStream();
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var act = async () => await client.ReadInitialPromptAsync(CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .Where(ex => ex.Message.Contains("DAPPS sender"));
    }

    [Fact]
    public async Task OfferMessageAsync_PeerSilentAfterAcceptingLine_ThrowsTimeout()
    {
        // Stream serves nothing on read; OfferMessageAsync writes the
        // ihave line then blocks waiting for `send <id>` and times out.
        var stream = new HangingStream();
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var act = async () => await client.OfferMessageAsync(
            id: "abcdeff", salt: null, format: DappsMessage.MessageFormat.Plain,
            destination: "x@y", length: 5, ct: CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task SendMessageAsync_PeerSilentAfterPayload_ThrowsTimeout()
    {
        var stream = new HangingStream();
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var act = async () => await client.SendMessageAsync("abc", [1, 2, 3], CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task OuterCancellation_TakesPrecedenceOverInactivityTimeout()
    {
        // If the caller cancels before the inactivity timer fires, we
        // surface the cancellation rather than swapping it for
        // TimeoutException - callers (forwarder loop) need to see
        // shutdown vs. timeout differently.
        var stream = new HangingStream();
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);
        using var outer = new CancellationTokenSource();

        var task = client.ReadInitialPromptAsync(outer.Token);
        outer.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    /// <summary>
    /// Stream that accepts writes but blocks indefinitely on reads
    /// until cancellation. Models a TCP socket whose peer has stopped
    /// sending - the kind of hang A3 protects against.
    /// </summary>
    private sealed class HangingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0; // unreachable
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(new Memory<byte>(buffer, offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) { /* swallow */ }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
