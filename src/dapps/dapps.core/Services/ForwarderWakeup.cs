namespace dapps.core.Services;

/// <summary>
/// Tells the outbound forwarder that a run now could send something: a
/// message was queued, or a neighbour's session ended so traffic held
/// back for it can go. Wakes coalesce: however many arrive while the
/// forwarder is busy, it runs once more afterwards, and that run takes
/// everything queued by then.
/// </summary>
public sealed class ForwarderWakeup
{
    private readonly SemaphoreSlim pending = new(0, 1);

    public void Wake()
    {
        try
        {
            pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; one is all the forwarder needs.
        }
    }

    /// <summary>
    /// Wait for a wake, or for <paramref name="timeout"/> to pass. True
    /// when woken (and the wake is consumed), false on timeout.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var woken = pending.WaitAsync(cts.Token);
        var timer = Task.Delay(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero, timeProvider, cts.Token);
        await Task.WhenAny(woken, timer);

        // Withdraw whichever is still waiting, so an abandoned wait can't
        // swallow a later wake.
        await cts.CancelAsync();
        try
        {
            await woken;
            return true;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
