namespace dapps.core.Services;

/// <summary>
/// Sliding-scale reconnect backoff shared by the inbound bearer services
/// (<see cref="AgwInboundService"/>, <see cref="Rhpv2InboundService"/>).
/// A flat retry interval either hammers a node that's genuinely down or
/// makes the operator wait too long after a transient blip - this ramps
/// from fast retries (the common case: BPQ restarting, a cable bounce)
/// up to a slow steady-state so a node that stays unreachable doesn't
/// get hit every few seconds forever.
///
/// Schedule: 10s x3, 30s x3, 1min x3, then 5min steady-state. Resets to
/// the top of the schedule on the next successful connect.
/// </summary>
public sealed class ReconnectBackoffSchedule(TimeProvider? timeProvider = null)
{
    private static readonly (TimeSpan Delay, int Attempts)[] RampSteps =
    [
        (TimeSpan.FromSeconds(10), 3),
        (TimeSpan.FromSeconds(30), 3),
        (TimeSpan.FromMinutes(1), 3),
    ];

    private static readonly TimeSpan SteadyState = TimeSpan.FromMinutes(5);

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Consecutive failures since the last successful connect. 0
    /// means either never tried, currently connected, or idle-gated.</summary>
    public int FailureStreak { get; private set; }

    /// <summary>When the next automatic retry is due, or null when not
    /// currently in a backoff wait.</summary>
    public DateTimeOffset? NextRetryAtUtc { get; private set; }

    /// <summary>Record a failed connect attempt and return how long to wait
    /// before the next one.</summary>
    public TimeSpan RecordFailure()
    {
        FailureStreak++;
        var delay = DelayForAttempt(FailureStreak);
        NextRetryAtUtc = timeProvider.GetUtcNow() + delay;
        return delay;
    }

    /// <summary>Record a successful connect, or that we're not currently in
    /// a failure state (idle-gated on missing config, cleanly cancelled) -
    /// resets the schedule so the next failure starts back at the fast end
    /// of the ramp.</summary>
    public void RecordSuccess()
    {
        FailureStreak = 0;
        NextRetryAtUtc = null;
    }

    /// <summary>Clears the pending-retry timestamp without touching the
    /// failure streak - used when a wait is collapsed early (manual
    /// retry) so a status read between the click and the next attempt's
    /// outcome shows "no wait in flight" rather than the stale countdown
    /// from before the click.</summary>
    public void ClearPendingWait() => NextRetryAtUtc = null;

    private static TimeSpan DelayForAttempt(int attempt)
    {
        var remaining = attempt;
        foreach (var (delay, count) in RampSteps)
        {
            if (remaining <= count) return delay;
            remaining -= count;
        }
        return SteadyState;
    }
}

/// <summary>
/// Owns one bearer's <see cref="ReconnectBackoffSchedule"/> plus the
/// plumbing to let a pending backoff wait be collapsed early - shared by
/// <see cref="AgwInboundService"/> and <see cref="Rhpv2InboundService"/>
/// so this behaviour (in particular, a /Config save interrupting an
/// in-flight wait - see <see cref="Interrupt"/>) can't drift between the
/// two nearly-identical services. Deliberately doesn't know about
/// <see cref="OperationalMetrics"/> or which bearer it is - the owning
/// service publishes <see cref="FailureStreak"/> / <see cref="NextRetryAtUtc"/>
/// itself, where it already has unambiguous access to its own
/// constructor-injected <c>metrics</c> field.
/// </summary>
public sealed class InboundReconnectController
{
    private readonly ReconnectBackoffSchedule backoff = new();

    // volatile: written on the service's background reconnect-loop
    // thread (WaitAsync), read from unrelated threads - an HTTP request
    // thread via TriggerRetry, an IOptionsMonitor callback thread via
    // Interrupt. Without this, a weaker memory model (this ships
    // linux-arm/arm64 builds for Raspberry Pi) doesn't guarantee the
    // write is visible yet, which would make an operator's "retry now"
    // click silently do nothing until the wait elapses on its own.
    private volatile CancellationTokenSource? waitCts;

    public int FailureStreak => backoff.FailureStreak;
    public DateTimeOffset? NextRetryAtUtc => backoff.NextRetryAtUtc;

    public void RecordSuccess() => backoff.RecordSuccess();

    public TimeSpan RecordFailure() => backoff.RecordFailure();

    /// <summary>Waits out <paramref name="delay"/>, returning early if
    /// <see cref="TriggerRetry"/> or <see cref="Interrupt"/> is called, or
    /// if <paramref name="outerCt"/> fires (host shutdown).</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken outerCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        waitCts = cts;
        try { await Task.Delay(delay, cts.Token); }
        catch (OperationCanceledException) { /* manual retry, options change, or shutdown */ }
        finally { waitCts = null; }
    }

    /// <summary>Operator-triggered "retry now": collapses an in-progress
    /// wait so the next connect attempt happens immediately, and clears
    /// the pending-retry timestamp so a status read between now and the
    /// next attempt's outcome doesn't show the stale pre-click countdown.
    /// Returns false when there's no wait in flight to collapse (already
    /// connected, idle-gated, or a connect attempt is already
    /// underway).</summary>
    public bool TriggerRetry()
    {
        var cts = waitCts;
        if (cts is null) return false;
        try
        {
            cts.Cancel();
            backoff.ClearPendingWait();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Collapses any in-progress wait without it counting as an
    /// operator-triggered retry - used so a /Config save (host, port,
    /// callsign, bearer flip) makes the loop re-evaluate immediately
    /// instead of sitting out the rest of a backoff delay computed under
    /// the old settings.</summary>
    public void Interrupt()
    {
        try { waitCts?.Cancel(); }
        catch (ObjectDisposedException) { /* wait already completing */ }
    }
}
