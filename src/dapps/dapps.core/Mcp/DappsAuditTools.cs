using System.ComponentModel;
using dapps.core.Models;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Read-only access to the transmission audit log. Every outbound
/// transmission (beacon, solicit, probe, forward, poll, ack, heartbeat)
/// is recorded with its <c>Reason</c> field describing the "why".
/// Useful for an assistant doing post-mortems ("which probe sweep last
/// touched the route to GB7XYZ before message abc1234 went missing?")
/// or compliance-shaped audits ("what did this node put on the air last
/// Tuesday between 02:00 and 03:00?").
/// </summary>
[McpServerToolType]
public sealed class DappsAuditTools(TransmissionAuditService audit)
{
    [McpServerTool(Name = "list_transmissions")]
    [Description(
        "Tail of the transmissions audit log, newest first. Each row records: when, kind " +
        "(beacon / solicit / solicit-reply / probe / probe-nodeprompt / forward / forward-flood / " +
        "poll / ack / nak / heartbeat), bearer (agw / udp / mqtt), channel-key, target " +
        "callsign (for directed sends), message id (when forwarding a specific message), bytes, " +
        "duration in ms, success bool, reason ('why' string, e.g. 'scheduled probe sweep' / " +
        "'operator-triggered probe (MCP)'), and an error tag on failure. Use this to trace what the " +
        "node has actually transmitted, not just what it intended to. Filter narrowly when looking " +
        "for a specific failure mode.")]
    public async Task<IReadOnlyList<DbTransmission>> ListTransmissionsAsync(
        [Description("Max rows to return (1-500, default 100).")] int limit = 100,
        [Description("Filter by one or more kinds. Comma-separated, e.g. 'probe,probe-nodeprompt'. Empty = all kinds.")] string kinds = "",
        [Description("Filter by target callsign (exact match incl. SSID). Empty = all targets.")] string target = "",
        [Description("When true, surface only failed transmissions. Useful for diagnosing a node that's not getting through.")] bool onlyFailures = false)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;

        var kindList = string.IsNullOrWhiteSpace(kinds)
            ? null
            : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return await audit.ListRecentAsync(
            limit: limit,
            kinds: kindList,
            targetCallsign: string.IsNullOrWhiteSpace(target) ? null : target,
            successOnly: onlyFailures ? false : null);
    }
}
