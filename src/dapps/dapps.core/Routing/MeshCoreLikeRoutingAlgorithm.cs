using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;

namespace dapps.core.Routing;

/// <summary>
/// MeshCore-flavoured DSR-with-passive-discovery. Two complementary
/// mechanisms:
///
/// <list type="number">
/// <item>Source-routed delivery: when this node has a discovered
///   path to the destination, the path is embedded in the outbound
///   <see cref="BackhaulMessage.SourceRoute"/>. Each downstream hop
///   pulls its next neighbour from the head of the list and strips
///   it before re-encoding - no per-hop routing decision needed
///   along the path. When a source-routed message arrives at a
///   forwarder, it just relays along the prescribed route.</item>
/// <item>Flood discovery: when this node has no path for a
///   destination, it originates a flood with an empty
///   <see cref="BackhaulMessage.TraversedHops"/> list. Each transit
///   node appends its own callsign before re-flooding to neighbours
///   it hasn't already traversed. When a flood arrives at any node,
///   the inbound TraversedHops reversed is the path back to the
///   originator - stored in <see cref="DbDiscoveredPath"/> for
///   future source-routed sends.</item>
/// </list>
///
/// Layered as a decorator: the inner algorithm (typically
/// <see cref="StaticRoutingAlgorithm"/>) ALWAYS wins for resolution
/// - operator overrides and direct neighbours take precedence over
/// discovered paths. Discovered paths are consulted only when inner
/// returns <see cref="RouteDecision.Unreachable"/>; flood-discovery
/// is the final fallback.
///
/// Failure handling: each failed forward against a discovered path
/// increments <see cref="DbDiscoveredPath.ConsecutiveFailures"/>;
/// once it hits <see cref="InvalidationThreshold"/> the row is
/// deleted and the next attempt falls through to flood-discovery.
///
/// Differences from <see cref="FloodFallbackAlgorithm"/>: this one
/// stores the FULL discovered path (not just the next hop), uses
/// the TraversedHops accumulator for loop avoidance during the
/// flood (rather than just excluding the immediate upstream), and
/// re-uses the discovered path for subsequent sends (eliminating
/// the need to flood per-message after first contact).
/// </summary>
public sealed class MeshCoreLikeRoutingAlgorithm(
    IRoutingAlgorithm inner,
    ILogger<MeshCoreLikeRoutingAlgorithm> logger) : IRoutingAlgorithm
{
    /// <summary>Forward failures before a discovered path is
    /// invalidated. 3 matches PassiveLearningAlgorithm - enough to
    /// ride out a transient without prematurely throwing away a
    /// working path.</summary>
    public const int InvalidationThreshold = 3;

    /// <summary>Default hop budget for a fresh flood-discovery -
    /// matches FloodFallbackAlgorithm so cold-start behaviour is
    /// comparable across algorithms. The 6-node sim's longest path
    /// is 4 hops; production meshes likely want 6-8.</summary>
    public const byte DefaultHopBudget = 6;

    public async Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
    {
        // 1. Source-routed message in transit: just follow the
        //    embedded route. SourceRouteCsv is non-null once the
        //    inbox persists a message that arrived with SourceRoute
        //    set; here we pull the next hop, strip it, and forward.
        if (message.SourceRouteCsv is not null)
        {
            return await ResolveSourceRoutedAsync(message, ctx, ct);
        }

        // 2. Flood-discovery in transit: append local callsign and
        //    re-flood. The hop budget on FloodHopsRemaining bounds
        //    propagation; the TraversedHops accumulator excludes
        //    nodes the message has already visited from the next
        //    flood wave, preventing repeat traversals.
        if (message.TraversedHopsCsv is not null && message.FloodHopsRemaining is { } remainingHops)
        {
            return await ContinueFloodAsync(message, remainingHops, ctx, ct);
        }

        // 3. Fresh originate (or relay-without-flood-state). Try
        //    inner first - operator overrides / direct neighbours
        //    win.
        var innerDecision = await inner.ResolveAsync(message, ctx, ct);
        if (innerDecision is not RouteDecision.Unreachable)
        {
            return innerDecision;
        }

        // 4. Inner gave up. Try a discovered path; embed it as a
        //    source route on the outbound message.
        //    Paths are kept per call+SSID (#242).
        var destCall = DestinationParser.StationOf(message.Destination);
        var path = await ctx.GetDiscoveredPathAsync(destCall, ct);
        if (path is not null)
        {
            var embed = await BuildSourceRoutedNextHopAsync(message, path, destCall, ctx, ct);
            if (embed is not null) return embed;
        }

        // 5. No path. Originate a flood-discovery with an empty
        //    TraversedHops accumulator. Receivers learn paths back
        //    to us as the flood propagates; once one of them
        //    eventually replies, our passive-observation hook
        //    populates a discovered path back.
        var floodRoutes = await BuildFloodRoutesAsync(ctx, traversed: [], ct);
        if (floodRoutes.Count == 0)
        {
            logger.LogWarning("No path AND no neighbours to flood for {0}; leaving in queue", message.Id);
            return new RouteDecision.Unreachable();
        }

        logger.LogInformation(
            "No discovered path for {0}; originating flood-discovery to {1} neighbour(s) with hop budget {2}",
            message.Id, floodRoutes.Count, DefaultHopBudget);
        return new RouteDecision.FloodToNeighbours(floodRoutes, DefaultHopBudget, TraversedHops: []);
    }

    public async Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct)
    {
        // Always give the inner algorithm a look - passive-learning
        // (when wrapped) still wants to learn next-hop routes from
        // the same observations.
        await inner.ObserveInboundAsync(message, linkSourceCallsign, ctx, ct);

        // Discovery-path learning only fires for messages that
        // arrived with TraversedHops set - i.e. flood-discovery
        // messages. Regular routed traffic doesn't carry a
        // traversal record.
        if (message.TraversedHops is null) return;
        if (string.IsNullOrEmpty(message.Originator)) return;

        // Don't learn a path back to ourselves. Our exact callsign only:
        // a flood from another SSID of ours teaches a path to it (#242).
        if (DestinationParser.SameStation(message.Originator, ctx.LocalCallsign)) return;

        // Reverse the traversed list to get the path FROM us back
        // to the originator. Filter out our own callsign defensively
        // - shouldn't appear, but a misbehaving peer could include
        // it and we'd loop.
        var reverse = message.TraversedHops
            .Reverse()
            .Where(h => !DestinationParser.SameStation(h, ctx.LocalCallsign))
            .ToList();

        await ctx.UpsertDiscoveredPathAsync(message.Originator, reverse, ct);
        logger.LogDebug(
            "Discovered path: {0} reachable via [{1}] (link source: {2})",
            message.Originator, string.Join(',', reverse), linkSourceCallsign);
    }

    public async Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct)
    {
        // Inner gets the outcome too - passive learning needs it for
        // its learned-route invalidation logic.
        await inner.ObserveForwardOutcomeAsync(message, attemptedRoute, result, ctx, ct);

        // We only update discovered-path stats for sends that
        // actually used a discovered path. If the source route was
        // populated from our discovered table (or the message
        // arrived already source-routed and the head matches our
        // destination's first intermediate), the outcome reflects
        // path liveness.
        var destCall = DestinationParser.StationOf(message.Destination);
        var path = await ctx.GetDiscoveredPathAsync(destCall, ct);
        if (path is null) return;

        // Heuristic: if the attempted route went to the neighbour the
        // path's first hop resolves to (the first of our stored
        // intermediates, or the destination itself when there are
        // none), this forward is exercising the discovered path.
        var intermediates = path.GetIntermediates();
        var pathFirstHop = intermediates.Count > 0 ? intermediates[0] : destCall;
        var firstHopNeighbour = CallsignMatch.Best(await ctx.GetNeighboursAsync(ct), n => n.Callsign, pathFirstHop, ctx.LocalCallsign);
        if (firstHopNeighbour is null || !DestinationParser.SameStation(attemptedRoute.Callsign, firstHopNeighbour.Callsign))
            return;

        if (result.Accepted)
        {
            await ctx.RecordDiscoveredPathSuccessAsync(destCall, ct);
        }
        else
        {
            var newCount = await ctx.RecordDiscoveredPathFailureAsync(destCall, InvalidationThreshold, ct);
            if (newCount < 0)
            {
                logger.LogInformation(
                    "Discovered path for {0} ([{1}]) invalidated after {2} consecutive failures",
                    destCall, string.Join(',', intermediates), InvalidationThreshold);
            }
        }
    }

    public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct)
        => inner.ObserveProbeOutcomeAsync(askedPeerCallsign, peers, ctx, ct);

    public Task RunAsync(IRoutingContext ctx, CancellationToken ct)
        => inner.RunAsync(ctx, ct);

    /// <summary>Resolve the next hop from a persisted source-routed
    /// message: pull the head of <see cref="DbMessage.SourceRouteCsv"/>,
    /// resolve to a neighbour, and return a NextHop with the
    /// remainder embedded so the receiver continues along the
    /// path.</summary>
    private async Task<RouteDecision> ResolveSourceRoutedAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
    {
        var remaining = string.IsNullOrEmpty(message.SourceRouteCsv)
            ? []
            : message.SourceRouteCsv.Split(',');

        // Head of the list is the next hop; if the list is empty
        // the destination is direct.
        string nextHopCallsign;
        IReadOnlyList<string> downstream;
        if (remaining.Length == 0)
        {
            nextHopCallsign = DestinationParser.StationOf(message.Destination);
            downstream = [];
        }
        else
        {
            nextHopCallsign = remaining[0];
            downstream = remaining.Skip(1).ToList();
        }

        var nextHop = await ctx.GetNeighbourByCallsignAsync(nextHopCallsign, ct);
        if (nextHop is null)
        {
            // Try base-callsign match against the manual neighbour
            // list - source routes use full callsigns, but neighbour
            // entries may differ in SSID. Fall back to a base-match
            // scan before declaring the source route broken.
            var neighbours = await ctx.GetNeighboursAsync(ct);
            nextHop = CallsignMatch.Best(neighbours, n => n.Callsign, nextHopCallsign, ctx.LocalCallsign);
            if (nextHop is null)
            {
                logger.LogWarning(
                    "Source-routed {0}: next hop {1} not in neighbours; route broken",
                    message.Id, nextHopCallsign);
                return new RouteDecision.Unreachable();
            }
        }

        logger.LogInformation(
            "Source-routed {0} for {1}: next hop {2}, remaining [{3}]",
            message.Id, message.Destination, nextHop.Callsign, string.Join(',', downstream));

        return new RouteDecision.NextHop(
            RouteBuilder.FromNeighbour(nextHop, ctx.DefaultPort),
            SourceRoute: downstream);
    }

    private async Task<RouteDecision> ContinueFloodAsync(DbMessage message, byte remainingHops, IRoutingContext ctx, CancellationToken ct)
    {
        if (remainingHops == 0)
        {
            logger.LogDebug("Flood-discovery for {0} dropped at this node: hop budget exhausted", message.Id);
            return new RouteDecision.Unreachable();
        }

        var traversed = string.IsNullOrEmpty(message.TraversedHopsCsv)
            ? []
            : message.TraversedHopsCsv.Split(',').ToList();

        // Append our callsign to the outbound traversal record so
        // the next hop knows we've been visited.
        var nextTraversed = traversed.Concat([ctx.LocalCallsign]).ToList();

        // The originator isn't in the traversal record (each transit node
        // adds itself), so it is kept from the flood separately.
        var routes = await BuildFloodRoutesAsync(ctx, [.. nextTraversed, message.OriginatorCallsign], ct);
        if (routes.Count == 0)
        {
            logger.LogDebug(
                "Flood-discovery for {0} dropped at this node: no untraversed neighbours to re-flood to",
                message.Id);
            return new RouteDecision.Unreachable();
        }

        return new RouteDecision.FloodToNeighbours(routes, (byte)(remainingHops - 1), nextTraversed);
    }

    private async Task<RouteDecision?> BuildSourceRoutedNextHopAsync(
        DbMessage message, DbDiscoveredPath path, string destCall, IRoutingContext ctx, CancellationToken ct)
    {
        var intermediates = path.GetIntermediates();
        string nextHopCallsign;
        IReadOnlyList<string> downstream;
        if (intermediates.Count == 0)
        {
            nextHopCallsign = DestinationParser.StationOf(message.Destination);
            downstream = [];
        }
        else
        {
            nextHopCallsign = intermediates[0];
            downstream = intermediates.Skip(1).ToList();
        }

        var nextHop = await ctx.GetNeighbourByCallsignAsync(nextHopCallsign, ct);
        if (nextHop is null)
        {
            var neighbours = await ctx.GetNeighboursAsync(ct);
            nextHop = CallsignMatch.Best(neighbours, n => n.Callsign, nextHopCallsign, ctx.LocalCallsign);
        }
        if (nextHop is null)
        {
            // The first hop along our discovered path isn't reachable
            // - drop the path and let the next tick re-flood.
            logger.LogInformation(
                "Discovered path for {0} unusable: first hop {1} not in neighbours; discarding path",
                destCall, nextHopCallsign);
            await ctx.RecordDiscoveredPathFailureAsync(destCall, invalidationThreshold: 1, ct);
            return null;
        }

        logger.LogInformation(
            "Routing {0} for {1} via discovered path: next hop {2}, downstream [{3}] (last seen {4:o})",
            message.Id, message.Destination, nextHop.Callsign,
            string.Join(',', downstream), path.LastSeenAt);

        return new RouteDecision.NextHop(
            RouteBuilder.FromNeighbour(nextHop, ctx.DefaultPort),
            SourceRoute: downstream);
    }

    /// <summary>Build the list of neighbours to re-flood to,
    /// excluding the neighbour each callsign in <paramref name="traversed"/>
    /// (the hops so far, and for a flood in transit its originator)
    /// resolves to, so the message doesn't loop back to a node it's
    /// already visited. A hop is the exact call+SSID of a node that
    /// passed the flood on, so another SSID of its callsign still gets
    /// it (#242); a neighbour row entered without the right SSID stands
    /// in for it, as in routing. Local callsign is also implicitly
    /// excluded - the local node never appears in neighbours.</summary>
    private static async Task<IReadOnlyList<BackhaulRoute>> BuildFloodRoutesAsync(
        IRoutingContext ctx, IReadOnlyList<string> traversed, CancellationToken ct)
    {
        var neighbours = await ctx.GetNeighboursAsync(ct);
        var visited = traversed
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => CallsignMatch.Best(neighbours, n => n.Callsign, h, ctx.LocalCallsign))
            .OfType<DbNeighbour>()
            .ToHashSet();
        return neighbours
            .Where(n => !visited.Contains(n))
            .Select(n => RouteBuilder.FromNeighbour(n, ctx.DefaultPort))
            .ToList();
    }
}
