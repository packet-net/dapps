using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Updater;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan C3 PR-B - single source of truth for the
/// "what's going on with this node?" aggregate snapshot consumed by
/// both <c>/Operational</c> (operator-facing JSON) and
/// <c>HeartbeatPublisher</c> (periodic MQTT publish).
///
/// Composes liveness probes (node, MQTT), counters from
/// <see cref="OperationalMetrics"/>, queue / peer / channel /
/// neighbour counts from <see cref="Database"/>, and trailing-hour
/// airtime usage from <see cref="AirtimeAccountant"/>. Plus a
/// trimmed recent-events tail (last 20 - lighter than the dashboard's
/// full 100-entry ring; the heartbeat goes on the wire every minute
/// and shouldn't carry the full history).
/// </summary>
public sealed class OperationalSnapshotBuilder(
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    AirtimeAccountant airtime,
    Database database,
    UpdateChecker updateChecker,
    Updater.IUpdaterFileSystem updaterFs,
    TimeProvider timeProvider)
{
    private const string PlaceholderCallsign = "N0CALL";
    private const int RecentEventsInSnapshot = 20;

    /// <summary>
    /// Builds the operator-facing aggregate. Lighter shape suitable for
    /// the periodic MQTT heartbeat (capped row counts, no payload bytes).
    /// </summary>
    public async Task<OperationalSnapshot> BuildAsync(CancellationToken ct = default)
        => await BuildInternalAsync(includeRows: false, ct);

    /// <summary>
    /// Builds the dashboard-facing aggregate. Includes the per-row
    /// tables the dashboard renders (outbound queue, local inbox, dropped,
    /// neighbours, probed/polled nodes, discovered peers, discovery
    /// channels, update status). Larger document; suitable for the
    /// dashboard's polling fallback or MQTT-WebSocket subscribers.
    /// </summary>
    public async Task<OperationalSnapshot> BuildFullAsync(CancellationToken ct = default)
        => await BuildInternalAsync(includeRows: true, ct);

    private async Task<OperationalSnapshot> BuildInternalAsync(bool includeRows, CancellationToken ct)
    {
        var opts = options.CurrentValue;

        var callsignOk = !string.IsNullOrWhiteSpace(opts.Callsign)
            && !string.Equals(opts.Callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        var nodePort = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase)
            ? (opts.RhpPort > 0 ? opts.RhpPort : 9000)
            : opts.AgwPort;
        var nodeOk = await ProbeTcp(opts.NodeHost, nodePort, TimeSpan.FromMilliseconds(500));
        var mqttOk = await ProbeTcp("127.0.0.1", opts.MqttPort, TimeSpan.FromMilliseconds(250));

        var counters = metrics.Take();
        var isRhpv2 = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase);
        var reconnectAttempt = isRhpv2 ? counters.Rhpv2ReconnectAttempt : counters.AgwReconnectAttempt;
        var reconnectNextAtUtc = isRhpv2 ? counters.Rhpv2ReconnectNextAtUtc : counters.AgwReconnectNextAtUtc;
        var pendingOutbound = await database.CountPendingOutbound();
        var undeliveredLocal = await database.CountUndeliveredLocal();
        var totalMessages = await database.CountMessages();

        IReadOnlyList<DbNeighbour> neighbours = (await database.GetNeighbours()).ToList();
        IReadOnlyList<DbDiscoveredPeer> discoveredPeers = (await database.GetDiscoveredPeers()).ToList();
        IReadOnlyList<DbDiscoveryChannel> discoveryChannels = await database.GetDiscoveryChannels();

        DashboardTables? tables = null;
        if (includeRows)
        {
            var local = opts.Callsign.Split('-')[0];
            var recent = (await database.GetRecentMessages(50)).ToList();
            bool IsLocal(DbMessage m) =>
                string.Equals(m.Destination.Split('@').Last().Split('-')[0], local, StringComparison.OrdinalIgnoreCase);
            QueueRow Row(DbMessage m, string? appOverride = null) => new(
                Id: m.Id,
                Destination: m.Destination,
                App: appOverride ?? m.Destination.Split('@')[0],
                SourceCallsign: m.SourceCallsign,
                Bytes: m.Payload.Length,
                Ttl: m.Ttl,
                AgeSeconds: (int)Math.Max(0, (timeProvider.GetUtcNow().UtcDateTime - m.CreatedAt).TotalSeconds));
            var outbound = recent.Where(m => !m.Forwarded && !IsLocal(m)).Select(m => Row(m)).ToList();
            var inbox = recent.Where(m => !m.LocallyDelivered && IsLocal(m))
                .Select(m => Row(m, m.Destination.Split('@')[0]))
                .ToList();
            var dropped = (await database.GetRecentDroppedMessages(50))
                .Select(d => new DroppedMessageRow(
                    Id: d.Id,
                    Destination: d.Destination,
                    SourceCallsign: d.SourceCallsign,
                    Bytes: d.Payload.Length,
                    Ttl: d.Ttl,
                    CreatedAt: d.CreatedAt,
                    DroppedAt: d.DroppedAt,
                    Reason: d.Reason)).ToList();
            var probed = await database.GetProbedNodes();
            var polled = await database.GetPolledNodes();

            // Update status: same shape /Update/status returns. Use the
            // same IUpdaterFileSystem reads UpdateController uses so we
            // don't drift.
            var paths = UpdaterPaths.Default;
            UpdateStatus? lastRun = null;
            var rawStatus = updaterFs.ReadAllText(paths.StatusPath);
            if (!string.IsNullOrWhiteSpace(rawStatus))
            {
                try { lastRun = JsonSerializer.Deserialize<UpdateStatus>(rawStatus); }
                catch (JsonException) { /* surface unknown rather than throw */ }
            }
            var latest = updateChecker.Latest;
            var update = new UpdateStatusInline(
                Current: updateChecker.Current,
                IsDevBuild: updateChecker.IsDevBuild,
                Latest: latest?.Tag,
                ReleaseUrl: latest?.Url,
                IsAvailable: updateChecker.UpdateAvailable,
                FetchedAt: latest?.FetchedAt,
                RequestPending: updaterFs.Exists(paths.RequestPath),
                LastRun: lastRun);

            tables = new DashboardTables(
                Outbound: outbound,
                LocalInbox: inbox,
                Dropped: dropped,
                Neighbours: neighbours.ToList(),
                DiscoveredPeers: discoveredPeers.ToList(),
                DiscoveryChannels: discoveryChannels.ToList(),
                ProbedNodes: probed,
                PolledNodes: polled,
                Update: update);
        }

        return new OperationalSnapshot(
            Callsign: opts.Callsign,
            Version: UpdaterCli.ResolveCurrentVersion(),
            GeneratedAt: timeProvider.GetUtcNow().UtcDateTime,
            UptimeSeconds: (long)Math.Max(0, (timeProvider.GetUtcNow().UtcDateTime - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds),

            Status: (callsignOk && nodeOk && mqttOk) ? "healthy" : "degraded",
            CallsignConfigured: callsignOk,
            NodeReachable: nodeOk,
            MqttBrokerUp: mqttOk,
            LastForwardSuccessAt: counters.LastForwardSuccessAt,
            NodeReconnectAttempt: reconnectAttempt,
            NodeReconnectAt: reconnectNextAtUtc,

            ForwardAttempts: counters.ForwardAttempts,
            ForwardSuccess: counters.ForwardSuccess,
            ForwardFailure: counters.ForwardFailure,
            TtlExpiredDrops: counters.TtlExpiredDrops,
            NoRouteSkips: counters.NoRouteSkips,
            AgwReconnects: counters.AgwReconnects,
            AgwLastReconnectAt: counters.AgwLastReconnectAt,
            InboundConnects: counters.InboundConnects,
            HashMismatches: counters.HashMismatches,
            ProbeAttempts: counters.ProbeAttempts,
            ProbeSuccess: counters.ProbeSuccess,
            ProbeFailure: counters.ProbeFailure,
            PollAttempts: counters.PollAttempts,
            PollSuccess: counters.PollSuccess,
            PollFailure: counters.PollFailure,
            RoutesLearned: counters.RoutesLearned,
            PeersAgedOut: counters.PeersAgedOut,
            BudgetRefusals: counters.BudgetRefusals,

            PendingOutboundCount: pendingOutbound,
            UndeliveredLocalCount: undeliveredLocal,
            TotalMessagesCount: totalMessages,
            NeighbourCount: neighbours.Count,
            DiscoveredPeerCount: discoveredPeers.Count,
            DiscoveryChannelCount: discoveryChannels.Count,

            AirtimeConsumedSecondsLastHour: airtime.ConsumedSecondsLastHour,
            AirtimeBudgetSecondsPerHour: airtime.BudgetSecondsPerHour,

            RecentEvents: counters.RecentEvents.Take(RecentEventsInSnapshot).ToList(),
            // Per-neighbour link state (last success / last failure /
            // counts) - the dashboard's "Per-neighbour link state"
            // panel renders this. Included on every snapshot (light vs
            // full), since the data is small (one row per neighbour
            // we've ever forwarded to).
            NeighbourLinks: counters.Neighbours,
            Tables: tables);
    }

    private static async Task<bool> ProbeTcp(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record OperationalSnapshot(
    string Callsign,
    string Version,
    DateTime GeneratedAt,
    long UptimeSeconds,

    string Status,
    bool CallsignConfigured,
    bool NodeReachable,
    bool MqttBrokerUp,
    DateTime? LastForwardSuccessAt,

    /// <summary>Consecutive connect failures for whichever bearer is
    /// currently configured (<see cref="SystemOptions.NodeBearer"/>). 0
    /// when connected, idle-gated, or never attempted.</summary>
    int NodeReconnectAttempt,
    /// <summary>When the next automatic reconnect attempt is due, or
    /// null when not currently in a backoff wait. Drives the dashboard's
    /// "retrying in Xs" countdown and gates the manual retry button.</summary>
    DateTime? NodeReconnectAt,

    long ForwardAttempts,
    long ForwardSuccess,
    long ForwardFailure,
    long TtlExpiredDrops,
    long NoRouteSkips,
    long AgwReconnects,
    DateTime? AgwLastReconnectAt,
    long InboundConnects,
    long HashMismatches,
    long ProbeAttempts,
    long ProbeSuccess,
    long ProbeFailure,
    long PollAttempts,
    long PollSuccess,
    long PollFailure,
    long RoutesLearned,
    long PeersAgedOut,
    long BudgetRefusals,

    int PendingOutboundCount,
    int UndeliveredLocalCount,
    int TotalMessagesCount,
    int NeighbourCount,
    int DiscoveredPeerCount,
    int DiscoveryChannelCount,

    double AirtimeConsumedSecondsLastHour,
    int AirtimeBudgetSecondsPerHour,

    IReadOnlyList<OperationalMetrics.OperationalEvent> RecentEvents,
    IReadOnlyList<OperationalMetrics.NeighbourSnapshot> NeighbourLinks,

    /// <summary>Per-row tables the dashboard renders. Null on the
    /// MQTT-heartbeat snapshot (the lighter shape); populated on the
    /// dashboard-facing /Operational?full=true response and on
    /// dapps/metrics/heartbeat.full for WebSocket subscribers.</summary>
    DashboardTables? Tables);

/// <summary>The per-row collections the dashboard's tables render.
/// Excluded from the lightweight heartbeat to keep the periodic publish
/// cheap; included on the full snapshot for one-fetch consolidation.</summary>
public sealed record DashboardTables(
    IReadOnlyList<QueueRow> Outbound,
    IReadOnlyList<QueueRow> LocalInbox,
    IReadOnlyList<DroppedMessageRow> Dropped,
    IReadOnlyList<DbNeighbour> Neighbours,
    IReadOnlyList<DbDiscoveredPeer> DiscoveredPeers,
    IReadOnlyList<DbDiscoveryChannel> DiscoveryChannels,
    IReadOnlyList<DbProbedNode> ProbedNodes,
    IReadOnlyList<DbPolledNode> PolledNodes,
    UpdateStatusInline Update);

/// <summary>Same shape as <c>VersionStatus</c> on /Update/status.
/// Inlined so the dashboard one-fetch can render the update card from
/// the same /Operational document.</summary>
public sealed record UpdateStatusInline(
    string Current,
    bool IsDevBuild,
    string? Latest,
    string? ReleaseUrl,
    bool IsAvailable,
    DateTime? FetchedAt,
    bool RequestPending,
    Updater.UpdateStatus? LastRun);
