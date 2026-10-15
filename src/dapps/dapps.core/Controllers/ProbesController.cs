using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Plan B6.1 - connected-mode probe-and-map REST surface.
///
/// <list type="bullet">
/// <item><c>GET /Probes</c> - current state of every probed-node row.</item>
/// <item><c>POST /Probes/run</c> - kick a full sweep now (off-cadence).</item>
/// <item><c>POST /Probes/run/{callsign}</c> - probe one callsign now.
///   Unlike the scheduled sweep, on-demand probes go through even when
///   the row is opt-out - a sysop wants the option to test a specific
///   peer without un-flipping the opt-out toggle.</item>
/// <item><c>POST /Probes/{callsign}/optout</c> - set/clear opt-out.</item>
/// <item><c>DELETE /Probes/{callsign}</c> - forget a row entirely
///   (next sweep will recreate it for non-opted-out targets).</item>
/// </list>
/// </summary>
[ApiController]
[Route("[controller]")]
public class ProbesController(
    Database database,
    ProbeSchedulerService scheduler,
    IOptionsMonitor<SystemOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ProbedNodeModel>> List()
    {
        var rows = await database.GetProbedNodes();
        return rows.Select(ToModel);
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunSweep(CancellationToken ct)
    {
        await scheduler.SweepAsync(options.CurrentValue, ct);
        return NoContent();
    }

    [HttpPost("run/{callsign}")]
    public async Task<ActionResult<ProbedNodeModel>> RunOne(string callsign, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return BadRequest("Callsign is required");
        var normalized = callsign.Trim().ToUpperInvariant();

        var (port, hasRoute) = await scheduler.ResolveProbePortAsync(normalized);
        if (!hasRoute)
        {
            return BadRequest(
                $"No AGW route to {normalized} - add a /Neighbours row, wait for a beacon, or probe a neighbour that knows it.");
        }

        var row = await scheduler.ProbeAndRecordAsync(
            options.CurrentValue.Callsign, normalized, port, ct,
            reason: "operator-triggered probe (REST)");
        return ToModel(row);
    }

    [HttpPost("{callsign}/optout")]
    public async Task<IActionResult> SetOptOut(string callsign, [FromBody] OptOutBody body)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return BadRequest("Callsign is required");
        var normalized = callsign.Trim().ToUpperInvariant();

        var row = await database.GetProbedNode(normalized) ?? new DbProbedNode { Callsign = normalized };
        row.OptOut = body.OptOut;
        await database.UpsertProbedNode(row);
        return NoContent();
    }

    [HttpDelete("{callsign}")]
    public async Task<IActionResult> Forget(string callsign)
    {
        var removed = await database.RemoveProbedNode(callsign.Trim().ToUpperInvariant());
        return removed ? NoContent() : NotFound();
    }

    private static ProbedNodeModel ToModel(DbProbedNode r) => new(
        Callsign: r.Callsign,
        LastBearerPort: r.LastBearerPort,
        LastProbedAt: r.LastProbedAt,
        LastSuccessAt: r.LastSuccessAt,
        LastError: r.LastError,
        ConsecutiveFailures: r.ConsecutiveFailures,
        SuccessCount: r.SuccessCount,
        OptOut: r.OptOut,
        Source: string.IsNullOrEmpty(r.Source) ? "neighbour" : r.Source);
}

/// <summary>Wire shape returned by the /Probes endpoints. Mirrors
/// <see cref="DbProbedNode"/> 1:1 - keeping a separate model lets the
/// row evolve without breaking the wire shape.</summary>
public sealed record ProbedNodeModel(
    string Callsign,
    int? LastBearerPort,
    DateTime? LastProbedAt,
    DateTime? LastSuccessAt,
    string LastError,
    int ConsecutiveFailures,
    int SuccessCount,
    bool OptOut,
    string Source);

/// <summary>Body for <c>POST /Probes/{callsign}/optout</c>.</summary>
public sealed record OptOutBody(bool OptOut);
