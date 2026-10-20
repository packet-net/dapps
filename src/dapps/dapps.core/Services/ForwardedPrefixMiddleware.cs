using System.Net;
using System.Text.RegularExpressions;

namespace dapps.core.Services;

/// <summary>
/// pdn app-gateway support: when the dashboard is reverse-proxied under
/// a path such as <c>/apps/dapps</c>, the proxy strips the prefix from
/// the forwarded path and sends <c>X-Forwarded-Prefix</c>. This sets
/// <c>PathBase</c> from it so every generated URL stays inside the mount.
///
/// The header is honoured only from a proxy on this machine (the pdn
/// gateway connects from loopback), and only when it looks like a plain
/// path. Anyone else could otherwise send it, and a caching proxy in
/// front could then store pages whose links and redirects point
/// somewhere of their choosing, such as <c>//evil.example</c> (#320).
/// </summary>
public sealed partial class ForwardedPrefixMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Forwarded-Prefix";

    public Task InvokeAsync(HttpContext ctx)
    {
        if (IsTrustedProxy(ctx.Connection.RemoteIpAddress)
            && TryNormalise(ctx.Request.Headers[HeaderName].ToString(), out var prefix))
        {
            ctx.Request.PathBase = new PathString(prefix);
        }
        return next(ctx);
    }

    internal static bool IsTrustedProxy(IPAddress? remote) =>
        remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    /// <summary>A prefix is one or more path segments of plain characters:
    /// <c>/apps/dapps</c>, never <c>//host</c>, <c>/a/../b</c> or anything
    /// with a scheme, backslash or query.</summary>
    internal static bool TryNormalise(string? header, out string prefix)
    {
        prefix = "";
        var value = (header ?? "").Trim().TrimEnd('/');
        if (value.Length == 0 || !SafePrefix().IsMatch(value)) return false;
        if (value.Split('/').Any(segment => segment is "." or "..")) return false;
        prefix = value;
        return true;
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9._~\-]+)+$")]
    private static partial Regex SafePrefix();
}
