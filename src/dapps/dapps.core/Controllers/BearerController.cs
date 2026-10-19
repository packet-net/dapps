using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Bearer-introspection endpoints for the dashboard. Today: AGW port
/// list, used by the Topology page so an operator picks a labelled
/// port from a dropdown ("Port 1 - VHF FM 1200 baud"), numbered as the
/// node numbers it (#233), when adding a neighbour or a discovery channel.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class BearerController(
    AgwPortQuery agwPortQuery,
    IOptionsMonitor<SystemOptions> options,
    ILogger<BearerController> logger) : ControllerBase
{
    /// <summary>
    /// Ask BPQ via AGW <c>'G'</c> for the list of configured radio
    /// ports. Returns 503 with a useful message when the configured
    /// bearer isn't AGW or BPQ can't be reached - the dashboard JS
    /// shows the message inline so the operator knows why the port
    /// dropdown is empty.
    /// </summary>
    [HttpGet("agw/ports")]
    public async Task<IActionResult> GetAgwPorts(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!string.Equals(opts.NodeBearer, "agw", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(503, "Bearer is not AGW; switch the node bearer to AGW in Settings to use port queries.");
        }
        try
        {
            var ports = await agwPortQuery.QueryAsync(opts.NodeHost, opts.AgwPort, ct);
            return Ok(new AgwPortListModel(ports, agwPortQuery.LastWarning));
        }
        catch (TimeoutException ex)
        {
            return StatusCode(504, $"AGW port query timed out: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AGW port query failed for {0}:{1}", opts.NodeHost, opts.AgwPort);
            return StatusCode(503, $"AGW port query failed: {ex.Message}");
        }
    }
}

/// <summary>The node's AGW ports, each with its own number for it
/// (<see cref="AgwPortInfo.Number"/>), and what is wrong with its port
/// numbering, if anything (hidden BPQ ports, say).</summary>
public sealed record AgwPortListModel(IReadOnlyList<AgwPortInfo> Ports, string? Warning);
