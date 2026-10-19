using dapps.core.Services;
using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// The original Plan B4 resolver, lifted out of
/// <see cref="dapps.core.Services.OutboundMessageManager"/> verbatim.
/// Precedence (first match wins):
///
/// <list type="number">
/// <item>Manual <see cref="DbNeighbour"/> for the destination's call+SSID,
///   or failing that one under its base callsign - explicit operator
///   override.</item>
/// <item>Fresh <see cref="DbDiscoveredPeer"/> rows for that station (or
///   its base callsign), sorted by <c>CostHint</c> ascending then
///   <c>Hops</c> ascending - pick the cheapest fresh channel the peer's
///   been heard on.</item>
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
    public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct)
        => ResolveAsync(message, ctx, cameFrom: null, ct);

    /// <summary>
    /// As <see cref="ResolveAsync(DbMessage, IRoutingContext, CancellationToken)"/>,
    /// passing over the neighbour a message passing through came from,
    /// <paramref name="cameFrom"/> (see <see cref="SplitHorizon.Avoids"/>),
    /// on every tier, so a lower tier gets its turn (#297).
    /// </summary>
    internal async Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, string? cameFrom, CancellationToken ct)
    {
        var allNeighbours = await ctx.GetNeighboursAsync(ct);
        var Avoided = SplitHorizon.Avoids(cameFrom, allNeighbours);
        var neighbours = allNeighbours.Where(n => !Avoided(n.Callsign)).ToList();
        var peers = await ctx.GetDiscoveredPeersAsync(ct);

        // Destinations are `app@call[-ssid]`. A neighbour with the exact
        // call+SSID wins; failing that, SSID mismatches between configured
        // route and destination are tolerated.
        var destCall = DestinationParser.StationOf(message.Destination);

        // 1. Manual operator-configured neighbour wins. Lets a sysop
        //    pin a specific route even if discovery would suggest a
        //    different (and possibly cheaper) channel.
        var manual = CallsignMatch.Best(neighbours, n => n.Callsign, destCall, ctx.LocalCallsign);
        if (manual is not null)
        {
            return new RouteDecision.NextHop(RouteBuilder.FromNeighbour(manual, ctx.DefaultPort));
        }

        // 2. Discovered peers, freshness-filtered, ordered by cost.
        //    The exact call+SSID first, then the cheapest fresh channel.
        var now = DateTime.UtcNow;
        var freshPeer = peers
            .Where(p => DestinationParser.SameStation(p.Callsign, destCall)
                || CallsignMatch.StandsIn(p.Callsign, destCall, ctx.LocalCallsign))
            .Where(p => (now - p.LastSeen).TotalSeconds <= p.TtlSeconds)
            .Where(p => p.PortPending != true) // heard before an upgrade, not converted yet (#370)
            .Where(p => !Avoided(p.Callsign))
            .OrderBy(p => DestinationParser.SameStation(p.Callsign, destCall) ? 0 : 1)
            .ThenBy(p => p.CostHint)
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
                BearerPort: freshPeer.BearerPort ?? ctx.DefaultPort,
                PortHeld: freshPeer.BearerPort is null && ctx.DefaultPort < 1 && freshPeer.UdpEndpoint is null && freshPeer.MeshCoreChannel is null,
                UdpEndpoint: freshPeer.UdpEndpoint,
                MeshCoreChannel: freshPeer.MeshCoreChannel));
        }

        // 3. Hand-maintained route hint. The fallback for "I know peer
        //    X is reachable via my neighbour Y" without a discovery
        //    record. Phase B5 (flood-and-learn) may eventually obsolete
        //    this, but it stays useful for explicit operator overrides.
        //    A hint for the exact call+SSID beats one written without an
        //    SSID, which covers every SSID of its callsign.
        var hintNeighbour = await ctx.ResolveRouteHintAsync(destCall, ct);
        if (hintNeighbour is not null && !Avoided(hintNeighbour.Callsign))
        {
            logger.LogInformation("Routing {0} for {1} via route-hint next-hop {2}",
                message.Id, message.Destination, hintNeighbour.Callsign);
            return new RouteDecision.NextHop(RouteBuilder.FromNeighbour(hintNeighbour, ctx.DefaultPort));
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
