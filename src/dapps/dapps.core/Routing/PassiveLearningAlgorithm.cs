using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// AODV-flavoured passive routing - but without explicit RREQ/RREP
/// control messages. Every inbound message that carries an F1
/// originator different from the link source teaches the receiver
/// "to reach <c>originator</c>, send via the neighbour
/// <c>linkSource</c>." Bidirectional traffic auto-converges in one
/// round-trip, no announcements, no flooding (PR-C handles cold-start).
///
/// Wraps <see cref="StaticRoutingAlgorithm"/> as a decorator: the
/// static precedence (manual neighbour / discovered peer / route
/// hint) ALWAYS wins. Learned routes are consulted only when static
/// would have returned <see cref="RouteDecision.Unreachable"/>. This
/// preserves the operator's ability to override learned routes by
/// adding a manual neighbour or hint, without learned data slipping
/// in front of explicit configuration.
///
/// Failure handling: each failed forward against a learned route
/// increments <see cref="DbLearnedRoute.ConsecutiveFailures"/>; once
/// it hits <see cref="InvalidationThreshold"/> the row is deleted
/// and the next attempt falls through to whatever else is available
/// (other static sources, or PR-C's bounded flood).
/// </summary>
public sealed class PassiveLearningAlgorithm(
    StaticRoutingAlgorithm staticAlgorithm,
    ILogger<PassiveLearningAlgorithm> logger) : IRoutingAlgorithm
{
    /// <summary>Forward failures before a learned route is invalidated.
    /// 3 is enough to ride out a single transient (a station briefly
    /// off-air) without prematurely throwing away a working path.</summary>
    public const int InvalidationThreshold = 3;

    public async Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
    {
        var staticDecision = await staticAlgorithm.ResolveAsync(message, ctx, ct);
        if (staticDecision is not RouteDecision.Unreachable)
        {
            return staticDecision;
        }

        // Static gave up - try learned routes.
        var destBaseCall = message.Destination.Split('@').Last().Split('-')[0];
        var learned = await ctx.GetLearnedRouteAsync(destBaseCall, ct);
        if (learned is null) return new RouteDecision.Unreachable();

        // Resolve the next-hop callsign back to the actual neighbour
        // row so we get the bearer hint (bearer port / UDP endpoint).
        // If the neighbour has gone away (e.g. operator removed it
        // while a learned route still references it), drop the
        // learned route and report Unreachable - re-learning will
        // re-populate when traffic returns from the original source.
        var nextHop = await ctx.GetNeighbourByCallsignAsync(learned.NextHopCallsign, ct);
        if (nextHop is null)
        {
            logger.LogInformation(
                "Learned route for {0} -> {1} discarded: next-hop neighbour no longer exists",
                destBaseCall, learned.NextHopCallsign);
            await ctx.RecordLearnedRouteFailureAsync(destBaseCall, invalidationThreshold: 1, ct);
            return new RouteDecision.Unreachable();
        }

        logger.LogInformation(
            "Routing {0} for {1} via learned route -> {2} (last seen {3:o}, failures={4})",
            message.Id, message.Destination, nextHop.Callsign, learned.LastSeenAt, learned.ConsecutiveFailures);

        return new RouteDecision.NextHop(RouteBuilder.FromNeighbour(nextHop, ctx.DefaultBearerPort));
    }

    public async Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct)
    {
        // The contract: <c>To reach the originator, send via the link
        // source.</c> Several reasons to ignore an observation:
        //
        //   - No originator (pre-F1 sender) - the (originator, link)
        //     pair isn't well-defined.
        //   - Originator IS us - message looped back somehow; learning
        //     would tell us "to reach myself, send via the loop"
        //     which is wrong.
        //   - Originator base-callsign IS the link source - single-hop
        //     send; the learned route would just duplicate the
        //     existing direct-neighbour entry. Static resolution
        //     handles direct neighbours already; no need to clutter.
        //   - Link source isn't actually one of our neighbours - we
        //     received from a station we didn't know about. Learning
        //     a route through an unknown next-hop would be unusable
        //     (the resolver re-checks the neighbour exists at use
        //     time, but it's cleaner to ignore at learn time too).
        if (string.IsNullOrEmpty(message.Originator)) return;

        var ourBase = ctx.LocalCallsign.Split('-')[0];
        var origBase = message.Originator.Split('-')[0];
        var linkBase = linkSourceCallsign.Split('-')[0];

        if (string.Equals(origBase, ourBase, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(origBase, linkBase, StringComparison.OrdinalIgnoreCase)) return;

        var nextHopNeighbour = await ctx.GetNeighbourByCallsignAsync(linkSourceCallsign, ct);
        if (nextHopNeighbour is null)
        {
            // The link source isn't in our neighbours table. This
            // shouldn't normally happen - bearers only deliver from
            // configured neighbours - but if it does, skip.
            return;
        }

        await ctx.UpsertLearnedRouteAsync(origBase, linkSourceCallsign, ct);
        logger.LogDebug("Learned route: {0} reachable via {1}", origBase, linkSourceCallsign);
    }

    public async Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct)
    {
        var destBaseCall = message.Destination.Split('@').Last().Split('-')[0];
        var learned = await ctx.GetLearnedRouteAsync(destBaseCall, ct);
        if (learned is null) return;

        // Did this forward actually go via the learned route? If a
        // higher-precedence source (manual neighbour) was used, the
        // outcome doesn't apply to the learned-route entry.
        if (!string.Equals(attemptedRoute.Callsign, learned.NextHopCallsign, StringComparison.OrdinalIgnoreCase))
            return;

        if (result.Accepted)
        {
            await ctx.RecordLearnedRouteSuccessAsync(destBaseCall, ct);
        }
        else
        {
            var newCount = await ctx.RecordLearnedRouteFailureAsync(destBaseCall, InvalidationThreshold, ct);
            if (newCount < 0)
            {
                logger.LogInformation(
                    "Learned route for {0} -> {1} invalidated after {2} consecutive failures",
                    destBaseCall, learned.NextHopCallsign, InvalidationThreshold);
            }
        }
    }

    /// <summary>
    /// B6.1 follow-up: turn a probe's <c>peers</c> response into learned
    /// routes. For each peer Y that the asked peer X returned, teach
    /// "to reach Y, send via X" - same shape as the passive-learning
    /// teach but driven by an active probe rather than inbound traffic.
    ///
    /// Filter set mirrors <see cref="ObserveInboundAsync"/>:
    /// <list type="bullet">
    /// <item>Skip Y == us (the asked peer always reports us as a peer
    ///   since we just talked to them; that's noise).</item>
    /// <item>Skip Y base == X base (single-hop self-loop - we'd just
    ///   duplicate the direct-neighbour entry).</item>
    /// <item>Require X to be a manual neighbour - the resolver re-checks
    ///   the next-hop in <see cref="DbNeighbour"/> at use-time, so a
    ///   learned route through a non-neighbour next-hop is dead weight.</item>
    /// </list>
    /// </summary>
    public async Task ObserveProbeOutcomeAsync(
        string askedPeerCallsign,
        IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers,
        IRoutingContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(askedPeerCallsign) || peers.Count == 0) return;

        var ourBase = ctx.LocalCallsign.Split('-')[0];
        var askedBase = askedPeerCallsign.Split('-')[0];

        // Resolver discards learned routes whose next-hop isn't a
        // neighbour. No point inserting in that case.
        var nextHopNeighbour = await ctx.GetNeighbourByCallsignAsync(askedPeerCallsign, ct);
        if (nextHopNeighbour is null) return;

        foreach (var peer in peers)
        {
            if (string.IsNullOrWhiteSpace(peer.Callsign)) continue;
            var peerBase = peer.Callsign.Split('-')[0];
            if (string.Equals(peerBase, ourBase, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(peerBase, askedBase, StringComparison.OrdinalIgnoreCase)) continue;

            await ctx.UpsertLearnedRouteAsync(peerBase, askedPeerCallsign, ct);
            logger.LogDebug(
                "Learned route from probe peers: {0} reachable via {1}",
                peerBase, askedPeerCallsign);
        }
    }

    public Task RunAsync(IRoutingContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}
