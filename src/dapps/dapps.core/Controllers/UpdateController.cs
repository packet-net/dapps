using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// The operator's "check now" for the passive new-version notice.
/// DAPPS never installs a new version itself: upgrade with apt, or replace
/// the binary (or pull the Docker image).
/// </summary>
[ApiController]
[Route("[controller]")]
public class UpdateController(UpdateChecker updateChecker) : ControllerBase
{
    /// <summary>
    /// Manual re-poll. The background <see cref="UpdateChecker"/> polls on a
    /// slow cadence; an operator who just shipped a release and wants to see
    /// it on this node's dashboard immediately hits this. Cheap (one HTTPS
    /// round-trip); auth-gated like the rest of /Update.
    /// </summary>
    [HttpPost("check")]
    public async Task<VersionStatus> Check(CancellationToken ct)
    {
        await updateChecker.RefreshAsync(ct);
        var latest = updateChecker.Latest;
        return new VersionStatus(
            Current: updateChecker.Current,
            IsDevBuild: updateChecker.IsDevBuild,
            Latest: latest?.Tag,
            ReleaseUrl: latest?.Url,
            IsAvailable: updateChecker.UpdateAvailable,
            FetchedAt: latest?.FetchedAt,
            Install: updateChecker.InstallName,
            UpgradeCommand: updateChecker.UpgradeCommand,
            DownloadUrl: updateChecker.DownloadUrl);
    }
}
