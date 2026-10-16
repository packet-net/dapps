using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Admin surface for issuing per-app credentials. POSTing returns the
/// plaintext token exactly once - capture it on creation and hand it
/// to the app owner, because we only store the hash.
///
/// Gated by <see cref="dapps.core.Services.AdminAuthMiddleware"/> like
/// the rest of the dashboard's admin surface (the operator's cookie,
/// not a bearer token) - it is deliberately not paired with
/// <see cref="dapps.core.Services.BearerAuthMiddleware"/>'s per-app
/// token check, which would be a chicken-and-egg on first use. The
/// dashboard's <c>/Tokens</c> page (<see cref="dapps.core.Pages.AppTokensModel"/>)
/// is the operator-facing front end for this; the app-developers
/// reference's Authentication section covers scripting it directly.
/// </summary>
[ApiController]
[Route("[controller]")]
public class AppTokensController(AppTokenStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<AppTokenInfo>> List()
    {
        return await store.ListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<CreateAppTokenResponse>> CreateOrRotate([FromBody] CreateAppTokenRequest req)
    {
        if (!AppTokenStore.TryNormalizeAppName(req.App, out var app, out var error))
        {
            return BadRequest(error);
        }
        var plaintext = await store.CreateOrRotateAsync(app);
        return Ok(new CreateAppTokenResponse(app, plaintext));
    }

    [HttpDelete("{app}")]
    public async Task<IActionResult> Revoke(string app)
    {
        var deleted = await store.RevokeAsync(app);
        return deleted ? NoContent() : NotFound();
    }
}

public sealed record CreateAppTokenRequest(string App);

/// <summary>
/// Returned only on POST. The <see cref="Token"/> value is plaintext;
/// the API will never return it again. Subsequent GETs surface
/// <see cref="AppTokenInfo"/> rows that omit the secret.
/// </summary>
public sealed record CreateAppTokenResponse(string App, string Token);
