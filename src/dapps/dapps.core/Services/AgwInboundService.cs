using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Inbound bearer: maintains an AGW client connection to BPQ, registers
/// our callsign so BPQ dispatches inbound L2 connects to us, and pumps
/// the resulting per-session byte streams into <see cref="InboundConnectionHandler"/>.
///
/// Replaces the old BPQ-Apps-Interface (HOST/CMDPORT TCP-bridge) inbound
/// path. AGW gives us:
///   - off-host operation (BPQ's HOST handler hard-codes 127.0.0.1; AGW
///     reaches across the network freely),
///   - frame-level visibility (no Telnet line-discipline rewrites),
///   - one TCP connection to BPQ multiplexing all sessions.
///
/// Operator note: BPQ still requires an <c>APPLICATION N,DAPPS,,&lt;CALL&gt;,...</c>
/// line in <c>bpq32.cfg</c> with an *empty* CMD field. Without that
/// line, BPQ's L2 layer doesn't accept frames addressed to the dapps
/// callsign and the AGW <c>'X'</c> registration is silently inert
/// (per linbpq apps-interface.md and AGWAPI.c:1427).
///
/// The connection itself, its registration, keepalive, reader and
/// session table, is an <see cref="AgwLink"/>; this service opens it,
/// publishes it (<see cref="IAgwLinkSource"/>) for our own calls to go
/// out on (#272), runs a handler for each inbound session it reports,
/// and reopens it when it ends. A save that changes the node's identity
/// (callsign, node host, AGW port or bearer) reopens it too, which ends
/// every session on it, outbound ones included.
///
/// When AGW is not the bearer but an AGW discovery channel is running
/// (a <see cref="SubscribeUi"/> subscription is held), the link is opened
/// listen-only (<see cref="AgwLink.ListenOnly"/>): no 'X', no inbound
/// calls, no dials, it only hears the beacons (discovery sends its own
/// over the bearer, <see cref="Rhpv2UiFrameSender"/>). It closes once the last
/// subscription goes. A bearer switch is a save in the restart set, so
/// it reopens the link in the other mode.
///
/// Reconnect policy: on any AGW socket error, back off along
/// <see cref="reconnect"/>'s sliding scale (10s x3, 30s x3, 1min x3, then
/// 5min steady-state) and retry; an operator can jump the queue via the
/// dashboard's "retry now" (<see cref="InboundReconnectController.TriggerRetry"/>).
/// In-flight inbound sessions are lost (their streams get EOF); the
/// sender's bearer surfaces a timeout and retries on its next forwarder
/// run - matches the existing at-least-once semantics. All waits go
/// through the injected <see cref="TimeProvider"/> so tests can drive
/// them with a fake clock.
/// </summary>
public sealed class AgwInboundService(
    IOptionsMonitor<SystemOptions> options,
    Database database,
    IBackhaulInbox inbox,
    ILoggerFactory loggerFactory,
    ILogger<AgwInboundService> logger,
    OperationalMetrics? metrics = null,
    IDappsTxGate? txGate = null,
    TimeProvider? timeProvider = null,
    PeerSessionRegistry? peerSessions = null,
    ExchangePolicy? exchangePolicy = null,
    SessionDirectory? openSessions = null) : IHostedService, IAgwLinkSource
{
    internal static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);
    /// <summary>Delay between cycles that ended without a real failure
    /// (idle-gated on missing config, or a cycle cancelled by a /Config
    /// save) - deliberately short and flat; the sliding backoff only
    /// applies to actual connect/socket failures.</summary>
    internal static readonly TimeSpan NonFailureRetryDelay = TimeSpan.FromSeconds(5);
    /// <summary>See <see cref="AgwLink.KeepaliveInterval"/>.</summary>
    internal static readonly TimeSpan KeepaliveInterval = AgwLink.KeepaliveInterval;
    /// <summary>Least time between two reconnects a dial asks for while
    /// the link backs off: with the node down, every dial would otherwise
    /// cost a connect attempt and its log lines.</summary>
    internal static readonly TimeSpan DialReopenInterval = TimeSpan.FromSeconds(30);
    /// <summary>How long a listen-only link stays up once nothing wants
    /// it, for a discovery channel edit, which restarts the bearer, to
    /// take it over rather than close and reopen it.</summary>
    internal static readonly TimeSpan ListenOnlyLinger = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource stoppingTokenSource = new();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();
    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly InboundReconnectController reconnect = new(timeProvider ?? TimeProvider.System);
    private Task? loopTask;
    private CancellationTokenSource? cycleTokenSource;
    private IDisposable? optionsChangeSubscription;
    // The published link (IAgwLinkSource). Guarded by linkGate, so a
    // waiter in GetLinkAsync never misses the link opening.
    private readonly Lock linkGate = new();
    private AgwLink? link;
    private AgwLinkState linkState = AgwLinkState.Idle;
    private TaskCompletionSource<AgwLink?> nextLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? lastDialReopen;
    private readonly AgwUiSubscribers ui = new(logger);

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
        metrics.RecordReconnectBackoff("agw", reconnect.FailureStreak, reconnect.NextRetryAtUtc?.UtcDateTime);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kick the current connect-cycle when a SystemOptions save changes
        // a field the cycle is built from (bearer, callsign, node host or
        // AGW port), so the fix takes effect on the next iteration -
        // including collapsing an in-flight backoff wait, so a fix to
        // NodeHost/AgwPort/Callsign reconnects immediately rather than
        // sitting out the rest of a (possibly multi-minute) scheduled
        // delay. Any other save (TX stop/resume, heartbeat, probing, ...)
        // is ignored here: closing the AGW socket makes BPQ disconnect
        // every live inbound link on it (#273). Subscribed in StartAsync
        // so test fixtures that construct the service without ever
        // calling StartAsync don't accumulate listeners.
        optionsChangeSubscription = options.OnChange((o, _) =>
            reconnect.OptionsChanged(CycleSettings.From(o), cycleTokenSource));
        loopTask = Task.Run(() => RunLoop(stoppingTokenSource.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        optionsChangeSubscription?.Dispose();
        optionsChangeSubscription = null;
        await stoppingTokenSource.CancelAsync();
        if (loopTask is not null)
        {
            try { await loopTask; } catch { /* shutdown */ }
        }
    }

    private async Task RunLoop(CancellationToken outerCt)
    {
        while (!outerCt.IsCancellationRequested)
        {
            cycleTokenSource = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            var cycleCt = cycleTokenSource.Token;
            try
            {
                await RunOnce(cycleCt);

                // Reached here only via a clean return - idle-gated on
                // missing config, or the cycle token's while-condition
                // caught a cancellation before the next read threw. Either
                // way it's not a failure: reset the backoff and reconnect
                // promptly rather than escalating. Routed through
                // reconnect.WaitAsync (rather than a bare Task.Delay) so
                // a manual "retry now" or a further options change can
                // still collapse this short wait too; WaitAsync swallows
                // cancellation internally, so the outer while-condition
                // (not an explicit return here) is what ends the loop on
                // shutdown.
                reconnect.RecordSuccess();
                PublishBackoffState();
                await reconnect.WaitAsync(NonFailureRetryDelay, outerCt);
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                SetLinkState(AgwLinkState.Idle);
                return;
            }
            catch (OperationCanceledException)
            {
                // Cycle cancelled by an options change - not a failure to
                // back off from, but still pause briefly before the next
                // attempt (same as the idle-gate case) rather than
                // reconnecting in a tight loop if options keep changing.
                // Not a failure even if the socket failed in the same
                // moment: dials wait for the next link rather than fail.
                SetLinkState(AgwLinkState.Connecting);
                reconnect.RecordSuccess();
                PublishBackoffState();
                await reconnect.WaitAsync(NonFailureRetryDelay, outerCt);
            }
            catch (Exception ex)
            {
                SetLinkState(AgwLinkState.Backoff);
                var delay = reconnect.RecordFailure();
                // Start the wait (which assigns reconnect's internal
                // cancellation source) before publishing/logging - the
                // dashboard shows this failure, and its "Retry now"
                // button becomes clickable, the moment PublishBackoffState
                // runs, so TriggerRetry must already have something to
                // cancel by then.
                var waitTask = reconnect.WaitAsync(delay, outerCt);
                PublishBackoffState();
                if (ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    // The node is not up: the stack trace says nothing more.
                    logger.LogWarning(
                        "AGW inbound loop ended ({3}); reconnecting in {0}s (attempt {1}, next at {2:O})",
                        delay.TotalSeconds, reconnect.FailureStreak, reconnect.NextRetryAtUtc, ex.Message);
                }
                else
                {
                    logger.LogWarning(ex,
                        "AGW inbound loop ended; reconnecting in {0}s (attempt {1}, next at {2:O})",
                        delay.TotalSeconds, reconnect.FailureStreak, reconnect.NextRetryAtUtc);
                }
                await waitTask;
            }
        }
        // Stopped, possibly out of a backoff wait (which swallows the
        // cancellation): nothing is coming any more.
        SetLinkState(AgwLinkState.Idle);
    }

    /// <summary>The SystemOptions fields one AGW connection cycle is
    /// built from, copied at cycle start: a save that changes one of
    /// these restarts the cycle, any other save leaves it running.</summary>
    internal sealed record CycleSettings(string NodeBearer, string Callsign, string NodeHost, int AgwPort)
    {
        public static CycleSettings From(SystemOptions o) => new(o.NodeBearer, o.Callsign, o.NodeHost, o.AgwPort);
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var opts = reconnect.BeginCycle(() => CycleSettings.From(options.CurrentValue));

        // Bearer-active gate: only run when AGW is the configured bearer.
        // The Rhpv2InboundService runs alongside us and gates the same way
        // on "rhpv2"; OnChange fires when /Config flips the value, which
        // cancels our cycle (or theirs) so the loop re-evaluates. With
        // another bearer, AGW discovery may still want the node's UI
        // frames: then listen only (class doc).
        var listenOnly = !string.Equals(opts.NodeBearer, "agw", StringComparison.OrdinalIgnoreCase);
        if (listenOnly && !ui.Any)
        {
            SetLinkState(AgwLinkState.Idle);
            await Task.Delay(IdleBackoff, timeProvider, ct);
            return;
        }

        var localCall = opts.Callsign;
        if (string.IsNullOrWhiteSpace(localCall)
            || string.Equals(localCall, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            SetLinkState(AgwLinkState.Idle);
            logger.LogDebug("Callsign not configured; AGW inbound idle (waiting for /Setup or /Config)");
            await Task.Delay(IdleBackoff, timeProvider, ct);
            return;
        }

        SetLinkState(AgwLinkState.Connecting);
        if (listenOnly)
        {
            logger.LogInformation(
                "AGW inbound: the bearer is {0}, which sends the discovery beacons, so the AGW connection only hears them: no callsign registration, no calls",
                opts.NodeBearer);
        }
        await using var opened = await AgwLink.OpenAsync(
            opts.NodeHost, opts.AgwPort, localCall, OnInbound, logger, txGate, timeProvider,
            onRegisterSent: listenOnly ? null : metrics.RecordAgwReconnect, stopping: stoppingTokenSource.Token, ct: ct,
            listenOnly: listenOnly);

        // Reset the backoff only now, not on the raw connect and write:
        // a host that accepts the TCP socket and closes it straight
        // back would otherwise reset FailureStreak to 0 just before the
        // failure puts it back to 1, capping the delay at the ramp's
        // fastest tier instead of escalating. A reply from the node (to
        // the 'X', or on a listen-only link to a 'G') is proof the
        // connection is more than a TCP handshake.
        reconnect.RecordSuccess();
        PublishBackoffState();
        logger.LogDebug(listenOnly ? "AGW inbound: 'G' answered" : "AGW inbound: 'X' ack");

        // Before it is published, so a subscriber hears it from the start.
        using var uiFrames = ui.Attach(opened);
        Publish(opened);
        var unwanted = false;
        try
        {
            if (listenOnly)
            {
                // Open only for discovery: closed once nothing wants it. A
                // channel edit stops the old bearer a moment before it
                // starts the new one, so wait a little before closing.
                while (!opened.Closed.IsCompleted)
                {
                    await Task.WhenAny(opened.Closed, ui.WhenEmptyAsync()).WaitAsync(ct);
                    if (opened.Closed.IsCompleted) break;
                    await Task.WhenAny(opened.Closed, Task.Delay(ListenOnlyLinger, timeProvider, ct)).WaitAsync(ct);
                    if (opened.Closed.IsCompleted || ui.Any) continue;
                    unwanted = true;
                    logger.LogInformation("AGW inbound: no AGW discovery channel is running any more; closing the listen-only connection");
                    return;
                }
            }
            else
            {
                await opened.Closed.WaitAsync(ct);
            }
            if (opened.CloseReason is { } reason) ExceptionDispatchInfo.Throw(reason);
        }
        finally
        {
            Unpublish(opened, unwanted);
        }
    }

    /// <summary>
    /// UI frames from whichever link is open, for AGW discovery (see
    /// <see cref="IAgwLinkSource.SubscribeUi"/>). While one is held and AGW
    /// is not the bearer, the link runs listen-only.
    /// </summary>
    public IDisposable SubscribeUi(Action<AgwFrame> onUi) => ui.Add(onUi);

    /// <summary>
    /// An inbound connect, already in the link's session table: hold the
    /// peer's lease for it and start its handler. Runs on the link's
    /// reader, so it never waits.
    /// </summary>
    private void OnInbound(AgwLinkSession session)
    {
        var remote = session.Remote;
        var local = session.Local;
        var port = session.Port;
        var stream = session.Stream;

        PeerSessionLease? lease = null;
        try
        {
            metrics.RecordInboundConnect(remote);

            // #178: while this session is open the forwarder must not dial
            // this peer (see PeerSessionRegistry). Released once the
            // handler is done and our 'd', if any, has gone out. Connected
            // now, so any older session with this peer is retired.
            lease = peerSessions?.Acquire(remote, "inbound", port);
            if (lease is not null) session.SilenceWhen(lease.Retired);
            logger.LogDebug("AGW inbound: session {0}<->{1} on port {2} registered", local, remote, port);

            // Retired: a newer session with this peer has connected here, and
            // the link is that one's. Out of the table, so nothing more is
            // routed to it, and marked closed by the far end, so its teardown
            // never sends the 'd' that BPQ would apply to the newer session.
            lease?.Retired.Register(() =>
            {
                session.Retire();
                logger.LogInformation("AGW inbound: session {0}<->{1} retired: a newer session with that peer has the link", local, remote);
            });

            // #205: our own call to this peer may already be on its way, the
            // two calls made a moment apart. Our node then sends our SABM down
            // this link, and the peer's node keeps its call up, sharing the link
            // with ours, only if nothing has reached it on the link yet;
            // otherwise it resets the link, and both calls are lost. So no
            // prompt until our call has connected (this session is then
            // retired, having said nothing) or given up.
            var ownCall = lease is null ? Task.CompletedTask : peerSessions!.OwnCallsSettledAsync(lease);

            var handler = new InboundConnectionHandler(
                stream, sourceCallsign: remote, loggerFactory, database, inbox, metrics,
                settingsFor: exchangePolicy is null ? null : exchangePolicy.ForPeerAsync,
                directory: openSessions);

            // Not tied to the cycle token: a cancellation landing in the gap
            // after the 'C' was read would skip the handler, leaving the
            // stream undisposed and the peer's lease held for good. The
            // link's end closes this session through its table instead.
            _ = Task.Run(async () =>
            {
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingTokenSource.Token, lease?.Retired ?? CancellationToken.None);
                try
                {
                    if (await HoldForOwnCallAsync(ownCall, remote, stream.RemoteClosed, sessionCts.Token)) await handler.Handle(sessionCts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "AGW inbound: session handler {0}<->{1} failed", local, remote);
                }
                finally
                {
                    // Remove only *our* session. The 'd' frame handler may
                    // already have removed it, and a reconnect from the same
                    // callsign pair may since have registered a new one
                    // under its key - removing by key alone would evict that.
                    session.Remove();
                    try { await stream.DisposeAsync(); } catch { /* best effort */ }
                    lease?.Dispose();
                }
            });
        }
        catch
        {
            // Failed between registering the session and starting its
            // handler: nothing else would ever end it. Take it out of the
            // table (only this session), let the peer go with a 'd' (the
            // stream's dispose sends it unless the link was retired or
            // closed meanwhile) and release the peer's lease, so the link
            // does not hang until it times out. The link's reader logs the
            // exception itself.
            session.Remove();
            var failedLease = lease;
            _ = Task.Run(async () =>
            {
                try { await stream.DisposeAsync(); } catch { /* best effort */ }
                failedLease?.Dispose();
            });
            throw;
        }
    }

    /// <summary>
    /// Waits for our own call to the peer to connect or give up before
    /// this session answers (see <see cref="OwnCallHold"/>). False when
    /// the session was retired meanwhile, our call having connected, when
    /// the peer hung up (<paramref name="hungUp"/>), or when the service is
    /// stopping: then it never answers.
    /// </summary>
    private async Task<bool> HoldForOwnCallAsync(Task ownCall, string remote, CancellationToken hungUp, CancellationToken ct)
    {
        if (ownCall.IsCompleted) return !ct.IsCancellationRequested;
        logger.LogInformation("AGW inbound: our own call to {0} is on its way; holding the prompt until it connects", remote);
        using var holding = CancellationTokenSource.CreateLinkedTokenSource(ct, hungUp);
        try
        {
            await ownCall.WaitAsync(OwnCallHold, timeProvider, holding.Token);
        }
        catch (TimeoutException)
        {
            logger.LogInformation("AGW inbound: our call to {0} hasn't connected after {1:F0}s; answering its call",
                remote, OwnCallHold.TotalSeconds);
        }
        catch (OperationCanceledException) when (holding.IsCancellationRequested)
        {
        }
        if (ct.IsCancellationRequested) return false;
        if (hungUp.IsCancellationRequested)
        {
            logger.LogInformation("AGW inbound: {0} hung up while we held the prompt; not answering", remote);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Longest an inbound session holds its prompt for our own call to the
    /// same peer. On net-sim at 1200 baud such a call connected 2 to 3 s
    /// after it was asked for, every time. By 10 s the peer's call has
    /// stopped waiting for a prompt and sent its <c>exchange</c> anyway, so
    /// holding longer gains nothing.
    /// </summary>
    internal static readonly TimeSpan OwnCallHold = TimeSpan.FromSeconds(10);

    public AgwLinkState State
    {
        get { lock (linkGate) return linkState; }
    }

    public AgwLink? TryGetLink()
    {
        lock (linkGate) return link;
    }

    public async Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct)
    {
        Task<AgwLink?> opening;
        lock (linkGate)
        {
            if (link is not null) return link;
            if (linkState != AgwLinkState.Connecting) return null;
            opening = nextLink.Task;
        }
        try
        {
            return await opening.WaitAsync(wait, timeProvider, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct)
    {
        Task<AgwLink?> opening;
        lock (linkGate)
        {
            if (link is not null) return link;
            if (linkState == AgwLinkState.Idle) return null;
            if (linkState == AgwLinkState.Backoff)
            {
                // At most one reconnect per DialReopenInterval on a dial's
                // account; in between, the dial fails at once, as before.
                var now = timeProvider.GetUtcNow();
                if (now - lastDialReopen < DialReopenInterval) return null;
                lastDialReopen = now;
            }
            // Taken before the retry, so the link it opens is not missed:
            // a failed attempt releases it with null (Backoff).
            opening = nextLink.Task;
        }
        // The existing retry-now: cuts short the wait the loop is in, a
        // backoff or the pause after a save. The ramp is unchanged: a
        // failed attempt counts as the next failure, as before.
        if (TriggerManualRetry())
        {
            logger.LogInformation("AGW inbound: a call is waiting; reconnecting now");
        }
        try
        {
            return await opening.WaitAsync(wait, timeProvider, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private void SetLinkState(AgwLinkState state)
    {
        TaskCompletionSource<AgwLink?>? giveUp;
        lock (linkGate) giveUp = ChangeState(state);
        giveUp?.TrySetResult(null);
    }

    /// <summary>Sets the state, under <see cref="linkGate"/>. Leaving
    /// Connecting for Idle or Backoff hands back the waiters in
    /// <see cref="GetLinkAsync"/>, to be released with null: no link is
    /// coming soon.</summary>
    private TaskCompletionSource<AgwLink?>? ChangeState(AgwLinkState state)
    {
        linkState = state;
        if (state is not (AgwLinkState.Idle or AgwLinkState.Backoff)) return null;
        var waiters = nextLink;
        nextLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return waiters;
    }

    private void Publish(AgwLink opened)
    {
        TaskCompletionSource<AgwLink?> waiters;
        lock (linkGate)
        {
            link = opened;
            linkState = AgwLinkState.Open;
            waiters = nextLink;
            nextLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        waiters.TrySetResult(opened);
    }

    /// <summary>Withdraws <paramref name="closed"/>: Idle if nothing
    /// wants it any more (<paramref name="unwanted"/>), Backoff if it
    /// failed, otherwise Connecting (a cycle cancelled by a save, which
    /// reconnects shortly; on shutdown the loop then says Idle).</summary>
    private void Unpublish(AgwLink closed, bool unwanted = false)
    {
        var failed = closed.CloseReason is { } reason && reason is not OperationCanceledException;
        var state = unwanted ? AgwLinkState.Idle : failed ? AgwLinkState.Backoff : AgwLinkState.Connecting;
        TaskCompletionSource<AgwLink?>? giveUp;
        lock (linkGate)
        {
            if (!ReferenceEquals(link, closed)) return;
            link = null;
            giveUp = ChangeState(state);
        }
        logger.LogDebug("AGW inbound: connection withdrawn ({0})", state);
        giveUp?.TrySetResult(null);
    }
}
