using System.Text;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.core.Services;

/// <summary>
/// Plan B6.1 - connected-mode probe-and-map. Performs a single
/// reachability probe: open an AGW session to <c>(callsign, port)</c>,
/// look for a <c>DAPPSv1&gt;</c> banner, then disconnect cleanly. The
/// scheduler ( <see cref="ProbeSchedulerService"/> ) and the on-demand
/// REST endpoint both call into this - schedule decisions and result
/// persistence live elsewhere.
///
/// The class is deliberately stateless (per-call Connect → Read prompt
/// → Disconnect) so the same instance can serve concurrent probes; the
/// underlying <see cref="IDappsOutboundTransport"/> makes a fresh call
/// per probe, just as the forwarder does.
/// </summary>
public sealed class NodeProber(
    IDappsOutboundTransport transport,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<NodeProber> logger,
    IRouteGossipPort? routeGossip = null,
    PeerSessionRegistry? peerSessions = null,
    SessionDirectory? sessionDirectory = null,
    Dappsv1SessionBackhaul? sessionBackhaul = null)
{
    /// <summary>How long a probe waits for the <c>pong</c> when it pings
    /// inside a session that is already open.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Longer than any session should take to reach its exchange:
    /// one open this long with nothing running on it looks stuck.</summary>
    private static readonly TimeSpan StuckLeaseAge = TimeSpan.FromMinutes(5);

    /// <summary>Outcome of a single probe attempt. <see cref="Success"/>
    /// is true iff the prompt was observed end-to-end. <see cref="Error"/>
    /// is empty on success, populated with a short human-readable
    /// description on failure (so the dashboard can surface it).
    /// <see cref="DiscoveredPeers"/> is populated when the caller passed
    /// <c>fetchPeers: true</c> and the remote answered the
    /// <c>peers</c> query - Plan B6.1 Phase 2 transitive discovery.
    /// Empty when peers wasn't requested or the request failed; failure
    /// to fetch peers does not mark the probe itself as failed.
    /// <see cref="Detail"/> is set when the probe found a session with the
    /// peer already open, and so didn't dial: what it found, for the
    /// operator (on failure it is the error too).</summary>
    public sealed record ProbeResult(
        string Callsign,
        int BearerPort,
        bool Success,
        string Error,
        DateTime At,
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> DiscoveredPeers,
        string Detail = "");

    /// <summary>
    /// Connect to <paramref name="remoteCallsign"/> on
    /// <paramref name="bearerPort"/>, look for the <c>DAPPSv1&gt;</c>
    /// banner, optionally ask the remote for its peers (Plan B6.1
    /// Phase 2), then disconnect. Captures the most common AGW
    /// failure modes (no socket, AGW reject, no prompt) into a stable
    /// <see cref="ProbeResult.Error"/> string rather than throwing.
    ///
    /// When <paramref name="fetchPeers"/> is true, a successful probe
    /// continues with a <c>peers</c> query whose result is reported
    /// in <see cref="ProbeResult.DiscoveredPeers"/>. A failed peers
    /// query is logged but does not flip the probe to failed - the
    /// remote *did* answer DAPPSv1&gt; and that's still useful state.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken ct,
        bool fetchPeers = false,
        ConnectScript? connectScript = null,
        string? connectVia = null)
    {
        var at = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> peers = [];
        if (FindOpenSession(remoteCallsign) is { } open)
        {
            return await PingOpenSessionAsync(open, remoteCallsign, bearerPort, at, ct);
        }
        // Refused before dialling, so nothing goes on the air (#309).
        if (ConnectScriptRunner.Refusal(connectScript, logger) is { } refused)
        {
            return new ProbeResult(remoteCallsign, bearerPort, false, $"connect-script: {refused}", at, peers);
        }
        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: connectVia ?? remoteCallsign,
                bearerPort: bearerPort,
                stoppingToken: ct);

            var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

            // Connect-script: when the neighbour row carries one, drive
            // the chain of intermediate-node connects before falling
            // into the DAPPSv1 prompt. The script's final step reads
            // the prompt itself, so on success we skip the regular
            // ReadInitialPromptAsync.
            if (connectScript is not null)
            {
                try
                {
                    await ConnectScriptRunner.RunAsync(connection.Stream, connectScript, logger, ct);
                }
                catch (Exception ex) when (ex is ConnectScriptException or EndOfStreamException)
                {
                    return new ProbeResult(remoteCallsign, bearerPort, false,
                        $"connect-script: {ex.Message}", at, peers);
                }
            }
            else if (!await protocol.ReadInitialPromptAsync(ct))
            {
                return new ProbeResult(remoteCallsign, bearerPort, false,
                    "no DAPPSv1> prompt", at, peers);
            }

            if (fetchPeers)
            {
                try
                {
                    peers = await protocol.RequestPeersAsync(ct);
                    logger.LogInformation("Probe ok: {0} on port {1} (got {2} peer record(s))",
                        remoteCallsign, bearerPort, peers.Count);
                }
                catch (Exception ex)
                {
                    logger.LogInformation(
                        "Probe ok but peers query failed: {0} on port {1} ({2})",
                        remoteCallsign, bearerPort, ex.Message);
                }
            }
            else
            {
                logger.LogInformation("Probe ok: {0} on port {1}", remoteCallsign, bearerPort);
            }

            // Route gossip: piggyback when the staleness gate allows.
            // Probes are infrequent and the exchange is small; safe
            // to add unconditionally on success.
            if (routeGossip is not null)
            {
                try
                {
                    if (await routeGossip.ShouldPullAsync(remoteCallsign, ct))
                    {
                        var gossiped = await protocol.RequestRoutesAsync(ct);
                        await routeGossip.ImportAsync(remoteCallsign, gossiped, ct);
                        await routeGossip.RecordPulledAsync(remoteCallsign, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogInformation(
                        "Probe ok but routes gossip failed: {0} ({1})", remoteCallsign, ex.Message);
                }
            }

            return new ProbeResult(remoteCallsign, bearerPort, true, "", at, peers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation - re-throw so the scheduler can
            // exit cleanly on shutdown.
            throw;
        }
        catch (PeerSessionBusyException ex)
        {
            // Held under the callsign dialled: the connect-script's node, if any.
            return BusyResult(ex.PeerCallsign, bearerPort, at, remoteCallsign);
        }
        catch (TimeoutException ex)
        {
            logger.LogInformation("Probe timeout: {0} ({1})", remoteCallsign, ex.Message);
            return new ProbeResult(remoteCallsign, bearerPort, false, $"timeout: {ex.Message}", at, peers);
        }
        catch (Exception ex)
        {
            // Probes are best-effort; a connect failure is the answer,
            // not an exception to bubble. Log at info - failures are
            // expected and not actionable noise.
            logger.LogInformation("Probe failed: {0} ({1})", remoteCallsign, ex.Message);
            return new ProbeResult(remoteCallsign, bearerPort, false, ex.Message, at, peers);
        }
    }

    /// <summary>
    /// Plan B6.1 Phase 2b - probe through a BPQ node prompt rather
    /// than direct to a DAPPS APPLICATION callsign. Connect lands at
    /// the node's command prompt (e.g. <c>READNG:GB7RDG}</c>); we
    /// type <paramref name="applicationCommand"/> + CR (default
    /// <c>DAPPS</c>), expect the BPQ APPLICATION dispatcher to
    /// route us into the DAPPS slot, and then proceed with the
    /// regular <c>DAPPSv1&gt;</c> handshake + optional peers query.
    ///
    /// Banner heuristic: read until the wire goes idle for a short
    /// window (<see cref="DefaultBannerIdle"/>, 500 ms). Works for any
    /// BPQ-style prompt regardless of the operator's banner text - we
    /// don't pattern-match the prompt, just the silence after it.
    /// </summary>
    public async Task<ProbeResult> ProbeViaNodeCallAsync(
        string localCallsign,
        string remoteNodeCall,
        int bearerPort,
        CancellationToken ct,
        string applicationCommand = "DAPPS",
        bool fetchPeers = true)
    {
        var at = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> peers = [];
        // The command is typed at the far node's prompt under our call.
        // A value saved by an older DAPPS wasn't checked, and a CR in it
        // would make that node run a second command (#309): don't dial.
        if (NodePromptText.ApplicationCommandError(applicationCommand) is { } commandError)
        {
            if (NodePromptText.FirstTime("command:" + applicationCommand))
            {
                logger.LogWarning(
                    "Node-prompt probe of {0} refused, nothing sent: the application command '{1}' is not valid. {2} Fix it on the Settings page. " +
                    "(Logged once; every node-prompt probe is refused until then.)",
                    remoteNodeCall, NodePromptText.Printable(applicationCommand), commandError);
            }
            return new ProbeResult(remoteNodeCall, bearerPort, false,
                $"application command refused: {commandError}", at, peers);
        }
        if (FindOpenSession(remoteNodeCall) is { } open)
        {
            return await PingOpenSessionAsync(open, remoteNodeCall, bearerPort, at, ct);
        }
        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: remoteNodeCall,
                bearerPort: bearerPort,
                stoppingToken: ct);

            var stream = connection.Stream;

            // Read the node banner + prompt. We don't parse it - we
            // wait for the wire to go quiet, which is a reliable
            // proxy for "the prompt is now waiting for input".
            var banner = await ReadUntilIdleAsync(stream, DefaultBannerTotal, DefaultBannerIdle, ct);
            if (banner.Length == 0)
            {
                return new ProbeResult(remoteNodeCall, bearerPort, false,
                    "no banner from node prompt", at, peers);
            }
            logger.LogDebug("Node banner from {0}: {1}", remoteNodeCall, banner.Replace('\n', ' ').Replace('\r', ' '));

            // Send the application command. Plain CR - BPQ's node
            // accepts both \r and \r\n; CR-only matches what an
            // operator at a packet-radio terminal would send.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(applicationCommand + "\r").AsMemory(), ct);
            await stream.FlushAsync(ct);

            var protocol = new DappsProtocolClient(stream, loggerFactory);
            if (!await protocol.ReadInitialPromptAsync(ct))
            {
                return new ProbeResult(remoteNodeCall, bearerPort, false,
                    $"sent '{applicationCommand}' to node prompt but no DAPPSv1> reply (banner was: {Snippet(banner)})",
                    at, peers);
            }

            if (fetchPeers)
            {
                try
                {
                    peers = await protocol.RequestPeersAsync(ct);
                    logger.LogInformation(
                        "Node-prompt probe ok: {0} via '{1}' command on port {2} (got {3} peer record(s))",
                        remoteNodeCall, applicationCommand, bearerPort, peers.Count);
                }
                catch (Exception ex)
                {
                    logger.LogInformation(
                        "Node-prompt probe ok but peers query failed: {0} on port {1} ({2})",
                        remoteNodeCall, bearerPort, ex.Message);
                }
            }
            else
            {
                logger.LogInformation(
                    "Node-prompt probe ok: {0} via '{1}' command on port {2}",
                    remoteNodeCall, applicationCommand, bearerPort);
            }
            return new ProbeResult(remoteNodeCall, bearerPort, true, "", at, peers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PeerSessionBusyException ex)
        {
            return BusyResult(ex.PeerCallsign, bearerPort, at, remoteNodeCall);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Node-prompt probe failed: {0} ({1})", remoteNodeCall, ex.Message);
            return new ProbeResult(remoteNodeCall, bearerPort, false, ex.Message, at, peers);
        }
    }

    /// <summary>
    /// The running session with <paramref name="callsign"/>, if there is
    /// one: one we dialled for traffic, or one it opened with us (or a poll
    /// of ours). Dialling it again would send a second SABM down the live
    /// link, and the far node takes that for a reset (#178).
    /// </summary>
    private ExchangeSession? FindOpenSession(string callsign) =>
        sessionBackhaul?.FindOpenSession(callsign)
        ?? sessionDirectory?.Find(callsign);

    /// <summary>
    /// A session with the peer is already open, so rather than dial, ping
    /// inside it: a <c>pong</c> shows the link carries traffic both ways
    /// end to end. Either way the result says where the session stands.
    /// </summary>
    private async Task<ProbeResult> PingOpenSessionAsync(
        ExchangeSession session, string callsign, int bearerPort, DateTime at, CancellationToken ct)
    {
        var ping = await session.PingAsync(PingTimeout, ct);
        var status = session.Status;
        var now = timeProvider.GetUtcNow();
        var who = status.Dialled ? "we called it" : "it called us";
        var state = Describe(status, now);

        if (ping.RoundTrip is { } rtt)
        {
            var detail = $"Already in a session with {callsign} ({who}), so pinged inside it: answered in {Duration(rtt)}. {state}";
            logger.LogInformation("Probe ok: {0}", detail);
            return new ProbeResult(callsign, bearerPort, true, "", at, [], detail);
        }

        string error;
        if (!ping.Sent)
        {
            error = $"The session with {callsign} ({who}) was ending, so there was nothing to ping. Probe again in a few seconds. {state}";
        }
        else if (status.Ending)
        {
            // What a probe of a stalled session most often finds: the ping
            // went, nothing came back, and the session gave up on the link.
            error = $"In a session with {callsign} ({who}): pinged it, but no answer came before the session ended. {state} "
                + "The link was most likely not getting anything through. The next message or probe dials afresh.";
        }
        else
        {
            // Heard from it since the ping went, yet no pong: it's
            // talking, so it's most likely on a version without ping.
            var heardSince = status.LastHeard > now - PingTimeout;
            error = $"In a session with {callsign} ({who}), but no answer to a ping in {Duration(PingTimeout)}. {state} "
                + (heardSince
                    ? $"{callsign} is still sending, so it may be on a DAPPS older than 0.50.0, which doesn't answer pings."
                    : "Nothing is getting through from it: the link may be up at our node and dead beyond it.");
        }
        logger.LogInformation("Probe failed: {0}", error);
        return new ProbeResult(callsign, bearerPort, false, error, at, [], error);
    }

    /// <summary>
    /// The peer is held by a session here with nothing running on it to
    /// ping: a call still connecting, another probe or poll before its
    /// exchange, or one left over from a link that died without a
    /// disconnect. Says which direction and for how long.
    /// <paramref name="held"/> is the callsign the session is held under
    /// (the dialled one), <paramref name="callsign"/> the probe's target.
    /// </summary>
    private ProbeResult BusyResult(string heldCall, int bearerPort, DateTime at, string callsign)
    {
        var now = timeProvider.GetUtcNow();
        var held = peerSessions?.Sessions(heldCall) ?? [];
        string error;
        if (held.Count == 0)
        {
            error = $"A session with {heldCall} was open a moment ago and has just ended. Probe again.";
        }
        else
        {
            var oldest = held[0];
            var who = oldest.Direction == "inbound" ? "it called us" : "we called it";
            var age = now - oldest.Since;
            error = $"A session with {heldCall} is open ({who}, {Duration(age)} ago, "
                + (oldest.Connected ? "connected" : "still connecting")
                + ") but hasn't reached the DAPPS exchange, so there is nothing to ping.";
            if (age >= StuckLeaseAge)
            {
                error += " That is far longer than setting up takes. Unless someone is at the DAPPSv1> prompt by hand, it looks stuck: the link may have died without a disconnect, and restarting DAPPS clears it.";
            }
        }
        logger.LogInformation("Probe skipped: {0}", error);
        return new ProbeResult(callsign, bearerPort, false, error, at, [], error);
    }

    private static string Describe(SessionStatus s, DateTimeOffset now)
    {
        var text = $"Session up {Duration(now - s.Started)}, last heard from it {Duration(now - s.LastHeard)} ago";
        text += s.Unanswered == 0
            ? ", none of ours waiting for an answer"
            : $", {s.Unanswered} of ours waiting for an answer (oldest sent {Duration(now - s.OldestUnansweredSentAt!.Value)} ago)";
        if (s.Receiving > 0) text += $", {s.Receiving} of its messages on their way to us";
        return text + ".";
    }

    /// <summary>A short duration for people: 2.4s, 3m10s, 2h05m.</summary>
    internal static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t < TimeSpan.FromSeconds(10)) return $"{t.TotalSeconds:F1}s";
        if (t < TimeSpan.FromMinutes(1)) return $"{(int)t.TotalSeconds}s";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
        return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
    }

    private static readonly TimeSpan DefaultBannerTotal = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultBannerIdle = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Read from <paramref name="stream"/> until either no bytes have
    /// arrived for <paramref name="idleWindow"/> (treated as "the
    /// prompt is now idle, waiting for input") or
    /// <paramref name="totalTimeout"/> elapses overall. Returns
    /// whatever ASCII bytes were collected - empty string when no
    /// data ever arrived (which the caller surfaces as a failure).
    ///
    /// Internal so the unit test can substitute its own fake stream
    /// without going through the transport layer.
    /// </summary>
    internal static async Task<string> ReadUntilIdleAsync(
        Stream stream,
        TimeSpan totalTimeout,
        TimeSpan idleWindow,
        CancellationToken ct)
    {
        var buffer = new byte[2048];
        var sb = new StringBuilder();
        var totalDeadline = DateTime.UtcNow + totalTimeout;
        while (true)
        {
            var remaining = totalDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return sb.ToString();
            // Once we've seen any bytes, switch from the long total
            // wait to the short idle window - silence after data
            // means the prompt has finished writing.
            var perReadTimeout = sb.Length > 0
                ? (idleWindow < remaining ? idleWindow : remaining)
                : remaining;
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            iterCts.CancelAfter(perReadTimeout);
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(), iterCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Idle / total deadline - return what we have.
                return sb.ToString();
            }
            if (n == 0) return sb.ToString();   // EOF
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
    }

    private static string Snippet(string s)
    {
        var clean = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 80 ? clean : clean[..77] + "...";
    }
}
