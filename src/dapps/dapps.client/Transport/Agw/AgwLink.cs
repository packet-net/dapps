using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// One AGW connection to the node: the TCP socket, our callsign's
/// <c>'X'</c> registration, the monitor, the keepalive, the one reader
/// that takes every frame off the socket, and the table of connected
/// sessions the reader hands their frames to, inbound and outbound
/// alike. The owner opens it with <see cref="OpenAsync"/>, is told of
/// each inbound connect through the callback it passed, and disposes it
/// to close it; <see cref="Closed"/> completes when the link has ended
/// either way. Our own calls go out on it with <see cref="DialAsync"/>
/// (#272: BPQ hands an inbound call to the first socket registered for
/// the callsign, so there must only ever be one).
///
/// Session identity. AGW frames carry no session id: a session is the
/// (local, remote, port) triple, the same for a call in either
/// direction, and BPQ reuses it the moment a peer redials. Everything
/// below that touches the session table follows three rules that fall
/// out of how BPQ's AGWAPI.c actually behaves:
///   1. BPQ stamps its disconnect notification ('d') with the port of
///      the last frame it *received from us* on the socket, not the
///      session's port (SendDisMsgtoAppl copies sockptr-&gt;AGWRXHeader.Port).
///      After a 'G' keepalive that is port 0 regardless of the session.
///      So a 'd' (or 'D') that matches no session exactly is resolved
///      by callsign pair alone, provided that pair identifies exactly
///      one session.
///   2. BPQ refuses a second inbound connect for a pair it still holds
///      a session for (AGWConnected, "Callsign is already connected"),
///      before any 'C' reaches us. So an inbound 'C' for a key we still
///      have an entry for means that entry is stale, never a live
///      duplicate: retire it in place and accept the new session.
///   3. A retired or remotely-closed session must never emit a 'd' of
///      its own, because BPQ resolves an app-sent 'd' by callsign pair
///      too and would tear down whatever newer session holds it.
///      <see cref="MultiplexedAgwSessionStream"/> enforces that, and a
///      session another one has replaced under its key sends nothing at
///      all; the table only ever removes an entry by (key, session) so a
///      handler's teardown cannot evict its successor.
///
/// The two kinds of 'C' are told apart by their text alone: the
/// confirmation of our own call reads "*** CONNECTED With Station",
/// an inbound call "*** CONNECTED To Station". BPQ answers each 'd' we
/// send with "*** DISCONNECTED RETRYOUT With" at once, on the 'd''s own
/// port, and, if the 'd' matched a connection, "*** DISCONNECTED From
/// Station" a moment later; Dire Wolf answers with the second alone,
/// once the link has gone. The link expects them for a while and drops them, so they
/// never end a newer session. A call that fails reads "RETRYOUT With"
/// too, on both hosts: a dial waiting under that exact key takes it.
///
/// When the link ends, for whatever reason, every dial still waiting
/// fails and every session in the table is marked closed by the far end
/// (its stream reads EOF, and it sends no 'd': the node has dropped it
/// already).
///
/// Every frame we send goes through one writer
/// (<see cref="AgwFrameTransport.StartWriter"/>), in order and each one
/// whole. A write the node has not taken within
/// <see cref="AgwFrameTransport.WriteStallLimit"/> ends the link, as the
/// socket dropping would: every session, call and beacon shares it.
///
/// The node's port list (its answer to a 'G', which the keepalive asks
/// for every <see cref="KeepaliveInterval"/>) is kept as
/// <see cref="LatestPorts"/>, and <see cref="QueryPortsAsync"/> asks for
/// it afresh when that is old.
///
/// A link opened listen-only (<see cref="ListenOnly"/>) sends no 'X':
/// it is for hearing discovery's UI frames when AGW is not the bearer.
/// BPQ and XRouter show the monitor to any socket, and hand inbound calls
/// only to a socket that registered the callsign. It sends no UI frames:
/// XRouter drops an 'M' from a socket that registered no callsign, so
/// discovery sends those through the bearer instead. It never calls out,
/// and an inbound call that reaches it all the same is disconnected.
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

    /// <summary>
    /// How long a dial waits, once the peer's own SABM to us has shown on
    /// the monitor, for either our confirmation or the peer's call to
    /// reach us, before giving up. When our 'C' reaches BPQ in the last
    /// few tens of ms before the peer's SABM, BPQ refuses the peer's call
    /// as a duplicate of ours and both calls die without a word to us
    /// (docs-internal/end-to-end-tests.md, "One AGW socket per node");
    /// in every normal crossing our confirmation came within 5 s.
    /// </summary>
    public static readonly TimeSpan CrossedDialGiveUp = TimeSpan.FromSeconds(15);

    /// <summary>How long after a 'd' of ours its "RETRYOUT With" answer
    /// is expected. BPQ sends it as it reads our 'd'; Dire Wolf never does.</summary>
    private static readonly TimeSpan RetryoutEchoWindow = TimeSpan.FromSeconds(10);

    /// <summary>How long after a 'd' of ours its "From Station" answer is
    /// expected: Dire Wolf sends it once the link is down, which can take
    /// FRACK x RETRIES on air, kept under 3 minutes (docs/tune.md). BPQ
    /// sends it within two ticks of its 10 Hz timer if the 'd' matched a
    /// connection (TNCTimerProc drops the host session, and the AGW poll
    /// reports it), and not at all otherwise, so once BPQ's "RETRYOUT
    /// With" has come the "From Station" is expected for
    /// <see cref="RetryoutEchoWindow"/> more at most.</summary>
    private static readonly TimeSpan FromEchoWindow = TimeSpan.FromMinutes(3);

    /// <summary>The longest a dial's best-effort 'd' may take.</summary>
    private static readonly TimeSpan DisconnectWriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How old <see cref="LatestPorts"/> may be for
    /// <see cref="QueryPortsAsync"/> to answer with it: two keepalives.</summary>
    public static readonly TimeSpan PortListFreshFor = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="QueryPortsAsync"/> waits for the
    /// node to answer its 'G'.</summary>
    public static readonly TimeSpan PortQueryTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpClient tcp;
    private readonly AgwFrameTransport framing;
    private readonly ILogger logger;
    private readonly Action<AgwLinkSession> onInbound;
    private readonly CancellationToken stopping;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PeriodicTimer keepalive;
    private readonly CancellationTokenSource keepaliveCts;
    private readonly Task keepaliveTask;
    private readonly ConcurrentDictionary<AgwSessionKey, AgwLinkSession> sessions = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Guards the dials, the monitor watchers, the expected echoes of our
    // own 'd's, the port list and its waiter, `ended` and `writeFault`.
    private readonly Lock gate = new();
    private readonly Dictionary<AgwSessionKey, PendingDial> dials = [];
    private readonly List<Action<AgwFrame>> monitorWatchers = [];
    private readonly Dictionary<AgwSessionKey, DisconnectEchoes> echoes = [];
    private volatile bool ended;
    private Exception? writeFault;
    private AgwPortList? latestPorts;
    private TaskCompletionSource<AgwPortList>? portsWanted;
    private TaskCompletionSource<(int Major, int Minor)>? versionWanted;
    private Task readerTask = Task.CompletedTask;
    private int disposed;

    private AgwLink(
        TcpClient tcp, AgwFrameTransport framing, string host, int tcpPort, string callsign, bool listenOnly, ILogger logger,
        Action<AgwLinkSession> onInbound, PeriodicTimer keepalive, TimeProvider clock, CancellationToken stopping)
    {
        this.tcp = tcp;
        Host = host;
        TcpPort = tcpPort;
        this.framing = framing;
        this.logger = logger;
        this.onInbound = onInbound;
        this.stopping = stopping;
        this.keepalive = keepalive;
        this.clock = clock;
        Callsign = callsign;
        ListenOnly = listenOnly;

        // A write the node will not take ends the link (class doc).
        _ = framing.WriteFailed.ContinueWith(t => OnWriteFailed(t.Result), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        // Keepalive: send 'G' (port count query) every KeepaliveInterval
        // so BPQ doesn't drop the idle socket. The reply lands in the
        // reader and becomes LatestPorts. The writer keeps it in turn
        // with every session's frames: AGW is a single-stream protocol.
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

    /// <summary>The callsign this connection registered with its 'X', or,
    /// on a listen-only link, sends its UI frames from.</summary>
    public string Callsign { get; }

    /// <summary>The node host this link connected to.</summary>
    public string Host { get; }

    /// <summary>The node's AGW TCP port this link connected to.</summary>
    public int TcpPort { get; }

    /// <summary>The node's last answer to a 'G' on this link, and when it
    /// came; null until one has.</summary>
    public AgwPortList? LatestPorts
    {
        get { lock (gate) return latestPorts; }
    }

    /// <summary>True for a link opened to listen only: it registered no
    /// callsign, takes no calls and makes none (class doc).</summary>
    public bool ListenOnly { get; }

    /// <summary>Completes (never faults) once the link has ended: the
    /// socket dropped or failed, or the link was disposed. By then every
    /// dial still waiting has failed and every session has been told the
    /// far end has gone.</summary>
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
    /// callsign was refused. Then turns the node's monitor on (one plain
    /// 'm'; BPQ's 'm' flips a per-socket flag that starts off). Once
    /// open, the link reads frames until it is disposed or the socket
    /// ends.
    /// </summary>
    /// <param name="onInbound">Called on the reader for each inbound
    /// connect, once its session is in the table. It must not block; if
    /// it throws, the reader logs that and carries on, and the session
    /// stays in the table unless the callback removed it.</param>
    /// <param name="logger">The owner's logger: the link logs as it does.</param>
    /// <param name="clock">Drives the keepalive and the dials' deadlines.</param>
    /// <param name="onRegisterSent">Called once the 'X' has gone out,
    /// before its reply.</param>
    /// <param name="stopping">Cancels session writes in flight: the
    /// owner shutting down. The link ending does not cancel them; they
    /// fail with an <see cref="IOException"/> instead.</param>
    /// <param name="ct">Cancels the open, and only the open.</param>
    /// <param name="listenOnly">Send no 'X' (class doc): the link only
    /// sends and hears UI frames. <paramref name="onInbound"/> is then
    /// never called.</param>
    public static Task<AgwLink> OpenAsync(
        string host,
        int port,
        string callsign,
        Action<AgwLinkSession> onInbound,
        ILogger logger,
        IDappsTxGate? txGate = null,
        TimeProvider? clock = null,
        Action? onRegisterSent = null,
        CancellationToken stopping = default,
        CancellationToken ct = default,
        bool listenOnly = false) =>
        OpenCoreAsync(host, port, callsign, onInbound, logger, txGate, clock, onRegisterSent, stopping, ct, listenOnly, writeClock: null);

    /// <summary><see cref="OpenAsync"/>, with the clock the writer times
    /// a stalled write on (<paramref name="writeClock"/>; the system clock
    /// if null). Real time otherwise, as the waits for the node's replies:
    /// it bounds a wait on the network. For tests.</summary>
    internal static async Task<AgwLink> OpenCoreAsync(
        string host,
        int port,
        string callsign,
        Action<AgwLinkSession> onInbound,
        ILogger logger,
        IDappsTxGate? txGate,
        TimeProvider? clock,
        Action? onRegisterSent,
        CancellationToken stopping,
        CancellationToken ct,
        bool listenOnly,
        TimeProvider? writeClock)
    {
        var tcp = new TcpClient();
        AgwLink? link = null;
        AgwFrameTransport? framing = null;
        try
        {
            logger.LogInformation("AGW inbound: connecting {0}:{1}", host, port);
            await tcp.ConnectAsync(host, port, ct);
            framing = new AgwFrameTransport(tcp.GetStream(), txGate, logger);
            framing.StartWriter(writeClock);

            // Keepalive cadence starts before the 'X' goes out so that, once
            // BPQ (or a test double) has seen our registration, the timer is
            // guaranteed to exist - a fake clock advanced past
            // KeepaliveInterval then deterministically produces a 'G'.
            var keepalive = new PeriodicTimer(KeepaliveInterval, clock ?? TimeProvider.System);

            if (listenOnly)
            {
                link = new AgwLink(tcp, framing, host, port, callsign, listenOnly: true, logger, onInbound, keepalive, clock ?? TimeProvider.System, stopping);
                await framing.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);

                // With no 'X' to answer, a port query ('G') stands in as
                // proof the host speaks AGW: a service that takes the TCP
                // connection and closes it, or never answers, fails the
                // open and backs off like a refused registration. Real
                // time, as the 'X' reply below.
                await framing.WriteFrameAsync(new AgwFrame(0, 'G', 0, "", "", []), ct);
                var ports = await framing.ReadReplyAsync('G', "a port query", TimeProvider.System, ct);
                link.latestPorts = new AgwPortList(ports.Payload, link.clock.GetUtcNow());

                link.readerTask = Task.Run(link.ReadLoopAsync);
                return link;
            }

            // 'X' register so BPQ knows where to dispatch inbound 'C' frames.
            // Note this is necessary but *not sufficient*: the operator must
            // also have the callsign on an APPLICATION line in bpq32.cfg
            // (apps-interface.md). Without that, the X register is a no-op
            // for inbound and we'll sit here happily forever, never being
            // dispatched to. It is also what our own calls go out from:
            // BPQ sends them from the socket's first registered callsign
            // whatever the 'C' says, so it is registered once and never
            // again on this connection.
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

            link = new AgwLink(tcp, framing, host, port, callsign, listenOnly: false, logger, onInbound, keepalive, clock ?? TimeProvider.System, stopping);

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

            // The monitor, for our dials to spot the peer calling us as
            // we call it. One plain 'm' per connection: BPQ's 'm' toggles.
            await framing.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);

            link.readerTask = Task.Run(link.ReadLoopAsync);
            return link;
        }
        catch
        {
            if (link is not null) await link.TearDownAsync();
            tcp.Dispose();
            if (framing is not null) await framing.StopWriterAsync();
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW inbound: closing the link's sessions failed part-way");
            }
            finally
            {
                lock (gate) CloseReason = writeFault ?? reason;
                closed.TrySetResult();
            }
        }
    }

    /// <summary>The writer has failed or stalled: the link ends, and says
    /// why. Not once it is ending anyway (closing the socket fails the
    /// write in hand).</summary>
    private void OnWriteFailed(Exception reason)
    {
        lock (gate)
        {
            if (ended) return;
            writeFault = reason;
        }
        lifetime.Cancel();
    }

    private async Task TearDownAsync()
    {
        // No dial may join once this is set, and no session write goes
        // out; the dials waiting fail below.
        TaskCompletionSource<AgwPortList>? portsWaiting;
        lock (gate)
        {
            ended = true;
            portsWaiting = portsWanted;
            portsWanted = null;
        }
        portsWaiting?.TrySetException(new IOException("AGW: the connection to the node closed while asking it for its ports"));
        TaskCompletionSource<(int, int)>? versionWaiting;
        lock (gate)
        {
            versionWaiting = versionWanted;
            versionWanted = null;
        }
        versionWaiting?.TrySetException(new IOException("AGW: the connection to the node closed while asking it for its version"));

        // Stop the keepalive before tearing down sessions to avoid
        // a doomed write racing the socket close.
        keepaliveCts.Cancel();
        try { await keepaliveTask; } catch { /* swallow - already logged */ }
        keepalive.Dispose();

        List<PendingDial> waiting;
        lock (gate)
        {
            waiting = [.. dials.Values];
            dials.Clear();
            monitorWatchers.Clear();
        }
        foreach (var dial in waiting)
        {
            dial.Outcome.TrySetException(new IOException(
                $"AGW: the connection to the node closed while the connect to {dial.Remote} was waiting"));
        }

        // Tear down any in-flight sessions cleanly so handlers exit. Every
        // session gets its notice even if one of them throws.
        List<Exception>? failures = null;
        foreach (var s in sessions.Values)
        {
            try { s.Stream.SignalRemoteDisconnect(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        sessions.Clear();
        tcp.Dispose();
        await framing.StopWriterAsync();
        if (failures is not null) throw new AggregateException(failures);
    }

    /// <summary>Closes the link, if it is not closed already, and waits
    /// for the reader to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) await lifetime.CancelAsync();
        try { await readerTask; } catch { /* the reader records and logs its own end */ }
    }

    /// <summary>
    /// Calls <paramref name="remote"/> from <paramref name="local"/> on
    /// <paramref name="port"/>, and returns once the node confirms it
    /// ("*** CONNECTED With Station"). The call is in the session table,
    /// as the inbound ones are, before the next frame is read. Fails with
    /// an <see cref="IOException"/> when the node reports the call failed,
    /// when the link closes, or when the peer's own call to us shows on
    /// the monitor and neither call connects within
    /// <see cref="CrossedDialGiveUp"/>; with a <see cref="TimeoutException"/>
    /// when the node has not answered within <paramref name="deadline"/>
    /// (on the link's clock, and not extended by frames that arrive
    /// meanwhile). After a timeout, a give-up or a cancellation it sends
    /// the node a best-effort 'd'.
    /// </summary>
    /// <param name="local">Must be the callsign this link registered: BPQ
    /// sends every call from that one, and keys what comes back on the
    /// one in the 'C'.</param>
    /// <param name="dialLogger">Where the dial's own lines go.</param>
    public Task<AgwDial> DialAsync(
        byte port, string local, string remote, TimeSpan deadline, CancellationToken ct, ILogger? dialLogger = null) =>
        DialCoreAsync(port, local, remote, deadline, ct, dialLogger, nodeLocal: false);

    private async Task<AgwDial> DialCoreAsync(
        byte port, string local, string remote, TimeSpan deadline, CancellationToken ct, ILogger? dialLogger, bool nodeLocal)
    {
        if (ListenOnly)
        {
            throw new InvalidOperationException(
                $"AGW: this connection only listens for discovery beacons, so it cannot call {remote}");
        }
        if (!string.Equals(local, Callsign, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"AGW: this link is registered as {Callsign}, so it cannot call from {local}", nameof(local));
        }
        var log = dialLogger ?? logger;
        var dial = new PendingDial(this, port, local, remote, log) { NodeLocal = nodeLocal };

        // The dial and its monitor watcher are in place before the 'C'
        // goes out, so nothing the node says about it can be missed.
        lock (gate)
        {
            if (ended) throw new IOException($"AGW: the connection to the node has closed; {remote} was not called");
            if (!dials.TryAdd(dial.Key, dial))
            {
                throw new InvalidOperationException($"AGW: a call to {remote} on port {port} is already on its way");
            }
            monitorWatchers.Add(dial.OnMonitor);
        }

        try
        {
            if (nodeLocal) log.LogDebug("AGW: opening a session to the node's prompt as {0}", remote);
            else log.LogInformation("AGW: requesting {local}->{remote} on port {p}", local, remote, port);
            try
            {
                await framing.WriteFrameAsync(new AgwFrame(port, 'C', 0xF0, local, remote, []), ct, nodeLocal);
            }
            catch
            {
                // Nothing went to the node: nothing to call off.
                Withdraw(dial);
                throw;
            }
            dial.CallSent = true;

            // One overall deadline for the answer (#278): monitored frames
            // and anything else that arrives meanwhile do not extend it.
            using var timeout = new CancellationTokenSource(deadline, clock);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            AgwLinkSession session;
            try
            {
                session = await dial.Outcome.Task.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested)
            {
                await CallOffAsync(dial);
                if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"AGW: the node did not answer the connect to {remote} within {deadline.TotalMinutes:F0} minutes");
                }
                throw;
            }
            catch (IOException) when (dial.GaveUp)
            {
                await CallOffAsync(dial);
                throw;
            }

            if (nodeLocal) log.LogDebug("AGW: connect confirmed ({0})", dial.ConfirmText);
            else log.LogInformation("AGW: connect confirmed ({0})", dial.ConfirmText);
            return new AgwDial(session, dial, log);
        }
        catch
        {
            dial.StopWatching();
            throw;
        }
    }

    /// <summary>
    /// Takes a dial that is still waiting off the link and tells the node
    /// to drop it ('d', best effort), unless a session holds the pair: the
    /// node would apply the 'd' to that one. One that has connected
    /// meanwhile is closed as a session instead.
    /// </summary>
    private async Task CallOffAsync(PendingDial dial)
    {
        if (!Withdraw(dial) && !dial.GaveUp)
        {
            // Taken by the reader just now: confirmed, failed, or the link
            // closing, and its outcome is being set. A confirmed call
            // nobody is waiting for any more is closed here.
            try
            {
                var session = await dial.Outcome.Task;
                session.Remove();
                try { await session.Stream.DisposeAsync(); } catch { /* best effort */ }
            }
            catch { /* failed: nothing to close */ }
            return;
        }
        if (sessions.Keys.Any(k => k.SamePair(dial.Key)))
        {
            logger.LogDebug("AGW: not calling off the call to {0} with a 'd': a session holds that pair", dial.Remote);
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(DisconnectWriteTimeout);
            await SendDisconnectAsync(dial.Port, dial.Local, dial.Remote, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AGW: best-effort 'd' for the call to {0} failed", dial.Remote);
        }
    }

    /// <summary>Takes <paramref name="dial"/> off the link if it is still
    /// waiting. False if it has connected, failed or gone already.</summary>
    private bool Withdraw(PendingDial dial)
    {
        lock (gate)
        {
            if (!dials.TryGetValue(dial.Key, out var held) || !ReferenceEquals(held, dial)) return false;
            dials.Remove(dial.Key);
            return true;
        }
    }

    /// <summary>
    /// Has <paramref name="watcher"/> called, on the reader, with every
    /// monitored frame the node sends ('U', 'S', 'I' and 'T'), until the
    /// returned handle is disposed. It must not block; if it throws, the
    /// reader logs that and carries on. A monitored frame's CallTo is
    /// whatever BPQ last put there: read the frame's text, not its header.
    /// </summary>
    public IDisposable WatchMonitor(Action<AgwFrame> watcher)
    {
        lock (gate)
        {
            if (!ended) monitorWatchers.Add(watcher);
        }
        return new MonitorWatch(this, watcher);
    }

    private void StopWatching(Action<AgwFrame> watcher)
    {
        lock (gate) monitorWatchers.Remove(watcher);
    }

    /// <summary>
    /// Has <paramref name="onUi"/> called, on the reader, with every UI
    /// frame ('U') the monitor shows, until the returned handle is disposed
    /// or the link ends. As <see cref="WatchMonitor"/>: it must not block,
    /// and the frame's CallTo is not to be trusted.
    /// </summary>
    public IDisposable SubscribeUi(Action<AgwFrame> onUi) =>
        WatchMonitor(frame =>
        {
            if (frame.Kind == 'U') onUi(frame);
        });

    /// <summary>
    /// Sends <paramref name="payload"/> as a UI frame ('M', PID 0xF0) from
    /// <see cref="Callsign"/> to <paramref name="to"/> on
    /// <paramref name="port"/>. Fails with an <see cref="IOException"/> once
    /// the link has closed; nothing is held back for a later link.
    /// </summary>
    public Task SendUiAsync(byte port, string to, byte[] payload, CancellationToken ct)
    {
        if (ListenOnly)
        {
            // XRouter would drop it without a word (class doc).
            throw new InvalidOperationException("AGW: a listen-only connection sends no UI frames");
        }
        if (ended)
        {
            throw new IOException("AGW: the connection to the node has closed");
        }
        return framing.WriteFrameAsync(new AgwFrame(port, 'M', 0xF0, Callsign, to, payload), ct);
    }

    private void Dispatch(AgwFrame frame)
    {
        switch (frame.Kind)
        {
            case 'X':
                logger.LogDebug("AGW inbound: a further 'X' reply; ignored");
                break;

            case 'C':
                if (IsOurConfirmation(frame)) OnConfirm(frame);
                else OnConnect(frame);
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
                OnDisconnect(frame);
                break;

            case 'U' or 'S' or 'I' or 'T':
                Action<AgwFrame>[] watchers;
                lock (gate) watchers = [.. monitorWatchers];
                foreach (var watcher in watchers)
                {
                    try
                    {
                        watcher(frame);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "AGW inbound: a monitor watcher failed on a '{0}' frame; carrying on", frame.Kind);
                    }
                }
                break;

            case 'G':
                OnPortList(frame);
                break;

            case 'R':
                OnVersion(frame);
                break;

            default:
                logger.LogDebug("AGW inbound: ignoring frame kind '{0}'", frame.Kind);
                break;
        }
    }

    /// <summary>The node's port list, in answer to a 'G' from the
    /// keepalive or from <see cref="QueryPortsAsync"/>: the node does not
    /// say which, and either will do.</summary>
    private void OnPortList(AgwFrame frame)
    {
        var ports = new AgwPortList(frame.Payload, clock.GetUtcNow());
        TaskCompletionSource<AgwPortList>? waiting;
        lock (gate)
        {
            latestPorts = ports;
            waiting = portsWanted;
            portsWanted = null;
        }
        waiting?.TrySetResult(ports);
        logger.LogDebug("AGW inbound: port list ('G') received");
    }

    /// <summary>
    /// The node's port list: <see cref="LatestPorts"/> if it came within
    /// <see cref="PortListFreshFor"/>, else the answer to a 'G' sent now.
    /// Callers asking meanwhile share that 'G', which belongs to the link,
    /// not to any one of them: a caller's cancellation only ends its own
    /// wait. Nothing else on the link waits for it. Fails with a
    /// <see cref="TimeoutException"/> when no answer has come within
    /// <see cref="PortQueryTimeout"/> of the call (on the link's clock),
    /// the 'G' being taken back if it has not gone by then, and with an
    /// <see cref="IOException"/> at once if the link closes first.
    /// </summary>
    public async Task<AgwPortList> QueryPortsAsync(CancellationToken ct)
    {
        TaskCompletionSource<AgwPortList> answer;
        var ask = false;
        lock (gate)
        {
            if (ended) throw new IOException("AGW: the connection to the node has closed");
            if (latestPorts is { } known && clock.GetUtcNow() - known.ReceivedAt < PortListFreshFor) return known;
            if (portsWanted is null)
            {
                portsWanted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                ask = true;
            }
            answer = portsWanted;
        }

        if (ask) _ = AskForPortsAsync(answer);

        try
        {
            return await answer.Task.WaitAsync(PortQueryTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            // The next caller asks again.
            ForgetPortsWanted(answer);
            throw new TimeoutException($"AGW: the node did not answer a port query within {PortQueryTimeout.TotalSeconds:F0}s");
        }
    }

    /// <summary>Sends the shared 'G' for <paramref name="answer"/>, taken
    /// back if it has not gone within <see cref="PortQueryTimeout"/>.</summary>
    private async Task AskForPortsAsync(TaskCompletionSource<AgwPortList> answer)
    {
        using var limit = new CancellationTokenSource(PortQueryTimeout, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, lifetime.Token);
        try
        {
            await framing.WriteFrameAsync(new AgwFrame(0, 'G', 0, "", "", []), bounded.Token);
        }
        catch (Exception ex)
        {
            // Nobody else will ask: let the callers sharing it go.
            ForgetPortsWanted(answer);
            answer.TrySetException(ex is OperationCanceledException
                ? new TimeoutException($"AGW: a port query could not go to the node within {PortQueryTimeout.TotalSeconds:F0}s")
                : ex);
        }
    }

    private void ForgetPortsWanted(TaskCompletionSource<AgwPortList> answer)
    {
        lock (gate)
        {
            if (ReferenceEquals(portsWanted, answer)) portsWanted = null;
        }
    }

    /// <summary>The AGW version LinBPQ reports in answer to an 'R'
    /// (AGWAPI.c). XRouter says 2000.20, Dire Wolf something else again.</summary>
    public static readonly (int Major, int Minor) LinbpqVersion = (2003, 999);

    /// <summary>
    /// The callsign a node-prompt session is opened to. LinBPQ's AGW takes
    /// a 'C' to any call starting SWITCH as a session at its own command
    /// prompt, and sends nothing on air (AGWAPI.c checks the first six
    /// letters). Each such session needs a key of its own (#425, Rig A);
    /// this SSID is one an operator's own SWITCH-n via is unlikely to use.
    /// </summary>
    public const string NodeSessionCallsign = "SWITCH-15";

    /// <summary>
    /// The node's AGW version, from its answer to an 'R'. Fails with a
    /// <see cref="TimeoutException"/> when it has not answered within
    /// <see cref="PortQueryTimeout"/>, and with an
    /// <see cref="IOException"/> if the link closes first.
    /// </summary>
    public async Task<(int Major, int Minor)> QueryVersionAsync(CancellationToken ct)
    {
        TaskCompletionSource<(int Major, int Minor)> answer;
        var ask = false;
        lock (gate)
        {
            if (ended) throw new IOException("AGW: the connection to the node has closed");
            if (versionWanted is null)
            {
                versionWanted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                ask = true;
            }
            answer = versionWanted;
        }
        if (ask)
        {
            try
            {
                await framing.WriteFrameAsync(new AgwFrame(0, 'R', 0, "", "", []), ct);
            }
            catch
            {
                lock (gate)
                {
                    if (ReferenceEquals(versionWanted, answer)) versionWanted = null;
                }
                throw;
            }
        }
        try
        {
            return await answer.Task.WaitAsync(PortQueryTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (ReferenceEquals(versionWanted, answer)) versionWanted = null;
            }
            throw new TimeoutException($"AGW: the node did not answer a version query within {PortQueryTimeout.TotalSeconds:F0}s");
        }
    }

    private void OnVersion(AgwFrame frame)
    {
        TaskCompletionSource<(int Major, int Minor)>? waiting;
        lock (gate)
        {
            waiting = versionWanted;
            versionWanted = null;
        }
        if (frame.Payload.Length < 8)
        {
            waiting?.TrySetException(new IOException($"AGW: the node's version reply was {frame.Payload.Length} bytes, not 8"));
            return;
        }
        waiting?.TrySetResult((BitConverter.ToInt32(frame.Payload, 0), BitConverter.ToInt32(frame.Payload, 4)));
    }

    /// <summary>
    /// A session at the node's own command prompt, for asking it things
    /// (NODES, LINKS): an AGW 'C' to <see cref="NodeSessionCallsign"/> on
    /// port 0, which LinBPQ answers at once without going on air. Only on
    /// a node that says it is LinBPQ (<see cref="LinbpqVersion"/>): any
    /// other AGW host would send a SABM to a station called SWITCH, so
    /// this throws <see cref="NotSupportedException"/> there, having sent
    /// nothing. The TX gate does not apply, as nothing is transmitted.
    /// Otherwise as <see cref="DialAsync"/>; dispose the result to close it.
    /// </summary>
    public async Task<AgwDial> OpenNodeSessionAsync(TimeSpan deadline, CancellationToken ct, ILogger? dialLogger = null)
    {
        var version = await QueryVersionAsync(ct);
        if (version != LinbpqVersion)
        {
            throw new NotSupportedException(
                $"the node's AGW reports version {version.Major}.{version.Minor}, not LinBPQ's {LinbpqVersion.Major}.{LinbpqVersion.Minor}, so it has no AGW way to its prompt");
        }
        return await DialCoreAsync(0, Callsign, NodeSessionCallsign, deadline, ct, dialLogger, nodeLocal: true);
    }

    /// <summary>BPQ confirms our own call with "*** CONNECTED With
    /// Station", and announces an inbound one with "*** CONNECTED To
    /// Station"; on one socket the text is all that tells them apart.</summary>
    private static bool IsOurConfirmation(AgwFrame frame) =>
        Encoding.Latin1.GetString(frame.Payload).Contains("CONNECTED With", StringComparison.Ordinal);

    private void OnConnect(AgwFrame frame)
    {
        // BPQ delivers an inbound 'C' frame with CallFrom = the remote
        // station that connected to us, CallTo = our local APPL call.
        var remote = frame.CallFrom;
        var local = frame.CallTo;
        var port = frame.Port;
        logger.LogInformation("AGW inbound: 'C' from {0} to {1} on port {2}", remote, local, port);

        if (ListenOnly)
        {
            // BPQ hands calls only to a socket that registered the
            // callsign, so this is not expected; let the caller go rather
            // than leave it waiting for a prompt that never comes.
            logger.LogWarning(
                "AGW inbound: {0} called {1} on port {2}, but this connection only listens for discovery beacons; disconnecting it",
                remote, local, port);
            var refused = new AgwLinkSession(this, port, local, remote, outbound: false);
            _ = refused.Stream.DisposeAsync().AsTask();
            return;
        }

        var text = Encoding.Latin1.GetString(frame.Payload);
        if (frame.Payload.Length > 0 && !text.Contains("CONNECTED To", StringComparison.Ordinal))
        {
            // Not BPQ's wording for either kind. A host that confirms our
            // own calls some other way would have them taken for inbound
            // calls here, and its dials would never connect: say so.
            logger.LogWarning(
                "AGW inbound: a 'C' from {0} reads \"{1}\", neither an inbound call (\"CONNECTED To\") nor the confirmation of ours (\"CONNECTED With\"); taking it as an inbound call",
                remote, Printable(frame.Payload));
        }

        var session = new AgwLinkSession(this, port, local, remote, outbound: false);

        lock (gate)
        {
            // The node hands over an inbound call only once it holds no
            // connection under this key, so an older link's answers have
            // all come that ever will.
            echoes.Remove(session.Key);
            // The peer's call has reached us while ours is on its way:
            // the calls crossed, and the node did not refuse its call. Our
            // call may yet connect over the same link.
            if (FindDial(session.Key) is { } crossing)
            {
                crossing.PeersCallArrived = true;
                crossing.Log.LogInformation("AGW: {0}'s call reached us while ours to it was on its way", remote);
            }
        }

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

    private void OnConfirm(AgwFrame frame)
    {
        PendingDial? dial;
        lock (gate)
        {
            // Exact key first, the frame read as BPQ writes it (CallFrom
            // the peer, CallTo us) or the other way round; then the only
            // dial for the pair.
            dial = FindDial(KeyForData(frame)) ?? FindDial(AgwSessionKey.For(frame.CallFrom, frame.CallTo, frame.Port));
            // Answers still due to an older 'd' under this key stay
            // expected: the old link's "From Station" can come after our
            // new call has connected, and must not end it.
            if (dial is not null) dials.Remove(dial.Key);
        }

        if (dial is null)
        {
            // A confirmation nothing is waiting for: a dial given up just
            // before the node answered. Call it off, unless a session holds
            // the pair: BPQ would apply the 'd' to that one.
            var key = KeyForData(frame);
            var held = sessions.Keys.Any(k => k.SamePair(key));
            logger.LogWarning(
                "AGW: the node confirmed a call {0}->{1} on port {2} that no dial is waiting for{3}",
                frame.CallTo, frame.CallFrom, frame.Port, held ? "; a session holds that pair, so no 'd'" : "; disconnecting it");
            if (!held)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(DisconnectWriteTimeout);
                        await SendDisconnectAsync(frame.Port, frame.CallTo, frame.CallFrom, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "AGW: best-effort 'd' for an unexpected confirmation failed");
                    }
                });
            }
            return;
        }

        // In the table before the next frame is read, so the peer's first
        // data is routed to it. An inbound session from the same peer
        // under the same key is replaced: the link is our call's now. It
        // is not told so here; the peer registry retires it when the dial
        // reports in (and it sends nothing more from now on, rule 3).
        var session = new AgwLinkSession(this, dial.Port, dial.Local, dial.Remote, outbound: true, nodeLocal: dial.NodeLocal);
        if (sessions.ContainsKey(session.Key))
        {
            dial.Log.LogInformation("AGW: our call to {0} connected over the session it had opened with us", dial.Remote);
        }
        sessions[session.Key] = session;
        dial.ConfirmText = Printable(frame.Payload);
        dial.Outcome.TrySetResult(session);
    }

    private void OnDisconnect(AgwFrame frame)
    {
        var text = Encoding.Latin1.GetString(frame.Payload);
        var exact = KeyForData(frame);

        PendingDial? failedDial = null;
        AgwLinkSession? closing = null;
        var portMismatch = false;
        lock (gate)
        {
            DropStaleEchoes();
            sessions.TryGetValue(exact, out var exactSession);
            dials.TryGetValue(exact, out var exactDial);

            // A call that failed reads "RETRYOUT With", the same as the
            // node's answer to a 'd' of ours; a dial waiting under the
            // exact key takes it. BPQ's answer comes as it reads our 'd',
            // long before a redial under the key can go out, and Dire Wolf
            // sends none. A link that went reads "From Station": with both
            // a session and our call on the key, that is the session's.
            if (exactDial is not null && text.Contains("RETRYOUT", StringComparison.Ordinal))
            {
                failedDial = exactDial;
            }
            // Then the answers to a 'd' of our own: they are about a
            // session already gone, and must not end a newer one.
            else if (TakeEcho(exact, text, exactOnly: true))
            {
                logger.LogDebug("AGW inbound: 'd' for {0}<->{1} on port {2} is the node's answer to our own 'd'; ignored",
                    frame.CallFrom, frame.CallTo, frame.Port);
                return;
            }
            else if (exactSession is not null)
            {
                closing = exactSession;
            }
            else if (exactDial is not null)
            {
                failedDial = exactDial;
            }
            else if (TakeEcho(exact, text, exactOnly: false))
            {
                logger.LogDebug("AGW inbound: 'd' for {0}<->{1} on port {2} is the node's answer to our own 'd'; ignored",
                    frame.CallFrom, frame.CallTo, frame.Port);
                return;
            }
            else
            {
                // Rule 1: the pair alone, when it names exactly one
                // session or dial between the two tables.
                var pairSessions = sessions.Where(kv => kv.Key.SamePair(exact)).Select(kv => kv.Value).Take(2).ToList();
                var pairDials = dials.Values.Where(d => d.Key.SamePair(exact)).Take(2).ToList();
                if (pairSessions.Count + pairDials.Count == 1)
                {
                    closing = pairSessions.FirstOrDefault();
                    failedDial = pairDials.FirstOrDefault();
                    portMismatch = true;
                }
            }

            if (failedDial is not null) dials.Remove(failedDial.Key);
        }

        if (failedDial is not null)
        {
            var msg = text.TrimEnd('\0', '\r', '\n');
            failedDial.Outcome.TrySetException(new IOException($"AGW connect to {failedDial.Remote} failed: {msg}"));
            return;
        }

        if (closing is not null)
        {
            // Remove by (key, session), never by key alone: the
            // handler's own teardown uses the same rule, and a
            // newer session may already sit under this key.
            Remove(closing);
            closing.Stream.SignalRemoteDisconnect();
            logger.LogInformation("AGW inbound: session closed {0}<->{1}", frame.CallFrom, frame.CallTo);
            if (portMismatch)
            {
                logger.LogDebug(
                    "AGW inbound: that 'd' carried port {0} but the session was on port {1} - BPQ stamps " +
                    "disconnect notifications with the port of the last frame it received from us",
                    frame.Port, closing.Key.Port);
            }
            return;
        }

        // Unknown, or the pair names more than one: leave them all alone.
        logger.LogDebug("AGW inbound: 'd' for unknown session {0}<->{1} on port {2}; ignored",
            frame.CallFrom, frame.CallTo, frame.Port);
    }

    /// <summary>The dial waiting under <paramref name="key"/>, else the only
    /// one for its callsign pair. Under <see cref="gate"/>.</summary>
    private PendingDial? FindDial(AgwSessionKey key)
    {
        if (dials.TryGetValue(key, out var exact)) return exact;
        PendingDial? only = null;
        foreach (var dial in dials.Values)
        {
            if (!dial.Key.SamePair(key)) continue;
            if (only is not null) return null;
            only = dial;
        }
        return only;
    }

    /// <summary>
    /// Counts one of the node's answers to a 'd' of ours off, if
    /// <paramref name="text"/> is one still expected for the key. "RETRYOUT
    /// With" comes on the 'd''s own port, so only for the exact key;
    /// "From Station" on whatever port we used last, so unless
    /// <paramref name="exactOnly"/> also for the only key with one expected
    /// for that callsign pair. Under <see cref="gate"/>.
    /// </summary>
    private bool TakeEcho(AgwSessionKey key, string text, bool exactOnly)
    {
        var retryout = text.Contains("RETRYOUT", StringComparison.Ordinal);
        var from = text.Contains("DISCONNECTED From", StringComparison.Ordinal);
        if (!retryout && !from) return false;

        if (!echoes.ContainsKey(key))
        {
            if (exactOnly || retryout) return false;
            var pair = echoes.Where(kv => kv.Key.SamePair(key) && kv.Value.FromUntil is not null).Take(2).ToList();
            if (pair.Count != 1) return false;
            key = pair[0].Key;
        }

        var expected = echoes[key];
        if (retryout && expected.RetryoutUntil is not null)
        {
            // BPQ, then: its "From Station" comes at once or not at all
            // (FromEchoWindow), so it is not expected for long.
            var soon = clock.GetUtcNow() + RetryoutEchoWindow;
            expected = new DisconnectEchoes(null, expected.FromUntil > soon ? soon : expected.FromUntil);
        }
        else if (from && expected.FromUntil is not null) expected = expected with { FromUntil = null };
        else return false;

        if (expected is { RetryoutUntil: null, FromUntil: null }) echoes.Remove(key);
        else echoes[key] = expected;
        return true;
    }

    /// <summary>Forgets answers that have not come in time: the host does
    /// not send that one, or the 'd' matched nothing. Under <see cref="gate"/>.</summary>
    private void DropStaleEchoes()
    {
        if (echoes.Count == 0) return;
        var now = clock.GetUtcNow();
        foreach (var (key, expected) in echoes.ToList())
        {
            var still = new DisconnectEchoes(
                expected.RetryoutUntil > now ? expected.RetryoutUntil : null,
                expected.FromUntil > now ? expected.FromUntil : null);
            if (still is { RetryoutUntil: null, FromUntil: null }) echoes.Remove(key);
            else echoes[key] = still;
        }
    }

    /// <summary>Takes <paramref name="session"/> out of the table if it
    /// is still there; a newer session under the same key stays.</summary>
    internal void Remove(AgwLinkSession session) =>
        sessions.TryRemove(new KeyValuePair<AgwSessionKey, AgwLinkSession>(session.Key, session));

    /// <summary>False once another session has taken
    /// <paramref name="session"/>'s key: anything it sent would land in
    /// that one (rule 3).</summary>
    internal bool MaySend(AgwLinkSession session) =>
        !sessions.TryGetValue(session.Key, out var holder) || ReferenceEquals(holder, session);

    internal Task WriteDataAsync(byte port, string local, string remote, byte[] data, bool nodeLocal = false)
    {
        if (ended)
        {
            throw new IOException("AGW: the connection to the node has closed");
        }
        return framing.WriteDataAsync(port, local, remote, data, stopping, nodeLocal);
    }

    /// <summary>Sends a 'd' for (port, local, remote), and expects for a
    /// while the answers the node may send back for it (class doc).</summary>
    internal async Task SendDisconnectAsync(byte port, string local, string remote, CancellationToken ct)
    {
        var key = AgwSessionKey.For(local, remote, port);
        var now = clock.GetUtcNow();
        lock (gate) echoes[key] = new DisconnectEchoes(now + RetryoutEchoWindow, now + FromEchoWindow);
        // Best effort, under the caller's short bound even once the writer
        // has started it (it then still goes whole).
        await framing.WriteFrameBestEffortAsync(new AgwFrame(port, 'd', 0, local, remote, []), ct);
    }

    /// <summary>
    /// Finds the session a 'D' frame refers to. Exact
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

    /// <summary>Frame text as it can go in a log: printable ASCII only.</summary>
    private static string Printable(byte[] payload) =>
        new([.. Encoding.Latin1.GetString(payload).Where(c => c is >= ' ' and <= '~')]);

    /// <summary>Until when each answer to a 'd' of ours is still expected;
    /// null once it has come, or is no longer expected.</summary>
    private readonly record struct DisconnectEchoes(DateTimeOffset? RetryoutUntil, DateTimeOffset? FromUntil);

    private sealed class MonitorWatch(AgwLink link, Action<AgwFrame> watcher) : IDisposable
    {
        public void Dispose() => link.StopWatching(watcher);
    }

    /// <summary>One of our calls, from the 'C' until the node answers it
    /// and for as long as its monitor watcher runs.</summary>
    internal sealed class PendingDial
    {
        private readonly AgwLink link;
        private readonly TaskCompletionSource crossed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? giveUp;
        private int stopped;

        public PendingDial(AgwLink link, byte port, string local, string remote, ILogger log)
        {
            this.link = link;
            Port = port;
            Local = local;
            Remote = remote;
            Log = log;
            Key = AgwSessionKey.For(local, remote, port);
        }

        public AgwSessionKey Key { get; }
        public byte Port { get; }
        public string Local { get; }
        public string Remote { get; }
        public ILogger Log { get; }
        public TaskCompletionSource<AgwLinkSession> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CrossedCall => crossed.Task;
        public string ConfirmText { get; set; } = "";

        /// <summary>A session to the node's own prompt: nothing it sends
        /// goes on air (<see cref="OpenNodeSessionAsync"/>).</summary>
        public bool NodeLocal { get; init; }

        // Set by the dialler once the 'C' is written, read by the reader.
        public volatile bool CallSent;
        // Under the link's gate.
        public bool PeersCallArrived;
        public volatile bool GaveUp;

        /// <summary>
        /// A monitored frame, on the reader. The peer's SABM to us means it
        /// is calling us as we call it: the calls crossed, and no prompt is
        /// coming. While our call is still waiting, that also starts the
        /// <see cref="CrossedDialGiveUp"/> clock.
        /// </summary>
        public void OnMonitor(AgwFrame monitored)
        {
            if (!AgwOutboundTransport.IsPeerCallingUs(monitored, Port, Local, Remote)) return;
            if (crossed.TrySetResult())
            {
                Log.LogInformation("AGW: {0} is calling us as we call it: the calls crossed", Remote);
            }
            if (!CallSent || Outcome.Task.IsCompleted || giveUp is not null) return;
            lock (link.gate)
            {
                if (PeersCallArrived || !link.dials.TryGetValue(Key, out var held) || !ReferenceEquals(held, this)) return;
            }
            giveUp = link.clock.CreateTimer(_ => GiveUp(), null, CrossedDialGiveUp, Timeout.InfiniteTimeSpan);
        }

        private void GiveUp()
        {
            lock (link.gate)
            {
                if (PeersCallArrived || !link.dials.TryGetValue(Key, out var held) || !ReferenceEquals(held, this)) return;
                link.dials.Remove(Key);
                GaveUp = true;
            }
            Log.LogInformation(
                "AGW: {0}'s call crossed ours and neither has connected within {1:F0}s; giving up our call",
                Remote, CrossedDialGiveUp.TotalSeconds);
            Outcome.TrySetException(new IOException(
                $"AGW connect to {Remote} failed: its call crossed ours and neither connected within {CrossedDialGiveUp.TotalSeconds:F0}s"));
        }

        /// <summary>Stops watching the monitor. Idempotent.</summary>
        public void StopWatching()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 1) return;
            link.StopWatching(OnMonitor);
            giveUp?.Dispose();
        }
    }
}

