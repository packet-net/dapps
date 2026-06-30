using System.IO.Compression;
using System.Text;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace MeshCorePoc;

/// <summary>
/// No-radio characterisation harness: how much DAPPS payload fits per MeshCore
/// LoRa packet on each transport (text+base64 vs binary channel-data), how heavy
/// compression changes that, and the resulting packet count + LoRa airtime.
///
/// Two numbers matter on this bearer:
///   - DAPPS bytes carried per packet (transport efficiency), and
///   - LoRa airtime per packet (the duty-cycle currency).
/// At SF8/BW62.5/CR4-8 a full packet is ~1 s on air, so every byte saved and
/// every packet avoided is real airtime back.
/// </summary>
public static class Characterise
{
    // ---- LoRa PHY (UK narrow) ----
    public const int Sf = 8;
    public const double BwHz = 62_500;
    public const int CrDenom = 8;        // 4/8
    public const int Preamble = 8;

    // MeshCore packet overhead on air (header + path + cipher MAC), beyond the
    // channel plaintext. Calibrated from a hardware LOG_RX_DATA measurement;
    // overridable so the report states its assumption.
    public const int MeshCoreOnAirOverhead = 16;

    // Channel plaintext caps (firmware): one LoRa packet holds at most this many
    // plaintext bytes for a group/channel message.
    public const int MaxGroupPlaintext = 165;   // MAX_GROUP_DATA_LENGTH

    // Per-packet plaintext consumed by framing on each path, before our bytes:
    //   text:   4-byte timestamp + 1 flags + "<name>: " prefix (assume 10)
    //   binary: NO on-air timestamp; just our own 1-byte nonce/flags header
    //           (needed because identical binary frames are de-duped by the mesh).
    //           data_type(2)+data_len(1) are already excluded from the 165 cap.
    public const int TextFramingOverhead = 4 + 1 + 10;
    public const int BinaryFramingOverhead = 1;

    public const string Marker = "D1:";          // text-path DAPPS marker
    public const int PacketiserHeader = Packetiser.HeaderLength; // 13

    /// <summary>LoRa time-on-air in ms for a PHY payload of <paramref name="payloadBytes"/>.</summary>
    public static double AirtimeMs(int payloadBytes)
    {
        double tSym = Math.Pow(2, Sf) / BwHz * 1000.0;
        double tPreamble = (Preamble + 4.25) * tSym;
        const int de = 0, ih = 0, crcOn = 1;      // explicit header, CRC on, no low-rate-opt
        int cr = CrDenom - 4;
        double num = 8 * payloadBytes - 4 * Sf + 28 + 16 * crcOn - 20 * ih;
        double den = 4 * (Sf - 2 * de);
        int symb = 8 + (int)Math.Max(Math.Ceiling(num / den) * (cr + 4), 0);
        return tPreamble + symb * tSym;
    }

    /// <summary>Max DAPPS-payload bytes carried per packet on each path
    /// (after Packetiser header), and the on-air packet size for a full packet.</summary>
    public static (int textPerPkt, int binPerPkt, int textOnAir, int binOnAir) PerPacketCapacity()
    {
        // text: base64 inflates 3 raw -> 4 chars. Available text chars =
        // MaxGroupPlaintext - TextFramingOverhead - marker. raw = chars/4*3.
        int textChars = MaxGroupPlaintext - TextFramingOverhead - Marker.Length;
        int textRaw = textChars / 4 * 3;                        // pre-base64 fragment bytes
        int textPerPkt = Math.Max(0, textRaw - PacketiserHeader);
        int textOnAir = MaxGroupPlaintext + MeshCoreOnAirOverhead;

        int binRaw = MaxGroupPlaintext - BinaryFramingOverhead; // fragment bytes
        int binPerPkt = Math.Max(0, binRaw - PacketiserHeader);
        int binOnAir = MaxGroupPlaintext + MeshCoreOnAirOverhead;

        return (textPerPkt, binPerPkt, textOnAir, binOnAir);
    }

