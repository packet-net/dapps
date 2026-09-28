namespace dapps.core.Services;

/// <summary>
/// Periodically deletes offers and messages whose ttl has elapsed. Runs
/// independently of the forwarder so a node that's not actively forwarding
/// (no neighbours, AGW down, etc.) still ages out stale rows instead of
/// holding them forever.
///
/// Rows with no ttl set are exempt - those are local app submissions
/// without an expiry, or pre-A1 rows from before TTL was tracked.
/// </summary>
public class TtlSweeperService(
    Database database,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptionsMonitor<dapps.core.Models.SystemOptions> options,
    TransmissionAuditService transmissionAudit,
    ILogger<TtlSweeperService> logger) : BackgroundService
{
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a flood-seen record (DbFloodSeen) is kept.</summary>
    public static readonly TimeSpan FloodSeenWindow = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer takes a TimeProvider in .NET 8 - FakeTimeProvider
        // can drive the WaitForNextTickAsync loop deterministically in
        // tests via Advance().
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);

        // Run immediately on startup so a freshly-restarted node clears
        // out anything that expired while it was down.
        await SweepOnce();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
    }

    private async Task SweepOnce()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var deleted = await database.DeleteExpired(now);
            if (deleted > 0)
            {
                logger.LogInformation("TTL sweeper deleted {0} expired row(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TTL sweeper threw");
        }

        // F2: drop incomplete fragment reassemblies whose first fragment
        // arrived more than FragmentReassemblyTimeoutSeconds ago. Long
        // window by default (7 days) - HF / mesh propagation gaps
        // legitimately last days, and we'd rather hold the partial
        // bytes than throw away most of a near-complete message.
        try
        {
            var timeout = TimeSpan.FromSeconds(options.CurrentValue.FragmentReassemblyTimeoutSeconds);
            var fragmentsDropped = await database.SweepStaleFragments(now - timeout);
            if (fragmentsDropped > 0)
            {
                logger.LogInformation(
                    "TTL sweeper dropped {0} stale fragment row(s) older than {1}",
                    fragmentsDropped, timeout);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fragment sweep threw");
        }

        // Messages we've received are remembered until they'd have
        // expired anyway (DbReceived); after that, forget them. Flood
        // copies are kept a day, far longer than a flood takes to spread.
        try
        {
            var forgotten = await database.SweepReceivedAsync(now);
            if (forgotten > 0) logger.LogInformation("TTL sweeper forgot {0} received message(s)", forgotten);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Received-message sweep threw");
        }
        try
        {
            var floods = await database.SweepFloodSeenAsync(now - FloodSeenWindow);
            if (floods > 0) logger.LogInformation("TTL sweeper forgot {0} flood record(s)", floods);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Flood-seen sweep threw");
        }

        // Transmission audit retention. Default 90 days; 0 disables.
        // Ride this sweeper's tick rather than spawning yet another
        // BG service - the workload is just a delete-where, fine to
        // run minutely alongside TTL.
        try
        {
            var deleted = await transmissionAudit.SweepOldRowsAsync();
            if (deleted > 0)
            {
                logger.LogInformation("transmission audit sweeper deleted {0} old row(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "transmission audit sweep threw");
        }
    }
}
