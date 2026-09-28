using System.Collections.Concurrent;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Pulls pending outbound messages from the queue, computes residual
/// TTL, asks the configured <see cref="IRoutingAlgorithm"/> for a
/// route, and hands the messages to a matching <see cref="IDappsBackhaul"/>
/// for delivery, batched per next hop so a session-based bearer carries
/// everything queued for one neighbour on one connection. Owns queue / dispatch concerns; routing strategy
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
    PeerSessionRegistry? peerSessions = null,
    SessionDirectory? openSessions = null)
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
    /// Messages handed to a bearer and not yet completed. A session held
    /// open to a neighbour (#187 proposal 9) sends in the background while
    /// later runs go on, so a message is claimed when it goes out and
    /// released when its outcome is recorded: it can never be sent twice
    /// at once.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    /// <summary>Outcomes arrive from background sessions as well as from
    /// the run itself; routing and metrics see them one at a time.</summary>
    private readonly SemaphoreSlim outcomeGate = new(1, 1);

    /// <summary>
    /// Next hops that answered <c>no</c> to a message, with what they
    /// said, by <see cref="RefusalKey"/>. The message stays queued for
    /// another route; if the next run can only find one of these, it is
    /// dropped with their reason. Each run forgets the entries for messages
    /// no longer queued, however they left. Only in memory: after a restart
    /// a refusing hop is asked once more, and says no again.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> refusals = new(StringComparer.Ordinal);

    /// <summary>A message's id with its salt and length: the id alone is
    /// only 28 bits of hash.</summary>
    private static string RefusalKey(DbMessage message) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{message.Id}|{message.Salt}|{message.Payload.Length}");

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
        var runStartedAt = DateTime.UtcNow;
        var messages = await database.GetPendingOutboundMessages();

        // Every message this run has looked at. A batch that goes back
        // to the queue for more only picks up messages queued since.
        var seen = new HashSet<string>(messages.Select(m => m.Id));

        // Refusals of messages that have left the queue since (expired,
        // deleted, sent another way) are no longer wanted.
        var queuedKeys = messages.Select(RefusalKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in refusals.Keys.Where(k => !queuedKeys.Contains(k)).ToList()) refusals.TryRemove(key, out _);

        // Messages for the same next hop go out together on one session
        // rather than one session each. The work runs in queue order: a
        // batch where its first message sits in the queue, a flood where
        // its message does.
        var batches = new Dictionary<BackhaulRoute, NextHopBatch>(SameLinkComparer.Instance);
        var callerBatches = new Dictionary<string, NextHopBatch>(StringComparer.OrdinalIgnoreCase);
        var work = new List<object>();

        foreach (var message in messages)
        {
            // Already going out on a session.
            if (inFlight.ContainsKey(message.Id)) continue;

            var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
            if (residualTtl is <= 0)
            {
                logger.LogWarning("Dropping message {0} for {1}: ttl expired ({2}s queued, original ttl={3}s)",
                    message.Id, message.Destination,
                    (int)(DateTime.UtcNow - message.CreatedAt).TotalSeconds, message.Ttl);
                metrics.RecordTtlExpired(message.Id, message.Destination);
                await database.SoftDeleteMessage(message.Id, "ttl-expired");
                refusals.TryRemove(RefusalKey(message), out _);
                continue;
            }

            var decision = await routingAlgorithm.ResolveAsync(message, routingContext, stoppingToken);

            if (refusals.TryGetValue(RefusalKey(message), out var refusedBy)
                && !(decision is RouteDecision.NextHop other && !refusedBy.ContainsKey(other.Route.Callsign)))
            {
                // Refused by its next hop, and there's no other way to send it.
                var reason = string.Join("; ", refusedBy.Values);
                logger.LogWarning("Dropping message {0} for {1}: no route left that will take it ({2})",
                    message.Id, message.Destination, reason);
                await database.SoftDeleteMessage(message.Id, "refused: " + reason);
                refusals.TryRemove(RefusalKey(message), out _);
                continue;
            }

            switch (decision)
            {
                case RouteDecision.NextHop nh:
                    if (!batches.TryGetValue(nh.Route, out var batch))
                    {
                        batch = new NextHopBatch(this, nh.Route, runStartedAt, seen);
                        batches.Add(nh.Route, batch);
                        work.Add(batch);
                    }
                    batch.Add(message, nh.SourceRoute);
                    break;

                case RouteDecision.FloodToNeighbours flood:
                    work.Add(new PendingFlood(message, flood));
                    break;

                case RouteDecision.Unreachable when openSessions?.CallerFor(message.Destination) is { } caller:
                    // No route, but the node it's for has a session open
                    // with us: it goes on that session (what `rev` used to
                    // collect), and nowhere else. We have no route to dial.
                    if (!callerBatches.TryGetValue(caller, out var callerBatch))
                    {
                        callerBatch = new NextHopBatch(this, new BackhaulRoute(caller), runStartedAt, seen) { HandOffOnly = true };
                        callerBatches.Add(caller, callerBatch);
                        work.Add(callerBatch);
                    }
                    callerBatch.Add(message, null);
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

        foreach (var item in work)
        {
            switch (item)
            {
                case NextHopBatch batch:
                    await SendBatchAsync(batch, optionsValue, stoppingToken);
                    break;
                case PendingFlood flood:
                    // Residual TTL as of now, after the work ahead of it.
                    var residualTtl = TtlMath.Residual(flood.Message.Ttl, flood.Message.CreatedAt, DateTime.UtcNow);
                    if (residualTtl is <= 0)
                    {
                        logger.LogInformation("Not flooding {0}: its ttl ran out while it waited its turn", flood.Message.Id);
                        break;
                    }
                    await FloodAndMarkAsync(flood.Message, flood.Decision, residualTtl,
                        OriginatorOf(flood.Message), optionsValue, stoppingToken);
                    break;
            }
        }
    }

    /// <summary>
    /// How many messages queued after a run started one batch will pick
    /// up on top of what it started with. The forwarder serves one
    /// neighbour at a time, so without a limit a neighbour with a steady
    /// stream of traffic could keep the others waiting indefinitely.
    /// </summary>
    internal const int MaxPickedUpPerBatch = 20;

    private sealed record PendingFlood(DbMessage Message, RouteDecision.FloodToNeighbours Decision);

    private async Task SendBatchAsync(NextHopBatch batch, SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        var route = batch.Route;
        var backhaul = backhauls.FirstOrDefault(b => b.CanHandle(route));
        if (backhaul is null)
        {
            logger.LogError(
                "No backhaul accepts route to {0} (BearerPort={1}, UdpEndpoint={2}). Skipping {3} message(s).",
                route.Callsign, route.BearerPort, route.UdpEndpoint, batch.Count);
            return;
        }
        batch.BackhaulName = backhaul.GetType().Name;

        // A session already open with this neighbour takes the batch at
        // once, in the background: one we dialled, or one it dialled.
        // It's a live link, so this goes ahead even in a cooldown.
        batch.Detached = true;
        if (backhaul.TryHandToOpenSession(route, batch))
        {
            logger.LogInformation("Handed {0} message(s) for {1} to the session we have open with it", batch.Count, route.Callsign);
            return;
        }
        if (RidesASession(route) && openSessions is not null && openSessions.TryHand(route.Callsign, batch))
        {
            logger.LogInformation("Handed {0} message(s) for {1} to the session open with it", batch.Count, route.Callsign);
            return;
        }
        batch.Detached = false;

        if (batch.HandOffOnly)
        {
            // Its session ended before the batch got there, and with no
            // route there's nothing to dial: the messages stay queued.
            logger.LogInformation("Leaving {0} message(s) for {1} queued: its session has ended and there's no route to it",
                batch.Count, route.Callsign);
            return;
        }

        if (destinationBackoff.IsInCooldown(route.Callsign, out var nextRetryAtUtc))
        {
            logger.LogDebug(
                "Skipping {0} message(s) for {1}: in reconnect cooldown until {2:O}",
                batch.Count, route.Callsign, nextRetryAtUtc);
            return;
        }

        if (WouldDialIntoOpenSession(route, out var openDirection))
        {
            // #178: dialling now would send a SABM down the live link
            // and reset the session at both ends. That session isn't
            // established yet (or it would have taken the batch above),
            // so leave the messages queued: the forwarder runs again when
            // it is, or when it ends.
            logger.LogInformation(
                "Deferring {0} message(s) for {1}: an {2} session with it is already open, dialling now would reset it",
                batch.Count, route.Callsign, openDirection);
            return;
        }

        await backhaul.SendBatchAsync(route, optionsValue.Callsign, batch, stoppingToken);
    }

    private BackhaulMessage ToBackhaulMessage(DbMessage message, int? residualTtl, IReadOnlyList<string>? sourceRoute) =>
        new(
            Id: message.Id,
            Destination: message.Destination,
            Salt: message.Salt,
            Ttl: residualTtl,
            Payload: message.Payload,
            Originator: OriginatorOf(message),
            SourceRoute: sourceRoute,
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

    /// <summary>
    /// F1: preserve the originating callsign verbatim across re-forwards.
    /// Null means we don't know - outbound omits src= rather than lying
    /// (e.g. claiming the link source is the originator).
    /// </summary>
    private static string? OriginatorOf(DbMessage message) =>
        string.IsNullOrEmpty(message.OriginatorCallsign) ? null : message.OriginatorCallsign;

    // For NextHopBatch: a nested class can't see the primary-constructor
    // parameters these wrap.
    private Task<ICollection<DbMessage>> PendingOutboundQueuedSinceAsync(DateTime since) =>
        database.GetPendingOutboundMessagesQueuedSince(since);
    private Task<bool> IsStillQueuedAsync(string id) => database.IsStillQueued(id);
    private Task<RouteDecision> ResolveAsync(DbMessage message, CancellationToken ct) =>
        routingAlgorithm.ResolveAsync(message, routingContext, ct);

    private async Task RecordForwardOutcomeAsync(
        DbMessage message, BackhaulRoute route, string backhaulName, BackhaulSendResult result,
        TimeSpan elapsed, CancellationToken stoppingToken)
    {
        try
        {
            await outcomeGate.WaitAsync(stoppingToken);
            try
            {
                await RecordForwardOutcomeCoreAsync(message, route, backhaulName, result, elapsed, stoppingToken);
            }
            finally
            {
                outcomeGate.Release();
            }
        }
        finally
        {
            // Released whatever happened recording it, or a failure here
            // would leave the message unsendable until a restart.
            inFlight.TryRemove(message.Id, out _);
        }
    }

    private async Task RecordForwardOutcomeCoreAsync(
        DbMessage message, BackhaulRoute route, string backhaulName, BackhaulSendResult result,
        TimeSpan elapsed, CancellationToken stoppingToken)
    {
        if (result.Deferred)
        {
            // Not sent, and nothing failed: the session ended before it
            // went, or the peer was busy. No cooldown and no outcome for
            // the route to learn from; it's still queued for the next run.
            logger.LogInformation("Deferred {0}: {1}", message.Id, result.Error);
        }
        else if (result.Refused)
        {
            // The neighbour is fine; it just won't take this one. The next
            // run sends it another way if there is one, else drops it.
            logger.LogWarning("{0} won't take message {1} ({2}); it goes another way if there is one", route.Callsign, message.Id, result.Error);
            refusals.GetOrAdd(RefusalKey(message), _ => new(StringComparer.OrdinalIgnoreCase))[route.Callsign] = result.Error ?? "refused";
        }
        else
        {
            await routingAlgorithm.ObserveForwardOutcomeAsync(message, route, result, routingContext, stoppingToken);
            if (result.Accepted)
            {
                logger.LogInformation("Remote end accepted message {0} (via {1})", message.Id, backhaulName);
                metrics.RecordForwardSuccess(message.Id, route.Callsign, message.Payload.Length);
                activityTracker?.RecordTransmission();
                await database.MarkMessageAsForwarded(message.Id);
                destinationBackoff.RecordSuccess(route.Callsign);
                refusals.TryRemove(RefusalKey(message), out _);
            }
            else
            {
                var nextRetryAtUtc = destinationBackoff.RecordFailure(route.Callsign);
                logger.LogError("Failed to forward message {0} to {1} via {2}: {3} (retrying no earlier than {4:O})",
                    message.Id, route.Callsign, backhaulName, result.Error, nextRetryAtUtc);
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
                durationMs: (int)elapsed.TotalMilliseconds,
                errorTag: ErrorTag(result));
        }
    }

    private static string ErrorTag(BackhaulSendResult result) =>
        result.Accepted ? "" : result.Deferred ? "deferred" : result.Refused ? "refused" : (result.Error ?? "unknown");

    /// <summary>
    /// The messages one run has for one next hop, handed to the backhaul
    /// as an <see cref="IBackhaulBatch"/>. Starts with what the queue
    /// held when the run began; once those are out it looks at the queue
    /// again and adds anything since queued for the same next hop (up to
    /// <see cref="MaxPickedUpPerBatch"/>), so traffic that arrives while
    /// the session is open goes on it instead of waiting for a new one.
    ///
    /// A message's residual TTL is worked out when it is handed out, not
    /// when the run began, so time spent on the messages ahead of it
    /// counts. One that expired while it waited isn't sent; the next run
    /// drops it.
    /// </summary>
    private sealed class NextHopBatch(OutboundMessageManager owner, BackhaulRoute route, DateTime runStartedAt, HashSet<string> seen) : IBackhaulBatch
    {
        private readonly Queue<(DbMessage Row, IReadOnlyList<string>? SourceRoute)> queued = new();
        private readonly Dictionary<string, DbMessage> handedOut = new();
        private int pickedUp;

        public BackhaulRoute Route => route;

        /// <summary>Messages not yet handed to the backhaul.</summary>
        public int Count => queued.Count;

        public string BackhaulName { get; set; } = "";

        /// <summary>
        /// Handed to a session that sends in the background, while this
        /// and later runs go on. It then only sends what it was given:
        /// looking at the queue again would race the run that made it.
        /// </summary>
        public bool Detached { get; set; }

        /// <summary>Only for a session already open: never dialled for.</summary>
        public bool HandOffOnly { get; init; }

        public void Add(DbMessage row, IReadOnlyList<string>? sourceRoute) => queued.Enqueue((row, sourceRoute));

        public async ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            while (true)
            {
                if (queued.Count == 0 && !Detached) await PickUpNewlyQueuedAsync(ct);
                if (!queued.TryDequeue(out var next)) return null;

                var residualTtl = TtlMath.Residual(next.Row.Ttl, next.Row.CreatedAt, DateTime.UtcNow);
                if (residualTtl is <= 0)
                {
                    owner.logger.LogInformation("Not sending {0}: its ttl ran out while it waited its turn", next.Row.Id);
                    continue;
                }

                // Claim it, and make sure a session running alongside
                // hasn't sent it since the queue was read.
                if (!owner.inFlight.TryAdd(next.Row.Id, 0)) continue;
                bool stillQueued;
                try
                {
                    stillQueued = await owner.IsStillQueuedAsync(next.Row.Id);
                }
                catch
                {
                    owner.inFlight.TryRemove(next.Row.Id, out _);
                    throw;
                }
                if (!stillQueued)
                {
                    owner.inFlight.TryRemove(next.Row.Id, out _);
                    continue;
                }
                handedOut[next.Row.Id] = next.Row;
                return owner.ToBackhaulMessage(next.Row, residualTtl, next.SourceRoute);
            }
        }

        public async ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct) =>
            await owner.RecordForwardOutcomeAsync(handedOut[message.Id], route, BackhaulName, result, elapsed, ct);

        private async Task PickUpNewlyQueuedAsync(CancellationToken ct)
        {
            if (pickedUp >= MaxPickedUpPerBatch) return;
            foreach (var row in await owner.PendingOutboundQueuedSinceAsync(runStartedAt))
            {
                // Once looked at, a message is the next run's to handle
                // if it isn't ours: no second look this run.
                if (!seen.Add(row.Id) || owner.inFlight.ContainsKey(row.Id)) continue;

                // Expired: the next run drops it with the usual log line.
                var residualTtl = TtlMath.Residual(row.Ttl, row.CreatedAt, DateTime.UtcNow);
                if (residualTtl is <= 0) continue;

                var decision = await owner.ResolveAsync(row, ct);
                if (decision is not RouteDecision.NextHop nh || !SameLinkComparer.Instance.Equals(nh.Route, route)) continue;

                owner.logger.LogInformation("Picked up {0} for {1}, queued while its session was open", row.Id, route.Callsign);
                queued.Enqueue((row, nh.SourceRoute));
                if (++pickedUp >= MaxPickedUpPerBatch) return;
            }
        }
    }

    /// <summary>
    /// Two routes that reach the neighbour the same way, so their
    /// messages can share a session. <see cref="BackhaulRoute"/>'s own
    /// equality would compare a connect script's step list by reference.
    /// </summary>
    private sealed class SameLinkComparer : IEqualityComparer<BackhaulRoute>
    {
        public static readonly SameLinkComparer Instance = new();

        public bool Equals(BackhaulRoute? x, BackhaulRoute? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Callsign, y.Callsign, StringComparison.OrdinalIgnoreCase)
                && x.BearerPort == y.BearerPort
                && x.UdpEndpoint == y.UdpEndpoint
                && x.MeshCoreChannel == y.MeshCoreChannel
                && (x.ConnectScript?.Steps ?? []).SequenceEqual(y.ConnectScript?.Steps ?? []);
        }

        public int GetHashCode(BackhaulRoute route) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(route.Callsign),
            route.BearerPort, route.UdpEndpoint, route.MeshCoreChannel);
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

            // A session open with the neighbour, either way, takes the
            // copy: with sessions held for minutes at a time, skipping
            // them would lose most floods.
            var copy = new FloodCopyBatch(this, message, route, bm, flood.HopBudget);
            if (backhaul.TryHandToOpenSession(route, copy))
            {
                logger.LogInformation("Flood of {0}: handed to the session we have open with {1}", message.Id, route.Callsign);
                continue;
            }
            if (RidesASession(route) && openSessions is not null && openSessions.TryHand(route.Callsign, copy))
            {
                logger.LogInformation("Flood of {0}: handed to the session open with {1}", message.Id, route.Callsign);
                continue;
            }
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
            await RecordFloodOutcomeAsync(message, route, flood.HopBudget, result, sw.Elapsed, stoppingToken);
        }

        // Mark the message forwarded so it doesn't keep flooding on
        // every tick. If the destination was unreachable the flood is
        // effectively lost - that's correct semantics; floods are
        // best-effort.
        await database.MarkMessageAsForwarded(message.Id);
    }

    private async Task RecordFloodOutcomeAsync(
        DbMessage message, BackhaulRoute route, byte hopBudget, BackhaulSendResult result,
        TimeSpan elapsed, CancellationToken stoppingToken)
    {
        await outcomeGate.WaitAsync(stoppingToken);
        try
        {
            if (result.Deferred || result.Refused)
            {
                // Not sent, or not wanted: this copy is lost like a failed
                // one (floods are one-shot), but it is not a failure of
                // the neighbour.
                logger.LogInformation("Flood of {0}: {1}, {2}", message.Id, result.Refused ? "refused" : "deferred", result.Error);
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
                    reason: $"flood to neighbour (hop budget {hopBudget})",
                    success: result.Accepted,
                    durationMs: (int)elapsed.TotalMilliseconds,
                    errorTag: ErrorTag(result));
            }
        }
        finally
        {
            outcomeGate.Release();
        }
    }

    /// <summary>
    /// One flood copy for a neighbour, handed to a session already open
    /// with it. The message itself is marked forwarded once every copy
    /// has gone or been handed over, as with any flood; this only carries
    /// the copy and reports how it went.
    /// </summary>
    private sealed class FloodCopyBatch(
        OutboundMessageManager owner, DbMessage row, BackhaulRoute route, BackhaulMessage copy, byte hopBudget) : IBackhaulBatch
    {
        private bool handedOut;

        public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            if (handedOut) return ValueTask.FromResult<BackhaulMessage?>(null);
            handedOut = true;
            return ValueTask.FromResult<BackhaulMessage?>(copy);
        }

        public async ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct) =>
            await owner.RecordFloodOutcomeAsync(row, route, hopBudget, result, elapsed, ct);
    }

    /// <summary>
    /// #178: true when <paramref name="route"/> rides a connected-mode
    /// session (AGW or RHP - the routes <see cref="Dappsv1SessionBackhaul"/>
    /// claims) and a session with that peer is already open in either
    /// direction. UDP and MeshCore are datagram bearers with no link to
    /// collide on, so their routes are never deferred.
    ///
    /// #185: this check runs once, early - before the settle-gate wait
    /// and the AGW/RHP connect round trip that follow it, both of which
    /// take real time. It's a fast path that skips the whole pipeline in
    /// the common case, not the only guard: a session from this same
    /// peer that appears after this check passes is still caught by
    /// <see cref="PeerSessionRegistry.TryAcquire"/> in
    /// <see cref="BearerSwitchingOutboundTransport"/>, immediately
    /// before the dial - see that class for why the gap matters.
    /// </summary>
    private bool WouldDialIntoOpenSession(BackhaulRoute route, out string? openDirection)
    {
        openDirection = null;
        if (peerSessions is null || !RidesASession(route)) return false;
        return peerSessions.IsActive(route.Callsign, out openDirection);
    }

    /// <summary>AGW and RHP routes ride a connected-mode session; UDP and
    /// MeshCore are datagram bearers with no session to share.</summary>
    private static bool RidesASession(BackhaulRoute route) => route.UdpEndpoint is null && route.MeshCoreChannel is null;
}
