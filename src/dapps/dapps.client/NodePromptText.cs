namespace dapps.client;

/// <summary>
/// What DAPPS may type at a packet node's command prompt (#309): the
/// node-prompt application command, and each line a connect-script
/// sends. Both go out over the air under this station's callsign,
/// followed by a CR, so a CR or LF inside one would make the node run a
/// second command nobody configured. Control characters and anything
/// outside printable ASCII are refused for the same reason.
/// </summary>
public static class NodePromptText
{
    /// <summary>The longest application command accepted. BPQ's
    /// <c>APPLICATION</c> names are shorter than this.</summary>
    public const int MaxApplicationCommandLength = 16;

    /// <summary>The longest connect-script line accepted, comfortably
    /// more than a node's longest connect command.</summary>
    public const int MaxSendLineLength = 128;

    /// <summary>Why <paramref name="command"/> can't be typed at a node
    /// prompt as the application command, or null when it can: a
    /// single word of letters, digits, '-' or '_'.</summary>
    public static string? ApplicationCommandError(string? command)
    {
        if (string.IsNullOrEmpty(command)) return "Application command is required.";
        if (command.Length > MaxApplicationCommandLength
            || !command.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return $"Application command must be one word of letters, digits, '-' or '_', at most {MaxApplicationCommandLength} characters (the name on the node's APPLICATION line).";
        }
        return null;
    }

    /// <summary>Why <paramref name="line"/> can't be sent as one
    /// connect-script line, or null when it can: printable ASCII only
    /// (spaces allowed), no control characters, at most
    /// <see cref="MaxSendLineLength"/> characters.</summary>
    public static string? SendLineError(string? line)
    {
        if (string.IsNullOrEmpty(line)) return "send must be non-empty.";
        if (line.Length > MaxSendLineLength) return $"send is longer than {MaxSendLineLength} characters.";
        if (!line.All(c => c is >= ' ' and <= '~'))
        {
            return "send can only hold printable ASCII: no control characters (such as a carriage return, which would end the line and start another command) and no characters outside ASCII.";
        }
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> warned = new(StringComparer.Ordinal);

    /// <summary>True the first time this process sees <paramref name="key"/>,
    /// false after: a refused value is logged once, not on every poll,
    /// probe or forwarder run that refuses it again.</summary>
    public static bool FirstTime(string key) => warned.TryAdd(key, 0);

    /// <summary><paramref name="text"/> with control characters written
    /// out as <c>\xNN</c>, for a log line: a raw CR would hide the rest of
    /// the line, and journalctl shows the whole line as blob data.</summary>
    public static string Printable(string? text) =>
        string.Concat((text ?? "").Select(c => char.IsControl(c) || c > '~' ? $"\\x{(int)c:X2}" : c.ToString()));
}
