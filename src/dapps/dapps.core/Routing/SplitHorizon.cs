using dapps.core.Models;
using dapps.core.Services;

namespace dapps.core.Routing;

/// <summary>
/// Where a queued message came from, for the two rules that depend on
/// it (#295, #297): only the node that originated a message floods it,
/// and a message passing through is never sent back to the neighbour
/// it arrived from.
/// </summary>
internal static class SplitHorizon
{
    /// <summary>
    /// True for a message a local app submitted here, false for one that
    /// arrived from a neighbour. A local submission is stored with our own
    /// callsign as both its link source and its originator (or, from the
    /// legacy REST endpoint, with neither). A message from a neighbour has
    /// the neighbour as its link source. Both must agree: a sibling of ours
    /// calling through a BPQ node can arrive under our own callsign (the
    /// node inverts its SSID), and its originator is then the sibling, or
    /// blank when it sent no <c>src=</c>; either way, not ours.
    /// </summary>
    public static bool OriginatedHere(DbMessage message, string ourCallsign)
        => (string.IsNullOrWhiteSpace(message.SourceCallsign) && string.IsNullOrWhiteSpace(message.OriginatorCallsign))
            || (IsUs(message.SourceCallsign, ourCallsign) && IsUs(message.OriginatorCallsign, ourCallsign));

    /// <summary>The neighbour a message passing through arrived from, or
    /// null for one that originated here.</summary>
    public static string? CameFrom(DbMessage message, string ourCallsign)
        => OriginatedHere(message, ourCallsign) ? null : message.SourceCallsign;

    /// <summary>
    /// The next hops a message from <paramref name="cameFrom"/> must not
    /// go back to: the link source itself, the exact call+SSID (another
    /// SSID of the same callsign is another station, #242). And when no
    /// neighbour row has that exact callsign, the row whose SSID it is
    /// inverted from: a neighbour calling through a BPQ node arrives with
    /// its SSID turned into 15 - n, G0NA-1 as G0NA-14. Null
    /// <paramref name="cameFrom"/> (our own message) avoids nothing.
    /// </summary>
    public static Func<string, bool> Avoids(string? cameFrom, IEnumerable<DbNeighbour> neighbours)
    {
        if (string.IsNullOrWhiteSpace(cameFrom)) return _ => false;
        var rows = neighbours.ToList();
        var inverted = rows.Any(n => DestinationParser.SameStation(n.Callsign, cameFrom))
            ? null
            : rows.FirstOrDefault(n => CallsignMatch.SsidInverted(n.Callsign, cameFrom))?.Callsign;
        return nextHop => DestinationParser.SameStation(nextHop, cameFrom)
            || (inverted is not null && DestinationParser.SameStation(nextHop, inverted));
    }

    private static bool IsUs(string callsign, string ourCallsign)
        => !string.IsNullOrWhiteSpace(callsign) && DestinationParser.SameStation(callsign, ourCallsign);
}
