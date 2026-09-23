using System.Text;
using Microsoft.Extensions.Logging;

namespace dapps.client;

/// <summary>
/// Speaks the DAPPSv1 protocol over a duplex byte stream - agnostic of how
/// that stream is plumbed. Pair with any <see cref="Transport.IDappsOutboundTransport"/>.
///
/// Today this is the sender side only: read the initial prompt, offer a
/// message, send its payload. Receiver-side (`ihave` parsing, `chk`
/// validation) lives in dapps.core's IHaveValidator.
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
    /// Sends an `ihave` line and waits for `send &lt;id&gt;`. Returns true on
    /// acceptance. Today only fmt=p (plain) is supported on the sender
    /// side; fmt=d would need clen to be threaded through.
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
            throw new NotImplementedException("Deflate format not yet wired through outbound");
        }

        // F2 multi-part: mid= and frag=N/M either both present or both
        // absent. Belt-and-braces - the receiver's parser also enforces
        // this - but catching it sender-side prevents a malformed line
        // from reaching the wire in the first place.
        var hasFragHeaders = !string.IsNullOrEmpty(masterId)
            && fragmentIndex.HasValue && fragmentTotal.HasValue;
        if (!hasFragHeaders
            && (!string.IsNullOrEmpty(masterId) || fragmentIndex.HasValue || fragmentTotal.HasValue))
        {
            throw new ArgumentException(
                "masterId, fragmentIndex, fragmentTotal must all be set together (multi-part) or all be null");
        }

        var sb = new StringBuilder($"ihave {id} len={length} fmt=p dst={destination}");
        if (salt.HasValue)
        {
            sb.Append($" s={salt}");
        }
        if (ttl.HasValue)
        {
            sb.Append($" ttl={ttl.Value}");
        }
        // F1 end-to-end source tracking. Emitted only when set - pre-F1
        // local submissions (or relayed messages with no upstream src=)
        // omit it so the receiver knows the originator is unknown.
        if (!string.IsNullOrEmpty(originator))
        {
            sb.Append($" src={originator}");
        }
        // F2 multi-part headers. Receiver groups fragments by mid=.
        // A pre-F2 receiver sees these as unknown KVs and (per spec)
        // ignores them - but with no reassembly it'll just deliver each
        // fragment to the app individually. F2 receivers route to the
        // reassembly buffer.
        if (hasFragHeaders)
        {
            sb.Append($" mid={masterId} frag={fragmentIndex}/{fragmentTotal}");
        }
        // Opt-in ordering keys. All three travel together; the receiver's
        // IHaveValidator rejects a partial set. Belt-and-braces sender-
        // side: catch the malformed envelope before it reaches the wire.
        var hasStream = !string.IsNullOrEmpty(streamId)
            && streamSeq.HasValue && streamGapTimeoutSeconds.HasValue;
        if (!hasStream
            && (!string.IsNullOrEmpty(streamId) || streamSeq.HasValue || streamGapTimeoutSeconds.HasValue))
        {
            throw new ArgumentException(
                "streamId, streamSeq, streamGapTimeoutSeconds must all be set together (opt-in ordering) or all be null");
        }
        if (hasStream)
        {
            sb.Append($" sid={streamId} sn={streamSeq} gt={streamGapTimeoutSeconds}");
        }
        sb.Append('\n');

        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);

        var line = await ReadLineAsync(ct);
        if (line == $"send {id}")
        {
            return true;
        }

        logger.LogError("Expected 'send {0}', got '{1}'", id, line);
        return false;
    }

    /// <summary>
    /// Sends `data &lt;id&gt;` followed by the raw payload bytes, then waits
    /// for `ack &lt;id&gt;` (success) or `bad &lt;id&gt;` (corrupt - far
    /// end's hash didn't match).
    /// </summary>
    public async Task<bool> SendMessageAsync(string id, byte[] payload, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data {id}\n"), ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);

        var line = await ReadLineAsync(ct);
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
            var line = await ReadLineAsync(ct);
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
            var line = await ReadLineAsync(ct);
            if (line.Length == 0)
            {
                logger.LogWarning("EOF reading routes response after {0} record(s)", results.Count);
                break;
            }
            if (string.Equals(line, "end", StringComparison.OrdinalIgnoreCase)) break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "route", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var dest = parts[1];
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
            results.Add(new GossipedRoute(dest, hops, ageSeconds));
        }
        return results;
    }

    /// <summary>One message yielded by the rev drain. Captures the
    /// fields a caller's <see cref="Backhaul.IBackhaulInbox"/>
    /// would need to deliver as if the message had arrived via push.
    /// Plan F3.</summary>
    public sealed record PolledMessage(
        string Id,
        string Destination,
        long? Salt,
        int? Ttl,
        byte[] Payload,
        string? Originator,
        string? MasterId,
        int? FragmentIndex,
        int? FragmentTotal,
        string? StreamId,
        uint? StreamSeq,
        uint? StreamGapTimeoutSeconds);

    /// <summary>
    /// Plan F3 - reverse forwarding from the client side. Send
    /// <c>rev</c> (or <c>rev id1 id2 …</c> for selective drain) and
    /// then yield each message the server pushes back via the
    /// <c>ihave</c>/<c>data</c>/<c>ack</c> exchange. Returns when the
    /// server signals "drained" by re-emitting the <c>DAPPSv1&gt;</c>
    /// prompt.
    ///
    /// The hash of each fragment's payload is verified against its
    /// <c>id</c> + <c>s=</c> salt before yielding, matching what the
    /// regular receive path does. Bad-hash messages are NAK'd back to
    /// the server with <c>bad &lt;id&gt;</c> and not yielded.
    /// </summary>
    public async IAsyncEnumerable<PolledMessage> PollAsync(
        IReadOnlyList<string>? requestedIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var cmd = (requestedIds is { Count: > 0 })
            ? "rev " + string.Join(' ', requestedIds) + "\n"
            : "rev\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(cmd), ct);
        await stream.FlushAsync(ct);

        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line.Length == 0)
            {
                // EOF mid-poll; treat as drained. Anything we already
                // yielded the caller has consumed.
                yield break;
            }
            // Server's "drained" marker. Distinct from `ihave` because
            // the prompt has a `>` and no spaces.
            if (line == "DAPPSv1>") yield break;

            if (!line.StartsWith("ihave ", StringComparison.Ordinal))
            {
                // Unexpected line shape; ignore (forward-compat) and
                // keep reading.
                continue;
            }

            var (offerOk, parsed) = TryParseOffer(line);
            if (!offerOk || parsed is null)
            {
                // Malformed offer - NAK with the id we could pluck out
                // (or ?? as a placeholder) and move on.
                var fallbackId = parsed?.Id ?? "??";
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"no {fallbackId}\n"), ct);
                await stream.FlushAsync(ct);
                continue;
            }

            // Always accept - the client asked for this; "no" is
            // reserved for when the receiver has a strong reason to
            // decline (none today).
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"send {parsed.Id}\n"), ct);
            await stream.FlushAsync(ct);

            // Server responds with `data <id>\n` followed by raw bytes.
            var dataHeader = await ReadLineAsync(ct);
            if (dataHeader != $"data {parsed.Id}")
            {
                logger.LogWarning("rev poll: expected 'data {0}', got '{1}' - bailing", parsed.Id, dataHeader);
                yield break;
            }
            var payload = new byte[parsed.Length];
            await ReadExactlyAsync(payload, ct);

            // Hash check before yielding - same contract as the
            // regular receiver. Bad payloads get NAK'd; the server
            // can choose to retry or move on.
            var computed = DappsMessage.ComputeHash(payload, parsed.Salt)[..7];
            if (computed != parsed.Id)
            {
                logger.LogWarning("rev poll: hash mismatch on {0}; NAKing", parsed.Id);
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"bad {parsed.Id}\n"), ct);
                await stream.FlushAsync(ct);
                continue;
            }

            await stream.WriteAsync(Encoding.UTF8.GetBytes($"ack {parsed.Id}\n"), ct);
            await stream.FlushAsync(ct);

            yield return new PolledMessage(
                Id: parsed.Id,
                Destination: parsed.Destination,
                Salt: parsed.Salt,
                Ttl: parsed.Ttl,
                Payload: payload,
                Originator: parsed.Originator,
                MasterId: parsed.MasterId,
                FragmentIndex: parsed.FragmentIndex,
                FragmentTotal: parsed.FragmentTotal,
                StreamId: parsed.StreamId,
                StreamSeq: parsed.StreamSeq,
                StreamGapTimeoutSeconds: parsed.StreamGapTimeoutSeconds);
        }
    }

    /// <summary>
    /// Read exactly <paramref name="buffer"/> bytes from the stream,
    /// using the per-read inactivity timeout that
    /// <see cref="ReadWithTimeoutAsync"/> applies. Loops until full
    /// or surfaces a <see cref="TimeoutException"/> from a stalled peer.
    /// </summary>
    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            var n = await ReadWithTimeoutAsync(buffer.AsMemory(off), ct);
            if (n == 0)
            {
                throw new IOException(
                    $"rev poll: stream ended after {off} of {buffer.Length} payload bytes");
            }
            off += n;
        }
    }

    /// <summary>Pure parser for the small subset of <c>ihave</c> the
    /// client poll loop needs. Avoids pulling the dapps.core
    /// IHaveValidator dependency into dapps.client. Returns
    /// (true, parsed) on success or (false, partial) when the
    /// minimum fields aren't present.</summary>
    private static (bool Ok, ParsedOffer? Offer) TryParseOffer(string line)
    {
        // line: "ihave <id> len=N fmt=p dst=… [s=…] [ttl=…] [src=…] [mid=… frag=N/M] [sid=… sn=… gt=…] …"
        var parts = line.Split(' ');
        if (parts.Length < 4 || parts[0] != "ihave") return (false, null);
        var id = parts[1];
        string? destination = null;
        int? len = null;
        long? salt = null;
        int? ttl = null;
        string? originator = null;
        string? masterId = null;
        int? fragIndex = null;
        int? fragTotal = null;
        string? streamId = null;
        uint? streamSeq = null;
        uint? streamGapTimeout = null;
        for (var i = 2; i < parts.Length; i++)
        {
            var kv = parts[i];
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq];
            var value = kv[(eq + 1)..];
            switch (key)
            {
                case "len": if (int.TryParse(value, out var l)) len = l; break;
                case "dst": destination = value; break;
                case "s": if (long.TryParse(value, out var s)) salt = s; break;
                case "ttl": if (int.TryParse(value, out var t)) ttl = t; break;
                case "src": originator = value; break;
                case "mid": masterId = value; break;
                case "frag":
                    var slash = value.IndexOf('/');
                    if (slash > 0 && slash < value.Length - 1
                        && int.TryParse(value.AsSpan(0, slash), out var fn)
                        && int.TryParse(value.AsSpan(slash + 1), out var fm)
                        && fn >= 1 && fm >= 2 && fn <= fm)
                    {
                        fragIndex = fn; fragTotal = fm;
                    }
                    break;
                case "sid": streamId = value; break;
                case "sn": if (uint.TryParse(value, out var snv)) streamSeq = snv; break;
                case "gt": if (uint.TryParse(value, out var gtv)) streamGapTimeout = gtv; break;
            }
        }
        if (destination is null || len is null) return (false, new ParsedOffer(id, "", 0, null, null, null, null, null, null, null, null, null));
        return (true, new ParsedOffer(id, destination, len.Value, salt, ttl, originator, masterId, fragIndex, fragTotal, streamId, streamSeq, streamGapTimeout));
    }

    private sealed record ParsedOffer(
        string Id, string Destination, int Length, long? Salt, int? Ttl,
        string? Originator, string? MasterId, int? FragmentIndex, int? FragmentTotal,
        string? StreamId, uint? StreamSeq, uint? StreamGapTimeoutSeconds);

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
