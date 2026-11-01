using dapps.client;

namespace dapps.core.Services;

/// <summary>
/// The rules for a neighbour's Dial as callsign (#431): the callsign DAPPS
/// calls that neighbour from, so that several neighbours reached through
/// one node each get their own AX.25 link and can have sessions open at
/// once. It must be an SSID of this node's own callsign, so the station
/// on air is still this one, and the exchange's <c>node=</c> (same base
/// callsign) tells a 0.56+ peer it is talking to this node.
/// </summary>
public static class DialAsCallsign
{
    /// <summary>
    /// <paramref name="value"/> as stored: null when blank or the same as
    /// <paramref name="ourCallsign"/> (the default), otherwise upper case.
    /// <paramref name="error"/> says why it can't be used, and the result
    /// is null then.
    /// </summary>
    public static string? Normalise(string? value, string? ourCallsign, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var dialAs = DestinationParser.StationKey(value);
        if (MessageFields.CallsignError(dialAs) is { } bad)
        {
            error = bad;
            return null;
        }
        if (string.IsNullOrWhiteSpace(ourCallsign)
            || string.Equals(ourCallsign.Trim(), DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            error = "set this node's callsign first; Dial as is an SSID of it.";
            return null;
        }
        var ours = DestinationParser.StationKey(ourCallsign);
        if (string.Equals(dialAs, ours, StringComparison.Ordinal)) return null;
        var baseCall = BaseOf(ours);
        if (!string.Equals(BaseOf(dialAs), baseCall, StringComparison.Ordinal))
        {
            error = $"must be an SSID of this node's callsign, such as {baseCall}-4.";
            return null;
        }
        if (!dialAs.Contains('-'))
        {
            error = $"{dialAs} with no SSID is usually your packet node's own callsign, and calling from it would disturb the node's own links; pick an SSID nothing else on the node uses.";
            return null;
        }
        return dialAs;
    }

    /// <summary>
    /// Why <paramref name="dialAs"/> can't be called from now, or null if
    /// it can: this node's callsign has changed since it was saved, so it
    /// is no longer an SSID of it. Nothing is sent from a callsign that
    /// isn't this station's.
    /// </summary>
    public static string? StillOurs(string dialAs, string ourCallsign) =>
        string.Equals(BaseOf(DestinationParser.StationKey(dialAs)), BaseOf(DestinationParser.StationKey(ourCallsign)), StringComparison.Ordinal)
            ? null
            : $"Dial as {dialAs} is not an SSID of this node's callsign {ourCallsign}; edit the neighbour";

    private static string BaseOf(string callsign) => callsign.Split('-')[0];
}
