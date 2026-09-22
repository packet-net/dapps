using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C3 - operator-facing aggregate observability surface,
/// distinct from the dashboard's <c>/Events/*</c> endpoints
/// (which are tightly coupled to the dashboard's UI shape and
/// poll cadence).
///
/// <list type="bullet">
/// <item><c>GET /Operational</c> - top-level snapshot: liveness, all
///   counters, queue / peer / channel counts, trailing-hour airtime,
///   last-20 recent events. Same JSON shape as the periodic MQTT
///   heartbeat publish on <c>dapps/metrics/heartbeat</c>.</item>
/// <item><c>GET /Operational/recent</c> - the full last-100 ring as
///   JSON for ad-hoc scrapers / curl-grep workflows. Smaller body
///   than the full snapshot when you just want the event tail.</item>
/// <item><c>POST /Operational/retry-now</c> - collapses the currently
///   configured bearer's reconnect backoff wait so it retries
///   immediately. Drives the dashboard's manual "Retry now" button.</item>
/// </list>
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class OperationalController(
    OperationalMetrics metrics,
    OperationalSnapshotBuilder snapshotBuilder,
    IOptionsMonitor<SystemOptions> options,
    AgwInboundService agwInbound,
    Rhpv2InboundService rhpv2Inbound) : ControllerBase
{
    /// <summary>
    /// <c>?full=true</c> includes the per-row tables the dashboard
    /// renders (outbound queue, local inbox, dropped, neighbours,
    /// probed/polled nodes, discovered peers, channels, update card).
    /// Default is the lighter heartbeat shape - excludes per-row data
    /// to keep external scrapers' bandwidth small.
    /// </summary>
    [HttpGet]
    public Task<OperationalSnapshot> Get([FromQuery] bool full, CancellationToken ct)
        => full ? snapshotBuilder.BuildFullAsync(ct) : snapshotBuilder.BuildAsync(ct);

    [HttpGet("recent")]
    public IReadOnlyList<OperationalMetrics.OperationalEvent> GetRecent()
        => metrics.Take().RecentEvents;

    /// <summary>
    /// Manual "retry now": whichever bearer is currently configured
    /// (<see cref="SystemOptions.NodeBearer"/>) collapses its backoff
    /// wait, if one is in flight, so the next connect attempt happens
    /// immediately instead of at the scheduled time. Returns
    /// <c>{ triggered: false }</c> rather than an error when there's
    /// nothing to collapse (already connected, idle-gated on missing
    /// config, or a connect attempt is already underway) - the operator
    /// gets an immediate reconnect either way on the next snapshot poll.
    /// </summary>
    [HttpPost("retry-now")]
    public IActionResult RetryNow()
    {
        var isRhpv2 = string.Equals(options.CurrentValue.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase);
        var triggered = isRhpv2 ? rhpv2Inbound.TriggerManualRetry() : agwInbound.TriggerManualRetry();
        return Ok(new { triggered });
    }
}
