using System.Globalization;
using System.Text;

namespace dapps.client.Backhaul;

/// <summary>
/// This node's settings for an exchange session with one peer, from the
/// operator's configuration for that neighbour.
/// </summary>
/// <param name="HoldSeconds">How long to keep a quiet link up
/// (<c>hold=</c>); the session holds for the lower of both ends' values.</param>
/// <param name="Compress">Whether what we send may go compressed, when
/// the peer holds our dictionary and it saves bytes.</param>
/// <param name="MaxBytes">Largest message we take (<c>max=</c>); null for
/// no limit.</param>
/// <param name="Inline">Largest on-air payload we take unasked as
/// <c>msg</c> (<c>inline=</c>).</param>
public sealed record ExchangeSettings(int HoldSeconds = 0, bool Compress = false, int? MaxBytes = null, int Inline = ExchangeSettings.DefaultInline)
{
    /// <summary>Most messages on a packet link fit in this, and BPQ
    /// carries it in one AGW frame.</summary>
    public const int DefaultInline = 256;
}

/// <summary>
/// The rules a node states in its <c>exchange</c> line: what it will
/// receive, how long it will hold a quiet link, and its session tag.
/// Anything a line leaves out takes the value that asks the least of
/// the other end: no hold, offer everything first, plain only.
/// </summary>
public sealed record ExchangeRules(string Tag, int HoldSeconds, int Inline, int? MaxBytes, IReadOnlyList<int> Dictionaries)
{
    public const string Verb = "exchange";

    /// <summary>A fresh session tag: random, so a peer can tell a new
    /// session object from the one it was talking to.</summary>
    public static string NewTag() => Random.Shared.Next(0x1000000).ToString("x6", CultureInfo.InvariantCulture);

    /// <summary>The <c>exchange</c> line, ending in <c>\n</c>.</summary>
    public string ToLine()
    {
        var sb = new StringBuilder(Verb);
        sb.Append(" id=").Append(Tag);
        sb.Append(" hold=").Append(HoldSeconds.ToString(CultureInfo.InvariantCulture));
        sb.Append(" inline=").Append(Inline.ToString(CultureInfo.InvariantCulture));
        if (MaxBytes is { } max) sb.Append(" max=").Append(max.ToString(CultureInfo.InvariantCulture));
        if (Dictionaries.Count > 0) sb.Append(" z=").Append(string.Join(',', Dictionaries));
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Reads an <c>exchange</c> line. Unknown keys are ignored,
    /// and a value that doesn't parse counts as left out.</summary>
    public static ExchangeRules Parse(string line)
    {
        var tag = "";
        var hold = 0;
        var inline = 0;
        int? max = null;
        var dictionaries = new List<int>();
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var value = token[(eq + 1)..];
            switch (token[..eq])
            {
                case "id": tag = value; break;
                case "hold": hold = NonNegative(value) ?? 0; break;
                case "inline": inline = NonNegative(value) ?? 0; break;
                case "max": max = NonNegative(value); break;
                case "z":
                    foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (NonNegative(v) is { } version) dictionaries.Add(version);
                    }
                    break;
            }
        }
        return new ExchangeRules(tag, hold, inline, max, dictionaries);
    }

    private static int? NonNegative(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
