using System.Collections.Concurrent;
using dapps.client.Backhaul;
using dapps.client.Tx;
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
    SessionDirectory? openSessions = null,
    ForwarderWakeup? wakeup = null,
    NodeDiagnostics? nodeDiagnostics = null)
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

    /// <summary>
    /// Messages handed to an open session in a batch it hasn't taken them
    /// from yet, with when. A session works through what it's handed as
    /// its window allows, so without this every run (every 30 s at least)
    /// handed it the same queued messages again, and when it ended each
    /// was deferred once per copy.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> handedToSession = new(StringComparer.Ordinal);

    /// <summary>When a message that the forwarder could not handle was
    /// last logged as an error. A row that keeps failing is logged once
    /// an hour, not on every run.</summary>
    private readonly ConcurrentDictionary<string, DateTime> failureLoggedAt = new(StringComparer.Ordinal);

    /// <summary>Messages passing through, waiting for a route, that have
    /// been logged as waiting: the line appears once per message.</summary>
    private readonly ConcurrentDictionary<string, byte> waitingTransit = new(StringComparer.Ordinal);

    /// <summary>How long a hand-off holds a message back from later runs,
    /// in case the session never gets to it.</summary>
    internal TimeSpan HandOffPatience { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a run waits on one neighbour's send before going on to the
    /// others and leaving that send to finish in the background. A dial
    /// can take minutes to fail (the node gives up on a dead RF link after
    /// its retries, or not at all for 3 minutes), and the run serves one
    /// neighbour at a time: without this, every other neighbour waited.
    /// </summary>
    internal TimeSpan SendPatience { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The clock <see cref="SendPatience"/> runs on.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Sends left running in the background by <see cref="SendPatience"/>,
    /// by the link they dial (<see cref="LinkKey"/>). Later runs leave
    /// that link alone until its send ends: dialling it again would only
    /// collide with the call still in progress.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> backgroundSends = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The node a route dials, and on which port. Neighbours
    /// reached through the same <see cref="BackhaulRoute.ConnectVia"/>
    /// share it.</summary>
    private static string LinkKey(BackhaulRoute route) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{route.DialCallsign}|{route.BearerPort}");

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
        foreach (var id in handedToSession.Keys.Where(id => !seen.Contains(id)).ToList()) handedToSession.TryRemove(id, out _);
        foreach (var id in failureLoggedAt.Keys.Where(id => !seen.Contains(id)).ToList()) failureLoggedAt.TryRemove(id, out _);
        foreach (var id in waitingTransit.Keys.Where(id => !seen.Contains(id)).ToList()) waitingTransit.TryRemove(id, out _);

        // Messages for the same next hop go out together on one session
        // rather than one session each. The work runs in queue order: a
        // batch where its first message sits in the queue, a flood where
        // its message does.
        var batches = new Dictionary<BackhaulRoute, NextHopBatch>(SameLinkComparer.Instance);
        var callerBatches = new Dictionary<string, NextHopBatch>(StringComparer.OrdinalIgnoreCase);
        var work = new List<object>();

        foreach (var message in messages)
        {
            // One bad row must not stop the run: an exception here would
            // leave every batch below unsent, on this run and every later
            // one while the row stays queued (#284).
            try
            {
                // Already going out on a session, or waiting in one's queue.
                if (inFlight.ContainsKey(message.Id)) continue;
                if (handedToSession.TryGetValue(message.Id, out var handedAt))
                {
                    if (DateTime.UtcNow - handedAt < HandOffPatience) continue;
                    handedToSession.TryRemove(message.Id, out _);
                }

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

                // No route, but the node it's for may have a session open with
                // us. Never the one it came from, though (#297).
                var caller = decision is RouteDecision.Unreachable
                    && openSessions?.CallerFor(message.Destination, optionsValue.Callsign) is { } c
                    && !SplitHorizon.Avoids(SplitHorizon.CameFrom(message, optionsValue.Callsign), await routingContext.GetNeighboursAsync(stoppingToken))(c)
                    ? c : null;

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

                    case RouteDecision.Unreachable when caller is not null:
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

                    case RouteDecision.Unreachable when !SplitHorizon.OriginatedHere(message, optionsValue.Callsign):
                        // Passing through with no route: it waits for one (#295).
                        // Said once per message, not on every run.
                        if (waitingTransit.TryAdd(message.Id, 0))
                        {
                            logger.LogInformation("No route for {0} from {1}; it stays queued until a route turns up or its ttl runs out",
                                message.Id, message.SourceCallsign);
                            metrics.RecordNoRoute(message.Id, message.Destination);
                        }
                        else logger.LogDebug("Still no route for {0}, leaving in queue", message.Id);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var now = DateTime.UtcNow;
                if (!failureLoggedAt.TryGetValue(message.Id, out var loggedAt) || now - loggedAt >= TimeSpan.FromHours(1))
                {
                    failureLoggedAt[message.Id] = now;
                    logger.LogError(ex, "Forwarder: could not handle message {0}, leaving it and carrying on with the rest", message.Id);
                }
                else
                {
                    logger.LogDebug(ex, "Forwarder: could not handle message {0} again", message.Id);
                }
            }
        }

        foreach (var item in work)
        {
            // One neighbour's trouble must not stop the run: an exception
            // here would leave every neighbour after it unserved (#263).
            try
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
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var what = item switch
                {
                    NextHopBatch b => $"messages for {b.Route.Callsign}",
                    PendingFlood f => $"the flood of {f.Message.Id}",
                    _ => "an item",
                };
                logger.LogError(ex, "Forwarder: could not send {0}, carrying on with the rest", what);
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
        batch.MarkHandedOff();
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
        batch.UnmarkHandedOff();
        batch.Detached = false;

        if (batch.HandOffOnly)
        {
            // Its session ended before the batch got there, and with no
            // route there's nothing to dial: the messages stay queued.
            logger.LogInformation("Leaving {0} message(s) for {1} queued: its session has ended and there's no route to it",
                batch.Count, route.Callsign);
            return;
        }

        if (backgroundSends.ContainsKey(LinkKey(route)))
        {
            logger.LogDebug(
                "Leaving {0} message(s) for {1} queued: a send through {2} from an earlier run is still going",
                batch.Count, route.Callsign, route.DialCallsign);
            // #425: what the node says about that link (in the background).
            nodeDiagnostics?.LinkBusy(route, optionsValue.Callsign);
            return;
        }

        if (destinationBackoff.IsInCooldown(route.Callsign, out var nextRetryAtUtc))
        {
            logger.LogDebug(
                "Skipping {0} message(s) for {1}: in reconnect cooldown until {2:O}",
                batch.Count, route.Callsign, nextRetryAtUtc);
            return;
        }

        if (ViaOf(route) is { } via && destinationBackoff.IsInCooldown(via, out var viaRetryAtUtc))
        {
            logger.LogDebug(
                "Skipping {0} message(s) for {1}: {2}, which it's reached through, is in reconnect cooldown until {3:O}",
                batch.Count, route.Callsign, via, viaRetryAtUtc);
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
            nodeDiagnostics?.LinkBusy(route, optionsValue.Callsign);
            return;
        }

        // Not linked to stoppingToken: when that's cancelled, the send
        // ends with it, and goes the usual way. The wait starts before the
        // send does, so it's running by the time the bearer is.
        using (var waiting = new CancellationTokenSource())
        {
            var patience = Task.Delay(SendPatience, TimeProvider, waiting.Token);
            var send = SendAndCompleteAsync(backhaul, batch, optionsValue, stoppingToken);
            var finished = await Task.WhenAny(send, patience);
            await waiting.CancelAsync();
            if (finished == send)
            {
                await send;
                return;
            }
            LeaveInBackground(batch, send);
        }
    }

    private void LeaveInBackground(NextHopBatch batch, Task send)
    {
        var route = batch.Route;

        // Still dialling, or still in the session: the others shouldn't
        // wait for it. The batch hands out only what it already has from
        // here on, as a session's batch does: what arrives meanwhile goes
        // by hand-off, or on a later run once this send has ended.
        batch.Detached = true;
        logger.LogInformation(
            "Sending to {0} through {1} is taking more than {2:F0}s; leaving it to finish in the background and going on with the rest",
            route.Callsign, route.DialCallsign, SendPatience.TotalSeconds);
        TrackInBackground(route, send);
    }

    /// <summary>Later runs leave <paramref name="route"/>'s link alone
    /// until <paramref name="send"/> ends; then the forwarder is woken.</summary>
    private void TrackInBackground(BackhaulRoute route, Task send)
    {
        var key = LinkKey(route);
        backgroundSends[key] = send;
        _ = send.ContinueWith(t =>
        {
            backgroundSends.TryRemove(new KeyValuePair<string, Task>(key, send));
            // Only on shutdown: anything else is caught and recorded.
            if (t.Exception is { } ex) logger.LogDebug(ex, "Background send to {0} ended with an exception", route.Callsign);
            // Whatever was held back for this link can go now.
            wakeup?.Wake();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task SendAndCompleteAsync(IDappsBackhaul backhaul, NextHopBatch batch, SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        var route = batch.Route;
        try
        {
            await backhaul.SendBatchAsync(route, optionsValue.Callsign, batch, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // A bearer should report every message it took through the
            // batch. If it threw instead, what it took is still claimed:
            // give each one an outcome, or no later run would send it
            // until a restart (#263).
            logger.LogError(ex, "Sending to {0} via {1} failed", route.Callsign, batch.BackhaulName);
            await batch.CompleteUnansweredAsync(ex, stoppingToken);
        }
    }

    /// <summary>The node a route is reached through, when it isn't the
    /// neighbour itself.</summary>
    private static string? ViaOf(BackhaulRoute route) =>
        route.ConnectVia is { } via && !string.Equals(via, route.Callsign, StringComparison.OrdinalIgnoreCase) ? via : null;

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
                if (ViaOf(route) is { } via) destinationBackoff.RecordSuccess(via);
                refusals.TryRemove(RefusalKey(message), out _);
            }
            else
            {
                // The node we dial never answered: every neighbour reached
                // through it would fail the same way, so they wait too.
                if (result.DialFailed && ViaOf(route) is { } via) destinationBackoff.RecordFailure(via, result.MinCooldown);
                var nextRetryAtUtc = destinationBackoff.RecordFailure(route.Callsign, result.MinCooldown);
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
        private readonly List<DbMessage> handedOutInOrder = [];
        private readonly HashSet<string> answered = new(StringComparer.Ordinal);
        private int pickedUp;

        public BackhaulRoute Route => route;

        /// <summary>Messages not yet handed to the backhaul.</summary>
        public int Count => queued.Count;

        public string BackhaulName { get; set; } = "";

        /// <summary>
        /// Handed to a session that sends in the background, while this
        /// and later runs go on, or left sending in the background after
        /// <see cref="SendPatience"/>. It then only sends what it was
        /// given: looking at the queue again would race the run that made it.
        /// </summary>
        public bool Detached
        {
            get => Volatile.Read(ref detached);
            set => Volatile.Write(ref detached, value);
        }
        private bool detached;

        /// <summary>Only for a session already open: never dialled for.</summary>
        public bool HandOffOnly { get; init; }

        public void Add(DbMessage row, IReadOnlyList<string>? sourceRoute) => queued.Enqueue((row, sourceRoute));

        /// <summary>About to be handed to an open session: later runs leave
        /// these messages alone until the session takes them.</summary>
        public void MarkHandedOff()
        {
            var now = DateTime.UtcNow;
            foreach (var (row, _) in queued) owner.handedToSession[row.Id] = now;
        }

        /// <summary>The hand-off didn't happen.</summary>
        public void UnmarkHandedOff()
        {
            foreach (var (row, _) in queued) owner.handedToSession.TryRemove(row.Id, out _);
        }

        public async ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            while (true)
            {
                if (queued.Count == 0 && !Detached) await PickUpNewlyQueuedAsync(ct);
                if (!queued.TryDequeue(out var next)) return null;
                // The session has it now: claimed below, or passed over.
                if (Detached) owner.handedToSession.TryRemove(next.Row.Id, out _);

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
                lock (answered) handedOutInOrder.Add(next.Row);
                return owner.ToBackhaulMessage(next.Row, residualTtl, next.SourceRoute);
            }
        }

        public async ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
        {
            lock (answered) answered.Add(message.Id);
            await owner.RecordForwardOutcomeAsync(handedOut[message.Id], route, BackhaulName, result, elapsed, ct);
        }

        /// <summary>
        /// The bearer threw rather than answer everything it took. The
        /// oldest unanswered message fails, as a failed dial would (unless
        /// the kill switch refused it: our refusal says nothing about the
        /// route), and the rest are deferred.
        /// </summary>
        public async Task CompleteUnansweredAsync(Exception ex, CancellationToken ct)
        {
            List<DbMessage> unanswered;
            lock (answered) unanswered = [.. handedOutInOrder.Where(m => answered.Add(m.Id))];
            var first = true;
            foreach (var row in unanswered)
            {
                var result = first && ex is not TxStoppedException
                    ? BackhaulSendResult.Fail(ex.Message)
                    : BackhaulSendResult.Defer(ex.Message);
                first = false;
                try
                {
                    // Releases the claim however recording goes.
                    await owner.RecordForwardOutcomeAsync(row, route, BackhaulName, result, TimeSpan.Zero, ct);
                }
                catch (Exception recordFailure) when (!ct.IsCancellationRequested)
                {
                    // One row failing to record mustn't strand the rest.
                    owner.logger.LogError(recordFailure, "Could not record the outcome for {0}; it stays queued", row.Id);
                }
                finally
                {
                    owner.inFlight.TryRemove(row.Id, out _);
                }
            }
        }

        private async Task PickUpNewlyQueuedAsync(CancellationToken ct)
        {
            if (pickedUp >= MaxPickedUpPerBatch) return;
            foreach (var row in await owner.PendingOutboundQueuedSinceAsync(runStartedAt))
            {
                // Once looked at, a message is the next run's to handle
                // if it isn't ours: no second look this run. Locked: a
                // batch left sending in the background may still be here
                // while the run goes on to the next one.
                bool firstLook;
                lock (seen) firstLook = seen.Add(row.Id);
                if (!firstLook || owner.inFlight.ContainsKey(row.Id)) continue;

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
                && string.Equals(x.ConnectVia, y.ConnectVia, StringComparison.OrdinalIgnoreCase)
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
            if (RidesASession(route) && backgroundSends.TryGetValue(LinkKey(route), out var busy))
            {
                // A send left in the background still has this link: a
                // copy now would only be turned away, and lost. It goes
                // once that send has ended, and holds the link till then.
                logger.LogInformation("Flood of {0}: the link to {1} is still busy; its copy goes when it's free", message.Id, route.Callsign);
                TrackInBackground(route, FloodCopyWhenFreeAsync(busy, message, flood, originator, route, optionsValue, stoppingToken));
                continue;
            }
            await SendFloodCopyAsync(message, flood, residualTtl, originator, route, optionsValue, stoppingToken);
        }

        // Mark the message forwarded so it doesn't keep flooding on
        // every tick. If the destination was unreachable the flood is
        // effectively lost - that's correct semantics; floods are
        // best-effort.
        await database.MarkMessageAsForwarded(message.Id);
    }

    private async Task FloodCopyWhenFreeAsync(
        Task busy, DbMessage message, RouteDecision.FloodToNeighbours flood,
        string? originator, BackhaulRoute route, SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        try { await busy; }
        catch { /* That send's own outcome is recorded where it ran. */ }
        stoppingToken.ThrowIfCancellationRequested();
        var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
        if (residualTtl is <= 0)
        {
            logger.LogInformation("Flood of {0}: not sending the copy for {1}, its ttl ran out while the link was busy", message.Id, route.Callsign);
            return;
        }
        await SendFloodCopyAsync(message, flood, residualTtl, originator, route, optionsValue, stoppingToken);
    }

    private async Task SendFloodCopyAsync(
        DbMessage message, RouteDecision.FloodToNeighbours flood, int? residualTtl,
        string? originator, BackhaulRoute route, SystemOptions optionsValue, CancellationToken stoppingToken)
    {
        // One neighbour's trouble mustn't stop the copies to the
        // others (#263). A copy whose send throws counts as a failed
        // send (deferred if the kill switch refused it).
        (BackhaulSendResult Result, TimeSpan Elapsed)? sent;
        try
        {
            sent = await FloodCopyAsync(message, flood, residualTtl, originator, route, optionsValue, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Flood of {0}: the copy for {1} failed", message.Id, route.Callsign);
            sent = (ex is TxStoppedException ? BackhaulSendResult.Defer(ex.Message) : BackhaulSendResult.Fail(ex.Message), TimeSpan.Zero);
        }
        if (sent is not { } s) return;
        try
        {
            await RecordFloodOutcomeAsync(message, route, flood.HopBudget, s.Result, s.Elapsed, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Flood of {0}: could not record the outcome for {1}", message.Id, route.Callsign);
        }
    }

    /// <summary>
    /// One flood copy to one neighbour. Returns how the send went, or
    /// null when nothing was sent here: no bearer for the route, handed
    /// to a session open with the neighbour (which reports through the
    /// copy's batch), or skipped.
    /// </summary>
    private async Task<(BackhaulSendResult Result, TimeSpan Elapsed)?> FloodCopyAsync(
        DbMessage message, RouteDecision.FloodToNeighbours flood, int? residualTtl,
        string? originator, BackhaulRoute route, SystemOptions optionsValue, CancellationToken stoppingToken)
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
        if (backhaul is null) return null;

        // A session open with the neighbour, either way, takes the
        // copy: with sessions held for minutes at a time, skipping
        // them would lose most floods.
        var copy = new FloodCopyBatch(this, message, route, bm, flood.HopBudget);
        if (backhaul.TryHandToOpenSession(route, copy))
        {
            logger.LogInformation("Flood of {0}: handed to the session we have open with {1}", message.Id, route.Callsign);
            return null;
        }
        if (RidesASession(route) && openSessions is not null && openSessions.TryHand(route.Callsign, copy))
        {
            logger.LogInformation("Flood of {0}: handed to the session open with {1}", message.Id, route.Callsign);
            return null;
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
            return null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
        return (result, sw.Elapsed);
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
                    destinationBackoff.RecordFailure(route.Callsign, result.MinCooldown);
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
