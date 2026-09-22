using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.Services;

/// <summary>
/// In-memory counters and recent-event ring for the dashboard's
/// observability surface (Plan C3). Cheap, no schema, no IO - every
/// hot path can call into this without worrying about latency.
///
/// Lifetimes: counters are process-lifetime (reset on restart),
/// recent events bounded at <see cref="MaxRecent"/>. If we eventually
/// want persistent metrics across restarts, this is the obvious
/// place to add a periodic flush - but most operational questions
/// ("did anything fail in the last hour?") are answered by the
/// in-memory copy.
///
/// Plan C3 PR-A - every recorded event is *also* emitted as a
/// structured ILogger.Information line so systemd journal captures
/// it for retrospective greps. The ring buffer is "what just
/// happened" (last 100, dashboard panel); the journal is "what
/// happened ten days ago" (`journalctl -u dapps -g 'forward.*abc1234'`).
/// Same data, two scopes.
///
/// Thread-safety: counters are <c>Interlocked</c>, the per-neighbour
/// dict is <see cref="ConcurrentDictionary{TKey,TValue}"/>, the
/// recent-events queue is <see cref="ConcurrentQueue{T}"/>. Every
/// recorder method is safe to call from any thread.
/// </summary>
public sealed class OperationalMetrics(TimeProvider? timeProviderOpt = null, ILogger<OperationalMetrics>? loggerOpt = null)
{
    public const int MaxRecent = 100;

    private readonly TimeProvider timeProvider = timeProviderOpt ?? TimeProvider.System;
    private readonly ILogger<OperationalMetrics> logger = loggerOpt ?? NullLogger<OperationalMetrics>.Instance;

    private long _forwardAttempts;
    private long _forwardSuccess;
    private long _forwardFailure;
    private long _ttlExpired;
    private long _noRoute;
    private long _agwReconnects;
    private long _inboundConnects;
    private long _hashMismatches;
    private long _probeAttempts;
    private long _probeSuccess;
    private long _probeFailure;
    private long _pollAttempts;
    private long _pollSuccess;
    private long _pollFailure;
    private long _routesLearned;
    private long _peersAgedOut;
    private long _budgetRefusals;

    private DateTime? _agwLastReconnectAt;
    private DateTime? _lastForwardSuccessAt;
    private int _agwReconnectAttempt;
    private DateTime? _agwReconnectNextAtUtc;
    private int _rhpv2ReconnectAttempt;
    private DateTime? _rhpv2ReconnectNextAtUtc;
    private readonly ConcurrentDictionary<string, NeighbourMetrics> _neighbours = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<OperationalEvent> _events = new();

    public void RecordForwardSuccess(string id, string callsign, int bytes)
    {
        Interlocked.Increment(ref _forwardAttempts);
        Interlocked.Increment(ref _forwardSuccess);
        var n = GetOrAdd(callsign);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        n.LastSuccessAt = now;
        n.LastError = null;
        _lastForwardSuccessAt = now;
        Interlocked.Increment(ref n._successCount);
        Push("forward.ok", $"{id} → {callsign} ({bytes} B)");
    }

    public void RecordForwardFailure(string id, string callsign, int bytes, string? error)
    {
        Interlocked.Increment(ref _forwardAttempts);
        Interlocked.Increment(ref _forwardFailure);
        var n = GetOrAdd(callsign);
        n.LastFailureAt = timeProvider.GetUtcNow().UtcDateTime;
        n.LastError = error;
        Interlocked.Increment(ref n._failureCount);
        Push("forward.fail", $"{id} → {callsign} ({bytes} B): {error}");
    }

    public void RecordTtlExpired(string id, string destination)
    {
        Interlocked.Increment(ref _ttlExpired);
        Push("ttl.expired", $"{id} for {destination}");
    }

    public void RecordNoRoute(string id, string destination)
    {
        Interlocked.Increment(ref _noRoute);
        Push("route.none", $"{id} for {destination}");
    }

