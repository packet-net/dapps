using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST surface over the transmission audit log. Used by the
/// dashboard's Transmissions panel and any external scraper that
/// prefers a poll over an MQTT subscribe.
///
/// Open access (allowlisted past <see cref="AdminAuthMiddleware"/>?
/// Currently behind the admin cookie like the rest of the dashboard's
/// REST surface; the MQTT publish stream gives operators an unauth
/// scrape path if they want it.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class TransmissionsController(TransmissionAuditService audit) : ControllerBase
{
    /// <summary>
    /// Recent transmissions, newest first. Optional filters:
    /// <c>kind</c> (one of beacon / solicit / solicit-reply / probe /
    /// probe-nodeprompt / forward / forward-flood / poll / ack / nak /
    /// heartbeat) - may repeat to OR multiple kinds;
    /// <c>target</c> for a specific callsign; <c>onlyFailures</c>
    /// to surface error rows.
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<DbTransmission>> Get(
        [FromQuery] int limit = 200,
        [FromQuery] string[]? kind = null,
        [FromQuery] string? target = null,
        [FromQuery] bool? onlyFailures = null)
    {
        var kinds = kind is { Length: > 0 } ? kind : null;
        var success = onlyFailures == true ? false : (bool?)null;
        return await audit.ListRecentAsync(limit, kinds, target, success);
    }
}