    // ---------- compression ----------

    public enum Scheme { None, Deflate, Brotli, Zstd, ZstdDict }

    public static byte[] Compress(Scheme s, byte[] data, ZstdSharp.Compressor? zstd, ZstdSharp.Compressor? zstdDict) => s switch
    {
        Scheme.None => data,
        Scheme.Deflate => DeflateBytes(data),
        Scheme.Brotli => BrotliBytes(data),
        Scheme.Zstd => zstd!.Wrap(data).ToArray(),
        Scheme.ZstdDict => zstdDict!.Wrap(data).ToArray(),
        _ => data,
    };

    private static byte[] DeflateBytes(byte[] d)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, true)) z.Write(d, 0, d.Length);
        return ms.ToArray();
    }

    private static byte[] BrotliBytes(byte[] d)
    {
        using var ms = new MemoryStream();
        using (var z = new BrotliStream(ms, CompressionLevel.SmallestSize, true)) z.Write(d, 0, d.Length);
        return ms.ToArray();
    }

    /// <summary>Packets needed to carry <paramref name="payloadBytes"/> on a path
    /// whose per-packet capacity is <paramref name="perPkt"/> (Packetiser always on).</summary>
    public static int Packets(int payloadBytes, int perPkt) =>
        Math.Max(1, (payloadBytes + perPkt - 1) / perPkt);

    // ---------- corpus ----------

    public static IReadOnlyList<(string label, BackhaulMessage msg)> Corpus(int seed)
    {
        var rng = new Random(seed);
        string[] calls = ["M0LTE-7", "GB7RDG-1", "EI5IYB-1", "G4BFG-9", "2E0XYZ", "MM0ABC-2", "GB7XYZ-1", "M7DEF-5"];
        string[] apps = ["chat", "mail", "pos", "sensor", "ack", "telem"];
        string[] chats =
        [
            "73", "QSL 73 GL", "GM all de M0LTE", "ack", "ok rx 5/9",
            "GM all de M0LTE, nice signal into Reading this morning, 599 here",
            "Anyone around for a sked on the DAPPS net at 1900 local? 73",
            "Rig is FT-991A into a 40m dipole at 8m, running 25W on this one",
        ];
        string[] positions = ["!5152.34N/00007.12W>DAPPS node QRV", "!5340.10N/00220.55W>portable /P on hilltop"];
        string[] sensors = ["{\"t\":21.4,\"h\":62,\"p\":1013}", "{\"t\":-3.1,\"h\":88,\"p\":998,\"w\":12.4}", "{\"batt\":3.92,\"sol\":0.41}"];
        string[] acks = ["ACK 4f2", "ACK a91 ok", "NAK 0c3 retry"];
        string mail = "Hello from the DAPPS mailbox. This is a longer store-and-forward message " +
                      "that a user might send over the mesh. It contains a few sentences of ordinary " +
                      "English prose so that the compressor has something realistic to chew on, and so " +
                      "that we can see how a multi-packet payload behaves over a slow LoRa channel. 73.";

        var list = new List<(string, BackhaulMessage)>();
        int n = 0;
        void Add(string app, string text)
        {
            string from = calls[rng.Next(calls.Length)];
            string to = calls[rng.Next(calls.Length)];
            var m = new BackhaulMessage(
                Id: n.ToString("x7"), Destination: $"{to}", Salt: rng.Next(),
                Ttl: 3600, Payload: Encoding.UTF8.GetBytes(text),
                Originator: from, LinkSourceCallsign: from,
                Headers: new Dictionary<string, string> { ["app"] = app });
            list.Add(($"{app}:{text.Length}B", m));
            n++;
        }

        foreach (var c in chats) Add("chat", c);
        foreach (var p in positions) Add("pos", p);
        foreach (var s in sensors) Add("sensor", s);
        foreach (var a in acks) Add("ack", a);
        Add("mail", mail);
        Add("mail", mail[..120]);
        // a bit more chat variety
        for (int i = 0; i < 12; i++) Add("chat", chats[rng.Next(chats.Length)] + (i % 3 == 0 ? " ##" + i : ""));
        return list;
    }

    // ---------- report ----------

    private static int Base64Len(int n) => (n + 2) / 3 * 4;

    /// <summary>Total LoRa airtime (ms) to carry <paramref name="dappsBytes"/>
    /// of (already-compressed) encoded payload over the given path.</summary>
    public static double AirtimeForPayload(int dappsBytes, bool binary)
    {
        var (textPerPkt, binPerPkt, _, _) = PerPacketCapacity();
        int perPkt = binary ? binPerPkt : textPerPkt;
        int packets = Packets(dappsBytes, perPkt);
        double total = 0;
        int remaining = dappsBytes;
        for (int i = 0; i < packets; i++)
        {
            int chunk = Math.Min(perPkt, Math.Max(remaining, 0));
            remaining -= chunk;
            int fragBytes = chunk + PacketiserHeader;
            int plaintext = binary
                ? BinaryFramingOverhead + fragBytes
                : TextFramingOverhead + Marker.Length + Base64Len(fragBytes);
            total += AirtimeMs(plaintext + MeshCoreOnAirOverhead);
        }
        return total;
    }

    private static byte[] BuildDict(List<byte[]> samples, int cap)
    {
        using var ms = new MemoryStream();
        foreach (var s in samples)
        {
            if (ms.Length + s.Length > cap) break;
            ms.Write(s, 0, s.Length);
        }
        return ms.ToArray();
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    public static string Run(int trainSeed = 1, int testSeed = 2)
    {
        var sb = new StringBuilder();
        var (textPerPkt, binPerPkt, textOnAir, binOnAir) = PerPacketCapacity();
        double fullAir = AirtimeMs(MaxGroupPlaintext + MeshCoreOnAirOverhead);

        // Dictionary trained on a DISJOINT corpus so the test isn't self-fitted.
        var trainEnc = Corpus(trainSeed).Select(x => BackhaulMessageCodec.Encode(x.msg)).ToList();
        var dictBlob = BuildDict(trainEnc, 8 * 1024);
        using var zstd = new ZstdSharp.Compressor(19);
        using var zstdDict = new ZstdSharp.Compressor(19);
        zstdDict.LoadDictionary(dictBlob);

        var test = Corpus(testSeed);
        var encoded = test.Select(x => (x.label, enc: BackhaulMessageCodec.Encode(x.msg))).ToList();

        sb.AppendLine("# MeshCore bearer characterisation (DAPPS payload over a private channel)");
        sb.AppendLine();
        sb.AppendLine($"- LoRa: SF{Sf} / BW {BwHz / 1000:0.#} kHz / CR 4/{CrDenom} / preamble {Preamble} → **full packet ≈ {fullAir:0} ms on air**");
        sb.AppendLine($"- Channel plaintext cap {MaxGroupPlaintext} B; assumed MeshCore on-air overhead {MeshCoreOnAirOverhead} B (calibrate from hardware)");
        sb.AppendLine($"- Dictionary: zstd content dict ({dictBlob.Length} B) trained on a disjoint corpus; test corpus = {encoded.Count} messages");
        sb.AppendLine();

        // Table 1 - per-packet transport capacity.
        sb.AppendLine("## 1. Transport efficiency (DAPPS bytes carried per packet)");
        sb.AppendLine();
        sb.AppendLine("| Path | base64? | DAPPS bytes/packet | vs text |");
        sb.AppendLine("|---|---|---:|---:|");
        sb.AppendLine($"| text `0x03` + base64 | yes | {textPerPkt} | 1.00× |");
        sb.AppendLine($"| binary `0x3E` | no | {binPerPkt} | {(double)binPerPkt / textPerPkt:0.00}× |");
        sb.AppendLine();

        // Table 2 - compression over the test corpus.
        sb.AppendLine("## 2. Compression of the encoded BackhaulMessage (test corpus)");
        sb.AppendLine();
        sb.AppendLine("| Scheme | mean ratio | median | mean bytes | ≤1 pkt (text) | ≤1 pkt (bin) | mean airtime (bin) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (Scheme s in Enum.GetValues<Scheme>())
        {
            var comp = encoded.Select(e => Compress(s, e.enc, zstd, zstdDict).Length).ToList();
            var ratios = encoded.Zip(comp, (e, c) => (double)c / e.enc.Length).ToList();
            int onePktText = encoded.Zip(comp, (e, c) => c <= textPerPkt ? 1 : 0).Sum();
            int onePktBin = encoded.Zip(comp, (e, c) => c <= binPerPkt ? 1 : 0).Sum();
            double airBin = encoded.Zip(comp, (e, c) => AirtimeForPayload(c, true)).Average();
            sb.AppendLine($"| {s} | {ratios.Average():0.00} | {Median(ratios):0.00} | {comp.Average():0.0} | " +
                          $"{100.0 * onePktText / comp.Count:0}% | {100.0 * onePktBin / comp.Count:0}% | {airBin:0} ms |");
        }
        sb.AppendLine();
        sb.AppendLine("_ratio = compressed ÷ encoded (lower is better); <1 means smaller. Ratios >1 on tiny messages = compressor overhead._");
        sb.AppendLine();

        // Table 3 - representative messages: raw vs best compression, both paths.
        sb.AppendLine("## 3. Representative messages (packets & airtime: raw → zstd+dict)");
        sb.AppendLine();
        sb.AppendLine("| message | encoded B | zstd+dict B | pkts text raw→cmp | pkts bin raw→cmp | airtime bin raw→cmp |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        string[] want = ["ack:", "chat:2B", "chat:15B", "pos:", "sensor:", "mail:"];
        foreach (var w in want)
        {
            var hit = encoded.FirstOrDefault(e => e.label.StartsWith(w, StringComparison.Ordinal));
            if (hit.enc is null) continue;
            int raw = hit.enc.Length;
            int cmp = Compress(Scheme.ZstdDict, hit.enc, zstd, zstdDict).Length;
            sb.AppendLine($"| {hit.label} | {raw} | {cmp} | " +
                          $"{Packets(raw, textPerPkt)}→{Packets(cmp, textPerPkt)} | " +
                          $"{Packets(raw, binPerPkt)}→{Packets(cmp, binPerPkt)} | " +
                          $"{AirtimeForPayload(raw, true):0}→{AirtimeForPayload(cmp, true):0} ms |");
        }
        sb.AppendLine();

        // Aggregate headline.
        var encLens = encoded.Select(e => e.enc.Length).ToList();
        var bestLens = encoded.Select(e => Compress(Scheme.ZstdDict, e.enc, zstd, zstdDict).Length).ToList();
        double airRawTextTotal = encLens.Sum(l => AirtimeForPayload(l, false));
        double airRawBinTotal = encLens.Sum(l => AirtimeForPayload(l, true));
        double airCmpBinTotal = bestLens.Sum(l => AirtimeForPayload(l, true));
        sb.AppendLine("## 4. Headline (total airtime to send the whole test corpus)");
        sb.AppendLine();
        sb.AppendLine($"| | total airtime | vs text-raw |");
        sb.AppendLine($"|---|---:|---:|");
        sb.AppendLine($"| text + base64, no compression | {airRawTextTotal / 1000:0.0} s | 1.00× |");
        sb.AppendLine($"| binary, no compression | {airRawBinTotal / 1000:0.0} s | {airRawBinTotal / airRawTextTotal:0.00}× |");
        sb.AppendLine($"| binary + zstd-dict | {airCmpBinTotal / 1000:0.0} s | {airCmpBinTotal / airRawTextTotal:0.00}× |");
        sb.AppendLine();
        sb.AppendLine($"**Net: binary + heavy dictionary compression ≈ {airRawTextTotal / airCmpBinTotal:0.0}× the goodput of the text path** on this corpus.");
        return sb.ToString();
    }
}
