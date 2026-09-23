using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Pulls pending outbound messages from the queue, computes residual
/// TTL, asks the configured <see cref="IRoutingAlgorithm"/> for a
/// route, and hands each message to a matching <see cref="IDappsBackhaul"/>
/// for delivery. Owns queue / dispatch concerns; routing strategy
/// itself lives behind <see cref="IRoutingAlgorithm"/> (B5 seam) so
/// algorithms (static, passive-learning, AODV-flood, NET-ROM-style,
/// MeshCore-style, …) can be swapped without touching this code.
///
/// The forward-outcome and inbound observation hooks are also routed
/// through the algorithm so it can update its internal state (learned
/// routes, failure counters, sequence numbers).
/// </summary>
public class OutboundMessageManager(
    Database database,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<SystemOptions> options,
    IEnumerable<IDappsBackhaul> backhauls,
    IRoutingAlgorithm routingAlgorithm,
    IRoutingContext routingContext,
    OperationalMetrics? metrics = null,
    OutboundActivityTracker? activityTracker = null,
    TransmissionAuditService? transmissionAudit = null,
    OutboundDestinationBackoff? destinationBackoff = null,
    PeerSessionRegistry? peerSessions = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<OutboundMessageManager>();
    private readonly IReadOnlyList<IDappsBackhaul> backhauls = backhauls.ToList();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();
    private readonly OutboundDestinationBackoff destinationBackoff = destinationBackoff ?? new OutboundDestinationBackoff();

    /// <summary>
    /// Mutex on <see cref="DoRun"/> so concurrent triggers
    /// (background ticker + manual <c>POST /Message/dorun</c>, or two
    /// manual POSTs in flight) don't race through the same pending
    /// list and double-send. Calls that arrive while a run is
    /// in-flight return immediately - whatever's pending will be
    /// picked up on the next tick anyway.
    /// </summary>
    private readonly SemaphoreSlim runLock = new(1, 1);

    /// <summary>
    /// Internal counter incremented at the start of each *actually
    /// executed* run (skipped contended calls don't bump it). Used by
    /// the auto-forwarder integration test to verify the background
    /// service is ticking; not exposed to operators.
    /// </summary>
    internal int RunCount;

    public async Task DoRun(CancellationToken stoppingToken = default)
    {
        if (!await runLock.WaitAsync(0, stoppingToken))
        {
            logger.LogDebug("DoRun skipped: another run is already in flight");
            return;
        }
        try
        {
            await DoRunCore(stoppingToken);
        }
        finally
        {
            runLock.Release();
        }
    }

    private async Task DoRunCore(CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref RunCount);
        logger.LogInformation("Starting a run");

        var optionsValue = options.CurrentValue;
        var messages = await database.GetPendingOutboundMessages();
        // Peers we have already logged a #178 deferral for this run, so
        // a queue of several messages for one busy peer costs one line.
        var deferredPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in messages)
        {
            var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
            if (residualTtl is <= 0)
            {
                logger.LogWarning("Dropping message {0} for {1}: ttl expired ({2}s queued, original ttl={3}s)",
                    message.Id, message.Destination,
                    (int)(DateTime.UtcNow - message.CreatedAt).TotalSeconds, message.Ttl);
                metrics.RecordTtlExpired(message.Id, message.Destination);
                await database.SoftDeleteMessage(message.Id, "ttl-expired");
                continue;
            }

            var decision = await routingAlgorithm.ResolveAsync(message, routingContext, stoppingToken);

            // F1: preserve the originating callsign verbatim across re-forwards.
            // Empty means we don't know - outbound omits src= rather than lying
            // (e.g. claiming the link source is the originator).
            var originator = string.IsNullOrEmpty(message.OriginatorCallsign)
                ? null
                : message.OriginatorCallsign;

            switch (decision)
            {
                case RouteDecision.NextHop nh:
                    if (destinationBackoff.IsInCooldown(nh.Route.Callsign, out var nextRetryAtUtc))
                    {
                        logger.LogDebug(
                            "Skipping {0}: {1} is in reconnect cooldown until {2:O}",
                            message.Id, nh.Route.Callsign, nextRetryAtUtc);
                        break;
                    }
                    if (WouldDialIntoOpenSession(nh.Route, out var openDirection))
                    {
                        // #178: dialling now would send a SABM down the
                        // live link and reset the session at both ends.
                        // Leave the message queued: if the peer polls
                        // (rev) on that session it drains it anyway, else
                        // the next tick dials once the session has ended.
                        if (deferredPeers.Add(nh.Route.Callsign))
                        {
                            logger.LogInformation(
                                "Deferring {0}: an {1} session with {2} is already open, dialling now would reset it",
                                message.Id, openDirection, nh.Route.Callsign);
                        }
                        break;
                    }
                    var bm = new BackhaulMessage(
                        Id: message.Id,
                        Destination: message.Destination,
                        Salt: message.Salt,
                        Ttl: residualTtl,
                        Payload: message.Payload,
                        Originator: originator,
                        SourceRoute: nh.SourceRoute,
                        // F2 multi-part: forwarder re-emits mid= + frag=N/M
                        // verbatim so the message stays groupable across hops.
                        MasterId: message.MasterId,
                        FragmentIndex: message.FragmentIndex,
                        FragmentTotal: message.FragmentTotal,
                        // Opt-in ordering: stream trio is end-to-end at the
                        // originator's intent; intermediate hops re-emit
                        // verbatim so the destination sees the originator's
                        // gap-timeout policy regardless of forwarding path.
                        StreamId: message.StreamId,
                        StreamSeq: message.StreamSeq,
                        StreamGapTimeoutSeconds: message.StreamGapTimeoutSeconds);
                    await ForwardAndObserveAsync(message, nh.Route, bm, optionsValue, stoppingToken);
                    break;

                case RouteDecision.FloodToNeighbours flood:
                    await FloodAndMarkAsync(message, flood, residualTtl, originator, optionsValue, stoppingToken);
                    break;

                case RouteDecision.Unreachable:
                    logger.LogWarning("No route for {0}, leaving in queue", message.Id);
                    metrics.RecordNoRoute(message.Id, message.Destination);
                    break;

                default:
                    // Future RouteDecision cases (SourceRoute, BearerDelegated)
                    // will land here when those algorithms ship; today they
                    // don't appear, so a defensive log keeps a regression
                    // visible rather than silently no-op'ing.
                    logger.LogError("Unsupported RouteDecision {0} for {1}; treating as Unreachable",
                        decision.GetType().Name, message.Id);
                    metrics.RecordNoRoute(message.Id, message.Destination);
                    break;
            }
        }
    }

    private async Task ForwardAndObserveAsync(
        DbMessage message, BackhaulRoute route, BackhaulMessage bm,
        SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        var backhaul = backhauls.FirstOrDefault(b => b.CanHandle(route));
        if (backhaul is null)
        {
            logger.LogError(
                "No backhaul accepts route to {0} (BearerPort={1}, UdpEndpoint={2}). Skipping {3}.",
                route.Callsign, route.BearerPort, route.UdpEndpoint, message.Id);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
        sw.Stop();
        if (result.Deferred)
        {
            // #178: the bearer resolved a crossed connect by serving the
            // peer's session on the link instead of pushing. Nothing
            // failed, so no cooldown and no outcome for the route to
            // learn from; the message is still pending for the next
            // tick, unless the peer's rev on that session drained it.
            logger.LogInformation("Deferred {0}: {1}", message.Id, result.Error);
        }
        else
        {
            await routingAlgorithm.ObserveForwardOutcomeAsync(message, route, result, routingContext, stoppingToken);
            if (result.Accepted)
            {
                logger.LogInformation("Remote end accepted message {0} (via {1})", message.Id, backhaul.GetType().Name);
                metrics.RecordForwardSuccess(message.Id, route.Callsign, message.Payload.Length);
                activityTracker?.RecordTransmission();
                await database.MarkMessageAsForwarded(message.Id);
                destinationBackoff.RecordSuccess(route.Callsign);
            }
            else
            {
                var nextRetryAtUtc = destinationBackoff.RecordFailure(route.Callsign);
                logger.LogError("Failed to forward message {0} to {1} via {2}: {3} (retrying no earlier than {4:O})",
                    message.Id, route.Callsign, backhaul.GetType().Name, result.Error, nextRetryAtUtc);
                metrics.RecordForwardFailure(message.Id, route.Callsign, message.Payload.Length, result.Error);
            }
        }
        if (transmissionAudit is { } ta)
        {
            await ta.RecordAsync(
                kind: "forward",
                bearer: route.MeshCoreChannel is not null ? "meshcore"
                    : route.UdpEndpoint is not null ? "udp" : "agw",
                channelKey: route.BearerPort?.ToString() ?? "",
                targetCallsign: route.Callsign,
                messageId: message.Id,
                bytes: message.Payload.Length,
                reason: $"forwarder tick: route via {route.Callsign}",
                success: result.Accepted,
                durationMs: (int)sw.ElapsedMilliseconds,
                errorTag: result.Accepted ? "" : result.Deferred ? "deferred" : (result.Error ?? "unknown"));
        }
    }

    private async Task FloodAndMarkAsync(
        DbMessage message, RouteDecision.FloodToNeighbours flood, int? residualTtl,
        string? originator, SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        // Each flooded copy is its own send + outcome observation. Even
        // partial success counts as "we did our part" - mark forwarded
        // after iterating regardless. The receivers' dedup ledger
        // (DbFloodSeen) handles duplicates that arrive at any node from
        // multiple flood paths.
        logger.LogInformation(
            "Flooding {0} for {1} to {2} neighbour(s) with hop budget {3}",
            message.Id, message.Destination, flood.Routes.Count, flood.HopBudget);

        foreach (var route in flood.Routes)
        {
            if (WouldDialIntoOpenSession(route, out var openDirection))
            {
                // #178: a flood copy is one-shot, so this one is skipped
                // rather than deferred - the outcome a failed send to
                // that neighbour already had, minus the collision on
                // air and the failure on its streak.
                logger.LogInformation(
                    "Flood of {0}: skipping {1}, an {2} session with it is already open",
                    message.Id, route.Callsign, openDirection);
                continue;
            }

            var bm = new BackhaulMessage(
                Id: message.Id,
                Destination: message.Destination,
                Salt: message.Salt,
                Ttl: residualTtl,
                Payload: message.Payload,
                Originator: originator,
                FloodHopsRemaining: flood.HopBudget,
                TraversedHops: flood.TraversedHops,
                MasterId: message.MasterId,
                FragmentIndex: message.FragmentIndex,
                FragmentTotal: message.FragmentTotal,
                StreamId: message.StreamId,
                StreamSeq: message.StreamSeq,
                StreamGapTimeoutSeconds: message.StreamGapTimeoutSeconds);

            var backhaul = backhauls.FirstOrDefault(b => b.CanHandle(route));
            if (backhaul is null) continue;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
            sw.Stop();
            if (result.Deferred)
            {
                // #178: crossed connect, served instead of pushed. This
                // copy is lost like a failed one (floods are one-shot)
                // but it is not a failure of the neighbour.
                logger.LogInformation("Flood of {0}: deferred, {1}", message.Id, result.Error);
            }
            else
            {
                await routingAlgorithm.ObserveForwardOutcomeAsync(message, route, result, routingContext, stoppingToken);
                if (result.Accepted)
                {
                    metrics.RecordForwardSuccess(message.Id, route.Callsign, message.Payload.Length);
                    activityTracker?.RecordTransmission();
                    destinationBackoff.RecordSuccess(route.Callsign);
                }
                else
                {
                    // Floods are one-shot (no retry of this message), so we
                    // don't skip on cooldown here the way NextHop does - but
                    // still record the failure so a neighbour that's
                    // currently failing NextHop sends is also reflected in
                    // its streak, and so a burst of floods to the same
                    // failing neighbour doesn't reset a streak NextHop is
                    // tracking.
                    destinationBackoff.RecordFailure(route.Callsign);
                    metrics.RecordForwardFailure(message.Id, route.Callsign, message.Payload.Length, result.Error);
                }
            }
            if (transmissionAudit is { } ta)
            {
                await ta.RecordAsync(
                    kind: "forward-flood",
                    bearer: route.MeshCoreChannel is not null ? "meshcore"
                    : route.UdpEndpoint is not null ? "udp" : "agw",
                    channelKey: route.BearerPort?.ToString() ?? "",
                    targetCallsign: route.Callsign,
                    messageId: message.Id,
                    bytes: message.Payload.Length,
                    reason: $"flood to neighbour (hop budget {flood.HopBudget})",
                    success: result.Accepted,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    errorTag: result.Accepted ? "" : result.Deferred ? "deferred" : (result.Error ?? "unknown"));
            }
        }

        // Mark the message forwarded so it doesn't keep flooding on
        // every tick. If the destination was unreachable the flood is
        // effectively lost - that's correct semantics; floods are
        // best-effort.
        await database.MarkMessageAsForwarded(message.Id);
    }

    /// <summary>
    /// #178: true when <paramref name="route"/> rides a connected-mode
    /// session (AGW or RHP - the routes <see cref="Dappsv1SessionBackhaul"/>
    /// claims) and a session with that peer is already open in either
    /// direction. UDP and MeshCore are datagram bearers with no link to
    /// collide on, so their routes are never deferred.
    /// </summary>
    private bool WouldDialIntoOpenSession(BackhaulRoute route, out string? openDirection)
    {
        openDirection = null;
        if (peerSessions is null || route.UdpEndpoint is not null || route.MeshCoreChannel is not null) return false;
        return peerSessions.IsActive(route.Callsign, out openDirection);
    }
}
