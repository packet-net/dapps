using dapps.client.Tx;

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
/// </summary>
public sealed class AgwFrameTransport
{
    private readonly Stream stream;
    private readonly IDappsTxGate txGate;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public AgwFrameTransport(Stream stream, IDappsTxGate? txGate = null)
    {
        this.stream = stream;
        this.txGate = txGate ?? AlwaysOpenTxGate.Instance;
    }

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

    public async Task<AgwFrame> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[AgwFrame.HeaderLength];
        await stream.ReadExactlyAsync(header, ct);
        var dataLength = AgwFrame.ReadDataLength(header);

        if (dataLength < 0)
        {
            throw new InvalidDataException($"AGW header has negative DataLength: {dataLength}");
        }

        var payload = dataLength == 0 ? [] : new byte[dataLength];
        if (dataLength > 0)
        {
            await stream.ReadExactlyAsync(payload, ct);
        }
        return AgwFrame.ParseHeader(header, payload);
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
