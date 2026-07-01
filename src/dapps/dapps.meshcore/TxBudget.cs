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
    private readonly List<(DateTime when, double ms)> _window = new();
    private readonly object _lock = new();
    private double _sumMs;

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

    /// <summary>Reserve airtime for one transmission; returns false (changes
    /// nothing) if it would exceed the trailing-hour budget.</summary>
    public bool TryReserve(double airtimeMs, DateTime now, out string reason)
    {
        lock (_lock)
        {
            Prune(now);
            if (_sumMs + airtimeMs > _budgetMs)
            {
                reason = $"airtime budget exceeded: used {_sumMs / 1000:0.0}s + {airtimeMs / 1000:0.00}s > {_budgetMs / 1000:0.0}s/hr";
                return false;
            }
            _window.Add((now, airtimeMs));
            _sumMs += airtimeMs;
            reason = "";
            return true;
        }
    }

    /// <summary>Return the most recent reservation to the budget — called when a
    /// send that reserved airtime did not actually go on air (link not ready /
    /// exception), so a failed attempt doesn't consume the duty budget.</summary>
    public void Refund()
    {
        lock (_lock)
        {
            if (_window.Count > 0)
            {
                _sumMs -= _window[^1].ms;
                _window.RemoveAt(_window.Count - 1);
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
