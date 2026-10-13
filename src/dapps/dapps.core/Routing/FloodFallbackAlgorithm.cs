using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// Bounded-flood fallback for cold-start routing - the second half
/// of B5. When the inner algorithm has no route and the message
/// hasn't yet been flooded, send to every direct neighbour with a
/// hop budget. Each forwarding hop decrements before re-flooding;
/// per-(message, link-source) deduplication via <see cref="DbFloodSeen"/>
/// stops the flood from looping back.
///
/// Layered as a decorator over the next inner algorithm (typically
/// <see cref="PassiveLearningAlgorithm"/>): if the inner algorithm
/// can resolve the destination, we use that; the flood is the
/// last-resort fallback. Floods that succeed in reaching the
/// destination cause the receiver to send replies back via passive
/// learning (which observed the inbound flood and learned reverse
/// routes) - so floods diminish as the network warms up.
///
/// In-flight flood messages: when a message arrives at this node
/// already in flood mode (<see cref="DbMessage.FloodHopsRemaining"/>
/// is set), <see cref="ResolveAsync"/> handles the propagation
/// regardless of what the inner algorithm thinks - the flood IS the
/// route, and we just need to spread it without circling back to the
/// link source.
/// </summary>
public sealed class FloodFallbackAlgorithm(
    IRoutingAlgorithm inner,
    ILogger<FloodFallbackAlgorithm> logger) : IRoutingAlgorithm
{
    /// <summary>Default hop budget for a fresh flood. 4 covers the
    /// 6-node simulator's longest path (A→B→C→E→F = 4 hops) with no
    /// slack; production meshes likely want 6-8. Kept conservative
    /// to bound the impact of a misconfigured node.</summary>
    public const byte DefaultHopBudget = 6;

    public async Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
    {
        // In-flight flood: this node is mid-propagating. Continue the
        // flood regardless of what inner says - the flood mechanism
        // owns the routing decision once a message is in flood mode.
        if (message.FloodHopsRemaining is { } remaining)
        {
            if (remaining == 0)
            {
                // Hop budget exhausted at this node. Don't re-flood;
                // if the destination is local the inbox already
                // delivered, and otherwise the message dies here.
                logger.LogDebug("Flood for {0} dropped at this node: hop budget exhausted", message.Id);
                return new RouteDecision.Unreachable();
            }

            var routes = await BuildFloodRoutesAsync(ctx, [message.SourceCallsign, message.OriginatorCallsign], ct);
            if (routes.Count == 0)
            {
                logger.LogDebug("Flood for {0} dropped at this node: no neighbours to re-flood to", message.Id);
                return new RouteDecision.Unreachable();
            }

            return new RouteDecision.FloodToNeighbours(routes, (byte)(remaining - 1));
        }

        // Regular routing path: try inner first; flood only as last
        // resort. The inner-then-flood ordering matters - if learning
        // has produced a route, use it (cheaper than a flood); only
        // fall back to flood when there's genuinely no other way.
        var innerDecision = await inner.ResolveAsync(message, ctx, ct);
        if (innerDecision is not RouteDecision.Unreachable)
        {
            return innerDecision;
        }

        var floodRoutes = await BuildFloodRoutesAsync(ctx, [], ct);
        if (floodRoutes.Count == 0)
        {
            logger.LogWarning("No route AND no neighbours to flood for {0}; leaving in queue", message.Id);
            return new RouteDecision.Unreachable();
        }

        logger.LogInformation(
            "No route for {0}; initiating bounded flood to {1} neighbour(s) with hop budget {2}",
            message.Id, floodRoutes.Count, DefaultHopBudget);
        return new RouteDecision.FloodToNeighbours(floodRoutes, DefaultHopBudget);
    }

    public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct)
        => inner.ObserveInboundAsync(message, linkSourceCallsign, ctx, ct);

    public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct)
        => inner.ObserveForwardOutcomeAsync(message, attemptedRoute, result, ctx, ct);

    public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct)
        => inner.ObserveProbeOutcomeAsync(askedPeerCallsign, peers, ctx, ct);

    public Task RunAsync(IRoutingContext ctx, CancellationToken ct)
        => inner.RunAsync(ctx, ct);

    /// <summary>Build the list of flood routes from the local
    /// neighbours table, skipping the neighbours a flood came from: the
    /// link source, so it doesn't bounce straight back upstream, and the
    /// originator. Each is the neighbour row the callsign maps to as
    /// routing picks one: the exact call+SSID, so another SSID of the
    /// same callsign still gets the flood (#242), else a row under the
    /// same callsign (one entered with another SSID, or a caller that
    /// came through a BPQ node, which inverts the SSID). The originator
    /// counts on its own because that inversion can make the link
    /// source our own callsign, which maps to no row at all.</summary>
    private static async Task<IReadOnlyList<BackhaulRoute>> BuildFloodRoutesAsync(
        IRoutingContext ctx, IReadOnlyList<string> excludeCallsigns, CancellationToken ct)
    {
        var neighbours = await ctx.GetNeighboursAsync(ct);
        var upstream = excludeCallsigns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => CallsignMatch.Best(neighbours, n => n.Callsign, c, ctx.LocalCallsign))
            .OfType<DbNeighbour>()
            .ToHashSet();
        return neighbours
            .Where(n => !upstream.Contains(n))
            .Select(n => RouteBuilder.FromNeighbour(n, ctx.DefaultBearerPort))
            .ToList();
    }
}