    public void RecordAgwReconnect()
    {
        Interlocked.Increment(ref _agwReconnects);
        _agwLastReconnectAt = timeProvider.GetUtcNow().UtcDateTime;
        Push("agw.reconnect", "AGW socket connected + 'X' registered");
    }

    /// <summary>
    /// Publishes the current sliding-scale reconnect state for one of the
    /// inbound bearer services (see <see cref="ReconnectBackoffSchedule"/>),
    /// so <c>/Operational</c> can show "retrying in Xs" for whichever
    /// bearer is active without the snapshot builder needing to know
    /// about <c>AgwInboundService</c> / <c>Rhpv2InboundService</c>
    /// directly. Called on every backoff state change - failure,
    /// success/reset, and (implicitly, via a zero streak) idle.
    /// </summary>
    public void RecordReconnectBackoff(string bearer, int failureStreak, DateTime? nextRetryAtUtc)
    {
        if (string.Equals(bearer, "rhpv2", StringComparison.OrdinalIgnoreCase))
        {
            _rhpv2ReconnectAttempt = failureStreak;
            _rhpv2ReconnectNextAtUtc = nextRetryAtUtc;
        }
        else
        {
            _agwReconnectAttempt = failureStreak;
            _agwReconnectNextAtUtc = nextRetryAtUtc;
        }
    }

    public void RecordInboundConnect(string remote)
    {
        Interlocked.Increment(ref _inboundConnects);
        Push("inbound.connect", $"L2 SABM from {remote}");
    }

    public void RecordHashMismatch(string id, string source)
    {
        Interlocked.Increment(ref _hashMismatches);
        Push("hash.mismatch", $"{id} from {source}");
    }

    public void RecordProbeOutcome(string callsign, bool success, string? error)
    {
        Interlocked.Increment(ref _probeAttempts);
        if (success)
        {
            Interlocked.Increment(ref _probeSuccess);
            Push("probe.ok", $"reached {callsign}");
        }
        else
        {
            Interlocked.Increment(ref _probeFailure);
            Push("probe.fail", $"{callsign}: {error}");
        }
    }

    public void RecordPollOutcome(string callsign, bool success, long messagesDrained, string? error)
    {
        Interlocked.Increment(ref _pollAttempts);
        if (success)
        {
            Interlocked.Increment(ref _pollSuccess);
            Push("poll.ok", messagesDrained > 0
                ? $"{callsign} drained {messagesDrained}"
                : $"{callsign} (empty)");
        }
        else
        {
            Interlocked.Increment(ref _pollFailure);
            Push("poll.fail", $"{callsign}: {error}");
        }
    }

    public void RecordRouteLearned(string destination, string nextHop)
    {
        Interlocked.Increment(ref _routesLearned);
        Push("route.learned", $"{destination} via {nextHop}");
    }

    public void RecordPeerAgedOut(string callsign, string bearer, string channelKey)
    {
        Interlocked.Increment(ref _peersAgedOut);
        Push("peer.aged", $"{callsign} on {bearer}/{channelKey}");
    }

    public void RecordBudgetRefused(string reason)
    {
        Interlocked.Increment(ref _budgetRefusals);
        Push("budget.refused", reason);
    }

