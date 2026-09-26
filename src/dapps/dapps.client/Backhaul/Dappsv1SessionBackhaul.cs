using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul;

/// <summary>
/// Backhaul implementation that uses a stream-shaped bearer (today AGW
/// via <see cref="IDappsOutboundTransport"/>) and speaks the DAPPSv1
/// `prompt` / `ihave` / `send` / `data` / `ack` session protocol.
///
/// Plan A0.2: this is "the BPQ/AGW path" treated as one backend rather
/// than the architectural center. The session protocol logic stays in
/// <see cref="DappsProtocolClient"/>; this class is the thin adapter
/// that translates semantic <see cref="BackhaulMessage"/>s into the
/// multi-step session exchange, several to a session when the forwarder
/// has several queued for the same neighbour.
///
/// Crossed connects (#178): when two neighbours dial each other within
/// one round trip, both links come up and each node's BPQ attaches its
/// link to its own outbound session. Neither side gets an inbound
/// connect, so neither sends the prompt, and both callers would sit
/// silent until the inactivity timeout, fail, and quite possibly
/// collide again. The side with the lower callsign breaks the tie:
/// after <see cref="GlareSilenceBudget"/> of nothing at all from the
/// peer it sends the prompt itself and serves the peer's session on
/// the link it already has (<c>serveOnGlare</c>, wired to the same
/// handler the inbound bearers use). The higher side keeps waiting as
/// it always did, sees that late prompt and carries on as the caller.
/// Only one side ever switches, so this also works when the peer runs
/// a version without it, provided the newer node is the lower callsign.
/// </summary>
public sealed class Dappsv1SessionBackhaul : IDappsBackhaul
{
    private readonly IDappsOutboundTransport transport;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly IBackhaulInbox? opportunisticInbox;
    private readonly Func<bool>? opportunisticEnabled;
    private readonly IRouteGossipPort? routeGossip;
    private readonly Func<Stream, string, CancellationToken, Task>? serveOnGlare;
    private readonly Func<string, CancellationToken, Task<bool>>? compressTo;

    /// <summary>
    /// How long a caller with the lower callsign waits for the first byte
    /// from the peer before treating the silence as a crossed connect.
    /// Long enough that a slow prompt (busy channel, loaded node) never
    /// trips it; well inside the 3-minute budget the other side waits,
    /// so the late prompt is still received there.
    /// </summary>
    public TimeSpan GlareSilenceBudget { get; init; } = TimeSpan.FromSeconds(60);

    public Dappsv1SessionBackhaul(IDappsOutboundTransport transport, ILoggerFactory loggerFactory)
        : this(transport, loggerFactory, opportunisticInbox: null, opportunisticEnabled: null, routeGossip: null)
    {
    }

    /// <param name="serveOnGlare">Runs the receiver side of a session over
    /// an already-connected stream to the named peer, prompt included:
    /// what a crossed connect is resolved with. Null leaves the caller
    /// waiting for the prompt as before.</param>
    /// <param name="compressTo">Whether payloads to the named peer may be
    /// sent compressed (the operator's setting for that neighbour). Asked
    /// once per session. Null sends everything plain.</param>
    public Dappsv1SessionBackhaul(
        IDappsOutboundTransport transport,
        ILoggerFactory loggerFactory,
        IBackhaulInbox? opportunisticInbox,
        Func<bool>? opportunisticEnabled,
        IRouteGossipPort? routeGossip = null,
        Func<Stream, string, CancellationToken, Task>? serveOnGlare = null,
        Func<string, CancellationToken, Task<bool>>? compressTo = null)
    {
        this.transport = transport;
        this.loggerFactory = loggerFactory;
        this.opportunisticInbox = opportunisticInbox;
        this.opportunisticEnabled = opportunisticEnabled;
        this.routeGossip = routeGossip;
        this.serveOnGlare = serveOnGlare;
        this.compressTo = compressTo;
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
    /// One message on its own session: connect, push it, then the
    /// usual route pull and <c>rev</c> before hanging up. The forwarder
    /// uses <see cref="SendBatchAsync"/> instead, so that everything
    /// queued for one neighbour shares a session.
    /// </summary>
    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        var single = new SingleMessageBatch(message);
        await SendBatchAsync(route, localCallsign, single, ct);
        return single.Result!;
    }

