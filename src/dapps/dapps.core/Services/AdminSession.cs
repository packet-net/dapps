using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace dapps.core.Services;

/// <summary>
/// The sysop's dashboard login cookie: issuing it, and checking on
/// every request that the password hasn't been set since it was issued
/// (#385). Login, the /Setup password step and a password change in
/// Settings all sign in through here.
/// </summary>
public static class AdminSession
{
    /// <summary>Sign the sysop in. Long sliding expiry: this is a
    /// sysop's home node, so the login is "remember me" by design.</summary>
    public static Task SignInAsync(HttpContext http)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "sysop") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            });
    }

    /// <summary>
    /// The cookie scheme's OnValidatePrincipal check: a login issued
    /// before the password was last set (from Settings, or with
    /// <c>dapps reset-admin-password</c> on the node) is rejected and
    /// its cookie deleted, so that browser lands on /Login.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<AdminPasswordStore>();
        if (await store.IsSessionCurrentAsync(ctx.Properties.IssuedUtc)) return;

        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
