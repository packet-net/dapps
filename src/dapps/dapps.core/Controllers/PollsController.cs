using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Plan F3b - scheduled-poll REST surface. Mirrors
/// <see cref="ProbesController"/> in shape: list state, sweep all
/// now, poll one now, set opt-out, forget row.
/// </summary>
[ApiController]
[Route("[controller]")]
public class PollsController(
    Database database,
    PollSchedulerService scheduler,
    IOptionsMonitor<SystemOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<PolledNodeModel>> List()
    {
        var rows = await database.GetPolledNodes();
        return rows.Select(ToModel);
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunSweep(CancellationToken ct)
    {
        await scheduler.SweepAsync(options.CurrentValue, ct);
        return NoContent();
    }

    [HttpPost("run/{callsign}")]
    public async Task<ActionResult<PolledNodeModel>> RunOne(string callsign, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return BadRequest("Callsign is required");
        var normalized = callsign.Trim().ToUpperInvariant();

        var (port, hasRoute) = await ResolvePort(normalized);
        if (!hasRoute)
        {
            return BadRequest(
                $"No AGW route to {normalized} - add a /Neighbours row first.");
        }
        if (port < 1)
        {
            return Conflict(
                $"{normalized}'s radio port was saved by an older DAPPS and is waiting for the node's AGW connection to answer, to be converted to the node's own port number; nothing is sent on it until then.");
        }

        var row = await scheduler.PollAndRecordAsync(
            options.CurrentValue.Callsign, normalized, port, ct,
            reason: "operator-triggered poll (REST)");
        return ToModel(row);
    }

    [HttpPost("{callsign}/optout")]
    public async Task<IActionResult> SetOptOut(string callsign, [FromBody] OptOutBody body)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return BadRequest("Callsign is required");
        var normalized = callsign.Trim().ToUpperInvariant();
        var row = await database.GetPolledNode(normalized) ?? new DbPolledNode { Callsign = normalized };
        row.OptOut = body.OptOut;
        await database.UpsertPolledNode(row);
        return NoContent();
    }

    [HttpDelete("{callsign}")]
    public async Task<IActionResult> Forget(string callsign)
    {
        var removed = await database.RemovePolledNode(callsign.Trim().ToUpperInvariant());
        return removed ? NoContent() : NotFound();
    }

    /// <summary>Resolve the bearer port for an on-demand poll. Only
    /// considers manual neighbours (UDP-only excluded - F3 is AGW-only
    /// by design).</summary>
    private async Task<(int Port, bool HasRoute)> ResolvePort(string callsign)
    {
        var neighbour = await database.GetNeighbour(callsign);
        if (neighbour is not null && neighbour.UdpEndpoint is null)
        {
            // 0 while the port is held (saved by an older DAPPS, #233).
            return (Routing.RouteBuilder.PortOf(neighbour, options.CurrentValue.DefaultPort) ?? 0, true);
        }
        return (options.CurrentValue.DefaultPort, false);
    }

    private static PolledNodeModel ToModel(DbPolledNode r) => new(
        Callsign: r.Callsign,
        LastPolledAt: r.LastPolledAt,
        LastSuccessAt: r.LastSuccessAt,
        LastError: r.LastError,
        ConsecutiveFailures: r.ConsecutiveFailures,
        MessagesDrained: r.MessagesDrained,
        OptOut: r.OptOut);
}

public sealed record PolledNodeModel(
    string Callsign,
    DateTime? LastPolledAt,
    DateTime? LastSuccessAt,
    string LastError,
    int ConsecutiveFailures,
    long MessagesDrained,
    bool OptOut);