    /// <summary>
    /// Carry every message <paramref name="batch"/> has for this peer on
    /// one session: connect once, push them one after another, pull
    /// routes, drain the peer's mail for us with <c>rev</c>, and before
    /// hanging up ask the batch again, so anything queued for the peer
    /// in the meantime goes on this session too instead of a fresh one
    /// a few seconds later. The <c>rev</c> is always the last exchange:
    /// while this session is open the peer holds back what it has for
    /// us (#178), so its mail only reaches us that way.
    ///
    /// Stops at the first message the peer doesn't accept. Messages
    /// already acked stay acked; the rest stay queued for the next run.
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
        IDappsConnection? connection = null;
        try
        {
            (connection, var protocol, var failure) = await OpenAsync(first, route, localCallsign, ct);
            if (protocol is null)
            {
                await batch.CompleteAsync(first, failure!, sw.Elapsed, ct);
                return;
            }
            var compress = await ShouldCompressAsync(route, ct);
            await RunSessionAsync(protocol, first, route, compress, batch, sw, ct);
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Connect and get to the point where the peer takes commands.
    /// Returns the protocol client on success, or the result that the
    /// first message gets when the session never got that far. The
    /// connection comes back either way once it exists, for the caller
    /// to dispose.
    /// </summary>
    private async Task<(IDappsConnection? Connection, DappsProtocolClient? Protocol, BackhaulSendResult? Failure)> OpenAsync(
        BackhaulMessage first,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        IDappsConnection? connection = null;
        try
        {
            connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: route.Callsign,
                bearerPort: route.BearerPort ?? 0,
                stoppingToken: ct);

            var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

            // Connect-script: when the route carries one, the script
            // drives a chain of node-to-node connects through
            // intermediate non-DAPPS packet nodes and consumes the
            // final DAPPSv1> prompt itself, so we skip
            // ReadInitialPromptAsync. Direct connections (no script)
            // take the regular path where the protocol client reads
            // the prompt.
            if (route.ConnectScript is { } script)
            {
                try
                {
                    await ConnectScriptRunner.RunAsync(connection.Stream, script, logger, ct);
                }
                catch (Exception ex) when (ex is ConnectScriptException or EndOfStreamException)
                {
                    return (connection, null, BackhaulSendResult.Fail($"connect-script failed for {route.Callsign}: {ex.Message}"));
                }
                return (connection, protocol, null);
            }

            // #178 crossed connect: see the class doc. Only the lower
            // callsign switches roles, and only on a direct link - a
            // connect-script chain has intermediate nodes talking.
            var weBreakTies = serveOnGlare is not null
                && StringComparer.OrdinalIgnoreCase.Compare(localCallsign, route.Callsign) < 0;
            var outcome = await protocol.ReadInitialPromptAsync(
                ct, silenceBudget: weBreakTies ? GlareSilenceBudget : DappsProtocolClient.InactivityTimeout);
            switch (outcome)
            {
                case DappsProtocolClient.PromptOutcome.Silent when weBreakTies:
                    logger.LogInformation(
                        "Nothing from {0} for {1:F0}s after connecting: assuming a crossed connect, sending the prompt and serving its session instead",
                        route.Callsign, GlareSilenceBudget.TotalSeconds);
                    await serveOnGlare!(connection.Stream, route.Callsign, ct);
                    return (connection, null, BackhaulSendResult.Defer(
                        $"crossed connect with {route.Callsign}: served its session instead, {first.Id} stays queued"));
                case DappsProtocolClient.PromptOutcome.Silent:
                    return (connection, null, BackhaulSendResult.Fail(
                        $"no data from {route.Callsign} within {DappsProtocolClient.InactivityTimeout.TotalSeconds:F0}s of connecting"));
                case DappsProtocolClient.PromptOutcome.NotSeen:
                    return (connection, null, BackhaulSendResult.Fail($"no DAPPSv1> prompt from {route.Callsign}"));
            }
            return (connection, protocol, null);
        }
        catch (PeerSessionBusyException ex)
        {
            // #185: the transport's own last-moment check caught what
            // OutboundMessageManager's earlier check missed - a session
            // with this peer opened in the gap between that check and
            // this dial actually reaching BPQ. Same non-outcome as any
            // other #178 defer: nothing failed, so no cooldown and
            // nothing for the route to learn from.
            logger.LogInformation(
                "Deferring {0}: {1} already has an {2} session open, dialling now would reset it",
                first.Id, ex.PeerCallsign, ex.OpenDirection);
            return (connection, null, BackhaulSendResult.Defer(
                $"{ex.PeerCallsign} already has an {ex.OpenDirection} session open"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backhaul send failed for {0} to {1}", first.Id, route.Callsign);
            return (connection, null, BackhaulSendResult.Fail(ex.Message));
        }
    }

    private async Task<bool> ShouldCompressAsync(BackhaulRoute route, CancellationToken ct)
    {
        if (compressTo is null) return false;
        try
        {
            return await compressTo(route.Callsign, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the compression setting for {0}; sending plain", route.Callsign);
            return false;
        }
    }

    private async Task RunSessionAsync(
        DappsProtocolClient protocol,
        BackhaulMessage first,
        BackhaulRoute route,
        bool compress,
        IBackhaulBatch batch,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var pushed = 0;
        var pulledRoutes = false;
        BackhaulMessage? next = first;
        while (true)
        {
            // Everything the batch has for this peer, including what
            // was queued while the earlier ones were going out.
            for (; next is not null; next = await batch.NextAsync(ct))
            {
                // The first message's time includes the connect; each
                // later one is timed on its own exchange.
                if (!ReferenceEquals(next, first)) sw.Restart();
                var result = await PushAsync(protocol, next, route, compress, laterOnSession: !ReferenceEquals(next, first), ct);
                await batch.CompleteAsync(next, result, sw.Elapsed, ct);
                if (!result.Accepted)
                {
                    LogSessionEnd(route, pushed, stoppedEarly: true);
                    return;
                }
                pushed++;
            }

            if (!pulledRoutes)
            {
                pulledRoutes = true;
                await PullRoutesAsync(protocol, route, ct);
            }

            // Only a drain that ended cleanly leaves the session fit to
            // carry more.
            if (!await PollAsync(protocol, route, ct)) break;

            // Queued for this peer while we were draining: send it now,
            // then rev again, rather than hang up and dial straight back.
            next = await batch.NextAsync(ct);
            if (next is null) break;
        }
        LogSessionEnd(route, pushed, stoppedEarly: false);
    }

    private void LogSessionEnd(BackhaulRoute route, int pushed, bool stoppedEarly)
    {
        if (stoppedEarly)
        {
            logger.LogInformation("Session with {0}: {1} message(s) accepted, then one was not; the rest stay queued", route.Callsign, pushed);
        }
        else if (pushed > 1)
        {
            logger.LogInformation("Session with {0}: {1} messages sent on one connection", route.Callsign, pushed);
        }
    }

    /// <summary>
    /// Offer one message and send its payload, compressed when allowed
    /// and worth it. A peer that says no, or a link that fails part way,
    /// is a failed send for this message; the session stops there
    /// because the exchange can't be trusted to be in step any more.
    ///
    /// Except when the session has already carried an exchange
    /// (<paramref name="laterOnSession"/>) and the link simply ends: the
    /// peer hanging up after an earlier exchange, or the link going, is
    /// the end of the session rather than a fault of the neighbour. The
    /// message is deferred, with no cooldown, and goes on the next
    /// session; if the neighbour really is down, that dial finds out.
    /// </summary>
    private async Task<BackhaulSendResult> PushAsync(
        DappsProtocolClient protocol, BackhaulMessage message, BackhaulRoute route, bool compress, bool laterOnSession, CancellationToken ct)
    {
        try
        {
            return await protocol.PushAsync(message, compress, ct) switch
            {
                DappsProtocolClient.PushOutcome.Accepted => BackhaulSendResult.Ok(),
                DappsProtocolClient.PushOutcome.PeerClosed when laterOnSession => SessionEnded(message, route),
                DappsProtocolClient.PushOutcome.OfferRefused or DappsProtocolClient.PushOutcome.PeerClosed =>
                    BackhaulSendResult.Fail($"offer rejected for {message.Id}"),
                _ => BackhaulSendResult.Fail($"payload rejected for {message.Id}"),
            };
        }
        catch (Exception ex) when (laterOnSession && ex is IOException or ObjectDisposedException)
        {
            return SessionEnded(message, route);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backhaul send failed for {0} to {1}", message.Id, route.Callsign);
            return BackhaulSendResult.Fail(ex.Message);
        }
    }

    private BackhaulSendResult SessionEnded(BackhaulMessage message, BackhaulRoute route)
    {
        logger.LogInformation("Session with {0} ended before {1} went; it stays queued for the next one", route.Callsign, message.Id);
        return BackhaulSendResult.Defer($"session with {route.Callsign} ended; {message.Id} stays queued");
    }

    /// <summary>
    /// Route gossip: piggyback a <c>routes</c> pull when the staleness
    /// gate allows. The session is already open and the exchange is
    /// small. Failures don't count against the pushes.
    /// </summary>
    private async Task PullRoutesAsync(DappsProtocolClient protocol, BackhaulRoute route, CancellationToken ct)
    {
        if (routeGossip is null) return;
        try
        {
            if (await routeGossip.ShouldPullAsync(route.Callsign, ct))
            {
                var routes = await protocol.RequestRoutesAsync(ct);
                await routeGossip.ImportAsync(route.Callsign, routes, ct);
                await routeGossip.RecordPulledAsync(route.Callsign, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Route gossip pull from {0} failed (push already succeeded)", route.Callsign);
        }
    }

    /// <summary>
    /// Opportunistic poll: when the operator has enabled it and we have
    /// somewhere to deliver inbound, send <c>rev</c> and drain anything
    /// the peer has queued for us. Returns true only when the drain
    /// ended cleanly on the peer's prompt; false when no poll ran, or it
    /// ended any other way (the peer hung up, the link went, an exchange
    /// failed) and the session shouldn't be used any further. Failures
    /// don't count against the pushes.
    /// </summary>
    private async Task<bool> PollAsync(DappsProtocolClient protocol, BackhaulRoute route, CancellationToken ct)
    {
        if (opportunisticInbox is null || !(opportunisticEnabled?.Invoke() ?? false)) return false;
        try
        {
            await foreach (var polled in protocol.PollAsync(requestedIds: null, ct))
            {
                var inbound = new BackhaulMessage(
                    Id: polled.Id,
                    Destination: polled.Destination,
                    Salt: polled.Salt,
                    Ttl: polled.Ttl,
                    Payload: polled.Payload,
                    Originator: polled.Originator,
                    MasterId: polled.MasterId,
                    FragmentIndex: polled.FragmentIndex,
                    FragmentTotal: polled.FragmentTotal,
                    StreamId: polled.StreamId,
                    StreamSeq: polled.StreamSeq,
                    StreamGapTimeoutSeconds: polled.StreamGapTimeoutSeconds);
                await opportunisticInbox.DeliverAsync(inbound, route.Callsign, ct);
            }
            return protocol.LastPollDrained;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Opportunistic poll of {0} failed (push already succeeded)", route.Callsign);
            return false;
        }
    }

    /// <summary>A batch of exactly one message, for <see cref="SendAsync"/>.</summary>
    private sealed class SingleMessageBatch(BackhaulMessage message) : IBackhaulBatch
    {
        private bool handedOut;

        /// <summary>Set once the message has been completed, which
        /// <see cref="SendBatchAsync"/> always does for a message it
        /// was handed.</summary>
        public BackhaulSendResult? Result { get; private set; }

        public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            if (handedOut) return ValueTask.FromResult<BackhaulMessage?>(null);
            handedOut = true;
            return ValueTask.FromResult<BackhaulMessage?>(message);
        }

        public ValueTask CompleteAsync(BackhaulMessage completed, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
        {
            Result = result;
            return ValueTask.CompletedTask;
        }
    }
}
