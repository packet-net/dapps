namespace dapps.core.Services;

/// <summary>
/// Helpers for the `app@callsign` destination format.
/// </summary>
public static class DestinationParser
{
    public static (string app, string callsign) Parse(string destination)
    {
        var at = destination.IndexOf('@');
        if (at < 0) return ("", "");
        return (destination[..at], destination[(at + 1)..]);
    }

    /// <summary>
    /// True when the destination's callsign is our own, SSID included:
    /// `app@G6FCI-8` is not local to G6FCI-7, so two nodes under one
    /// callsign can mail each other (#239). Case-insensitive, and a bare
    /// callsign is SSID 0, as in AX.25.
    /// </summary>
    public static bool IsLocal(string destination, string localCallsign)
    {
        // The callsign follows the last '@', as the queue's SQL patterns
        // (LocalLikePatterns) read it, so the two always agree.
        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1) return false;
        return SameStation(destination[(at + 1)..], localCallsign.Trim());
    }

    /// <summary>True when two callsigns name the same station: same call,
    /// same SSID, ignoring case, with no SSID meaning SSID 0.</summary>
    public static bool SameStation(string a, string b)
        => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The SQL LIKE patterns (escaped with '\') that match a destination
    /// for <paramref name="app"/> (or any app, when null) at our own
    /// callsign. Two patterns, because SSID 0 can be written with or
    /// without the "-0"; for any other SSID both are the same. SQLite's
    /// LIKE is case-insensitive for ASCII, as callsigns are.
    /// </summary>
    public static (string, string) LocalLikePatterns(string localCallsign, string? app = null)
    {
        var call = EscapeLike(Normalise(localCallsign.Trim()));
        var prefix = app is null ? "%@" : EscapeLike(app) + "@";
        return call.Contains('-')
            ? (prefix + call, prefix + call)
            : (prefix + call, prefix + call + "-0");
    }

    private static string Normalise(string callsign)
        => callsign.EndsWith("-0", StringComparison.Ordinal) ? callsign[..^2] : callsign;

    private static string EscapeLike(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
