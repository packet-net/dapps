namespace dapps.core.Services;

/// <summary>
/// Refuses a cross-site browser page on the MQTT WebSocket at
/// <c>/mqtt</c> while the broker needs no credentials (#314).
///
/// Browsers let any web page open a WebSocket to any host and do not
/// apply CORS to it; the only clue is the <c>Origin</c> header they
/// always send. With <see cref="Models.SystemOptions.AuthRequired"/>
/// off, the broker accepts any CONNECT, so a page on another site,
/// open in the operator's browser, could subscribe to everything and
/// publish to <c>dapps/out/*</c>, which makes the node transmit.
///
/// The rule:
/// <list type="bullet">
/// <item>No <c>Origin</c> header: allowed. Non-browser MQTT clients
/// (paho, mqtt.js under Node) don't send one, and a browser always
/// does, so a missing header is never a browser page.</item>
/// <item>Same origin (the <c>Origin</c> host and port match the
/// request's <c>Host</c>, or the <c>X-Forwarded-Host</c> a reverse
/// proxy set): allowed. A page can't set either header itself on a
/// WebSocket upgrade.</item>
/// <item>Any other origin, including <c>null</c> (a sandboxed frame or
/// a file opened from disk): refused with 403 while app tokens are not
/// required. With tokens required, the MQTT CONNECT has to carry a
/// valid one, so a page on another site gets nothing without it and is
/// let through, which keeps browser apps hosted elsewhere working.</item>
/// </list>
/// </summary>
public sealed class MqttWebSocketOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx, SystemOptionsStore options)
    {
        if (ctx.Request.Path.StartsWithSegments("/mqtt", StringComparison.OrdinalIgnoreCase)
            && !IsAllowed(
                ctx.Request.Headers.Origin.ToString(),
                ctx.Request.Host.Value,
                ctx.Request.Headers["X-Forwarded-Host"].ToString(),
                options.CurrentValue.AuthRequired))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(ctx);
    }

    /// <summary>The decision, separated out for tests. <paramref name="origin"/>
    /// is the raw <c>Origin</c> header (empty when absent);
    /// <paramref name="host"/> and <paramref name="forwardedHost"/> are
    /// <c>host[:port]</c> values (empty when absent).</summary>
    public static bool IsAllowed(string? origin, string? host, string? forwardedHost, bool authRequired)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (authRequired) return true;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var o)
            || (o.Scheme != Uri.UriSchemeHttp && o.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // Uri.Authority drops a scheme's default port, as browsers do in
        // Origin, so compare with the request's host the same way.
        foreach (var candidate in new[] { host, forwardedHost?.Split(',')[0] })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (SameAuthority(o, candidate.Trim())) return true;
        }
        return false;
    }

    private static bool SameAuthority(Uri origin, string hostHeader)
    {
        if (!Uri.TryCreate($"{origin.Scheme}://{hostHeader}", UriKind.Absolute, out var h)) return false;
        return string.Equals(origin.Host, h.Host, StringComparison.OrdinalIgnoreCase) && origin.Port == h.Port;
    }
}
