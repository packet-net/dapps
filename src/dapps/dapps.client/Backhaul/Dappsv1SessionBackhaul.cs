using System.Collections.Concurrent;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul;

/// <summary>
/// Backhaul implementation that uses a stream-shaped bearer (AGW or RHP
/// via <see cref="IDappsOutboundTransport"/>) and speaks DAPPSv1: it
/// dials the neighbour and runs an <see cref="ExchangeSession"/> on the
/// link, the same session the answering node runs.
///
/// Plan A0.2: this is "the BPQ/AGW path" treated as one backend rather
/// than the architectural center. The session protocol lives in
/// <see cref="ExchangeSession"/>; this class dials, hands the session
/// the batch that made it dial, and keeps the session open for the
/// forwarder to hand it more (<see cref="TryHandToOpenSession"/>) until
/// the link has been quiet for the agreed hold.
///
/// Crossed calls: when two neighbours dial each other at once, each
/// node's BPQ can attach its link to its own outbound session, so
/// neither hears a prompt. The session copes by itself: each caller
/// sends its <c>exchange</c> as soon as the transport says the calls
/// crossed, or after a few seconds of silence, and the two sessions
/// carry on as if one had answered.
///
/// Moved links: a newer session with the peer at this node takes the
/// link (<see cref="IDappsConnection.Retired"/>); ours stops and defers
/// what it had.
/// </summary>
public sealed class Dappsv1SessionBackhaul : IDappsBackhaul
{
    private readonly IDappsOutboundTransport transport;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly IBackhaulInbox? inbox;
    private readonly IRouteGossipPort? routeGossip;
    private readonly Func<string, CancellationToken, Task<ExchangeSettings>>? settingsFor;
    private readonly Action? sessionOpened;