/// <summary>
/// One of our calls, connected: its session on the link, whether the
/// peer's call crossed it, and how to close it.
/// </summary>
public sealed class AgwDial : IAsyncDisposable
{
    private readonly AgwLinkSession session;
    private readonly AgwLink.PendingDial dial;
    private readonly ILogger logger;

    internal AgwDial(AgwLinkSession session, AgwLink.PendingDial dial, ILogger logger)
    {
        this.session = session;
        this.dial = dial;
        this.logger = logger;
    }

    public MultiplexedAgwSessionStream Stream => session.Stream;

    /// <summary>Completes if the peer's own SABM to us showed on the
    /// monitor, before or after our call connected.</summary>
    public Task CrossedCall => dial.CrossedCall;

    /// <summary>The node has told us the link is gone (a 'd' came, or
    /// the connection to the node closed).</summary>
    public bool RemoteDisconnected => session.Stream.RemoteClosed.IsCancellationRequested;

    /// <summary>Close without a 'd': the link is a newer session's now.</summary>
    public async ValueTask AbandonAsync()
    {
        dial.StopWatching();
        session.Retire();
        await session.Stream.DisposeAsync();
    }

    /// <summary>
    /// Out of the table, and a 'd' so BPQ tears down the AX.25 link at
    /// once rather than at its idle timeout; without it a follow-up
    /// connect from the same callsign pair within a few minutes collides
    /// with the stale half-up link. Not once the node has told us the
    /// link is gone, though: BPQ applies a 'd' to whichever session it
    /// has for the callsign pair, and a newer one could only be the
    /// peer's next call.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        dial.StopWatching();
        if (RemoteDisconnected)
        {
            logger.LogDebug("AGW: the link to {0} has already gone; no 'd' to send", dial.Remote);
        }
        session.Remove();
        await session.Stream.DisposeAsync();
    }
}

