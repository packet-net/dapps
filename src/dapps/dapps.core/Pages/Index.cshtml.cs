using System.Text;
using dapps.client;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Overview page (/) - lean first paint plus a JS layer driven by
/// <c>/Operational?full=true</c>. The model only needs SystemOptions
/// and the TX-gate state for the page header; live data (queue counts,
/// node reachability, decisions ring, airtime, update card) all come
/// from the snapshot the layout polls.
/// </summary>
public sealed class IndexModel(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    SystemOptionsBackedTxGate txGate,
    ILogger<IndexModel> logger) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public bool TxAllowed => txGate.TxAllowed;
    public string? TxBlockReason => txGate.BlockReason;

    [BindProperty]
    public SendForm Send { get; set; } = new();

    public string? FlashOk { get; set; }
    public string? FlashError { get; set; }

    public Task OnGetAsync()
    {
        Options = options.CurrentValue;
        return Task.CompletedTask;
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        Options = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(Send.App)) FlashError = "App is required";
        else if (string.IsNullOrWhiteSpace(Send.Destination)) FlashError = "Destination callsign is required";
        else if (string.IsNullOrWhiteSpace(Send.Payload)) FlashError = "Payload is required";
        // Both go on the air in the offer line (#313).
        else if (MessageFields.AppNameError(Send.App.Trim()) is { } appError) FlashError = appError;
        else if (MessageFields.CallsignError(Send.Destination.Trim()) is { } callError) FlashError = $"Destination: {callError}";

        if (FlashError is null)
        {
            try
            {
                // Same 24 h expiry Compose offers by default, so the two
                // forms behave alike.
                var id = await database.SubmitOutboundMessage(
                    Send.App.Trim(),
                    Send.Destination.Trim().ToUpperInvariant(),
                    Encoding.UTF8.GetBytes(Send.Payload!),
                    ttlSeconds: QuickSendTtlSeconds);
                FlashOk = $"Queued message {id} for {Send.App}@{Send.Destination}.";
                Send = new SendForm();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Overview test-send failed");
                FlashError = $"Send failed: {ex.Message}";
            }
        }

        return Page();
    }

    /// <summary>Quick send's fixed expiry, matching Compose's default.</summary>
    public const int QuickSendTtlSeconds = 86400;

    public sealed class SendForm
    {
        public string App { get; set; } = "";
        public string Destination { get; set; } = "";
        public string? Payload { get; set; }
    }

    /// <summary>
    /// Maps a queue row's internal flags (Forwarded / LocallyDelivered
    /// + destination-is-local) onto the (label, pill-class) pair the
    /// queue tables render. Pure for testability; used historically by
    /// the server-rendered queue table on /, kept here so the unit
    /// tests in DashboardLogicTests still exercise the routing.
    /// </summary>
    public static (string Label, string PillClass) MessageStatus(DbMessage m, string ourCallsign)
    {
        var isLocal = DestinationParser.IsLocal(m.Destination, ourCallsign);
        if (isLocal && m.LocallyDelivered) return ("delivered", "ok");
        if (isLocal) return ("pending local", "warn");
        if (m.Forwarded) return ("forwarded", "info");
        return ("pending", "");
    }

    /// <summary>Compact age string (seconds / minutes / hours / days)
    /// for queue row display.</summary>
    public static string Age(DateTime createdAt)
    {
        var span = DateTime.UtcNow - createdAt;
        if (span.TotalSeconds < 90) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 90) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }
}
