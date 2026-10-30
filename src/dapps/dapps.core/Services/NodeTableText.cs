using System.Text.RegularExpressions;

namespace dapps.core.Services;

/// <summary>One destination in a node's NODES list: its alias, if it has
/// one, and its callsign.</summary>
public sealed record NodeListEntry(string? Alias, string Callsign)
{
    public override string ToString() => Alias is { Length: > 0 } ? $"{Alias}:{Callsign}" : Callsign;
}

/// <summary>
/// Reads what a node prints for NODES and LINKS, loosely: the formats
/// differ between LinBPQ and XRouter (and between versions), so this
/// looks for callsigns in the text rather than for a layout.
///
/// NODES: every <c>ALIAS:CALL</c> pair anywhere in the text, several to a
/// line or one, plus bare callsigns (LinBPQ prints a node with no alias as
/// its call alone). The prompt header (<c>LOC:G9LOC} Nodes</c> on LinBPQ,
/// <c>G9LOC:LOC} Nodes:</c> on XRouter) is left out: its last pair ends in
/// <c>}</c>.
///
/// LINKS: the lines that name one of the given callsigns as a word of
/// their own.
/// </summary>
public static partial class NodeTableText
{
    [GeneratedRegex(@"^(?<alias>[^:\s}]{1,9}):(?<call>[A-Za-z0-9]{1,6}(?:-\d{1,2})?)$")]
    private static partial Regex AliasCall();

    // A callsign: letters and digits with at least one of each, and an SSID maybe.
    [GeneratedRegex(@"^(?=[A-Za-z0-9]*[0-9])(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]{3,6}(?:-\d{1,2})?$")]
    private static partial Regex BareCall();

    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n', '\0'];

    /// <summary>The destinations in a NODES reply, in the order printed.</summary>
    public static IReadOnlyList<NodeListEntry> ParseNodes(string text)
    {
        var found = new List<NodeListEntry>();
        foreach (var token in text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = AliasCall().Match(token);
            if (m.Success)
            {
                found.Add(new NodeListEntry(m.Groups["alias"].Value.ToUpperInvariant(), m.Groups["call"].Value.ToUpperInvariant()));
            }
            else if (BareCall().IsMatch(token))
            {
                found.Add(new NodeListEntry(null, token.ToUpperInvariant()));
            }
        }
        return found;
    }

    /// <summary>The entry <paramref name="via"/> names, by alias or by
    /// callsign (no SSID matching -0), or null.</summary>
    public static NodeListEntry? Find(IReadOnlyList<NodeListEntry> nodes, string via)
    {
        var wanted = via.Trim();
        return nodes.FirstOrDefault(n => SameCall(n.Callsign, wanted))
            ?? nodes.FirstOrDefault(n => n.Alias is not null && string.Equals(n.Alias, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The lines of a LINKS reply that name any of
    /// <paramref name="calls"/> as a word, with runs of spaces closed up.</summary>
    public static IReadOnlyList<string> LinesNaming(string text, IEnumerable<string> calls)
    {
        var wanted = calls.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var lines = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = line.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(w => wanted.Any(c => SameCall(w, c)))) lines.Add(string.Join(' ', words));
        }
        return lines;
    }

    /// <summary>Two callsigns the same, case aside, with no SSID the same as -0.</summary>
    public static bool SameCall(string a, string b) =>
        string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string call)
    {
        var c = call.Trim();
        return c.EndsWith("-0", StringComparison.Ordinal) ? c[..^2] : c;
    }
}
