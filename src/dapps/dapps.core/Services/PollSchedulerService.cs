using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan F3b - connected-mode scheduled poll. When
/// <see cref="SystemOptions.ScheduledPollEnabled"/> is true, runs a
/// slow sweep of every AGW-reachable manual <see cref="DbNeighbour"/>
/// row: calls it, takes whatever it sends through the regular inbox
/// path, and hangs up when the link goes quiet (<see cref="NodePoller"/>).
/// Off by default - every session carries traffic both ways, so mail
/// usually rides a call made for other traffic; this service is for
/// nodes that don't send often (read-only consumers, scheduled HF
/// stations) and would otherwise let mail rot at their forwarding
/// partners.
///
/// Sweeps live in <see cref="DbPolledNode"/> for dashboard surface.
/// On-demand polls (<c>POST /Polls/run/{callsign}</c>) bypass the
/// schedule but still update the persisted state.
/// </summary>
public sealed class PollSchedulerService(
    NodePoller poller,
    Database database,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<PollSchedulerService> logger,
    OperationalMetrics? metrics = null,
    TransmissionAuditService? transmissionAudit = null) : BackgroundService
{
    /// <summary>Delay before the first sweep after startup. Long enough
    /// for AGW reconnect, MQTT broker init to settle. Tunable for
    /// tests via init-only setter.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Bounds on per-poll jitter so two co-scheduled nodes
    /// don't dial the same partner at the same instant.</summary>
    public TimeSpan MinInterPollDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxInterPollDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Re-check whether ScheduledPollEnabled has been flipped
    /// by /Config while we sleep. Without this, an operator turning
    /// on polling has to wait through one whole disabled tick before
    /// it kicks in.</summary>
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.ScheduledPollEnabled)
            {
                try { await Task.Delay(DisabledPollInterval, timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try { await SweepAsync(opts, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Poll sweep failed"); }

            var hours = Math.Max(1, opts.PollIntervalHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Walk every eligible neighbour once, sequentially with
    /// jitter between polls. Public so the on-demand REST sweep path
    /// can call into the same machinery.</summary>
    public async Task SweepAsync(SystemOptions opts, CancellationToken ct)
    {
        var targets = await EnumerateTargetsAsync();
        if (targets.Count == 0)
        {
            logger.LogDebug("Poll sweep: no eligible targets");
            return;
        }

        logger.LogInformation("Poll sweep: {0} target(s)", targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = targets[i];
            await PollAndRecordAsync(opts.Callsign, t.Callsign, t.BearerPort, ct);
            if (i < targets.Count - 1)
            {
                var jitterMs = RandomDelayMs(MinInterPollDelay, MaxInterPollDelay);
                try { await Task.Delay(TimeSpan.FromMilliseconds(jitterMs), timeProvider, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Run a single poll and persist the outcome into
    /// <see cref="DbPolledNode"/>. Used by the scheduler and the
    /// on-demand REST endpoint.</summary>
    public async Task<DbPolledNode> PollAndRecordAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken ct,
        string reason = "scheduled poll sweep")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Pass any configured connect-script so a multi-hop neighbour
        // gets the same chained-connect treatment for poll as for push.
        var nb = await database.GetNeighbour(remoteCallsign);
        var connectScript = dapps.client.ConnectScript.FromJson(nb?.ConnectScriptJson);
        var result = await poller.PollAsync(localCallsign, remoteCallsign, bearerPort, ct, connectScript,
            connectVia: connectScript is null ? null : nb?.ConnectVia);
        sw.Stop();
        var row = await RecordResultAsync(result);
        if (transmissionAudit is { } ta)
        {
            await ta.RecordAsync(
                kind: "poll",
                bearer: string.Equals(options.CurrentValue.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase) ? "rhpv2" : "agw",
                channelKey: bearerPort.ToString(),
                targetCallsign: remoteCallsign,
                reason: result.Success
                    ? $"{reason} (drained {result.MessagesDrained})"
                    : reason,
                success: result.Success,
                durationMs: (int)sw.ElapsedMilliseconds,
                errorTag: result.Success ? "" : (result.Error ?? "unknown"));
        }
        return row;
    }

    /// <summary>Apply a result to the persisted row. Preserves
    /// <see cref="DbPolledNode.OptOut"/> across updates and updates
    /// the cumulative MessagesDrained tally on success.</summary>
    public async Task<DbPolledNode> RecordResultAsync(NodePoller.PollResult result)
    {
        var existing = await database.GetPolledNode(result.Callsign);
        var row = existing ?? new DbPolledNode { Callsign = result.Callsign };
        row.LastPolledAt = result.At;
        if (result.Success)
        {
            row.LastSuccessAt = result.At;
            row.LastError = "";
            row.ConsecutiveFailures = 0;
            row.MessagesDrained = unchecked(row.MessagesDrained + result.MessagesDrained);
        }
        else
        {
            row.LastError = result.Error;
            row.ConsecutiveFailures = unchecked(row.ConsecutiveFailures + 1);
        }
        await database.UpsertPolledNode(row);
        metrics?.RecordPollOutcome(result.Callsign, result.Success, result.MessagesDrained, result.Error);
        return row;
    }

    /// <summary>
    /// Eligible polling targets: AGW-reachable manual neighbours,
    /// minus any flagged opt-out in <see cref="DbPolledNode"/>. UDP-
    /// only neighbours are excluded - UDP has no session to poll over,
    /// and every datagram goes out as soon as it's queued.
    /// </summary>
    public async Task<IReadOnlyList<PollTarget>> EnumerateTargetsAsync()
    {
        var neighbours = await database.GetNeighbours();
        var polled = await database.GetPolledNodes();
        var optOut = polled
            .Where(p => p.OptOut)
            .Select(p => p.Callsign)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var defaultPort = options.CurrentValue.DefaultBearerPort;
        var targets = new List<PollTarget>();
        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            if (n.UdpEndpoint is not null) continue;
            if (optOut.Contains(n.Callsign)) continue;
            targets.Add(new PollTarget(n.Callsign, n.BearerPort ?? defaultPort));
        }
        return targets
            .OrderBy(t => t.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RandomDelayMs(TimeSpan min, TimeSpan max)
    {
        var lo = (int)Math.Max(0, min.TotalMilliseconds);
        var hi = (int)Math.Max(lo + 1, max.TotalMilliseconds);
        return Random.Shared.Next(lo, hi);
    }

    public sealed record PollTarget(string Callsign, int BearerPort);
}
