using System.Text;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul;

/// <summary>
/// Plays a <see cref="ConnectScript"/> over a duplex byte stream:
/// for each step, write the send line + carriage return, read the
/// stream until the expected substring appears (or per-step timeout
/// elapses), and proceed. When the final step's expect lands on
/// <c>DAPPSv1&gt;</c>, the stream is positioned just past the prompt
/// and ready for the normal DAPPS protocol exchange.
///
/// <para>
/// Used by <see cref="Dappsv1SessionBackhaul"/> when a route's
/// <see cref="BackhaulRoute.ConnectScript"/> is set, to reach far-end
/// DAPPS nodes via a chain of intermediate non-DAPPS packet nodes
/// (the operator's manual "C node1 / C node2 / ... / DAPPS" sequence,
/// automated). Used the same way by the prober.
/// </para>
///
/// <para>
/// Carriage return (<c>\r</c>, 0x0D) is appended to each <c>Send</c>
/// because BPQ-derived node prompts treat CR as line-end. LF would
/// not advance the prompt on most node software.
/// </para>
/// </summary>
public static class ConnectScriptRunner
{
    /// <summary>
    /// Play the script. Returns the captured pre-script-end transcript
    /// (everything received during the script, useful for the test-now
    /// dashboard button) on success. Throws on timeout or stream EOF.
    /// </summary>
    public static async Task<string> RunAsync(
        Stream stream,
        ConnectScript script,
        ILogger? logger,
        CancellationToken ct)
    {
        // Nothing goes on the link unless every line is safe to type at a
        // node prompt. A script saved by an older DAPPS wasn't checked, so it can
        // still hold a CR that would make the node run a second command
        // (#309): refuse it, loudly, rather than send any of it.
        if (script.SendError() is { } refused)
        {
            logger?.LogWarning(
                "Connect-script refused, nothing sent: {0}. Fix the neighbour's connect script on the Topology page.",
                refused);
            throw new ConnectScriptException($"refused, nothing sent: {refused}");
        }

        var transcript = new StringBuilder();
        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            var stepTimeout = TimeSpan.FromSeconds(
                step.TimeoutSeconds ?? ConnectScript.DefaultStepTimeoutSeconds);

            var line = step.Send + "\r";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
            await stream.FlushAsync(ct);
            logger?.LogDebug("connect-script step {0}/{1}: sent {2}", i + 1, script.Steps.Count, step.Send);

            var observed = await ReadUntilSubstringAsync(stream, step.Expect, stepTimeout, transcript, ct);
            if (!observed)
            {
                throw new ConnectScriptException(
                    $"connect-script step {i + 1}/{script.Steps.Count}: " +
                    $"timed out waiting for '{step.Expect}' after {stepTimeout.TotalSeconds:F0}s. " +
                    $"Last 200 chars received: {Tail(transcript, 200)}");
            }
            logger?.LogDebug("connect-script step {0}/{1}: matched '{2}'", i + 1, script.Steps.Count, step.Expect);
        }
        return transcript.ToString();
    }

    /// <summary>
    /// Read from the stream byte-by-byte until <paramref name="expected"/>
    /// is observed in a sliding window of received bytes. Appends
    /// everything read to <paramref name="transcript"/> for diagnostics.
    /// Returns true on match; false on timeout. Throws <see cref="EndOfStreamException"/>
    /// on EOF before match (callers translate to script failure).
    /// </summary>
    private static async Task<bool> ReadUntilSubstringAsync(
        Stream stream, string expected, TimeSpan timeout, StringBuilder transcript, CancellationToken outer)
    {
        if (string.IsNullOrEmpty(expected))
        {
            // Empty expect would match immediately; treat as a config
            // error rather than silently accepting any byte (or none).
            throw new ArgumentException("connect-script step expect must be non-empty", nameof(expected));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        var buffer = new byte[1];
        // Keep a sliding window of the last `expected.Length` bytes so we
        // detect the substring without buffering the whole transcript.
        var window = new char[expected.Length];
        var windowFill = 0;

        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, cts.Token);
                }
                catch (OperationCanceledException) when (!outer.IsCancellationRequested)
                {
                    return false;
                }
                if (n == 0)
                {
                    throw new EndOfStreamException(
                        $"connect-script: stream closed while waiting for '{expected}'. Transcript: {Tail(transcript, 200)}");
                }
                var c = (char)buffer[0];
                transcript.Append(c);
                if (windowFill < expected.Length)
                {
                    window[windowFill++] = c;
                }
                else
                {
                    Array.Copy(window, 1, window, 0, expected.Length - 1);
                    window[expected.Length - 1] = c;
                }
                if (windowFill == expected.Length && new string(window) == expected)
                {
                    return true;
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw;
        }
    }

    private static string Tail(StringBuilder sb, int chars)
    {
        if (sb.Length <= chars) return sb.ToString();
        return sb.ToString(sb.Length - chars, chars);
    }
}

/// <summary>Thrown when a connect-script step times out or otherwise
/// fails. The session callsite catches and surfaces as a forward
/// failure.</summary>
public sealed class ConnectScriptException(string message) : Exception(message);
