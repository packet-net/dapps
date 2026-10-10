using System.ComponentModel;
using System.Text.Json;
using dapps.core.Updater;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M follow-up - operator-supervised self-update tools so an
/// MCP client can do what the dashboard's "Apply update" button
/// does, plus check status and re-poll GitHub. Closes the iteration
/// loop where the agent ships a PR and then asks the human to
/// trigger the deploy: the agent can do it itself now.
///
/// Wraps the same code paths as <see cref="dapps.core.Controllers.UpdateController"/>;
/// no new privileged surface.
/// </summary>
[McpServerToolType]
public sealed class DappsUpdateTools(IUpdaterFileSystem fs, UpdateChecker updateChecker)
{
    [McpServerTool(Name = "trigger_update")]
    [Description(
        "Write the update-request marker to /var/lib/dapps/update-requested. The privileged " +
        "dapps-updater.service picks it up on its next 60 s tick and runs `dapps --apply-update`, " +
        "which downloads the latest release for this RID, swaps the binary, restarts the daemon, " +
        "verifies it stays up for 60 s, and rolls back to dapps.previous on any failure. Returns " +
        "the marker body that was written. Same code path as the dashboard's 'Apply update' button. " +
        "Smoke-test by polling get_update_status until current == latest.")]
    public string TriggerUpdate()
    {
        if (!updateChecker.CanApply)
        {
            throw new InvalidOperationException(
                "This node was installed from the .deb, so apt owns its upgrades and there is no self-updater. " +
                $"Upgrade it on the machine with: {updateChecker.UpgradeCommand ?? UpdateChecker.AptCommand}");
        }
        var paths = UpdaterPaths.Default;
        var markerBody = $"requested_at={DateTime.UtcNow:O}\n";
        try
        {
            fs.WriteAllText(paths.RequestPath, markerBody);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Cannot write update request marker: {ex.Message}. Is dapps-updater.service installed?", ex);
        }
        return $"update marker written to {paths.RequestPath}";
    }

    [McpServerTool(Name = "get_update_status")]
    [Description(
        "Returns the current binary version, the newest release (from the apt repository on a .deb install, " +
        "else GitHub), whether an update is available, how DAPPS was installed and, on a .deb install, the apt " +
        "command that upgrades it, whether a request marker is currently pending (set by trigger_update), and the " +
        "last update-run phase (downloading / swapping / restarting / verifying / success / rolled-back / " +
        "failed). Use this to follow a triggered update through to completion.")]
    public UpdateStatusReport GetUpdateStatus()
    {
        var paths = UpdaterPaths.Default;
        UpdateStatus? lastRun = null;
        var raw = fs.ReadAllText(paths.StatusPath);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { lastRun = JsonSerializer.Deserialize<UpdateStatus>(raw); }
            catch (JsonException) { /* operator-readable file; tolerate junk */ }
        }
        var requestPending = fs.Exists(paths.RequestPath);
        var latest = updateChecker.Latest;
        return new UpdateStatusReport(
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

    [McpServerTool(Name = "check_for_updates")]
    [Description(
        "Force the UpdateChecker to re-poll now (packet-net's apt repository on a .deb install, GitHub Releases " +
        "otherwise), rather than waiting for its background tick. Useful right after an MCP client has merged a PR and wants the new release " +
        "to show up on this node's dashboard / heartbeat snapshot before triggering the apply.")]
    public async Task<UpdateStatusReport> CheckForUpdatesAsync(CancellationToken ct)
    {
        await updateChecker.RefreshAsync(ct);
        return GetUpdateStatus();
    }
}

public sealed record UpdateStatusReport(
    string Current,
    bool IsDevBuild,
    string? Latest,
    string? ReleaseUrl,
    bool IsAvailable,
    DateTime? FetchedAt,
    bool RequestPending,
    UpdateStatus? LastRun,
    // "apt", "deb" or "other"; see UpdateStatusResponse.
    string Install = "other",
    bool CanApply = true,
    string? UpgradeCommand = null,
    string? DownloadUrl = null);
