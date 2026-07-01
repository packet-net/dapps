using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace dapps.meshcore;

/// <summary>
/// Carries a DAPPS <see cref="BackhaulMessage"/> over a MeshCore private channel
/// using the binary channel-data path. Reuses the real DAPPS codec + packetiser.
///
///   send: Encode → (optional) compress → Packetiser.Split → per fragment prepend a
///         header and emit as one channel-data datagram.
///   recv: strip header → Reassembler → (decompress) → Decode.
///
/// Frame header: byte0 = flags (bit0 = compressed, bits1-7 = rolling nonce). The nonce
/// is essential - the binary path has no on-air timestamp, so byte-identical frames
/// share a packet hash and the mesh hasSeen table would drop the duplicate. When bit0
/// is set, byte1 is the compression dictionary VERSION (#23) and the fragment follows
/// at offset 2; when clear, the fragment follows at offset 1 (uncompressed frames are
/// byte-identical to the pre-versioning format). The version lets a receiver pick the
/// matching dictionary and drop frames from a dictionary it doesn't hold rather than
/// decompress with the wrong one.
/// </summary>
public sealed class MeshCoreChannelTransport
{
    /// <summary>Fragment size incl. the 13-byte Packetiser header. The channel-data
    /// payload = our header (1 B, or 2 B when compressed) + fragment; worst case
    /// 2 + 160 = 162, inside the firmware's 165 B limit.</summary>
    public const int Mtu = 160;

    private readonly Reassembler _reassembler = new();
    private readonly Dictionary<string, (bool comp, byte version, DateTime seen)> _compressed = new();
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
            // Compressed frames carry the dictionary version in byte1 so the receiver
            // decompresses with the matching dictionary; uncompressed frames omit it.
            byte[] frame;
            if (comp)
            {
                frame = new byte[2 + f.Length];
                frame[0] = hdr;
                frame[1] = DappsCompression.CurrentDictionaryVersion;
                f.CopyTo(frame, 2);
            }
            else
            {
                frame = new byte[1 + f.Length];
                frame[0] = hdr;
                f.CopyTo(frame, 1);
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary><see cref="Unsupported"/> = a fully-reassembled compressed message whose
    /// dictionary version this build doesn't hold; safe to drop, distinct from malformed
    /// (<see cref="Bad"/>) so the caller can log "peer on a newer dictionary".</summary>
    public enum Kind { FragmentPartial, BackhaulComplete, Bad, Unsupported }

    public readonly record struct Result(Kind Kind, BackhaulMessage? Message, FragmentHeader? Header);

    /// <summary>Feed one received channel-data payload.</summary>
    public Result Ingest(byte[] dataPayload, DateTime now)
    {
        if (dataPayload.Length < 1) return new Result(Kind.Bad, null, null);
        bool comp = (dataPayload[0] & 1) != 0;

        // Compressed frames carry a version byte before the fragment (see ToFrames).
        int fragOffset = comp ? 2 : 1;
        byte version = comp && dataPayload.Length >= 2 ? dataPayload[1] : (byte)0;
        if (dataPayload.Length < fragOffset + Packetiser.HeaderLength) return new Result(Kind.Bad, null, null);
        var fragment = dataPayload[fragOffset..];

        FragmentHeader header;
        try { header = Packetiser.ParseHeader(fragment); }
        catch (InvalidDataException) { return new Result(Kind.Bad, null, null); }

        _compressed[header.Id] = (comp, version, now);
        var assembled = _reassembler.Accept(fragment, now);
        if (assembled is null) return new Result(Kind.FragmentPartial, null, header);

        var meta = _compressed.TryGetValue(header.Id, out var c) ? c : (comp, version, now);
        _compressed.Remove(header.Id);

        if (meta.comp && !DappsCompression.IsKnownVersion(meta.version))
            return new Result(Kind.Unsupported, null, header);
        try
        {
            var body = meta.comp ? DappsCompression.Decompress(meta.version, assembled) : assembled;
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
