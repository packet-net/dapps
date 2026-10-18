using System.Globalization;
using System.Text;

namespace dapps.client.Discovery;

/// <summary>
/// Encodes and parses the beacon wire form. Plan B2 sketches a positional
/// line; we use the same KV style as <c>ihave</c> instead - both for
/// extensibility (future fields don't reorder existing parsers) and so
/// hand-typing or grepping captured AX.25 UI traffic stays trivial.
///
/// Wire form:
/// <code>
///   DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300 end=1
/// </code>
///
/// One line, no trailing newline. ASCII-only on the wire - callsigns
/// are ASCII anyway and the KV values are integers. The closing
/// <c>end=1</c> takes whatever a node appends after the data (see
/// <see cref="DiscoveryFields"/>); lines without it, from older
/// senders, are still read, their last field tolerantly.
///
/// The bearer-specific source hint (<see cref="BeaconFrame.Bearer"/>) is
/// NOT carried in the wire form - it's stamped by the receive bearer
/// based on the channel the beacon arrived on. Including it on the wire
/// would let a misbehaving peer claim to be reachable on routes it
/// isn't.
/// </summary>
public static class BeaconCodec
{
    public const string Magic = "DAPPS v1";

    /// <summary>The largest ttl read from an older sender's line, whose
    /// last field (ttl) may carry junk digits: 24 hours, the longest any
    /// link class advertises (<see cref="LinkClassDefaults.AdvertisedTtlSeconds"/>,
    /// HF). Lines that end with <c>end=1</c> are not capped.</summary>
    public const int MaxTolerantTtlSeconds = 86400;

    public static byte[] Encode(BeaconFrame beacon)
    {
        var line = $"{Magic} callsign={beacon.Callsign} hops={beacon.Hops} ttl={beacon.Ttl} {DiscoveryFields.End}";
        return Encoding.ASCII.GetBytes(line);
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, BeaconBearerHint bearer, out BeaconFrame? beacon)
    {
        beacon = null;
        if (payload.Length < Magic.Length) return false;

        var text = Encoding.ASCII.GetString(payload).TrimEnd('\r', '\n');
        if (!text.StartsWith(Magic + " ", StringComparison.Ordinal)) return false;
        if (!DiscoveryFields.TryRead(text[(Magic.Length + 1)..], out var fields, out var lastIsTolerant)) return false;

        string? callsign = null;
        int? hops = null;
        int? ttl = null;

        for (var i = 0; i < fields.Count; i++)
        {
            var (key, value) = fields[i];
            var tolerant = lastIsTolerant && i == fields.Count - 1;
            switch (key)
            {
                case "callsign":
                    if (!DiscoveryFields.TryCallsign(value, tolerant, out var c)) return false;
                    callsign = c;
                    break;
                case "hops":
                    if (!DiscoveryFields.TryInt(value, tolerant, int.MaxValue, out var h)) return false;
                    hops = h;
                    break;
                case "ttl":
                    if (!DiscoveryFields.TryInt(value, tolerant, MaxTolerantTtlSeconds, out var t) || t <= 0) return false;
                    ttl = t;
                    break;
                // Unknown keys ignored - forward-compat with future fields.
            }
        }

        if (callsign is null || hops is null || ttl is null) return false;
        if (callsign.Length == 0) return false;

        beacon = new BeaconFrame(callsign, hops.Value, ttl.Value, bearer);
        return true;
    }
}
