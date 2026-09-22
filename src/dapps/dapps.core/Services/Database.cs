using dapps.client;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;
using System.Text.Json;

namespace dapps.core.Services;


public class Database(
    ILogger<Database> logger,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider? timeProviderOpt = null)
{
    // Default to the system clock when DI / tests don't supply one.
    // Lets the existing test-fixture call sites
    // (`new Database(NullLogger, opts)`) keep working - they get
    // production semantics without each test having to construct a
    // FakeTimeProvider. Cadence-sensitive tests inject one.
    private readonly TimeProvider timeProvider = timeProviderOpt ?? TimeProvider.System;
    internal async Task DeleteOffer(string id)
    {
        await DbInfo.GetAsyncConnection().DeleteAsync<DbOffer>(id);
    }

    public async Task<ICollection<DbMessage>> GetPendingOutboundMessages()
    {
        var connection = DbInfo.GetAsyncConnection();
        // Outbound = destined for a remote node and not yet forwarded.
        // "Local" matches when the @-suffix of Destination matches our base callsign.
        var local = options.CurrentValue.Callsign.Split('-')[0];
        var rows = await connection.QueryAsync<DbMessage>(
            "select * from messages where forwarded=0 and not (destination like ?);",
            $"%@{local}%");
        return rows;
    }

    /// <summary>Messages destined for a local app that haven't been ack'd yet.</summary>
    public async Task<ICollection<DbMessage>> GetUnacknowledgedLocalMessagesForApp(string appName)
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        // Destination shape is `app@call[-ssid]`; match exact app + local-callsign prefix.
        var prefix = $"{appName}@{local}";
        var rows = await connection.QueryAsync<DbMessage>(
            "select * from messages where locallydelivered=0 and (destination=? or destination like ?);",
            prefix, $"{prefix}-%");
        return rows;
    }

    public async Task MarkLocallyDelivered(string id)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync(
            "update messages set locallydelivered=1 where id=?", id);
    }

    /// <summary>
    /// Persist a fresh outbound message submitted by a local app via the MQTT
    /// or REST app interface. Computes the message id from the payload + a
    /// salt; returns the id for the caller to log/echo back to the app.
    ///
    /// <paramref name="ttlSeconds"/> is the residual lifetime the app
    /// requests for this message - propagates onto the outgoing
    /// <c>ihave</c> as <c>ttl=N</c>. Null means "no expiry", which makes
    /// the message persist in the queue indefinitely if it can't be
    /// forwarded; apps that want guaranteed cleanup should set a value.
    /// </summary>
    public async Task<string> SubmitOutboundMessage(
        string appName,
        string destCallsign,
        byte[] payload,
        int? ttlSeconds = null,
        string? streamId = null,
        uint? streamGapTimeoutSeconds = null)
    {
        var destination = $"{appName}@{destCallsign}";
        var ourCall = options.CurrentValue.Callsign;
        var threshold = options.CurrentValue.FragmentThresholdBytes;

        // Opt-in ordering: if a stream id is supplied, mint the next seq
        // from DbStreamSendState. The counter and the message persist in
        // the same call site; a crash between the two leaves the counter
        // coherent with the on-disk message (the message commit happens
        // after the counter bump). Gap timeout 0 (= strict) is the
        // default when streamId is set without an explicit value.
        var hasStream = !string.IsNullOrEmpty(streamId);
        if (hasStream && (streamGapTimeoutSeconds is null))
        {
            streamGapTimeoutSeconds = 0;
        }
        uint? streamSeq = null;
        if (hasStream)
        {
            streamSeq = await AllocateStreamSeqAsync(ourCall, destCallsign, streamId!);
        }

        // F2 multi-part: split payloads above the threshold into N
        // fragment rows. Each fragment is its own DbMessage with a
        // shared MasterId and a 1-based FragmentIndex. The forwarder
        // picks them up individually; the destination's inbox
        // reassembles. Threshold = 0 disables fragmentation.
        //
        // Opt-in ordering note: stream metadata is stamped on the
        // master id of a fragmented submission (and on the sole row
        // of a non-fragmented one). Each fragment carries the full
        // stream trio so a fragment-aware-but-stream-naive forwarder
        // doesn't accidentally strip ordering. The receiver only
        // applies ordering once reassembly produces the assembled
        // payload (fragments arriving out of frag-index order are
        // independent of the stream cursor).
        if (threshold > 0 && payload.Length > threshold)
        {
            var masterSalt = (long)(timeProvider.GetUtcNow().UtcDateTime - DateTime.UnixEpoch).TotalMilliseconds;
            var masterId = DappsMessage.ComputeHash(payload, masterSalt)[..7];
            var total = (payload.Length + threshold - 1) / threshold;
            for (var i = 0; i < total; i++)
            {
                var offset = i * threshold;
                var len = Math.Min(threshold, payload.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(payload, offset, chunk, 0, len);
                // Each fragment gets its own salt + per-fragment id.
                // Bump masterSalt to keep ids distinct across fragments
                // while staying related (same time-base).
                var fragSalt = masterSalt + i;
                var fragId = DappsMessage.ComputeHash(chunk, fragSalt)[..7];
                await SaveMessage(
                    fragId, chunk, fragSalt, destination,
                    sourceCallsign: ourCall, "{}", ttl: ttlSeconds,
                    originatorCallsign: ourCall,
                    masterId: masterId, fragmentIndex: i + 1, fragmentTotal: total,
                    streamId: streamId, streamSeq: streamSeq,
                    streamGapTimeoutSeconds: streamGapTimeoutSeconds);
            }
            return masterId;
        }

        var salt = (long)(timeProvider.GetUtcNow().UtcDateTime - DateTime.UnixEpoch).TotalMilliseconds;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        // Local submission: we are both the link-source AND the
        // originator. Recorded explicitly so re-forwards downstream
        // surface us in the receiver's dapps-origin user property.
        await SaveMessage(id, payload, salt, destination, sourceCallsign: ourCall, "{}", ttl: ttlSeconds,
            originatorCallsign: ourCall,
            streamId: streamId, streamSeq: streamSeq,
            streamGapTimeoutSeconds: streamGapTimeoutSeconds);
        return id;
    }

    internal async Task<DbOffer> LoadOfferMetadata(string id)
    {
        var data = await DbInfo.GetAsyncConnection().GetAsync<DbOffer>(id);
        logger.LogInformation("Loaded metadata for offer {0}", id);
        return data;
    }

    internal async Task SaveMessage(string id, byte[] buffer, long? salt, string destination, string sourceCallsign, string additionalProperties, int? ttl, string originatorCallsign = "", byte? floodHopsRemaining = null, string? sourceRouteCsv = null, string? traversedHopsCsv = null, string? masterId = null, int? fragmentIndex = null, int? fragmentTotal = null, string? streamId = null, uint? streamSeq = null, uint? streamGapTimeoutSeconds = null, bool pendingInOrder = false)
    {
        var connection = DbInfo.GetAsyncConnection();

        var message = await connection.FindAsync<DbMessage>(id);

        if (message != null)
        {
            logger.LogWarning("Message {0} already exists, overwriting", id);
            await connection.DeleteAsync<DbMessage>(id);
        }

        await DbInfo.GetAsyncConnection().InsertAsync(new DbMessage
        {
            Id = id,
            Salt = salt,
            Payload = buffer,
            Destination = destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originatorCallsign,
            AdditionalProperties = additionalProperties,
            Ttl = ttl,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            FloodHopsRemaining = floodHopsRemaining,
            SourceRouteCsv = sourceRouteCsv,
            TraversedHopsCsv = traversedHopsCsv,
            MasterId = masterId,
            FragmentIndex = fragmentIndex,
            FragmentTotal = fragmentTotal,
            StreamId = streamId,
            StreamSeq = streamSeq,
            StreamGapTimeoutSeconds = streamGapTimeoutSeconds,
            PendingInOrder = pendingInOrder,
        });
    }

    internal async Task SaveOffer(IHaveOffer offer)
    {
        var connection = DbInfo.GetAsyncConnection();

        var existing = await connection.FindAsync<DbOffer>(offer.Id);
        if (existing != null)
        {
            logger.LogWarning("We already have metadata for offer {0}, overwriting", offer.Id);
            await connection.DeleteAsync<DbOffer>(offer.Id);
        }

        await connection.InsertAsync(new DbOffer
        {
            Id = offer.Id,
            Length = offer.Length,
            Format = offer.Format,
            Salt = offer.Salt,
            CompressedLength = offer.CompressedLength,
            Destination = offer.Destination,
            OriginatorCallsign = offer.Originator ?? "",
            AdditionalProperties = JsonSerializer.Serialize(offer.AdditionalHeaders),
            Ttl = offer.Ttl,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            MasterId = offer.MasterId,
            FragmentIndex = offer.Fragment?.Index,
            FragmentTotal = offer.Fragment?.Total,
            StreamId = offer.StreamId,
            StreamSeq = offer.StreamSeq,
            StreamGapTimeoutSeconds = offer.StreamGapTimeoutSeconds,
        });

        logger.LogInformation("Saved metadata for offer {0}", offer.Id);
    }

    internal async Task<DbRouteHint?> GetRouteHint(string destination)
    {
        return await DbInfo.GetAsyncConnection().FindAsync<DbRouteHint>(destination);
    }

    internal async Task<DbNeighbour> GetNeighbour(string callsign)
    {
        return await DbInfo.GetAsyncConnection().FindWithQueryAsync<DbNeighbour>("select * from neighbours where callsign=?", callsign);
    }

    internal async Task MarkMessageAsForwarded(string id)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync("update messages set forwarded=1 where id=?", id);
    }

    internal async Task DeleteMessage(string id)
    {
        await DbInfo.GetAsyncConnection().DeleteAsync<DbMessage>(id);
    }

    /// <summary>
    /// Soft-delete: copy the message into <c>dropped_messages</c> with
    /// the given reason + a now() timestamp, then remove from
    /// <c>messages</c>. Idempotent - a missing row is a no-op (it might
    /// have been concurrently dropped). Used by the forwarder and TTL
    /// sweeper instead of <see cref="DeleteMessage"/> when discarding
    /// rather than acknowledging.
    /// </summary>
    internal async Task SoftDeleteMessage(string id, string reason)
    {
        var c = DbInfo.GetAsyncConnection();
        var row = await c.FindAsync<DbMessage>(id);
        if (row is null) return;
        await c.InsertAsync(new DbDroppedMessage
        {
            Id = row.Id,
            Payload = row.Payload,
            Salt = row.Salt,
            Destination = row.Destination,
            SourceCallsign = row.SourceCallsign,
            AdditionalProperties = row.AdditionalProperties,
            Forwarded = row.Forwarded,
            LocallyDelivered = row.LocallyDelivered,
            Ttl = row.Ttl,
            CreatedAt = row.CreatedAt,
            DroppedAt = timeProvider.GetUtcNow().UtcDateTime,
            Reason = reason,
            StreamId = row.StreamId,
            StreamSeq = row.StreamSeq,
            StreamGapTimeoutSeconds = row.StreamGapTimeoutSeconds,
        });
        await c.DeleteAsync<DbMessage>(id);
    }

    public async Task<IReadOnlyList<DbDroppedMessage>> GetRecentDroppedMessages(int limit = 50)
    {
        var c = DbInfo.GetAsyncConnection();
        var rows = await c.QueryAsync<DbDroppedMessage>(
            "select * from dropped_messages order by DroppedAt desc limit ?", limit);
        return rows.ToList();
    }

    /// <summary>Single dropped-message lookup by id. Returns null if the
    /// row has aged out of the dropped-messages table or never existed.
    /// Used by the dashboard's payload-preview endpoint so the "Dropped"
    /// tab can show what a dropped message actually contained.</summary>
    public async Task<DbDroppedMessage?> GetDroppedMessage(string id)
        => await DbInfo.GetAsyncConnection().FindAsync<DbDroppedMessage>(id);

    /// <summary>
    /// Soft-delete every message whose TTL has elapsed. Hard-delete
    /// every offer whose TTL has elapsed (offers are protocol-level
    /// scaffolding - there's no audit value in keeping a "we never
    /// got the data for that offer" row around). Returns the count of
    /// rows actioned across both tables.
    /// </summary>
    internal async Task<int> DeleteExpired(DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();

        // SQLite-net stores DateTime as ticks. CreatedAt + ttl seconds < now.
        // We can't do "+ ttl seconds" portably in SQL, so do the comparison
        // in C# after pulling the candidate rows. Both tables are small.
        var expiredOffers = (await connection.QueryAsync<DbOffer>(
                "select * from offers where Ttl is not null"))
            .Where(o => TtlMath.HasExpired(o.Ttl, o.CreatedAt, now))
            .ToList();
        foreach (var offer in expiredOffers)
        {
            await connection.DeleteAsync<DbOffer>(offer.Id);
        }

        var expiredMessages = (await connection.QueryAsync<DbMessage>(
                "select * from messages where Ttl is not null"))
            .Where(m => TtlMath.HasExpired(m.Ttl, m.CreatedAt, now))
            .ToList();
        foreach (var message in expiredMessages)
        {
            await SoftDeleteMessage(message.Id, "ttl-expired");
        }

        return expiredOffers.Count + expiredMessages.Count;
    }

    internal async Task<ICollection<DbNeighbour>> GetNeighbours()
    {
        return await DbInfo.GetAsyncConnection().QueryAsync<DbNeighbour>("select * from neighbours");
    }

    /// <summary>
    /// Most-recent messages across both local and remote destinations,
    /// newest first. Used by the dashboard to give a sysop a live view
    /// of activity without paging through the whole queue.
    /// </summary>
    public async Task<IReadOnlyList<DbMessage>> GetRecentMessages(int limit)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbMessage>(
            "select * from messages order by CreatedAt desc limit ?", limit);
    }

    /// <summary>Single-message lookup by id. Returns null if the row
    /// has been delivered + cleaned up or never existed. Used by the
    /// MCP tools (Plan M) to surface a specific message's state to
    /// an operator-assistant ("explain why abc1234 didn't ship").</summary>
    public async Task<DbMessage?> GetMessage(string id)
        => await DbInfo.GetAsyncConnection().FindAsync<DbMessage>(id);

    /// <summary>Every manual route-hint row. Mirrors
    /// <see cref="GetRouteHint(string)"/> but for the whole table.</summary>
    public async Task<IReadOnlyList<DbRouteHint>> GetRouteHintsAsync()
        => await DbInfo.GetAsyncConnection().QueryAsync<DbRouteHint>(
            "select * from routehints order by Destination");

    /// <summary>Total message-table row count, for dashboard summaries.</summary>
    public async Task<int> CountMessages()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteScalarAsync<int>("select count(*) from messages");
    }

    /// <summary>Pending outbound rows - messages destined for a remote
    /// node that haven't yet been forwarded.</summary>
    public async Task<int> CountPendingOutbound()
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from messages where forwarded=0 and not (destination like ?);",
            $"%@{local}%");
    }

    /// <summary>Undelivered local rows - messages destined for a local
    /// app that haven't been ack'd by the app yet.</summary>
    public async Task<int> CountUndeliveredLocal()
    {
        var connection = DbInfo.GetAsyncConnection();
        var local = options.CurrentValue.Callsign.Split('-')[0];
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from messages where locallydelivered=0 and (destination like ?);",
            $"%@{local}%");
    }

    /// <summary>
    /// Insert or refresh a discovered-peer row. Keyed on
    /// (callsign, bearer, channel-key) so the same peer heard on
    /// multiple channels occupies multiple rows. Idempotent - beacons
    /// re-arrive every cadence and just bump
    /// <see cref="DbDiscoveredPeer.LastSeen"/>.
    /// </summary>
    internal async Task UpsertDiscoveredPeer(DbDiscoveredPeer peer)
    {
        peer.PeerKey = DbDiscoveredPeer.MakeKey(peer.Callsign, peer.Bearer, peer.ChannelKey);
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbDiscoveredPeer>(peer.PeerKey);
        if (existing is null)
        {
            await connection.InsertAsync(peer);
        }
        else
        {
            await connection.UpdateAsync(peer);
        }
    }

    /// <summary>Insert a new <see cref="DbDiscoveryChannel"/> row, or
    /// update the bearer-specific tunables on an existing one. Identity
    /// is (Bearer, ChannelKey) - same channel re-POSTed updates in
    /// place. Channel defaults are filled from <see cref="LinkClass"/>
    /// before persisting.</summary>
    internal async Task UpsertDiscoveryChannel(DbDiscoveryChannel channel)
    {
        channel.ApplyClassDefaults();
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindWithQueryAsync<DbDiscoveryChannel>(
            "select * from discoverychannels where Bearer=? and ChannelKey=?",
            channel.Bearer, channel.ChannelKey);
        if (existing is null)
        {
            await connection.InsertAsync(channel);
        }
        else
        {
            channel.Id = existing.Id;
            await connection.UpdateAsync(channel);
        }
    }

    public async Task<IReadOnlyList<DbDiscoveryChannel>> GetDiscoveryChannels()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveryChannel>(
            "select * from discoverychannels order by CostHint, Bearer, ChannelKey");
    }

    internal async Task<bool> RemoveDiscoveryChannel(int id)
    {
        var connection = DbInfo.GetAsyncConnection();
        var deleted = await connection.ExecuteAsync(
            "delete from discoverychannels where Id=?", id);
        return deleted > 0;
    }

    /// <summary>List all currently-known discovered peers, irrespective
    /// of staleness. The dashboard / debugger uses this; the routing
    /// resolver should age out before consulting.</summary>
    public async Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeers()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveredPeer>(
            "select * from discoveredpeers order by LastSeen desc");
    }

    /// <summary>Remove discovered-peer rows whose freshness window has
    /// elapsed. Returns the rows that were deleted so callers can
    /// record per-peer observability events without re-querying.</summary>
    internal async Task<IReadOnlyList<DbDiscoveredPeer>> AgeOutDiscoveredPeers(DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var stale = (await connection.QueryAsync<DbDiscoveredPeer>(
                "select * from discoveredpeers"))
            .Where(p => (now - p.LastSeen).TotalSeconds > p.TtlSeconds)
            .ToList();
        foreach (var p in stale)
        {
            await connection.DeleteAsync<DbDiscoveredPeer>(p.PeerKey);
        }
        return stale;
    }

    /// <summary>
    /// Insert a neighbour or update its bearer hints if one already
    /// exists for the same callsign. Idempotent: callers can re-POST
    /// the same neighbour without checking for prior existence.
    /// </summary>
    internal async Task UpsertNeighbour(string callsign, int? bearerPort, string? udpEndpoint = null, string? connectScriptJson = null)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindWithQueryAsync<DbNeighbour>(
            "select * from neighbours where callsign=?", callsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbNeighbour
            {
                Callsign = callsign,
                BearerPort = bearerPort,
                UdpEndpoint = udpEndpoint,
                ConnectScriptJson = connectScriptJson,
            });
        }
        else
        {
            existing.BearerPort = bearerPort;
            existing.UdpEndpoint = udpEndpoint;
            existing.ConnectScriptJson = connectScriptJson;
            await connection.UpdateAsync(existing);
        }
    }

    /// <summary>
    /// Remove a neighbour by callsign. Returns true if a row was deleted,
    /// false if no neighbour existed with that callsign.
    /// </summary>
    internal async Task<bool> RemoveNeighbour(string callsign)
    {
        var deleted = await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from neighbours where callsign=?", callsign);
        return deleted > 0;
    }

    // ── Passive-learning routes (PR-B) ──────────────────────────────

    /// <summary>
    /// Record (or refresh) a learned route for the given destination.
    /// One row per destination base callsign - newer observations
    /// overwrite older ones. <see cref="DbLearnedRoute.LastSeenAt"/>
    /// always bumps to <c>now</c>; failure counter resets when the
    /// next-hop changes (a new path is fresh evidence of liveness).
    /// </summary>
    internal async Task UpsertLearnedRouteAsync(string destinationBaseCallsign, string nextHopCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbLearnedRoute
            {
                DestinationBaseCallsign = destinationBaseCallsign,
                NextHopCallsign = nextHopCallsign,
                LastSeenAt = now,
                LastUsedAt = DateTime.MinValue,
                ConsecutiveFailures = 0,
            });
            return;
        }

        existing.LastSeenAt = now;
        if (!string.Equals(existing.NextHopCallsign, nextHopCallsign, StringComparison.OrdinalIgnoreCase))
        {
            existing.NextHopCallsign = nextHopCallsign;
            existing.ConsecutiveFailures = 0;
        }
        await connection.UpdateAsync(existing);
    }

    /// <summary>Look up the current learned route, if any.</summary>
    internal async Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationBaseCallsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbLearnedRoute>(destinationBaseCallsign);

    /// <summary>Forward succeeded - reset failure counter, update <see cref="DbLearnedRoute.LastUsedAt"/>.</summary>
    internal async Task RecordLearnedRouteSuccessAsync(string destinationBaseCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (row is null) return;
        row.ConsecutiveFailures = 0;
        row.LastUsedAt = now;
        await connection.UpdateAsync(row);
    }

    /// <summary>Forward failed - increment failure counter; delete the
    /// row if the threshold is hit. Returns the new failure count, or
    /// <c>-1</c> if the row was deleted (i.e. invalidated).</summary>
    internal async Task<int> RecordLearnedRouteFailureAsync(string destinationBaseCallsign, int invalidationThreshold)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (row is null) return 0;
        row.ConsecutiveFailures++;
        if (row.ConsecutiveFailures >= invalidationThreshold)
        {
            await connection.DeleteAsync<DbLearnedRoute>(destinationBaseCallsign);
            return -1;
        }
        await connection.UpdateAsync(row);
        return row.ConsecutiveFailures;
    }

    /// <summary>All current learned routes - for dashboard / debug.</summary>
    public async Task<IReadOnlyList<DbLearnedRoute>> GetLearnedRoutesAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbLearnedRoute>("select * from learnedroutes order by LastSeenAt desc");
    }

    // ── Flood deduplication (PR-C) ──────────────────────────────────

    /// <summary>
    /// Have we already processed a flood with this (id, link-source)
    /// pair? Returns true if the row exists. The lookup is cheap
    /// (primary-key-indexed); used on every inbound flooded message
    /// to short-circuit duplicates before any expensive processing.
    /// </summary>
    internal async Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbFloodSeen.MakeKey(messageId, linkSourceCallsign);
        var row = await connection.FindAsync<DbFloodSeen>(key);
        return row is not null;
    }

    /// <summary>Idempotent record-this-flood. No-ops if the row
    /// already exists.</summary>
    internal async Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbFloodSeen.MakeKey(messageId, linkSourceCallsign);
        var existing = await connection.FindAsync<DbFloodSeen>(key);
        if (existing is not null) return;
        await connection.InsertAsync(new DbFloodSeen { Key = key, SeenAt = now });
    }

    /// <summary>Drop flood-seen rows older than <paramref name="cutoff"/>.
    /// The dedup window must outlive the maximum expected flood
    /// propagation time; everything older than that is just memory
    /// pressure.</summary>
    internal async Task<int> SweepFloodSeenAsync(DateTime cutoff)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteAsync(
            "delete from flood_seen where SeenAt < ?", cutoff.Ticks);
    }

    // ── Discovered paths (MeshCore-flavoured algorithm) ─────────────

    /// <summary>
    /// Record (or refresh) the intermediate-hop path to a destination.
    /// The most-recent observation wins; failure counter resets when
    /// the path itself changes (a different intermediate set is fresh
    /// evidence of liveness over the new path).
    /// </summary>
    internal async Task UpsertDiscoveredPathAsync(string destinationBaseCallsign, IReadOnlyList<string> intermediates, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var csv = DbDiscoveredPath.ToCsv(intermediates);
        var existing = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (existing is null)
        {
            await connection.InsertAsync(new DbDiscoveredPath
            {
                DestinationBaseCallsign = destinationBaseCallsign,
                IntermediatesCsv = csv,
                LastSeenAt = now,
                LastUsedAt = DateTime.MinValue,
                ConsecutiveFailures = 0,
            });
            return;
        }

        existing.LastSeenAt = now;
        if (!string.Equals(existing.IntermediatesCsv, csv, StringComparison.OrdinalIgnoreCase))
        {
            existing.IntermediatesCsv = csv;
            existing.ConsecutiveFailures = 0;
        }
        await connection.UpdateAsync(existing);
    }

    internal async Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationBaseCallsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbDiscoveredPath>(destinationBaseCallsign);

    internal async Task RecordDiscoveredPathSuccessAsync(string destinationBaseCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (row is null) return;
        row.ConsecutiveFailures = 0;
        row.LastUsedAt = now;
        await connection.UpdateAsync(row);
    }

    internal async Task<int> RecordDiscoveredPathFailureAsync(string destinationBaseCallsign, int invalidationThreshold)
    {
        var connection = DbInfo.GetAsyncConnection();
        var row = await connection.FindAsync<DbDiscoveredPath>(destinationBaseCallsign);
        if (row is null) return 0;
        row.ConsecutiveFailures++;
        if (row.ConsecutiveFailures >= invalidationThreshold)
        {
            await connection.DeleteAsync<DbDiscoveredPath>(destinationBaseCallsign);
            return -1;
        }
        await connection.UpdateAsync(row);
        return row.ConsecutiveFailures;
    }

    /// <summary>All current discovered paths - for dashboard / debug.</summary>
    public async Task<IReadOnlyList<DbDiscoveredPath>> GetDiscoveredPathsAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbDiscoveredPath>("select * from discoveredpaths order by LastSeenAt desc");
    }


    // ── Probed nodes (B6.1) ──────────────────────────────────────────

    /// <summary>List every <see cref="DbProbedNode"/> row, newest probe first.</summary>
    public async Task<IReadOnlyList<DbProbedNode>> GetProbedNodes()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbProbedNode>(
            "select * from probednodes order by " +
            "case when LastProbedAt is null then 1 else 0 end, " +
            "LastProbedAt desc, Callsign asc");
    }

    public async Task<DbProbedNode?> GetProbedNode(string callsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbProbedNode>(callsign);

    /// <summary>Idempotent upsert of a probed-node row. Used both by the
    /// scheduler when recording results, and by the controller when an
    /// operator pre-creates an opt-out entry.</summary>
    internal async Task UpsertProbedNode(DbProbedNode node)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbProbedNode>(node.Callsign);
        if (existing is null)
        {
            await connection.InsertAsync(node);
        }
        else
        {
            await connection.UpdateAsync(node);
        }
    }

    /// <summary>Remove a probed-node row by callsign. Returns true if a
    /// row was actually deleted.</summary>
    internal async Task<bool> RemoveProbedNode(string callsign)
    {
        var deleted = await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from probednodes where callsign=?", callsign);
        return deleted > 0;
    }

    // ── Fragment reassembly buffer (F2) ────────────────────────────

    /// <summary>
    /// Insert (or refresh, on a re-arrival of the same fragment) a
    /// DbFragment row. Idempotent on the (masterId, index) primary
    /// key. <see cref="DbFragment.FirstSeenAt"/> is preserved across
    /// upserts - the stale-fragment sweep clock is set by the original
    /// arrival, not the latest copy. Plan F2.
    /// </summary>
    internal async Task UpsertFragment(DbFragment fragment)
    {
        fragment.Key = DbFragment.MakeKey(fragment.MasterId, fragment.FragmentIndex);
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbFragment>(fragment.Key);
        if (existing is null)
        {
            await connection.InsertAsync(fragment);
            return;
        }
        // Re-arrival of an already-stored fragment. Preserve the
        // original FirstSeenAt so the sweep deadline doesn't drift.
        fragment.FirstSeenAt = existing.FirstSeenAt;
        await connection.UpdateAsync(fragment);
    }

    /// <summary>All currently-stored fragments for a master id, ordered
    /// by index. Empty if no fragments have arrived for that
    /// master.</summary>
    internal async Task<IReadOnlyList<DbFragment>> GetFragmentsForMaster(string masterId)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbFragment>(
            "select * from fragments where MasterId=? order by FragmentIndex asc", masterId);
    }

    /// <summary>Drop every fragment row for the given master id.
    /// Called after successful reassembly so the row's payload bytes
    /// don't sit on disk twice (once in fragments + once in the
    /// assembled DbMessage).</summary>
    internal async Task DeleteFragmentsForMaster(string masterId)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from fragments where MasterId=?", masterId);
    }

    /// <summary>Drop fragment rows whose FirstSeenAt is older than
    /// <paramref name="cutoff"/>. Called by the TTL sweeper on a slow
    /// cadence. Returns the number of rows deleted (across all
    /// master ids).</summary>
    internal async Task<int> SweepStaleFragments(DateTime cutoff)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteAsync(
            "delete from fragments where FirstSeenAt < ?", cutoff.Ticks);
    }

    /// <summary>For dashboard/debug - list every fragment row, newest
    /// first by first-seen time. Useful when an incomplete reassembly
    /// is hanging around and the sysop wants to know what's missing.</summary>
    public async Task<IReadOnlyList<DbFragment>> GetAllFragments()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbFragment>(
            "select * from fragments order by FirstSeenAt desc, FragmentIndex asc");
    }

    // ── F3 rev poll: messages we'd hand to a polling caller ─────────

    /// <summary>
    /// Plan F3 - return outbound queue entries whose final destination
    /// matches <paramref name="callerBaseCallsign"/> (the SSID-stripped
    /// base of the polling station's callsign). These are the messages
    /// that the rev handler would drain to a station calling <c>rev</c>.
    ///
    /// When <paramref name="requestedIds"/> is empty, returns every
    /// matching un-forwarded row. When non-empty, narrows to that
    /// specific set - used for selective polling
    /// (<c>rev id1 id2 ...</c>).
    ///
    /// Final destination only - we don't drain transit messages where
    /// the caller is just a known forwarder; the caller's session is
    /// for THEIR mail, not for them to act as a downstream relay
    /// (that's a separate request shape; design decision in plan F3).
    /// </summary>
    internal async Task<IReadOnlyList<DbMessage>> GetMessagesForCaller(
        string callerBaseCallsign, IReadOnlyList<string> requestedIds)
    {
        var connection = DbInfo.GetAsyncConnection();
        // Two cases on the wire: "app@CALL" (no SSID) and
        // "app@CALL-N" (with SSID). The two `like` patterns cover both
        // and the C# filter below pins the base-callsign suffix exactly
        // (so e.g. caller "N0THEM" doesn't pick up dst="app@N0THEMA").
        var rows = await connection.QueryAsync<DbMessage>(
            "select * from messages where forwarded=0 and (destination like ? or destination like ?) " +
            "order by CreatedAt asc",
            $"%@{callerBaseCallsign}",
            $"%@{callerBaseCallsign}-%");
        var matching = rows
            .Where(r => MatchesCallerBase(r.Destination, callerBaseCallsign))
            .ToList();
        if (requestedIds.Count == 0) return matching;
        var idSet = requestedIds.ToHashSet(StringComparer.Ordinal);
        return matching.Where(r => idSet.Contains(r.Id)).ToList();
    }

    private static bool MatchesCallerBase(string destination, string callerBase)
    {
        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1) return false;
        var destCall = destination[(at + 1)..];
        var destBase = destCall.Split('-')[0];
        return string.Equals(destBase, callerBase, StringComparison.OrdinalIgnoreCase);
    }

    // ── Polled nodes (F3b) ─────────────────────────────────────────

    public async Task<IReadOnlyList<DbPolledNode>> GetPolledNodes()
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbPolledNode>(
            "select * from polledNodes order by " +
            "case when LastPolledAt is null then 1 else 0 end, " +
            "LastPolledAt desc, Callsign asc");
    }

    public async Task<DbPolledNode?> GetPolledNode(string callsign)
        => await DbInfo.GetAsyncConnection().FindAsync<DbPolledNode>(callsign);

    internal async Task UpsertPolledNode(DbPolledNode node)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbPolledNode>(node.Callsign);
        if (existing is null)
        {
            await connection.InsertAsync(node);
        }
        else
        {
            await connection.UpdateAsync(node);
        }
    }

    internal async Task<bool> RemovePolledNode(string callsign)
    {
        var deleted = await DbInfo.GetAsyncConnection().ExecuteAsync(
            "delete from polledNodes where callsign=?", callsign);
        return deleted > 0;
    }

    // ── Opt-in message ordering: send-side counter ──────────────────

    /// <summary>
    /// Mint the next sender-side seq for (LocalCallsign, RemoteCallsign,
    /// StreamId) and persist the bumped counter. Idempotent only across
    /// transactions: each call returns a fresh seq and advances the row.
    /// New rows start at 1; existing rows return NextSeq and increment.
    /// </summary>
    internal async Task<uint> AllocateStreamSeqAsync(string localCallsign, string remoteCallsign, string streamId)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbStreamSendState.MakeKey(localCallsign, remoteCallsign, streamId);
        var row = await connection.FindAsync<DbStreamSendState>(key);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            await connection.InsertAsync(new DbStreamSendState
            {
                Key = key,
                LocalCallsign = localCallsign,
                RemoteCallsign = remoteCallsign,
                StreamId = streamId,
                NextSeq = 2,
                UpdatedAt = now,
            });
            return 1;
        }
        var seq = row.NextSeq;
        row.NextSeq = seq + 1;
        row.UpdatedAt = now;
        await connection.UpdateAsync(row);
        return seq;
    }

    /// <summary>All sender-side stream counters - dashboard listing.</summary>
    public async Task<IReadOnlyList<DbStreamSendState>> GetStreamSendStatesAsync()
        => await DbInfo.GetAsyncConnection().QueryAsync<DbStreamSendState>(
            "select * from streamsendstate order by UpdatedAt desc");

    // ── Opt-in message ordering: recv-side cursor ───────────────────

    /// <summary>Look up the receive cursor for a (sender, stream) pair.
    /// Null when no message has ever been seen for that pair.</summary>
    internal async Task<DbStreamRecvState?> GetStreamRecvStateAsync(string localCallsign, string senderCallsign, string streamId)
    {
        var key = DbStreamRecvState.MakeKey(localCallsign, senderCallsign, streamId);
        return await DbInfo.GetAsyncConnection().FindAsync<DbStreamRecvState>(key);
    }

    /// <summary>Idempotent upsert of the recv cursor.</summary>
    internal async Task UpsertStreamRecvStateAsync(DbStreamRecvState state)
    {
        state.Key = DbStreamRecvState.MakeKey(state.LocalCallsign, state.SenderCallsign, state.StreamId);
        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbStreamRecvState>(state.Key);
        if (existing is null) await connection.InsertAsync(state);
        else await connection.UpdateAsync(state);
    }

    /// <summary>All recv cursors - dashboard listing.</summary>
    public async Task<IReadOnlyList<DbStreamRecvState>> GetStreamRecvStatesAsync()
        => await DbInfo.GetAsyncConnection().QueryAsync<DbStreamRecvState>(
            "select * from streamrecvstate order by LastReceivedAt desc");

    /// <summary>Pending (parked-awaiting-prior) ordered messages for a
    /// (sender, stream) pair, ordered by StreamSeq ascending. The inbox
    /// drains from the front as the cursor advances.</summary>
    internal async Task<IReadOnlyList<DbMessage>> GetPendingInOrderAsync(string senderCallsign, string streamId)
    {
        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbMessage>(
            "select * from messages where PendingInOrder=1 and OriginatorCallsign=? and StreamId=? order by StreamSeq asc",
            senderCallsign, streamId);
    }

    /// <summary>Mark a parked row as no-longer-pending. Used when the
    /// inbox drains it to MQTT or the sweeper skips past it.</summary>
    internal async Task ClearPendingInOrderAsync(string id)
    {
        await DbInfo.GetAsyncConnection().ExecuteAsync(
            "update messages set PendingInOrder=0 where Id=?", id);
    }

    /// <summary>Recv cursors that have a non-MinValue gap deadline at or
    /// before <paramref name="cutoff"/>. The sweeper advances these.</summary>
    internal async Task<IReadOnlyList<DbStreamRecvState>> GetStaleStreamGapsAsync(DateTime cutoff)
    {
        var connection = DbInfo.GetAsyncConnection();
        var rows = await connection.QueryAsync<DbStreamRecvState>(
            "select * from streamrecvstate where GapDeadline > 0 and GapDeadline <= ?",
            cutoff.Ticks);
        return rows;
    }

    // ── Route gossip ──────────────────────────────────────────────

    /// <summary>
    /// Should we piggyback a <c>routes</c> pull on the next session
    /// to <paramref name="remoteCallsign"/>? True when no record exists
    /// (never pulled) or when the previous pull is older than
    /// <paramref name="stalenessHours"/>. Pure read - the caller must
    /// follow up with <see cref="MarkRouteGossipPulledAsync"/> when
    /// the pull actually happens.
    /// </summary>
    internal async Task<bool> ShouldPullRouteGossipAsync(
        string localCallsign, string remoteCallsign, int stalenessHours, DateTime now)
    {
        if (stalenessHours <= 0) return false;
        var key = DbRouteGossipState.MakeKey(localCallsign, remoteCallsign);
        var row = await DbInfo.GetAsyncConnection().FindAsync<DbRouteGossipState>(key);
        if (row is null) return true;
        return (now - row.LastPulledAt).TotalHours >= stalenessHours;
    }

    /// <summary>Record that a routes pull just happened. Idempotent
    /// upsert.</summary>
    internal async Task MarkRouteGossipPulledAsync(
        string localCallsign, string remoteCallsign, DateTime now)
    {
        var connection = DbInfo.GetAsyncConnection();
        var key = DbRouteGossipState.MakeKey(localCallsign, remoteCallsign);
        var row = await connection.FindAsync<DbRouteGossipState>(key);
        if (row is null)
        {
            await connection.InsertAsync(new DbRouteGossipState
            {
                Key = key,
                LocalCallsign = localCallsign,
                RemoteCallsign = remoteCallsign,
                LastPulledAt = now,
            });
        }
        else
        {
            row.LastPulledAt = now;
            await connection.UpdateAsync(row);
        }
    }

    /// <summary>
    /// Import a route gossiped to us by <paramref name="advertiserCallsign"/>.
    /// Writes (or refreshes) a <see cref="DbLearnedRoute"/> row with
    /// <c>Source = "gossip"</c>; the next-hop is the advertiser. We
    /// don't overwrite a traffic-learned row with a gossip one - direct
    /// observation outranks hearsay.
    /// </summary>
    internal async Task UpsertGossipedRouteAsync(
        string destinationBaseCallsign, string advertiserCallsign, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(destinationBaseCallsign)) return;
        if (string.IsNullOrWhiteSpace(advertiserCallsign)) return;
        // Don't import a route TO ourselves - the advertiser may know
        // they can reach us, but we already know who we are.
        var advertiserBase = advertiserCallsign.Split('-')[0];
        if (string.Equals(destinationBaseCallsign, advertiserBase, StringComparison.OrdinalIgnoreCase)) return;

        var connection = DbInfo.GetAsyncConnection();
        var existing = await connection.FindAsync<DbLearnedRoute>(destinationBaseCallsign);
        if (existing is not null
            && !string.Equals(existing.Source, "gossip", StringComparison.OrdinalIgnoreCase))
        {
            // Existing row is traffic-learned (Source = "" or "traffic")
            // - direct observation outranks hearsay, don't overwrite.
            return;
        }
        if (existing is null)
        {
            await connection.InsertAsync(new DbLearnedRoute
            {
                DestinationBaseCallsign = destinationBaseCallsign,
                NextHopCallsign = advertiserCallsign,
                LastSeenAt = now,
                LastUsedAt = DateTime.MinValue,
                ConsecutiveFailures = 0,
                Source = "gossip",
            });
            return;
        }

        existing.LastSeenAt = now;
        if (!string.Equals(existing.NextHopCallsign, advertiserCallsign, StringComparison.OrdinalIgnoreCase))
        {
            existing.NextHopCallsign = advertiserCallsign;
            existing.ConsecutiveFailures = 0;
        }
        existing.Source = "gossip";
        await connection.UpdateAsync(existing);
    }
}