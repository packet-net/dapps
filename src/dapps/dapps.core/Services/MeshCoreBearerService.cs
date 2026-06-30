namespace dapps.core.Services;

/// <summary>Hosted service that drives the MeshCore bearer for the lifetime of
/// the daemon. Delegates to <see cref="MeshCoreBearer.RunAsync"/>, which is a
/// no-op when <c>MeshCoreEnabled=false</c>.</summary>
public sealed class MeshCoreBearerService(MeshCoreBearer bearer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bearer.RunAsync(stoppingToken);
}
