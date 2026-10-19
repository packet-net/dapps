using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// Read access to the routing-relevant slice of node state, plus the
/// "what is my callsign" identity. Passed to every
/// <see cref="IRoutingAlgorithm"/> hook so the algorithm can consult
/// the things the static resolver consults today (neighbours,
/// discovered peers, route hints) without taking a direct dependency
/// on <see cref="dapps.core.Services.Database"/>.
///
/// Future algorithms will need additional surface: writing learned
/// routes (<see cref="dapps.core.Routing.PassiveLearningAlgorithm"/>
/// in PR-B), reading flood-dedup state, injecting messages via the
/// backhauls (active discovery / B6 probing). Methods get added as
/// concrete algorithms need them - kept narrow on purpose so each
/// addition is justified by a working implementation.
/// </summary>
public interface IRoutingContext
{
    /// <summary>This node's local callsign with SSID
    /// (<c>SystemOptions.Callsign</c>).</summary>
    string LocalCallsign { get; }

    /// <summary>Default bearer port (0-indexed) for AGW routes when
    /// no per-neighbour override is set.</summary>
    int DefaultPort { get; }

    Task<IReadOnlyList<DbNeighbour>> GetNeighboursAsync(CancellationToken ct);

    Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeersAsync(CancellationToken ct);

    /// <summary>Look up a manual route hint for the given destination
    /// callsign, SSID included: a hint for that exact station, else one
    /// written without an SSID for its callsign, else the wildcard
    /// <c>*</c>. Returns the next-hop neighbour (already resolved) or
    /// null if no hint applies.</summary>
    Task<DbNeighbour?> ResolveRouteHintAsync(string destinationCallsign, CancellationToken ct);

    /// <summary>Look up the neighbour row for a callsign - used by
    /// passive-learning to resolve a learned-route's next-hop into a
    /// concrete bearer hint.</summary>
    Task<DbNeighbour?> GetNeighbourByCallsignAsync(string callsign, CancellationToken ct);

    // ── Passive-learning state (PR-B) ──────────────────────────────
    //
    // Learned routes and discovered paths are keyed by the destination's
    // full callsign, SSID included (#242); the store normalises case and
    // a "-0", so callers pass the callsign as they have it.
    //
    // Algorithms that need to PERSIST learned facts (not just read
    // existing ones) call these. Kept on the context interface rather
    // than handing the algorithm a Database directly: this way an
    // in-memory test context can stub the learning surface without
    // standing up SQLite.

    Task UpsertLearnedRouteAsync(string destinationCallsign, string nextHopCallsign, CancellationToken ct);

    Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationCallsign, CancellationToken ct);

    Task RecordLearnedRouteSuccessAsync(string destinationCallsign, CancellationToken ct);

    /// <summary>Returns the new failure count, or -1 if the row was
    /// deleted (invalidation threshold hit).</summary>
    Task<int> RecordLearnedRouteFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct);

    // ── Flood-deduplication state (PR-C) ──────────────────────────

    Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign, CancellationToken ct);

    Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, CancellationToken ct);

    // ── Discovered paths (MeshCore-flavoured algorithm) ───────────

    Task UpsertDiscoveredPathAsync(string destinationCallsign, IReadOnlyList<string> intermediates, CancellationToken ct);

    Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationCallsign, CancellationToken ct);

    Task RecordDiscoveredPathSuccessAsync(string destinationCallsign, CancellationToken ct);

    /// <summary>Returns the new failure count, or -1 if the row was
    /// deleted (invalidation threshold hit).</summary>
    Task<int> RecordDiscoveredPathFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct);
}
