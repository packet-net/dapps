using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Operator view of opt-in ordering state. Two tables:
/// <list type="bullet">
/// <item><description><b>Send</b> - per-(remote, stream) counters minted by local app submissions.</description></item>
/// <item><description><b>Receive</b> - per-(originator, stream) cursors plus the count of currently-parked rows for each, so a stalled stream is obvious at a glance.</description></item>
/// </list>
/// Refresh-driven; the work involved is too sparse to warrant SSE.
///
/// A stream with messages waiting has a "Skip to next" button, for one
/// stalled on a message that will never come (#290). It posts to
/// <see cref="OnPostSkipAsync"/>, gated by <see cref="AdminAuthMiddleware"/>
/// like the rest of the dashboard and by the standard Razor Pages
/// antiforgery check.
/// </summary>
public sealed class StreamsModel(
    Database database,
    DatabaseAndMqttInbox inbox,
    IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public IReadOnlyList<DbStreamSendState> SendStates { get; private set; } = [];
    public IReadOnlyList<RecvRow> RecvStates { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Options = options.CurrentValue;
        SendStates = await database.GetStreamSendStatesAsync();
        var recvs = await database.GetStreamRecvStatesAsync();
        var rows = new List<RecvRow>(recvs.Count);
        foreach (var r in recvs)
        {
            var pending = await database.GetPendingInOrderAsync(r.SenderCallsign, r.StreamId);
            DateTime? oldestPending = pending.Count == 0
                ? null
                : pending.Min(p => p.CreatedAt);
            rows.Add(new RecvRow(r, pending.Count, oldestPending));
        }
        RecvStates = rows;
    }

    /// <summary>
    /// Skip the stream with this key on to its next waiting message. 200
    /// when skipped, 404 when there's no such stream, 409 when nothing is
    /// waiting on it.
    /// </summary>
    public async Task<IActionResult> OnPostSkipAsync(string? key)
    {
        if (string.IsNullOrEmpty(key)) return BadRequest("Which stream? None was given.");
        return await inbox.SkipToNextWaitingByOperatorAsync(key, HttpContext.RequestAborted) switch
        {
            StreamSkipOutcome.Skipped => new JsonResult(new { skipped = true }),
            StreamSkipOutcome.NothingWaiting => new ConflictObjectResult("Nothing is waiting on that stream any more, so there is nothing to skip to."),
            _ => NotFound("That stream isn't listed any more."),
        };
    }

    public sealed record RecvRow(DbStreamRecvState State, int PendingCount, DateTime? OldestPendingAt);
}
