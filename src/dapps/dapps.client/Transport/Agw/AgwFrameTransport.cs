using System.Threading.Channels;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// Reads and writes <see cref="AgwFrame"/>s over a duplex Stream. Writes are
/// serialised so concurrent senders can't interleave. Once
/// <see cref="StartWriter"/> has been called, as an <see cref="AgwLink"/>
/// does, one writer task sends them in turn instead (its doc says why).
///
/// Single chokepoint for AGW-side TX gating: every AGW byte heading toward
/// the node passes through <see cref="WriteFrameAsync"/>. The configured
/// <see cref="IDappsTxGate"/> is consulted on each write; when closed, frames
/// whose <see cref="AgwFrame.Kind"/> produces RF (connect / data / UNPROTO /
/// raw) raise <see cref="TxStoppedException"/>. Node-control kinds
/// (register, login, port queries, monitor toggle, ...) are always permitted
/// so admin traffic to BPQ/XR keeps flowing while TX is gagged. Disconnect
/// frames ('d') are likewise allowed through to avoid leaking sessions.
///
/// Both directions are bounded by what BPQ itself accepts. A frame we
/// send with a port over 64 or more than 400 bytes of data makes BPQ's
/// AGW reader call the stream corrupt and drop the whole connection,
/// every session on it included, so <see cref="WriteFrameAsync"/>
/// refuses such a frame to its caller instead. On the read side a
/// header claiming more than <see cref="MaxReceivedDataLength"/> bytes,
/// or a kind that is not a letter, means the stream is not AGW (a
/// wrong port setting) or has lost its framing; reading it fails
/// before anything is allocated for it.
/// </summary>
public sealed class AgwFrameTransport
{
    private readonly Stream stream;
    private readonly IDappsTxGate txGate;
    private readonly ILogger? logger;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    // The writer (StartWriter), when there is one.
    private Channel<QueuedWrite>? queue;
    private CancellationTokenSource? writerStop;
    private Task writerTask = Task.CompletedTask;
    private readonly TaskCompletionSource<Exception> writeFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Ports over MaxPort already warned about on this connection: one
    // warning each, not one per frame. Touched only by the reader.
    private readonly HashSet<byte> warnedPorts = [];

    public AgwFrameTransport(Stream stream, IDappsTxGate? txGate = null, ILogger? logger = null)
    {
        this.stream = stream;
        this.txGate = txGate ?? AlwaysOpenTxGate.Instance;
        this.logger = logger;
    }

    /// <summary>Highest AGW port number BPQ accepts in a frame header.
    /// Above it BPQ's reader drops the connection as corrupt.</summary>
    public const int MaxPort = 64;

    /// <summary>Most data bytes BPQ accepts in one frame from us, of any
    /// kind. Above it BPQ's reader drops the connection as corrupt.</summary>
    public const int MaxFrameDataBytes = 400;

    /// <summary>Most data bytes an UNPROTO frame ('M' or 'V') may carry:
    /// the same host-API limit as a 'D' frame (<see cref="MaxDataFrameBytes"/>).</summary>
    public const int MaxUnprotoBytes = 256;

    /// <summary>Largest DataLength accepted on a received frame. BPQ
    /// sends nothing near it (a monitor line or a 'G' port list is a few
    /// hundred bytes); a header claiming more is not an AGW stream.</summary>
    public const int MaxReceivedDataLength = 8192;

    /// <summary>How long to wait for the reply to an 'X' registration.
    /// A host that accepts the TCP connection and never answers (the
    /// AGW port setting pointing at some other service, or a node that
    /// wedged) would otherwise hold the caller for good.</summary>
    public static readonly TimeSpan RegisterReplyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest one write to the node may take once the writer
    /// (<see cref="StartWriter"/>) has started it. BPQ reads its AGW
    /// sockets every 100 ms; a write that has not gone in this long means
    /// the node has stopped reading, and every caller on the connection
    /// is stuck behind it.</summary>
    public static readonly TimeSpan WriteStallLimit = TimeSpan.FromSeconds(30);

