using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// Reads and writes <see cref="AgwFrame"/>s over a duplex Stream. Writes are
/// serialised so concurrent senders can't interleave.
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
    /// </summary>
    public async Task WriteDataAsync(byte port, string callFrom, string callTo, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        for (var offset = 0; offset < data.Length; offset += MaxDataFrameBytes)
        {
            var chunk = data.Slice(offset, Math.Min(MaxDataFrameBytes, data.Length - offset)).ToArray();
            await WriteFrameAsync(new AgwFrame(port, 'D', 0xF0, callFrom, callTo, chunk), ct);
        }
    }

    public async Task WriteFrameAsync(AgwFrame frame, CancellationToken ct)
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

        var bytes = frame.ToBytes();
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
