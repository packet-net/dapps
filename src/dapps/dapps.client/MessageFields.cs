using System.Text;

namespace dapps.client;

/// <summary>
/// The one set of rules for the fields an app hands the daemon (app name,
/// destination callsign, stream id) and for every field value that goes
/// on a DAPPSv1 header line. A header line is space-separated
/// <c>key=value</c> tokens ended by a line break, so a value holding a
/// space, CR, LF or other control character would split into extra keys
/// or extra commands on the far end (#313). Each check returns null when
/// the value is fine, or a sentence saying what is wrong with it.
/// </summary>
public static class MessageFields
{
    /// <summary>A stream id's limit in UTF-8 bytes: the datagram codec
    /// carries its length in one byte.</summary>
    public const int MaxStreamIdBytes = 255;

    /// <summary>An app name's limit in characters (all ASCII, so also
    /// bytes). The spec sets none, but a header line has to fit the
    /// receiver's line buffer (8 KiB in the reference daemon), and no
    /// real app needs more than a few words.</summary>
    public const int MaxAppNameLength = 64;

    /// <summary>
    /// An app name: plain printable ASCII, with no whitespace, control
    /// characters, '/', '+' or '#' (MQTT topic separator and wildcards,
    /// the same rule app tokens follow) and no '=' (it would end the key
    /// of the <c>dst=</c> token), and at most <see cref="MaxAppNameLength"/>
    /// characters. Case is not changed or checked; app
    /// names match case-insensitively everywhere.
    /// </summary>
    public static string? AppNameError(string? app)
    {
        if (string.IsNullOrEmpty(app)) return "App name is required.";
        if (app.Length > MaxAppNameLength) return $"App name can be at most {MaxAppNameLength} characters.";
        foreach (var c in app)
        {
            if (c <= ' ' || c > '~' || c is '/' or '+' or '#' or '=')
            {
                return "App name must be plain ASCII and can't contain '/', '+', '#', '=', whitespace, or control characters.";
            }
        }
        return null;
    }

    /// <summary>
    /// A station callsign: CALL or CALL-SSID, where CALL is 1 to 10
    /// ASCII letters and digits and SSID is 0 to 15, with no leading zero
    /// (G1AAA-07 would be a different station key from G1AAA-7). Either
    /// case.
    /// </summary>
    public static string? CallsignError(string? callsign)
    {
        const string Rule = "Callsign must be CALL or CALL-SSID: letters and digits, then an optional SSID from 0 to 15 with no leading zero.";
        if (string.IsNullOrEmpty(callsign)) return "Callsign is required.";
        var dash = callsign.IndexOf('-');
        var call = dash < 0 ? callsign : callsign[..dash];
        if (call.Length is 0 or > 10 || !call.All(char.IsAsciiLetterOrDigit)) return Rule;
        if (dash < 0) return null;
        var ssid = callsign[(dash + 1)..];
        if (ssid.Length is 0 or > 2 || !ssid.All(char.IsAsciiDigit) || (ssid.Length == 2 && ssid[0] == '0')
            || int.Parse(ssid) > 15) return Rule;
        return null;
    }

    /// <summary>A destination, <c>app@CALL[-SSID]</c>: the callsign is
    /// what follows the last '@' (as the queue reads it), and what comes
    /// before it is the app name.</summary>
    public static string? DestinationError(string? destination)
    {
        if (string.IsNullOrEmpty(destination)) return "Destination is required.";
        var at = destination.LastIndexOf('@');
        if (at < 0) return "Destination must be app@CALL or app@CALL-SSID.";
        return AppNameError(destination[..at]) ?? CallsignError(destination[(at + 1)..]);
    }

    /// <summary>Why a stream id without a gap policy is refused: only the
    /// app knows whether waiting or skipping is right, so there is no
    /// default (#401).</summary>
    public const string StreamGapTimeoutRequired =
        "required with a stream id: 0 to wait for a missing message however long it takes, or the number of seconds to wait before skipping it.";

    /// <summary>A stream id: UTF-8, with no whitespace, control
    /// characters or '=', and at most <see cref="MaxStreamIdBytes"/>
    /// bytes.</summary>
    public static string? StreamIdError(string? streamId)
    {
        if (string.IsNullOrEmpty(streamId)) return "Stream id, when supplied, must be non-empty.";
        if (!IsWireSafe(streamId) || streamId.Contains('='))
        {
            return "Stream id can't contain '=', whitespace, or control characters.";
        }
        if (Encoding.UTF8.GetByteCount(streamId) > MaxStreamIdBytes)
        {
            return $"Stream id exceeds {MaxStreamIdBytes} bytes.";
        }
        return null;
    }

    /// <summary>True when <paramref name="value"/> can stand as one value
    /// on a header line: no whitespace (a space would start a new
    /// token, CR or LF a new line) and no control characters. Empty is
    /// safe here; whether a field may be empty is the caller's rule.</summary>
    public static bool IsWireSafe(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        return true;
    }
}
