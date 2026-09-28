using System.ComponentModel;
using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-D - supervised exploration. The LLM can ask "I want to
/// reach X but can't - what should I try?" and "what does neighbour
/// Y currently know?". Both are composites over PR-A reads + PR-B
/// actions; the agent proposes, the operator decides whether to act
/// on the proposal.
///
/// Distinct from autonomous routing-participation: the routing
/// algorithm stays deterministic. These tools surface *options*, not
/// decisions made on the operator's behalf. Routing change is still
/// achieved by the operator running the recommended action tool, not
/// by the agent silently mutating route state.
/// </summary>
[McpServerToolType]
public sealed class DappsExplorationTools(
    Database database,
    ProbeSchedulerService probeScheduler,
    IOptionsMonitor<SystemOptions> options)
{
    [McpServerTool(Name = "explore_via_neighbour")]
    [Description(
        "Probe a configured neighbour and return their known peers, annotated for what's NEW (we haven't " +
        "heard them via discovery or configured them ourselves) vs. KNOWN (we already track them). Useful " +
        "when answering 'this peer is well-connected - what could they teach us?'. Same wire as run_probe " +
        "but the response highlights the gap analysis instead of just the raw peer list.")]
    public async Task<NeighbourExploration> ExploreViaNeighbourAsync(
        [Description("Neighbour callsign to probe. Must be a configured manual neighbour with an AGW path (UDP-only neighbours can't run the peers exchange).")]
        string neighbour,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(neighbour))
            throw new ArgumentException("neighbour is required", nameof(neighbour));
        var normalized = neighbour.Trim().ToUpperInvariant();
        var neighbourRow = await database.GetNeighbour(normalized);
        if (neighbourRow is null)
        {
            throw new InvalidOperationException(
                $"{normalized} is not a configured neighbour. Add a /Neighbours row first.");
        }
        if (neighbourRow.UdpEndpoint is not null && neighbourRow.BearerPort is null)
        {
            throw new InvalidOperationException(
                $"{normalized} has only a UDP endpoint - the peers exchange is AGW-session only.");
        }

        var port = neighbourRow.BearerPort ?? options.CurrentValue.DefaultBearerPort;
        var (_, result) = await probeScheduler.ProbeAndRecordVerboseAsync(
            options.CurrentValue.Callsign, normalized, port, ct, fetchPeers: true,
            reason: "operator-triggered explore_via_neighbour (MCP)");

        // Build the "what we already know" set: our own callsign +
        // every manual neighbour + every discovered peer + every
        // probed node we already track.
        var ourBase = options.CurrentValue.Callsign.Split('-')[0];
        var allNeighbours = (await database.GetNeighbours()).Select(n => n.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allPeers = (await database.GetDiscoveredPeers()).Select(p => p.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allProbed = (await database.GetProbedNodes()).Select(p => p.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsKnown(string c) =>
            allNeighbours.Contains(c) || allPeers.Contains(c) || allProbed.Contains(c) ||
            c.StartsWith(ourBase, StringComparison.OrdinalIgnoreCase);

        var annotated = result.DiscoveredPeers
            .Select(p => new AnnotatedPeer(
                Callsign: p.Callsign,
                BearerPort: p.BearerPort,
                Status: IsKnown(p.Callsign) ? "known" : "new"))
            .ToList();

        var newCount = annotated.Count(a => a.Status == "new");
        var summary = result.Success
            ? $"{normalized}: probed via AGW port {port}, fetched {annotated.Count} peer(s), " +
              $"{newCount} previously unknown to this node."
            : $"{normalized}: probe failed - {result.Error}";

        return new NeighbourExploration(
            Neighbour: normalized,
            ProbeSuccess: result.Success,
            ProbeError: result.Error,
            BearerPort: port,
            Peers: annotated,
            NewPeerCount: newCount,
            Summary: summary);
    }

    [McpServerTool(Name = "opinion_on_route")]
    [Description(
        "Recommend a route for a target the operator wants to reach. Composes find_path_to (current local " +
        "resolution) with transitive 'via:CALLSIGN' candidates from B6.1 Phase 2 discovery, plus a freshness " +
        "verdict on each option. Returns ranked candidates with rationale + a recommended next action " +
        "(e.g. 'try run_probe(X) first; if that succeeds, run_probe(target) via X'). The agent proposes - " +
        "the operator runs the recommended action tool to actually act on the suggestion.")]
    public async Task<RouteOpinion> OpinionOnRouteAsync(
        [Description("Destination callsign. The base callsign without SSID is what gets resolved.")]
        string destination)
    {
        var destBase = destination.Trim().Split('-')[0].ToUpperInvariant();

        var candidates = new List<RouteCandidate>();

        // Direct surfaces first - these are the same the routing
        // algorithm would consult right now.
        var hints = await database.GetRouteHintsAsync();
        var hint = hints.FirstOrDefault(h => string.Equals(h.Destination, destBase, StringComparison.OrdinalIgnoreCase));
        if (hint is not null)
        {
            candidates.Add(new RouteCandidate(
                Source: "route_hint",
                NextHop: hint.NextHop,
                Confidence: "high",
                Rationale: $"Manual operator-set route hint to {hint.NextHop}.",
                RecommendedAction: $"send_test_message would already use this - no extra step needed."));
        }

        var learned = await database.GetLearnedRouteAsync(destBase);
        if (learned is not null)
        {
            var ageMin = (int)Math.Max(0, (DateTime.UtcNow - learned.LastSeenAt).TotalMinutes);
            var conf = learned.ConsecutiveFailures == 0 ? "high"
                : learned.ConsecutiveFailures < 3 ? "medium"
                : "low";
            candidates.Add(new RouteCandidate(
                Source: "learned_route",
                NextHop: learned.NextHopCallsign,
                Confidence: conf,
                Rationale: $"Passively learned next-hop {learned.NextHopCallsign}, last fresh {ageMin}m ago, {learned.ConsecutiveFailures} consecutive failure(s).",
                RecommendedAction: "send_test_message; the routing layer will consult this learned route."));
        }

        var path = await database.GetDiscoveredPathAsync(destBase);
        if (path is not null && !string.IsNullOrEmpty(path.IntermediatesCsv))
        {
            var hops = path.IntermediatesCsv.Split(',');
            candidates.Add(new RouteCandidate(
                Source: "discovered_path",
                NextHop: hops.First(),
                Confidence: "medium",
                Rationale: $"MeshCore-style discovered path: [{path.IntermediatesCsv}].",
                RecommendedAction: $"send_test_message; the meshcore algorithm will source-route."));
        }

        // Direct discovery - peer heard from us.
        var peers = await database.GetDiscoveredPeers();
        var direct = peers.FirstOrDefault(p =>
            string.Equals(p.Callsign, destBase, StringComparison.OrdinalIgnoreCase)
            || p.Callsign.StartsWith(destBase + "-", StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            var ageMin = (int)Math.Max(0, (DateTime.UtcNow - direct.LastSeen).TotalMinutes);
            candidates.Add(new RouteCandidate(
                Source: "discovered_peer",
                NextHop: direct.Callsign,
                Confidence: ageMin < 60 ? "high" : "medium",
                Rationale: $"Heard {direct.Callsign} directly via {direct.Bearer}/{direct.ChannelKey} {ageMin}m ago.",
                RecommendedAction: $"run_probe({direct.Callsign}) to confirm session-level reachability, then send_test_message."));
        }

        // Transitive via:CALLSIGN candidates from B6.1 Phase 2.
        // These are entries the local node hasn't probed itself but
        // learned from another peer's `peers` response.
        var probed = await database.GetProbedNodes();
        var transitive = probed
            .Where(p => string.Equals(p.Callsign, destBase, StringComparison.OrdinalIgnoreCase)
                        || p.Callsign.StartsWith(destBase + "-", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Source.StartsWith("via:", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var via = p.Source["via:".Length..];
                return new RouteCandidate(
                    Source: "transitive",
                    NextHop: via,
                    Confidence: p.LastSuccessAt.HasValue ? "medium" : "low",
                    Rationale: $"Heard about {p.Callsign} from {via}'s peers list. " +
                               (p.LastSuccessAt.HasValue
                                   ? $"Last successful probe {(int)(DateTime.UtcNow - p.LastSuccessAt.Value).TotalMinutes}m ago."
                                   : "Never probed directly from here."),
                    RecommendedAction: $"explore_via_neighbour({via}) to confirm they still know about {p.Callsign}, then run_probe({p.Callsign}) over the same path.");
            });
        candidates.AddRange(transitive);

        var sb = new StringBuilder();
        sb.Append($"{destBase}: ");
        if (candidates.Count == 0)
        {
            sb.Append("no path known on any surface (route hints, learned routes, discovered paths, " +
                      "discovered peers, transitive via:* probes). The bounded-flood fallback would " +
                      "carry the first message and let a learned route form.");
            return new RouteOpinion(
                Destination: destBase,
                Candidates: candidates,
                RecommendedAction: $"send_test_message(app, '{destBase}', '...') - flood fallback is automatic.",
                Summary: sb.ToString());
        }

        var ordered = candidates
            .OrderBy(c => c.Confidence switch { "high" => 0, "medium" => 1, _ => 2 })
            .ToList();
        var best = ordered.First();
        sb.Append($"{ordered.Count} candidate(s); best is {best.Source} -> {best.NextHop} ({best.Confidence} confidence). ");
        sb.Append(best.RecommendedAction);

        return new RouteOpinion(
            Destination: destBase,
            Candidates: ordered,
            RecommendedAction: best.RecommendedAction,
            Summary: sb.ToString());
    }
}

public sealed record AnnotatedPeer(
    string Callsign,
    int? BearerPort,
    [property: Description("'known' if this node already tracks the callsign as a neighbour, discovered peer, or probed node; 'new' otherwise.")]
    string Status);

public sealed record NeighbourExploration(
    string Neighbour,
    bool ProbeSuccess,
    string ProbeError,
    int BearerPort,
    IReadOnlyList<AnnotatedPeer> Peers,
    int NewPeerCount,
    string Summary);

public sealed record RouteCandidate(
    string Source,
    string NextHop,
    string Confidence,
    string Rationale,
    string RecommendedAction);

public sealed record RouteOpinion(
    string Destination,
    IReadOnlyList<RouteCandidate> Candidates,
    string RecommendedAction,
    string Summary);
