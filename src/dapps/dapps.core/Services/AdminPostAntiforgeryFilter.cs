using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace dapps.core.Services;

/// <summary>
/// #320: the admin endpoints are protected by a SameSite=Strict cookie,
/// which does not help against another service on the same host (same
/// site, different port). A page there can POST here with no body, or
/// with a form or text body, and the browser attaches the cookie.
///
/// So a POST that isn't JSON needs the antiforgery token (the
/// <c>RequestVerificationToken</c> header, which the dashboard's own
/// script adds to every fetch). A JSON POST doesn't: a page on another
/// origin can't send <c>Content-Type: application/json</c> without a CORS
/// preflight, which this app never grants, so the cookie can't be
/// ridden that way. PUT, PATCH and DELETE are preflighted in any case.
/// Controllers opt out with <c>[IgnoreAntiforgeryToken]</c> (the
/// bearer-token <c>/AppApi</c> has no cookie to ride on).
/// </summary>
public sealed class AdminPostAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method)) return;
        if (request.HasJsonContentType()) return;
        if (context.Filters.OfType<IgnoreAntiforgeryTokenAttribute>().Any()) return;

        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            context.Result = new BadRequestObjectResult(
                "Missing or invalid antiforgery token. Send the RequestVerificationToken header, or post JSON.");
        }
    }
}