    public Snapshot Take()
    {
        return new Snapshot(
            ForwardAttempts: Interlocked.Read(ref _forwardAttempts),
            ForwardSuccess: Interlocked.Read(ref _forwardSuccess),
            ForwardFailure: Interlocked.Read(ref _forwardFailure),
            TtlExpiredDrops: Interlocked.Read(ref _ttlExpired),
            NoRouteSkips: Interlocked.Read(ref _noRoute),
            AgwReconnects: Interlocked.Read(ref _agwReconnects),
            AgwLastReconnectAt: _agwLastReconnectAt,
            AgwReconnectAttempt: _agwReconnectAttempt,
            AgwReconnectNextAtUtc: _agwReconnectNextAtUtc,
            Rhpv2ReconnectAttempt: _rhpv2ReconnectAttempt,
            Rhpv2ReconnectNextAtUtc: _rhpv2ReconnectNextAtUtc,
            InboundConnects: Interlocked.Read(ref _inboundConnects),
            HashMismatches: Interlocked.Read(ref _hashMismatches),
            ProbeAttempts: Interlocked.Read(ref _probeAttempts),
            ProbeSuccess: Interlocked.Read(ref _probeSuccess),
            ProbeFailure: Interlocked.Read(ref _probeFailure),
            PollAttempts: Interlocked.Read(ref _pollAttempts),
            PollSuccess: Interlocked.Read(ref _pollSuccess),
            PollFailure: Interlocked.Read(ref _pollFailure),
            RoutesLearned: Interlocked.Read(ref _routesLearned),
            PeersAgedOut: Interlocked.Read(ref _peersAgedOut),
            BudgetRefusals: Interlocked.Read(ref _budgetRefusals),
            LastForwardSuccessAt: _lastForwardSuccessAt,
            Neighbours: _neighbours
                .Select(kv => new NeighbourSnapshot(
                    kv.Key,
                    kv.Value.LastSuccessAt,
                    kv.Value.LastFailureAt,
                    kv.Value.LastError,
                    Interlocked.Read(ref kv.Value._successCount),
                    Interlocked.Read(ref kv.Value._failureCount)))
                .OrderBy(n => n.Callsign)
                .ToList(),
            RecentEvents: _events.ToArray().Reverse().ToList());
    }

    /// <summary>The latest successful forward across any neighbour, or null
    /// if we've never forwarded a message. Used by /Health as a crude
    /// "is the node actually doing anything?" liveness signal.</summary>
    public DateTime? LastForwardSuccessAt => _lastForwardSuccessAt;

    private NeighbourMetrics GetOrAdd(string callsign) =>
        _neighbours.GetOrAdd(callsign, _ => new NeighbourMetrics());

    private void Push(string kind, string summary)
    {
        _events.Enqueue(new OperationalEvent(timeProvider.GetUtcNow().UtcDateTime, kind, summary));
        while (_events.Count > MaxRecent)
        {
            _events.TryDequeue(out _);
        }
        // Plan C3 PR-A - also emit as a structured log line so systemd
        // journal captures every event. Greppable: the {Kind} placeholder
        // makes `journalctl -u dapps -g 'forward.fail'` find them, the
        // {Summary} carries any message id / callsign / error so the
        // grep can narrow further (`-g 'abc1234'`, `-g 'G0CALL'`).
        // Information-level - these are decisions, not warnings; every
        // failure has paired counter state for "is this getting worse?".
        logger.LogInformation("event {Kind} {Summary}", kind, summary);
    }

    private sealed class NeighbourMetrics
    {
        internal long _successCount;
        internal long _failureCount;
        public DateTime? LastSuccessAt;
        public DateTime? LastFailureAt;
        public string? LastError;
    }

    public sealed record OperationalEvent(DateTime At, string Kind, string Summary);

    public sealed record NeighbourSnapshot(
        string Callsign,
        DateTime? LastSuccessAt,
        DateTime? LastFailureAt,
        string? LastError,
        long SuccessCount,
        long FailureCount);

    public sealed record Snapshot(
        long ForwardAttempts,
        long ForwardSuccess,
        long ForwardFailure,
        long TtlExpiredDrops,
        long NoRouteSkips,
        long AgwReconnects,
        DateTime? AgwLastReconnectAt,
        int AgwReconnectAttempt,
        DateTime? AgwReconnectNextAtUtc,
        int Rhpv2ReconnectAttempt,
        DateTime? Rhpv2ReconnectNextAtUtc,
        long InboundConnects,
        long HashMismatches,
        long ProbeAttempts,
        long ProbeSuccess,
        long ProbeFailure,
        long PollAttempts,
        long PollSuccess,
        long PollFailure,
        long RoutesLearned,
        long PeersAgedOut,
        long BudgetRefusals,
        DateTime? LastForwardSuccessAt,
        IReadOnlyList<NeighbourSnapshot> Neighbours,
        IReadOnlyList<OperationalEvent> RecentEvents);
}
