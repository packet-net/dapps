using System.ComponentModel;
using dapps.core.Models;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-B - operator-supervised config setters. A single
/// <c>update_config</c> tool that takes a partial: each field is
/// nullable, null = no change. Fewer tools than one-setter-per-knob,
/// type-safe (the LLM can't pass garbage that gets stringly-coerced),
/// and the LLM can introspect current state via
/// <see cref="DappsHealthTools"/>'s <c>get_system_options</c> first.
///
/// Risky knobs (Callsign, NodeHost, AgwPort, MqttPort, UdpListenPort)
/// are intentionally excluded - they need a daemon restart and are
/// the kinds of mistakes that take a node off the air. The dashboard's
/// /Config form is the right surface for those; an MCP agent that
/// confidently rewrites them on a Saturday at 02:00 is a worse outcome
/// than asking the operator to type `vim` once.
/// </summary>
[McpServerToolType]
public sealed class DappsConfigTools(SystemOptionsStore optionsStore)
{
    [McpServerTool(Name = "update_config")]
    [Description(
        "Apply a partial update to SystemOptions. Each field is optional - pass only the keys you want to " +
        "change. Type-safe: malformed values are rejected. Excluded for safety: Callsign, NodeHost, AgwPort, " +
        "MqttPort, UdpListenPort (all need a daemon restart and a wrong value can take the node off-air; " +
        "use the /Config dashboard form for those). Returns the resolved SystemOptions row after the merge.")]
    public async Task<SystemOptions> UpdateConfigAsync(ConfigUpdate update)
    {
        var current = optionsStore.CurrentValue;

        if (update.ProbingEnabled.HasValue) current.ProbingEnabled = update.ProbingEnabled.Value;
        if (update.ProbeIntervalHours.HasValue) current.ProbeIntervalHours = ClampPositive(update.ProbeIntervalHours.Value);
        if (!string.IsNullOrEmpty(update.ProbeStrategy))
        {
            if (!Enum.TryParse<ProbeStrategy>(update.ProbeStrategy, ignoreCase: true, out var ps))
                throw new ArgumentException($"ProbeStrategy must be one of: {string.Join(", ", Enum.GetNames<ProbeStrategy>())}");
            current.ProbeStrategy = ps;
        }
        if (update.ProbeOvernightStartHour.HasValue) current.ProbeOvernightStartHour = ClampHour(update.ProbeOvernightStartHour.Value);
        if (update.ProbeOvernightEndHour.HasValue) current.ProbeOvernightEndHour = ClampHour(update.ProbeOvernightEndHour.Value);
        if (update.ProbeQuietWindowSeconds.HasValue) current.ProbeQuietWindowSeconds = ClampPositive(update.ProbeQuietWindowSeconds.Value);
        if (update.ScheduledPollEnabled.HasValue) current.ScheduledPollEnabled = update.ScheduledPollEnabled.Value;
        if (update.PollIntervalHours.HasValue) current.PollIntervalHours = ClampPositive(update.PollIntervalHours.Value);
        if (update.OpportunisticPollEnabled.HasValue) current.OpportunisticPollEnabled = update.OpportunisticPollEnabled.Value;
        if (update.CompressionEnabled.HasValue) current.CompressionEnabled = update.CompressionEnabled.Value;
        if (update.SessionTailSeconds.HasValue) current.SessionTailSeconds = Math.Clamp(update.SessionTailSeconds.Value, 0, 600);
        if (update.HeartbeatEnabled.HasValue) current.HeartbeatEnabled = update.HeartbeatEnabled.Value;
        if (update.HeartbeatIntervalSeconds.HasValue) current.HeartbeatIntervalSeconds = Math.Max(10, update.HeartbeatIntervalSeconds.Value);
        if (update.DiscoveryAirtimeBudgetSecondsPerHour.HasValue) current.DiscoveryAirtimeBudgetSecondsPerHour = Math.Max(0, update.DiscoveryAirtimeBudgetSecondsPerHour.Value);
        if (!string.IsNullOrEmpty(update.RoutingAlgorithm))
        {
            var algo = update.RoutingAlgorithm.Trim().ToLowerInvariant();
            if (algo is not ("passive-flood" or "meshcore"))
                throw new ArgumentException("RoutingAlgorithm must be 'passive-flood' or 'meshcore'.");
            current.RoutingAlgorithm = algo;
        }
        if (update.FragmentThresholdBytes.HasValue) current.FragmentThresholdBytes = Math.Max(0, update.FragmentThresholdBytes.Value);
        if (update.FragmentReassemblyTimeoutSeconds.HasValue) current.FragmentReassemblyTimeoutSeconds = ClampPositive(update.FragmentReassemblyTimeoutSeconds.Value);
        if (update.AuthRequired.HasValue) current.AuthRequired = update.AuthRequired.Value;
        if (update.UpdateCheckEnabled.HasValue) current.UpdateCheckEnabled = update.UpdateCheckEnabled.Value;
        if (update.DefaultBearerPort.HasValue) current.DefaultBearerPort = Math.Max(0, update.DefaultBearerPort.Value);
        if (update.AutoDiscoverViaNodeCall.HasValue) current.AutoDiscoverViaNodeCall = update.AutoDiscoverViaNodeCall.Value;
        if (!string.IsNullOrEmpty(update.NodePromptApplicationCommand))
            current.NodePromptApplicationCommand = update.NodePromptApplicationCommand.Trim();

        await optionsStore.SaveAsync(current);
        return current;
    }

