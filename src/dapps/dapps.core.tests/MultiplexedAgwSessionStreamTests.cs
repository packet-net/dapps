using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for the multiplexed AGW session stream - the inbound-side
/// glue that lets <c>InboundConnectionHandler</c> consume bytes from a
/// shared AGW socket as if it had a private duplex stream.
/// </summary>
public class MultiplexedAgwSessionStreamTests
{
    [Fact]
    public async Task PushedBytes_AreReadableInOrder()
    {
        var sent = new List<byte[]>();
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (data, _) => { sent.Add(data); return Task.CompletedTask; },
            sendRemoteDisconnect: _ => Task.CompletedTask);

        stream.TryPush("hello "u8).Should().Be(PushResult.Accepted);
        stream.TryPush("world\n"u8).Should().Be(PushResult.Accepted);

        var buf = new byte[64];
        var n = await stream.ReadAsync(buf.AsMemory(), TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("hello world\n");
    }

    [Fact]
    public async Task Read_BlocksUntilPushArrives_ThenReturns()
    {
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => Task.CompletedTask);

        var ct = TestContext.Current.CancellationToken;
        var buf = new byte[16];
        var read = stream.ReadAsync(buf.AsMemory(), ct).AsTask();
        read.IsCompleted.Should().BeFalse("no bytes pushed yet");

        stream.TryPush("abc"u8).Should().Be(PushResult.Accepted);
        var n = await read.WaitAsync(TimeSpan.FromSeconds(1), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("abc");
    }

    [Fact]
    public async Task SignalRemoteDisconnect_DrainsBufferThenReturnsZero()
    {
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => Task.CompletedTask);

        var ct = TestContext.Current.CancellationToken;
        stream.TryPush("trailing"u8).Should().Be(PushResult.Accepted);
        stream.SignalRemoteDisconnect();

        var buf = new byte[16];
        var n = await stream.ReadAsync(buf.AsMemory(), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("trailing",
            "bytes pushed before disconnect must still be readable");

        var n2 = await stream.ReadAsync(buf.AsMemory(), ct);
        n2.Should().Be(0, "subsequent reads see EOF after the buffer drains");
    }

    [Fact]
    public async Task Write_InvokesCallbackWithExactBytes()
    {
        var sent = new List<byte[]>();
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (data, _) => { sent.Add(data); return Task.CompletedTask; },
            sendRemoteDisconnect: _ => Task.CompletedTask);

        await stream.WriteAsync("DAPPSv1>\n"u8.ToArray().AsMemory(),
            TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        sent.Should().HaveCount(1);
        Encoding.UTF8.GetString(sent[0]).Should().Be("DAPPSv1>\n");
    }

    [Fact]
    public async Task DisposeAsync_FiresRemoteDisconnectCallback()
    {
        var disconnectFired = false;
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => { disconnectFired = true; return Task.CompletedTask; });

