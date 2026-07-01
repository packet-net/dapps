using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace dapps.meshcore;

/// <summary>
/// Carries a DAPPS <see cref="BackhaulMessage"/> over a MeshCore private channel
/// using the binary channel-data path. Reuses the real DAPPS codec + packetiser.
///
///   send: Encode → (optional) compress → Packetiser.Split → per fragment prepend a
///         1-byte header and emit as one channel-data datagram.
///   recv: strip header → Reassembler → (decompress) → Decode.
///
/// Frame header byte: bit0 = compressed, bits1-7 = rolling nonce. The nonce is
/// essential - the binary path has no on-air timestamp, so byte-identical frames
/// share a packet hash and the mesh hasSeen table would drop the duplicate.
/// </summary>
public sealed class MeshCoreChannelTransport
{
    /// <summary>Fragment size incl. the 13-byte Packetiser header. The channel-data
    /// payload = 1 (our header) + fragment, capped at the firmware's 165 B limit.</summary>
    public const int Mtu = 160;

    private readonly Reassembler _reassembler = new();
    private readonly Dictionary<string, (bool comp, DateTime seen)> _compressed = new();
    private readonly object _nonceLock = new();
    private byte _nonce;

    /// <summary>Encode a BackhaulMessage into one-or-more channel-data payloads.</summary>
    public IReadOnlyList<byte[]> ToFrames(BackhaulMessage message, DappsCompression.Mode compress)
    {
        var encoded = BackhaulMessageCodec.Encode(message);
        var body = DappsCompression.Compress(compress, encoded);
        var fragments = Packetiser.Split(message.Id, body, Mtu);
        bool comp = compress != DappsCompression.Mode.None;
        var frames = new List<byte[]>(fragments.Count);
        foreach (var f in fragments)
        {
            // ToFrames is now called concurrently (OMM send + reliability resend loop
            // + inbound ACK emission), so the rolling nonce must be atomic.
            byte hdr;
            lock (_nonceLock)
            {
                hdr = (byte)((_nonce << 1) | (comp ? 1 : 0));
                _nonce = (byte)((_nonce + 1) & 0x7F);
            }
            var frame = new byte[1 + f.Length];
            frame[0] = hdr;
            f.CopyTo(frame, 1);
            frames.Add(frame);
        }
        return frames;
    }

    public enum Kind { FragmentPartial, BackhaulComplete, Bad }

    public readonly record struct Result(Kind Kind, BackhaulMessage? Message, FragmentHeader? Header);

    /// <summary>Feed one received channel-data payload.</summary>
    public Result Ingest(byte[] dataPayload, DateTime now)
    {
        if (dataPayload.Length < 1 + Packetiser.HeaderLength) return new Result(Kind.Bad, null, null);
        bool comp = (dataPayload[0] & 1) != 0;
        var fragment = dataPayload[1..];

        FragmentHeader header;
        try { header = Packetiser.ParseHeader(fragment); }
        catch (InvalidDataException) { return new Result(Kind.Bad, null, null); }

        _compressed[header.Id] = (comp, now);
        var assembled = _reassembler.Accept(fragment, now);
        if (assembled is null) return new Result(Kind.FragmentPartial, null, header);

        var compressed = _compressed.TryGetValue(header.Id, out var c) && c.comp;
        _compressed.Remove(header.Id);
        try
        {
            var body = compressed ? DappsCompression.Decompress(DappsCompression.Mode.ZstdDict, assembled) : assembled;
            return new Result(Kind.BackhaulComplete, BackhaulMessageCodec.Decode(body), header);
        }
        catch (Exception)
        {
            return new Result(Kind.Bad, null, header);
        }
    }

    /// <summary>Drop reassembly state (and the matching compressed-flag entries) for
    /// messages whose first fragment is older than <paramref name="cutoff"/>.</summary>
    public int DropStale(DateTime cutoff)
    {
        foreach (var k in _compressed.Where(kv => kv.Value.seen < cutoff).Select(kv => kv.Key).ToList())
            _compressed.Remove(k);
        return _reassembler.DropOlderThan(cutoff);
    }
}
