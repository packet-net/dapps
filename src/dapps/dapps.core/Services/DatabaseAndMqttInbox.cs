using System.Collections.Concurrent;
using System.Text.Json;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Default <see cref="IBackhaulInbox"/> for this node: persists the
/// received message into the SQLite queue and, if the destination is a
/// local app, pushes it to the MQTT broker for any connected subscriber.
///
/// Bearer-neutral - this code doesn't know whether the message arrived
/// over a DAPPSv1 session or a MeshCore datagram. The bearer-specific
/// receive layer is the one that called <see cref="DeliverAsync"/>.
/// </summary>
public sealed class DatabaseAndMqttInbox : IBackhaulInbox
{
    private readonly Database database;
    private readonly MqttBrokerService mqtt;
    private readonly InboundEventBus events;
    private readonly IOptionsMonitor<SystemOptions> options;
    private readonly IRoutingAlgorithm routingAlgorithm;
    private readonly IRoutingContext routingContext;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DatabaseAndMqttInbox> logger;

    public DatabaseAndMqttInbox(
        Database database,
        MqttBrokerService mqtt,
        InboundEventBus events,
        IOptionsMonitor<SystemOptions> options,
        IRoutingAlgorithm routingAlgorithm,
        IRoutingContext routingContext,
        TimeProvider timeProvider,
        ILogger<DatabaseAndMqttInbox> logger)
    {
        this.database = database;
        this.mqtt = mqtt;
        this.events = events;
        this.options = options;
        this.routingAlgorithm = routingAlgorithm;
        this.routingContext = routingContext;
        this.timeProvider = timeProvider;
        this.logger = logger;

        // Messages an app here sends to an app on this node come through
        // the same storing and publishing as an arrival (#288). There is
        // one inbox per node.
        database.LocalSubmissionHandler = AcceptLocalSubmissionAsync;
    }

    /// <summary>
    /// Take a message an app here sent to an app on this node. On an
    /// ordered stream its seq is already minted, so if taking it fails
    /// that seq can never arrive: messages behind it must not wait for it
    /// (#384). The message itself is stored and listed for the app.
    /// </summary>
    private async Task AcceptLocalSubmissionAsync(DbMessage message)
    {
        try
        {
            await AcceptAsync(ToBackhaul(message), message.SourceCallsign, CancellationToken.None, localSubmission: true);
        }
        catch
        {
            if (message.StreamSeq is { } seq && !string.IsNullOrEmpty(message.StreamId))
            {
                database.EndLocalStreamSend(message.StreamId, seq);
                try
                {
                    await ReleaseLocalStreamAsync(message.OriginatorCallsign, message.StreamId, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Couldn't move stream {0} on past {1}, which failed", message.StreamId, seq);
                }
            }
            throw;
        }
    }

    private static BackhaulMessage ToBackhaul(DbMessage m) => new(
        Id: m.Id, Destination: m.Destination, Salt: m.Salt, Ttl: m.Ttl, Payload: m.Payload,
        Originator: m.OriginatorCallsign,
        StreamId: m.StreamId, StreamSeq: m.StreamSeq, StreamGapTimeoutSeconds: m.StreamGapTimeoutSeconds);

    /// <summary>Only a stored message counts, and never one without a
    /// salt: those aren't remembered.</summary>
    public async Task<bool> HasAsync(string id, long? salt, int length, CancellationToken ct) =>
        salt is { } s && await database.HasReceivedAsync(DbReceived.MakeKey(id, s, length));

    public async Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct)
    {
        // Our own message, come back from a neighbour: we sent it, so it is
        // a repeat. Taking it would overwrite our forwarded copy with a
        // fresh queued one, and we'd send or flood it all over again
        // (#295). Our exact callsign only: another SSID of ours is a
        // station like any other (#242). Accepted, so the neighbour stops
        // offering it.
        if (!string.IsNullOrEmpty(message.Originator)
            && DestinationParser.SameStation(message.Originator, options.CurrentValue.Callsign))
        {
            logger.LogWarning(
                "Message {0} for {1} is one of ours, come back from {2}: dropped as a repeat",
                message.Id, message.Destination, sourceCallsign);
            return;
        }

        // Flood deduplication (B5): a message arriving with FloodHopsRemaining
        // set is part of an in-flight bounded flood. Multiple flood paths can
        // converge at this node from different neighbours; we only process
        // the first arrival and silently drop subsequent copies. The dedup
        // key is (id, link-source) - different upstreams might be legitimate
        // independent floods of distinct messages with the same id (rare,
        // but possible if two senders salt-collide). Including the link
        // source means we'll dedup re-arrivals from the SAME upstream
        // without conflating them with arrivals from DIFFERENT upstreams.
        if (message.FloodHopsRemaining is { } _)
        {
            if (await routingContext.HasSeenFloodAsync(message.Id, sourceCallsign, ct))
            {
                logger.LogDebug("Flood {0} from {1} dropped - already seen", message.Id, sourceCallsign);
                return;
            }
            await routingContext.RecordFloodSeenAsync(message.Id, sourceCallsign, ct);
        }

        // A message with no salt can't be told apart from a later one with
        // the same content (same id, same length), so it isn't remembered:
        // it's delivered every time it arrives, as before.
        if (message.Salt is not { } salt)
        {
            await routingAlgorithm.ObserveInboundAsync(message, sourceCallsign, routingContext, ct);
            await AcceptAsync(message, sourceCallsign, ct);
            return;
        }

        // Never deliver or forward the same message twice (DbReceived): a
        // sender that restarted or lost our ack offers it again, and a
        // second neighbour can pass on a copy by another path. A copy that
        // arrives while another is being stored waits for it: if that one
        // is stored this one is a repeat, and if storing it failed this one
        // gets its turn, so no copy is dropped in favour of one that failed.
        var key = DbReceived.MakeKey(message.Id, salt, message.Payload.Length);
        while (true)
        {
            var attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inProgress = accepting.GetOrAdd(key, attempt.Task);
            if (inProgress != attempt.Task)
            {
                if (await inProgress)
                {
                    logger.LogInformation("Message {0} from {1} is one we already have; not delivering or forwarding it again",
                        message.Id, sourceCallsign);
                    return;
                }
                continue;
            }

            try
            {
                await AcceptOnceAsync(message, sourceCallsign, key, ct);
                attempt.SetResult(true);
                return;
            }
            catch
            {
                attempt.SetResult(false);
                throw;
            }
            finally
            {
                accepting.TryRemove(new KeyValuePair<string, Task<bool>>(key, attempt.Task));
            }
        }
    }

