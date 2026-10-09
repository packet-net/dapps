using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Routing;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan B6.1 - connected-mode probe-and-map scheduler. When
/// <see cref="SystemOptions.ProbingEnabled"/> is true, runs a slow
/// sweep of every known peer (manual <see cref="DbNeighbour"/> +
/// AGW-bearer <see cref="DbDiscoveredPeer"/>, less opt-outs) and
/// records reachability into <see cref="DbProbedNode"/>.
///
/// Cadence: a startup grace, then a sweep every
/// <see cref="SystemOptions.ProbeIntervalHours"/> hours. Within a
/// sweep, individual probes are spaced by a small random jitter so
/// we don't burst every BPQ on the network simultaneously. Off-by-
/// default - sysops opt in.
///
/// This class is the schedule + state-update half of B6.1; the actual
/// probe transaction lives in <see cref="NodeProber"/>. Operator-
/// triggered probes from the REST surface use the same prober but
/// bypass this scheduler.
/// </summary>
public sealed class ProbeSchedulerService(
    NodeProber prober,
    Database database,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<ProbeSchedulerService> logger,
    AirtimeAccountant? airtime = null,
    OutboundActivityTracker? activityTracker = null,
    OperationalMetrics? metrics = null,
    TransmissionAuditService? transmissionAudit = null,
    IRoutingAlgorithm? routingAlgorithm = null,
    IRoutingContext? routingContext = null) : BackgroundService
{
    /// <summary>
    /// Plan B7 - clock the strategy dispatcher consults for "what time
    /// is it locally?". Production uses the system local-time zone;
    /// tests inject a deterministic offset so they don't depend on the
    /// CI host's TZ. (TimeProvider.LocalTimeZone is a recent addition
    /// - see <see cref="LocalTimeZone"/>.)
    /// </summary>
    public TimeZoneInfo LocalTimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>How often to wake during the disabled-or-deferred path
    /// when the strategy says "not yet" (Overnight outside window,
    /// WhenQuiet with recent activity). Tunable for tests.</summary>
    public TimeSpan StrategyPollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the first sweep after startup. Long enough
    /// for AGW reconnect, MQTT broker init, and the first beacon round
    /// to land - probing into a node that just booted produces noise
    /// rather than signal.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Bounds on the per-probe jitter. Picks a uniform random
    /// duration inside this window between consecutive probes within a
    /// single sweep, so two nodes running on the same cron offset don't
    /// dial the same BPQ at the same instant.</summary>
    public TimeSpan MinInterProbeDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxInterProbeDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to re-check whether probing has been enabled
    /// while the service has been idling. Probing toggles via /Config
    /// without a restart, so we can't sleep through the
    /// disabled-then-enabled transition.</summary>
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup grace. Tests use a tiny StartupDelay; production
        // defaults to 15 minutes so other hosted services have a
        // chance to settle.
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        DateTime? lastSweepCompletedAt = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.ProbingEnabled)
            {
                try { await Task.Delay(DisabledPollInterval, timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Plan B7 - strategy dispatcher decides whether THIS tick
            // is the right time to sweep. FixedInterval is the pre-B7
            // shape (sweep, then sleep PIH hours). Overnight runs once
            // per local-time day inside the configured window.
            // WhenQuiet runs on the same fixed cadence but defers if
            // the forwarder is currently busy.
            var decision = ShouldRunSweep(opts, lastSweepCompletedAt);
            if (!decision.RunNow)
            {
                logger.LogDebug("Probe sweep deferred: {Reason}", decision.Reason);
                try { await Task.Delay(decision.SleepFor, timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await SweepAsync(opts, stoppingToken);
                lastSweepCompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Probe sweep failed");
            }

            try { await Task.Delay(decision.SleepFor, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Plan B7 - strategy-aware "is it time to sweep?" decision. Pure
    /// function of (now, options, last-sweep-time, forwarder-activity);
    /// no side effects. Returned <c>SleepFor</c> is the interval to
    /// wait before the next decision (whether or not we sweep this
    /// tick).
    /// </summary>
    public Decision ShouldRunSweep(SystemOptions opts, DateTime? lastSweepCompletedAt)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var hours = Math.Max(1, opts.ProbeIntervalHours);
        var fixedInterval = TimeSpan.FromHours(hours);

        switch (opts.ProbeStrategy)
        {
            case ProbeStrategy.Overnight:
            {
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, LocalTimeZone);
                var inWindow = IsInOvernightWindow(nowLocal.Hour, opts.ProbeOvernightStartHour, opts.ProbeOvernightEndHour);
                var sweptRecently = lastSweepCompletedAt is { } at
                    && (nowUtc - at) < TimeSpan.FromHours(23);
                if (inWindow && !sweptRecently)
                {
                    return new Decision(true, StrategyPollInterval, "in overnight window, no sweep within last 23h");
                }
                return new Decision(false, StrategyPollInterval,
                    inWindow ? "in overnight window but already swept today" : "outside overnight window");
            }
            case ProbeStrategy.WhenQuiet:
            {
                var idle = activityTracker?.IdleFor();
                var quietWindow = TimeSpan.FromSeconds(Math.Max(1, opts.ProbeQuietWindowSeconds));
                var quiet = idle is null || idle >= quietWindow;
                if (quiet)
                {
                    return new Decision(true, fixedInterval, "forwarder quiet for {idle}");
                }
                // Recheck soon - quiet windows arrive on the order of
                // forwarder ticks (5s), no point sleeping a full hour.
                return new Decision(false, TimeSpan.FromSeconds(Math.Max(15, opts.ProbeQuietWindowSeconds / 4)),
                    $"forwarder active ({idle} ago), waiting for quiet");
            }
            case ProbeStrategy.FixedInterval:
            default:
                return new Decision(true, fixedInterval, "fixed-interval cadence");
        }
    }

    /// <summary>True if <paramref name="hour"/> ∈ [start, end). Handles
    /// the straddle-midnight case (start &gt; end means 22→6 type
    /// windows). Edge case: start == end is treated as "always" so
    /// nobody accidentally locks themselves out by mis-typing.</summary>
    internal static bool IsInOvernightWindow(int hour, int start, int end)
    {
        if (start == end) return true;
        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
    }

    public sealed record Decision(bool RunNow, TimeSpan SleepFor, string Reason);

    /// <summary>Walk every eligible probe target once, sequentially with
    /// jitter between probes. Public so a triggered "sweep now" path
    /// can call into the same machinery; the scheduler loop calls it
    /// internally on its cadence. Plan B7: budget-aware - when the
    /// airtime accountant says no, the remaining targets are skipped
    /// for this sweep and resume next time. Operator-triggered
    /// single-callsign probes from <see cref="ProbeAndRecordAsync"/>
    /// bypass the budget; that's an explicit human action.</summary>
    public async Task SweepAsync(SystemOptions opts, CancellationToken ct)
    {
        var targets = await EnumerateTargets(opts);
        if (targets.Count == 0)
        {
            logger.LogDebug("Probe sweep: no eligible targets");
            return;
        }

        logger.LogInformation("Probe sweep: {0} target(s)", targets.Count);
        // Probe sessions go over AGW (B6.1 is AGW-only by design); use
        // the VHF/UHF-FM estimate as the per-probe airtime cost.
        // Refining this per-callsign would need to know each peer's
        // link class, which we don't reliably have for non-discovered
        // manual neighbours.
        var probeCost = LinkClassDefaults.AirtimeSecondsEstimate(LinkClass.VhfUhfFm, AirtimeKind.ProbeSession);

        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = targets[i];

            if (airtime is { } acct && !acct.TryReserve(probeCost, $"probe {t.Callsign}"))
            {
                logger.LogInformation(
                    "Probe sweep stopping early: airtime budget exhausted after {0}/{1} probe(s)",
                    i, targets.Count);
                return;
            }

            await ProbeAndRecordAsync(opts.Callsign, t.Callsign, t.BearerPort, ct);
            if (i < targets.Count - 1)
            {
                var jitter = RandomDelayMs(MinInterProbeDelay, MaxInterProbeDelay);
                try { await Task.Delay(TimeSpan.FromMilliseconds(jitter), timeProvider, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Run a single probe and persist the outcome into
    /// <see cref="DbProbedNode"/>. Used by the scheduler and by the
    /// on-demand REST endpoint. Returns the row as written.
    /// <paramref name="fetchPeers"/> controls Plan B6.1 Phase 2
    /// transitive discovery - when true, a successful probe asks the
    /// remote for its peers and stores any new callsigns as candidate
    /// rows for future sweeps. The default is true; opt out by
    /// passing false (e.g. in unit tests that don't model the response).
    /// </summary>
    public async Task<DbProbedNode> ProbeAndRecordAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken ct,
        bool fetchPeers = true,
        string reason = "scheduled probe sweep")
    {
        var (row, _) = await ProbeAndRecordVerboseAsync(localCallsign, remoteCallsign, bearerPort, ct, fetchPeers, reason);
        return row;
    }

    /// <summary>Same as <see cref="ProbeAndRecordAsync"/> but also
    /// returns the underlying <see cref="NodeProber.ProbeResult"/> so
    /// callers (Plan M PR-D - exploration tools) can reason about the
    /// raw peers exchange. Persistence happens identically.
    ///
    /// Plan B6.1 Phase 2b - dispatches to the node-prompt path when the
    /// existing <see cref="DbProbedNode"/> row's <see cref="DbProbedNode.Source"/>
    /// starts with <c>node-prompt:</c>. That marker is set by
    /// <see cref="DiscoveryService"/>'s auto-discovery seeder (or by an
    /// operator who manually inserted the row). Direct callers
    /// (operator-triggered <c>run_probe</c>, regular sweeps) hit the
    /// standard DAPPS-callsign path automatically when no such marker
    /// is present.</summary>
    public async Task<(DbProbedNode Row, NodeProber.ProbeResult Result)> ProbeAndRecordVerboseAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken ct,
        bool fetchPeers = true,
        string reason = "scheduled probe sweep")
    {
        var existing = await database.GetProbedNode(remoteCallsign);
        var useNodePrompt = existing is not null
            && existing.Source.StartsWith("node-prompt:", StringComparison.OrdinalIgnoreCase);

        // If the operator's configured a connect-script for this peer
        // (via a manually-added neighbour row), use it for the probe.
        // The probe replays the same chain the forwarder would, so a
        // green probe accurately reflects forwarder reachability.
        dapps.client.ConnectScript? connectScript = null;
        string? connectVia = null;
        if (!useNodePrompt)
        {
            var nb = await database.GetNeighbour(remoteCallsign);
            connectScript = dapps.client.ConnectScript.FromJson(nb?.ConnectScriptJson);
            if (connectScript is not null) connectVia = nb?.ConnectVia;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = useNodePrompt
            ? await prober.ProbeViaNodeCallAsync(localCallsign, remoteCallsign, bearerPort, ct,
                applicationCommand: options.CurrentValue.NodePromptApplicationCommand,
                fetchPeers: fetchPeers)
            : await prober.ProbeAsync(localCallsign, remoteCallsign, bearerPort, ct, fetchPeers,
                connectScript: connectScript, connectVia: connectVia);
        sw.Stop();
        var row = await RecordResultAsync(result);
        if (result.Success && result.DiscoveredPeers.Count > 0)
        {
            await PersistTransitiveDiscoveriesAsync(result);

            // B6.1 follow-up: feed the (peer, asked-peer) signal into
            // the routing algorithm's learned-route graph too. The
            // probe scheduler stores transitive discoveries as
            // DbProbedNode candidates; without this hook, the routing
            // resolver wouldn't see them until a future inbound
            // message taught the same fact passively. The algorithm
            // applies its own filters (next-hop must be a manual
            // neighbour, etc.) and is a noop on Static.
            if (routingAlgorithm is not null && routingContext is not null)
            {
                try
                {
                    await routingAlgorithm.ObserveProbeOutcomeAsync(
                        result.Callsign, result.DiscoveredPeers, routingContext, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Routing algorithm rejected probe-outcome observation for {0}",
                        result.Callsign);
                }
            }
        }
        if (transmissionAudit is { } ta)
        {
            await ta.RecordAsync(
                kind: useNodePrompt ? "probe-nodeprompt" : "probe",
                bearer: BearerLabel(options.CurrentValue),
                channelKey: bearerPort.ToString(),
                targetCallsign: remoteCallsign,
                reason: reason,
                success: result.Success,
                durationMs: (int)sw.ElapsedMilliseconds,
                errorTag: result.Success ? "" : (result.Error ?? "unknown"));
        }
        return (row, result);
    }

    /// <summary>Apply a probe result to the persisted row (insert if
    /// missing, update otherwise). Preserves <see cref="DbProbedNode.OptOut"/>
    /// across updates and bumps <c>ConsecutiveFailures</c> /
    /// <c>SuccessCount</c>. Sets <see cref="DbProbedNode.Source"/> on
    /// first insert (defaulting to <c>neighbour</c>); never overwrites
    /// it on update - the row's origin is a fact about how we first
    /// heard about the callsign and shouldn't drift over time.</summary>
    public async Task<DbProbedNode> RecordResultAsync(NodeProber.ProbeResult result)
    {
        var existing = await database.GetProbedNode(result.Callsign);
        var row = existing ?? new DbProbedNode
        {
            Callsign = result.Callsign,
            // First time we've ever recorded this callsign - assume
            // it's a neighbour-class probe target. Transitively-
            // discovered candidates set their own Source explicitly
            // before the first probe runs (see PersistTransitiveDiscoveriesAsync).
            Source = "neighbour",
        };
        row.LastBearerPort = result.BearerPort;
        row.LastProbedAt = result.At;
        if (result.Success)
        {
            row.LastSuccessAt = result.At;
            row.LastError = "";
            row.ConsecutiveFailures = 0;
            row.SuccessCount = unchecked(row.SuccessCount + 1);
        }
        else
        {
            row.LastError = result.Error;
            row.ConsecutiveFailures = unchecked(row.ConsecutiveFailures + 1);
        }
        await database.UpsertProbedNode(row);
        metrics?.RecordProbeOutcome(result.Callsign, result.Success, result.Error);
        return row;
    }

    /// <summary>
    /// Plan B6.1 Phase 2 - record transitively-discovered callsigns as
    /// candidate <see cref="DbProbedNode"/> rows. Skips callsigns we
    /// already track (we don't want hearsay overwriting fresher first-
    /// hand probe state) and skips ourselves (the remote always reports
    /// us as a peer, since we just talked to them - recording that is
    /// noise). Records the asking peer's callsign in <c>Source</c> so a
    /// sysop can tell where each candidate came from. Best guess for
    /// the port is the same port we just probed the source peer on -
    /// usually right when the network is one shared frequency, often
    /// wrong otherwise; the next sweep surfaces the mismatch as a
    /// regular probe failure.
    /// </summary>
    private async Task PersistTransitiveDiscoveriesAsync(NodeProber.ProbeResult result)
    {
        var ourCallsign = options.CurrentValue.Callsign;
        foreach (var p in result.DiscoveredPeers)
        {
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (string.Equals(p.Callsign, ourCallsign, StringComparison.OrdinalIgnoreCase)) continue;

            var existing = await database.GetProbedNode(p.Callsign);
            if (existing is not null) continue;   // we already track this - don't clobber direct state

            await database.UpsertProbedNode(new DbProbedNode
            {
                Callsign = p.Callsign.ToUpperInvariant(),
                LastBearerPort = p.BearerPort ?? result.BearerPort,
                Source = $"via:{result.Callsign}",
                // No probe attempt yet - leave LastProbedAt / LastSuccessAt
                // null; the scheduler picks it up on the next sweep.
            });
            logger.LogInformation(
                "Transitive discovery: {0} via {1} (port {2})",
                p.Callsign, result.Callsign, p.BearerPort ?? result.BearerPort);
        }
    }

    /// <summary>
    /// Build the eligible target list for a sweep. Sources:
    /// <see cref="DbNeighbour"/> rows without a UDP endpoint (AGW-routable),
    /// AGW-bearer <see cref="DbDiscoveredPeer"/> rows, and Phase-2
    /// transitive candidates - <see cref="DbProbedNode"/> rows whose
    /// <c>Source</c> starts with <c>via:</c> (i.e. learned from another
    /// peer's <c>peers</c> response, not yet on either of the other
    /// two surfaces). Per-callsign opt-outs from
    /// <see cref="DbProbedNode.OptOut"/> are honoured. Port preference
    /// (highest precedence first): neighbour's <c>BearerPort</c>, peer's
    /// observed <c>BearerPort</c>, candidate's stored <c>LastBearerPort</c>,
    /// <see cref="SystemOptions.DefaultBearerPort"/>.
    /// </summary>
    public async Task<IReadOnlyList<ProbeTarget>> EnumerateTargets(SystemOptions opts)
    {
        var neighbours = await database.GetNeighbours();
        var peers = await database.GetDiscoveredPeers();
        var probed = await database.GetProbedNodes();

        var optOut = probed
            .Where(p => p.OptOut)
            .Select(p => p.Callsign)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = new Dictionary<string, ProbeTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            if (n.UdpEndpoint is not null) continue;   // UDP path - no AGW probe possible
            if (optOut.Contains(n.Callsign)) continue;
            targets[n.Callsign] = new ProbeTarget(n.Callsign, n.BearerPort ?? opts.DefaultBearerPort);
        }

        foreach (var p in peers)
        {
            if (!string.Equals(p.Bearer, "agw", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (optOut.Contains(p.Callsign)) continue;
            // Don't overwrite a manual neighbour's port with a guessed one.
            if (targets.ContainsKey(p.Callsign)) continue;
            targets[p.Callsign] = new ProbeTarget(p.Callsign, p.BearerPort ?? opts.DefaultBearerPort);
        }

        // Phase 2 transitive candidates - DbProbedNode rows whose Source
        // says they were learned via someone else's peers list. Without
        // this, a transitively-discovered callsign would sit in the
        // table forever waiting for an operator to manually probe it.
        foreach (var c in probed)
        {
            if (c.OptOut) continue;
            if (string.IsNullOrWhiteSpace(c.Callsign)) continue;
            if (!c.Source.StartsWith("via:", StringComparison.OrdinalIgnoreCase)) continue;
            if (targets.ContainsKey(c.Callsign)) continue;
            targets[c.Callsign] = new ProbeTarget(c.Callsign, c.LastBearerPort ?? opts.DefaultBearerPort);
        }

        return targets.Values
            .OrderBy(t => t.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RandomDelayMs(TimeSpan min, TimeSpan max)
    {
        var lo = (int)Math.Max(0, min.TotalMilliseconds);
        var hi = (int)Math.Max(lo + 1, max.TotalMilliseconds);
        return Random.Shared.Next(lo, hi);
    }

    /// <summary>Bearer label used in transmission-audit rows. Reflects the
    /// configured <see cref="SystemOptions.NodeBearer"/> so XR-side
    /// daemons audit as <c>rhpv2</c> rather than the historical <c>agw</c>
    /// hardcode.</summary>
    private static string BearerLabel(SystemOptions opts) =>
        string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase) ? "rhpv2" : "agw";

    public sealed record ProbeTarget(string Callsign, int BearerPort);
}
