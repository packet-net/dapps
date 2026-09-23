using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace dapps.core.Services;

/// <summary>
/// Gate the dashboard and admin endpoints behind the cookie auth
/// scheme. Runs after <see cref="BearerAuthMiddleware"/>, which owns
/// <c>/AppApi/*</c> with its own bearer-token model - those requests
/// pass through here untouched.
///
/// Three states:
/// <list type="bullet">
/// <item>No admin password configured (fresh install) → redirect to
///   <c>/Setup</c>. The first request lands the operator on a one-shot
///   "set your password" form; once they submit it, normal cookie auth
///   kicks in.</item>
/// <item>Password configured, no valid cookie → redirect to <c>/Login</c>
///   via the cookie scheme's challenge.</item>
/// <item>Password configured, valid cookie → pass through.</item>
/// </list>
/// </summary>
public sealed class AdminAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AdminPasswordStore store, SystemOptionsStore options)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Pass-through for paths the middleware deliberately doesn't gate:
        //   /AppApi/*       - owned by BearerAuthMiddleware
        //   /Setup          - first-use password creation; gated by
        //                     state, not auth (see PageModel)
        //   /Login, /Logout - the auth UX itself
        //   /Health         - C3 liveness; systemd watchdog units +
        //                     external uptime monitors can't log in
        //   /Operational    - C3 metrics aggregate; same posture as
        //                     /Events/health which the dashboard JS
        //                     polls without auth context anyway.
        //                     GET only: the retry-now action under the
        //                     same prefix needs the admin cookie.
        //   /mcp            - Plan G MCP endpoint; clients (Claude,
        //                     Cursor) don't have admin cookies. An
        //                     MCP-specific token model can come later.
        //   /mqtt           - MQTT-over-WebSocket endpoint; the MQTT
        //                     CONNECT-time username/password is the
        //                     auth surface here, same model as the
        //                     TCP broker on :MqttPort.
        //   static asset paths Razor's StaticFiles middleware serves
        if (IsPassThrough(path, ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        // No password configured? First-use flow. Send the operator to
        // /Setup. Until they complete it, every other path bounces
        // here.
        if (!await store.IsConfiguredAsync())
        {
            ctx.Response.Redirect($"{ctx.Request.PathBase}/Setup");
            return;
        }

        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            // Setup-required state: admin password configured, callsign
            // still the placeholder. Bounce navigational HTML requests
            // to /Setup so the wizard's bearer step picks up. Existing
            // operators (real callsign) skip this branch entirely.
            //
            // API endpoints (/Config/detect-bearer, /Neighbours, etc.)
            // pass through even in setup-required state - the wizard's
            // own JS calls /Config/detect-bearer and would die JSON-
            // parsing the redirected /Setup HTML otherwise.
            var callsign = options.CurrentValue.Callsign;
            var isSetupRequired = string.IsNullOrWhiteSpace(callsign)
                || string.Equals(callsign, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
            if (isSetupRequired && LooksLikeNavigation(ctx.Request))
            {
                ctx.Response.Redirect($"{ctx.Request.PathBase}/Setup");
                return;
            }

            await next(ctx);
            return;
        }

        // Trigger the cookie scheme's challenge → 302 to LoginPath.
        await ctx.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>True when the request looks like a browser navigation
    /// (GET + Accept includes text/html). API calls from JS (which use
    /// fetch with Accept: */* or application/json) return false; redirecting
    /// those to /Setup would hand back HTML that the JS can't parse.</summary>
    private static bool LooksLikeNavigation(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;
        var accept = request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPassThrough(string path, string method)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("/AppApi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Health", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/Operational", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method))
            || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mqtt", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            || path == "/favicon.ico";
    }
}
