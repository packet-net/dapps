using System.Text;
using dapps.client;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Plan D2 - manual ihave compose page. Server-rendered POST handler
/// calls <see cref="Database.SubmitOutboundMessage"/> directly (same
/// path the dashboard's send-test form uses). Distinct from the
/// dashboard's send-test in that it surfaces every operator-relevant
/// field - TTL, the upcoming on-air ihave preview - for debugging
/// the protocol layer without writing an app.
/// </summary>
public sealed class ComposeModel(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    ILogger<ComposeModel> logger) : PageModel
{
    public SystemOptions Options { get; private set; } = new();

    [BindProperty]
    public ComposeForm Form { get; set; } = new();

    public string? FlashOk { get; set; }
    public string? FlashError { get; set; }

    public string? LastSubmittedId { get; private set; }
    public int LastPayloadBytes { get; private set; }
    public string LastDestinationFull { get; private set; } = "";
    public string LastIhavePreview { get; private set; } = "";

    public void OnGet()
    {
        Options = options.CurrentValue;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Options = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(Form.App)) FlashError = "App is required";
        else if (string.IsNullOrWhiteSpace(Form.Destination)) FlashError = "Destination callsign is required";
        else if (string.IsNullOrWhiteSpace(Form.Payload)) FlashError = "Payload is required";
        // Both go on the air in the offer line (#313).
        else if (MessageFields.AppNameError(Form.App.Trim()) is { } appError) FlashError = appError;
        else if (MessageFields.CallsignError(Form.Destination.Trim()) is { } callError) FlashError = $"Destination: {callError}";

        int? ttl = null;
        if (FlashError is null && !string.IsNullOrWhiteSpace(Form.TtlSeconds))
        {
            if (!int.TryParse(Form.TtlSeconds, out var ttlVal) || ttlVal < 0)
            {
                FlashError = "TTL must be a non-negative integer (or empty for no expiry)";
            }
            else
            {
                ttl = ttlVal;
            }
        }

        if (FlashError is null)
        {
            try
            {
                var dest = Form.Destination!.Trim().ToUpperInvariant();
                var bytes = Encoding.UTF8.GetBytes(Form.Payload!);
                var id = await database.SubmitOutboundMessage(Form.App!.Trim(), dest, bytes, ttl);
                LastSubmittedId = id;
                LastPayloadBytes = bytes.Length;
                LastDestinationFull = $"{Form.App!.Trim()}@{dest}";
                LastIhavePreview = ttl is null
                    ? $"ihave {id} len={bytes.Length} dst={LastDestinationFull}"
                    : $"ihave {id} len={bytes.Length} dst={LastDestinationFull} ttl={ttl}";
                FlashOk = $"Queued message {id} ({bytes.Length} bytes) for {LastDestinationFull}.";
                // Don't reset the form - operator may want to tweak and resubmit.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "manual-ihave submit failed");
                FlashError = $"Submit failed: {ex.Message}";
            }
        }

        return Page();
    }

    public sealed class ComposeForm
    {
        public string? App { get; set; } = "chat";
        public string? Destination { get; set; }
        public string? Payload { get; set; }
        public string? TtlSeconds { get; set; } = "86400";
    }
}
