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
/// underlying <see cref="IDappsOutboundTransport"/> opens a fresh TCP
/// socket per probe, just as the forwarder does.
/// </summary>
public sealed class NodeProber(
    IDappsOutboundTransport transport,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<NodeProber> logger,
    IRouteGossipPort? routeGossip = null)
{
    /// <summary>Outcome of a single probe attempt. <see cref="Success"/>
    /// is true iff the prompt was observed end-to-end. <see cref="Error"/>
    /// is empty on success, populated with a short human-readable
    /// description on failure (so the dashboard can surface it).
    /// <see cref="DiscoveredPeers"/> is populated when the caller passed
    /// <c>fetchPeers: true</c> and the remote answered the
    /// <c>peers</c> query - Plan B6.1 Phase 2 transitive discovery.
    /// Empty when peers wasn't requested or the request failed; failure
    /// to fetch peers does not mark the probe itself as failed.</summary>
    public sealed record ProbeResult(
        string Callsign,
        int BearerPort,
        bool Success,
        string Error,
        DateTime At,
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> DiscoveredPeers);

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
        ConnectScript? connectScript = null)
    {
        var at = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> peers = [];
        // A connect-script can name the first hop; dial that, not the peer.
        (var dialCallsign, connectScript) = ConnectScript.ResolveFirstHop(remoteCallsign, connectScript);
        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: dialCallsign,
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
        catch (Exception ex)
        {
            logger.LogInformation("Node-prompt probe failed: {0} ({1})", remoteNodeCall, ex.Message);
            return new ProbeResult(remoteNodeCall, bearerPort, false, ex.Message, at, peers);
        }
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
