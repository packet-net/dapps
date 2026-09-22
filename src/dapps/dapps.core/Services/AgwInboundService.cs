using System.Collections.Concurrent;
using System.Net.Sockets;
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
/// Session identity. AGW frames carry no session id: a session is the
/// (local, remote, port) triple, and BPQ reuses it the moment a peer
/// redials. Everything below that touches the session table follows
/// three rules that fall out of how BPQ's AGWAPI.c actually behaves:
///   1. BPQ stamps its disconnect notification ('d') with the port of
///      the last frame it *received from us* on the socket, not the
///      session's port (SendDisMsgtoAppl copies sockptr-&gt;AGWRXHeader.Port).
///      After a 'G' keepalive that is port 0 regardless of the session.
///      So a 'd' (or 'D') that matches no session exactly is resolved
///      by callsign pair alone, provided that pair identifies exactly
///      one session.
///   2. BPQ refuses a second inbound connect for a pair it still holds
///      a session for (AGWConnected, "Callsign is already connected"),
///      before any 'C' reaches us. So a 'C' for a key we still have an
///      entry for means that entry is stale, never a live duplicate:
///      retire it in place and accept the new session.
///   3. A retired or remotely-closed session must never emit a 'd' of
///      its own, because BPQ resolves an app-sent 'd' by callsign pair
///      too and would tear down whatever newer session holds it.
///      <see cref="MultiplexedAgwSessionStream"/> enforces that; the
///      table only ever removes an entry by (key, stream) so a
///      handler's teardown cannot evict its successor.
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
    TimeProvider? timeProvider = null) : IHostedService
{
    internal static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);
    /// <summary>Delay between cycles that ended without a real failure
    /// (idle-gated on missing config, or a cycle cancelled by a /Config
    /// save) - deliberately short and flat; the sliding backoff only
    /// applies to actual connect/socket failures.</summary>
    internal static readonly TimeSpan NonFailureRetryDelay = TimeSpan.FromSeconds(5);
    /// <summary>
    /// AGW keepalive period. BPQ closes idle AGW client connections
    /// after ~20s of no traffic; without a periodic frame from us,
    /// every install with a real BPQ saw an EndOfStreamException +
    /// reconnect every ~25 s (20 s idle + the reconnect delay). The
    /// 'G' frame queries port count and is the cheapest no-op we can
    /// send - BPQ replies with a 'G' frame, both directions count as
    /// activity, BPQ's idle timer resets. 15s is comfortably under
    /// 20s with margin for jitter.
    /// </summary>
    internal static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource stoppingTokenSource = new();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();
    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly InboundReconnectController reconnect = new(timeProvider ?? TimeProvider.System);
    private Task? loopTask;
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
        metrics.RecordReconnectBackoff("agw", reconnect.FailureStreak, reconnect.NextRetryAtUtc?.UtcDateTime);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kick the current connect-cycle on any SystemOptions change so
        // a /Config save (callsign, node host/port, bearer flip, RHP
        // creds) takes effect on the next iteration - including
        // collapsing an in-flight backoff wait, so a fix to NodeHost/
        // AgwPort/Callsign reconnects immediately rather than sitting
        // out the rest of a (possibly multi-minute) scheduled delay.
        // Subscribed in StartAsync so test fixtures that construct the
        // service without ever calling StartAsync don't accumulate
        // listeners.
        optionsChangeSubscription = options.OnChange((_, _) =>
        {
            cycleTokenSource?.Cancel();
            reconnect.Interrupt();
        });
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
                return;
            }
            catch (OperationCanceledException)
            {
                // Cycle cancelled by an options change - not a failure to
                // back off from, but still pause briefly before the next
                // attempt (same as the idle-gate case) rather than
                // reconnecting in a tight loop if options keep changing.
                reconnect.RecordSuccess();
                PublishBackoffState();
                await reconnect.WaitAsync(NonFailureRetryDelay, outerCt);
            }
            catch (Exception ex)
            {
                var delay = reconnect.RecordFailure();
                // Start the wait (which assigns reconnect's internal
                // cancellation source) before publishing/logging - the
                // dashboard shows this failure, and its "Retry now"
                // button becomes clickable, the moment PublishBackoffState
                // runs, so TriggerRetry must already have something to
                // cancel by then.
                var waitTask = reconnect.WaitAsync(delay, outerCt);
                PublishBackoffState();
                logger.LogWarning(ex,
                    "AGW inbound loop ended; reconnecting in {0}s (attempt {1}, next at {2:O})",
                    delay.TotalSeconds, reconnect.FailureStreak, reconnect.NextRetryAtUtc);
                await waitTask;
            }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var opts = options.CurrentValue;

        // Bearer-active gate: only run when AGW is the configured bearer.
        // The Rhpv2InboundService runs alongside us and gates the same way
        // on "rhpv2"; OnChange fires when /Config flips the value, which
        // cancels our cycle (or theirs) so the loop re-evaluates.
        if (!string.Equals(opts.NodeBearer, "agw", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(IdleBackoff, timeProvider, ct);
            return;
        }

        var localCall = opts.Callsign;
        if (string.IsNullOrWhiteSpace(localCall)
            || string.Equals(localCall, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Callsign not configured; AGW inbound idle (waiting for /Setup or /Config)");
            await Task.Delay(IdleBackoff, timeProvider, ct);
            return;
        }

        using var tcp = new TcpClient();
        logger.LogInformation("AGW inbound: connecting {0}:{1}", opts.NodeHost, opts.AgwPort);
        await tcp.ConnectAsync(opts.NodeHost, opts.AgwPort, ct);
        var framing = new AgwFrameTransport(tcp.GetStream(), txGate);

        // Keepalive cadence starts before the 'X' goes out so that, once
        // BPQ (or a test double) has seen our registration, the timer is
        // guaranteed to exist - a fake clock advanced past
        // KeepaliveInterval then deterministically produces a 'G'.
        using var keepalive = new PeriodicTimer(KeepaliveInterval, timeProvider);

        // 'X' register so BPQ knows where to dispatch inbound 'C' frames.
        // Note this is necessary but *not sufficient*: the operator must
        // also have the callsign on an APPLICATION line in bpq32.cfg
        // (apps-interface.md). Without that, the X register is a no-op
        // for inbound and we'll sit here happily forever, never being
        // dispatched to.
        await framing.WriteFrameAsync(
            new AgwFrame(0, 'X', 0, localCall, "", []), ct);
        metrics.RecordAgwReconnect();

        // Note: the backoff isn't reset here on the raw connect+write - a
        // persistent post-connect rejection (BPQ accepts the TCP socket
        // but immediately closes it back, e.g. a firewall RST or BPQ
        // refusing the client) would otherwise see FailureStreak reset to
        // 0 right before the resulting ReadFrameAsync failure puts it
        // straight back to 1, permanently capping the delay at the
        // ramp's fastest (10s) tier instead of escalating. RecordSuccess()
        // only fires once BPQ has actually sent us a frame, below -
        // proof the connection is more than just a TCP handshake.
        var recordedConnectSuccess = false;

        var sessions = new ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream>();

        // Keepalive: send 'G' (port count query) every KeepaliveInterval
        // so BPQ doesn't drop the idle socket. The reply lands in the
        // same Read loop below and is ignored as a default-case frame.
        // Run in a fire-and-forget task tied to the cycle's ct so it
        // dies when the cycle does. AgwFrameTransport serialises the
        // shared write side - inbound frame handling can also write
        // (session 'D' frames via the multiplexed streams), and AGW is
        // a single-stream protocol.
        using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepaliveTask = Task.Run(async () =>
        {
            try
            {
                while (await keepalive.WaitForNextTickAsync(keepaliveCts.Token))
                {
                    await framing.WriteFrameAsync(
                        new AgwFrame(0, 'G', 0, "", "", []), keepaliveCts.Token);
                    logger.LogDebug("AGW inbound: keepalive 'G' sent");
                }
            }
            catch (OperationCanceledException) { /* expected on cycle end */ }
            catch (Exception ex)
            {
                // A keepalive write failure means the socket's already
                // dead; the read side will see the same EOF and drive
                // the reconnect. Log at debug to avoid duplicating the
                // warn that the read side will emit.
                logger.LogDebug(ex, "AGW keepalive write failed");
            }
        }, keepaliveCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await framing.ReadFrameAsync(ct);
                if (!recordedConnectSuccess)
                {
                    recordedConnectSuccess = true;
                    reconnect.RecordSuccess();
                    PublishBackoffState();
                }
                await Dispatch(frame, framing, sessions, ct);
            }
        }
        finally
        {
            // Stop the keepalive before tearing down sessions to avoid
            // a doomed write racing the socket close.
            keepaliveCts.Cancel();
            try { await keepaliveTask; } catch { /* swallow - already logged */ }

            // Tear down any in-flight sessions cleanly so handlers exit.
            foreach (var s in sessions.Values) s.SignalRemoteDisconnect();
            sessions.Clear();
        }
    }

    private async Task Dispatch(
        AgwFrame frame,
        AgwFrameTransport framing,
        ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream> sessions,
        CancellationToken ct)
    {
        switch (frame.Kind)
        {
            case 'X':
                logger.LogDebug("AGW inbound: 'X' ack");
                break;

            case 'C':
                OnConnect(frame, framing, sessions, ct);
                break;

            case 'D':
                if (TryResolve(sessions, frame, out var data, out var dataPortMismatch))
                {
                    if (dataPortMismatch)
                    {
                        logger.LogDebug("AGW inbound: 'D' on port {0} matched session {1}<->{2} on port {3} by callsign pair",
                            frame.Port, data.Key.Local, data.Key.Remote, data.Key.Port);
                    }
                    await data.Value.PushIncoming(frame.Payload, ct);
                }
                else
                {
                    logger.LogDebug("AGW inbound: 'D' for unknown session {0}<->{1} on port {2}; dropped",
                        frame.CallFrom, frame.CallTo, frame.Port);
                }
                break;

            case 'd':
                if (TryResolve(sessions, frame, out var closing, out var closePortMismatch))
                {
                    // Remove by (key, stream), never by key alone: the
                    // handler's own teardown uses the same rule, and a
                    // newer stream may already sit under this key.
                    sessions.TryRemove(closing);
                    closing.Value.SignalRemoteDisconnect();
                    logger.LogInformation("AGW inbound: session closed {0}<->{1}", frame.CallFrom, frame.CallTo);
                    if (closePortMismatch)
                    {
                        logger.LogDebug(
                            "AGW inbound: that 'd' carried port {0} but the session was on port {1} - BPQ stamps " +
                            "disconnect notifications with the port of the last frame it received from us",
                            frame.Port, closing.Key.Port);
                    }
                }
                else
                {
                    logger.LogDebug("AGW inbound: 'd' for unknown session {0}<->{1} on port {2}; ignored",
                        frame.CallFrom, frame.CallTo, frame.Port);
                }
                break;

            default:
                logger.LogDebug("AGW inbound: ignoring frame kind '{0}'", frame.Kind);
                break;
        }
    }

    private void OnConnect(
        AgwFrame frame,
        AgwFrameTransport framing,
        ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream> sessions,
        CancellationToken ct)
    {
        // BPQ delivers an inbound 'C' frame with CallFrom = the remote
        // station that connected to us, CallTo = our local APPL call.
        var remote = frame.CallFrom;
        var local = frame.CallTo;
        var port = frame.Port;
        logger.LogInformation("AGW inbound: 'C' from {0} to {1} on port {2}", remote, local, port);
        metrics.RecordInboundConnect(remote);

        // Some AGW emulators emit a "*** CONNECTED..." status string in
        // the 'C' payload; that's noise from dapps's POV and we just
        // discard it.

        var key = SessionKey.For(local, remote, port);
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: async (data, c) =>
            {
                await framing.WriteFrameAsync(
                    new AgwFrame(port, 'D', 0xF0, local, remote, data), c);
            },
            sendRemoteDisconnect: async c =>
            {
                await framing.WriteFrameAsync(
                    new AgwFrame(port, 'd', 0, local, remote, []), c);
            });

        // Rule 2 (class doc): BPQ never dispatches a live duplicate, so
        // an entry still under this key is stale - its 'd' was lost, or
        // arrived in a form we could not match. Retire it in place.
        // Marking it remote-closed keeps its handler's teardown from
        // emitting a 'd' that BPQ would apply to the session we are
        // about to register.
        if (sessions.TryRemove(key, out var stale))
        {
            stale.SignalRemoteDisconnect();
            logger.LogWarning(
                "AGW inbound: 'C' for {0}<->{1} on port {2} while a session was still registered for that pair; " +
                "retiring the stale entry (its 'd' never reached us in a form we could match)",
                local, remote, port);
        }
        sessions[key] = stream;

        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: remote, loggerFactory, database, inbox, metrics);

        _ = Task.Run(async () =>
        {
            try { await handler.Handle(stoppingTokenSource.Token); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound: session handler {0}<->{1} failed", local, remote);
            }
            finally
            {
                // Remove only *our* stream. The 'd' frame handler may
                // already have removed it, and a reconnect from the same
                // callsign pair may since have registered a new stream
                // under this key - removing by key alone would evict and
                // dispose that one.
                sessions.TryRemove(new KeyValuePair<SessionKey, MultiplexedAgwSessionStream>(key, stream));
                try { await stream.DisposeAsync(); } catch { /* best effort */ }
            }
        }, ct);
    }

    /// <summary>
    /// Finds the session a 'D' / 'd' frame refers to. Exact
    /// (local, remote, port) first; failing that, the callsign pair
    /// alone when it identifies exactly one session (rule 1 in the
    /// class doc). Two sessions for the same pair on different ports
    /// make a port-less frame genuinely ambiguous, and we leave both
    /// alone rather than guess.
    /// </summary>
    private static bool TryResolve(
        ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream> sessions,
        AgwFrame frame,
        out KeyValuePair<SessionKey, MultiplexedAgwSessionStream> match,
        out bool portMismatch)
    {
        portMismatch = false;
        var exact = KeyForData(frame);
        if (sessions.TryGetValue(exact, out var stream))
        {
            match = new(exact, stream);
            return true;
        }

        KeyValuePair<SessionKey, MultiplexedAgwSessionStream>? candidate = null;
        foreach (var kv in sessions)
        {
            if (!kv.Key.SamePair(exact)) continue;
            if (candidate is not null)
            {
                match = default;
                return false;
            }
            candidate = kv;
        }

        if (candidate is null)
        {
            match = default;
            return false;
        }

        match = candidate.Value;
        portMismatch = true;
        return true;
    }

    /// <summary>For 'D' / 'd' frames the callsign pair is flipped
    /// relative to the 'C' frame: BPQ uses CallFrom = peer, CallTo = us
    /// on the inbound connect and CallFrom = peer, CallTo = us on the
    /// frames it forwards to us for that session.</summary>
    private static SessionKey KeyForData(AgwFrame frame) =>
        SessionKey.For(frame.CallTo, frame.CallFrom, frame.Port);

    internal readonly record struct SessionKey(string Local, string Remote, byte Port)
    {
        public static SessionKey For(string local, string remote, byte port) =>
            new(local.ToUpperInvariant(), remote.ToUpperInvariant(), port);

        public bool SamePair(SessionKey other) =>
            string.Equals(Local, other.Local, StringComparison.Ordinal)
            && string.Equals(Remote, other.Remote, StringComparison.Ordinal);
    }
}