        await stream.DisposeAsync();
        disconnectFired.Should().BeTrue(
            "disposing the stream should signal a 'd' frame so BPQ tears down the L2 link rather than leaving it half-up");
    }

    [Fact]
    public async Task DisposeAsync_AfterRemoteDisconnect_DoesNotSendDisconnect()
    {
        var disconnectCount = 0;
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => { Interlocked.Increment(ref disconnectCount); return Task.CompletedTask; });
        var ct = TestContext.Current.CancellationToken;
        var read = stream.ReadAsync(new byte[16].AsMemory(), ct).AsTask();
        read.IsCompleted.Should().BeFalse();

        stream.SignalRemoteDisconnect();
        stream.SignalRemoteDisconnect();
        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await stream.DisposeAsync(), ct)))
            .WaitAsync(TimeSpan.FromSeconds(5), ct);

        (await read.WaitAsync(TimeSpan.FromSeconds(5), ct)).Should().Be(0);
        disconnectCount.Should().Be(0,
            "the peer already hung up, so BPQ has released the session; a late 'd' is addressed by " +
            "callsign pair only and would tear down a new session that reused the same pair");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhileDisconnectPending_ClosesStreamAndSendsOnlyOnce(bool remoteCloses)
    {
        var disconnectCount = 0;
        var writeCount = 0;
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => { Interlocked.Increment(ref writeCount); return Task.CompletedTask; },
            sendRemoteDisconnect: _ =>
            {
                Interlocked.Increment(ref disconnectCount);
                return releaseDisconnect.Task;
            });
        var ct = TestContext.Current.CancellationToken;
        var read = stream.ReadAsync(new byte[16].AsMemory(), ct).AsTask();
        var disposal = stream.DisposeAsync().AsTask();

        try
        {
            disposal.IsCompleted.Should().BeFalse();
            disconnectCount.Should().Be(1);
            if (remoteCloses) stream.SignalRemoteDisconnect();

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                stream.Dispose();
                await stream.DisposeAsync();
            }, ct))).WaitAsync(TimeSpan.FromSeconds(5), ct);

            (await read.WaitAsync(TimeSpan.FromSeconds(5), ct)).Should().Be(0);
            stream.CanRead.Should().BeFalse();
            stream.CanWrite.Should().BeFalse();
            await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync("late"u8.ToArray().AsMemory(), ct).AsTask());
            writeCount.Should().Be(0);
            disconnectCount.Should().Be(1);
        }
        finally
        {
            releaseDisconnect.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
    }

    [Fact]
    public async Task DisposeAsync_ReenteredFromDisconnectCallback_SendsOnlyOnce()
    {
        var disconnectCount = 0;
        MultiplexedAgwSessionStream? stream = null;
        stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: async _ =>
            {
                Interlocked.Increment(ref disconnectCount);
                stream!.SignalRemoteDisconnect();
                stream.Dispose();
                await stream.DisposeAsync();
            });

        await stream.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        disconnectCount.Should().Be(1);
        stream.CanRead.Should().BeFalse();
        stream.CanWrite.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentRemoteDisconnectAndDispose_SendAtMostOneDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 100; i++)
        {
            var disconnectCount = 0;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new MultiplexedAgwSessionStream(
                writeOutgoing: (_, _) => Task.CompletedTask,
                sendRemoteDisconnect: _ => { Interlocked.Increment(ref disconnectCount); return Task.CompletedTask; });
            var closers = Enumerable.Range(0, 8).Select(async n =>
            {
                await start.Task;
                if (n % 2 == 0) stream.SignalRemoteDisconnect();
                else await stream.DisposeAsync();
            }).ToArray();

            start.SetResult();
            await Task.WhenAll(closers).WaitAsync(TimeSpan.FromSeconds(5), ct);

            disconnectCount.Should().BeInRange(0, 1);
            stream.CanRead.Should().BeFalse();
            stream.CanWrite.Should().BeFalse();
            (await stream.ReadAsync(new byte[16].AsMemory(), ct).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), ct)).Should().Be(0);
        }
    }

    // #276: pushes never throw, never wait, and never lose or reorder bytes.

    private static MultiplexedAgwSessionStream NewStream(Action? onDisconnect = null) => new(
        writeOutgoing: (_, _) => Task.CompletedTask,
        sendRemoteDisconnect: _ => { onDisconnect?.Invoke(); return Task.CompletedTask; });

    [Fact]
    public async Task PushAfterRemoteDisconnect_IsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = NewStream();
        stream.SignalRemoteDisconnect();

        stream.TryPush("late"u8).Should().Be(PushResult.NotOpen);
        stream.TryPush("later"u8).Should().Be(PushResult.NotOpen);

        (await stream.ReadAsync(new byte[16].AsMemory(), ct)).Should().Be(0, "nothing pushed after the close is readable");
    }

    [Fact]
    public async Task PushAfterDispose_IsANoOp()
    {
        var stream = NewStream();
        await stream.DisposeAsync();

        stream.TryPush("late"u8).Should().Be(PushResult.NotOpen);
        stream.TryPush("later"u8).Should().Be(PushResult.NotOpen);
    }

    [Fact]
    public async Task OverlappingPushes_PastThePipesOldPauseThreshold_KeepOrderAndLoseNothing()
    {
        // What the RHP and AGW readers do: push and move on. A push used
        // to park once 64 KB sat unread, and the next one then overlapped
        // it and corrupted the pipe.
        var ct = TestContext.Current.CancellationToken;
        var stream = NewStream();
        var big = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        var small = "0123456789"u8.ToArray();

        stream.TryPush(big).Should().Be(PushResult.Accepted);
        stream.TryPush(small).Should().Be(PushResult.Accepted);

        var got = new MemoryStream();
        var buf = new byte[4096];
        while (got.Length < big.Length + small.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(), ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            n.Should().BePositive();
            got.Write(buf, 0, n);
        }
        got.ToArray().Should().Equal([.. big, .. small]);
    }

    [Fact]
    public async Task ConcurrentPushes_EachLandWholeAndInOrderPerPusher()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = NewStream();
        const int pushers = 8;
        const int chunks = 200;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, pushers).Select(p => Task.Run(async () =>
        {
            await start.Task;
            for (var c = 0; c < chunks; c++)
            {
                // 4 bytes: pusher, chunk high, chunk low, end marker.
                stream.TryPush([(byte)p, (byte)(c >> 8), (byte)c, 0xFF]).Should().Be(PushResult.Accepted);
            }
        }, ct)).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), ct);

        var all = new byte[pushers * chunks * 4];
        await stream.ReadExactlyAsync(all, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        var next = new int[pushers];
        for (var i = 0; i < all.Length; i += 4)
        {
            all[i + 3].Should().Be(0xFF, "a push lands whole, never interleaved with another");
            var p = all[i];
            ((all[i + 1] << 8) | all[i + 2]).Should().Be(next[p]++, "each pusher's bytes arrive in the order it pushed them");
        }
        next.Should().AllSatisfy(n => n.Should().Be(chunks));
    }

    [Fact]
    public async Task ASessionThatLeavesTooMuchUnread_IsClosedLocally_AndItsTeardownSendsTheDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var disconnects = 0;
        var stream = NewStream(() => Interlocked.Increment(ref disconnects));

        stream.TryPush(new byte[MultiplexedAgwSessionStream.MaxUnreadBytes]).Should().Be(PushResult.Accepted);
        stream.TryPush([1]).Should().Be(PushResult.Overflowed);
        stream.TryPush([2]).Should().Be(PushResult.NotOpen, "the session is closed from then on");

        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[16].AsMemory(), ct).AsTask());
        await stream.DisposeAsync();
        disconnects.Should().Be(1, "the link is still up at the node, and still ours to close");
    }

    [Fact]
    public async Task BytesRead_NoLongerCountAgainstTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = NewStream();
        var chunk = new byte[MultiplexedAgwSessionStream.MaxUnreadBytes * 3 / 4];

        for (var round = 0; round < 4; round++)
        {
            stream.TryPush(chunk).Should().Be(PushResult.Accepted);
            await stream.ReadExactlyAsync(new byte[chunk.Length], ct);
        }
    }
}