    private static int ClampPositive(int v) => v > 0 ? v : 1;
    private static int ClampHour(int v) => v is >= 0 and <= 23 ? v : 0;
}

/// <summary>
/// Partial-update payload for <see cref="DappsConfigTools.UpdateConfigAsync"/>.
/// Every field is nullable; null = leave alone. Same field names as
/// <see cref="SystemOptions"/> for least-surprise.
/// </summary>
public sealed record ConfigUpdate(
    [property: Description("B6.1 - turn the connected-mode probe scheduler on/off.")]
    bool? ProbingEnabled = null,
    [property: Description("B6.1 - hours between full probe sweeps when ProbingEnabled. >=1.")]
    int? ProbeIntervalHours = null,
    [property: Description("B7 - probe-scheduling strategy. One of: 'FixedInterval', 'Overnight', 'WhenQuiet'.")]
    string? ProbeStrategy = null,
    [property: Description("B7 - local-time hour the Overnight strategy's window opens (0-23, default 2).")]
    int? ProbeOvernightStartHour = null,
    [property: Description("B7 - local-time hour the Overnight strategy's window closes (0-23, default 6). If end<start the window straddles midnight.")]
    int? ProbeOvernightEndHour = null,
    [property: Description("B7 - seconds of forwarder-quiet required before the WhenQuiet strategy fires. >=1.")]
    int? ProbeQuietWindowSeconds = null,
    [property: Description("F3b - turn the scheduled rev-poll sweeper on/off.")]
    bool? ScheduledPollEnabled = null,
    [property: Description("F3b - hours between full poll sweeps when ScheduledPollEnabled. >=1.")]
    int? PollIntervalHours = null,
    [property: Description("F3a - opportunistic poll-on-push. Drains a peer's queued mail at the end of every push session. Default true.")]
    bool? OpportunisticPollEnabled = null,
    [property: Description("Compress message payloads on DAPPSv1 sessions (AGW/RHP neighbours) when it saves bytes and the neighbour supports it. Sender-side only - incoming compressed messages are always accepted. A neighbour's own CompressionEnabled override wins when set. Default true.")]
    bool? CompressionEnabled = null,
    [property: Description("After a session with a neighbour that moved messages, keep the link open until it has been idle this many seconds, so follow-up messages in either direction go straight away without a new connection. 0 turns it off. Clamped to 0-600 (many nodes drop an idle circuit after 10 minutes). A neighbour's own SessionTailSeconds override wins when set. Default 120.")]
    int? SessionTailSeconds = null,
    [property: Description("C3 PR-B - periodic MQTT heartbeat publish to dapps/metrics/heartbeat. Default true.")]
    bool? HeartbeatEnabled = null,
    [property: Description("C3 PR-B - seconds between heartbeat publishes (>=10, default 60).")]
    int? HeartbeatIntervalSeconds = null,
    [property: Description("B7 - global trailing-hour airtime cap in seconds for ALL discovery-class transmissions (beacons + solicits + probes). 0 = unlimited.")]
    int? DiscoveryAirtimeBudgetSecondsPerHour = null,
    [property: Description("B5/B5.1 - routing algorithm: 'passive-flood' (default, AODV-style) or 'meshcore' (DSR-style source routing). Restart required to take effect.")]
    string? RoutingAlgorithm = null,
    [property: Description("F2 - payloads strictly larger than this byte count get fragmented at submit. 0 disables fragmentation.")]
    int? FragmentThresholdBytes = null,
    [property: Description("F2 - drop incomplete reassembly buffer rows older than this many seconds. Default 7 days.")]
    int? FragmentReassemblyTimeoutSeconds = null,
    [property: Description("A4 - when true, MQTT/REST app-interface clients must present a per-app token.")]
    bool? AuthRequired = null,
    [property: Description("C5.1 - when true, periodically check GitHub Releases and surface available updates on the dashboard.")]
    bool? UpdateCheckEnabled = null,
    [property: Description("Default bearer port (0-indexed) used when originating to a neighbour with no explicit BearerPort set.")]
    int? DefaultBearerPort = null,
    [property: Description("B6.1 Phase 2b - when true, AGW DAPPS beacons auto-seed node-prompt-probe candidates for the BASE callsign of the source. Off by default.")]
    bool? AutoDiscoverViaNodeCall = null,
    [property: Description("B6.1 Phase 2b - application command typed at the BPQ node prompt to enter the DAPPS slot. Default 'DAPPS'; override for operators using a different APPLICATIONS= name.")]
    string? NodePromptApplicationCommand = null);
