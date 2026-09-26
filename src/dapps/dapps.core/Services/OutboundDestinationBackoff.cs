using System.Collections.Concurrent;

namespace dapps.core.Services;

/// <summary>
/// Per-destination sliding-scale backoff for outbound message forwarding.
///
/// Without this, a destination that connects fine at the AX.25 link
/// layer but then rejects at the application layer (no DAPPSv1> prompt -
/// e.g. BPQ answering "No AGWPE Host Sessions available" instead of the
/// prompt) got hammered on every <see cref="OutboundForwarderService"/>
/// tick (5s) forever: a failed forward just leaves the message pending,
/// so the very next tick dials the same destination again. Reusing
/// <see cref="ReconnectBackoffSchedule"/> here - one instance per
/// destination callsign, created lazily on first failure - gives the
/// same sliding scale (10s x3, 30s x3, 1min x3, then 5min steady-state)
/// the inbound bearer services use, so a persistently-failing
/// destination stops being dialled every 5s and instead backs off like
/// any other reconnect.
///
/// Deliberately in-memory only (not persisted): a destination's cooldown
/// is about *this process's* recent attempts, and resetting on restart
/// is the right behaviour (give a destination a fresh chance rather than
/// remembering a grudge across restarts).
///
/// Interaction with route learning: a cooldown-skipped tick never calls
/// <see cref="OutboundMessageManager"/>'s forward-observation hook, so
/// e.g. <c>PassiveLearningAlgorithm</c>'s 3-consecutive-failure route
/// invalidation now sees those 3 failures spread across the ramp's
/// escalating delays (roughly 10s+30s+60s) rather than 3 flat 5s ticks
/// (~15s). Slower fallback-route pickup is the intended trade for not
/// hammering a failing destination.
/// </summary>
public sealed class OutboundDestinationBackoff(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ReconnectBackoffSchedule> schedules =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a prior failure to this destination means a
    /// forward attempt should be skipped this tick. <paramref name="nextRetryAtUtc"/>
    /// is populated when the destination has an active cooldown; null
    /// both when there's no schedule for it yet (never failed) and when
    /// its schedule exists but was reset by a subsequent
    /// <see cref="RecordSuccess"/> - either way, null means "nothing to
    /// wait for".</summary>
    public bool IsInCooldown(string destination, out DateTimeOffset? nextRetryAtUtc)
    {
        if (schedules.TryGetValue(destination, out var schedule) && schedule.NextRetryAtUtc is { } at)
        {
            nextRetryAtUtc = at;
            return at > timeProvider.GetUtcNow();
        }
        nextRetryAtUtc = null;
        return false;
    }

    /// <summary>Record a successful forward - resets the destination's
    /// schedule so the next failure starts back at the fast end of the
    /// ramp. A no-op when the destination has no schedule yet (never
    /// failed), so the common all-success case doesn't grow the
    /// dictionary.</summary>
    public void RecordSuccess(string destination)
    {
        if (schedules.TryGetValue(destination, out var schedule))
        {
            schedule.RecordSuccess();
        }
    }

    /// <summary>
    /// When the next destination comes out of cooldown, or null if none
    /// is waiting. The forwarder wakes then instead of polling.
    /// </summary>
    public DateTimeOffset? EarliestRetry()
    {
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? earliest = null;
        foreach (var schedule in schedules.Values)
        {
            if (schedule.NextRetryAtUtc is { } at && at > now && (earliest is null || at < earliest))
            {
                earliest = at;
            }
        }
        return earliest;
    }

    /// <summary>Record a failed forward and return when this destination
    /// is next eligible for a retry - read back from the schedule rather
    /// than computed independently by the caller, so a logged timestamp
    /// can't disagree with the actual cooldown expiry.</summary>
    public DateTimeOffset RecordFailure(string destination)
    {
        var schedule = schedules.GetOrAdd(destination, _ => new ReconnectBackoffSchedule(timeProvider));
        schedule.RecordFailure();
        return schedule.NextRetryAtUtc!.Value; // RecordFailure() just set this
    }
}
