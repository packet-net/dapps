using dapps.client.Backhaul.Datagram;

namespace MeshCorePoc;

/// <summary>
/// Optional payload compression for the DAPPS-over-MeshCore bearer. The big win
/// on this slow, shared, flooded channel is a SHARED DICTIONARY trained on
/// representative DAPPS traffic - generic compressors barely dent a ~100-byte
/// message, but a dictionary collapses most messages into a single LoRa packet.
///
/// The dictionary here is built deterministically from the characterisation
/// corpus, so every node running this binary derives byte-identical dictionary
/// bytes (a real deployment would ship a versioned dictionary blob, negotiated
/// by id so both ends agree).
/// </summary>
public static class DappsCompression
{
    public enum Mode { None, ZstdDict }

    private static readonly byte[] Dict = BuildDict();

    private static byte[] BuildDict()
    {
        using var ms = new MemoryStream();
        foreach (var (_, msg) in Characterise.Corpus(1))
        {
            var enc = BackhaulMessageCodec.Encode(msg);
            if (ms.Length + enc.Length > 8 * 1024) break;
            ms.Write(enc, 0, enc.Length);
        }
        return ms.ToArray();
    }

    public static byte[] Compress(Mode mode, byte[] data)
    {
        if (mode == Mode.None) return data;
        using var c = new ZstdSharp.Compressor(19);
        c.LoadDictionary(Dict);
        return c.Wrap(data).ToArray();
    }

    public static byte[] Decompress(Mode mode, byte[] data)
    {
        if (mode == Mode.None) return data;
        using var d = new ZstdSharp.Decompressor();
        d.LoadDictionary(Dict);
        return d.Unwrap(data).ToArray();
    }
}
