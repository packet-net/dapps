using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Operator-facing CRUD for per-app MQTT/REST credentials (#316
/// follow-up). A thin Razor Pages wrapper over <see cref="AppTokenStore"/>
/// - the same mint/list/revoke logic the <c>/AppTokens</c> REST
/// endpoint uses, rendered for a human instead of returned as JSON, so
/// the hashing and rotation semantics live in exactly one place.
///
/// Gated by <see cref="AdminAuthMiddleware"/> like the rest of the
/// dashboard's admin surface - the operator's cookie, not an app
/// token. Standard Razor Pages antiforgery applies to both POST
/// handlers below (no <c>[IgnoreAntiforgeryToken]</c>, unlike
/// <see cref="SetupModel"/> and <see cref="LoginModel"/> which need to
/// work before any cookie exists).
///
/// Routed at <c>/Tokens</c>, not the file-name-inferred <c>/AppTokens</c>:
/// <see cref="dapps.core.Controllers.AppTokensController"/> already owns
/// that path, and <c>MapControllers()</c> runs before <c>MapRazorPages()</c>
/// in <c>Program.cs</c>, so a page at <c>/AppTokens</c> would be
/// silently shadowed by the REST endpoint rather than rendering.
/// </summary>
public sealed class AppTokensModel(AppTokenStore tokens, IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public IReadOnlyList<AppTokenInfo> Tokens { get; private set; } = [];

    [BindProperty]
    public string? App { get; set; }

    public string? FlashOk { get; private set; }
    public string? FlashError { get; private set; }

    /// <summary>Set only on the response right after minting - the one
    /// render where the plaintext is visible. Never persisted (only the
    /// hash is stored) and never reconstructable after this request.</summary>
    public string? MintedApp { get; private set; }
    public string? MintedToken { get; private set; }

    public async Task OnGetAsync()
    {
        Options = options.CurrentValue;
        Tokens = await tokens.ListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Options = options.CurrentValue;

        // Same validation the /AppTokens REST endpoint applies - see
        // AppTokenStore.TryNormalizeAppName for exactly what that
        // allows and why (it's what MQTT topics and /AppApi can use,
        // lowercased since both already compare case-insensitively).
        if (!AppTokenStore.TryNormalizeAppName(App, out var app, out var error))
        {
            FlashError = error;
            Tokens = await tokens.ListAsync();
            return Page();
        }

        MintedToken = await tokens.CreateOrRotateAsync(app);
        MintedApp = app;
        Tokens = await tokens.ListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string? app)
    {
        Options = options.CurrentValue;

        var trimmed = (app ?? "").Trim();
        var revoked = !string.IsNullOrEmpty(trimmed) && await tokens.RevokeAsync(trimmed);
        if (revoked)
        {
            FlashOk = $"Revoked the token for \"{trimmed}\".";
        }
        else
        {
            FlashError = $"No token found for \"{trimmed}\".";
        }

        Tokens = await tokens.ListAsync();
        return Page();
    }
}
