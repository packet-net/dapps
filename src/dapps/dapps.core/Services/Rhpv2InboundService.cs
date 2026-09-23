using System.Collections.Concurrent;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>
/// Inbound listener for the RHPv2 bearer. Equivalent of
/// <see cref="AgwInboundService"/> for hosts that expose RHPv2 (XRouter
/// today; future BPQ versions). Maintains one TCP connection to the
/// host's RHPv2 port, opens a passive AX.25 stream socket, binds the
/// local callsign, listens for inbound connects, and dispatches each
/// accepted session into <see cref="InboundConnectionHandler"/> the
/// same way the AGW service does.
///
/// Architectural advantage over AGW for the XRouter case: RHPv2's
/// session-handle model and per-handle event stream means we can
/// share one TCP connection between inbound and outbound work without
/// the AGW per-connection-callsign-claim collision that breaks DAPPS-
/// on-XR via AGW. (The current implementation still uses separate
/// connections for outbound via <c>Rhpv2OutboundTransport</c> for
/// simplicity; sharing the connection is a follow-up optimisation.)
/// </summary>
public sealed class Rhpv2InboundService(
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<Rhpv2InboundService> logger,
    Database database,
    IBackhaulInbox inbox,
    OperationalMetrics metrics,
    IDappsTxGate? txGate = null,
    TimeProvider? timeProvider = null,
    PeerSessionRegistry? peerSessions = null) : BackgroundService
{
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);
    /// <summary>Delay between cycles that ended without a real failure
    /// (idle-gated on missing config, or a cycle cancelled by a /Config
    /// save) - deliberately short and flat; the sliding backoff (see
    /// <see cref="reconnect"/>) only applies to actual connect/bind
    /// failures.</summary>
    private static readonly TimeSpan NonFailureRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly InboundReconnectController reconnect = new(timeProvider ?? TimeProvider.System);
    private CancellationTokenSource? cycleTokenSource;
    private IDisposable? optionsChangeSubscription;

    /// <summary>Operator-triggered "retry now" - see
    /// <see cref="InboundReconnectController.TriggerRetry"/>. Publishes
    /// the collapsed wait immediately so a snapshot fetched right after
    /// the click doesn't show the stale pre-click countdown.</summary>
    public bool TriggerManualRetry()
    {
        var triggered = reconnect.TriggerRetry();
        if (triggered) PublishBackoffState();
        return triggered;
    }

    /// <summary>Mirrors <see cref="reconnect"/>'s current state into
    /// <see cref="OperationalMetrics"/> so <c>/Operational</c> can render
    /// it without depending on this concrete service type.</summary>
    private void PublishBackoffState() =>
        metrics.RecordReconnectBackoff("rhpv2", reconnect.FailureStreak, reconnect.NextRetryAtUtc?.UtcDateTime);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cancel the current connection-cycle on any SystemOptions change
        // so a /Config save (callsign, RHP host/port/auth, bearer flip)
        // takes effect on the next iteration without a daemon restart -
        // including collapsing an in-flight backoff wait, so a fix to
        // the RHP host/port/callsign reconnects immediately rather than
        // sitting out the rest of a (possibly multi-minute) delay.
        optionsChangeSubscription = options.OnChange((_, _) =>
        {
            cycleTokenSource?.Cancel();
            reconnect.Interrupt();
        });
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                cycleTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var cycleCt = cycleTokenSource.Token;
                try
                {
                    await RunOnce(cycleCt);

                    // Clean return - idle-gated on missing config only
                    // (a genuine bind/listen failure now throws - see
                    // BindListenerAsync). Not a hard failure: reset the
                    // backoff, but still pause briefly rather than
                    // spinning. Routed through reconnect.WaitAsync (rather
                    // than a bare Task.Delay) so a manual "retry now" or a
                    // further options change can still collapse this
                    // short wait too; WaitAsync swallows cancellation
                    // internally, so the outer while-condition (not an
                    // explicit return here) is what ends the loop on
                    // shutdown.
                    reconnect.RecordSuccess();
                    PublishBackoffState();
                    await reconnect.WaitAsync(NonFailureRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Cycle cancelled by an options change; not a failure,
                    // but still pause briefly before reconnecting.
                    reconnect.RecordSuccess();
                    PublishBackoffState();
                    await reconnect.WaitAsync(NonFailureRetryDelay, stoppingToken);
                }
                catch (Exception ex)
                {
                    var delay = reconnect.RecordFailure();
                    // Start the wait (which assigns reconnect's internal
                    // cancellation source) before publishing/logging - see
                    // the matching comment in AgwInboundService.RunLoop.
                    var waitTask = reconnect.WaitAsync(delay, stoppingToken);
                    PublishBackoffState();
                    logger.LogWarning(ex,
                        "RHP inbound: connection lost; reconnecting in {0}s (attempt {1}, next at {2:O})",
                        delay.TotalSeconds, reconnect.FailureStreak, reconnect.NextRetryAtUtc);
                    await waitTask;
                }
            }
        }
        finally
        {
            optionsChangeSubscription?.Dispose();
            optionsChangeSubscription = null;
        }
    }

    private async Task RunOnce(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;

        // Bearer-active gate: only run when RHPv2 is configured.
        // AgwInboundService runs alongside us with the matching gate on
        // "agw"; OnChange fires when /Config flips the value.
        if (!string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(IdleBackoff, timeProvider, stoppingToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Callsign)
            || string.Equals(opts.Callsign, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Callsign not configured; RHP inbound idle (waiting for /Setup or /Config)");
            await Task.Delay(IdleBackoff, timeProvider, stoppingToken);
            return;
        }

        var host = string.IsNullOrEmpty(opts.NodeHost) ? "127.0.0.1" : opts.NodeHost;
        var port = opts.RhpPort > 0 ? opts.RhpPort : RhpClient.DefaultPort;

        logger.LogInformation("RHP inbound: connecting to {host}:{port}", host, port);
        await using var rhp = await RhpClient.ConnectAsync(host, port, stoppingToken);
        metrics.RecordAgwReconnect();  // re-using the AGW counter; same operator concept

        // Note: the backoff isn't reset here on the raw TCP connect - a
        // persistent bind/listen failure below (e.g. callsign already
        // claimed) would otherwise see FailureStreak reset to 0 every
        // cycle right before RecordFailure() puts it straight back to 1,
        // permanently capping the delay at the ramp's fastest (10s) tier
        // instead of escalating. RecordSuccess() only fires once the
        // listener is actually bound, below.
        if (!string.IsNullOrEmpty(opts.RhpUser))
        {
            await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", stoppingToken);
        }

        // socket + bind + listen for inbound AX.25 streams to our callsign.
        // Port omitted = listen across all configured XRouter ports.
        // A callsign freshly derived from PDN_NODE_CALLSIGN may probe-
        // walk to a free SSID here; see BindListenerAsync.
        var bound = await BindListenerAsync(rhp, opts, stoppingToken);
        if (bound is null)
        {
            return; // logged inside; the ExecuteAsync loop retries after NonFailureRetryDelay
        }
        var (listenerHandle, boundCallsign) = bound.Value;
        logger.LogInformation("RHP inbound: listener bound to {call} on handle {h}", boundCallsign, listenerHandle);
        reconnect.RecordSuccess();
        PublishBackoffState();

        var sessions = new ConcurrentDictionary<int, MultiplexedAgwSessionStream>();
        var disconnect = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<RhpAcceptedEventArgs> acceptedHandler = (_, e) =>
        {
            if (e.Message.Handle != listenerHandle) return;
            var child = e.Message.Child;
            var remote = e.Message.Remote ?? "(unknown)";
            logger.LogInformation("RHP inbound: ACCEPT child={child} from {remote}", child, remote);
            metrics.RecordInboundConnect(remote);

            // RF-emitting: SendOnHandleAsync emits AX.25 I-frames on this
            // accepted-inbound session. Gate each call so the box stops
            // replying as soon as the operator hits the kill-switch.
            // CloseAsync stays ungated so we can still tear down cleanly.
            var stream = new MultiplexedAgwSessionStream(
                writeOutgoing: async (data, c) =>
                {
                    if (!txGate.TxAllowed)
                    {
                        throw new TxStoppedException(
                            $"RHP inbound send on child {child}: {txGate.BlockReason ?? "(no reason)"}");
                    }
                    await rhp.SendOnHandleAsync(child, data, c);
                },
                sendRemoteDisconnect: async c =>
                {
                    try { await rhp.CloseAsync(child, c); }
                    catch { /* may already be closed */ }
                });

            if (!sessions.TryAdd(child, stream))
            {
                logger.LogWarning("RHP inbound: duplicate child handle {child}; ignoring", child);
                _ = stream.DisposeAsync().AsTask();
                return;
            }

            // #178: while this session is open the forwarder must not dial
            // this peer (see PeerSessionRegistry). Released once the
            // handler is done and the session is closed.
            var lease = e.Message.Remote is { Length: > 0 } peer ? peerSessions?.Acquire(peer, "inbound") : null;

            var handler = new InboundConnectionHandler(
                stream, sourceCallsign: remote, loggerFactory, database, inbox, metrics);

            _ = Task.Run(async () =>
            {
                try { await handler.Handle(stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "RHP inbound: session handler {child} failed", child); }
                finally
                {
                    if (sessions.TryRemove(child, out var s))
                    {
                        try { await s.DisposeAsync(); } catch { }
                    }
                    try { await rhp.CloseAsync(child, CancellationToken.None); } catch { }
                    lease?.Dispose();
                }
            }, stoppingToken);
        };

        EventHandler<RhpReceivedEventArgs> recvHandler = (_, e) =>
        {
            if (sessions.TryGetValue(e.Message.Handle, out var stream))
            {
                var bytes = RhpDataEncoding.FromWireString(e.Message.Data);
                _ = stream.PushIncoming(bytes, stoppingToken);
            }
        };

        EventHandler<RhpClosedEventArgs> closedHandler = (_, e) =>
        {
            if (sessions.TryGetValue(e.Handle, out var stream))
            {
                stream.SignalRemoteDisconnect();
            }
        };

        EventHandler<Exception?> disconnectedHandler = (_, ex) =>
        {
            disconnect.TrySetResult(ex);
        };

        rhp.Accepted += acceptedHandler;
        rhp.Received += recvHandler;
        rhp.Closed += closedHandler;
        rhp.Disconnected += disconnectedHandler;

        try
        {
            // Wait for either: external cancellation, or RhpClient
            // disconnect (TCP socket closed by peer or local error).
            using var reg = stoppingToken.Register(() => disconnect.TrySetCanceled());
            var ex = await disconnect.Task;
            if (ex is not null) throw ex;
        }
        finally
        {
            rhp.Accepted -= acceptedHandler;
            rhp.Received -= recvHandler;
            rhp.Closed -= closedHandler;
            rhp.Disconnected -= disconnectedHandler;
            foreach (var s in sessions.Values) s.SignalRemoteDisconnect();
            sessions.Clear();
        }
    }

    /// <summary>
    /// Bind + listen the daemon's callsign, probing for a free SSID
    /// when the identity is a not-yet-confirmed derivation.
    ///
    /// pdn answers a listen on an already-claimed callsign - including
    /// the node's own - with errCode 9 "Duplicate socket",
    /// deterministically (packet.net docs/rhp2-server.md deviation D5).
    /// That turns the callsign derived from PDN_NODE_CALLSIGN into a
    /// probe: while <see cref="DbStartup.DerivedCallsignPendingKey"/>
    /// still matches the callsign in use (placeholder + PDN_NODE_CALLSIGN
    /// at boot, no explicit DAPPS_CALLSIGN, no successful listen yet), a
    /// 9 walks the candidate SSIDs - start+1 … 15, then 1 … start−1,
    /// skipping 0 and the SSID the node itself uses - and the first
    /// successful listen wins. The winner is persisted as the stored
    /// callsign, so the identity is stable on every later start: a
    /// persisted, confirmed callsign never walks again.
    ///
    /// An explicitly configured callsign (DAPPS_CALLSIGN, dashboard, or
    /// an already-confirmed derivation) NEVER walks: a 9 logs the
    /// refusal and keeps the existing retry/reconnect behaviour.
    ///
    /// Against a server that answers duplicate listens Ok (live XRouter
    /// does - D5 is pdn's deviation from it), the very first listen
    /// succeeds and the walk simply never triggers. That's fine:
    /// derivation only ever runs under pdn supervision, because only a
    /// pdn host injects PDN_NODE_CALLSIGN.
    /// </summary>
    /// <returns>The listener handle and the callsign it is bound to; or
    /// null when a walk exhausts every candidate SSID (config problem,
    /// logged and parked in setup-required mode - not escalated as a
    /// failure). Throws <see cref="InvalidOperationException"/> when an
    /// explicitly-configured callsign is already claimed on the node, so
    /// the caller's sliding backoff escalates instead of retrying flat.</returns>
    private async Task<(int Handle, string Callsign)?> BindListenerAsync(
        RhpClient rhp, SystemOptions opts, CancellationToken ct)
    {
        var pending = DbStartup.ReadPendingDerivedCallsign();
        var walkEligible =
            pending is not null
            && string.Equals(pending, opts.Callsign, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DbStartup.EnvVarFor("Callsign")))
            // Node-owned-callsign contract: a host-assigned PDN_APP_CALLSIGN
            // is bound verbatim - never probe-walk off it.
            && DbStartup.ReadNodeAssignedCallsign() is null;

        IReadOnlyList<string> candidates = walkEligible
            ? SsidProbeCandidates(opts.Callsign, Environment.GetEnvironmentVariable(DbStartup.NodeCallsignEnvVar))
            : [opts.Callsign];
        var taken = new List<string>();

        foreach (var candidate in candidates)
        {
            var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, ct);
            try
            {
                await rhp.BindAsync(handle, local: candidate, port: null, ct);
                await rhp.ListenAsync(handle, OpenFlags.Passive, ct);
            }
            catch (RhpServerException ex) when (ex.ErrorCode == RhpErrorCode.DuplicateSocket)
            {
                try { await rhp.CloseAsync(handle, ct); }
                catch (RhpProtocolException) { /* refused handles may already be gone server-side */ }

                if (!walkEligible)
                {
                    // A real, potentially-transient failure (the node may
                    // free the callsign up later) - not config the loop
                    // should idle-gate on. Throw so ExecuteAsync's
                    // exception path escalates the sliding backoff
                    // instead of treating this as a clean, non-failure
                    // return and hammering the node every
                    // NonFailureRetryDelay forever.
                    var message =
                        $"RHP inbound: callsign {candidate} is already claimed on the node (errCode 9 'Duplicate socket'). " +
                        "It is explicitly configured, so not probing for a free SSID. " +
                        "Pick a different callsign via the dashboard or DAPPS_CALLSIGN if this persists.";
                    logger.LogWarning(message);
                    throw new InvalidOperationException(message);
                }

                taken.Add(candidate);
                continue;
            }

            if (walkEligible)
            {
                // First successful listen confirms the derived identity -
                // persist it so every later start binds it directly.
                DbStartup.ConfirmDerivedCallsign(candidate);
                if (taken.Count > 0)
                {
                    logger.LogInformation(
                        "RHP inbound: derived callsign {winner} — {taken} was taken on the node",
                        candidate, string.Join(", ", taken.Select(t => $"-{t.Split('-')[^1]}")));
                    // Reload so every consumer (outbound forwarder,
                    // beacons, UI) sees the confirmed identity. The
                    // OnChange this fires cancels the current cycle; the
                    // reconnect binds the winner via the normal
                    // non-walking path (the marker is cleared).
                    ReloadOptionsStore();
                }
            }

            return (handle, candidate);
        }

        // Every candidate SSID is taken on the node. Park in setup-
        // required mode rather than hammering the node every reconnect;
        // the operator pins an identity via the dashboard /
        // DAPPS_CALLSIGN, or the next daemon restart re-derives and
        // probes again.
        logger.LogError(
            "RHP inbound: no free SSID for the derived callsign {call} — every candidate ({candidates}) is taken " +
            "on the node. Reverting to setup-required mode; configure a callsign via the dashboard or DAPPS_CALLSIGN.",
            opts.Callsign, string.Join(", ", candidates));
        DbStartup.AbandonDerivedCallsign();
        ReloadOptionsStore();
        return null;
    }

    /// <summary>
    /// The SSID probe order for a derived callsign: the derivation
    /// itself first, then the SSIDs after it in order (start+1 … 15,
    /// then wrapping to 1 … start−1), skipping 0 (the node's bare
    /// callsign) and the SSID the node itself uses (parsed off
    /// PDN_NODE_CALLSIGN).
    /// </summary>
    internal static IReadOnlyList<string> SsidProbeCandidates(string derivedCallsign, string? nodeCallsign)
    {
        var dash = derivedCallsign.LastIndexOf('-');
        var baseCall = dash > 0 ? derivedCallsign[..dash] : derivedCallsign;
        var start = dash > 0 && int.TryParse(derivedCallsign[(dash + 1)..], out var s) ? s : 0;

        var nodeSsid = 0;
        if (!string.IsNullOrWhiteSpace(nodeCallsign))
        {
            var nodeDash = nodeCallsign.LastIndexOf('-');
            if (nodeDash > 0 && int.TryParse(nodeCallsign[(nodeDash + 1)..], out var ns)) nodeSsid = ns;
        }

        var candidates = new List<string>(16) { derivedCallsign };
        for (var offset = 1; offset <= 15; offset++)
        {
            var ssid = ((start - 1 + offset) % 15) + 1; // start+1 … 15, then 1 … start−1; never 0
            if (ssid == start || ssid == nodeSsid) continue;
            candidates.Add($"{baseCall}-{ssid}");
        }
        return candidates;
    }

    private void ReloadOptionsStore()
    {
        // In production IOptionsMonitor<SystemOptions> is the
        // SystemOptionsStore singleton; re-read it so CurrentValue
        // reflects what the probe just persisted.
        (options as SystemOptionsStore)?.Reload();
    }
}
