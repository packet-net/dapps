using System.ComponentModel;
using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-C - composite diagnostic tools. The actual point of the
/// MCP work: each method gathers data from 3-5 underlying surfaces
/// (Database, OperationalMetrics, OperationalSnapshotBuilder), works
/// through a deterministic decision tree, and returns a structured
/// finding plus a one-paragraph human-readable summary.
///
/// The summaries are deliberately flat strings - they're prompts for
/// the LLM to riff on, not finished prose. The structured fields
/// support more nuanced narratives if the agent wants them.
/// </summary>
[McpServerToolType]
public sealed class DappsDiagnosticTools(
    Database database,
    OperationalMetrics metrics,
    IOptionsMonitor<SystemOptions> options)
{
    [McpServerTool(Name = "explain_why_message_failed")]
    [Description(
        "Trace a single message's journey: live queue, dropped log, or unknown. Pulls the row, looks for " +
        "matching forward.fail / ttl.expired / route.none events in the in-memory ring, and resolves the " +
        "destination's reachability (route hints, learned routes, neighbour config). Returns a structured " +
        "finding + a one-paragraph summary the operator can read at a glance. Pure read-only.")]
    public async Task<MessageDiagnosis> ExplainWhyMessageFailedAsync(
        [Description("7-character message id, e.g. 'abc1234'.")] string id)
    {
        var live = await database.GetMessage(id);
        DbDroppedMessage? dropped = null;
        if (live is null)
        {
            var droppedList = await database.GetRecentDroppedMessages(200);
            dropped = droppedList.FirstOrDefault(d =>
                string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        var matchingEvents = metrics.Take().RecentEvents
            .Where(e => e.Summary.Contains(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string state;
        string destination;
        string? reason;
        if (live is not null)
        {
            destination = live.Destination;
            if (live.LocallyDelivered) state = "delivered locally";
            else if (live.Forwarded) state = "forwarded";
            else state = "queued (not yet forwarded)";
            reason = null;
        }
        else if (dropped is not null)
        {
            destination = dropped.Destination;
            state = "dropped";
            reason = dropped.Reason;
        }
        else
        {
            return new MessageDiagnosis(
                Id: id, Found: false, State: "unknown",
                Destination: null, DropReason: null,
                MatchingEvents: matchingEvents, RouteFinding: null,
                Summary: $"No record of message {id}. It may have been delivered and cleaned up from the queue, " +
                         "or never existed on this node. Decision events older than the in-memory ring (~100 " +
                         "entries) are still in the systemd journal: try `journalctl -u dapps -g '" + id + "'`.");
        }

        var destBase = destination.Split('@').Last().Split('-')[0];
        var routeFinding = await ResolveRouteAsync(destBase);

        var sb = new StringBuilder();
        sb.Append($"Message {id} for {destination}: {state}.");
        if (reason is not null) sb.Append($" Reason: {reason}.");
        if (matchingEvents.Count > 0)
        {
            sb.Append($" {matchingEvents.Count} matching event(s) in the in-memory ring");
            var failures = matchingEvents.Where(e => e.Kind.EndsWith(".fail", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0) sb.Append($" ({failures.Count} failure(s))");
            sb.Append('.');
        }
        sb.Append(' ').Append(routeFinding.Summary);
        return new MessageDiagnosis(
            Id: id, Found: true, State: state,
            Destination: destination, DropReason: reason,
            MatchingEvents: matchingEvents, RouteFinding: routeFinding,
            Summary: sb.ToString());
    }

    [McpServerTool(Name = "diagnose_silent_neighbour")]
    [Description(
        "Walk every state surface for a given callsign - manual neighbour config, last-heard discovery beacon, " +
        "B6.1 probe history, F3b poll history - and stitch together 'when did we last see them, when did we " +
        "last forward to them, what's been failing'. Useful when an operator notices a peer hasn't shown " +
        "activity for a while.")]
    public async Task<NeighbourDiagnosis> DiagnoseSilentNeighbourAsync(
        [Description("Target callsign (case-insensitive). Resolves against neighbours, discovered peers, probes, polls.")]
        string callsign)
    {
        var normalized = callsign.Trim().ToUpperInvariant();
        var neighbour = await database.GetNeighbour(normalized);
        var discoveredPeers = (await database.GetDiscoveredPeers())
            .Where(p => string.Equals(p.Callsign, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var probed = await database.GetProbedNode(normalized);
        var polled = await database.GetPolledNode(normalized);

        var matchingEvents = metrics.Take().RecentEvents
            .Where(e => e.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var anyTrace = neighbour is not null || discoveredPeers.Count > 0 || probed is not null || polled is not null;
        if (!anyTrace)
        {
            return new NeighbourDiagnosis(
                Callsign: normalized, Found: false,
                Neighbour: null, DiscoveredPeers: discoveredPeers,
                Probed: null, Polled: null,
                MatchingEvents: matchingEvents,
                Summary: $"No trace of {normalized} on any surface (neighbours / discovered peers / probes / polls). " +
                         "Either the callsign is wrong, or this node has never heard of them.");
        }

        var sb = new StringBuilder();
        sb.Append($"{normalized}: ");
        if (neighbour is not null)
        {
            var via = neighbour.UdpEndpoint is not null ? $"UDP {neighbour.UdpEndpoint}" : $"AGW port {neighbour.BearerPort}";
            sb.Append($"configured as a manual neighbour ({via}); ");
        }
        if (discoveredPeers.Count > 0)
        {
            var freshest = discoveredPeers.OrderByDescending(p => p.LastSeen).First();
            var ageMinutes = Math.Max(0, (int)(DateTime.UtcNow - freshest.LastSeen).TotalMinutes);
            sb.Append($"last beacon heard {ageMinutes}m ago via {freshest.Bearer}/{freshest.ChannelKey}; ");
        }
        else if (neighbour is not null)
        {
            sb.Append("no recent beacons in the discovered-peers table; ");
        }
        if (probed is not null)
        {
            if (probed.LastSuccessAt.HasValue)
            {
                var ageMin = Math.Max(0, (int)(DateTime.UtcNow - probed.LastSuccessAt.Value).TotalMinutes);
                sb.Append($"last successful probe {ageMin}m ago, {probed.ConsecutiveFailures} failure(s) since; ");
            }
            else if (probed.LastError.Length > 0)
            {
                sb.Append($"never probed successfully ({probed.ConsecutiveFailures} failure(s) - last: {probed.LastError}); ");
            }
        }
        if (polled is not null && polled.LastSuccessAt.HasValue)
        {
            var ageMin = Math.Max(0, (int)(DateTime.UtcNow - polled.LastSuccessAt.Value).TotalMinutes);
            sb.Append($"last successful poll {ageMin}m ago drained {polled.MessagesDrained} total; ");
        }
        if (matchingEvents.Count > 0)
        {
            sb.Append($"{matchingEvents.Count} event(s) in the recent ring mention {normalized}.");
        }
        else
        {
            sb.Append("no recent events mention this callsign.");
        }
        return new NeighbourDiagnosis(
            Callsign: normalized, Found: true,
            Neighbour: neighbour, DiscoveredPeers: discoveredPeers,
            Probed: probed, Polled: polled,
            MatchingEvents: matchingEvents,
            Summary: sb.ToString());
    }

    [McpServerTool(Name = "summarize_recent_activity")]
    [Description(
        "Aggregate the in-memory decision-events ring (last ~100 entries) into per-kind counts plus a " +
        "human-readable digest. Honest about scope: the ring is bounded by count not time, so on a busy node " +
        "this represents the recent past in minutes; on a quiet node it might span days. For a true 24h " +
        "window, parse `journalctl -u dapps -g 'event ' --since '24 hours ago'` host-side.")]
    public ActivitySummary SummarizeRecentActivity()
    {
        var snap = metrics.Take();
        var events = snap.RecentEvents;

        var byKind = events
            .GroupBy(e => e.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        DateTime? oldest = events.Count > 0 ? events.Min(e => e.At) : null;
        DateTime? newest = events.Count > 0 ? events.Max(e => e.At) : null;

        var sb = new StringBuilder();
        sb.Append($"In-memory ring: {events.Count} events");
        if (oldest is not null && newest is not null)
        {
            var span = newest.Value - oldest.Value;
            sb.Append($" spanning {Format(span)}");
        }
        sb.Append(". ");
        sb.Append($"Forwards: {snap.ForwardSuccess} ok, {snap.ForwardFailure} failed (lifetime). ");
        sb.Append($"Probes: {snap.ProbeSuccess}/{snap.ProbeAttempts}. ");
        sb.Append($"Polls: {snap.PollSuccess}/{snap.PollAttempts}. ");
        if (snap.RoutesLearned > 0) sb.Append($"{snap.RoutesLearned} route learnings. ");
        if (snap.PeersAgedOut > 0) sb.Append($"{snap.PeersAgedOut} peer age-outs. ");
        if (snap.BudgetRefusals > 0) sb.Append($"{snap.BudgetRefusals} airtime-budget refusals. ");
        if (snap.LastForwardSuccessAt.HasValue)
        {
            var ageMin = Math.Max(0, (int)(DateTime.UtcNow - snap.LastForwardSuccessAt.Value).TotalMinutes);
            sb.Append($"Last successful forward: {ageMin}m ago.");
        }
        else
        {
            sb.Append("No successful forwards yet.");
        }

        return new ActivitySummary(
            Counters: snap,
            EventsByKind: byKind,
            EventCount: events.Count,
            OldestEventAt: oldest,
            NewestEventAt: newest,
            Summary: sb.ToString());
    }

    [McpServerTool(Name = "find_path_to")]
    [Description(
        "Show how a message would currently be routed to a destination callsign. Walks the resolution " +
        "precedence: manual route hint -> learned route (B5) -> discovered path (B5.1 meshcore) -> discovered " +
        "peer (direct) -> manual neighbour. Returns the chosen next hop + which surface it came from + " +
        "freshness info, or 'no path' when nothing matches (a flood would be the fallback in that case).")]
    public async Task<RouteFinding> FindPathToAsync(
        [Description("Destination callsign (case-insensitive). The base callsign without SSID is what gets resolved against the routing tables.")]
        string destination)
    {
        var destBase = destination.Trim().Split('-')[0].ToUpperInvariant();
        return await ResolveRouteAsync(destBase);
    }

    [McpServerTool(Name = "propose_topology_changes")]
    [Description(
        "Look at the current network state and suggest concrete changes: heard-but-not-configured peers " +
        "the operator should consider adding; long-silent neighbours that may want investigating or " +
        "removing; high-failure-streak probed nodes that may be misconfigured. Pure read-only - proposes, " +
        "doesn't act. Each proposal has a rationale string the operator can sanity-check before " +
        "running the corresponding action tool.")]
    public async Task<TopologyProposals> ProposeTopologyChangesAsync()
    {
        var neighbours = (await database.GetNeighbours()).ToList();
        var peers = (await database.GetDiscoveredPeers()).ToList();
        var probed = (await database.GetProbedNodes()).ToList();
        var ourBase = options.CurrentValue.Callsign.Split('-')[0];

        var configuredCallsigns = neighbours.Select(n => n.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var heardNotConfigured = peers
            .Where(p => !configuredCallsigns.Contains(p.Callsign))
            .Where(p => !p.Callsign.StartsWith(ourBase, StringComparison.OrdinalIgnoreCase))   // skip self
            .GroupBy(p => p.Callsign, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.CostHint).First())
            .ToList();

        var addCandidates = heardNotConfigured
            .Select(p => new AddNeighbourProposal(
                Callsign: p.Callsign,
                Bearer: p.Bearer,
                ChannelKey: p.ChannelKey,
                Rationale: $"Heard via {p.Bearer}/{p.ChannelKey} (link class {p.LinkClass}, cost {p.CostHint}, last seen {Format(DateTime.UtcNow - p.LastSeen)} ago) but not in the manual neighbours table."))
            .ToList();

        var nowUtc = DateTime.UtcNow;
        var investigateCandidates = probed
            .Where(p => !p.OptOut)
            .Where(p => p.ConsecutiveFailures >= 5)
            .Select(p =>
            {
                var lastSuccess = p.LastSuccessAt.HasValue
                    ? $"last successful probe {Format(nowUtc - p.LastSuccessAt.Value)} ago"
                    : "never probed successfully";
                return new InvestigateProposal(
                    Callsign: p.Callsign,
                    ConsecutiveFailures: p.ConsecutiveFailures,
                    LastError: p.LastError,
                    Rationale: $"{p.ConsecutiveFailures} consecutive probe failure(s); {lastSuccess}. " +
                               "Consider running run_probe to confirm, then either fixing the path or " +
                               "setting OptOut to stop scheduled probes.");
            })
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"{addCandidates.Count} new peer(s) heard but not configured");
        if (addCandidates.Count > 0) sb.Append($" ({string.Join(", ", addCandidates.Take(5).Select(c => c.Callsign))}{(addCandidates.Count > 5 ? ", ..." : "")})");
        sb.Append("; ");
        sb.Append($"{investigateCandidates.Count} probe target(s) with 5+ consecutive failures");
        if (investigateCandidates.Count > 0) sb.Append($" ({string.Join(", ", investigateCandidates.Take(5).Select(c => c.Callsign))}{(investigateCandidates.Count > 5 ? ", ..." : "")})");
        sb.Append('.');
        if (addCandidates.Count == 0 && investigateCandidates.Count == 0)
        {
            sb.Clear();
            sb.Append("No actionable proposals - neighbours match heard peers, no probe targets stuck in failure.");
        }

        return new TopologyProposals(
            AddCandidates: addCandidates,
            InvestigateCandidates: investigateCandidates,
            Summary: sb.ToString());
    }

    private async Task<RouteFinding> ResolveRouteAsync(string destinationBaseCallsign)
    {
        var hints = await database.GetRouteHintsAsync();
        var hint = hints.FirstOrDefault(h =>
            string.Equals(h.Destination, destinationBaseCallsign, StringComparison.OrdinalIgnoreCase));
        if (hint is not null)
        {
            return new RouteFinding(
                Destination: destinationBaseCallsign,
                NextHop: hint.NextHop,
                Source: "route_hint",
                AgeSummary: null,
                Summary: $"{destinationBaseCallsign}: would forward via manual route hint to {hint.NextHop}.");
        }

        var learned = await database.GetLearnedRouteAsync(destinationBaseCallsign);
        if (learned is not null)
        {
            var age = Format(DateTime.UtcNow - learned.LastSeenAt);
            return new RouteFinding(
                Destination: destinationBaseCallsign,
                NextHop: learned.NextHopCallsign,
                Source: "learned_route",
                AgeSummary: age,
                Summary: $"{destinationBaseCallsign}: would forward via learned next-hop {learned.NextHopCallsign} (last seen {age} ago, {learned.ConsecutiveFailures} failure(s)).");
        }

        var path = await database.GetDiscoveredPathAsync(destinationBaseCallsign);
        if (path is not null && !string.IsNullOrEmpty(path.IntermediatesCsv))
        {
            var first = path.IntermediatesCsv.Split(',').First();
            return new RouteFinding(
                Destination: destinationBaseCallsign,
                NextHop: first,
                Source: "discovered_path",
                AgeSummary: Format(DateTime.UtcNow - path.LastSeenAt),
                Summary: $"{destinationBaseCallsign}: would source-route via [{path.IntermediatesCsv}] (MeshCore-style; first hop {first}).");
        }

        var peers = await database.GetDiscoveredPeers();
        var direct = peers.FirstOrDefault(p =>
            string.Equals(p.Callsign, destinationBaseCallsign, StringComparison.OrdinalIgnoreCase)
            || p.Callsign.StartsWith(destinationBaseCallsign + "-", StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return new RouteFinding(
                Destination: destinationBaseCallsign,
                NextHop: direct.Callsign,
                Source: "discovered_peer",
                AgeSummary: Format(DateTime.UtcNow - direct.LastSeen),
                Summary: $"{destinationBaseCallsign}: directly heard as {direct.Callsign} via {direct.Bearer}/{direct.ChannelKey} (last seen {Format(DateTime.UtcNow - direct.LastSeen)} ago).");
        }

        var neighbours = await database.GetNeighbours();
        var matchingNeighbour = neighbours.FirstOrDefault(n =>
            string.Equals(n.Callsign, destinationBaseCallsign, StringComparison.OrdinalIgnoreCase)
            || n.Callsign.StartsWith(destinationBaseCallsign + "-", StringComparison.OrdinalIgnoreCase));
        if (matchingNeighbour is not null)
        {
            var via = matchingNeighbour.UdpEndpoint is not null ? $"UDP {matchingNeighbour.UdpEndpoint}" : $"AGW port {matchingNeighbour.BearerPort}";
            return new RouteFinding(
                Destination: destinationBaseCallsign,
                NextHop: matchingNeighbour.Callsign,
                Source: "neighbour",
                AgeSummary: null,
                Summary: $"{destinationBaseCallsign}: configured as a manual neighbour ({via}); would forward directly.");
        }

        return new RouteFinding(
            Destination: destinationBaseCallsign,
            NextHop: null,
            Source: null,
            AgeSummary: null,
            Summary: $"{destinationBaseCallsign}: no path known. The bounded-flood fallback would carry the first message, then a learned route would form.");
    }

    private static string Format(TimeSpan span)
    {
        if (span.TotalSeconds < 0) return "0s";
        if (span.TotalSeconds < 90) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 90) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }
}

public sealed record RouteFinding(
    string Destination,
    string? NextHop,
    string? Source,
    string? AgeSummary,
    string Summary);

public sealed record MessageDiagnosis(
    string Id,
    bool Found,
    string State,
    string? Destination,
    string? DropReason,
    IReadOnlyList<OperationalMetrics.OperationalEvent> MatchingEvents,
    RouteFinding? RouteFinding,
    string Summary);

public sealed record NeighbourDiagnosis(
    string Callsign,
    bool Found,
    DbNeighbour? Neighbour,
    IReadOnlyList<DbDiscoveredPeer> DiscoveredPeers,
    DbProbedNode? Probed,
    DbPolledNode? Polled,
    IReadOnlyList<OperationalMetrics.OperationalEvent> MatchingEvents,
    string Summary);

public sealed record ActivitySummary(
    OperationalMetrics.Snapshot Counters,
    IReadOnlyDictionary<string, int> EventsByKind,
    int EventCount,
    DateTime? OldestEventAt,
    DateTime? NewestEventAt,
    string Summary);

public sealed record AddNeighbourProposal(
    string Callsign,
    string Bearer,
    string ChannelKey,
    string Rationale);

public sealed record InvestigateProposal(
    string Callsign,
    int ConsecutiveFailures,
    string LastError,
    string Rationale);

public sealed record TopologyProposals(
    IReadOnlyList<AddNeighbourProposal> AddCandidates,
    IReadOnlyList<InvestigateProposal> InvestigateCandidates,
    string Summary);
