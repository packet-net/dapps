using System.Text;
using dapps.client.Compression;
using Microsoft.Extensions.Logging;

namespace dapps.client;

/// <summary>
/// The command-and-response side of DAPPSv1 over a duplex byte stream,
/// agnostic of how that stream is plumbed: read the prompt, ask for
/// <c>peers</c> or <c>routes</c>, push one message with <c>ihave</c> /
/// <c>data</c>, say <c>quit</c>. Probes and simple senders use it. Traffic
/// between DAPPS nodes goes by <see cref="Backhaul.ExchangeSession"/>.
///
/// The answering node sends its <c>exchange</c> line straight after the
/// prompt; the replies read here skip it.
/// </summary>
public class DappsProtocolClient(Stream stream, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<DappsProtocolClient>();

    private const string PromptText = "DAPPSv1>";
    private const int PromptScanCapBytes = 256;

    /// <summary>Per-read inactivity timeout. Mirrors the receiver-side
    /// budget in <c>InboundConnectionHandler</c> (3 minutes, matching the
    /// AX.25 T3 default) so a hung peer can't wedge a forwarder run
    /// indefinitely. Plan A3.</summary>
    public static TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>What <see cref="ReadInitialPromptAsync(CancellationToken, TimeSpan)"/> found.</summary>
    public enum PromptOutcome
    {
        /// <summary>The prompt arrived; the session is ready for a command.</summary>
        Seen,
        /// <summary>Bytes arrived but no prompt among them (EOF, or the scan
        /// cap): whatever answered is not a DAPPS session.</summary>
        NotSeen,
        /// <summary>Not a single byte arrived within the silence budget. On
        /// a link that has just come up between two DAPPS neighbours this
        /// is the crossed-connect signature (#178): both ends dialled, so
        /// both are callers and neither sends the prompt.</summary>
        Silent,
    }

    /// <summary>
    /// <see cref="ReadInitialPromptAsync(CancellationToken, TimeSpan)"/>
    /// with the ordinary per-read budget; total silence surfaces as the
    /// same <see cref="TimeoutException"/> as any other stalled read.
    /// </summary>
    public async Task<bool> ReadInitialPromptAsync(CancellationToken ct)
    {
        var outcome = await ReadInitialPromptAsync(ct, InactivityTimeout);
        if (outcome == PromptOutcome.Silent)
        {
            throw new TimeoutException(
                $"DAPPS sender: no data from peer within {InactivityTimeout.TotalSeconds:F0}s");
        }
        return outcome == PromptOutcome.Seen;
    }

    /// <summary>
    /// Reads from the stream until either the DAPPSv1 prompt is seen or we
    /// exceed PromptScanCapBytes (which is enough to absorb a typical noisy
    /// connect-banner from a misbehaving node without becoming a DoS sink).
    ///
    /// We match on <c>"DAPPSv1>"</c> followed by *any* line terminator
    /// (<c>\n</c>, <c>\r</c>, or <c>\r\n</c>) - BPQ's Telnet driver, when
    /// bridging an inbound L2 session via Apps Interface, rewrites LF → CR
    /// in the app-to-user direction (apps-interface.md "App → user"
    /// section). Strict <c>\n</c>-only matching would hang every time
    /// dapps is reached over that bridge.
    ///
    /// <paramref name="silenceBudget"/> bounds the wait for the first byte
    /// only; once the peer has said anything at all the ordinary
    /// <see cref="InactivityTimeout"/> applies to each read, so a prompt
    /// that started to arrive is never cut off part-way.
    /// </summary>
    public async Task<PromptOutcome> ReadInitialPromptAsync(CancellationToken ct, TimeSpan silenceBudget)
    {
        var promptBytes = Encoding.UTF8.GetBytes(PromptText);
        var seen = new List<byte>();
        var oneByte = new byte[1];
        var promptSeen = false;

        while (seen.Count < PromptScanCapBytes)
        {
            int n;
            if (seen.Count == 0)
            {
                try
                {
                    n = await ReadWithTimeoutAsync(oneByte, ct, silenceBudget);
                }
                catch (TimeoutException)
                {
                    logger.LogInformation("Nothing at all from peer within {0:F0}s of connecting", silenceBudget.TotalSeconds);
                    return PromptOutcome.Silent;
                }
            }
            else
            {
                n = await ReadWithTimeoutAsync(oneByte, ct);
            }

            if (n == 0)
            {
                logger.LogWarning("EOF before DAPPSv1> prompt (got {0} bytes)", seen.Count);
                return PromptOutcome.NotSeen;
            }
            seen.Add(oneByte[0]);

            if (!promptSeen
                && seen.Count >= promptBytes.Length
                && seen.GetRange(seen.Count - promptBytes.Length, promptBytes.Length).SequenceEqual(promptBytes))
            {
                promptSeen = true;
                continue;
            }

            if (promptSeen && (oneByte[0] == (byte)'\n' || oneByte[0] == (byte)'\r'))
            {
                return PromptOutcome.Seen;
            }
        }

        logger.LogWarning("DAPPSv1> prompt not seen in first {0} bytes", PromptScanCapBytes);
        return PromptOutcome.NotSeen;
    }

    /// <summary>
    /// Tell the peer we're done with the session, and wait for its
    /// <c>bye</c> (or the link closing), so the <c>quit</c> is known to
    /// have gone before the caller hangs up.
    /// </summary>
    public async Task QuitAsync(CancellationToken ct)
    {
        await stream.WriteAsync("quit\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line.Length == 0 || line == "bye") return;
        }
    }

    /// <summary>
    /// Sends a plain (<c>fmt=p</c>) `ihave` line and waits for
    /// `send &lt;id&gt;`. Returns true on acceptance.
    /// </summary>
    public async Task<bool> OfferMessageAsync(
        string id,
        long? salt,
        DappsMessage.MessageFormat format,
        string destination,
        int length,
        CancellationToken ct,
        int? ttl = null,
        string? originator = null,
        string? masterId = null,
        int? fragmentIndex = null,
        int? fragmentTotal = null,
        string? streamId = null,
        uint? streamSeq = null,
        uint? streamGapTimeoutSeconds = null)
    {
        if (format != DappsMessage.MessageFormat.Plain)
        {
            throw new NotImplementedException("Only plain offers are sent this way");
        }

        var line = await OfferCoreAsync(id, salt, "p", null, destination, length, ct, ttl, originator,
            masterId, fragmentIndex, fragmentTotal, streamId, streamSeq, streamGapTimeoutSeconds);
        if (line == $"send {id}")
        {
            return true;
        }

        logger.LogError("Expected 'send {0}', got '{1}'", id, line);
        return false;
    }

    /// <summary>Writes an `ihave` line and returns the peer's reply.</summary>
    private async Task<string> OfferCoreAsync(
        string id,
        long? salt,
        string format,
        int? compressedLength,
        string destination,
        int length,
        CancellationToken ct,
        int? ttl,
        string? originator,
        string? masterId,
        int? fragmentIndex,
        int? fragmentTotal,
        string? streamId,
        uint? streamSeq,
        uint? streamGapTimeoutSeconds)
    {
        var line = OfferLine.Build("ihave", id, salt, format, compressedLength, destination, length, ttl, originator,
            masterId, fragmentIndex, fragmentTotal, streamId, streamSeq, streamGapTimeoutSeconds);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
        await stream.FlushAsync(ct);
        return await ReadReplyAsync(ct);
    }

    /// <summary>
    /// Sends `data &lt;id&gt;` followed by the raw payload bytes, then waits
    /// for `ack &lt;id&gt;` (success) or `bad &lt;id&gt;` (corrupt - far
    /// end's hash didn't match).
    ///
    /// The line and the payload go out in one write, so on AX.25 they
    /// travel in one I-frame (payload permitting) rather than two, with
    /// the extra key-up and RR that a second frame costs.
    /// </summary>
    public async Task<bool> SendMessageAsync(string id, byte[] payload, CancellationToken ct)
    {
        var header = Encoding.UTF8.GetBytes($"data {id}\n");
        var frame = new byte[header.Length + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);

        var line = await ReadReplyAsync(ct);
        if (line == $"ack {id}")
        {
            return true;
        }
        if (line == $"bad {id}")
        {
            logger.LogError("Remote NAKed message {0} - payload hash mismatch", id);
            return false;
        }

        logger.LogError("Expected 'ack/bad {0}', got '{1}'", id, line);
        return false;
    }

    /// <summary>
    /// Plan B6.1 Phase 2 - ask the remote DAPPS for its peers. Sends
    /// <c>peers\n</c> and reads <c>peer …</c> lines until <c>end</c>;
    /// silently tolerates other command-shaped lines arriving in between
    /// so a noisy peer doesn't break the parse. Inactivity timeout
    /// applies per-line (same budget as <see cref="ReadInitialPromptAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPeerInfo>> RequestPeersAsync(CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes("peers\n"), ct);
        await stream.FlushAsync(ct);

        var results = new List<DiscoveredPeerInfo>();
        while (true)
        {
            var line = await ReadReplyAsync(ct);
            if (line.Length == 0)
            {
                logger.LogWarning("EOF reading peers response after {0} record(s)", results.Count);
                break;
            }
            if (string.Equals(line, "end", StringComparison.OrdinalIgnoreCase)) break;

            // Line shape: "peer <callsign> source=<n|d>[ port=<byte>]"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "peer", StringComparison.OrdinalIgnoreCase))
            {
                // Non-peer / non-end lines aren't part of this protocol;
                // skip rather than abort so a future server adding extra
                // info to the response doesn't break old clients.
                continue;
            }

            var callsign = parts[1];
            string? source = null;
            int? port = null;
            for (var i = 2; i < parts.Length; i++)
            {
                var kv = parts[i];
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var key = kv[..eq];
                var value = kv[(eq + 1)..];
                if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase)) source = value;
                else if (string.Equals(key, "port", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(value, out var p) && p >= 0 && p <= 255) port = p;
            }
            results.Add(new DiscoveredPeerInfo(callsign, source ?? "", port));
        }
        return results;
    }

    /// <summary>One row of a <c>peers</c> response. <see cref="Source"/>
    /// is empty when the server didn't tag it; the wire form is
    /// <c>"n"</c> for a neighbour, <c>"d"</c> for a beacon-discovered
    /// peer.</summary>
    public sealed record DiscoveredPeerInfo(string Callsign, string Source, int? BearerPort);

    /// <summary>One row of a <c>routes</c> response. The remote is
    /// asserting it can reach <see cref="DestinationBaseCallsign"/>;
    /// the receiver's gossip importer adds this as a learned route
    /// via the responding neighbour.</summary>
    public sealed record GossipedRoute(string DestinationBaseCallsign, int? Hops, int? AgeSeconds);

    /// <summary>
    /// Send <c>routes\n</c> and read <c>route &lt;dest&gt; ...</c>
    /// lines until <c>end</c>. Reusable shape with
    /// <see cref="RequestPeersAsync"/>: unknown lines are skipped
    /// (forward-compat with future fields), unparseable rows are
    /// dropped silently rather than aborting the parse.
    ///
    /// <para>
    /// Wire form per row:
    /// </para>
    /// <code>
    /// route &lt;destBaseCallsign&gt; [hops=&lt;int&gt;] [ageSeconds=&lt;int&gt;]
    /// </code>
    /// <para>
    /// terminated by <c>end\n</c>. Receivers ignore unknown KVs.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GossipedRoute>> RequestRoutesAsync(CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes("routes\n"), ct);
        await stream.FlushAsync(ct);

        var results = new List<GossipedRoute>();
        while (true)
        {
            var line = await ReadReplyAsync(ct);
            if (line.Length == 0)
            {
                logger.LogWarning("EOF reading routes response after {0} record(s)", results.Count);
                break;
            }
            if (string.Equals(line, "end", StringComparison.OrdinalIgnoreCase)) break;

            if (ParseRouteLine(line) is { } route) results.Add(route);
        }
        return results;
    }

    /// <summary>One row of a <c>routes</c> answer, or null for any other
    /// line.</summary>
    public static GossipedRoute? ParseRouteLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "route", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        int? hops = null;
        int? ageSeconds = null;
        for (var i = 2; i < parts.Length; i++)
        {
            var kv = parts[i];
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq];
            var value = kv[(eq + 1)..];
            if (string.Equals(key, "hops", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var h)) hops = h;
            else if (string.Equals(key, "ageSeconds", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var a)) ageSeconds = a;
        }
        return new GossipedRoute(parts[1], hops, ageSeconds);
    }

    /// <summary>
    /// <see cref="ReadLineAsync"/> for a reply to one of our commands:
    /// skips the <c>exchange</c> line the answering node sends after its
    /// prompt, which says what it takes from another DAPPS node and isn't
    /// a reply to anything.
    /// </summary>
    private async Task<string> ReadReplyAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (!line.StartsWith(Backhaul.ExchangeRules.Verb + " ", StringComparison.Ordinal)) return line;
        }
    }

    /// <summary>
    /// Reads a line terminated by <c>\n</c>, <c>\r</c>, or <c>\r\n</c>.
    /// Leading line terminators are skipped (so a stranded <c>\n</c>
    /// after a <c>\r\n</c> sequence on the previous call doesn't yield
    /// a phantom empty line). Empty lines from the peer are not part of
    /// the DAPPSv1 protocol so this is safe.
    ///
    /// BPQ's Telnet driver rewrites line endings in the app→user
    /// direction (LF → CR with the default text-mode line discipline,
    /// per apps-interface.md), so accepting either form is necessary
    /// when dapps is reached via the BPQ APPLICATION+ATTACH bridge.
    /// </summary>
    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        var sawContent = false;
        while (true)
        {
            var n = await ReadWithTimeoutAsync(oneByte, ct);
            if (n == 0) break;
            if (oneByte[0] == (byte)'\n' || oneByte[0] == (byte)'\r')
            {
                if (!sawContent) continue;   // skip leading terminator(s)
                break;
            }
            sawContent = true;
            buffer.Add(oneByte[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads with a per-call inactivity timeout layered on top of the
    /// caller's cancellation token. Surfaces an explicit
    /// <see cref="TimeoutException"/> when the peer goes silent -
    /// callers (e.g. <c>OutboundMessageManager</c>) catch and log,
    /// then move on to the next message rather than hanging the run.
    /// <paramref name="budget"/> overrides <see cref="InactivityTimeout"/>
    /// for this one read.
    /// </summary>
    private async ValueTask<int> ReadWithTimeoutAsync(Memory<byte> buffer, CancellationToken outer, TimeSpan? budget = null)
    {
        var timeout = budget ?? InactivityTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"DAPPS sender: no data from peer within {timeout.TotalSeconds:F0}s");
        }
    }
}
