using dapps.core.Services;

namespace dapps.core.Routing;

/// <summary>
/// Picking the row for a callsign out of a neighbour or peer list. The
/// SSID is part of a station's identity (#239), so an exact call+SSID
/// match wins; failing that, a row under the same base callsign stands
/// in, as configured routes and destinations often differ only in SSID.
/// </summary>
internal static class CallsignMatch
{
    public static T? Best<T>(IEnumerable<T> rows, Func<T, string> callsign, string wanted) where T : class
    {
        var list = rows as IReadOnlyList<T> ?? rows.ToList();
        return list.FirstOrDefault(r => DestinationParser.SameStation(callsign(r), wanted))
            ?? list.FirstOrDefault(r => SameBase(callsign(r), wanted));
    }

    public static bool SameBase(string a, string b)
        => a.Split('-')[0].Equals(b.Split('-')[0], StringComparison.OrdinalIgnoreCase);
}
