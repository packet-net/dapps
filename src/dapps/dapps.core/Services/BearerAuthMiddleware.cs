using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Per-app bearer-token authentication for the <c>/AppApi/*</c> surface.
/// Reads <c>Authorization: Bearer &lt;token&gt;</c>, looks up which app
/// the token belongs to via <see cref="AppTokenStore"/>, and stamps the
/// authenticated app name onto <see cref="HttpContext.Items"/> so
/// controllers can scope-check (e.g. an app authenticated as "myapp"
/// can't ack messages for app "yourapp").
///
/// Enforcement only kicks in when <see cref="SystemOptions.AuthRequired"/>
/// is true - on by default for a brand new install (#316), but an
/// upgrade from an older DAPPS keeps whatever it already had set, so
/// existing single-host loopback deployments don't break on upgrade.
/// In open mode the middleware is a no-op. Plan A4.
/// </summary>
public sealed class BearerAuthMiddleware(
    RequestDelegate next,
    AppTokenStore tokens,
    IOptionsMonitor<SystemOptions> options,
    ILogger<BearerAuthMiddleware> logger,
    AuthThrottle? throttleOrNull = null)
{
    private readonly AuthThrottle throttle = throttleOrNull ?? new AuthThrottle(TimeProvider.System);

    /// <summary>Path prefix the middleware enforces against. Other
    /// surfaces (/Config, /Neighbours, /AppTokens) are admin-flavoured
    /// and intentionally not covered - those need the operator-level
    /// auth model documented in the README's loopback warning.</summary>
    public const string ProtectedPrefix = "/AppApi";

    /// <summary>Key under which downstream code (controllers) can read
    /// the authenticated app name.</summary>
    public const string AuthenticatedAppKey = "DappsAuthenticatedApp";

    public async Task InvokeAsync(HttpContext context)
    {
        var authRequired = options.CurrentValue.AuthRequired;
        var path = context.Request.Path.Value ?? "";
        var protectedPath = path.StartsWith(ProtectedPrefix, StringComparison.OrdinalIgnoreCase);

        if (!protectedPath || !authRequired)
        {
            await next(context);
            return;
        }

        var auth = context.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("AppApi request to {0} rejected: missing/non-bearer Authorization", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            return;
        }

        var address = context.Connection.RemoteIpAddress;
        if (throttle.LockedFor("rest", address) is { } wait)
        {
            logger.LogInformation("AppApi request to {0} from {1} rejected: too many invalid tokens", path, address);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return;
        }

        var token = auth["Bearer ".Length..].Trim();
        var appName = await tokens.VerifyAsync(token);
        if (appName is null)
        {
            throttle.Failed("rest", address);
            logger.LogInformation("AppApi request to {0} rejected: invalid bearer token", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
            return;
        }

        throttle.Succeeded("rest", address);
        context.Items[AuthenticatedAppKey] = appName;
        await next(context);
    }
}

/// <summary>
/// Helpers for controllers to read the authenticated app name and
/// validate that a request's path-app matches who's authenticated.
/// </summary>
public static class AuthenticatedAppExtensions
{
    public static string? GetAuthenticatedApp(this HttpContext context)
    {
        return context.Items.TryGetValue(BearerAuthMiddleware.AuthenticatedAppKey, out var v)
            ? v as string
            : null;
    }

    /// <summary>
    /// True when no auth is enforced (the middleware passed everyone
    /// through) OR the authenticated app matches <paramref name="app"/>.
    /// Controllers call this with the path / body app name and 403 if it
    /// returns false.
    /// </summary>
    public static bool IsAuthorisedForApp(this HttpContext context, string app)
    {
        var authed = context.GetAuthenticatedApp();
        if (authed is null) return true; // open mode - middleware didn't run
        return string.Equals(authed, app, StringComparison.OrdinalIgnoreCase);
    }
}
