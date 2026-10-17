using System.Text.RegularExpressions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Counting what went over the air between two DAPPS callsigns, from an
/// <see cref="IAirMonitor.Sequence"/>, so that AX.25 recovering on a loaded
/// host isn't mistaken for DAPPS saying something twice (#352), and nothing
/// DAPPS really did twice is folded away.
/// </summary>
internal static partial class AirFrames
{
    /// <summary>
    /// The frames <paramref name="side"/> sent, each once. An I-frame counts
    /// again only when it is new: one with the same N(S) and the same text
    /// as an earlier frame on the same connection, sent before the other end
    /// acknowledged that earlier frame, is its retransmission and doesn't
    /// count. Every connect or disconnect on the link (SABM, UA, DISC, DM,
    /// either way) starts afresh, as N(S) does, so the same frame on a
    /// second connection counts twice. Frames that aren't between the two
    /// callsigns (a node's own NET/ROM traffic) are kept as they are.
    /// </summary>
    public static IReadOnlyList<string> SentBy(
        IReadOnlyList<(string From, string Text)> sequence, string side, string callA, string callB)
    {
        string Call(string s) => s == "A" ? callA : callB;
        string Other(string s) => s == "A" ? "B" : "A";

        // The other end's acknowledgements, unwrapped: how many of this
        // side's I-frames it has acknowledged on this connection.
        var acked = 0;
        // This side's I-frames on this connection not yet acknowledged when
        // last sent, by N(S) and text: the acknowledgement count that covers each.
        var outstanding = new Dictionary<string, int>();
        var frames = new List<string>();

        foreach (var (from, text) in sequence)
        {
            var onLink = text.Contains($"Fm {Call(from)} To {Call(Other(from))} ", StringComparison.Ordinal);
            if (!onLink)
            {
                if (from == side) frames.Add(text);
                continue;
            }
            if (LinkReset().IsMatch(text))
            {
                acked = 0;
                outstanding.Clear();
                if (from == side) frames.Add(text);
                continue;
            }

            if (from != side)
            {
                if (ReceiveSequence().Match(text) is { Success: true } nr)
                {
                    var r = int.Parse(nr.Groups["nr"].Value);
                    acked += ((r - acked) % 8 + 8) % 8;
                }
                continue;
            }

            if (IFrame().Match(text) is { Success: true } i)
            {
                var ns = int.Parse(i.Groups["ns"].Value);
                var key = ns + "|" + IFrame().Replace(text, "");
                if (outstanding.TryGetValue(key, out var coveredAt) && acked < coveredAt) continue; // a retransmission
                // Covered once the other end has acknowledged up to and
                // including this N(S): at most 7 frames are ever outstanding.
                outstanding[key] = acked + ((ns - acked) % 8 + 8) % 8 + 1;
            }
            frames.Add(text);
        }
        return frames;
    }

    /// <summary>
    /// Connects (SABM) from <paramref name="side"/> to the other DAPPS
    /// callsign. A SABM sent again with no frame either way on that link
    /// since the last one is the same connect retried, not a second dial;
    /// a SABM after the other end answered (UA, DM or anything else)
    /// counts.
    /// </summary>
    public static int Connects(IReadOnlyList<(string From, string Text)> sequence, string side, string callA, string callB)
    {
        string Call(string s) => s == "A" ? callA : callB;
        string Other(string s) => s == "A" ? "B" : "A";
        var sabm = $"Fm {Call(side)} To {Call(Other(side))} <C C";
        var count = 0;
        var lastWasOurSabm = false;
        foreach (var (from, text) in sequence)
        {
            if (!text.Contains($"Fm {Call(from)} To {Call(Other(from))} ", StringComparison.Ordinal)) continue; // not this link
            var isOurSabm = from == side && text.Contains(sabm, StringComparison.Ordinal);
            if (isOurSabm && !lastWasOurSabm) count++;
            lastWasOurSabm = isOurSabm;
        }
        return count;
    }

    /// <summary>An I-frame's header as either node's monitor writes it, BPQ's
    /// <c>&lt;I C S3 R2&gt;[12:00:00]</c> or pdn's with milliseconds: what can
    /// differ between a frame and its retransmission is N(R), the poll bit
    /// and the time.</summary>
    [GeneratedRegex(@"<I(?: [CR])?(?: [PF])? S(?<ns>\d+) R\d+>(?:\[[\d:.]+\])?")]
    private static partial Regex IFrame();

    /// <summary>N(R) in an I-frame or a supervisory frame (RR, RNR, REJ, SREJ).</summary>
    [GeneratedRegex(@"<(?:I|RR|RNR|REJ|SREJ)\b[^>]*? R(?<nr>\d+)>")]
    private static partial Regex ReceiveSequence();

    /// <summary>A connect, its answer, a disconnect or a refusal: <c>&lt;C</c>
    /// (SABM or SABME), <c>&lt;UA</c>, <c>&lt;D</c> (DISC), <c>&lt;DM</c>.</summary>
    [GeneratedRegex(@"<(?:C|UA|D|DM)[ >]")]
    private static partial Regex LinkReset();
}