    /// <summary>Most writes the writer holds waiting to go; a caller
    /// beyond that waits for room.</summary>
    public const int WriteQueueCapacity = 256;

    /// <summary>
    /// Most bytes one AGW 'D' frame may carry. BPQ hands a data frame to
    /// its host API's <c>SendMsgEx</c>, which starts
    /// <c>if (len &gt; 256) return 0; // IGNORE</c> (CommonCode.c): a larger
    /// frame vanishes without a word, and the far end waits for a payload
    /// that never comes. Over 400 bytes is worse: BPQ's AGW reader
    /// (AGWAPI.c) calls the frame corrupt and drops the whole AGW
    /// connection, and every session on it. Measured against linbpq:
    /// 256 bytes arrive, 257 vanish. Still so in 6.0.25.40.
    /// </summary>
    public const int MaxDataFrameBytes = 256;

    /// <summary>
    /// Send <paramref name="data"/> on a connected session as as many 'D'
    /// frames as it takes to keep each within <see cref="MaxDataFrameBytes"/>.
    /// The node packetises them onto the link as usual, so splitting here
    /// costs nothing on air.
    /// The frames go out together, one after another, with nothing else
    /// between them, or none goes: the TX gate and the limits are checked
    /// for all of them first.
    /// </summary>
    public async Task WriteDataAsync(byte port, string callFrom, string callTo, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var bytes = new List<byte>(data.Length + (data.Length / MaxDataFrameBytes + 1) * AgwFrame.HeaderLength);
        for (var offset = 0; offset < data.Length; offset += MaxDataFrameBytes)
        {
            var chunk = data.Slice(offset, Math.Min(MaxDataFrameBytes, data.Length - offset)).ToArray();
            bytes.AddRange(Encode(new AgwFrame(port, 'D', 0xF0, callFrom, callTo, chunk)));
        }
        if (bytes.Count > 0) await SendAsync([.. bytes], ct);
    }

    public async Task WriteFrameAsync(AgwFrame frame, CancellationToken ct) => await SendAsync(Encode(frame), ct);

