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

    public async Task<DbNeighbour?> ResolveRouteHintAsync(string destinationCallsign, CancellationToken ct)
    {
        var hint = await database.GetRouteHintFor(destinationCallsign);
        if (hint is null) return null;
        return await database.GetNeighbour(hint.NextHop);
    }

    public async Task<DbNeighbour?> GetNeighbourByCallsignAsync(string callsign, CancellationToken ct)
        => await database.GetNeighbour(callsign);

    public async Task UpsertLearnedRouteAsync(string destinationCallsign, string nextHopCallsign, CancellationToken ct)
    {
        // Pre-check whether the row is a refresh or a real change. Skip
        // the metrics event on refreshes - every successful inbound
        // F1-stamped message would otherwise emit `route.learned`,
        // drowning the journal in noise. Two queries on the learning
        // hot path is fine (it's per-inbound-message at most, not per-
        // tick).
        var existing = await database.GetLearnedRouteAsync(destinationCallsign);
        var significant = existing is null
            || !string.Equals(existing.NextHopCallsign, nextHopCallsign, StringComparison.OrdinalIgnoreCase);
        await database.UpsertLearnedRouteAsync(destinationCallsign, nextHopCallsign, DateTime.UtcNow);
        if (significant) metrics?.RecordRouteLearned(destinationCallsign, nextHopCallsign);
    }

    public Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationCallsign, CancellationToken ct)
        => database.GetLearnedRouteAsync(destinationCallsign);

    public Task RecordLearnedRouteSuccessAsync(string destinationCallsign, CancellationToken ct)
        => database.RecordLearnedRouteSuccessAsync(destinationCallsign, DateTime.UtcNow);

    public Task<int> RecordLearnedRouteFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordLearnedRouteFailureAsync(destinationCallsign, invalidationThreshold);

    public Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.HasSeenFloodAsync(messageId, linkSourceCallsign);

    public Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.RecordFloodSeenAsync(messageId, linkSourceCallsign, DateTime.UtcNow);

    public async Task UpsertDiscoveredPathAsync(string destinationCallsign, IReadOnlyList<string> intermediates, CancellationToken ct)
    {
        // Same diff-then-record shape as UpsertLearnedRouteAsync - only
        // emit `route.learned` on a new path or a path-shape change,
        // not on every refresh tick.
        var existing = await database.GetDiscoveredPathAsync(destinationCallsign);
        var newCsv = Models.DbDiscoveredPath.ToCsv(intermediates);
        var significant = existing is null
            || !string.Equals(existing.IntermediatesCsv, newCsv, StringComparison.OrdinalIgnoreCase);
        await database.UpsertDiscoveredPathAsync(destinationCallsign, intermediates, DateTime.UtcNow);
        if (significant)
        {
            var summary = intermediates.Count == 0 ? "(direct)" : string.Join("->", intermediates);
            metrics?.RecordRouteLearned(destinationCallsign, summary);
        }
    }

    public Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationCallsign, CancellationToken ct)
        => database.GetDiscoveredPathAsync(destinationCallsign);

    public Task RecordDiscoveredPathSuccessAsync(string destinationCallsign, CancellationToken ct)
        => database.RecordDiscoveredPathSuccessAsync(destinationCallsign, DateTime.UtcNow);

    public Task<int> RecordDiscoveredPathFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordDiscoveredPathFailureAsync(destinationCallsign, invalidationThreshold);
}
