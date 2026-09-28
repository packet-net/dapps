using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;

namespace dapps.core.Routing;

/// <summary>
/// <see cref="IRoutingContext"/> backed by the SQLite-resident node
/// state. The default and only context implementation today;
/// alternative contexts (in-memory / test stubs) plug in here for
/// algorithms that want to be exercised without a database.
/// </summary>
public sealed class DatabaseRoutingContext(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics? metrics = null) : IRoutingContext
{
    public string LocalCallsign => options.CurrentValue.Callsign;

    public int DefaultBearerPort => options.CurrentValue.DefaultBearerPort;

    public async Task<IReadOnlyList<DbNeighbour>> GetNeighboursAsync(CancellationToken ct)
        => (await database.GetNeighbours()).ToList();

    public async Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeersAsync(CancellationToken ct)
        => await database.GetDiscoveredPeers();

    public async Task<DbNeighbour?> ResolveRouteHintAsync(string destinationBaseCallsign, CancellationToken ct)
    {
        var hint = await database.GetRouteHint(destinationBaseCallsign)
            ?? await database.GetRouteHint("*");
        if (hint is null) return null;
        return await database.GetNeighbour(hint.NextHop);
    }

    public async Task<DbNeighbour?> GetNeighbourByCallsignAsync(string callsign, CancellationToken ct)
        => await database.GetNeighbour(callsign);

    public async Task UpsertLearnedRouteAsync(string destinationBaseCallsign, string nextHopCallsign, CancellationToken ct)
    {
        // Pre-check whether the row is a refresh or a real change. Skip
        // the metrics event on refreshes - every successful inbound
        // F1-stamped message would otherwise emit `route.learned`,
        // drowning the journal in noise. Two queries on the learning
        // hot path is fine (it's per-inbound-message at most, not per-
        // tick).
        var existing = await database.GetLearnedRouteAsync(destinationBaseCallsign);
        var significant = existing is null
            || !string.Equals(existing.NextHopCallsign, nextHopCallsign, StringComparison.OrdinalIgnoreCase);
        await database.UpsertLearnedRouteAsync(destinationBaseCallsign, nextHopCallsign, DateTime.UtcNow);
        if (significant) metrics?.RecordRouteLearned(destinationBaseCallsign, nextHopCallsign);
    }

    public Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.GetLearnedRouteAsync(destinationBaseCallsign);

    public Task RecordLearnedRouteSuccessAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.RecordLearnedRouteSuccessAsync(destinationBaseCallsign, DateTime.UtcNow);

    public Task<int> RecordLearnedRouteFailureAsync(string destinationBaseCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordLearnedRouteFailureAsync(destinationBaseCallsign, invalidationThreshold);

    public Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.HasSeenFloodAsync(messageId, linkSourceCallsign);

    public Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.RecordFloodSeenAsync(messageId, linkSourceCallsign, DateTime.UtcNow);

    public async Task UpsertDiscoveredPathAsync(string destinationBaseCallsign, IReadOnlyList<string> intermediates, CancellationToken ct)
    {
        // Same diff-then-record shape as UpsertLearnedRouteAsync - only
        // emit `route.learned` on a new path or a path-shape change,
        // not on every refresh tick.
        var existing = await database.GetDiscoveredPathAsync(destinationBaseCallsign);
        var newCsv = Models.DbDiscoveredPath.ToCsv(intermediates);
        var significant = existing is null
            || !string.Equals(existing.IntermediatesCsv, newCsv, StringComparison.OrdinalIgnoreCase);
        await database.UpsertDiscoveredPathAsync(destinationBaseCallsign, intermediates, DateTime.UtcNow);
        if (significant)
        {
            var summary = intermediates.Count == 0 ? "(direct)" : string.Join("->", intermediates);
            metrics?.RecordRouteLearned(destinationBaseCallsign, summary);
        }
    }

    public Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.GetDiscoveredPathAsync(destinationBaseCallsign);

    public Task RecordDiscoveredPathSuccessAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.RecordDiscoveredPathSuccessAsync(destinationBaseCallsign, DateTime.UtcNow);

    public Task<int> RecordDiscoveredPathFailureAsync(string destinationBaseCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordDiscoveredPathFailureAsync(destinationBaseCallsign, invalidationThreshold);
}
