using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace dapps.core.Pages;

[IgnoreAntiforgeryToken]
public sealed class LoginModel(AdminPasswordStore store) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        // Fresh install with no password set yet: bounce to /Setup so
        // the operator sees the right form, not a login form for a
        // password that doesn't exist.
        if (!await store.IsConfiguredAsync()) return LocalRedirect("~/Setup");

        // Already signed in? Skip the form.
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturn(returnUrl));
        }
        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;

        if (!await store.VerifyAsync(Password))
        {
            Error = "Wrong password.";
            // Brief delay slows down brute-forcers without affecting
            // legitimate use. The PBKDF2 verification already takes
            // ~50ms; this adds another ~150ms.
            await Task.Delay(150);
            return Page();
        }

        await AdminSession.SignInAsync(HttpContext);

        return LocalRedirect(SafeReturn(returnUrl));
    }

    /// <summary>Defensive: only allow same-origin local paths as
    /// post-login redirect targets - never an off-host URL even if it
    /// arrived in a query string.</summary>
    private static string SafeReturn(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return "~/";
        if (!candidate.StartsWith('/')) return "~/";
        if (candidate.StartsWith("//")) return "~/";
        return candidate;
    }
}
