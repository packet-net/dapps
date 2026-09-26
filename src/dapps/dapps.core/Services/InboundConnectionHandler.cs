using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Compression;

namespace dapps.core.Services;

/// <summary>
/// Receiver-side DAPPSv1 session reader. Bearer-neutral: takes a
/// duplex byte <see cref="Stream"/> already-connected to a peer and
/// the peer's callsign already-determined (each bearer figures the
/// callsign out its own way - AGW reads it off the inbound 'C' frame's
/// CallFrom field; the legacy Apps-Interface bearer read it from the
/// first line of the bridged TCP socket). Owns the
/// `prompt` / `ihave` / `data` correlation and the on-the-wire ack
/// contract. Once a payload is received and hash-validated, the
/// completed message is handed off to <see cref="IBackhaulInbox"/> -
/// where DAPPS-level concerns (queue persistence, MQTT injection,
/// future forwarding decisions) live, decoupled from the bearer.
/// </summary>
public class InboundConnectionHandler(
    Stream stream,
    string sourceCallsign,
    ILoggerFactory loggerFactory,
    Database database,
    IBackhaulInbox inbox,
    OperationalMetrics? metrics = null,
    Func<string, CancellationToken, Task<bool>>? compressTo = null,
    InboundSessionDirectory? directory = null,
    Func<string, CancellationToken, Task<int>>? tailFor = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();

    // Inactivity timeout per spec - AX.25 T3 default is 3 min; matching that
    // keeps DAPPS sessions tearing down on roughly the same cadence as the
    // underlying link layer would on its own.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The offers this session accepted, by id. The database copy is
    /// keyed by id alone, and two neighbours can offer the same message
    /// at once with different encodings (one compressed, one plain), so
    /// a session reads its payload by the offer it accepted itself.
    /// </summary>
    private readonly Dictionary<string, IHaveOffer> sessionOffers = new(StringComparer.Ordinal);

    /// <summary>How long to wait for the caller's next command. Longer
    /// once the caller has asked us to hold the session (<c>tail</c>).</summary>
    private TimeSpan idleTimeout = InactivityTimeout;

    /// <summary>Traffic the forwarder handed us for the caller, sent on its next <c>rev</c>.</summary>
    private readonly ConcurrentQueue<IBackhaulBatch> handed = new();

    // Telling the caller it has mail (`pending`) happens from outside
    // this session's own flow, so it's only written while the session is
    // idle between commands, and the session takes this gate before it
    // answers anything: the two writes can't interleave.
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private bool idle;
    private bool closing;
    private bool pendingWanted;
    private bool pendingSent;

    /// <summary>
    /// Take traffic for the caller from the forwarder: tell the caller
    /// (<c>pending</c>) and send it when the caller asks with <c>rev</c>.
    /// False once the session is closing; the forwarder then leaves it
    /// queued and sends it once this session has gone.
    /// </summary>
    internal bool TryTakeBatch(IBackhaulBatch batch)
    {
        if (closing) return false;
        handed.Enqueue(batch);
        _ = RequestPendingAsync();
        return true;
    }

    private async Task RequestPendingAsync()
    {
        try
        {
            await stateGate.WaitAsync();
            try
            {
                pendingWanted = true;
                if (idle && !closing) await SendPendingIfWantedAsync();
            }
            finally
            {
                stateGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't tell {0} it has mail waiting", sourceCallsign);
        }
    }

    /// <summary>Call holding <see cref="stateGate"/>.</summary>
    private async Task SendPendingIfWantedAsync()
    {
        if (!pendingWanted || pendingSent) return;
        await stream.WriteAsync("pending\n"u8.ToArray());
        await stream.FlushAsync();
        pendingSent = true;
        logger.LogInformation("Told {0} it has mail waiting", sourceCallsign);
    }

    private async Task EnterIdleAsync()
    {
        await stateGate.WaitAsync();
        try
        {
            idle = true;
            await SendPendingIfWantedAsync();
        }
        finally
        {
            stateGate.Release();
        }
    }

    private async Task LeaveIdleAsync()
    {
        await stateGate.WaitAsync();
        idle = false;
        stateGate.Release();
    }

    public async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Inbound session from {0}", sourceCallsign);
            directory?.Register(sourceCallsign, this);

            await stream.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n"), stoppingToken);
            await stream.FlushAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Waiting for command");

                string command;
                await EnterIdleAsync();
                try
                {
                    command = await Extensions.WithInactivityTimeout(t => stream.ReadLine(t), idleTimeout, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Inactivity timeout waiting for command, closing connection");
                    return;
                }
                finally
                {
                    await LeaveIdleAsync();
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    logger.LogInformation("Empty command, closing connection");
                    return;
                }

                var cmd = Interpret(command);

                if (cmd == null)
                {
                    logger.LogInformation("Unrecognised command {0}", command);
                    await stream.WriteUtf8AndFlush("eh?\n");
                    return;
                }
                else if (cmd == Command.Quit)
                {
                    logger.LogInformation("Client has asked to quit");
                    await stream.WriteUtf8AndFlush("bye\n");
                    return;
                }
                else if (cmd == Command.Help)
                {
                    await stream.WriteUtf8AndFlush("This is DAPPS. See https://github.com/packet-net/dapps/blob/master/README.md for details.\n");
                }
                else if (cmd == Command.IHave)
                {
                    var parts = command.Split(' ');
                    if (parts.Length < 2)
                    {
                        logger.LogError("ihave command has wrong number of parts");
                        await stream.WriteUtf8AndFlush("error\n");
                    }
                    else
                    {
                        logger.LogInformation("Client is offering us message {0}", parts[1]);
                        await HandleMessageOffer(stream, command, stoppingToken);
                    }
                }
                else if (cmd == Command.Data)
                {
                    var parts = command.Split(' ');
                    if (parts.Length != 2)
                    {
                        logger.LogError("data command has wrong number of parts");
                        await stream.WriteUtf8AndFlush("error\n");
                    }
                    else
                    {
                        logger.LogInformation("Client is sending us data for message {0}", parts[1]);
                        await HandleData(stream, parts[1], stoppingToken);
                    }
                }
                else if (cmd == Command.Peers)
                {
                    logger.LogInformation("Client is asking for our peers");
                    await HandlePeers(stream, stoppingToken);
                }
                else if (cmd == Command.Rev)
                {
                    logger.LogInformation("Client is asking for queued mail (rev)");
                    await HandleRev(stream, command, stoppingToken);
                }
                else if (cmd == Command.Routes)
                {
                    logger.LogInformation("Client is asking for our known routes (gossip)");
                    await HandleRoutes(stream, stoppingToken);
                }
                else if (cmd == Command.Tail)
                {
                    await HandleTail(stream, command, stoppingToken);
                }
            }
        }
        finally
        {
            await stateGate.WaitAsync();
            closing = true;
            stateGate.Release();
            directory?.Unregister(sourceCallsign, this);
            await stream.DisposeAsync();
        }
    }

    private enum Command
    {
        Quit,
        IHave,
        Data,
        Help,
        /// <summary>
        /// Plan B6.1 Phase 2 - transitive peer discovery. Client asks
        /// "who do you forward to?"; we emit one <c>peer &lt;call&gt;</c>
        /// line per known forward target, then <c>end</c>, then loop
        /// back to the next prompt. No state, no persistence - purely
        /// a read-only view of our neighbour / discovered-peer tables.
        /// </summary>
        Peers,
        /// <summary>
        /// Reverse forwarding. Client asks "got mail for me?";
        /// we drain matching outbound queue entries via the same
        /// ihave/send/data/ack pattern we'd use to push, then re-emit
        /// the <c>DAPPSv1&gt;</c> prompt to signal we're done. Optional
        /// trailing id list for selective drain (<c>rev id1 id2 …</c>).
        /// </summary>
        Rev,
        /// <summary>
        /// Route gossip. Client asks "what destinations can you reach?";
        /// we emit one <c>route &lt;dest&gt; ...</c> line per
        /// known-good destination (filtered to those we'd actually
        /// attempt a forward to), then <c>end</c>. Receivers import
        /// each row into their <c>learnedroutes</c> table with
        /// <c>Source = "gossip"</c>; the existing failure-counter
        /// machinery handles invalidation if the imported route turns
        /// out not to work.
        /// </summary>
        Routes,
        /// <summary>
        /// #187 proposal 9: <c>tail &lt;seconds&gt;</c>, the caller asking
        /// us to hold the session open until it has been quiet that long.
        /// We answer <c>tail &lt;seconds&gt;</c> with what we'll allow (0 =
        /// no), and while holding tell the caller <c>pending</c> when we
        /// have mail for it.
        /// </summary>
        Tail,
    }

    private static readonly string[] exitCommands = ["q", "bye", "quit", "exit"];
    private static readonly string[] helpCommands = ["info", "help"];

    private static Command? Interpret(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var command = input.Trim().ToLower();

        if (exitCommands.Contains(command))
        {
            return Command.Quit;
        }

        if (helpCommands.Contains(command))
        {
            return Command.Help;
        }

        var parts = command.Split(' ');

        if (parts[0] == "ihave")
        {
            return Command.IHave;
        }

        if (parts[0] == "data")
        {
            return Command.Data;
        }

        if (command == "peers" || command == "who")
        {
            // Accept either form; the spec doc canonicalises on `peers`,
            // but `who` is the verb a sysop already types at a node prompt
            // so it's a natural alias.
            return Command.Peers;
        }

        if (parts[0] == "tail")
        {
            return Command.Tail;
        }

        if (parts[0] == "rev")
        {
            // Reverse forward: bare "rev" drains everything for the
            // caller; "rev id1 id2 …" drains the named subset.
            return Command.Rev;
        }

        if (command == "routes")
        {
            return Command.Routes;
        }

        return null;
    }

    /// <summary>
    /// Plan B6.1 Phase 2 - emit the set of callsigns we forward to.
    /// One <c>peer &lt;callsign&gt; source=&lt;n|d&gt;[ port=&lt;byte&gt;]</c>
    /// line per known forward target, then <c>end</c>. Callers (today:
    /// dapps probers populating their own <c>DbProbedNode</c> table for
    /// transitive discovery) parse line-by-line until <c>end</c>.
    ///
    /// Sources reported:
    /// <list type="bullet">
    /// <item><c>n</c> - manual <see cref="Models.DbNeighbour"/> row,
    /// AGW-routable (UDP-only neighbours are skipped; the asker can't
    /// reach them over the same bearer).</item>
    /// <item><c>d</c> - AGW-bearer <see cref="Models.DbDiscoveredPeer"/>
    /// row. We've heard a beacon but never been asked to forward to
    /// them ourselves; useful as an exploration hint for the asker.</item>
    /// </list>
    ///
    /// Read-only - emitting a peer is not an endorsement, doesn't bind
    /// us to forward, and doesn't leak anything more sensitive than
    /// what already shows up on the air via beacons or DAPPS forwarding.
    /// </summary>
    private async Task HandlePeers(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var neighbours = await database.GetNeighbours();
        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            if (n.UdpEndpoint is not null) continue;   // UDP-only - not AGW-reachable for the asker
            if (!emitted.Add(n.Callsign)) continue;
            sb.Append("peer ").Append(n.Callsign).Append(" source=n");
            if (n.BearerPort is { } port) sb.Append(" port=").Append(port);
            sb.Append('\n');
        }

        var peers = await database.GetDiscoveredPeers();
        foreach (var p in peers)
        {
            if (!string.Equals(p.Bearer, "agw", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (!emitted.Add(p.Callsign)) continue;     // already reported as a neighbour
            sb.Append("peer ").Append(p.Callsign).Append(" source=d");
            if (p.BearerPort is { } port) sb.Append(" port=").Append(port);
            sb.Append('\n');
        }

        sb.Append("end\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);
        logger.LogInformation("Sent {0} peer record(s) to {1}", emitted.Count, sourceCallsign);
    }

    /// <summary>
    /// Route gossip - emit one <c>route &lt;dest&gt;</c> line per
    /// destination this node believes it can reach, then <c>end</c>.
    /// Receivers import each row as a learned route (with
    /// <c>Source = "gossip"</c>) and use the existing failure-counter
    /// invalidation if it turns out not to work.
    ///
    /// <para>
    /// Filter: only routes whose <see cref="DbLearnedRoute.ConsecutiveFailures"/>
    /// is zero. Skips rows we ourselves think are broken; an
    /// imported-via-gossip row is suppressed too (we don't re-export
    /// hearsay - that's how distance-vector loops form).
    /// </para>
    /// </summary>
    private async Task HandleRoutes(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Manual neighbours - the most trusted class. Highest priority
        // in the gossip output too. Gossip-importers will re-validate
        // by attempting forwards; a poisoned advert here is bounded by
        // their own ConsecutiveFailures threshold.
        var neighbours = await database.GetNeighbours();
        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            var baseCall = n.Callsign.Split('-')[0];
            if (!emitted.Add(baseCall)) continue;
            sb.Append("route ").Append(baseCall).Append(" hops=1\n");
        }

        // Traffic-learned routes. Only export rows we ourselves trust:
        // ConsecutiveFailures = 0 AND not gossip-imported (don't
        // re-export hearsay). LastUsedAt unset means we've never
        // actually traversed it; suppress those too.
        var learned = await database.GetLearnedRoutesAsync();
        foreach (var r in learned)
        {
            if (r.ConsecutiveFailures > 0) continue;
            if (string.Equals(r.Source, "gossip", StringComparison.OrdinalIgnoreCase)) continue;
            if (r.LastUsedAt == DateTime.MinValue) continue;
            if (string.IsNullOrWhiteSpace(r.DestinationBaseCallsign)) continue;
            if (!emitted.Add(r.DestinationBaseCallsign)) continue;
            var ageSeconds = (int)Math.Max(0, (now - r.LastSeenAt).TotalSeconds);
            sb.Append("route ").Append(r.DestinationBaseCallsign)
              .Append(" hops=2 ageSeconds=").Append(ageSeconds).Append('\n');
        }

        sb.Append("end\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);
        logger.LogInformation("Sent {0} route record(s) to {1}", emitted.Count, sourceCallsign);
    }

    /// <summary>
    /// Plan F3 - reverse forwarding. The caller has asked us to drain
    /// queued mail destined for them; we walk the outbound queue,
    /// emitting <c>ihave</c> / <c>data</c> exchanges as the SENDER,
    /// then re-emit the <c>DAPPSv1&gt;</c> prompt so the caller's
    /// poll loop knows we're done.
    ///
    /// Final-destination only: we drain messages whose <c>destination</c>
    /// suffix matches the caller's base callsign. Transit messages
    /// (caller is just a known forwarder for somewhere else) are
    /// deliberately not included - the caller's <c>rev</c> is for
    /// THEIR mail, not for them to act as a downstream relay.
    /// </summary>
    private async Task HandleRev(Stream stream, string command, CancellationToken ct)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var requestedIds = parts.Skip(1).ToList();   // empty = drain all
        var callerBase = sourceCallsign.Split('-')[0];

        // Sender state machine: reuse DappsProtocolClient but skip its
        // ReadInitialPromptAsync - we're already mid-session, the
        // client doesn't owe us another prompt.
        var protocol = new DappsProtocolClient(stream, loggerFactory);
        var compress = await ShouldCompressToCallerAsync(ct);
        var drained = 0;

        // This rev collects whatever the caller was told about.
        await stateGate.WaitAsync(ct);
        pendingWanted = false;
        pendingSent = false;
        stateGate.Release();

        // First what the forwarder handed this session for the caller,
        // which can include traffic the caller relays onwards, not only
        // its own mail. A plain `rev`: a selective one names its ids.
        if (requestedIds.Count == 0 && !await DrainHandedAsync(protocol, compress, () => drained++, ct))
        {
            logger.LogInformation("rev drain to {0}: it hung up part way", sourceCallsign);
            return;
        }

        // Read after that drain, which may have sent some of these.
        var queue = await database.GetMessagesForCaller(callerBase, requestedIds);
        foreach (var msg in queue)
        {
            // Residual TTL: same calculation OutboundMessageManager
            // does on outbound. An expired message would be dropped
            // by the TtlSweeper eventually but might be sitting in
            // the queue right now; skip rather than offer it.
            int? residualTtl = msg.Ttl is { } ttl
                ? TtlMath.Residual(ttl, msg.CreatedAt, DateTime.UtcNow)
                : null;
            if (residualTtl is <= 0) continue;

            try
            {
                var outcome = await protocol.PushAsync(new BackhaulMessage(
                    Id: msg.Id,
                    Destination: msg.Destination,
                    Salt: msg.Salt,
                    Ttl: residualTtl,
                    Payload: msg.Payload,
                    Originator: string.IsNullOrEmpty(msg.OriginatorCallsign) ? null : msg.OriginatorCallsign,
                    MasterId: msg.MasterId,
                    FragmentIndex: msg.FragmentIndex,
                    FragmentTotal: msg.FragmentTotal,
                    StreamId: msg.StreamId,
                    StreamSeq: msg.StreamSeq,
                    StreamGapTimeoutSeconds: msg.StreamGapTimeoutSeconds), compress, ct);
                if (outcome == DappsProtocolClient.PushOutcome.PeerClosed)
                {
                    logger.LogInformation("rev drain: {0} hung up; the rest stay queued", sourceCallsign);
                    break;
                }
                if (outcome == DappsProtocolClient.PushOutcome.OfferRefused)
                {
                    logger.LogInformation("rev drain: caller declined {0}", msg.Id);
                    continue;
                }
                if (outcome == DappsProtocolClient.PushOutcome.Accepted)
                {
                    await database.MarkMessageAsForwarded(msg.Id);
                    drained++;
                }
            }
            catch (Exception ex)
            {
                // One failed exchange shouldn't bail the whole drain -
                // the caller might still want subsequent queued
                // messages. The link layer will tear us down if it's
                // really gone.
                logger.LogWarning(ex, "rev drain: send of {0} failed", msg.Id);
            }
        }

        logger.LogInformation(
            "rev drain to {0}: {1}/{2} messages sent (selective={3})",
            sourceCallsign, drained, queue.Count, requestedIds.Count > 0);

        // Done draining. Re-emit DAPPSv1> so the caller's poll loop
        // sees a clean "ready for next command" signal that's distinct
        // from another `ihave` arriving.
        await stream.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n"), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>The operator's compression setting for the caller, for
    /// what we send it in a rev drain. Plain if it can't be read.</summary>
    private async Task<bool> ShouldCompressToCallerAsync(CancellationToken ct)
    {
        if (compressTo is null) return false;
        try
        {
            return await compressTo(sourceCallsign, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the compression setting for {0}; sending plain", sourceCallsign);
            return false;
        }
    }

    /// <summary>
    /// Send what the forwarder handed this session, reporting each
    /// outcome back to its batch. A batch stops at a message the caller
    /// doesn't take, as it would on a session of our own; the rest of it
    /// stays queued. False when the caller hung up.
    /// </summary>
    private async Task<bool> DrainHandedAsync(DappsProtocolClient protocol, bool compress, Action onSent, CancellationToken ct)
    {
        while (handed.TryDequeue(out var batch))
        {
            while (await batch.NextAsync(ct) is { } message)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BackhaulSendResult result;
                try
                {
                    result = await protocol.PushAsync(message, compress, ct) switch
                    {
                        DappsProtocolClient.PushOutcome.Accepted => BackhaulSendResult.Ok(),
                        DappsProtocolClient.PushOutcome.OfferRefused => BackhaulSendResult.Fail($"offer rejected for {message.Id}"),
                        DappsProtocolClient.PushOutcome.PeerClosed => BackhaulSendResult.Defer($"{sourceCallsign} hung up; {message.Id} stays queued"),
                        _ => BackhaulSendResult.Fail($"payload rejected for {message.Id}"),
                    };
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    result = BackhaulSendResult.Defer($"{sourceCallsign}'s link went; {message.Id} stays queued");
                }
                await batch.CompleteAsync(message, result, sw.Elapsed, ct);
                if (result.Accepted)
                {
                    onSent();
                    continue;
                }
                if (result.Deferred) return false;
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// #187 proposal 9: the caller asks us to hold the session open until
    /// it has been quiet for the given seconds. We allow the lower of
    /// that and our own setting for the caller (0 = don't hold), and wait
    /// that long plus a margin for its next command, so its <c>quit</c>
    /// arrives before we'd give up on it.
    /// </summary>
    private async Task HandleTail(Stream stream, string command, CancellationToken ct)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var asked = parts.Length == 2
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds : 0;
        var ours = 0;
        if (tailFor is not null)
        {
            try
            {
                ours = await tailFor(sourceCallsign, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't read the link-hold setting for {0}; not holding", sourceCallsign);
            }
        }

        var agreed = Math.Max(0, Math.Min(asked, ours));
        var held = TimeSpan.FromSeconds(agreed) + TailMargin;
        idleTimeout = agreed > 0 && held > InactivityTimeout ? held : InactivityTimeout;
        logger.LogInformation(agreed > 0
            ? "Holding the session with {0} open until it has been quiet for {1}s"
            : "Not holding the session with {0} open", sourceCallsign, agreed);
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"tail {agreed}\n"), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Added to an agreed hold before we give up on a quiet caller.</summary>
    private static readonly TimeSpan TailMargin = TimeSpan.FromSeconds(30);

    private async Task HandleMessageOffer(Stream stream, string command, CancellationToken stoppingToken)
    {
        var result = IHaveValidator.Validate(command);
        if (!result.IsValid)
        {
            logger.LogError("Rejecting offer: {0}", result.Error);
            var idForReply = result.Id ?? "??";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"error {idForReply}\n"), stoppingToken);
            return;
        }

        var offer = result.Offer!;
        logger.LogInformation("Accepting message {0} (len={1}, fmt={2}, dst={3})", offer.Id, offer.Length, offer.Format, offer.Destination);

        await stream.WriteAsync(Encoding.UTF8.GetBytes($"send {offer.Id}\n"));
        sessionOffers[offer.Id] = offer;
        await database.SaveOffer(offer);
    }

    private async Task HandleData(Stream stream, string id, CancellationToken stoppingToken)
    {
        // The offer this session accepted. The stored copy is keyed by id
        // alone, so another session offering the same message (perhaps
        // encoded differently) can replace it under us: it's only the
        // fallback.
        var offer = sessionOffers.TryGetValue(id, out var own)
            ? Database.ToDbOffer(own, DateTime.UtcNow)
            : await database.LoadOfferMetadata(id);

        byte[] buffer;
        if (offer.Format is "p" or "")
        {
            buffer = new byte[offer.Length];
            logger.LogInformation("Waiting for {0} uncompressed bytes", buffer.Length);
            try
            {
                await Extensions.WithInactivityTimeout(t => stream.ReadExactlyAsync(buffer, t).AsTask(), InactivityTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Inactivity timeout waiting for uncompressed payload, closing");
                return;
            }
            logger.LogInformation("Received uncompressed data");
        }
        else // fmt=d (deflate) or fmt=z<N> (zstd with shared dictionary N)
        {
            if (offer.CompressedLength is null)
            {
                // Shouldn't happen - we validate at offer time - but defend
                // against a corrupted DB row.
                logger.LogError("Offer {0} marked fmt={1} but has no clen stored", id, offer.Format);
                await stream.WriteUtf8AndFlush("bad " + id + "\n");
                return;
            }

            logger.LogInformation("Waiting for {0} compressed bytes", offer.CompressedLength.Value);
            var compressed = new byte[offer.CompressedLength.Value];
            try
            {
                await Extensions.WithInactivityTimeout(t => stream.ReadExactlyAsync(compressed, t).AsTask(), InactivityTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Inactivity timeout waiting for compressed payload, closing");
                return;
            }

            try
            {
                buffer = PayloadCompression.Decode(offer.Format, compressed, offer.Length);
            }
            catch (InvalidDataException ex)
            {
                logger.LogWarning("Payload for {0} does not decode: {1}", id, ex.Message);
                await stream.WriteUtf8AndFlush("bad " + id + "\n");
                return;
            }
            logger.LogInformation("Received {0} as fmt={1}: {2} bytes, {3} decompressed",
                id, offer.Format, compressed.Length, buffer.Length);
        }

        var text = Encoding.UTF8.GetString(buffer);
        logger.LogInformation("Got message {0}", text);
        
        var computedId = DappsMessage.ComputeHash(buffer, offer.Salt)[..7];

        if (computedId == id)
        {
            logger.LogInformation("Hash matches, handing message {0} to the inbox", id);

            // Rehydrate the offer's stored AdditionalProperties JSON back into
            // a header dict for the bearer-neutral inbox. Empty/missing →
            // null, which the inbox treats as no headers.
            IReadOnlyDictionary<string, string>? headers = null;
            if (!string.IsNullOrWhiteSpace(offer.AdditionalProperties)
                && offer.AdditionalProperties != "{}")
            {
                try
                {
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(offer.AdditionalProperties);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Could not parse stored offer headers for {0}; dropping", id);
                }
            }

            var backhaulMessage = new BackhaulMessage(
                Id: id,
                Destination: offer.Destination,
                Salt: offer.Salt,
                Ttl: offer.Ttl,
                Payload: buffer,
                Headers: headers,
                Originator: string.IsNullOrEmpty(offer.OriginatorCallsign) ? null : offer.OriginatorCallsign,
                MasterId: offer.MasterId,
                FragmentIndex: offer.FragmentIndex,
                FragmentTotal: offer.FragmentTotal,
                StreamId: offer.StreamId,
                StreamSeq: offer.StreamSeq,
                StreamGapTimeoutSeconds: offer.StreamGapTimeoutSeconds);

            await inbox.DeliverAsync(backhaulMessage, sourceCallsign, stoppingToken);
            sessionOffers.Remove(id);
            await database.DeleteOffer(id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ack " + id + "\n"));
        }
        else
        {
            logger.LogWarning("Hash does not match - payload corrupt");
            metrics.RecordHashMismatch(id, sourceCallsign);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("bad " + id + "\n"));
        }
    }
}
