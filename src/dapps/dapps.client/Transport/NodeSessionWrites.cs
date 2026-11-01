namespace dapps.client.Transport;

/// <summary>
/// How DAPPS cuts the bytes of a connected session into writes to the
/// packet node, on every transport that carries one (AGW and RHPv2).
/// Only the writes change; the bytes and their order on the session are
/// the same however they are cut.
/// </summary>
public static class NodeSessionWrites
{
    /// <summary>
    /// Most bytes DAPPS hands the node in one write on a connected
    /// session: NET/ROM's L4 payload (LinBPQ's <c>MaxL4Len</c>), so the
    /// node never has to split a write into NET/ROM fragments.
    ///
    /// Why: LinBPQ cuts 20 bytes off a fragment when it resends it
    /// (M0LTE/linbpq#85, found on packet-net/dapps#425). The far end then
    /// waits for bytes that never come, and the session stalls until it
    /// gives up (180 s). A write that fits in one NET/ROM frame is never
    /// fragmented, so the bug can't touch it.
    ///
    /// When it could go back up: once a LinBPQ release carries the fix
    /// and versions without it are gone from the network. AGW's own
    /// limit is <see cref="Agw.AgwFrameTransport.MaxDataFrameBytes"/> (256).
    /// </summary>
    public const int MaxBytes = 236;

    /// <summary><paramref name="data"/> in order, as pieces of at most
    /// <see cref="MaxBytes"/> each. None for empty data.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += MaxBytes)
        {
            yield return data.Slice(offset, Math.Min(MaxBytes, data.Length - offset));
        }
    }
}
