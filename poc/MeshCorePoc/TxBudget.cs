namespace MeshCorePoc;

/// <summary>
/// Self-enforced airtime governor — the centerpiece "good citizen" control for
/// Model A (riding the public preset, where every channel send floods the whole
/// same-preset network). It is a HARD gate, not advisory: a send that would push
/// our trailing-hour airtime over budget is refused (back-pressure), so DAPPS can
/// never burst the shared channel however much traffic the app offers.
///
/// Budget is expressed as airtime seconds per trailing hour. The regulatory limit
/// on the UK 869.4-869.65 sub-band is 10% duty (360 s/hr); a polite DAPPS
/// self-limit on a shared public mesh should be a small fraction of that. The
/// default here is deliberately conservative and is a policy knob.
/// </summary>
public sealed class TxBudget
{
    private readonly double _budgetMs;
    private readonly Queue<(DateTime when, double ms)> _window = new();
    private double _sumMs;

    /// <summary>Conservative default: 30 s/hr ≈ 0.83% duty (12× under the 10% cap).</summary>
    public const double DefaultSecondsPerHour = 30;

    public TxBudget(double secondsPerHour) => _budgetMs = secondsPerHour * 1000.0;

    public double BudgetSeconds => _budgetMs / 1000.0;
    public double UsedSeconds(DateTime now) { Prune(now); return _sumMs / 1000.0; }
    /// <summary>Trailing-hour duty cycle as a percentage.</summary>
    public double DutyPercent(DateTime now) { Prune(now); return _sumMs / 36_000.0; }

    /// <summary>Reserve airtime for one transmission. Returns false (and changes
    /// nothing) if it would exceed the trailing-hour budget.</summary>
    public bool TryReserve(double airtimeMs, DateTime now, out string reason)
    {
        Prune(now);
        if (_sumMs + airtimeMs > _budgetMs)
        {
            reason = $"airtime budget exceeded: used {_sumMs / 1000:0.0}s + {airtimeMs / 1000:0.00}s " +
                     $"> {_budgetMs / 1000:0.0}s/hr";
            return false;
        }
        _window.Enqueue((now, airtimeMs));
        _sumMs += airtimeMs;
        reason = "";
        return true;
    }

    private void Prune(DateTime now)
    {
        var cutoff = now.AddHours(-1);
        while (_window.Count > 0 && _window.Peek().when < cutoff)
            _sumMs -= _window.Dequeue().ms;
        if (_sumMs < 0) _sumMs = 0;
    }
}
