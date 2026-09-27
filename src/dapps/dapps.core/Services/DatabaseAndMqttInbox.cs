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
public sealed class DatabaseAndMqttInbox(
    Database database,
    MqttBrokerService mqtt,
    InboundEventBus events,
    IOptionsMonitor<SystemOptions> options,
    IRoutingAlgorithm routingAlgorithm,
    IRoutingContext routingContext,
    TimeProvider timeProvider,
    ILogger<DatabaseAndMqttInbox> logger) : IBackhaulInbox
{
    public async Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct)
    {

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

        // Hand the message to the routing algorithm BEFORE persistence -
        // passive-learning algorithms care about the (originator, link-source)
        // pair, and that pair is only meaningful here at the wire boundary.
        // Algorithms that don't observe inbound (StaticRoutingAlgorithm) no-op
        // immediately.
        await routingAlgorithm.ObserveInboundAsync(message, sourceCallsign, routingContext, ct);

        // Never deliver or forward the same message twice (DbReceived): a
        // sender that restarted or lost our ack offers it again, and a
        // second neighbour can pass on a copy by another path. Recording
        // first, in one insert, means two copies arriving at once can't
        // both get through. If storing it then fails, the record goes, so
        // the sender's retry isn't turned away: a message is never lost
        // to this, at worst delivered twice after an internal failure.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var receivedKey = DbReceived.MakeKey(message.Id, message.Salt, message.Payload.Length);
        var remember = message.Ttl is { } ttl
            ? TimeSpan.FromSeconds(ttl) + DbReceived.ExpirySlack
            : TimeSpan.FromSeconds(options.CurrentValue.ReceivedMemorySeconds);
        if (!await database.TryRecordReceivedAsync(receivedKey, now, now + remember, sourceCallsign))
        {
            logger.LogInformation("Message {0} from {1} is one we already have; not delivering or forwarding it again",
                message.Id, sourceCallsign);
            return;
        }

        try
        {
            await AcceptAsync(message, sourceCallsign, ct);
        }
        catch
        {
            try { await database.ForgetReceivedAsync(receivedKey); }
            catch (Exception e) { logger.LogWarning(e, "Couldn't forget {0} after failing to store it", message.Id); }
            throw;
        }
    }

    /// <summary>Store a message arriving for the first time, and hand it to its app or leave it queued to pass on.</summary>
    private async Task AcceptAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
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
                SourceCallsign: sourceCallsign,
                Destination: message.Destination,
                PayloadLength: message.Payload.Length,
                Ttl: message.Ttl));
            return;
        }

        var isLocal = DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign);

        // Opt-in ordering: when the envelope carries sn=, gate local
        // delivery on the per-(originator, sid) cursor. Transit messages
        // flow through unchanged - intermediate hops re-emit the trio
        // verbatim; only the final destination reorders.
        if (isLocal && message.StreamSeq.HasValue && !string.IsNullOrEmpty(message.StreamId))
        {
            await DeliverOrderedAsync(message, sourceCallsign, originator, headersJson, ct);
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
            SourceCallsign: sourceCallsign,
            Destination: message.Destination,
            PayloadLength: message.Payload.Length,
            Ttl: message.Ttl));
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
    private async Task DeliverOrderedAsync(BackhaulMessage message, string sourceCallsign, string originator, string headersJson, CancellationToken ct)
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
            events.Publish(new InboundEvent(now, message.Id, sourceCallsign, message.Destination, message.Payload.Length, message.Ttl));
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
            events.Publish(new InboundEvent(now, message.Id, sourceCallsign, message.Destination, message.Payload.Length, message.Ttl));
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
        events.Publish(new InboundEvent(now, message.Id, sourceCallsign, message.Destination, message.Payload.Length, message.Ttl));
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
    /// Called by <see cref="StreamGapSweeper"/> when a recv-state's
    /// gap deadline has elapsed. Skips the cursor forward to the
    /// smallest pending sn for that stream and drains successors.
    /// Logs each skipped seq for operator visibility.
    /// </summary>
    internal async Task SkipGapAsync(DbStreamRecvState recv, CancellationToken ct)
    {
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
}