    /// <summary>Messages being stored right now, by <see cref="DbReceived"/> key: true once stored.</summary>
    private readonly ConcurrentDictionary<string, Task<bool>> accepting = new(StringComparer.Ordinal);

    /// <summary>
    /// Claim, store and commit one message, unless this node already has
    /// it. The claim is written before the message is stored and only
    /// counts once committed, so dying in between costs a repeat, never
    /// the message; and if storing fails the claim goes, so the sender's
    /// retry isn't turned away.
    /// </summary>
    private async Task AcceptOnceAsync(BackhaulMessage message, string sourceCallsign, string key, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var memory = TimeSpan.FromSeconds(options.CurrentValue.ReceivedMemorySeconds);
        var remember = message.Ttl is { } ttl && TimeSpan.FromSeconds(ttl) + DbReceived.ExpirySlack < memory
            ? TimeSpan.FromSeconds(ttl) + DbReceived.ExpirySlack
            : memory;
        if (await database.ClaimReceivedAsync(key, now + remember, sourceCallsign) is { } first)
        {
            LogRepeat(message, sourceCallsign, first);
            return;
        }

        try
        {
            // Hand the message to the routing algorithm BEFORE persistence -
            // passive-learning algorithms care about the (originator, link-source)
            // pair, and that pair is only meaningful here at the wire boundary.
            // Algorithms that don't observe inbound (StaticRoutingAlgorithm) no-op
            // immediately. First arrivals only: a late copy by a longer path
            // mustn't replace what the first one taught.
            await routingAlgorithm.ObserveInboundAsync(message, sourceCallsign, routingContext, ct);
            await AcceptAsync(message, sourceCallsign, ct);
            await database.CommitReceivedAsync(key);
        }
        catch
        {
            try { await database.ForgetReceivedAsync(key); }
            catch (Exception e) { logger.LogWarning(e, "Couldn't drop the claim on {0} after failing to store it", message.Id); }
            throw;
        }
    }

