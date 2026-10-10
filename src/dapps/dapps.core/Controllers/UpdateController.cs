using System.Text.Json;
using dapps.core.Services;
using dapps.core.Updater;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C5.2 - operator surface for the triggered-update flow. Two
/// endpoints:
///
/// <list type="bullet">
/// <item><c>GET /Update/status</c> - current binary version, latest
/// known release, and the most recent <c>--apply-update</c> phase
/// from <see cref="UpdaterPaths.StatusPath"/> (so the dashboard can
/// follow a live update without polling the privileged updater
/// directly).</item>
/// <item><c>POST /Update/apply</c> - write the request marker to
/// <see cref="UpdaterPaths.RequestPath"/>. The privileged
/// <c>dapps-updater.service</c> picks it up on its next 60 s tick
/// and invokes <c>dapps --apply-update</c>. dapps itself stays
/// unprivileged.</item>
/// </list>
/// </summary>
[ApiController]
[Route("[controller]")]
public class UpdateController(
    UpdateChecker updateChecker,
    IUpdaterFileSystem fs) : ControllerBase
{
    [HttpGet("status")]
    public UpdateStatusResponse Status()
    {
        var paths = UpdaterPaths.Default;
        UpdateStatus? lastRun = null;
        var raw = fs.ReadAllText(paths.StatusPath);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                lastRun = JsonSerializer.Deserialize<UpdateStatus>(raw);
            }
            catch (JsonException)
            {
                // Status file is operator-readable; if something else
                // wrote nonsense, surface "unknown" rather than 500.
            }
        }

        var requestPending = fs.Exists(paths.RequestPath);
        var latest = updateChecker.Latest;

        return new UpdateStatusResponse(
            Current: updateChecker.Current,
            IsDevBuild: updateChecker.IsDevBuild,
            Latest: latest?.Tag,
            ReleaseUrl: latest?.Url,
            IsAvailable: updateChecker.UpdateAvailable,
            FetchedAt: latest?.FetchedAt,
            RequestPending: requestPending,
            LastRun: lastRun,
            Install: updateChecker.InstallName,
            CanApply: updateChecker.CanApply,
            UpgradeCommand: updateChecker.UpgradeCommand,
            DownloadUrl: updateChecker.DownloadUrl);
    }

    /// <summary>
    /// Plan C5.2 - manual GitHub re-poll. The background
    /// <see cref="UpdateChecker"/> polls every hour; operators who
    /// just shipped a release and want to see it appear on a node's
    /// dashboard immediately hit this. Cheap (one HTTPS round-trip);
    /// auth-gated like the rest of /Update.
    /// </summary>
    [HttpPost("check")]
    public async Task<UpdateStatusResponse> Check(CancellationToken ct)
    {
        await updateChecker.RefreshAsync(ct);
        return Status();
    }

    [HttpPost("apply")]
    public IActionResult Apply()
    {
        if (!updateChecker.CanApply)
        {
            // A .deb install: apt owns /usr/lib/dapps and there is no
            // dapps-updater unit to pick a marker up.
            return Conflict("This node was installed from the .deb, so apt upgrades it: " +
                (updateChecker.UpgradeCommand ?? UpdateChecker.AptCommand));
        }
        var paths = UpdaterPaths.Default;
        // Marker file is empty; the updater always fetches latest.
        // Time stamp inside is purely informational for the dashboard.
        var markerBody = $"requested_at={DateTime.UtcNow:O}\n";
        try
        {
            fs.WriteAllText(paths.RequestPath, markerBody);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The unprivileged dapps user couldn't write the marker.
            // Most likely cause: /var/lib/dapps/ permissions or the
            // dapps-updater install recipe wasn't followed. Surface
            // the cause rather than a generic 500.
            return StatusCode(503, $"Cannot write update request marker: {ex.Message}. " +
                "Is dapps-updater.service installed?");
        }
        catch (DirectoryNotFoundException ex)
        {
            return StatusCode(503, $"Cannot write update request marker: {ex.Message}");
        }
        return Accepted();
    }
}

public sealed record UpdateStatusResponse(
    string Current,
    bool IsDevBuild,
    string? Latest,
    string? ReleaseUrl,
    bool IsAvailable,
    DateTime? FetchedAt,
    bool RequestPending,
    UpdateStatus? LastRun,
    // How DAPPS was installed: "apt" (from packet-net's apt repo), "deb" (a .deb installed by hand) or "other".
    string Install = "other",
    // Whether the self-updater's Apply applies; false on a .deb install, where apt owns the upgrade.
    bool CanApply = true,
    // On a .deb install with a newer version ready, the command that upgrades it.
    string? UpgradeCommand = null,
    // On a hand-installed .deb with a newer version ready, the new .deb's download.
    string? DownloadUrl = null);
