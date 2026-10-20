using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Unified message-flow surface. Tabs (URL ?tab=) for the four
/// distinct message lifecycles an operator wants to inspect:
/// <list type="bullet">
/// <item><c>outbound</c>: queued for forwarding (someone else's destination).</item>
/// <item><c>inbox</c>: queued for local delivery (an app on this node).</item>
/// <item><c>dropped</c>: TTL-expired or otherwise removed without delivery.</item>
/// <item><c>live</c>: SSE feed of arrivals as they happen.</item>
/// </list>
/// All four read from <c>/Operational?full=true</c> (cached by _Layout)
/// or directly from <c>/Events/inbound</c> (SSE) - this page model just
/// picks the active tab.
///
/// The inbox tab's Drop button posts to <see cref="OnPostDropAsync"/>
/// (#389). Gated by <see cref="AdminAuthMiddleware"/> like the rest of
/// the dashboard, and by the standard Razor Pages antiforgery check.
/// </summary>
public sealed class MessagesModel(IOptionsMonitor<SystemOptions> options, DatabaseAndMqttInbox inbox) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public string Tab { get; private set; } = "outbound";

    public void OnGet(string? tab)
    {
        Options = options.CurrentValue;
        Tab = (tab ?? "").ToLowerInvariant() switch
        {
            "inbox"  => "inbox",
            "dropped"=> "dropped",
            "live"   => "live",
            _        => "outbound",
        };
    }

    /// <summary>
    /// Drop one message from the local inbox: it moves to the dropped
    /// messages as dropped by the operator and is never handed to its app.
    /// 200 when dropped, 404 when there's no such message waiting for an
    /// app here, 409 when its app has already acked it.
    /// </summary>
    public async Task<IActionResult> OnPostDropAsync(string? id)
    {
        var trimmed = (id ?? "").Trim();
        if (trimmed.Length == 0) return BadRequest("Which message? No id was given.");
        return await inbox.DropByOperatorAsync(trimmed, HttpContext.RequestAborted) switch
        {
            InboxDropOutcome.Dropped => new JsonResult(new { id = trimmed, dropped = true }),
            InboxDropOutcome.AlreadyAcked => new ConflictObjectResult("Its app has already acknowledged it, so there is nothing to drop."),
            _ => NotFound("That message isn't in the local inbox any more."),
        };
    }
}
