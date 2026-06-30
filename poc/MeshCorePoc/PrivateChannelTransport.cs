using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace MeshCorePoc;

/// <summary>
/// Carries a DAPPS <see cref="BackhaulMessage"/> over a MeshCore private channel
/// using the BINARY channel-data path (`SEND_CHANNEL_DATA` 0x3E / `CHANNEL_DATA_RECV`
/// 0x1B). Binary is the chosen carriage: ~1.5× the payload/packet of text, no base64,
/// and no firmware "&lt;name&gt;: " prefix (the bytes arrive verbatim).
///
///   send: Encode → (optional) compress → Packetiser.Split → per fragment prepend a
///         1-byte header and emit as one channel-data datagram.
///   recv: strip header → Reassembler → (decompress) → Decode.
///
/// Frame header byte: bit0 = compressed, bits1-7 = rolling nonce. The nonce is
/// essential: the binary path has NO on-air timestamp, so two byte-identical
/// datagrams share a packet hash and the mesh `hasSeen` table would silently drop
/// the second. Varying the header guarantees each datagram is distinct on air.
/// </summary>
public sealed class PrivateChannelTransport
{
    /// <summary>Fragment size incl. the 13-byte Packetiser header. The channel-data
    /// payload = 1 (our header) + fragment, capped at the firmware's 165-byte plaintext
    /// limit; 160 leaves margin.</summary>
    public const int Mtu = 160;

    private readonly Reassembler _reassembler = new();
    private readonly Dictionary<string, bool> _compressed = new();

    /// <summary>Encode a BackhaulMessage into one-or-more channel-data payloads.</summary>
    public static IReadOnlyList<byte[]> ToFrames(BackhaulMessage message, DappsCompression.Mode compress, ref byte nonce)
    {
        var encoded = BackhaulMessageCodec.Encode(message);
        var body = DappsCompression.Compress(compress, encoded);
        var fragments = Packetiser.Split(message.Id, body, Mtu);
        bool comp = compress != DappsCompression.Mode.None;
        var frames = new List<byte[]>(fragments.Count);
        foreach (var f in fragments)
        {
            byte hdr = (byte)((nonce << 1) | (comp ? 1 : 0));
            nonce = (byte)((nonce + 1) & 0x7F);
            var frame = new byte[1 + f.Length];
            frame[0] = hdr;
            f.CopyTo(frame, 1);
            frames.Add(frame);
        }
        return frames;
    }

    public enum Kind { FragmentPartial, BackhaulComplete, Bad }

    public readonly record struct Result(Kind Kind, BackhaulMessage? Message, FragmentHeader? Header);

    /// <summary>Feed one received channel-data payload. Returns whether it was a
    /// partial fragment or completed (and decoded) a BackhaulMessage.</summary>
    public Result Ingest(byte[] dataPayload, DateTime now)
    {
        if (dataPayload.Length < 1 + Packetiser.HeaderLength) return new Result(Kind.Bad, null, null);
        bool comp = (dataPayload[0] & 1) != 0;
        var fragment = dataPayload[1..];

        FragmentHeader header;
        try { header = Packetiser.ParseHeader(fragment); }
        catch (InvalidDataException) { return new Result(Kind.Bad, null, null); }

        _compressed[header.Id] = comp;
        var assembled = _reassembler.Accept(fragment, now);
        if (assembled is null) return new Result(Kind.FragmentPartial, null, header);

        var compressed = _compressed.TryGetValue(header.Id, out var c) && c;
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
}
