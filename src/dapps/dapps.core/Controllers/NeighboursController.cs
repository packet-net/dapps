using dapps.client;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST surface for the neighbour table - the list of remote DAPPS nodes
/// this instance is willing to forward through. A sysop maintains it by
/// hand today; the auto-discovery work in Phase B feeds the same table.
///
/// Callsign is the natural key. POST is upsert (re-POSTing changes the
/// bearer port without erroring), DELETE is idempotent (404 only when there
/// was nothing to delete).
/// </summary>
[ApiController]
[Route("[controller]")]
public class NeighboursController(Database database) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<NeighbourModel>> List()
    {
        var rows = await database.GetNeighbours();
        return rows.Select(n =>
        {
            // Hand the dashboard a textarea-shaped script representation
            // (round-trip with ParseLines/ToLines) instead of the raw
            // JSON, since that's what the operator typed in.
            var script = ConnectScript.FromJson(n.ConnectScriptJson);
            return new NeighbourModel(n.Callsign, n.BearerPort, n.UdpEndpoint,
                ConnectScript: script?.ToLines(),
                ConnectScriptStepCount: script?.Steps.Count ?? 0,
                CompressionEnabled: n.CompressionEnabled,
                SessionTailSeconds: n.SessionTailSeconds);
        });
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] NeighbourModel neighbour)
    {
        if (string.IsNullOrWhiteSpace(neighbour.Callsign))
        {
            return BadRequest("Callsign is required");
        }
        string? scriptJson = null;
        if (!string.IsNullOrWhiteSpace(neighbour.ConnectScript))
        {
            ConnectScript? parsed;
            try { parsed = ConnectScript.ParseLines(neighbour.ConnectScript); }
            catch (FormatException ex) { return BadRequest("Connect script: " + ex.Message); }
            if (parsed is not null && !parsed.EndsOnDappsPrompt)
            {
                return BadRequest("Connect script: final step's expect must contain 'DAPPSv1>' (the DAPPS prompt is what the protocol client takes over from)");
            }
            scriptJson = parsed?.ToJson();
        }
        await database.UpsertNeighbour(
            neighbour.Callsign.Trim().ToUpperInvariant(),
            neighbour.BearerPort,
            string.IsNullOrWhiteSpace(neighbour.UdpEndpoint) ? null : neighbour.UdpEndpoint.Trim(),
            connectScriptJson: scriptJson,
            compressionEnabled: neighbour.CompressionEnabled,
            sessionTailSeconds: neighbour.SessionTailSeconds is { } tail ? Math.Clamp(tail, 0, 600) : null);
        return NoContent();
    }

    [HttpDelete("{callsign}")]
    public async Task<IActionResult> Remove(string callsign)
    {
        var removed = await database.RemoveNeighbour(callsign.Trim().ToUpperInvariant());
        return removed ? NoContent() : NotFound();
    }
}

/// <summary>
/// Wire shape for /Neighbours. <see cref="UdpEndpoint"/> ("host:port") is
/// set when this neighbour is reachable over the UDP datagram backhaul;
/// null routes via the configured node bearer (AGW or RHPv2).
/// <see cref="BearerPort"/> is the 0-indexed bearer port; null falls back
/// to DefaultBearerPort.
///
/// <para>
/// <see cref="ConnectScript"/> is the optional human-readable
/// connect-script in the line-shaped form
/// (<c>SEND|EXPECT[|TIMEOUT_SECONDS]</c> per line). The controller
/// parses it server-side, validates it ends on the DAPPSv1 prompt,
/// and persists it as JSON. The dashboard round-trips the same text.
/// <see cref="ConnectScriptStepCount"/> is read-only - the daemon
/// fills it on GET so the dashboard can show "N steps" without
/// re-parsing.
/// </para>
///
/// <para>
/// <see cref="CompressionEnabled"/> is the per-neighbour override for
/// <see cref="dapps.core.Models.SystemOptions.CompressionEnabled"/>:
/// null defers to the system-wide setting, false never compresses to
/// this neighbour, true compresses when it saves bytes even if the
/// system setting is off.
/// </para>
///
/// <para>
/// <see cref="SessionTailSeconds"/> is the per-neighbour override for
/// <see cref="dapps.core.Models.SystemOptions.SessionTailSeconds"/>:
/// null defers to the system-wide setting, 0 never holds the link open
/// to this neighbour after a session, otherwise the number of idle
/// seconds to hold it for. Clamped 0-600 on upsert.
/// </para>
/// </summary>
public sealed record NeighbourModel(
    string Callsign,
    int? BearerPort,
    string? UdpEndpoint = null,
    string? ConnectScript = null,
    int ConnectScriptStepCount = 0,
    bool? CompressionEnabled = null,
    int? SessionTailSeconds = null);