/// <summary>
/// One connected session in an <see cref="AgwLink"/>'s table, ours or
/// the peer's call. Its <see cref="Stream"/> reads what the reader routes
/// to it and writes 'D' frames on the link; disposing the stream sends
/// the 'd', unless the far end closed it or <see cref="Retire"/> did.
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

    internal AgwLinkSession(AgwLink link, byte port, string local, string remote, bool outbound, bool nodeLocal = false)
    {
        this.link = link;
        Port = port;
        Local = local;
        Remote = remote;
        Outbound = outbound;
        Key = AgwSessionKey.For(local, remote, port);
        // A write that got past the check goes out whole, though, not cut
        // off by the handler's token part way through its frames: half a
        // message would leave the peer's new session reading our next
        // bytes as payload or commands. Nor does one go out once another
        // session has taken this one's key on the link (rule 3), and none
        // at all once the link has closed.
        Stream = new MultiplexedAgwSessionStream(
            writeOutgoing: async (data, _) =>
            {
                if (silenced.IsCancellationRequested || !link.MaySend(this)) return;
                await link.WriteDataAsync(port, local, remote, data, nodeLocal);
            },
            sendRemoteDisconnect: async c =>
            {
                if (silenced.IsCancellationRequested || !link.MaySend(this)) return;
                await link.SendDisconnectAsync(port, local, remote, c);
            });
    }

    public byte Port { get; }

    /// <summary>Our callsign, as the node gave it on the connect.</summary>
    public string Local { get; }

    /// <summary>The peer's callsign, as the node gave it on the connect.</summary>
    public string Remote { get; }

    /// <summary>True for our own call, false for the peer's.</summary>
    public bool Outbound { get; }

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

/// <summary>The node's answer to a 'G' (its port count and names, as it
/// sent them) and when it came, on the link's clock.</summary>
public sealed record AgwPortList(byte[] Payload, DateTimeOffset ReceivedAt);
