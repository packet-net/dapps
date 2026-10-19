namespace dapps.core.Services;

/// <summary>
/// A warning for something that keeps failing on a retry, logged the first
/// time, then at most once per <see cref="Every"/> while it goes on, and an
/// information line once it works again. A node whose XRouter RHPv2 port
/// can't be reached would otherwise log on every 30 s retry, thousands of
/// lines a day.
/// </summary>
internal sealed class FailureLog(ILogger logger, TimeProvider? clock = null)
{
    /// <summary>How often a failure that goes on is logged again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private DateTimeOffset? lastLogged;
    private bool failing;

    /// <summary>It failed again, for <paramref name="reason"/>.</summary>
    public void Failed(string reason)
    {
        bool log;
        bool again;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            again = failing;
            log = !failing || lastLogged is not { } last || now - last >= Every;
            if (log) lastLogged = now;
            failing = true;
        }
        if (log) logger.LogWarning("{0}{1}", reason, again ? " (still failing; logged at most once an hour)" : "");
    }

    /// <summary>It worked. Says so once if it had been failing.</summary>
    public void Worked(string message)
    {
        bool log;
        lock (gate)
        {
            log = failing;
            failing = false;
            lastLogged = null;
        }
        if (log) logger.LogInformation("{0}", message);
    }
}
