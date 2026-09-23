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
/// that translates a single semantic <see cref="BackhaulMessage"/>
/// into the multi-step session exchange.
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
    public Dappsv1SessionBackhaul(
        IDappsOutboundTransport transport,
        ILoggerFactory loggerFactory,
        IBackhaulInbox? opportunisticInbox,
        Func<bool>? opportunisticEnabled,
        IRouteGossipPort? routeGossip = null,
        Func<Stream, string, CancellationToken, Task>? serveOnGlare = null)
    {
        this.transport = transport;
        this.loggerFactory = loggerFactory;
        this.opportunisticInbox = opportunisticInbox;
        this.opportunisticEnabled = opportunisticEnabled;
        this.routeGossip = routeGossip;
        this.serveOnGlare = serveOnGlare;
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

    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        var bearerPort = route.BearerPort ?? 0;

        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: route.Callsign,
                bearerPort: bearerPort,
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
                    return BackhaulSendResult.Fail($"connect-script failed for {route.Callsign}: {ex.Message}");
                }
            }
            else
            {
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
                        return BackhaulSendResult.Defer(
                            $"crossed connect with {route.Callsign}: served its session instead, {message.Id} stays queued");
                    case DappsProtocolClient.PromptOutcome.Silent:
                        return BackhaulSendResult.Fail(
                            $"no data from {route.Callsign} within {DappsProtocolClient.InactivityTimeout.TotalSeconds:F0}s of connecting");
                    case DappsProtocolClient.PromptOutcome.NotSeen:
                        return BackhaulSendResult.Fail($"no DAPPSv1> prompt from {route.Callsign}");
                }
            }

            if (!await protocol.OfferMessageAsync(
                    message.Id,
                    message.Salt,
                    DappsMessage.MessageFormat.Plain,
                    message.Destination,
                    message.Payload.Length,
                    ct,
                    ttl: message.Ttl,
                    originator: message.Originator,
                    masterId: message.MasterId,
                    fragmentIndex: message.FragmentIndex,
                    fragmentTotal: message.FragmentTotal,
                    streamId: message.StreamId,
                    streamSeq: message.StreamSeq,
                    streamGapTimeoutSeconds: message.StreamGapTimeoutSeconds))
            {
                return BackhaulSendResult.Fail($"offer rejected for {message.Id}");
            }

            if (!await protocol.SendMessageAsync(message.Id, message.Payload, ct))
            {
                return BackhaulSendResult.Fail($"payload rejected for {message.Id}");
            }

            // Route gossip: piggyback a `routes` pull when the
            // staleness gate allows. Same shape as opportunistic poll -
            // the session is already open, the ack just landed, the
            // exchange is small. Failures don't flip the SendResult.
            if (routeGossip is not null)
            {
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

            // Opportunistic poll. The session is open, the ack just
            // landed; if the operator's enabled the feature and we
            // have a place to deliver inbound, send `rev` and drain
            // anything the remote has queued for us. Failures here
            // don't flip the SendResult to fail - the push was the
            // actual ask, the drain is a bonus.
            if (opportunisticInbox is not null && (opportunisticEnabled?.Invoke() ?? false))
            {
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
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Opportunistic poll of {0} failed (push already succeeded)", route.Callsign);
                }
            }

            return BackhaulSendResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backhaul send failed for {0} to {1}", message.Id, route.Callsign);
            return BackhaulSendResult.Fail(ex.Message);
        }
    }
}
