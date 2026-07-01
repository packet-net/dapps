namespace dapps.meshcore;

/// <summary>
/// Estimates how busy the shared LoRa channel is from the radio's LOG_RX_DATA
/// (0x88) "packet heard" events (#157). Every packet the radio overhears — our
/// peers' floods, other same-preset traffic — is recorded with its estimated
/// airtime; occupancy is the busy fraction over a trailing window. The bearer
/// uses this to be a *dynamically* good citizen: listen-before-talk and back off
/// when the channel is congested, on top of the static airtime budget.
/// </summary>
public sealed class ChannelMonitor
{
    private readonly RegionPreset _region;
    private readonly TimeSpan _window;
    private readonly Queue<(DateTime when, double airMs)> _heard = new();
    private readonly object _lock = new();
    private double _sumMs;

    public DateTime LastHeardUtc { get; private set; } = DateTime.MinValue;
    public long HeardCount { get; private set; }

    public ChannelMonitor(RegionPreset region, TimeSpan? window = null)
    {
        _region = region;
        _window = window ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Record a packet overheard on the channel. <paramref name="rawLen"/>
    /// is the logged on-air length; its airtime is estimated for the active preset.</summary>
    public void RecordHeard(int rawLen, DateTime now)
    {
        var airMs = LoRaAirtime.Ms(Math.Max(rawLen, 1), _region.Sf, _region.BwKhz * 1000, _region.Cr);
        lock (_lock)
        {
            Prune(now);
            _heard.Enqueue((now, airMs));
            _sumMs += airMs;
            LastHeardUtc = now;
            HeardCount++;
        }
    }

    /// <summary>Busy fraction (0..1) over the trailing window.</summary>
    public double OccupancyFraction(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            return Math.Min(1.0, _sumMs / _window.TotalMilliseconds);
        }
    }

    public TimeSpan SinceLastHeard(DateTime now) =>
        LastHeardUtc == DateTime.MinValue ? TimeSpan.MaxValue : now - LastHeardUtc;

    private void Prune(DateTime now)
    {
        var cutoff = now - _window;
        while (_heard.Count > 0 && _heard.Peek().when < cutoff)
            _sumMs -= _heard.Dequeue().airMs;
        // Reset exactly when the window empties to avoid float residual drift.
        if (_heard.Count == 0 || _sumMs < 0) _sumMs = 0;
    }
}
