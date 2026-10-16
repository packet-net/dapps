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
    /// callsign (or, from the legacy REST endpoint, nothing) as both its
    /// link source and its originator. A message from a neighbour has the
    /// neighbour as its link source. The originator is checked as well
    /// because a sibling of ours calling through a BPQ node can arrive
    /// under our own callsign (the node inverts its SSID); its originator
    /// is then the sibling, not us.
    /// </summary>
    public static bool OriginatedHere(DbMessage message, string ourCallsign)
        => IsUsOrUnset(message.SourceCallsign, ourCallsign)
            && IsUsOrUnset(message.OriginatorCallsign, ourCallsign);

    /// <summary>The neighbour a message passing through arrived from, or
    /// null for one that originated here.</summary>
    public static string? CameFrom(DbMessage message, string ourCallsign)
        => OriginatedHere(message, ourCallsign) ? null : message.SourceCallsign;

    /// <summary>True when sending <paramref name="message"/> to
    /// <paramref name="nextHop"/> would send it straight back where it
    /// came from. The exact call+SSID: another SSID of the same callsign
    /// is another station (#242).</summary>
    public static bool GoesBack(DbMessage message, string nextHop, string ourCallsign)
        => CameFrom(message, ourCallsign) is { } from && DestinationParser.SameStation(nextHop, from);

    private static bool IsUsOrUnset(string callsign, string ourCallsign)
        => string.IsNullOrWhiteSpace(callsign) || DestinationParser.SameStation(callsign, ourCallsign);
}
