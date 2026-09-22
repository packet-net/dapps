namespace dapps.core.Services;

/// <summary>
/// Paces successive connects to the same link key so a redial never
/// beats the previous link's teardown to the far end.
///
/// Why this exists: disconnecting only tells <em>our own</em> node
/// (BPQ, XRouter, ...) to tear the session down. That has to propagate
/// to the remote node (over RF, AXIP, ...) and be noticed by its AGW
/// poll loop before it will accept a fresh connect for the same
/// callsign pair. BPQ in particular keeps the old session's key
/// reserved until its next poll tick processes the disconnect, and if
/// the redial lands on a lower-numbered stream in the same tick it is
/// refused outright ("Callsign is already connected", AGWAPI.c
/// AGWConnected). That refusal is logged only on the remote node; on
/// our side the forward just times out for no visible reason.
/// <see cref="OutboundMessageManager"/> sends queued messages to a
/// destination back-to-back with no gap of its own, so this supplies
/// one.
///
/// Time comes from the injected <see cref="TimeProvider"/> so tests
/// drive it with a fake clock rather than sleeping. Keys are opaque
/// strings; <see cref="BearerSwitchingOutboundTransport"/> builds them
/// from (bearer, local, remote, port).
/// </summary>
public sealed class LinkSettleGate(TimeProvider timeProvider, TimeSpan settleDelay)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> releasedAt = new();

    public TimeSpan SettleDelay { get; } = settleDelay;

    /// <summary>Number of keys currently remembered; exposed for tests.</summary>
    internal int TrackedKeys
    {
        get { lock (gate) return releasedAt.Count; }
    }

    /// <summary>How much longer a connect to <paramref name="key"/> has
    /// to wait right now. Zero when nothing is pending.</summary>
    public TimeSpan PendingWait(string key)
    {
        if (SettleDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        lock (gate)
        {
            if (!releasedAt.TryGetValue(key, out var last)) return TimeSpan.Zero;
            var wait = SettleDelay - (timeProvider.GetUtcNow() - last);
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    /// <summary>Completes once the settle delay since the last release
    /// of <paramref name="key"/> has elapsed; immediately if there is
    /// none, or the delay is zero.</summary>
    public async Task WaitAsync(string key, CancellationToken ct)
    {
        var wait = PendingWait(key);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, timeProvider, ct);
        }
    }

    /// <summary>Records that the link for <paramref name="key"/> was just
    /// released (our disconnect frame went out, or a connect attempt
    /// ended without ever producing a usable link). Entries that have
    /// already settled are dropped so the table stays bounded by the
    /// number of destinations dialled within one settle window.</summary>
    public void RecordRelease(string key)
    {
        if (SettleDelay <= TimeSpan.Zero) return;
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            releasedAt[key] = now;
            foreach (var stale in releasedAt.Where(kv => now - kv.Value >= SettleDelay).Select(kv => kv.Key).ToList())
            {
                releasedAt.Remove(stale);
            }
        }
    }
}
