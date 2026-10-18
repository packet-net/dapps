using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan B7 - discovery airtime budget. Beacons, solicit replies, and
/// probes all share one pool of airtime per hour: the scratchpad
/// called for "this DAPPS node is using N% of channel time" as one
/// operator-tuneable knob rather than a per-subsystem silo.
///
/// Every entry is tagged with an optional <c>channelKey</c> so per-
/// channel budgets can be enforced alongside the global one. A
/// reservation must fit under BOTH ceilings when both are set:
/// "global = 60 s/h" caps the node-wide rate, "this channel =
/// 20 s/h" caps how much of that pool any one HF channel can
/// consume. Probes (which don't have a channel) reserve against
/// global only.
///
/// Disabled by default (<see cref="SystemOptions.DiscoveryAirtimeBudgetSecondsPerHour"/>
/// = 0 + every channel's <see cref="DbDiscoveryChannel.AirtimeBudgetSecondsPerHour"/>
/// = 0). Operators on shared 1200-baud VHF or HF opt in by setting
/// either knob.
///
/// Estimates rather than measurements: see
/// <see cref="dapps.client.Discovery.LinkClassDefaults.AirtimeSecondsEstimate"/>.
/// </summary>
public sealed class AirtimeAccountant(
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<AirtimeAccountant> logger,
    OperationalMetrics? metrics = null)
{
    private static readonly TimeSpan WindowSize = TimeSpan.FromHours(1);
    private readonly object _lock = new();
    private readonly Queue<Entry> _entries = new();

    /// <summary>
    /// Try to reserve <paramref name="estimatedSeconds"/> of airtime
    /// for an upcoming transmission. Returns true if the reservation
    /// fits inside both the global and (when applicable) per-channel
    /// budget; false if it would push either trailing-hour total past
    /// its cap.
    ///
    /// <paramref name="reason"/> is logged on a deferred call and
    /// surfaced via diagnostics - pass something recognisable
    /// (e.g. "beacon udp/239.x", "probe G0CALL", "solicit").
    /// <paramref name="channelKey"/> is the per-channel bucket;
    /// pass null for transmissions that don't belong to a channel
    /// (probes today). <paramref name="channelBudgetSecondsPerHour"/>
    /// is the per-channel cap, read from the channel row by the
    /// caller and passed in so the accountant doesn't need to
    /// reach back to the database. 0 = no per-channel cap (only
    /// the global cap applies, if set).
    /// </summary>
    public bool TryReserve(
        double estimatedSeconds,
        string reason,
        string? channelKey = null,
        int channelBudgetSecondsPerHour = 0)
    {
        if (estimatedSeconds < 0) estimatedSeconds = 0;
        var globalBudget = options.CurrentValue.DiscoveryAirtimeBudgetSecondsPerHour;

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            DropExpiredLocked(now);

            // Global cap.
            if (globalBudget > 0)
            {
                var globalConsumed = SumLocked(channelKey: null);
                if (globalConsumed + estimatedSeconds > globalBudget)
                {
                    logger.LogInformation(
                        "Airtime budget exhausted (global) - refusing {Reason} ({Estimate:0.000}s); consumed {Consumed:0.000}s of {Budget}s/hour",
                        reason, estimatedSeconds, globalConsumed, globalBudget);
                    metrics?.RecordBudgetRefused($"global cap: {reason}");
                    return false;
                }
            }

            // Per-channel cap (only when both a key was supplied AND
            // a non-zero per-channel cap is set).
            if (channelKey is not null && channelBudgetSecondsPerHour > 0)
            {
                var channelConsumed = SumLocked(channelKey);
                if (channelConsumed + estimatedSeconds > channelBudgetSecondsPerHour)
                {
                    logger.LogInformation(
                        "Airtime budget exhausted (channel {Channel}) - refusing {Reason} ({Estimate:0.000}s); consumed {Consumed:0.000}s of {Budget}s/hour",
                        channelKey, reason, estimatedSeconds, channelConsumed, channelBudgetSecondsPerHour);
                    metrics?.RecordBudgetRefused($"{channelKey} cap: {reason}");
                    return false;
                }
            }

            _entries.Enqueue(new Entry(now, estimatedSeconds, reason, channelKey));
            return true;
        }
    }

    /// <summary>
    /// Gives back the latest reservation made with this
    /// <paramref name="reason"/> and <paramref name="channelKey"/>: the
    /// transmission it was for never went (the connection to the node
    /// was down).
    /// </summary>
    public void Release(string reason, string? channelKey = null)
    {
        lock (_lock)
        {
            var entries = _entries.ToList();
            var i = entries.FindLastIndex(e =>
                e.Reason == reason && string.Equals(e.ChannelKey, channelKey, StringComparison.Ordinal));
            if (i < 0) return;
            entries.RemoveAt(i);
            _entries.Clear();
            foreach (var e in entries) _entries.Enqueue(e);
        }
    }

    /// <summary>Trailing-hour airtime consumption, in seconds, across
    /// every channel. For dashboards / diagnostics; not part of the
    /// reservation flow.</summary>
    public double ConsumedSecondsLastHour
    {
        get
        {
            lock (_lock)
            {
                DropExpiredLocked(timeProvider.GetUtcNow().UtcDateTime);
                return SumLocked(channelKey: null);
            }
        }
    }

    /// <summary>Trailing-hour consumption restricted to one channel.
    /// Pass null to ask for the global total; same as
    /// <see cref="ConsumedSecondsLastHour"/>.</summary>
    public double ConsumedSecondsLastHourFor(string? channelKey)
    {
        lock (_lock)
        {
            DropExpiredLocked(timeProvider.GetUtcNow().UtcDateTime);
            return SumLocked(channelKey);
        }
    }

    /// <summary>The currently-effective global budget, for dashboards.
    /// 0 = unlimited.</summary>
    public int BudgetSecondsPerHour => options.CurrentValue.DiscoveryAirtimeBudgetSecondsPerHour;

    private void DropExpiredLocked(DateTime now)
    {
        var cutoff = now - WindowSize;
        while (_entries.Count > 0 && _entries.Peek().At < cutoff)
        {
            _entries.Dequeue();
        }
    }

    private double SumLocked(string? channelKey)
    {
        var total = 0.0;
        foreach (var e in _entries)
        {
            if (channelKey is null || string.Equals(e.ChannelKey, channelKey, StringComparison.Ordinal))
            {
                total += e.Seconds;
            }
        }
        return total;
    }

    private readonly record struct Entry(DateTime At, double Seconds, string Reason, string? ChannelKey);
}