    /// <summary>Sessions we dialled that are established, by peer callsign.</summary>
    private readonly ConcurrentDictionary<string, OpenSession> open = new(StringComparer.OrdinalIgnoreCase);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Longest a session stays up, however busy (<see cref="ExchangeSession.MaxLength"/>).</summary>
    public TimeSpan MaxHold { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a caller waits for the prompt before sending its
    /// exchange anyway (<see cref="ExchangeSession.PromptWait"/>).</summary>
    public TimeSpan PromptWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The shortest quiet spell before hanging up
    /// (<see cref="ExchangeSession.MinQuiet"/>).</summary>
    public TimeSpan MinQuiet { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long <see cref="SendBatchAsync"/> keeps the forwarder waiting
    /// on a session with nothing of its batch answered, before it lets the
    /// forwarder get on with other neighbours. Answers still to come are
    /// recorded when they arrive, and what the batch hadn't handed out yet
    /// goes on the session by hand-off in a later run.
    /// </summary>
    public TimeSpan BatchPatience { get; init; } = TimeSpan.FromSeconds(60);

    public Dappsv1SessionBackhaul(IDappsOutboundTransport transport, ILoggerFactory loggerFactory)
        : this(transport, loggerFactory, inbox: null)
    {
    }

    /// <param name="inbox">Where the messages the peer sends us on a
    /// session go. Null refuses them.</param>
    /// <param name="routeGossip">Pulls the peer's routes before the
    /// exchange when the staleness gate allows.</param>
    /// <param name="settingsFor">Our settings for a session with the
    /// named peer: the hold, whether to compress, the largest message
    /// we take. Asked once per session. Null: no hold, plain, no limit.</param>
    /// <param name="sessionOpened">Called when a session becomes
    /// established, so the forwarder can hand it anything else queued
    /// for that peer.</param>
    public Dappsv1SessionBackhaul(
        IDappsOutboundTransport transport,
        ILoggerFactory loggerFactory,
        IBackhaulInbox? inbox,
        IRouteGossipPort? routeGossip = null,
        Func<string, CancellationToken, Task<ExchangeSettings>>? settingsFor = null,
        Action? sessionOpened = null)
    {
        this.transport = transport;
        this.loggerFactory = loggerFactory;
        this.inbox = inbox;
        this.routeGossip = routeGossip;
        this.settingsFor = settingsFor;
        this.sessionOpened = sessionOpened;
        logger = loggerFactory.CreateLogger<Dappsv1SessionBackhaul>();
    }

    /// <summary>
    /// AGW handles any route that does not specify a higher-priority
    /// bearer like UDP or MeshCore. Effectively: this is the fallback bearer
    /// when only callsign + bearer port are known.
    ///
    /// The MeshCore exclusion matters for passive discovery (#27): a peer
    /// heard only over MeshCore produces a route with a MeshCoreChannel and a
    /// null UdpEndpoint. If the MeshCore bearer is currently down (disabled,
    /// serial link failed, or not yet started), it declines the route - and
    /// without this guard AGW would claim it and attempt a doomed connected-mode
    /// session (or spurious RF on a gateway node) to a callsign only ever heard
    /// over LoRa. Excluding MeshCore routes leaves it Unreachable so the message
    /// waits for MeshCore to return rather than mis-routing over the wrong bearer.
    /// </summary>
    public bool CanHandle(BackhaulRoute route) =>
        route.UdpEndpoint is null && route.MeshCoreChannel is null;

    /// <summary>
    /// One message on its own session. The forwarder uses
    /// <see cref="SendBatchAsync"/> instead, so that everything queued
    /// for one neighbour shares a session.
    /// </summary>
    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        var single = new SingleMessageBatch(message);
        await SendBatchAsync(route, localCallsign, single, ct);
        // Answered by now, unless the batch ran out of patience: then it
        // comes by the answer timeout at the latest.
        return await single.Result;
    }

    /// <summary>
    /// Dial the route's peer and carry <paramref name="batch"/> on an
    /// exchange session. Returns once every message the batch had has
    /// been answered, the session ended, or nothing has been answered for
    /// <see cref="BatchPatience"/>; the session itself stays up in the
    /// background, taking whatever else the forwarder hands it and
    /// whatever the peer sends, until the link has been quiet for the
    /// agreed hold.
    ///
    /// A session that never gets as far as the exchange (no prompt, not a
    /// DAPPS node, the link failing) fails the first message; nothing
    /// else was handed out.
    /// </summary>
    public async Task SendBatchAsync(
        BackhaulRoute route,
        string localCallsign,
        IBackhaulBatch batch,
        CancellationToken ct)
    {
        // Nothing queued, nothing to dial for.
        var first = await batch.NextAsync(ct);
        if (first is null) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IDappsConnection connection;
        try
        {
            connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: route.Callsign,
                bearerPort: route.BearerPort ?? 0,
                stoppingToken: ct);
        }
        catch (PeerSessionBusyException ex)
        {
            // #185: the transport's own last-moment check caught a
            // session with this peer opened in the gap since the
            // forwarder's earlier check. Nothing failed, so no cooldown
            // and nothing for the route to learn from.
            logger.LogInformation(
                "Deferring {0}: {1} already has an {2} session open, dialling now would reset it",
                first.Id, ex.PeerCallsign, ex.OpenDirection);
            await batch.CompleteAsync(first, BackhaulSendResult.Defer(
                $"{ex.PeerCallsign} already has an {ex.OpenDirection} session open"), sw.Elapsed, ct);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Backhaul send failed for {0} to {1}", first.Id, route.Callsign);
            await batch.CompleteAsync(first, BackhaulSendResult.Fail(ex.Message), sw.Elapsed, ct);
            return;
        }

        // Everything reads through the pump, so the session can wait on
        // the link without a read in progress.
        var stream = new PumpedReadStream(connection.Stream);
        ExchangeSession session;
        try
        {
            // Connect-script: the script drives a chain of connects through
            // intermediate non-DAPPS packet nodes and reads the final
            // DAPPSv1> prompt itself.
            if (route.ConnectScript is { } script)
            {
                await ConnectScriptRunner.RunAsync(stream, script, logger, ct);
            }
            var settings = await SettingsForAsync(route.Callsign, ct);
            session = new ExchangeSession(stream, route.Callsign, dialled: true, settings, inbox, loggerFactory)
            {
                TimeProvider = TimeProvider,
                PromptWait = PromptWait,
                MinQuiet = MinQuiet,
                MaxLength = MaxHold,
                PromptConsumed = route.ConnectScript is not null,
                CrossedCall = connection.CrossedCall,
                RouteGossip = routeGossip,
                Opened = s => OnOpened(route, s),
            };
        }
        catch (Exception ex) when (ex is ConnectScriptException or EndOfStreamException)
        {
            await CloseAsync(connection, stream);
            await batch.CompleteAsync(first, BackhaulSendResult.Fail($"connect-script failed for {route.Callsign}: {ex.Message}"), sw.Elapsed, ct);
            return;
        }
        catch
        {
            await CloseAsync(connection, stream);
            throw;
        }

        var tracked = new TrackedBatch(batch, first, TimeProvider);
        session.TryTake(tracked);
        var run = RunAsync(session, route, connection, stream, ct);
        while (!tracked.Done.IsCompleted && !run.IsCompleted)
        {
            if (!tracked.FirstHandedOut)
            {
                // The handshake has its own limits.
                await Task.WhenAny(tracked.Started, tracked.Done, run);
                continue;
            }
            var left = tracked.LastProgress + BatchPatience - TimeProvider.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                await tracked.ReleaseAsync();
                logger.LogInformation(
                    "Nothing answered by {0} for {1:F0}s; moving on, its answers are recorded as they come",
                    route.Callsign, BatchPatience.TotalSeconds);
                break;
            }
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(tracked.Done, run, Task.Delay(left, TimeProvider, waiting.Token));
            await waiting.CancelAsync();
        }

