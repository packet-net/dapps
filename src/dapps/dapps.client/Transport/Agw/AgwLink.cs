using System.Collections.Concurrent;
using System.Net.Sockets;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// One AGW connection to the node: the TCP socket, our callsign's
/// <c>'X'</c> registration, the keepalive, the one reader that takes
/// every frame off the socket, and the table of connected sessions the
/// reader hands their frames to. The owner opens it with
/// <see cref="OpenAsync"/>, is told of each inbound connect through the
/// callback it passed, and disposes it to close it; <see cref="Closed"/>
/// completes when the link has ended either way.
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
///      table only ever removes an entry by (key, session) so a
///      handler's teardown cannot evict its successor.
///
/// When the link ends, for whatever reason, every session in the table
/// is marked closed by the far end (its stream reads EOF, and it sends
/// no 'd': the node has dropped it already).
/// </summary>
public sealed class AgwLink : IAsyncDisposable
{
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
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    private readonly TcpClient tcp;
    private readonly AgwFrameTransport framing;
    private readonly ILogger logger;
    private readonly Action<AgwLinkSession> onInbound;
    private readonly CancellationToken stopping;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PeriodicTimer keepalive;
    private readonly CancellationTokenSource keepaliveCts;
    private readonly Task keepaliveTask;
    private readonly ConcurrentDictionary<AgwSessionKey, AgwLinkSession> sessions = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task readerTask = Task.CompletedTask;
    private int disposed;

    private AgwLink(
        TcpClient tcp, AgwFrameTransport framing, string callsign, ILogger logger,
        Action<AgwLinkSession> onInbound, PeriodicTimer keepalive, CancellationToken stopping)
    {
        this.tcp = tcp;
        this.framing = framing;
        this.logger = logger;
        this.onInbound = onInbound;
        this.stopping = stopping;
        this.keepalive = keepalive;
        Callsign = callsign;

        // Keepalive: send 'G' (port count query) every KeepaliveInterval
        // so BPQ doesn't drop the idle socket. The reply lands in the
        // reader and is ignored as a default-case frame. AgwFrameTransport
        // serialises the shared write side - session 'D' frames go out
        // from other threads too, and AGW is a single-stream protocol.
        keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var keepaliveToken = keepaliveCts.Token;
        keepaliveTask = Task.Run(async () =>
        {
            try
            {
                while (await keepalive.WaitForNextTickAsync(keepaliveToken))
                {
                    await framing.WriteFrameAsync(
                        new AgwFrame(0, 'G', 0, "", "", []), keepaliveToken);
                    logger.LogDebug("AGW inbound: keepalive 'G' sent");
                }
            }
            catch (OperationCanceledException) { /* expected on link end */ }
            catch (Exception ex)
            {
                // A keepalive write failure means the socket's already
                // dead; the reader will see the same EOF and end the
                // link. Log at debug to avoid duplicating the warn that
                // the owner will emit.
                logger.LogDebug(ex, "AGW keepalive write failed");
            }
        }, keepaliveToken);
    }

    /// <summary>The callsign this connection registered with its 'X'.</summary>
    public string Callsign { get; }

    /// <summary>Completes (never faults) once the link has ended: the
    /// socket dropped or failed, or the link was disposed. By then every
    /// session has been told the far end has gone.</summary>
    public Task Closed => closed.Task;

    /// <summary>Why the link ended, once <see cref="Closed"/> has
    /// completed: what the reader failed with (an
    /// <see cref="OperationCanceledException"/> after a dispose), or
    /// null when a dispose landed between two frames and the reader
    /// stopped without one.</summary>
    public Exception? CloseReason { get; private set; }