    /// <summary>
    /// A repeat is dropped. One for another node, arriving by a different
    /// neighbour from the first copy, usually means a routing loop (we
    /// passed it on and it came back), so that's worth an operator's eye.
    /// </summary>
    private void LogRepeat(BackhaulMessage message, string sourceCallsign, DbReceived first)
    {
        var looped = message.FloodHopsRemaining is null
            && !DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign)
            && !string.Equals(first.LinkSourceCallsign, sourceCallsign, StringComparison.OrdinalIgnoreCase);
        if (looped)
        {
            logger.LogWarning(
                "Message {0} for {1} came back from {2} after we had it from {3}: dropped as a repeat. If this recurs, look for a routing loop",
                message.Id, message.Destination, sourceCallsign, first.LinkSourceCallsign);
        }
        else
        {
            logger.LogInformation("Message {0} from {1} is one we already have; not delivering or forwarding it again",
                message.Id, sourceCallsign);
        }
    }

    /// <summary>Store a message arriving for the first time, and hand it to its app or leave it queued to pass on.</summary>
    private async Task AcceptAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct, bool localSubmission = false)
    {
        var headersJson = message.Headers is null
            ? "{}"
            : JsonSerializer.Serialize(message.Headers);

        var originator = message.Originator ?? "";

        // F2 multi-part - destination-side reassembly. Fragments
        // destined for a local app go into the reassembly buffer
        // instead of the regular DbMessage table; the regular table
        // gets the assembled payload as one row when the last fragment
        // arrives. Intermediate hops (where IsLocal is false) just
        // forward each fragment as an opaque message - they take the
        // normal SaveMessage path below.
        var isFragmentForLocal = message.MasterId is not null
            && message.FragmentIndex.HasValue
            && message.FragmentTotal.HasValue
            && DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign);
        if (isFragmentForLocal)
        {
            await HandleLocalFragmentAsync(message, sourceCallsign, originator, headersJson, ct);
            // Notify SSE so operators see fragments arriving in real time.
            // Fragment rows aren't in DbMessage so the normal Recent-
            // messages panel won't show them; the event stream is the
            // only signal until reassembly completes.
            events.Publish(new InboundEvent(
                ReceivedAt: timeProvider.GetUtcNow().UtcDateTime,
                Id: message.Id,
                SourceCallsign: FromOf(message, sourceCallsign),
                Destination: message.Destination,
                PayloadLength: message.Payload.Length,
                Ttl: message.Ttl,
                ViaCallsign: sourceCallsign));
            return;
        }

        var isLocal = DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign);

        // Opt-in ordering: when the envelope carries sn=, gate local
        // delivery on the per-(originator, sid) cursor. Transit messages
        // flow through unchanged - intermediate hops re-emit the trio
        // verbatim; only the final destination reorders.
        if (isLocal && message.StreamSeq.HasValue && !string.IsNullOrEmpty(message.StreamId))
        {
            await DeliverOrderedAsync(message, sourceCallsign, originator, headersJson, localSubmission, ct);
            return;
        }

        await database.SaveMessage(
            message.Id,
            message.Payload,
            message.Salt,
            message.Destination,
            sourceCallsign,
            headersJson,
            message.Ttl,
            originatorCallsign: originator,
            // Floods carry a per-hop budget; preserve it on the queued
            // row so the next forwarder tick re-floods at the right
            // remaining-hops level (decremented by FloodFallbackAlgorithm
            // when it picks this row up). For non-flood messages this
            // stays null.
            floodHopsRemaining: message.FloodHopsRemaining,
            // MeshCore-flavoured routing carries the remaining
            // source route (already stripped by the previous sender)
            // and the accumulated traversal record. Both are
            // persisted so that the algorithm can act on them on the
            // next forwarder tick - e.g. re-flood with the right
            // accumulator after a process restart.
            sourceRouteCsv: message.SourceRoute is { Count: > 0 }
                ? string.Join(',', message.SourceRoute)
                : (message.SourceRoute is null ? null : ""),
            traversedHopsCsv: message.TraversedHops is null
                ? null
                : string.Join(',', message.TraversedHops),
            // F2 multi-part: preserve the master id + fragment index/total
            // on transit rows so the forwarder re-emits them on the next
            // hop. (Local-fragment-for-this-node took the early-return
            // path above; this branch only fires for non-fragmented
            // messages OR fragments-for-elsewhere.)
            masterId: message.MasterId,
            fragmentIndex: message.FragmentIndex,
            fragmentTotal: message.FragmentTotal,
            // Opt-in ordering: stream trio is preserved on transit rows
            // so the forwarder re-emits them on the next hop verbatim.
            // (Local-ordered-delivery took the early-return above.)
            streamId: message.StreamId,
            streamSeq: message.StreamSeq,
            streamGapTimeoutSeconds: message.StreamGapTimeoutSeconds);

        if (isLocal)
        {
            var dbMessage = new DbMessage
            {
                Id = message.Id,
                Payload = message.Payload,
                Salt = message.Salt,
                Destination = message.Destination,
                SourceCallsign = sourceCallsign,
                OriginatorCallsign = originator,
                AdditionalProperties = headersJson,
                Ttl = message.Ttl,
                StreamId = message.StreamId,
                StreamSeq = message.StreamSeq,
                StreamGapTimeoutSeconds = message.StreamGapTimeoutSeconds,
            };
            await mqtt.InjectInboundMessage(dbMessage);
        }
        else
        {
            logger.LogDebug("Message {0} for {1} is not local - leaving in queue for forwarding",
                message.Id, message.Destination);
        }

        // Notify dashboard SSE subscribers regardless of local-vs-relay -
        // operators want to see traffic flowing through the node.
        events.Publish(new InboundEvent(
            ReceivedAt: timeProvider.GetUtcNow().UtcDateTime,
            Id: message.Id,
            SourceCallsign: FromOf(message, sourceCallsign),
            Destination: message.Destination,
            PayloadLength: message.Payload.Length,
            Ttl: message.Ttl,
            ViaCallsign: sourceCallsign));
    }

    /// <summary>
    /// Local-destination delivery for an opt-in-ordered message. Compares
    /// StreamSeq against the persisted cursor for (LocalCallsign,
    /// originator, StreamId):
    ///
    /// <list type="bullet">
    /// <item><description>seq == expected -&gt; persist, deliver to MQTT, advance cursor, drain consecutive successors</description></item>
    /// <item><description>seq &gt; expected -&gt; persist with PendingInOrder=true, set GapDeadline (when gt&gt;0) so <see cref="StreamGapSweeper"/> can skip later</description></item>
    /// <item><description>seq &lt; expected -&gt; persist + soft-delete with reason "stream-stale" (already delivered or already skipped past)</description></item>
    /// </list>
    /// </summary>
    private async Task DeliverOrderedAsync(BackhaulMessage message, string sourceCallsign, string originator, string headersJson, bool localSubmission, CancellationToken ct)
    {
        // One at a time per (sender, stream): the cursor is read, then
        // written, so two at once would overwrite each other's advance.
        var lockKey = (!string.IsNullOrEmpty(message.Originator) ? message.Originator : sourceCallsign) + "|" + message.StreamId;
        using (await LockStreamAsync(lockKey, ct))
        {
            await DeliverOrderedLockedAsync(message, sourceCallsign, originator, headersJson, localSubmission, ct);
        }
    }

    /// <summary>
    /// Stream locks in use, by (sender, stream). An entry lives only while
    /// someone holds or waits for it, so a station inventing stream ids
    /// can't grow this for the life of the process (#384).
    /// </summary>
    private readonly Dictionary<string, StreamLock> streamLocks = new(StringComparer.Ordinal);

    internal int StreamLockCount
    {
        get { lock (streamLocks) return streamLocks.Count; }
    }

    private sealed class StreamLock
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private async Task<IDisposable> LockStreamAsync(string key, CancellationToken ct)
    {
        StreamLock entry;
        lock (streamLocks)
        {
            if (!streamLocks.TryGetValue(key, out entry!))
            {
                streamLocks[key] = entry = new StreamLock();
            }
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new StreamLockRelease(this, key, entry);
    }

    private void Leave(string key, StreamLock entry)
    {
        lock (streamLocks)
        {
            if (--entry.Users == 0)
            {
                streamLocks.Remove(key);
            }
        }
    }

    private sealed class StreamLockRelease(DatabaseAndMqttInbox owner, string key, StreamLock entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }

    private async Task DeliverOrderedLockedAsync(BackhaulMessage message, string sourceCallsign, string originator, string headersJson, bool localSubmission, CancellationToken ct)
    {
        var localCall = options.CurrentValue.Callsign;
        var streamId = message.StreamId!;
        var sn = message.StreamSeq!.Value;
        // SenderCallsign on the recv-state row keys on the F1 originator
        // (end-to-end intent), not the link source. Falling back to the
        // link source preserves usable behaviour on legacy peers that
        // don't propagate src= - they're just stuck with one cursor per
        // physical neighbour for that StreamId.
        var streamSender = !string.IsNullOrEmpty(message.Originator)
            ? message.Originator
            : sourceCallsign;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var recv = await database.GetStreamRecvStateAsync(localCall, streamSender, streamId)
                   ?? new DbStreamRecvState
                   {
                       LocalCallsign = localCall,
                       SenderCallsign = streamSender,
                       StreamId = streamId,
                       NextExpectedSeq = 1,
                       LastReceivedAt = now,
                       GapDeadline = DateTime.MinValue,
                   };

        // Seqs the operator dropped before the app had them never come:
        // step over them so what follows isn't held for them (#389).
        await SkipOperatorDropsAsync(recv, streamSender, streamId);

        // This node's own stream: a seq below this one that isn't being
        // sent right now never will be (its send failed, or it was sent
        // before this node delivered its own streams in order), so don't
        // wait for it (#384).
        if (localSubmission && sn > recv.NextExpectedSeq)
        {
            await SkipLocalGapsAsync(recv, streamSender, streamId, upTo: sn, ct);
        }

        if (sn < recv.NextExpectedSeq
            && await database.GetMessage(message.Id) is { } held
            && held.StreamId == streamId && held.StreamSeq == sn
            && string.Equals(held.OriginatorCallsign, originator, StringComparison.OrdinalIgnoreCase))
        {
            // The very message already delivered (or parked and skipped),
            // come again: two reassemblies of it at once, or its full set
            // repeated after the duplicate memory forgot it. The row is
            // the one the app sees and acks, so leave it alone (#384).
            logger.LogInformation(
                "Stream {0}|{1}: {2} (sn={3}) is one we already have; not delivering it again",
                streamSender, streamId, message.Id, sn);
            return;
        }

        if (sn < recv.NextExpectedSeq)
        {
            // Already delivered, or the sweeper already advanced past
            // this seq. Persist for audit, then soft-delete with a
            // dedicated reason so the dashboard's dropped panel makes
            // it obvious why this was discarded rather than delivered.
            await database.SaveMessage(
                message.Id, message.Payload, message.Salt, message.Destination,
                sourceCallsign, headersJson, message.Ttl,
                originatorCallsign: originator,
                streamId: streamId, streamSeq: sn,
                streamGapTimeoutSeconds: message.StreamGapTimeoutSeconds);
            await database.SoftDeleteMessage(message.Id, "stream-stale");
            recv.LastReceivedAt = now;
            await database.UpsertStreamRecvStateAsync(recv);
            logger.LogInformation(
                "Stream {0}|{1}: dropping {2} (sn={3} < expected {4}) as stream-stale",
                streamSender, streamId, message.Id, sn, recv.NextExpectedSeq);
            events.Publish(new InboundEvent(now, message.Id, FromOf(message, sourceCallsign), message.Destination, message.Payload.Length, message.Ttl, sourceCallsign));
            return;
        }

        if (sn > recv.NextExpectedSeq)
        {
            // Park. Persist with PendingInOrder=true so the row is in
            // the messages table (visible in the dashboard, ack'able by
            // ID, etc.) but the inbox doesn't push to MQTT until the
            // gap fills. GapDeadline is set on first detection of the
            // gap; subsequent arrivals don't push it out - the original
            // arrival's deadline is the right cap.
            await database.SaveMessage(
                message.Id, message.Payload, message.Salt, message.Destination,
                sourceCallsign, headersJson, message.Ttl,
                originatorCallsign: originator,
                streamId: streamId, streamSeq: sn,
                streamGapTimeoutSeconds: message.StreamGapTimeoutSeconds,
                pendingInOrder: true);
            recv.LastReceivedAt = now;
            if (recv.GapDeadline == DateTime.MinValue
                && message.StreamGapTimeoutSeconds is { } gt && gt > 0)
            {
                recv.GapDeadline = now + TimeSpan.FromSeconds(gt);
            }
            await database.UpsertStreamRecvStateAsync(recv);
            logger.LogInformation(
                "Stream {0}|{1}: parking {2} (sn={3}, expected {4}, gt={5})",
                streamSender, streamId, message.Id, sn, recv.NextExpectedSeq,
                message.StreamGapTimeoutSeconds ?? 0);
            events.Publish(new InboundEvent(now, message.Id, FromOf(message, sourceCallsign), message.Destination, message.Payload.Length, message.Ttl, sourceCallsign));
            return;
        }

        // sn == NextExpectedSeq: deliver immediately + drain successors.
        await database.SaveMessage(
            message.Id, message.Payload, message.Salt, message.Destination,
            sourceCallsign, headersJson, message.Ttl,
            originatorCallsign: originator,
            streamId: streamId, streamSeq: sn,
            streamGapTimeoutSeconds: message.StreamGapTimeoutSeconds);

        var dbMessage = new DbMessage
        {
            Id = message.Id, Payload = message.Payload, Salt = message.Salt,
            Destination = message.Destination, SourceCallsign = sourceCallsign,
            OriginatorCallsign = originator, AdditionalProperties = headersJson,
            Ttl = message.Ttl,
            StreamId = streamId, StreamSeq = sn,
            StreamGapTimeoutSeconds = message.StreamGapTimeoutSeconds,
        };
        await mqtt.InjectInboundMessage(dbMessage);
        recv.NextExpectedSeq = sn + 1;
        recv.LastReceivedAt = now;

        await DrainConsecutivePendingAsync(recv, streamSender, streamId, ct);
        events.Publish(new InboundEvent(now, message.Id, FromOf(message, sourceCallsign), message.Destination, message.Payload.Length, message.Ttl, sourceCallsign));
    }

    /// <summary>
    /// Drain pending rows whose StreamSeq matches the cursor, advancing
    /// it as it consumes. Stops at the first gap; recomputes the
    /// recv-state's GapDeadline based on the remaining pending head's
    /// gt (or clears it when no gap remains). Used by both the
    /// in-order arrival path and the gap sweeper.
    /// </summary>
    internal async Task DrainConsecutivePendingAsync(
        DbStreamRecvState recv, string streamSender, string streamId, CancellationToken ct)
    {
        var pending = await database.GetPendingInOrderAsync(streamSender, streamId);
        // pending is ordered by StreamSeq asc, so a single forward pass
        // either delivers a consecutive run or stops at the first gap.
        var byteOffset = 0;
        foreach (var p in pending)
        {
            if (p.StreamSeq is null) continue;
            if (p.StreamSeq.Value < recv.NextExpectedSeq)
            {
                // Stale parked row (the sweeper advanced past it via
                // gap-skip and a new arrival didn't catch this up).
                await database.SoftDeleteMessage(p.Id, "stream-stale");
                continue;
            }
            if (p.StreamSeq.Value > recv.NextExpectedSeq)
            {
                await SkipOperatorDropsAsync(recv, streamSender, streamId);
            }
            if (p.StreamSeq.Value != recv.NextExpectedSeq) break;

            var inject = new DbMessage
            {
                Id = p.Id, Payload = p.Payload, Salt = p.Salt,
                Destination = p.Destination, SourceCallsign = p.SourceCallsign,
                OriginatorCallsign = p.OriginatorCallsign,
                AdditionalProperties = p.AdditionalProperties,
                Ttl = p.Ttl, CreatedAt = p.CreatedAt,
                StreamId = p.StreamId, StreamSeq = p.StreamSeq,
                StreamGapTimeoutSeconds = p.StreamGapTimeoutSeconds,
            };
            await mqtt.InjectInboundMessage(inject);
            await database.ClearPendingInOrderAsync(p.Id);
            recv.NextExpectedSeq = p.StreamSeq.Value + 1;
            byteOffset += p.Payload.Length;
        }

        await SkipOperatorDropsAsync(recv, streamSender, streamId);

        // Recompute deadline: if anything still pending above the new
        // cursor, use the head's stored gt as the new gap budget; else
        // clear.
        var stillPending = (await database.GetPendingInOrderAsync(streamSender, streamId))
            .FirstOrDefault(p => p.StreamSeq is { } s && s > recv.NextExpectedSeq);
        if (stillPending is null)
        {
            recv.GapDeadline = DateTime.MinValue;
        }
        else if (stillPending.StreamGapTimeoutSeconds is { } gt && gt > 0)
        {
            recv.GapDeadline = recv.LastReceivedAt + TimeSpan.FromSeconds(gt);
        }
        else
        {
            // gt=0 (strict): the new gap stalls forever.
            recv.GapDeadline = DateTime.MinValue;
        }
        await database.UpsertStreamRecvStateAsync(recv);
        if (byteOffset > 0)
        {
            logger.LogInformation(
                "Stream {0}|{1}: drained pending up to sn={2}",
                streamSender, streamId, recv.NextExpectedSeq - 1);
        }
    }

    /// <summary>
    /// Move the cursor past every seq the operator dropped from the inbox
    /// before it was delivered (#389): it will never be delivered, so on a
    /// strict stream waiting for it would stall everything after it.
    /// </summary>
    private async Task SkipOperatorDropsAsync(DbStreamRecvState recv, string streamSender, string streamId)
    {
        while (recv.NextExpectedSeq < uint.MaxValue
               && await database.WasDroppedByOperatorAsync(streamSender, streamId, recv.NextExpectedSeq))
        {
            logger.LogInformation(
                "Stream {0}|{1}: stepping over sn={2}, dropped by the operator",
                streamSender, streamId, recv.NextExpectedSeq);
            recv.NextExpectedSeq++;
        }
    }

    /// <summary>
    /// The operator's Drop on the dashboard's local inbox (#389). The
    /// message goes to the dropped messages as dropped by the operator, so
    /// neither MQTT replay nor the REST inbox hands it to the app again,
    /// and any fragments under its id go with it. On an ordered stream it
    /// is done under the stream's lock, and the cursor steps over its seq
    /// if the app hadn't had it yet, so later messages are not held for it.
    /// </summary>
    public async Task<InboxDropOutcome> DropByOperatorAsync(string id, CancellationToken ct = default)
    {
        var row = await database.GetMessage(id);
        if (row?.StreamId is not { Length: > 0 } streamId || row.StreamSeq is null)
        {
            var (outcome, _) = await database.DropFromLocalInboxAsync(id);
            if (outcome == InboxDropOutcome.Dropped) logger.LogInformation("Inbox: {0} dropped by the operator", id);
            return outcome;
        }

        var streamSender = string.IsNullOrEmpty(row.OriginatorCallsign) ? row.SourceCallsign : row.OriginatorCallsign;
        using (await LockStreamAsync(streamSender + "|" + streamId, ct))
        {
            var (outcome, dropped) = await database.DropFromLocalInboxAsync(id);
            if (outcome != InboxDropOutcome.Dropped) return outcome;
            logger.LogInformation("Inbox: {0} (stream {1}|{2} sn={3}) dropped by the operator",
                id, streamSender, streamId, dropped!.StreamSeq);
            var recv = await database.GetStreamRecvStateAsync(options.CurrentValue.Callsign, streamSender, streamId);
            if (recv is not null)
            {
                await DrainConsecutivePendingAsync(recv, streamSender, streamId, ct);
            }
            return outcome;
        }
    }

    /// <summary>
    /// Called by <see cref="StreamGapSweeper"/> when a recv-state's
    /// gap deadline has elapsed. Skips the cursor forward to the
    /// smallest pending sn for that stream and drains successors.
    /// Logs each skipped seq for operator visibility.
    /// </summary>
    internal async Task SkipGapAsync(DbStreamRecvState recv, CancellationToken ct)
    {
        using (await LockStreamAsync(recv.SenderCallsign + "|" + recv.StreamId, ct))
        {
            await SkipGapLockedAsync(recv, ct);
        }
    }

    /// <summary>
    /// After a message on this node's own stream failed to go in: release
    /// any messages parked behind its seq (#384).
    /// </summary>
    private async Task ReleaseLocalStreamAsync(string streamSender, string streamId, CancellationToken ct)
    {
        using (await LockStreamAsync(streamSender + "|" + streamId, ct))
        {
            var recv = await database.GetStreamRecvStateAsync(options.CurrentValue.Callsign, streamSender, streamId);
            if (recv is null) return;
            var pending = await database.GetPendingInOrderAsync(streamSender, streamId);
            if (pending.Where(p => p.StreamSeq is not null).Select(p => p.StreamSeq!.Value).DefaultIfEmpty().Max() is var last
                && last >= recv.NextExpectedSeq && last > 0)
            {
                await SkipLocalGapsAsync(recv, streamSender, streamId, upTo: last + 1, ct);
                await database.UpsertStreamRecvStateAsync(recv);
            }
        }
    }

    /// <summary>
    /// On this node's own stream, move the cursor up to
    /// <paramref name="upTo"/> past every seq that is neither parked nor
    /// being sent right now: those were never delivered here and can't
    /// arrive any more. Parked messages met on the way are delivered;
    /// a seq still being sent stops it. Under the stream's lock.
    /// </summary>
    private async Task SkipLocalGapsAsync(DbStreamRecvState recv, string streamSender, string streamId, uint upTo, CancellationToken ct)
    {
        var pending = (await database.GetPendingInOrderAsync(streamSender, streamId))
            .Where(p => p.StreamSeq is not null).Select(p => p.StreamSeq!.Value).ToList();
        while (recv.NextExpectedSeq < upTo)
        {
            var k = recv.NextExpectedSeq;
            if (pending.Contains(k))
            {
                await DrainConsecutivePendingAsync(recv, streamSender, streamId, ct);
                if (recv.NextExpectedSeq == k) return;
                continue;
            }
            var inFlight = database.LowestLocalStreamSeqInFlight(streamId, k);
            if (inFlight == k) return;
            var next = Math.Min(upTo, Math.Min(inFlight ?? upTo, pending.Where(p => p > k).DefaultIfEmpty(upTo).Min()));
            logger.LogWarning(
                "Stream {0}|{1}: skipped sn={2} to {3} on this node's own stream; they never reached the app here and can't arrive any more",
                streamSender, streamId, k, next - 1);
            recv.NextExpectedSeq = next;
        }
    }

    private async Task SkipGapLockedAsync(DbStreamRecvState recv, CancellationToken ct)
    {
        // The caller's copy may be stale by the time we hold the lock.
        recv = await database.GetStreamRecvStateAsync(recv.LocalCallsign, recv.SenderCallsign, recv.StreamId) ?? recv;
        var pending = await database.GetPendingInOrderAsync(recv.SenderCallsign, recv.StreamId);
        var firstAbove = pending.FirstOrDefault(p => p.StreamSeq is { } s && s >= recv.NextExpectedSeq);
        if (firstAbove?.StreamSeq is null)
        {
            // Nothing pending - clear the deadline.
            recv.GapDeadline = DateTime.MinValue;
            await database.UpsertStreamRecvStateAsync(recv);
            return;
        }
        var fromSn = recv.NextExpectedSeq;
        var toSn = firstAbove.StreamSeq.Value;
        for (var k = fromSn; k < toSn; k++)
        {
            logger.LogWarning(
                "Stream {0}|{1}: gap-skipped sn={2} (deadline elapsed)",
                recv.SenderCallsign, recv.StreamId, k);
        }
        recv.NextExpectedSeq = toSn;
        await DrainConsecutivePendingAsync(recv, recv.SenderCallsign, recv.StreamId, ct);
    }

    /// <summary>
    /// Plan F2 - destination-side fragment handling. Stores the
    /// fragment in <see cref="DbFragment"/>, checks whether the full
    /// set is now present, and if so reassembles + delivers the
    /// assembled payload via the regular DbMessage + MQTT path.
    /// Pre: <paramref name="message"/> has MasterId / FragmentIndex /
    /// FragmentTotal all set, AND the destination is local to this
    /// node.
    /// </summary>
    private async Task HandleLocalFragmentAsync(
        BackhaulMessage message, string sourceCallsign, string originator, string headersJson, CancellationToken ct)
    {
        var masterId = message.MasterId!;
        var fragIndex = message.FragmentIndex!.Value;
        var fragTotal = message.FragmentTotal!.Value;

        await database.UpsertFragment(new DbFragment
        {
            MasterId = masterId,
            FragmentIndex = fragIndex,
            FragmentTotal = fragTotal,
            Payload = message.Payload,
            Destination = message.Destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originator,
            AdditionalProperties = headersJson,
            Ttl = message.Ttl,
            FirstSeenAt = timeProvider.GetUtcNow().UtcDateTime,
            StreamId = message.StreamId,
            StreamSeq = message.StreamSeq,
            StreamGapTimeoutSeconds = message.StreamGapTimeoutSeconds,
        });

        var fragments = await database.GetFragmentsForMaster(masterId);
        // Distinct-by-index covers the (rare) case where a fragment
        // is re-delivered via a different path before its sibling
        // arrives - the upsert above keeps a single row per index, so
        // Count here = unique-index count.
        if (fragments.Count < fragTotal)
        {
            logger.LogInformation(
                "Fragment {Index} of {Total} for master {MasterId} stored ({Have} so far) - waiting for siblings",
                fragIndex, fragTotal, masterId, fragments.Count);
            return;
        }

        // All fragments present - assemble. Concatenate by 1-based
        // FragmentIndex. The fragments table query already orders.
        var totalLen = fragments.Sum(f => f.Payload.Length);
        var assembled = new byte[totalLen];
        var offset = 0;
        foreach (var f in fragments)
        {
            Buffer.BlockCopy(f.Payload, 0, assembled, offset, f.Payload.Length);
            offset += f.Payload.Length;
        }

        // Use the master id as the assembled message's id - it's
        // the natural content-addressable identifier from the
        // originator's view, and the dashboard / app-interface
        // dapps-id user property reads coherently from sender to
        // receiver. Salt is the FIRST fragment's salt for traceability.
        var firstSeen = fragments.Min(f => f.FirstSeenAt);
        var assembledTtl = message.Ttl;

        // Opt-in ordering (#283): the sender stamps the stream trio on
        // every fragment. A fragment that came through a forwarder older
        // than #122 has lost it, so take it from any fragment that has it
        // (#384). The assembled message goes through the ordered path like
        // any other sn= arrival: delivered, parked or dropped as stale
        // against the cursor, which moves on past it.
        var stream = message.StreamSeq.HasValue && !string.IsNullOrEmpty(message.StreamId)
            ? (Id: message.StreamId, Seq: message.StreamSeq, Gt: message.StreamGapTimeoutSeconds)
            : fragments.Where(f => f.StreamSeq.HasValue && !string.IsNullOrEmpty(f.StreamId))
                .Select(f => (Id: f.StreamId, Seq: f.StreamSeq, Gt: f.StreamGapTimeoutSeconds))
                .FirstOrDefault();
        if (stream.Seq is { } streamSeq)
        {
            var assembledMessage = message with
            {
                Id = masterId,
                Payload = assembled,
                Salt = null,
                MasterId = null,
                FragmentIndex = null,
                FragmentTotal = null,
                StreamId = stream.Id,
                StreamSeq = streamSeq,
                StreamGapTimeoutSeconds = stream.Gt,
            };
            await DeliverOrderedAsync(assembledMessage, sourceCallsign, originator, headersJson, localSubmission: false, ct);
            await database.DeleteFragmentsForMaster(masterId);
            logger.LogInformation(
                "Reassembled master {0} ({1} fragments, {2} bytes) on stream {3} sn={4}",
                masterId, fragTotal, totalLen, stream.Id, streamSeq);
            return;
        }

        await database.SaveMessage(
            id: masterId,
            buffer: assembled,
            salt: null,
            destination: message.Destination,
            sourceCallsign: sourceCallsign,
            additionalProperties: headersJson,
            ttl: assembledTtl,
            originatorCallsign: originator);

        // Inject the assembled message into MQTT exactly as a
        // single-part arrival would have been.
        var assembledRow = new DbMessage
        {
            Id = masterId,
            Payload = assembled,
            Salt = null,
            Destination = message.Destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originator,
            AdditionalProperties = headersJson,
            Ttl = assembledTtl,
            CreatedAt = firstSeen,
        };
        await mqtt.InjectInboundMessage(assembledRow);

        // Drop fragment rows now that the assembled message is in DbMessage.
        await database.DeleteFragmentsForMaster(masterId);

        logger.LogInformation(
            "Reassembled master {0} ({1} fragments, {2} bytes) - delivered as one message",
            masterId, fragTotal, totalLen);
    }

    /// <summary>Who a message is from, as the dashboard shows it: the
    /// originator, else the neighbour that handed it to us.</summary>
    private static string FromOf(BackhaulMessage message, string linkSource) =>
        string.IsNullOrEmpty(message.Originator) ? linkSource : message.Originator;
}
