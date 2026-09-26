using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Operator master TX kill-switch. Backed by
/// <see cref="SystemOptions.TxEnabled"/>; toggling here persists to
/// the systemoptions table and fires <c>OnChange</c>, so every bearer
/// chokepoint sees the new state on its next call without a restart.
///
/// Sits behind <see cref="AdminAuthMiddleware"/> like every other
/// non-allowlisted controller route - the toggle requires the admin
/// cookie. <see cref="GetStatus"/> is plain-text so the dashboard's
/// JS can render the banner without parsing JSON; the POST handlers
/// return 204 because the caller refreshes the page anyway.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class TxControlController(
    SystemOptionsStore options,
    SystemOptionsBackedTxGate gate,
    TransmissionAuditService audit) : ControllerBase
{
    [HttpGet("status")]
    public TxControlStatus GetStatus() => new(
        TxAllowed: gate.TxAllowed,
        LocalAllowed: gate.LocalAllowed,
        RemoteAllowed: gate.RemoteAllowed,
        BlockReason: gate.BlockReason,
        RemoteBlockReason: gate.RemoteBlockReason);

    [HttpPost("stop")]
    public async Task<IActionResult> Stop() => await SetLocal(enabled: false);

    [HttpPost("resume")]
    public async Task<IActionResult> Resume() => await SetLocal(enabled: true);

    private async Task<IActionResult> SetLocal(bool enabled)
    {
        var current = options.CurrentValue;
        if (current.TxEnabled == enabled)
        {
            // Idempotent - the operator might double-click, or two
            // browser tabs might race. Still record an audit so the
            // log shows the intent even when the state didn't change.
            await audit.RecordAsync(
                kind: "tx-control",
                bearer: "ui",
                reason: enabled ? "operator pressed RESUME (no-op)" : "operator pressed STOP (no-op)",
                success: true);
            return RedirectToReferer();
        }

        var updated = Clone(current);
        updated.TxEnabled = enabled;
        await options.SaveAsync(updated);

        await audit.RecordAsync(
            kind: "tx-control",
            bearer: "ui",
            reason: enabled
                ? "operator pressed RESUME - TX re-enabled"
                : "operator pressed STOP - TX gated at all bearers",
            success: true);

        // The toggle is wired into the dashboard header as a normal
        // form-POST so the response should bounce the operator back
        // to wherever they came from. JS callers can ignore the body.
        return RedirectToReferer();
    }

    private IActionResult RedirectToReferer()
    {
        var referer = Request.Headers.Referer.ToString();
        return Redirect(string.IsNullOrEmpty(referer) ? $"{Request.PathBase}/" : referer);
    }

    private static SystemOptions Clone(SystemOptions s) => new()
    {
        NodeHost = s.NodeHost,
        AgwPort = s.AgwPort,
        NodeBearer = s.NodeBearer,
        RhpPort = s.RhpPort,
        RhpUser = s.RhpUser,
        RhpPass = s.RhpPass,
        DefaultBearerPort = s.DefaultBearerPort,
        Callsign = s.Callsign,
        MqttPort = s.MqttPort,
        UdpListenPort = s.UdpListenPort,
        AuthRequired = s.AuthRequired,
        UpdateCheckEnabled = s.UpdateCheckEnabled,
        RoutingAlgorithm = s.RoutingAlgorithm,
        ProbingEnabled = s.ProbingEnabled,
        ProbeIntervalHours = s.ProbeIntervalHours,
        FragmentThresholdBytes = s.FragmentThresholdBytes,
        FragmentReassemblyTimeoutSeconds = s.FragmentReassemblyTimeoutSeconds,
        RouteGossipStalenessHours = s.RouteGossipStalenessHours,
        OpportunisticPollEnabled = s.OpportunisticPollEnabled,
        CompressionEnabled = s.CompressionEnabled,
        ScheduledPollEnabled = s.ScheduledPollEnabled,
        PollIntervalHours = s.PollIntervalHours,
        DiscoveryAirtimeBudgetSecondsPerHour = s.DiscoveryAirtimeBudgetSecondsPerHour,
        ProbeStrategy = s.ProbeStrategy,
        ProbeOvernightStartHour = s.ProbeOvernightStartHour,
        ProbeOvernightEndHour = s.ProbeOvernightEndHour,
        ProbeQuietWindowSeconds = s.ProbeQuietWindowSeconds,
        HeartbeatEnabled = s.HeartbeatEnabled,
        HeartbeatIntervalSeconds = s.HeartbeatIntervalSeconds,
        AutoDiscoverViaNodeCall = s.AutoDiscoverViaNodeCall,
        NodePromptApplicationCommand = s.NodePromptApplicationCommand,
        TransmissionAuditEnabled = s.TransmissionAuditEnabled,
        TransmissionAuditRetentionDays = s.TransmissionAuditRetentionDays,
        TransmissionAuditMqttPublish = s.TransmissionAuditMqttPublish,
        TxEnabled = s.TxEnabled,
    };
}

public sealed record TxControlStatus(
    bool TxAllowed,
    bool LocalAllowed,
    bool RemoteAllowed,
    string? BlockReason,
    string? RemoteBlockReason);