    /// <summary>
    /// Connects to the node's AGW port, registers <paramref name="callsign"/>
    /// and waits for the node's reply, at most
    /// <see cref="AgwFrameTransport.RegisterReplyTimeout"/>. Anything but
    /// 0x01 fails the open with an <see cref="IOException"/> saying the
    /// callsign was refused. Once open, the link reads frames until it
    /// is disposed or the socket ends.
    /// </summary>
    /// <param name="onInbound">Called on the reader for each inbound
    /// connect, once its session is in the table. It must not block; if
    /// it throws, the reader logs that and carries on, and the session
    /// stays in the table unless the callback removed it.</param>
    /// <param name="logger">The owner's logger: the link logs as it does.</param>
    /// <param name="clock">Drives the keepalive.</param>
    /// <param name="onRegisterSent">Called once the 'X' has gone out,
    /// before its reply.</param>
    /// <param name="stopping">Cancels session writes in flight: the
    /// owner shutting down. The link ending does not cancel them; they
    /// fail on the closed socket instead.</param>
    /// <param name="ct">Cancels the open, and only the open.</param>
    public static async Task<AgwLink> OpenAsync(
        string host,
        int port,
        string callsign,
        Action<AgwLinkSession> onInbound,
        ILogger logger,
        IDappsTxGate? txGate = null,
        TimeProvider? clock = null,
        Action? onRegisterSent = null,
        CancellationToken stopping = default,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        AgwLink? link = null;
        try
        {
            logger.LogInformation("AGW inbound: connecting {0}:{1}", host, port);
            await tcp.ConnectAsync(host, port, ct);
            var framing = new AgwFrameTransport(tcp.GetStream(), txGate, logger);

            // Keepalive cadence starts before the 'X' goes out so that, once
            // BPQ (or a test double) has seen our registration, the timer is
            // guaranteed to exist - a fake clock advanced past
            // KeepaliveInterval then deterministically produces a 'G'.
            var keepalive = new PeriodicTimer(KeepaliveInterval, clock ?? TimeProvider.System);

            // 'X' register so BPQ knows where to dispatch inbound 'C' frames.
            // Note this is necessary but *not sufficient*: the operator must
            // also have the callsign on an APPLICATION line in bpq32.cfg
            // (apps-interface.md). Without that, the X register is a no-op
            // for inbound and we'll sit here happily forever, never being
            // dispatched to.
            try
            {
                await framing.WriteFrameAsync(new AgwFrame(0, 'X', 0, callsign, "", []), ct);
            }
            catch
            {
                keepalive.Dispose();
                throw;
            }
            onRegisterSent?.Invoke();

            link = new AgwLink(tcp, framing, callsign, logger, onInbound, keepalive, stopping);

            // The reply to the 'X', under a timeout: a host that takes the
            // TCP connection and never answers (the AGW port setting
            // pointing at some other service) must not leave us sitting
            // here deaf. Real time, not the injected clock: it bounds a
            // wait on the network, and the fake clocks in tests drive the
            // backoff and keepalive. Anything but 0x01 means the host
            // would not give us the callsign (XRouter answers 0x00 when
            // another connection holds it), and inbound connects would
            // never reach us: back off and say so rather than run deaf.
            var registered = await framing.ReadRegisterReplyAsync(callsign, TimeProvider.System, ct);
            if (registered.Payload is not [0x01])
            {
                throw new IOException(
                    $"AGW inbound: the node refused callsign {callsign} (callsign refused, register reply [{string.Join(',', registered.Payload)}]); " +
                    "is it already registered on another AGW connection?");
            }

            link.readerTask = Task.Run(link.ReadLoopAsync);
            return link;
        }
        catch
        {
            if (link is not null) await link.TearDownAsync();
            tcp.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await framing.ReadFrameAsync(lifetime.Token);
                // One frame's consumer failing must never end the socket:
                // BPQ would drop every inbound link on it at once.
                try
                {
                    Dispatch(frame);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "AGW inbound: handling a '{0}' frame {1}->{2} on port {3} failed; carrying on with the connection",
                        frame.Kind, frame.CallFrom, frame.CallTo, frame.Port);
                }
            }
        }
        catch (Exception ex)
        {
            reason = ex;
        }
        finally
        {
            try
            {
                await TearDownAsync();
            }
            finally
            {
                CloseReason = reason;
                closed.TrySetResult();
            }
        }
    }

    private async Task TearDownAsync()
    {
        // Stop the keepalive before tearing down sessions to avoid
        // a doomed write racing the socket close.
        keepaliveCts.Cancel();
        try { await keepaliveTask; } catch { /* swallow - already logged */ }
        keepalive.Dispose();

        // Tear down any in-flight sessions cleanly so handlers exit.
        foreach (var s in sessions.Values) s.Stream.SignalRemoteDisconnect();
        sessions.Clear();
        tcp.Dispose();
    }

    /// <summary>Closes the link, if it is not closed already, and waits
    /// for the reader to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) await lifetime.CancelAsync();
        try { await readerTask; } catch { /* the reader records its own end */ }
    }

    private void Dispatch(AgwFrame frame)
    {
        switch (frame.Kind)
        {
            case 'X':
                logger.LogDebug("AGW inbound: a further 'X' reply; ignored");
                break;

            case 'C':
                OnConnect(frame);
                break;

            case 'D':
                if (TryResolve(frame, out var data, out var dataPortMismatch))
                {
                    if (dataPortMismatch)
                    {
                        logger.LogDebug("AGW inbound: 'D' on port {0} matched session {1}<->{2} on port {3} by callsign pair",
                            frame.Port, data.Key.Local, data.Key.Remote, data.Key.Port);
                    }
                    // Never waits: one slow session must not hold up the
                    // frames of every other session on this socket.
                    if (data.Stream.TryPush(frame.Payload) == PushResult.Overflowed)
                    {
                        logger.LogWarning(
                            "AGW inbound: session {0}<->{1} left more than {2} KB unread; closing it",
                            data.Key.Local, data.Key.Remote, MultiplexedAgwSessionStream.MaxUnreadBytes / 1024);
                    }
                }
                else
                {
                    logger.LogDebug("AGW inbound: 'D' for unknown session {0}<->{1} on port {2}; dropped",
                        frame.CallFrom, frame.CallTo, frame.Port);
                }
                break;

            case 'd':
                if (TryResolve(frame, out var closing, out var closePortMismatch))
                {
                    // Remove by (key, session), never by key alone: the
                    // handler's own teardown uses the same rule, and a
                    // newer session may already sit under this key.
                    Remove(closing);
                    closing.Stream.SignalRemoteDisconnect();
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

    private void OnConnect(AgwFrame frame)
    {
        // BPQ delivers an inbound 'C' frame with CallFrom = the remote
        // station that connected to us, CallTo = our local APPL call.
        var remote = frame.CallFrom;
        var local = frame.CallTo;
        var port = frame.Port;
        logger.LogInformation("AGW inbound: 'C' from {0} to {1} on port {2}", remote, local, port);

        // Some AGW emulators emit a "*** CONNECTED..." status string in
        // the 'C' payload; that's noise from dapps's POV and we just
        // discard it.

        var session = new AgwLinkSession(this, port, local, remote);

        // Rule 2 (class doc): BPQ never dispatches a live duplicate, so
        // an entry still under this key is stale - its 'd' was lost, or
        // arrived in a form we could not match. Retire it in place.
        // Marking it remote-closed keeps its handler's teardown from
        // emitting a 'd' that BPQ would apply to the session we are
        // about to register.
        if (sessions.TryRemove(session.Key, out var stale))
        {
            stale.Stream.SignalRemoteDisconnect();
            logger.LogWarning(
                "AGW inbound: 'C' for {0}<->{1} on port {2} while a session was still registered for that pair; " +
                "retiring the stale entry (its 'd' never reached us in a form we could match)",
                local, remote, port);
        }

        sessions[session.Key] = session;
        onInbound(session);
    }

    /// <summary>Takes <paramref name="session"/> out of the table if it
    /// is still there; a newer session under the same key stays.</summary>
    internal void Remove(AgwLinkSession session) =>
        sessions.TryRemove(new KeyValuePair<AgwSessionKey, AgwLinkSession>(session.Key, session));

    internal Task WriteDataAsync(byte port, string local, string remote, byte[] data) =>
        framing.WriteDataAsync(port, local, remote, data, stopping);

    internal Task WriteFrameAsync(AgwFrame frame, CancellationToken ct) => framing.WriteFrameAsync(frame, ct);

    /// <summary>
    /// Finds the session a 'D' / 'd' frame refers to. Exact
    /// (local, remote, port) first; failing that, the callsign pair
    /// alone when it identifies exactly one session (rule 1 in the
    /// class doc). Two sessions for the same pair on different ports
    /// make a port-less frame genuinely ambiguous, and we leave both
    /// alone rather than guess.
    /// </summary>
    private bool TryResolve(AgwFrame frame, out AgwLinkSession match, out bool portMismatch)
    {
        portMismatch = false;
        var exact = KeyForData(frame);
        if (sessions.TryGetValue(exact, out var session))
        {
            match = session;
            return true;
        }

        AgwLinkSession? candidate = null;
        foreach (var kv in sessions)
        {
            if (!kv.Key.SamePair(exact)) continue;
            if (candidate is not null)
            {
                match = null!;
                return false;
            }
            candidate = kv.Value;
        }

        if (candidate is null)
        {
            match = null!;
            return false;
        }

        match = candidate;
        portMismatch = true;
        return true;
    }

    /// <summary>For 'D' / 'd' frames the callsign pair is flipped
    /// relative to the 'C' frame: BPQ uses CallFrom = peer, CallTo = us
    /// on the inbound connect and CallFrom = peer, CallTo = us on the
    /// frames it forwards to us for that session.</summary>
    private static AgwSessionKey KeyForData(AgwFrame frame) =>
        AgwSessionKey.For(frame.CallTo, frame.CallFrom, frame.Port);
}

/// <summary>
/// One connected session in an <see cref="AgwLink"/>'s table. Its
/// <see cref="Stream"/> reads what the reader routes to it and writes
/// 'D' frames on the link; disposing the stream sends the 'd', unless
/// the far end closed it or <see cref="Retire"/> did.
/// </summary>
public sealed class AgwLinkSession
{
    private readonly AgwLink link;
    // Once cancelled, the link is a newer session's: anything this one
    // still tried to send, a 'd' above all, would land in that session,
    // so it goes nowhere. Checked at the moment of each write rather than
    // left to whoever cancels it, which can run after the handler has
    // already seen its token cancelled and started tearing down.
    private CancellationToken silenced;

    internal AgwLinkSession(AgwLink link, byte port, string local, string remote)
    {
        this.link = link;
        Port = port;
        Local = local;
        Remote = remote;
        Key = AgwSessionKey.For(local, remote, port);
        // A write that got past the check goes out whole, though, not cut
        // off by the handler's token part way through its frames: half a
        // message would leave the peer's new session reading our next
        // bytes as payload or commands.
        Stream = new MultiplexedAgwSessionStream(
            writeOutgoing: async (data, _) =>
            {
                if (silenced.IsCancellationRequested) return;
                await link.WriteDataAsync(port, local, remote, data);
            },
            sendRemoteDisconnect: async c =>
            {
                if (silenced.IsCancellationRequested) return;
                await link.WriteFrameAsync(new AgwFrame(port, 'd', 0, local, remote, []), c);
            });
    }

    public byte Port { get; }

    /// <summary>Our callsign, as the node gave it on the connect.</summary>
    public string Local { get; }

    /// <summary>The peer's callsign, as the node gave it on the connect.</summary>
    public string Remote { get; }

    public MultiplexedAgwSessionStream Stream { get; }

    internal AgwSessionKey Key { get; }

    /// <summary>From the moment <paramref name="token"/> is cancelled
    /// this session sends nothing more, not even its 'd'. Set once,
    /// before the session's handler starts.</summary>
    public void SilenceWhen(CancellationToken token) => silenced = token;

    /// <summary>A newer session with this peer has the link: out of the
    /// table, so nothing more is routed to it, and marked closed by the
    /// far end, so its teardown never sends the 'd' that the node would
    /// apply to the newer session (rule 3 in <see cref="AgwLink"/>).</summary>
    public void Retire()
    {
        link.Remove(this);
        Stream.SignalRemoteDisconnect();
    }

    /// <summary>Takes this session out of the table, if it is still
    /// there; a newer one under the same key is left alone.</summary>
    public void Remove() => link.Remove(this);
}

/// <summary>An AGW session's identity: (local, remote, port), callsigns
/// upper-cased.</summary>
internal readonly record struct AgwSessionKey(string Local, string Remote, byte Port)
{
    public static AgwSessionKey For(string local, string remote, byte port) =>
        new(local.ToUpperInvariant(), remote.ToUpperInvariant(), port);

    public bool SamePair(AgwSessionKey other) =>
        string.Equals(Local, other.Local, StringComparison.Ordinal)
        && string.Equals(Remote, other.Remote, StringComparison.Ordinal);
}