    /// <summary>The frame's bytes, once it has passed BPQ's limits and the
    /// TX gate; otherwise throws, and nothing is sent.</summary>
    private byte[] Encode(AgwFrame frame)
    {
        var length = frame.Payload?.Length ?? 0;
        if (frame.Port > MaxPort)
        {
            throw new ArgumentException(
                $"AGW frame kind '{frame.Kind}' on port {frame.Port}: BPQ accepts ports 0 to {MaxPort} and drops the connection otherwise", nameof(frame));
        }
        if (length > MaxFrameDataBytes)
        {
            throw new ArgumentException(
                $"AGW frame kind '{frame.Kind}' with {length} bytes: BPQ accepts at most {MaxFrameDataBytes} and drops the connection otherwise", nameof(frame));
        }
        if (frame.Kind is 'M' or 'V' && length > MaxUnprotoBytes)
        {
            throw new ArgumentException(
                $"AGW UNPROTO frame with {length} bytes: BPQ sends at most {MaxUnprotoBytes} and drops the rest without a word", nameof(frame));
        }

        if (IsRfEmitting(frame.Kind) && !txGate.TxAllowed)
        {
            throw new TxStoppedException(
                $"AGW frame kind '{frame.Kind}' (port {frame.Port}, {frame.CallFrom}->{frame.CallTo}): {txGate.BlockReason ?? "(no reason)"}");
        }

        return frame.ToBytes();
    }

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        if (queue is null)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
            return;
        }

        var write = new QueuedWrite(bytes);
        try
        {
            await queue.Writer.WriteAsync(write, ct);
        }
        catch (ChannelClosedException)
        {
            throw WriterClosed();
        }
        try
        {
            await write.Done.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Not started: it never goes. Started: it goes whole (a frame
            // cut short would leave the node reading the next frame's
            // header out of this one's data, on every session at once),
            // and the caller hears how that went.
            if (write.TryCancel()) throw;
            await write.Done.Task;
        }
    }

    /// <summary>
    /// From now on writes go through one writer task, in the order they
    /// were made, rather than each taking a lock and writing for itself.
    /// A caller whose token is cancelled while its write waits its turn
    /// takes it back; once the write has started it goes whole. A write
    /// that has not gone within <see cref="WriteStallLimit"/> (on
    /// <paramref name="clock"/>; the system clock if null) fails, as does
    /// every write after it, and <see cref="WriteFailed"/> completes: one
    /// connection carries every session, call and beacon, and a node that
    /// has stopped reading would otherwise hold them all for good, with
    /// nothing to say so.
    /// </summary>
    public void StartWriter(TimeProvider? clock = null)
    {
        if (queue is not null) throw new InvalidOperationException("the writer has already started");
        queue = Channel.CreateBounded<QueuedWrite>(new BoundedChannelOptions(WriteQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        writerStop = new CancellationTokenSource();
        writerTask = Task.Run(() => RunWriterAsync(queue, clock ?? TimeProvider.System, writerStop.Token));
    }

    /// <summary>Completes, with the reason, once a write has failed or
    /// stalled (<see cref="StartWriter"/>). Nothing more is written then.</summary>
    public Task<Exception> WriteFailed => writeFailed.Task;

    /// <summary>Stops the writer, failing every write still waiting with
    /// an <see cref="IOException"/>, and waits for it to finish.</summary>
    public async Task StopWriterAsync()
    {
        if (queue is null || writerStop is null) return;
        queue.Writer.TryComplete();
        await writerStop.CancelAsync();
        try { await writerTask; } catch { /* its failures went to the callers */ }
    }

    private async Task RunWriterAsync(Channel<QueuedWrite> writes, TimeProvider clock, CancellationToken stop)
    {
        try
        {
            await foreach (var write in writes.Reader.ReadAllAsync(stop))
            {
                if (!write.TryStart()) continue;
                try
                {
                    var sending = WriteToStreamAsync(write.Bytes, stop);
                    if (!sending.IsCompleted)
                    {
                        // Left behind if it stalls: the socket's close ends it.
                        _ = sending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        await sending.WaitAsync(WriteStallLimit, clock, stop);
                    }
                    await sending;
                    write.Done.TrySetResult();
                }
                catch (TimeoutException)
                {
                    var stalled = new IOException(
                        $"AGW: a write to the node has not gone in {WriteStallLimit.TotalSeconds:F0}s; the node is not reading, so the connection is closed");
                    write.Done.TrySetException(stalled);
                    writeFailed.TrySetResult(stalled);
                    return;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    write.Done.TrySetException(WriterClosed());
                    return;
                }
                catch (Exception ex)
                {
                    // The socket's write side has gone: so has the connection.
                    write.Done.TrySetException(ex is IOException ? ex : new IOException($"AGW: a write to the node failed: {ex.Message}", ex));
                    writeFailed.TrySetResult(ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            writes.Writer.TryComplete();
            while (writes.Reader.TryRead(out var left))
            {
                left.TryCancel(WriterClosed());
            }
        }
    }

    private async Task WriteToStreamAsync(byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static IOException WriterClosed() => new("AGW: the connection to the node has closed");

    /// <summary>One write waiting for, or taken by, the writer.</summary>
    private sealed class QueuedWrite(byte[] bytes)
    {
        private const int Waiting = 0, Started = 1, Cancelled = 2;
        private int state;

        public byte[] Bytes { get; } = bytes;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The writer takes it; false if its caller took it back.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref state, Started, Waiting) == Waiting;

        /// <summary>Its caller takes it back (or, with a
        /// <paramref name="reason"/>, the writer stopping fails it); false
        /// once the writer has it.</summary>
        public bool TryCancel(Exception? reason = null)
        {
            if (Interlocked.CompareExchange(ref state, Cancelled, Waiting) != Waiting) return false;
            if (reason is null) Done.TrySetCanceled();
            else Done.TrySetException(reason);
            return true;
        }
    }

    /// <summary>
    /// Reads the next frame. Throws <see cref="InvalidDataException"/>,
    /// before allocating anything for the payload, when the header's
    /// DataLength is negative or over <see cref="MaxReceivedDataLength"/>
    /// or its kind is not an ASCII letter: the stream is not AGW, or has
    /// lost its framing, and the connection has to go. A frame for a
    /// port over <see cref="MaxPort"/> is read past and dropped with a
    /// warning.
    /// </summary>
    public async Task<AgwFrame> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[AgwFrame.HeaderLength];
        while (true)
        {
            await stream.ReadExactlyAsync(header, ct);
            var dataLength = AgwFrame.ReadDataLength(header);

            if (dataLength < 0 || dataLength > MaxReceivedDataLength)
            {
                throw new InvalidDataException(
                    $"AGW header claims {dataLength} bytes of data (at most {MaxReceivedDataLength} accepted): not an AGW stream, or its framing is lost");
            }
            var kind = header[4];
            if (!char.IsAsciiLetter((char)kind))
            {
                throw new InvalidDataException(
                    $"AGW header has kind byte 0x{kind:X2}, not a letter: not an AGW stream, or its framing is lost");
            }

            var payload = dataLength == 0 ? [] : new byte[dataLength];
            if (dataLength > 0)
            {
                await stream.ReadExactlyAsync(payload, ct);
            }

            var port = header[0];
            if (port > MaxPort)
            {
                if (warnedPorts.Add(port))
                {
                    logger?.LogWarning(
                        "AGW: dropping frames for port {0}; AGW ports go up to {1} (warned once per connection)", port, MaxPort);
                }
                continue;
            }
            return AgwFrame.ParseHeader(header, payload);
        }
    }

    /// <summary>
    /// Waits for the host's reply to an 'X' registration, at most
    /// <see cref="RegisterReplyTimeout"/> on <paramref name="clock"/>.
    /// Frames of other kinds ahead of it are skipped. Throws
    /// <see cref="TimeoutException"/> if no reply comes in time.
    /// </summary>
    public Task<AgwFrame> ReadRegisterReplyAsync(string callsign, TimeProvider clock, CancellationToken ct) =>
        ReadReplyAsync('X', $"registering {callsign}", clock, ct);

    /// <summary>
    /// Waits for the host's reply to a <paramref name="kind"/> request
    /// (<paramref name="request"/> names it in the error), at most
    /// <see cref="RegisterReplyTimeout"/> on <paramref name="clock"/>.
    /// Frames of other kinds ahead of it are skipped. Throws
    /// <see cref="TimeoutException"/> if no reply comes in time.
    /// </summary>
    public async Task<AgwFrame> ReadReplyAsync(char kind, string request, TimeProvider clock, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RegisterReplyTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(linked.Token);
                if (frame.Kind == kind) return frame;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"AGW: no reply to {request} within {RegisterReplyTimeout.TotalSeconds:F0}s; is the AGW port setting really the node's AGW port?");
        }
    }

    /// <summary>
    /// AGW frame kinds whose write produces an on-air emission. The set
    /// is small and stable per the AGW host-protocol spec - everything
    /// else is node-control or RX-side.
    ///   'C' connect (SABM)
    ///   'v' connect via digipeaters
    ///   'c' connect with non-standard PID
    ///   'D' data (I-frame)
    ///   'M' UNPROTO send (UI frame)
    ///   'V' UNPROTO via digi
    ///   'K' raw frame
    /// </summary>
    private static bool IsRfEmitting(char kind) => kind switch
    {
        'C' or 'v' or 'c' or 'D' or 'M' or 'V' or 'K' => true,
        _ => false,
    };
}
