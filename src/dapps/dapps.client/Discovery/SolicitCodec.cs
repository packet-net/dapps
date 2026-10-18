using System.Text;

namespace dapps.client.Discovery;

/// <summary>
/// Encodes and parses the solicit wire form. Same KV style as
/// <see cref="BeaconCodec"/> - both for extensibility and so an
/// operator monitoring AX.25 UI traffic can read it. Distinguished
/// from a beacon by the literal <c>solicit</c> keyword in the
/// fixed prefix.
///
/// Wire form:
/// <code>
///   DAPPS v1 solicit callsign=M0LTE-9 end=1
/// </code>
///
/// One line, no trailing newline. ASCII-only. The closing
/// <c>end=1</c> takes whatever a node appends after the data (see
/// <see cref="DiscoveryFields"/>); lines without it, from older
/// senders, are still read, their last field tolerantly. The asker's callsign
/// is stamped on the wire - receivers use it for logging only;
/// replies always go to the channel's broadcast address (the same
/// place beacons go), not unicast back to the asker.
/// </summary>
public static class SolicitCodec
{
    public const string Magic = "DAPPS v1 solicit";

    public static byte[] Encode(SolicitFrame solicit)
    {
        var line = $"{Magic} callsign={solicit.Callsign} {DiscoveryFields.End}";
        return Encoding.ASCII.GetBytes(line);
    }

    /// <summary>
    /// Parse a solicit. Returns false if the payload doesn't lead
    /// with the solicit magic - beacons (which lead with the shorter
    /// <c>"DAPPS v1"</c> magic but have no <c>solicit</c> keyword)
    /// hit this case.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SolicitFrame? solicit)
    {
        solicit = null;
        if (payload.Length < Magic.Length) return false;

        var text = Encoding.ASCII.GetString(payload).TrimEnd('\r', '\n');
        if (!text.StartsWith(Magic + " ", StringComparison.Ordinal)
            && !text.Equals(Magic, StringComparison.Ordinal)) return false;

        // Strict byte-for-byte: beacon parser sees "DAPPS v1 callsign="
        // and would otherwise grab a "solicit" KV field as a callsign.
        // Anchoring on "DAPPS v1 solicit " keeps the two unambiguous.
        var rest = text.Length == Magic.Length ? "" : text[(Magic.Length + 1)..];
        if (!DiscoveryFields.TryRead(rest, out var fields, out var lastIsTolerant)) return false;

        string? callsign = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var (key, value) = fields[i];
            if (key != "callsign") continue; // Unknown keys ignored - forward-compat.
            if (!DiscoveryFields.TryCallsign(value, lastIsTolerant && i == fields.Count - 1, out var c)) return false;
            callsign = c;
        }

        if (string.IsNullOrEmpty(callsign)) return false;

        solicit = new SolicitFrame(callsign);
        return true;
    }
}
