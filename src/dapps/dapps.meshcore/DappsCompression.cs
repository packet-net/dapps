using System.Text;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace dapps.meshcore;

/// <summary>
/// Optional payload compression for the MeshCore bearer. The win on this slow,
/// shared, flooded channel is a SHARED DICTIONARY trained on representative DAPPS
/// traffic - generic compressors barely dent a ~100-byte message, a dictionary
/// collapses most messages into a single LoRa packet.
///
/// The dictionary is built deterministically from a fixed sample corpus so every
/// node running the same build derives byte-identical dictionary bytes. Version 1.
/// A production build should ship a versioned dictionary blob negotiated by id
/// (#154 / dictionary-versioning) rather than rebuilding from code.
/// </summary>
public static class DappsCompression
{
    public enum Mode { None, ZstdDict }

    /// <summary>Dictionary version - both ends must agree. Carried implicitly by
    /// the build today; negotiate explicitly later.</summary>
    public const byte DictionaryVersion = 1;

    private static readonly byte[] Dict = BuildDict();

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

    private static byte[] BuildDict()
    {
        using var ms = new MemoryStream();
        foreach (var m in SampleCorpus())
        {
            var enc = BackhaulMessageCodec.Encode(m);
            if (ms.Length + enc.Length > 16 * 1024) break;
            ms.Write(enc, 0, enc.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Fixed, representative DAPPS messages used to seed the zstd content
    /// dictionary. Deterministic across builds.</summary>
    private static IEnumerable<BackhaulMessage> SampleCorpus()
    {
        string[] calls = ["M0LTE-7", "GB7RDG-1", "EI5IYB-1", "G4BFG-9", "2E0XYZ", "MM0ABC-2", "GB7XYZ-1", "M7DEF-5"];
        string[] texts =
        [
            "73", "QSL 73 GL", "GM all de M0LTE", "ack", "ok rx 5/9", "ACK 4f2", "NAK 0c3 retry",
            "GM all de M0LTE, nice signal into Reading this morning, 599 here",
            "Anyone around for a sked on the DAPPS net at 1900 local? 73",
            "Rig is FT-991A into a 40m dipole at 8m, running 25W on this one",
            "!5152.34N/00007.12W>DAPPS node QRV", "!5340.10N/00220.55W>portable /P on hilltop",
            "{\"t\":21.4,\"h\":62,\"p\":1013}", "{\"t\":-3.1,\"h\":88,\"p\":998,\"w\":12.4}", "{\"batt\":3.92,\"sol\":0.41}",
            "Hello from the DAPPS mailbox. This is a longer store-and-forward message that a user might send over the mesh. 73.",
        ];
        var i = 0;
        foreach (var t in texts)
        {
            var from = calls[i % calls.Length];
            var to = calls[(i + 3) % calls.Length];
            yield return new BackhaulMessage(
                Id: i.ToString("x7"), Destination: to, Salt: 1000 + i, Ttl: 3600,
                Payload: Encoding.UTF8.GetBytes(t), Originator: from, LinkSourceCallsign: from,
                Headers: new Dictionary<string, string> { ["app"] = "chat" });
            i++;
        }
    }
}
