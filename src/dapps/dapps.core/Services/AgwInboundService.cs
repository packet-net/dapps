using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
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
/// Reconnect policy: on any AGW socket error, sleep
/// <see cref="ReconnectBackoff"/> and retry. In-flight inbound sessions
/// are lost (their streams get EOF); the sender's bearer surfaces a
/// timeout and retries on its next forwarder run - matches the
/// existing at-least-once semantics.
/// </summary>
public sealed class AgwInboundService(
    IOptionsMonitor<SystemOptions> options,
    Database database,
    IBackhaulInbox inbox,
    ILoggerFactory loggerFactory,
    ILogger<AgwInboundService> logger,
    OperationalMetrics? metrics = null,
    IDappsTxGate? txGate = null) : IHostedService
{
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);
    /// <summary>
    /// AGW keepalive period. BPQ closes idle AGW client connections
    /// after ~20s of no traffic; without a periodic frame from us,
    /// every install with a real BPQ saw an EndOfStreamException +
    /// reconnect every 25 s (20 s idle + 5 s ReconnectBackoff). The
    /// 'G' frame queries port count and is the cheapest no-op we can
    /// send - BPQ replies with a 'G' frame, both directions count as
    /// activity, BPQ's idle timer resets. 15s is comfortably under
    /// 20s with margin for jitter.
    /// </summary>
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource stoppingTokenSource = new();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();
    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private Task? loopTask;
    private CancellationTokenSource? cycleTokenSource;
    private IDisposable? optionsChangeSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kick the current connect-cycle on any SystemOptions change so
        // a /Config save (callsign, node host/port, bearer flip, RHP
        // creds) takes effect on the next iteration. Subscribed in
        // StartAsync so test fixtures that construct the service without
        // ever calling StartAsync don't accumulate listeners.
        optionsChangeSubscription = options.OnChange((_, _) => cycleTokenSource?.Cancel());
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
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Cycle cancelled by an options change; loop and reconnect.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound loop ended; reconnecting in {0}s", ReconnectBackoff.TotalSeconds);
            }

            try { await Task.Delay(ReconnectBackoff, outerCt); }
            catch (OperationCanceledException) { return; }
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
            await Task.Delay(IdleBackoff, ct);
            return;
        }

        var localCall = opts.Callsign;
        if (string.IsNullOrWhiteSpace(localCall)
            || string.Equals(localCall, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Callsign not configured; AGW inbound idle (waiting for /Setup or /Config)");
            await Task.Delay(IdleBackoff, ct);
            return;
        }

        using var tcp = new TcpClient();
        logger.LogInformation("AGW inbound: connecting {0}:{1}", opts.NodeHost, opts.AgwPort);
        await tcp.ConnectAsync(opts.NodeHost, opts.AgwPort, ct);
        var framing = new AgwFrameTransport(tcp.GetStream(), txGate);

        // 'X' register so BPQ knows where to dispatch inbound 'C' frames.
        // Note this is necessary but *not sufficient*: the operator must
        // also have the callsign on an APPLICATION line in bpq32.cfg
        // (apps-interface.md). Without that, the X register is a no-op
        // for inbound and we'll sit here happily forever, never being
        // dispatched to.
        await framing.WriteFrameAsync(
            new AgwFrame(0, 'X', 0, localCall, "", []), ct);
        metrics.RecordAgwReconnect();

        var sessions = new ConcurrentDictionary<SessionKey, MultiplexedAgwSessionStream>();

        // Keepalive: send 'G' (port count query) every KeepaliveInterval
        // so BPQ doesn't drop the idle socket. The reply lands in the
        // same Read loop below and is ignored as a default-case frame.
        // Run in a fire-and-forget task tied to the cycle's ct so it
        // dies when the cycle does. SemaphoreSlim guards the shared
        // write side - inbound frame handling can also write (e.g.
        // 'D' acknowledgements via the multiplexed sessions), and
        // AGW is a single-stream protocol so we serialise writes.
        using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepaliveTask = Task.Run(async () =>
        {
            try
            {
                while (!keepaliveCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(KeepaliveInterval, keepaliveCts.Token);
                    await framing.WriteFrameAsync(
                        new AgwFrame(0, 'G', 0, "", "", []), keepaliveCts.Token);
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
                await OnConnect(frame, framing, sessions, ct);
                break;

            case 'D':
                if (sessions.TryGetValue(KeyForData(frame), out var stream))
                {
                    await stream.PushIncoming(frame.Payload, ct);
                }
                else
                {
                    logger.LogDebug("AGW inbound: 'D' for unknown session {0}↔{1}",
                        frame.CallFrom, frame.CallTo);
                }
                break;

            case 'd':
                if (sessions.TryRemove(KeyForData(frame), out var dstream))
                {
                    dstream.SignalRemoteDisconnect();
                    logger.LogInformation("AGW inbound: session closed {0}↔{1}", frame.CallFrom, frame.CallTo);
                }
                break;

            default:
                logger.LogDebug("AGW inbound: ignoring frame kind '{0}'", frame.Kind);
                break;
        }
    }

    private async Task OnConnect(
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

        var key = new SessionKey(local, remote, port);
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

        // A 'C' for a key we still have an entry for is *not* necessarily a
        // real concurrent duplicate: BPQ itself refuses a genuine one at the
        // L2 level ("... already connected on socket N", logged on BPQ's
        // side, never reaching us as a 'C' at all). When we do see one, it
        // means the previous session's 'd' just hasn't reached us yet - BPQ
        // can flush the AGW frame for a brand-new connect ahead of the
        // teardown notification for what it replaced. Retire the stale
        // entry in place rather than rejecting the new, legitimate connect;
        // marking it remote-closed keeps its own teardown from emitting a
        // 'd' that could otherwise hit whatever reuses this key next.
        sessions.AddOrUpdate(key, stream, (_, existing) =>
        {
            logger.LogWarning(
                "AGW inbound: 'C' for {0}↔{1} while a session was still registered for that pair; " +
                "retiring the stale entry (its 'd' probably hasn't reached us yet)", local, remote);
            existing.SignalRemoteDisconnect();
            return stream;
        });

        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: remote, loggerFactory, database, inbox, metrics);

        _ = Task.Run(async () =>
        {
            try { await handler.Handle(stoppingTokenSource.Token); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound: session handler {0}↔{1} failed", local, remote);
            }
            finally
            {
                // Remove only *our* stream. The 'd' frame handler may
                // already have removed it, and a reconnect from the same
                // callsign pair may since have registered a new stream
                // under this key - removing by key alone would evict and
                // dispose that one.
                // (ICollection.Remove is the atomic key+value compare-remove
                // on net8; ConcurrentDictionary.TryRemove(KeyValuePair) is net9+.)
                ((ICollection<KeyValuePair<SessionKey, MultiplexedAgwSessionStream>>)sessions)
                    .Remove(new KeyValuePair<SessionKey, MultiplexedAgwSessionStream>(key, stream));
                try { await stream.DisposeAsync(); } catch { /* best effort */ }
            }
        }, ct);
    }

    /// <summary>For 'D' / 'd' frames the (local, remote, port) tuple is
    /// flipped relative to the 'C' frame: BPQ uses CallFrom = peer,
    /// CallTo = us on the inbound and CallFrom = us, CallTo = peer for
    /// frames it forwards to us. Try both orientations.</summary>
    private static SessionKey KeyForData(AgwFrame frame) =>
        new(frame.CallTo, frame.CallFrom, frame.Port);

    private record struct SessionKey(string Local, string Remote, byte Port);
}
