using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// The original Plan B4 resolver, lifted out of
/// <see cref="dapps.core.Services.OutboundMessageManager"/> verbatim.
/// Precedence (first match wins):
///
/// <list type="number">
/// <item>Manual <see cref="DbNeighbour"/> with matching base callsign
///   - explicit operator override.</item>
/// <item>Fresh <see cref="DbDiscoveredPeer"/> rows for that base
///   callsign, sorted by <c>CostHint</c> ascending then <c>Hops</c>
///   ascending - pick the cheapest fresh channel the peer's been
///   heard on.</item>
/// <item>Hand-maintained <see cref="DbRouteHint"/> next-hop fallback.</item>
/// <item>None of the above → <see cref="RouteDecision.Unreachable"/>.</item>
/// </list>
///
/// Purely reactive: no observation hooks, no background loop. The
/// default algorithm at boot; replaced or composed-with by other
/// algorithms (passive-learning, AODV-flood) once those land.
/// </summary>
public sealed class StaticRoutingAlgorithm(ILogger<StaticRoutingAlgorithm> logger) : IRoutingAlgorithm
{
    public async Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
    {
        var neighbours = await ctx.GetNeighboursAsync(ct);
        var peers = await ctx.GetDiscoveredPeersAsync(ct);

        // Destinations are `app@call[-ssid]`. Compare on the base
        // callsign - SSID mismatches between configured route and
        // destination are tolerated.
        var destBaseCall = message.Destination.Split('@').Last().Split('-')[0];

        // 1. Manual operator-configured neighbour wins. Lets a sysop
        //    pin a specific route even if discovery would suggest a
        //    different (and possibly cheaper) channel.
        var manual = neighbours.FirstOrDefault(
            n => n.Callsign.Split('-')[0].Equals(destBaseCall, StringComparison.OrdinalIgnoreCase));
        if (manual is not null)
        {
            return new RouteDecision.NextHop(RouteBuilder.FromNeighbour(manual, ctx.DefaultBearerPort));
        }

        // 2. Discovered peers, freshness-filtered, ordered by cost.
        //    The cheapest fresh channel wins.
        var now = DateTime.UtcNow;
        var freshPeer = peers
            .Where(p => p.Callsign.Split('-')[0].Equals(destBaseCall, StringComparison.OrdinalIgnoreCase))
            .Where(p => (now - p.LastSeen).TotalSeconds <= p.TtlSeconds)
            .OrderBy(p => p.CostHint)
            .ThenBy(p => p.Hops)
            .FirstOrDefault();
        if (freshPeer is not null)
        {
            logger.LogInformation(
                "Routing {0} to {1} via discovered peer on {2}/{3} (cost={4}, hops={5})",
                message.Id, message.Destination,
                freshPeer.Bearer, freshPeer.ChannelKey,
                freshPeer.CostHint, freshPeer.Hops);
            return new RouteDecision.NextHop(new BackhaulRoute(
                freshPeer.Callsign,
                BearerPort: freshPeer.BearerPort ?? ctx.DefaultBearerPort,
                UdpEndpoint: freshPeer.UdpEndpoint,
                MeshCoreChannel: freshPeer.MeshCoreChannel));
        }

        // 3. Hand-maintained route hint. The fallback for "I know peer
        //    X is reachable via my neighbour Y" without a discovery
        //    record. Phase B5 (flood-and-learn) may eventually obsolete
        //    this, but it stays useful for explicit operator overrides.
        var hintNeighbour = await ctx.ResolveRouteHintAsync(destBaseCall, ct);
        if (hintNeighbour is not null)
        {
            logger.LogInformation("Routing {0} for {1} via route-hint next-hop {2}",
                message.Id, message.Destination, hintNeighbour.Callsign);
            return new RouteDecision.NextHop(RouteBuilder.FromNeighbour(hintNeighbour, ctx.DefaultBearerPort));
        }

        return new RouteDecision.Unreachable();
    }

    public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public Task RunAsync(IRoutingContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}
