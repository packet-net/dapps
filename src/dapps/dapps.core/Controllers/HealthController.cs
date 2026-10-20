using System.Diagnostics;
using dapps.client;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C3 - lightweight liveness endpoint distinct from
/// <c>/Events/health</c> (the metrics dump). HTTP 200 when every
/// critical dependency is up; 503 with details when degraded so
/// systemd watchdog units / external uptime monitors / load balancers
/// have a clean signal to act on.
///
/// Definition of healthy:
/// <list type="bullet">
/// <item><description>Callsign is configured (not the placeholder) and valid.</description></item>
/// <item><description>The configured packet node is reachable on its bearer port (AGW or RHPv2 depending on <see cref="SystemOptions.NodeBearer"/>).</description></item>
/// <item><description>The MQTT broker is bound on the configured port.</description></item>
/// </list>
/// All three are degradations the operator wants to know about
/// immediately. Pending-outbound count and last-forward-success
/// timestamp are surfaced for context but don't affect status.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class HealthController(
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    Database database,
    TcpReachabilityProbe probe) : ControllerBase
{
    private const string PlaceholderCallsign = "N0CALL";

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var opts = options.CurrentValue;

        var callsignOk = !string.IsNullOrWhiteSpace(opts.Callsign)
            && !string.Equals(opts.Callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        var callsignProblem = callsignOk ? MessageFields.CallsignError(opts.Callsign) : null;

        var nodePort = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase)
            ? (opts.RhpPort > 0 ? opts.RhpPort : 9000)
            : opts.AgwPort;
        var nodeOk = await probe.IsReachableAsync(opts.NodeHost, nodePort, TimeSpan.FromMilliseconds(500));
        var mqttOk = await probe.IsReachableAsync("127.0.0.1", opts.MqttPort, TimeSpan.FromMilliseconds(250));

        var pendingOutbound = await database.CountPendingOutbound();

        var status = (callsignOk && callsignProblem is null && nodeOk && mqttOk) ? "healthy" : "degraded";
        var body = new HealthResponse(
            Status: status,
            Callsign: opts.Callsign,
            Version: Services.AppVersion.Current(),
            UptimeSeconds: (long)Math.Max(0, (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds),
            CallsignConfigured: callsignOk,
            CallsignProblem: callsignProblem,
            NodeReachable: nodeOk,
            MqttBrokerUp: mqttOk,
            LastForwardSuccessAt: metrics.LastForwardSuccessAt,
            PendingOutboundCount: pendingOutbound);

        return status == "healthy" ? Ok(body) : StatusCode(503, body);
    }
}

public sealed record HealthResponse(
    string Status,
    string Callsign,
    string Version,
    long UptimeSeconds,
    bool CallsignConfigured,
    string? CallsignProblem,
    bool NodeReachable,
    bool MqttBrokerUp,
    DateTime? LastForwardSuccessAt,
    int PendingOutboundCount);
