using System.IO.Compression;
using System.Text;

namespace dapps.client.Compression;

/// <summary>
/// Payload compression for DAPPSv1 sessions. A payload goes on the wire
/// in one of these formats, named by the offer's <c>fmt=</c>:
/// <list type="bullet">
/// <item><c>p</c> - plain bytes.</item>
/// <item><c>d</c> - raw deflate. Received, never sent: it barely dents
/// the short payloads DAPPS carries.</item>
/// <item><c>z&lt;N&gt;</c> - zstd with shared dictionary version N. The
/// dictionary is what makes compression worth having on small messages:
/// it already holds the shapes of typical traffic (WPS replication JSON,
/// chat, telemetry), so even a 200-byte message has plenty to match.</item>
/// </list>
///
/// Versioning: every node keeps every dictionary version it has ever
/// shipped, so it can read what any peer sends, and compresses with
/// <see cref="CurrentDictionaryVersion"/>. A peer that doesn't hold that
/// version yet refuses the offer, and the sender offers the message
/// plain instead (see <see cref="DappsProtocolClient.PushAsync"/>). To
/// ship a new dictionary, add <c>payload-v2.dict</c>, register it in
/// <see cref="Dictionaries"/> and bump <see cref="CurrentDictionaryVersion"/>.
/// Never change a shipped version's bytes: peers and queued offers still
/// refer to it. A test pins each version's hash.
/// </summary>
public static class PayloadCompression
{
    /// <summary>The dictionary this build compresses with.</summary>
    public const int CurrentDictionaryVersion = 1;

    /// <summary>
    /// Only compress when it saves at least this many bytes on air,
    /// after paying for the <c>clen=</c> field the offer gains. Below
    /// that, a readable payload on a monitor is worth more than the
    /// fraction of a second saved.
    /// </summary>
    public const int MinimumSaving = 32;

    private const int Level = 19;

    private static readonly IReadOnlyDictionary<int, byte[]> Dictionaries = new Dictionary<int, byte[]>
    {
        [1] = LoadDictionary("payload-v1.dict"),
    };

    /// <summary>The <c>fmt=</c> value for zstd with dictionary <paramref name="version"/>.</summary>
    public static string ZstdFormat(int version) => "z" + version;

    /// <summary>True for every format this build can decode: <c>p</c>,
    /// <c>d</c>, and <c>z&lt;N&gt;</c> for each dictionary it holds.</summary>
    public static bool CanDecode(string format) =>
        format is "p" or "d" || (TryParseZstd(format, out var version) && Dictionaries.ContainsKey(version));

    /// <summary>
    /// The payload as it should go on the wire: compressed with the
    /// current dictionary when that saves at least
    /// <see cref="MinimumSaving"/> bytes, otherwise null (send it plain).
    /// </summary>
    public static (string Format, byte[] Bytes)? TryCompress(byte[] payload)
    {
        // Can't possibly clear the bar; don't spend the CPU finding out.
        if (payload.Length < MinimumSaving + 16) return null;

        byte[] compressed;
        using (var compressor = new ZstdSharp.Compressor(Level))
        {
            compressor.LoadDictionary(Dictionaries[CurrentDictionaryVersion]);
            compressed = compressor.Wrap(payload).ToArray();
        }

        var format = ZstdFormat(CurrentDictionaryVersion);
        // " clen=NNN" is new on the offer line, and "fmt=z1" is one byte
        // longer than "fmt=p".
        var headerCost = " clen=".Length + compressed.Length.ToString().Length + (format.Length - 1);
        var saving = payload.Length - compressed.Length - headerCost;
        return saving >= MinimumSaving ? (format, compressed) : null;
    }

    /// <summary>
    /// Decode a payload received as <paramref name="format"/>.
    /// <paramref name="length"/> is the offer's <c>len=</c>, the size of
    /// the original payload; decoding never produces more than that.
    /// Throws <see cref="InvalidDataException"/> for an unknown format,
    /// corrupt data, or a result that isn't exactly <paramref name="length"/>
    /// bytes.
    /// </summary>
    public static byte[] Decode(string format, byte[] wire, int length)
    {
        byte[] decoded;
        if (format == "p")
        {
            decoded = wire;
        }
        else if (format == "d")
        {
            decoded = InflateAtMost(wire, length);
        }
        else if (TryParseZstd(format, out var version) && Dictionaries.TryGetValue(version, out var dictionary))
        {
            try
            {
                using var decompressor = new ZstdSharp.Decompressor();
                decompressor.LoadDictionary(dictionary);
                decoded = decompressor.Unwrap(wire, maxDecompressedSize: length).ToArray();
            }
            catch (ZstdSharp.ZstdException ex)
            {
                throw new InvalidDataException($"fmt={format} payload does not decode: {ex.Message}", ex);
            }
        }
        else
        {
            throw new InvalidDataException($"unknown fmt={format}");
        }

        if (decoded.Length != length)
        {
            throw new InvalidDataException($"fmt={format} payload decoded to {decoded.Length} bytes, len= said {length}");
        }
        return decoded;
    }

    /// <summary>The bytes of dictionary <paramref name="version"/>, for
    /// the test that pins them. Null when this build doesn't hold it.</summary>
    internal static byte[]? DictionaryBytes(int version) => Dictionaries.GetValueOrDefault(version);

    private static bool TryParseZstd(string format, out int version)
    {
        version = 0;
        return format.Length > 1 && format[0] == 'z'
            && int.TryParse(format.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out version)
            && version > 0;
    }

    private static byte[] InflateAtMost(byte[] wire, int length)
    {
        using var input = new MemoryStream(wire);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        // One byte of headroom so an over-long result is caught as a
        // length mismatch rather than silently truncated.
        var buffer = new byte[length + 1];
        var total = 0;
        try
        {
            int n;
            while (total < buffer.Length && (n = inflater.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += n;
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"fmt=d payload does not decode: {ex.Message}", ex);
        }
        return buffer[..total];
    }

    private static byte[] LoadDictionary(string name)
    {
        using var stream = typeof(PayloadCompression).Assembly.GetManifestResourceStream("dapps.client.Compression." + name)
            ?? throw new InvalidOperationException($"compression dictionary {name} is not embedded in this build");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
