using dapps.core.Models;
using dapps.core.Services;

namespace dapps.core.Routing;

/// <summary>
/// Picking the row for a callsign out of a neighbour, peer or session
/// list. The SSID is part of a station's identity (#239), so an exact
/// call+SSID match wins; failing that, a row under the same base
/// callsign stands in, as configured routes and destinations often
/// differ only in SSID. Never a row under our own base callsign, though:
/// a sibling of ours is no stand-in for another SSID of ours, and it
/// would only hand the mail straight back.
/// </summary>
internal static class CallsignMatch
{
    public static T? Best<T>(IEnumerable<T> rows, Func<T, string> callsign, string wanted, string ourCallsign) where T : class
    {
        var list = rows as IReadOnlyList<T> ?? rows.ToList();
        return list.FirstOrDefault(r => DestinationParser.SameStation(callsign(r), wanted))
            ?? list.FirstOrDefault(r => StandsIn(callsign(r), wanted, ourCallsign));
    }

    /// <summary>True when <paramref name="candidate"/> may stand in for
    /// <paramref name="wanted"/>: the same base callsign, and not ours.</summary>
    public static bool StandsIn(string candidate, string wanted, string ourCallsign)
        => SameBase(candidate, wanted) && !SameBase(candidate, ourCallsign);

    /// <summary>
    /// The route hint for <paramref name="destinationCallsign"/> (#242):
    /// one written for that exact call+SSID wins; failing that, one
    /// written with no SSID covers every SSID of its callsign, as hints
    /// always have; failing that, the "*" catch-all. Hints are the
    /// operator's own configuration, so they are read as written and
    /// never rewritten.
    /// </summary>
    public static DbRouteHint? RouteHint(IEnumerable<DbRouteHint> hints, string destinationCallsign)
    {
        var list = hints as IReadOnlyList<DbRouteHint> ?? hints.ToList();
        static bool Wildcard(DbRouteHint h) => h.Destination.Trim() == "*";
        return list.FirstOrDefault(h => !Wildcard(h) && DestinationParser.SameStation(h.Destination, destinationCallsign))
            ?? list.FirstOrDefault(h => !Wildcard(h) && !h.Destination.Contains('-') && SameBase(h.Destination, destinationCallsign))
            ?? list.FirstOrDefault(Wildcard);
    }

    /// <summary>
    /// True when <paramref name="linkSource"/> is how a BPQ node presents
    /// <paramref name="station"/> on a call it makes for it: the same
    /// callsign with the SSID inverted, 15 - n (G6FCI-8 as G6FCI-7). A
    /// link source like that is the station itself, a single hop, never
    /// a route to it through the station it happens to name.
    /// </summary>
    public static bool SsidInverted(string station, string linkSource)
        => SameBase(station, linkSource)
            && Ssid(station) is { } a && Ssid(linkSource) is { } b
            && a + b == 15;

    private static int? Ssid(string callsign)
    {
        var dash = callsign.IndexOf('-');
        if (dash < 0) return 0;
        return int.TryParse(callsign[(dash + 1)..].Trim(), out var n) && n is >= 0 and <= 15 ? n : null;
    }

    public static bool SameBase(string a, string b)
        => a.Split('-')[0].Trim().Equals(b.Split('-')[0].Trim(), StringComparison.OrdinalIgnoreCase);
}
