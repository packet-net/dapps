using System.Globalization;
using System.Text.RegularExpressions;

namespace dapps.client.Discovery;

/// <summary>
/// The <c>key=value</c> fields of a beacon or solicit line, shared by
/// <see cref="BeaconCodec"/> and <see cref="SolicitCodec"/>.
///
/// Senders end every line with <c>end=1</c>. Anything after the
/// <c>end</c> field, in the same token or after it, is ignored: 64-bit
/// LinBPQ's AGW monitor hands every UI frame over with 4 stale bytes
/// after its data (AGWMoncode.c assumes a 23-byte buffer header, which
/// is 27 bytes on 64-bit; M0LTE/linbpq#75), and they land there. Older
/// parsers ignore <c>end</c> as an unknown key.
///
/// Lines from older senders have no <c>end</c>, so the junk lands on
/// their last field. That one field alone is read tolerantly
/// (<see cref="Tolerant"/>): a number takes its leading digits, a
/// callsign the leading run of callsign characters, which must then be a
/// whole callsign. Every other field is read as strictly as before.
/// </summary>
internal static class DiscoveryFields
{
    /// <summary>The field every line ends with.</summary>
    public const string End = "end=1";

    /// <summary>
    /// The fields of <paramref name="rest"/> (the line after its magic), in
    /// order, up to the <c>end</c> field. False if a field before that
    /// is not <c>key=value</c>. <paramref name="lastIsTolerant"/> is true
    /// when there was no <c>end</c>: the last field may then carry junk.
    /// </summary>
    public static bool TryRead(string rest, out List<(string Key, string Value)> fields, out bool lastIsTolerant)
    {
        fields = [];
        lastIsTolerant = true;
        foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("end=", StringComparison.Ordinal))
            {
                lastIsTolerant = false;
                return true;
            }
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) return false;
            fields.Add((token[..eq], token[(eq + 1)..]));
        }
        return true;
    }

    /// <summary>A non-negative integer field. When <paramref name="tolerant"/>,
    /// the leading digits of the value, at least one, at most
    /// <paramref name="max"/>; otherwise the whole value.</summary>
    public static bool TryInt(string value, bool tolerant, int max, out int result)
    {
        result = 0;
        if (tolerant)
        {
            var digits = 0;
            while (digits < value.Length && char.IsAsciiDigit(value[digits])) digits++;
            if (digits == 0) return false;
            value = value[..digits];
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var wide)) wide = long.MaxValue;
            result = (int)Math.Min(wide, max);
            return true;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>A callsign field. When <paramref name="tolerant"/>, the
    /// leading run of callsign characters (letters, digits, '-'), which
    /// must be a whole callsign with an optional SSID of 0 to 15;
    /// otherwise the value as it stands, as before.</summary>
    public static bool TryCallsign(string value, bool tolerant, out string callsign)
    {
        callsign = value;
        if (!tolerant) return value.Length > 0;
        var run = 0;
        while (run < value.Length && (char.IsAsciiLetterOrDigit(value[run]) || value[run] == '-')) run++;
        callsign = value[..run];
        return Callsign.IsMatch(callsign);
    }

    // As ConnectScript's first-hop callsign: 1 to 6 letters or digits,
    // then an optional SSID of 0 to 15.
    private static readonly Regex Callsign = new(
        @"^[A-Z0-9]{1,6}(?:-(?:[0-9]|1[0-5]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
