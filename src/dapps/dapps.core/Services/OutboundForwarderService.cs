namespace dapps.core.Services;

/// <summary>
/// Background loop that runs <see cref="OutboundMessageManager.DoRun"/>
/// whenever there may be something to send. Without it, messages
/// submitted to the queue would just sit there until an operator called
/// <c>POST /Message/dorun</c> by hand.
///
/// A run starts when <see cref="ForwarderWakeup"/> is poked (a message
/// was queued, or a neighbour's session ended so traffic held back for
/// it can go), when the next destination comes out of its reconnect
/// cooldown, and otherwise every <see cref="TickInterval"/> as a
/// fallback for changes nothing signals, such as a neighbour being
/// heard for the first time. It used to run on a fixed 5-second tick,
/// which added 2.5 s on average to every message at every hop.
///
/// After a wake it waits <see cref="WakeSettle"/> before running, so a
/// burst of messages queued within a few milliseconds of each other
/// (a replication catch-up, say) goes out as one batch.
///
/// Manual <c>POST /Message/dorun</c> still works alongside this - it
/// goes through the same mutex as the loop, so two runs never overlap.
/// </summary>
public sealed class OutboundForwarderService(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<OutboundForwarderService> logger) : BackgroundService
{
    /// <summary>Longest wait between runs when nothing wakes the
    /// forwarder. Tunable in tests via init-only setters.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause between a wake and the run it starts, so messages
    /// queued together go out together.</summary>
    public TimeSpan WakeSettle { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Brief delay before the first run so the rest of the
    /// service surface (DB schema creation, MQTT broker bind, AGW
    /// connect, discovery channel join) gets a chance to settle.
    /// Picking the first message off the queue before AGW is reachable
    /// would just log a forwarding failure and re-queue.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The fallback interval, or less if a destination comes
    /// out of cooldown sooner: its retry shouldn't wait for the fallback.</summary>
    private TimeSpan NextWait(OutboundDestinationBackoff? backoff)
    {
        if (backoff?.EarliestRetry() is not { } retryAt) return TickInterval;
        var untilRetry = retryAt - timeProvider.GetUtcNow();
        return untilRetry < TickInterval ? untilRetry : TickInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Resolve OutboundMessageManager lazily on first tick rather
        // than via the ctor - IDappsOutboundTransport's factory reads
        // SystemOptions at construction, which is fine post-build but
        // gratuitous to chain through during DI graph materialisation.
        var outbound = services.GetRequiredService<OutboundMessageManager>();
        var wakeup = services.GetService<ForwarderWakeup>();
        var backoff = services.GetService<OutboundDestinationBackoff>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outbound.DoRun(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A bearer-level fault on a single message is already
                // caught inside DoRun; anything that escapes is the
                // queue-iteration logic itself. Log and try again next
                // tick rather than tearing the whole hosted service
                // down - a stuck forwarder is worse than a noisy one.
                logger.LogError(ex, "Forwarder tick failed; will retry on next interval");
            }

            try
            {
                if (wakeup is null)
                {
                    await Task.Delay(TickInterval, timeProvider, stoppingToken);
                }
                else if (await wakeup.WaitAsync(NextWait(backoff), timeProvider, stoppingToken))
                {
                    await Task.Delay(WakeSettle, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
