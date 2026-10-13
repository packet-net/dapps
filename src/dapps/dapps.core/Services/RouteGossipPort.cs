using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Implementation of <see cref="IRouteGossipPort"/> that drives the
/// SQLite-backed gossip state tables. Plumbs into
/// <see cref="Dappsv1SessionBackhaul"/> via the constructor seam so
/// the session-level code stays free of database concerns.
/// </summary>
public sealed class RouteGossipPort(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<RouteGossipPort> logger) : IRouteGossipPort
{
    public async Task<bool> ShouldPullAsync(string remoteCallsign, CancellationToken ct)
    {
        var hours = options.CurrentValue.RouteGossipStalenessHours;
        if (hours <= 0) return false;
        var local = options.CurrentValue.Callsign;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await database.ShouldPullRouteGossipAsync(local, remoteCallsign, hours, now);
    }

    public async Task ImportAsync(string advertiserCallsign, IReadOnlyList<DappsProtocolClient.GossipedRoute> routes, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var imported = 0;
        foreach (var r in routes)
        {
            try
            {
                await database.UpsertGossipedRouteAsync(r.DestinationCallsign, advertiserCallsign, now);
                imported++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gossip import failed for {0} via {1}", r.DestinationCallsign, advertiserCallsign);
            }
        }
        if (imported > 0)
        {
            logger.LogInformation("Imported {0} gossiped route(s) from {1}", imported, advertiserCallsign);
        }
    }

    public async Task RecordPulledAsync(string remoteCallsign, CancellationToken ct)
    {
        var local = options.CurrentValue.Callsign;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.MarkRouteGossipPulledAsync(local, remoteCallsign, now);
    }
}
