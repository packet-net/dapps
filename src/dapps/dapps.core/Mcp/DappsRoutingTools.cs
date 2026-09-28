using System.ComponentModel;
using dapps.core.Models;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-A - read-only tools covering the routing-state surface:
/// learned routes (B5 passive learning), discovered paths (B5.1
/// MeshCore-flavoured DSR), manual route hints, plus per-callsign
/// probe (B6.1) and poll (F3b) state. Composed by the diagnostic
/// tools in PR-C to answer "why is X reachable / unreachable?".
/// </summary>
[McpServerToolType]
public sealed class DappsRoutingTools(Database database)
{
    [McpServerTool(Name = "list_learned_routes")]
    [Description(
        "Per-destination next-hop routes learned passively from inbound F1 src= traffic (B5 PR-B). One row per " +
        "DestinationBaseCallsign with NextHopCallsign + LastSeenAt + ConsecutiveFailures. Three failed forwards " +
        "invalidate; success resets the counter. The default (passive-flood) routing algorithm consults this " +
        "table first and falls back to bounded flood when no learned route exists.")]
    public async Task<IReadOnlyList<DbLearnedRoute>> ListLearnedRoutesAsync()
        => await database.GetLearnedRoutesAsync();

    [McpServerTool(Name = "list_discovered_paths")]
    [Description(
        "Source-routed paths discovered by the MeshCore-like algorithm (B5.1 - selectable via " +
        "SystemOptions.RoutingAlgorithm = 'meshcore'). Stores the full ordered intermediate-hop list rather than " +
        "just the next hop. Empty on a passive-flood-only deployment.")]
    public async Task<IReadOnlyList<DbDiscoveredPath>> ListDiscoveredPathsAsync()
        => await database.GetDiscoveredPathsAsync();

    [McpServerTool(Name = "list_route_hints")]
    [Description(
        "Manual route-hint overrides - operator-configured 'I know X is reachable via Y' entries. Consulted " +
        "before learned routes and discovered peers when resolving a forward path. Usually empty in cold-start " +
        "deployments since B5 passive learning covers most cases automatically.")]
    public async Task<IReadOnlyList<DbRouteHint>> ListRouteHintsAsync()
        => await database.GetRouteHintsAsync();

    [McpServerTool(Name = "list_probed_nodes")]
    [Description(
        "Per-callsign B6.1 connected-mode probe state. One row per callsign we've ever probed: LastProbedAt, " +
        "LastSuccessAt, LastError, ConsecutiveFailures, SuccessCount, the bearer port we used, OptOut flag (operator " +
        "asked us to stop probing this peer), Source ('neighbour' for direct rows or 'via:CALLSIGN' for transitive " +
        "candidates from a remote's peers list). Probes only fire when SystemOptions.ProbingEnabled is true.")]
    public async Task<IReadOnlyList<DbProbedNode>> ListProbedNodesAsync()
        => await database.GetProbedNodes();

    [McpServerTool(Name = "list_polled_nodes")]
    [Description(
        "Per-callsign F3b scheduled-poll state. One row per callsign the daemon has polled (or been asked to). " +
        "LastPolledAt, LastSuccessAt, LastError, ConsecutiveFailures, MessagesDrained (cumulative count drained " +
        "from this peer), OptOut flag. Polls only fire when SystemOptions.ScheduledPollEnabled is true; mail " +
        "also comes back on every session dialled for other traffic, regardless.")]
    public async Task<IReadOnlyList<DbPolledNode>> ListPolledNodesAsync()
        => await database.GetPolledNodes();
}
