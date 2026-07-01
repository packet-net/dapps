namespace dapps.meshcore;

/// <summary>
/// Self-enforced airtime governor (the "good citizen" control for Model A, where
/// every channel send floods the whole same-preset network). HARD gate: a send
/// that would push trailing-hour airtime over budget is refused (back-pressure),
/// so DAPPS can never burst the shared channel. Budget is airtime seconds/hour;
/// the default is a small fraction of the 10% regulatory duty. Thread-safe.
/// </summary>
public sealed class TxBudget
{
    public const double DefaultSecondsPerHour = 30;   // ≈0.83% duty

    private readonly double _budgetMs;
    private readonly List<(long token, DateTime when, double ms)> _window = new();
    private readonly object _lock = new();
    private double _sumMs;
    private long _nextToken;

    public TxBudget(double secondsPerHour) => _budgetMs = secondsPerHour * 1000.0;

    public double BudgetSeconds => _budgetMs / 1000.0;

    public double UsedSeconds(DateTime now)
    {
        lock (_lock) { Prune(now); return _sumMs / 1000.0; }
    }

    public double DutyPercent(DateTime now)
    {
        lock (_lock) { Prune(now); return _sumMs / 36_000.0; }
    }

    /// <summary>Reserve airtime for one transmission. Returns false (changing nothing)
    /// if it would exceed the trailing-hour budget; otherwise <paramref name="token"/>
    /// identifies the reservation for a later <see cref="Refund"/>.</summary>
    public bool TryReserve(double airtimeMs, DateTime now, out string reason, out long token)
    {
        lock (_lock)
        {
            Prune(now);
            if (_sumMs + airtimeMs > _budgetMs)
            {
                reason = $"airtime budget exceeded: used {_sumMs / 1000:0.0}s + {airtimeMs / 1000:0.00}s > {_budgetMs / 1000:0.0}s/hr";
                token = 0;
                return false;
            }
            token = ++_nextToken;
            _window.Add((token, now, airtimeMs));
            _sumMs += airtimeMs;
            reason = "";
            return true;
        }
    }

    /// <summary>Convenience overload for callers that never refund (e.g. tests).</summary>
    public bool TryReserve(double airtimeMs, DateTime now, out string reason)
        => TryReserve(airtimeMs, now, out reason, out _);

    /// <summary>Return a specific reservation (by <paramref name="token"/>) — called
    /// when a send that reserved airtime did not go on air. Concurrency-safe: removes
    /// exactly that reservation, not merely the most recent (which, under concurrent
    /// sends, could belong to a different in-flight send).</summary>
    public void Refund(long token)
    {
        lock (_lock)
        {
            var i = _window.FindIndex(e => e.token == token);
            if (i >= 0)
            {
                _sumMs -= _window[i].ms;
                _window.RemoveAt(i);
            }
            if (_window.Count == 0 || _sumMs < 0) _sumMs = 0;
        }
    }

    private void Prune(DateTime now)
    {
        var cutoff = now.AddHours(-1);
        // Entries are appended in time order, so the stale ones are a prefix.
        var drop = 0;
        while (drop < _window.Count && _window[drop].when < cutoff)
            _sumMs -= _window[drop++].ms;
        if (drop > 0) _window.RemoveRange(0, drop);
        if (_window.Count == 0 || _sumMs < 0) _sumMs = 0;
    }
}
