using System.ComponentModel;
using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-B - operator-supervised action tools. Each method
/// performs the same work the dashboard's "run now" buttons do:
/// fire a probe / poll / solicit, submit a test message. Deliberately
/// no autonomy - the LLM proposes, the operator (via the user prompt)
/// triggers. Risky shapes (changing callsign, restarting services,
/// rolling back updates) are out of scope.
/// </summary>
[McpServerToolType]
public sealed class DappsActionTools(
    Database database,
    ProbeSchedulerService probeScheduler,
    PollSchedulerService pollScheduler,
    DiscoveryService discovery,
    NodeProber prober,
    IOptionsMonitor<SystemOptions> options)
{
    [McpServerTool(Name = "run_probe")]
    [Description(
        "Probe a single callsign now (B6.1). Bypasses the normal cadence + airtime budget - operator-triggered " +
        "actions are explicit human decisions. The probe is over AGW; the bearer port is resolved in this " +
        "precedence order: (1) the configured neighbour's BearerPort, (2) the AGW-bearer discovered peer's " +
        "observed port, (3) SystemOptions.DefaultBearerPort. Returns an error if the callsign is unknown on " +
        "every surface - add a /Neighbours row first, or wait for a beacon.")]
    public async Task<DbProbedNode> RunProbeAsync(
        [Description("Target DAPPS callsign, case-insensitive (e.g. 'M0LTE-9').")] string callsign,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            throw new ArgumentException("callsign is required", nameof(callsign));
        var normalized = callsign.Trim().ToUpperInvariant();
        var (port, hasRoute) = await ResolveAgwPortAsync(normalized);
        if (!hasRoute)
        {
            throw new InvalidOperationException(
                $"No AGW route to {normalized} - add a /Neighbours row or wait for a beacon.");
        }
        return await probeScheduler.ProbeAndRecordAsync(
            options.CurrentValue.Callsign, normalized, port, ct,
            reason: "operator-triggered probe (MCP)");
    }

    [McpServerTool(Name = "run_probe_sweep")]
    [Description(
        "Run a full B6.1 probe sweep across every eligible target (manual neighbours + AGW-bearer discovered " +
        "peers + transitive 'via:CALLSIGN' candidates, less opt-outs). Same code path the scheduler runs on " +
        "its cadence. Per-probe airtime-budget gating still applies - if the budget is exhausted mid-sweep " +
        "the remaining probes are skipped. Returns once the sweep completes.")]
    public async Task<string> RunProbeSweepAsync(CancellationToken ct)
    {
        await probeScheduler.SweepAsync(options.CurrentValue, ct);
        return "probe sweep completed";
    }

    [McpServerTool(Name = "run_poll")]
    [Description(
        "Poll a single callsign now (F3b - call the peer so it can send the mail it holds for us). " +
        "AGW/RHP only; UDP peers can't be polled. Looks up the peer's bearer port the same way as " +
        "run_probe. Mostly useful when an operator suspects a peer has mail queued for us and no session " +
        "between the two has come up since.")]
    public async Task<DbPolledNode> RunPollAsync(
        [Description("Target DAPPS callsign, case-insensitive (e.g. 'M0LTE-9').")] string callsign,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            throw new ArgumentException("callsign is required", nameof(callsign));
        var normalized = callsign.Trim().ToUpperInvariant();
        var (port, hasRoute) = await ResolveAgwPortAsync(normalized);
        if (!hasRoute)
        {
            throw new InvalidOperationException(
                $"No AGW route to {normalized} - add a /Neighbours row first.");
        }
        return await pollScheduler.PollAndRecordAsync(
            options.CurrentValue.Callsign, normalized, port, ct,
            reason: "operator-triggered poll (MCP)");
    }

    [McpServerTool(Name = "run_poll_sweep")]
    [Description(
        "Run a full F3b poll sweep across every AGW-reachable manual neighbour. UDP-only neighbours are " +
        "excluded (there's no session to poll over). Opt-out flags are honoured.")]
    public async Task<string> RunPollSweepAsync(CancellationToken ct)
    {
        await pollScheduler.SweepAsync(options.CurrentValue, ct);
        return "poll sweep completed";
    }

    [McpServerTool(Name = "probe_via_nodecall")]
    [Description(
        "Plan B6.1 Phase 2b - probe a BPQ NODECALL (not a DAPPS application callsign) by connecting " +
        "to the node prompt, typing an application command (default 'DAPPS') + CR, and waiting for the " +
        "DAPPSv1> handshake. Banner detection is heuristic: read until the wire goes idle for 500 ms, " +
        "treat that as 'prompt waiting for input', then send the command. Banner-text-agnostic - works " +
        "for any BPQ-style prompt. Use this when the operator knows a remote BPQ has DAPPS configured " +
        "as an APPLICATION but you only know the NODECALL, not the DAPPS callsign. The peers query then " +
        "returns the DAPPS peers the remote knows about, which the agent can run_probe directly.")]
    public async Task<NodeProber.ProbeResult> ProbeViaNodeCallAsync(
        [Description("Target BPQ NODECALL, case-insensitive (e.g. 'GB7RDG', 'M0LTE'). NOT the DAPPS application callsign.")]
        string nodeCall,
        [Description("bearer port (0-indexed) to use for the connect.")]
        int bearerPort,
        CancellationToken ct,
        [Description("Application command to type at the node prompt. Default 'DAPPS' - operators with a different APPLICATIONS= name need to override.")]
        string applicationCommand = "DAPPS")
    {
        if (string.IsNullOrWhiteSpace(nodeCall))
            throw new ArgumentException("nodeCall is required", nameof(nodeCall));
        return await prober.ProbeViaNodeCallAsync(
            options.CurrentValue.Callsign,
            nodeCall.Trim().ToUpperInvariant(),
            bearerPort,
            ct,
            applicationCommand: applicationCommand,
            fetchPeers: true);
    }

    [McpServerTool(Name = "run_solicit")]
    [Description(
        "Fire a one-shot 'who's there?' solicit on a discovery channel (B6.2). Reachable peers reply with " +
        "their normal beacon after a small random delay; replies populate DbDiscoveredPeer via the standard " +
        "beacon path. The bearer must be currently running - the channel had to be enabled at boot. Useful " +
        "during HF testing when scheduled beacons may have missed a propagation window.")]
    public async Task<string> RunSolicitAsync(
        [Description("Bearer name: 'agw' or 'udp'.")] string bearer,
        [Description("Channel key - bearer port stringified for AGW (e.g. '0'), or multicast endpoint for UDP (e.g. '239.0.0.1:54321').")] string channelKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearer)) throw new ArgumentException("bearer is required", nameof(bearer));
        if (string.IsNullOrWhiteSpace(channelKey)) throw new ArgumentException("channelKey is required", nameof(channelKey));
        await discovery.SolicitAsync(bearer.Trim().ToLowerInvariant(), channelKey.Trim(), ct);
        return $"solicit emitted on {bearer}/{channelKey}";
    }

    [McpServerTool(Name = "send_test_message")]
    [Description(
        "Submit an outbound message to the queue. Same code path as the dashboard's send-test form. The " +
        "originator is automatically set to this node's callsign. Payload is sent as UTF-8 bytes; if you " +
        "need binary, use the REST /AppApi/outbound endpoint instead. Returns the assigned message id; if the " +
        "payload exceeds SystemOptions.FragmentThresholdBytes (default 4096) the message is split into N " +
        "F2 fragment rows and the master id is returned.")]
    public async Task<string> SendTestMessageAsync(
        [Description("App name on the destination node, e.g. 'chat'.")] string app,
        [Description("Destination callsign with optional SSID, e.g. 'M0LTE-1'.")] string destinationCallsign,
        [Description("Payload as UTF-8 text.")] string payload,
        [Description("Optional residual TTL in seconds. Null means no expiry; the message persists in the queue indefinitely.")]
        int? ttlSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("app is required", nameof(app));
        if (string.IsNullOrWhiteSpace(destinationCallsign)) throw new ArgumentException("destinationCallsign is required", nameof(destinationCallsign));
        if (payload is null) throw new ArgumentException("payload is required", nameof(payload));
        return await database.SubmitOutboundMessage(
            app.Trim(),
            destinationCallsign.Trim().ToUpperInvariant(),
            Encoding.UTF8.GetBytes(payload),
            ttlSeconds);
    }

    /// <summary>Mirrors the precedence the scheduler + REST controllers
    /// use: explicit neighbour > AGW-bearer discovered peer > the
    /// configured DefaultBearerPort. (port, true) when at least one
    /// surface knew about the callsign; (default, false) when the
    /// callsign is a stranger to us - caller must surface that as an
    /// error rather than blindly probing the default port.</summary>
    private async Task<(int Port, bool HasRoute)> ResolveAgwPortAsync(string callsign)
    {
        var neighbour = await database.GetNeighbour(callsign);
        if (neighbour is not null && neighbour.UdpEndpoint is null)
        {
            return (neighbour.BearerPort ?? options.CurrentValue.DefaultBearerPort, true);
        }
        var peers = await database.GetDiscoveredPeers();
        var match = peers.FirstOrDefault(p =>
            string.Equals(p.Bearer, "agw", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return (match.BearerPort ?? options.CurrentValue.DefaultBearerPort, true);
        }
        return (options.CurrentValue.DefaultBearerPort, false);
    }
}
