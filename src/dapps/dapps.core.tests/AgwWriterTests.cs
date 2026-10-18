using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;

namespace dapps.core.tests;

/// <summary>
/// The one writer on an AGW connection (<see cref="AgwFrameTransport.StartWriter"/>):
/// every session, call, beacon and keepalive shares it since #272, so a
/// node that stops reading must end the link rather than hold them all,
/// and nothing about what goes on the wire may change: order, whole
/// frames, the 256-byte split and the TX gate.
/// </summary>
public sealed class AgwWriterTests
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";

    [Fact]
    public async Task AWriteTheNodeDoesNotTake_FailsAfterThirtySeconds_AndSoDoesEveryWriteAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var wire = new HeldStream();
        var framing = new AgwFrameTransport(wire);
        framing.StartWriter(clock);
        wire.Hold();

        var stuck = framing.WriteFrameAsync(G, ct);
        await wire.WriteEnteredAsync(ct);
        var behind = framing.WriteFrameAsync(Data("behind"), ct);
        await clock.WaitForTimerAsync(AgwFrameTransport.WriteStallLimit, ct);

        clock.Advance(AgwFrameTransport.WriteStallLimit - TimeSpan.FromSeconds(1));
        await Task.Delay(100, ct);
        stuck.IsCompleted.Should().BeFalse("not yet 30 s");
        framing.WriteFailed.IsCompleted.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await FluentActions.Awaiting(() => stuck.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().ThrowAsync<IOException>();
        failed.Which.Message.Should().Contain("not reading");
        await FluentActions.Awaiting(() => behind.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().ThrowAsync<IOException>();
        (await framing.WriteFailed.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Message.Should().Contain("not reading");
        await FluentActions.Awaiting(() => framing.WriteFrameAsync(G, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<IOException>("nothing more goes on a connection the node has stopped reading");
        await framing.StopWriterAsync();
    }

    [Fact]
    public async Task WritesGoInTheOrderMade_AndAMessagesFramesGoTogether_EachWithin256Bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = new HeldStream();
        var framing = new AgwFrameTransport(wire);
        framing.StartWriter();
        wire.Hold();

        var first = framing.WriteFrameAsync(G, ct);
        await wire.WriteEnteredAsync(ct);
        var message = framing.WriteDataAsync(1, Local, Remote, Bytes(600, 'a'), ct);
        var keepalive = framing.WriteFrameAsync(G, ct);
        var beacon = framing.WriteFrameAsync(new AgwFrame(1, 'M', 0xF0, Local, "DAPPS", Bytes(40, 'b')), ct);
        wire.Release();
        await Task.WhenAll(first, message, keepalive, beacon).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        var frames = await wire.FramesAsync(ct);
        frames.Select(f => f.Kind).Should().Equal(['G', 'D', 'D', 'D', 'G', 'M'],
            "in the order made, and nothing between one message's frames");
        frames.Where(f => f.Kind == 'D').Select(f => f.Payload.Length).Should().Equal(256, 256, 88);
        frames.Where(f => f.Kind == 'D').SelectMany(f => f.Payload).Should().Equal(Bytes(600, 'a'));
        await framing.StopWriterAsync();
    }

    [Fact]
    public async Task TheTxGate_StillStopsEveryFrameOfAMessage_AndLetsNodeControlThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = new HeldStream();
        var gate = new SwitchableGate { TxAllowed = false };
        var framing = new AgwFrameTransport(wire, gate);
        framing.StartWriter();

        await FluentActions.Awaiting(() => framing.WriteDataAsync(1, Local, Remote, Bytes(600, 'a'), ct))
            .Should().ThrowAsync<TxStoppedException>();
        await FluentActions.Awaiting(() => framing.WriteFrameAsync(new AgwFrame(1, 'C', 0xF0, Local, Remote, []), ct))
            .Should().ThrowAsync<TxStoppedException>();
        await framing.WriteFrameAsync(G, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await framing.WriteFrameAsync(new AgwFrame(1, 'd', 0, Local, Remote, []), ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        (await wire.FramesAsync(ct)).Select(f => f.Kind).Should().Equal(['G', 'd'], "no RF while the gate is shut");
        await framing.StopWriterAsync();
    }

    [Fact]
    public async Task ACallerCancellingWhileItsWriteWaits_TakesItBack_ButAWriteAlreadyGoingGoesWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = new HeldStream();
        var framing = new AgwFrameTransport(wire);
        framing.StartWriter();
        wire.Hold();

        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();
        var going = framing.WriteDataAsync(1, Local, Remote, Bytes(300, 'a'), firstCaller.Token);
        await wire.WriteEnteredAsync(ct);
        var waiting = framing.WriteFrameAsync(Data("never"), secondCaller.Token);

        await secondCaller.CancelAsync();
        await FluentActions.Awaiting(() => waiting.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().ThrowAsync<OperationCanceledException>();
        await firstCaller.CancelAsync();
        await Task.Delay(100, ct);
        going.IsCompleted.Should().BeFalse("a write the writer has started is not cut short");

        wire.Release();
        await going.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await framing.WriteFrameAsync(G, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        var frames = await wire.FramesAsync(ct);
        frames.Select(f => f.Kind).Should().Equal(['D', 'D', 'G'], "the message whole, the taken-back write never");
        frames.Where(f => f.Kind == 'D').SelectMany(f => f.Payload).Should().Equal(Bytes(300, 'a'));
        await framing.StopWriterAsync();
    }

    [Fact]
    public async Task ALinkWhoseNodeStopsReading_Closes_AfterThirtySeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerFactory();
        var writeClock = new ObservableTimeProvider();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        try
        {
            var sessions = Channel.CreateUnbounded<AgwLinkSession>();
            var opening = AgwLink.OpenCoreAsync(
                "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Local, s => sessions.Writer.TryWrite(s), logs.CreateLogger("link"),
                txGate: null, clock: null, onRegisterSent: null, stopping: ct, ct: ct, listenOnly: false, writeClock: writeClock);
            using var node = new FakeAgwSocket(await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct));
            await node.ExpectRegisterAsync(ct);
            await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
            await node.ReadUntilAsync('m', ct);
            await using var link = await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct);
            await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, 1));
            var session = await sessions.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);

            // The node stops reading: beacons go until the socket's buffers
            // are full, and then one does not.
            Task stalled;
            var sent = 0;
            while (true)
            {
                var beacon = link.SendUiAsync(1, "DAPPS", Bytes(250, 'b'), ct);
                if (!beacon.IsCompleted && await Task.WhenAny(beacon, Task.Delay(500, ct)) != beacon)
                {
                    stalled = beacon;
                    break;
                }
                await beacon;
                (++sent).Should().BeLessThan(200_000, "the socket's buffers fill long before this");
            }
            link.Closed.IsCompleted.Should().BeFalse();

            writeClock.Advance(AgwFrameTransport.WriteStallLimit);
            await link.Closed.WaitAsync(FakeAgwHost.FrameTimeout, ct);
            link.CloseReason.Should().BeOfType<IOException>().Which.Message.Should().Contain("not reading");
            await FluentActions.Awaiting(() => stalled.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().ThrowAsync<IOException>();
            session.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue("every session on the link has gone with it");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static readonly AgwFrame G = new(0, 'G', 0, "", "", []);

    private static AgwFrame Data(string text) => new(1, 'D', 0xF0, Local, Remote, System.Text.Encoding.ASCII.GetBytes(text));

    private static byte[] Bytes(int count, char fill) => Enumerable.Repeat((byte)fill, count).ToArray();

    private sealed class SwitchableGate : IDappsTxGate
    {
        public bool TxAllowed { get; set; } = true;
        public string? BlockReason => TxAllowed ? null : "test";
    }

    /// <summary>A wire that records what is written, and can hold every
    /// write until released: a node that has stopped reading.</summary>
    private sealed class HeldStream : Stream
    {
        private readonly MemoryStream written = new();
        private readonly Channel<int> entered = Channel.CreateUnbounded<int>();
        private TaskCompletionSource? hold;

        public void Hold() => hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => hold?.TrySetResult();

        /// <summary>Completes once a write has reached the wire (and, if
        /// held, is waiting there).</summary>
        public async Task WriteEnteredAsync(CancellationToken ct) =>
            await entered.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);

        public async Task<List<AgwFrame>> FramesAsync(CancellationToken ct)
        {
            byte[] bytes;
            lock (written) bytes = written.ToArray();
            var reader = new AgwFrameTransport(new MemoryStream(bytes));
            var frames = new List<AgwFrame>();
            var total = 0;
            while (total < bytes.Length)
            {
                var frame = await reader.ReadFrameAsync(ct);
                total += AgwFrame.HeaderLength + frame.Payload.Length;
                frames.Add(frame);
            }
            return frames;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            entered.Writer.TryWrite(buffer.Length);
            if (hold is { } held) await held.Task.WaitAsync(ct);
            lock (written) written.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanWrite => true;
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
}