        if (!tracked.FirstHandedOut)
        {
            // The session ended before our first message went. If it never
            // got going, that's the neighbour failing; otherwise it just
            // ended, or gave way to a newer session with the peer, and the
            // message waits for the next one.
            await batch.CompleteAsync(first, session.Established || connection.Retired.IsCancellationRequested
                ? BackhaulSendResult.Defer($"session with {route.Callsign} ended; {first.Id} stays queued")
                : BackhaulSendResult.Fail(session.Failure ?? $"no session with {route.Callsign}", session.RedialAfter), sw.Elapsed, ct);
        }
    }

    /// <summary>
    /// An established session we dialled to the route's peer takes the
    /// batch, if there is one on the same bearer port.
    /// </summary>
    public bool TryHandToOpenSession(BackhaulRoute route, IBackhaulBatch batch) =>
        open.TryGetValue(route.Callsign, out var entry)
        && entry.Route.BearerPort == route.BearerPort
        && entry.Session.TryTake(batch);

    /// <summary>Peers this bearer has an established session with.</summary>
    public IReadOnlyCollection<string> OpenPeers => [.. open.Keys];

    private void OnOpened(BackhaulRoute route, ExchangeSession session)
    {
        if (!open.TryAdd(route.Callsign, new OpenSession(route, session)))
        {
            logger.LogInformation("Already have a session open to {0}; the forwarder keeps using that one", route.Callsign);
            return;
        }
        sessionOpened?.Invoke();
    }

    /// <summary>
    /// Runs the session until it ends, or until a newer session with the
    /// same peer connects at this node and the link goes to that one
    /// (<see cref="IDappsConnection.Retired"/>): then it stops at once,
    /// what it had is deferred, and the connection is dropped without a
    /// disconnect.
    /// </summary>
    private async Task RunAsync(ExchangeSession session, BackhaulRoute route, IDappsConnection connection, PumpedReadStream stream, CancellationToken ct)
    {
        try
        {
            using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Retired);
            await session.RunAsync(running.Token);
        }
        finally
        {
            foreach (var kv in open.Where(kv => ReferenceEquals(kv.Value.Session, session)))
            {
                open.TryRemove(kv);
            }
            var retired = connection.Retired.IsCancellationRequested;
            await CloseAsync(connection, stream, abandon: retired);
            if (retired)
            {
                logger.LogInformation("Session with {0} retired: a newer session with it has the link", route.Callsign);
            }
            else if (session.Established)
            {
                logger.LogInformation("Session with {0} closed", route.Callsign);
            }
        }
    }

    private static async Task CloseAsync(IDappsConnection connection, PumpedReadStream stream, bool abandon = false)
    {
        try
        {
            if (abandon) await connection.AbandonAsync();
            else await connection.DisposeAsync();
        }
        catch (Exception)
        {
            // Already gone.
        }
        stream.Dispose();
    }

    private async Task<ExchangeSettings> SettingsForAsync(string peer, CancellationToken ct)
    {
        if (settingsFor is null) return new ExchangeSettings();
        try
        {
            return await settingsFor(peer, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the settings for {0}; no hold, sending plain", peer);
            return new ExchangeSettings();
        }
    }

    private sealed record OpenSession(BackhaulRoute Route, ExchangeSession Session);

    /// <summary>
    /// The batch that made us dial, with its first message already taken
    /// out (to know there was something to dial for). Done once the batch
    /// has run dry and everything it handed out has been answered: from
    /// then on the forwarder's run can go on, and the batch is never
    /// asked again. Released early when the forwarder stops waiting: it
    /// then hands out nothing more (what's left stays queued for a later
    /// run), and the batch is never touched from the session again except
    /// to record answers to what it already handed out.
    /// </summary>
    private sealed class TrackedBatch(IBackhaulBatch inner, BackhaulMessage first, TimeProvider clock) : IBackhaulBatch
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int outstanding;
        private bool ranDry;
        private bool released;
        private volatile bool firstHandedOut;
        private long lastProgressTicks = clock.GetUtcNow().UtcTicks;

        /// <summary>
        /// The first message has gone to the session. Set, and
        /// <see cref="Started"/> completed, only once that hand-out is in
        /// <see cref="LastProgress"/>: the wait for answers measures its
        /// patience from there, and reading it any earlier once made it
        /// look 60 s gone and give up at once.
        /// </summary>
        public bool FirstHandedOut => firstHandedOut;
        public Task Done => done.Task;
        public Task Started => started.Task;

        /// <summary>When a message last went out or was answered.</summary>
        public DateTimeOffset LastProgress => new(Interlocked.Read(ref lastProgressTicks), TimeSpan.Zero);

        public async ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (released) return null;
                var handingOutFirst = !firstHandedOut;
                var next = handingOutFirst ? first : await inner.NextAsync(ct);
                if (next is null)
                {
                    ranDry = true;
                    if (outstanding == 0) done.TrySetResult();
                }
                else
                {
                    outstanding++;
                    Progress();
                }
                if (handingOutFirst)
                {
                    firstHandedOut = true;
                    started.TrySetResult();
                }
                return next;
            }
            finally
            {
                gate.Release();
            }
        }

        public async ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
        {
            try
            {
                await inner.CompleteAsync(message, result, elapsed, ct);
            }
            finally
            {
                Progress();
                if (--outstanding == 0 && ranDry) done.TrySetResult();
            }
        }

        /// <summary>Hand out nothing more. Waits for a hand-out in progress to finish.</summary>
        public async Task ReleaseAsync()
        {
            await gate.WaitAsync();
            released = true;
            gate.Release();
        }

        private void Progress() => Interlocked.Exchange(ref lastProgressTicks, clock.GetUtcNow().UtcTicks);
    }

    /// <summary>A batch of exactly one message, for <see cref="SendAsync"/>.</summary>
    private sealed class SingleMessageBatch(BackhaulMessage message) : IBackhaulBatch
    {
        private readonly TaskCompletionSource<BackhaulSendResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool handedOut;

        /// <summary>Completes once the message has been completed, which
        /// always happens to a message <see cref="SendBatchAsync"/> was
        /// handed: by an answer, by the session, or by the dial failing.</summary>
        public Task<BackhaulSendResult> Result => result.Task;

        public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            if (handedOut) return ValueTask.FromResult<BackhaulMessage?>(null);
            handedOut = true;
            return ValueTask.FromResult<BackhaulMessage?>(message);
        }

        public ValueTask CompleteAsync(BackhaulMessage completed, BackhaulSendResult outcome, TimeSpan elapsed, CancellationToken ct)
        {
            result.TrySetResult(outcome);
            return ValueTask.CompletedTask;
        }
    }
}
