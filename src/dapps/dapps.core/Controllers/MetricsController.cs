using System.Diagnostics;
using dapps.client;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// <c>GET /metrics</c>: the node's statistics in the Prometheus text
/// exposition format, for a Prometheus, VictoriaMetrics, Grafana Agent
/// or similar scraper. Open access like <c>/Health</c>: a scraper
/// carries no admin cookie (see <see cref="AdminAuthMiddleware"/>).
///
/// Same sources as <c>/Operational</c>: the process-lifetime counters in
/// <see cref="OperationalMetrics"/>, queue and table counts from
/// <see cref="Database"/>, the trailing-hour airtime from
/// <see cref="AirtimeAccountant"/>, open sessions from
/// <see cref="PeerSessionRegistry"/>, and the packet-node and MQTT
/// reachability probes (cached 15 s, so a 15 s scrape interval costs one
/// TCP connect per probe).
///
/// Link uptime comes out three ways, because the node has three kinds of
/// link: the socket to the packet node (<c>dapps_node_link_uptime_seconds</c>),
/// a DAPPS session with a peer (<c>dapps_peer_session_duration_seconds</c>),
/// and the per-neighbour forwarding record
/// (<c>dapps_neighbour_last_success_timestamp_seconds</c> and friends),
/// since an AX.25 link to a neighbour exists only while an exchange is
/// running.
///
/// Counters reset when the process restarts, as every Prometheus
/// counter does; <c>rate()</c> and <c>increase()</c> handle that.
/// </summary>
[ApiController]
[Route("metrics")]
public sealed class MetricsController(
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    Database database,
    AirtimeAccountant airtime,
    PeerSessionRegistry sessions,
    TcpReachabilityProbe probe,
    TimeProvider timeProvider,
    MeshCoreBearer? meshCore = null,
    UpdateChecker? updateChecker = null) : ControllerBase
{
    private const string PlaceholderCallsign = "N0CALL";

    private static readonly DateTime ProcessStartedAt = GetProcessStartedAt();

    private static DateTime GetProcessStartedAt()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    [HttpGet]
    [Produces("text/plain")]
    public async Task<ContentResult> Get()
    {
        var opts = options.CurrentValue;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var isRhpv2 = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase);
        var bearer = isRhpv2 ? "rhpv2" : "agw";

        var callsignOk = !string.IsNullOrWhiteSpace(opts.Callsign)
            && !string.Equals(opts.Callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        var callsignProblem = callsignOk ? MessageFields.CallsignError(opts.Callsign) : null;
        var nodePort = isRhpv2 ? (opts.RhpPort > 0 ? opts.RhpPort : 9000) : opts.AgwPort;
        var nodeOk = await probe.IsReachableAsync(opts.NodeHost, nodePort, TimeSpan.FromMilliseconds(500));
        var mqttOk = await probe.IsReachableAsync("127.0.0.1", opts.MqttPort, TimeSpan.FromMilliseconds(250));
        var healthy = callsignOk && callsignProblem is null && nodeOk && mqttOk;

        var c = metrics.Take();
        var pendingOutbound = await database.CountPendingOutbound();
        var undeliveredLocal = await database.CountUndeliveredLocal();
        var totalMessages = await database.CountMessages();
        var neighbourCount = (await database.GetNeighbours()).Count();
        var discoveredPeerCount = (await database.GetDiscoveredPeers()).Count;
        var discoveryChannelCount = (await database.GetDiscoveryChannels()).Count;

        var text = new PrometheusText();

        // Identity and process.
        text.Gauge("dapps_build_info", "DAPPS version and callsign; always 1.")
            .Sample("dapps_build_info", 1, ("version", AppVersion.Current()), ("callsign", opts.Callsign ?? ""), ("bearer", bearer));
        var startedAt = ProcessStartedAt;
        text.Gauge("dapps_process_start_time_seconds", "When this DAPPS process started, as seconds since the Unix epoch.",
            PrometheusText.UnixSeconds(startedAt));
        text.Gauge("dapps_uptime_seconds", "Seconds since this DAPPS process started.",
            Math.Max(0, (now - startedAt).TotalSeconds));

        // Health, the same rule /Health applies.
        text.Gauge("dapps_healthy", "1 when /Health reports healthy (callsign set and valid, packet node reachable, MQTT broker up), else 0.", healthy ? 1 : 0);
        text.Gauge("dapps_callsign_configured", "1 when a callsign other than the placeholder is set.", callsignOk ? 1 : 0);
        text.Gauge("dapps_callsign_valid", "1 when the configured callsign is valid. Every message from this node is dropped while it is 0.",
            callsignOk && callsignProblem is null ? 1 : 0);
        text.Gauge("dapps_node_reachable", "1 when the packet node answers on its bearer port (TCP connect, cached 15 s).")
            .Sample("dapps_node_reachable", nodeOk ? 1 : 0, ("bearer", bearer));
        text.Gauge("dapps_mqtt_broker_up", "1 when the built-in MQTT broker accepts a TCP connect on its port (cached 15 s).", mqttOk ? 1 : 0);
        if (updateChecker is not null)
            text.Gauge("dapps_update_available", "1 when a newer DAPPS release is known.", updateChecker.UpdateAvailable ? 1 : 0);

        // The link to the packet node (AGW or RHPv2 socket). The connect
        // counter and last-connect time are shared by both bearers; the
        // failure streak is kept per bearer.
        var reconnectAttempt = isRhpv2 ? c.Rhpv2ReconnectAttempt : c.AgwReconnectAttempt;
        var reconnectNextAt = isRhpv2 ? c.Rhpv2ReconnectNextAtUtc : c.AgwReconnectNextAtUtc;
        var linkUp = (isRhpv2 ? c.Rhpv2LinkOpen : c.AgwLinkOpen) && c.AgwLastReconnectAt is not null;
        text.Counter("dapps_node_link_connects_total", "Times the connection to the packet node was established (first connect and every reconnect).")
            .Sample("dapps_node_link_connects_total", c.AgwReconnects, ("bearer", bearer));
        text.Gauge("dapps_node_link_up", "1 while the connection to the packet node is open; 0 from the moment it closes, for any reason, until it is open again.")
            .Sample("dapps_node_link_up", linkUp ? 1 : 0, ("bearer", bearer));
        text.Gauge("dapps_node_link_uptime_seconds", "Seconds since the connection to the packet node was last established; 0 while down or before the first connect.")
            .Sample("dapps_node_link_uptime_seconds",
                linkUp ? Math.Max(0, (now - c.AgwLastReconnectAt!.Value).TotalSeconds) : 0,
                ("bearer", bearer));
        if (c.AgwLastReconnectAt is { } lastConnect)
            text.Gauge("dapps_node_link_last_connect_timestamp_seconds", "When the connection to the packet node was last established, as seconds since the Unix epoch.")
                .Sample("dapps_node_link_last_connect_timestamp_seconds", PrometheusText.UnixSeconds(lastConnect), ("bearer", bearer));
        text.Gauge("dapps_node_link_reconnect_failures", "Consecutive failed connects to the packet node; 0 while connected.")
            .Sample("dapps_node_link_reconnect_failures", reconnectAttempt, ("bearer", bearer));
        if (reconnectNextAt is { } nextAt)
            text.Gauge("dapps_node_link_next_retry_timestamp_seconds", "When the next automatic reconnect to the packet node is due, as seconds since the Unix epoch. Absent when not waiting.")
                .Sample("dapps_node_link_next_retry_timestamp_seconds", PrometheusText.UnixSeconds(nextAt), ("bearer", bearer));

        // MeshCore radio, when that bearer is on.
        var mesh = meshCore?.GetStatus();
        text.Gauge("dapps_meshcore_enabled", "1 when the MeshCore bearer is enabled.", mesh?.Enabled == true ? 1 : 0);
        if (mesh is { Enabled: true })
        {
            text.Gauge("dapps_meshcore_link_state", "MeshCore radio link state; 1 on the current state's label, 0 on the others.");
            foreach (var state in new[] { "Down", "Healthy", "Resetting", "Failed" })
                text.Sample("dapps_meshcore_link_state", string.Equals(mesh.LinkState, state, StringComparison.OrdinalIgnoreCase) ? 1 : 0, ("state", state));
            text.Counter("dapps_meshcore_link_resets_total", "Times the MeshCore radio link was reset.", mesh.Resets);
            text.Gauge("dapps_meshcore_channel_occupancy", "Fraction of the MeshCore channel in use, 0 to 1.", mesh.Occupancy);
            text.Counter("dapps_meshcore_delivered_total", "Messages delivered over the MeshCore bearer.", mesh.Delivered);
            text.Gauge("dapps_meshcore_reliable_pending", "MeshCore reliable sends still waiting for confirmation.", mesh.ReliablePending);
            text.Counter("dapps_meshcore_reliable_confirmed_total", "MeshCore reliable sends confirmed by the far end.", mesh.ReliableConfirmed);
            text.Counter("dapps_meshcore_reliable_expired_total", "MeshCore reliable sends that expired unconfirmed.", mesh.ReliableExpired);
        }

        // Messages passed.
        text.Counter("dapps_forward_attempts_total", "Attempts to forward a message to a neighbour.", c.ForwardAttempts);
        text.Counter("dapps_forward_success_total", "Messages forwarded to a neighbour and accepted.", c.ForwardSuccess);
        text.Counter("dapps_forward_failure_total", "Forward attempts that failed.", c.ForwardFailure);
        text.Counter("dapps_forward_bytes_total", "Payload bytes of messages forwarded to a neighbour and accepted.", c.ForwardBytes);
        text.Counter("dapps_inbound_messages_total", "Messages received, from a neighbour or from a local app to a local app. Each piece of a split message counts once; the reassembled whole does not count again.", c.InboundMessages);
        text.Counter("dapps_inbound_bytes_total", "Payload bytes of the messages counted by dapps_inbound_messages_total.", c.InboundBytes);
        text.Counter("dapps_inbound_connects_total", "Inbound DAPPS sessions the packet node handed to this node.", c.InboundConnects);
        text.Counter("dapps_ttl_expired_total", "Messages dropped because their time to live ran out.", c.TtlExpiredDrops);
        text.Counter("dapps_no_route_total", "Forwarding passes that found no route for a message.", c.NoRouteSkips);
        text.Counter("dapps_hash_mismatches_total", "Inbound messages whose payload hash did not match and were refused.", c.HashMismatches);
        text.Counter("dapps_probes_total", "Neighbour probes by outcome.")
            .Sample("dapps_probes_total", c.ProbeSuccess, ("result", "success"))
            .Sample("dapps_probes_total", c.ProbeFailure, ("result", "failure"));
        text.Counter("dapps_polls_total", "Neighbour polls by outcome.")
            .Sample("dapps_polls_total", c.PollSuccess, ("result", "success"))
            .Sample("dapps_polls_total", c.PollFailure, ("result", "failure"));
        text.Counter("dapps_routes_learned_total", "Routes learned from traffic or gossip.", c.RoutesLearned);
        text.Counter("dapps_peers_aged_out_total", "Discovered peers removed because they went quiet.", c.PeersAgedOut);
        text.Counter("dapps_airtime_budget_refusals_total", "Discovery transmissions refused because the airtime budget was spent.", c.BudgetRefusals);
        if (c.LastForwardSuccessAt is { } lastForward)
            text.Gauge("dapps_last_forward_success_timestamp_seconds", "When a message was last forwarded and accepted, as seconds since the Unix epoch. Absent before the first.",
                PrometheusText.UnixSeconds(lastForward));

        // Queues and tables.
        text.Gauge("dapps_messages_pending_outbound", "Messages for a remote node not yet forwarded.", pendingOutbound);
        text.Gauge("dapps_messages_undelivered_local", "Messages for a local app the app has not yet acknowledged.", undeliveredLocal);
        text.Gauge("dapps_messages_stored", "Rows in the messages table.", totalMessages);
        text.Gauge("dapps_neighbours", "Configured neighbours.", neighbourCount);
        text.Gauge("dapps_discovered_peers", "Peers known from discovery.", discoveredPeerCount);
        text.Gauge("dapps_discovery_channels", "Discovery channels configured.", discoveryChannelCount);

        // Airtime.
        text.Gauge("dapps_discovery_airtime_used_seconds", "Discovery airtime used in the trailing hour.", airtime.ConsumedSecondsLastHour);
        text.Gauge("dapps_discovery_airtime_budget_seconds", "Discovery airtime allowed per hour.", airtime.BudgetSecondsPerHour);

        // Per-neighbour link record: one row per neighbour ever forwarded to.
        if (c.Neighbours.Count > 0)
        {
            text.Counter("dapps_neighbour_forward_success_total", "Messages forwarded to this neighbour and accepted.");
            foreach (var n in c.Neighbours) text.Sample("dapps_neighbour_forward_success_total", n.SuccessCount, ("neighbour", n.Callsign));
            text.Counter("dapps_neighbour_forward_failure_total", "Forward attempts to this neighbour that failed.");
            foreach (var n in c.Neighbours) text.Sample("dapps_neighbour_forward_failure_total", n.FailureCount, ("neighbour", n.Callsign));
            text.Counter("dapps_neighbour_forward_bytes_total", "Payload bytes forwarded to this neighbour and accepted.");
            foreach (var n in c.Neighbours) text.Sample("dapps_neighbour_forward_bytes_total", n.BytesForwarded, ("neighbour", n.Callsign));
            text.Gauge("dapps_neighbour_link_ok", "1 when the last forward to this neighbour succeeded, 0 when it failed.");
            foreach (var n in c.Neighbours)
            {
                var ok = n.LastSuccessAt is { } s && (n.LastFailureAt is not { } f || s >= f);
                text.Sample("dapps_neighbour_link_ok", ok ? 1 : 0, ("neighbour", n.Callsign));
            }
            text.Gauge("dapps_neighbour_last_success_timestamp_seconds", "When a forward to this neighbour last succeeded, as seconds since the Unix epoch.");
            foreach (var n in c.Neighbours.Where(n => n.LastSuccessAt is not null))
                text.Sample("dapps_neighbour_last_success_timestamp_seconds", PrometheusText.UnixSeconds(n.LastSuccessAt!.Value), ("neighbour", n.Callsign));
            text.Gauge("dapps_neighbour_last_failure_timestamp_seconds", "When a forward to this neighbour last failed, as seconds since the Unix epoch.");
            foreach (var n in c.Neighbours.Where(n => n.LastFailureAt is not null))
                text.Sample("dapps_neighbour_last_failure_timestamp_seconds", PrometheusText.UnixSeconds(n.LastFailureAt!.Value), ("neighbour", n.Callsign));
        }

        // Open DAPPS sessions with peers, grouped by peer and direction.
        var open = sessions.AllSessions()
            .GroupBy(s => (Peer: s.Peer, s.Session.Direction))
            .OrderBy(g => g.Key.Peer).ThenBy(g => g.Key.Direction)
            .ToList();
        text.Gauge("dapps_peer_sessions_open", "DAPPS sessions open with this peer in this direction.");
        foreach (var g in open)
            text.Sample("dapps_peer_sessions_open", g.Count(), ("peer", g.Key.Peer), ("direction", g.Key.Direction));
        text.Gauge("dapps_peer_session_duration_seconds", "Seconds the longest-open connected session with this peer in this direction has been open.");
        foreach (var g in open)
        {
            var connected = g.Where(s => s.Session.Connected).ToList();
            if (connected.Count == 0) continue;
            var oldest = connected.Min(s => s.Session.Since).UtcDateTime;
            text.Sample("dapps_peer_session_duration_seconds", Math.Max(0, (now - oldest).TotalSeconds), ("peer", g.Key.Peer), ("direction", g.Key.Direction));
        }

        return Content(text.ToString(), PrometheusText.ContentType);
    }
}
