using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Dashboard / API surface for the MeshCore bearer (#159): a read-only status
/// snapshot and an operator device-control reset. Configuration itself is edited
/// through the Settings form (persisted via <c>/Config</c> into SystemOptions).
/// </summary>
[ApiController]
[Route("[controller]")]
public class MeshCoreController(MeshCoreBearer bearer) : ControllerBase
{
    [HttpGet("status")]
    public MeshCoreStatus Status() => bearer.GetStatus();

    /// <summary>Force the radio through a hard reset + reconfigure.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
        => await bearer.ResetRadioAsync(ct) ? Ok() : StatusCode(503, "meshcore bearer not running");
}
