using System.Text.Json;
using System.Text.RegularExpressions;

namespace dapps.client;

/// <summary>
/// One step of a connect-script: a line to send (with carriage-return
/// terminator implicit on transmission, since the receiver is almost
/// always a BPQ-style packet node prompt that uses CR), and a substring
/// to wait for in the response stream before moving on.
///
/// <para>
/// Example: <c>new ConnectScriptStep("C G0NODE3", "Connected to G0NODE3", 30)</c> -
/// send <c>C G0NODE3</c>, wait up to 30 seconds for the substring
/// <c>Connected to G0NODE3</c> to appear in the inbound bytes, then
/// proceed to the next step.
/// </para>
///
/// <para>
/// <see cref="TimeoutSeconds"/> is per-step. Null falls back to the
/// runner default (30s). The final step that lands on
/// <c>DAPPSv1&gt;</c> may want a longer timeout because the application
/// command on the far-end node may take longer to dispatch than a
/// node-to-node connect.
/// </para>
/// </summary>
public sealed record ConnectScriptStep(string Send, string Expect, int? TimeoutSeconds = null);

/// <summary>
/// An ordered series of (send, expect) pairs the daemon plays before
/// falling into the DAPPSv1 protocol exchange. Used to reach a far-end
/// DAPPS node through a chain of intermediate packet nodes that are
/// not themselves DAPPS-aware (and may not run NET/ROM either) - the
/// operator types this chain by hand today; the script just automates
/// the same steps.
///
/// <para>
/// The script's final step MUST end with the expect string
/// <c>DAPPSv1&gt;</c> (the standard DAPPS prompt). After the runner
/// observes that substring, the stream is positioned just past the
/// prompt and ready for the normal <c>ihave</c>/<c>data</c>/<c>ack</c>
/// exchange - so the protocol client skips its own
/// <see cref="DappsProtocolClient.ReadInitialPromptAsync"/> when a
/// connect-script is in play.
/// </para>
/// </summary>
public sealed record ConnectScript(IReadOnlyList<ConnectScriptStep> Steps)
{
    /// <summary>The DAPPSv1 session prompt. Connect-scripts MUST end on
    /// this expect, since the protocol client takes over from there.</summary>
    public const string DappsPrompt = "DAPPSv1>";

    /// <summary>Default per-step timeout when a step doesn't specify one.</summary>
    public const int DefaultStepTimeoutSeconds = 30;

    /// <summary>Serialise to JSON for storage on
    /// <c>DbNeighbour.ConnectScriptJson</c>. Uses the default web
    /// JSON shape so the dashboard can round-trip the same payload.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse JSON written by <see cref="ToJson"/>. Returns null
    /// when the input is null/empty/whitespace - that's the no-script
    /// case, which is the common one.</summary>
    public static ConnectScript? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<ConnectScript>(json, JsonOptions);
    }

    /// <summary>True when the script's final step lands on the DAPPSv1
    /// prompt - the contract the runner relies on.</summary>
    public bool EndsOnDappsPrompt =>
        Steps.Count > 0 && Steps[^1].Expect.Contains(DappsPrompt, StringComparison.Ordinal);

    /// <summary>
    /// Parse a human-friendly multi-line text form: one step per line,
    /// pipe-separated as <c>SEND|EXPECT[|TIMEOUT_SECONDS]</c>. Blank
    /// lines and lines beginning with <c>#</c> (comments) are ignored.
    /// Returns null when the text is empty/whitespace - the no-script
    /// case. Throws <see cref="FormatException"/> on a malformed line.
    ///
    /// <para>
    /// Example input the dashboard accepts:
    /// </para>
    /// <code>
    /// C G0NODE2|Connected to G0NODE2
    /// C G0NODE3|Connected to G0NODE3
    /// DAPPS|DAPPSv1&gt;|60
    /// </code>
    /// </summary>
    public static ConnectScript? ParseLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var steps = new List<ConnectScriptStep>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|');
            if (parts.Length < 2 || parts.Length > 3)
            {
                throw new FormatException(
                    $"line {lineNumber}: expected 'send|expect' or 'send|expect|timeoutSeconds'; got '{line}'");
            }
            var send = parts[0];
            var expect = parts[1];
            int? timeout = null;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[2], out var t) || t <= 0)
                {
                    throw new FormatException(
                        $"line {lineNumber}: timeout must be a positive integer; got '{parts[2]}'");
                }
                timeout = t;
            }
            if (send.Length == 0 || expect.Length == 0)
            {
                throw new FormatException(
                    $"line {lineNumber}: send and expect must both be non-empty");
            }
            steps.Add(new ConnectScriptStep(send, expect, timeout));
        }
        return steps.Count == 0 ? null : new ConnectScript(steps);
    }

    /// <summary>
    /// Work out which callsign to dial and which steps are left to play.
    /// A script whose first step is a bare <c>C &lt;CALL&gt;</c> (no port
    /// number, e.g. <c>C GB7BDH|Connected</c>) names the first hop: the
    /// daemon dials that callsign itself, and the connect succeeding stands
    /// in for the step's expect (the bearer reports the connect out of
    /// band, not as text on the stream). Any other script is played over a
    /// connection to <paramref name="target"/>, as before.
    ///
    /// <para>
    /// The remaining script is null when the first step was the only one,
    /// so callers read the DAPPSv1&gt; prompt themselves.
    /// </para>
    /// </summary>
    public static (string DialCallsign, ConnectScript? Script) ResolveFirstHop(
        string target, ConnectScript? script)
    {
        if (script is null || script.Steps.Count == 0) return (target, script);
        var first = script.Steps[0];
        // A first step that already lands on the prompt has to be played,
        // or nothing would be left to consume it.
        if (first.Expect.Contains(DappsPrompt, StringComparison.Ordinal)) return (target, script);
        var match = FirstHopPattern.Match(first.Send.Trim());
        if (!match.Success) return (target, script);
        var rest = script.Steps.Skip(1).ToList();
        return (match.Groups[1].Value.ToUpperInvariant(), rest.Count == 0 ? null : new ConnectScript(rest));
    }

    // C or CONNECT followed by a single callsign with optional SSID. A
    // port number ("C 14 G0XYZ") means a connect from a node prompt, not
    // something the daemon can dial.
    private static readonly Regex FirstHopPattern = new(
        @"^C(?:ONNECT)?\s+([A-Z0-9]{1,6}(?:-(?:[0-9]|1[0-5]))?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reverse of <see cref="ParseLines"/> - render the script
    /// back out as the same human-friendly text form for the dashboard
    /// to populate the textarea.</summary>
    public string ToLines()
    {
        return string.Join('\n', Steps.Select(s =>
            s.TimeoutSeconds is { } t
                ? $"{s.Send}|{s.Expect}|{t}"
                : $"{s.Send}|{s.Expect}"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
