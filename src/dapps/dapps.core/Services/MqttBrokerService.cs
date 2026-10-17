using dapps.core.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using System.Reflection;
using System.Text;

namespace dapps.core.Services;

/// <summary>
/// Embedded MQTT broker that fronts the DAPPS app interface.
///
/// Topic structure (from the spec / gist):
///   - `dapps/in/&lt;app&gt;`              - apps subscribe; DAPPS publishes
///   - `dapps/out/&lt;app&gt;/&lt;dest&gt;` - apps publish; DAPPS queues for forwarding
///   - `dapps/ack/&lt;app&gt;`             - apps publish msg-id to ack receipt
///
/// Durability: DAPPS owns the queue (in SQLite). The broker is just a
/// real-time delivery channel. Messages stay in the database until the app
/// explicitly acks; if no subscriber is connected, the publish is a no-op
/// and the message is replayed when a subscriber later connects.
///
/// Hosting: the broker itself is registered via MQTTnet.AspNetCore's
/// <c>AddHostedMqttServer</c> so a single <see cref="MqttServer"/>
/// instance can serve both TCP (port <see cref="SystemOptions.MqttPort"/>)
/// and the WebSocket endpoint mounted at <c>/mqtt</c>. This service is a
/// thin lifecycle wrapper around the shared instance: it attaches DAPPS-
/// specific event handlers in <see cref="StartAsync"/> and detaches them
/// in <see cref="StopAsync"/>, leaving the actual listener lifecycle to
/// the hosted broker.
/// </summary>
public sealed class MqttBrokerService(
    ILogger<MqttBrokerService> logger,
    IOptionsMonitor<SystemOptions> options,
    Database database,
    AppTokenStore tokens,
    MqttServer server) : IHostedService
{
    private const string OutTopicPrefix = "dapps/out/";
    private const string InTopicPrefix = "dapps/in/";
    private const string AckTopicPrefix = "dapps/ack/";
    private const string DappsTopicRoot = "dapps";

    /// <summary>One connected client: the app it authenticated as (null
    /// when it connected without valid credentials, which is only allowed
    /// while auth is off), the MQTT version it speaks, the remote endpoint
    /// of this particular connection (so a late disconnect of an older
    /// connection with the same ClientId can't remove this one), and the
    /// app's token generation seen before its token was checked.</summary>
    private sealed record ClientEntry(
        string? App, MqttProtocolVersion ProtocolVersion, string? Endpoint, long TokenGeneration);

    /// <summary>Every connected MQTT client, keyed on ClientId. When auth
    /// is required, the publish/subscribe interceptors check the app here
    /// to enforce topic-app scoping.</summary>
    private readonly Dictionary<string, ClientEntry> clientApps = new(StringComparer.Ordinal);
    private readonly object clientAppsLock = new();

    /// <summary>Bumped each time an app's token is issued, rotated or
    /// revoked (case-insensitive, as app names are). A CONNECT captures
    /// it before checking the token and re-checks it once the client is
    /// registered, so a revoke that lands while the token check is still
    /// running can't let the connection through. Guarded by
    /// <see cref="clientAppsLock"/>.</summary>
    private readonly Dictionary<string, long> tokenGenerations = new(StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;

        server.ValidatingConnectionAsync += OnValidatingConnection;
        server.InterceptingPublishAsync += OnInterceptingPublish;
        server.InterceptingSubscriptionAsync += OnInterceptingSubscription;
        server.ClientSubscribedTopicAsync += OnClientSubscribedTopic;
        server.ClientConnectedAsync += OnClientConnected;
        server.ClientDisconnectedAsync += OnClientDisconnected;
        tokens.TokenChanged += OnTokenChanged;

        logger.LogInformation(
            "MQTT broker: TCP :{port}, WebSocket /mqtt (auth required: {auth})",
            opts.MqttPort, opts.AuthRequired);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Detach handlers so a re-StartAsync (e.g. test scenarios) doesn't
        // double-subscribe. The broker itself stops via its own hosted
        // service - we don't own that lifecycle here.
        server.ValidatingConnectionAsync -= OnValidatingConnection;
        server.InterceptingPublishAsync -= OnInterceptingPublish;
        server.InterceptingSubscriptionAsync -= OnInterceptingSubscription;
        server.ClientSubscribedTopicAsync -= OnClientSubscribedTopic;
        server.ClientConnectedAsync -= OnClientConnected;
        server.ClientDisconnectedAsync -= OnClientDisconnected;
        tokens.TokenChanged -= OnTokenChanged;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disconnect every live MQTT client authenticated as
    /// <paramref name="app"/>. The token is only checked at CONNECT, so
    /// without this a revoked or rotated token would keep working on an
    /// already open connection. The app reconnects with its new token, or
    /// is refused if it has none. Returns how many clients were dropped.
    /// </summary>
    public async Task<int> DisconnectAppAsync(string app)
    {
        string[] ids;
        lock (clientAppsLock)
        {
            ids = clientApps
                .Where(kv => kv.Value.App is not null
                    && string.Equals(kv.Value.App, app, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToArray();
        }
        var dropped = 0;
        foreach (var id in ids)
        {
            try
            {
                await server.DisconnectClientAsync(id, MqttDisconnectReasonCode.NotAuthorized);
                dropped++;
                logger.LogInformation("MQTT: disconnected client {0} (app {1}) after its token changed", id, app);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT: could not disconnect client {0} (app {1})", id, app);
            }
        }
        return dropped;
    }

    private void OnTokenChanged(string app)
    {
        lock (clientAppsLock)
        {
            tokenGenerations[app] = tokenGenerations.GetValueOrDefault(app) + 1;
        }
        _ = DisconnectAppAsync(app);
    }

    private long TokenGeneration(string app)
    {
        lock (clientAppsLock)
        {
            return tokenGenerations.GetValueOrDefault(app);
        }
    }

    /// <summary>
    /// Publish an inbound (received-from-another-DAPPS) message to its
    /// local app's topic. Called by InboundConnectionHandler on receipt.
    /// If no subscriber is connected, the broker drops the publish - the
    /// message is in the DB and will be replayed on next subscribe.
    /// </summary>
    /// <summary>
    /// Plan C3 PR-B - publish a retained message on a fixed topic
    /// (heartbeat use case). Retained = true so a subscriber connecting
    /// later sees the latest snapshot immediately rather than waiting
    /// for the next interval. QoS 0 because the next snapshot is
    /// always &lt;interval&gt; seconds away - losing one in flight is
    /// no big deal.
    /// </summary>
    public async Task<bool> PublishRetainedAsync(string topic, byte[] payload, CancellationToken ct = default)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        try
        {
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT: retained publish to {0} failed", topic);
            return false;
        }
    }

    /// <summary>
    /// Non-retained publish on a fixed topic. Used by the transmission
    /// audit log to live-stream every outbound transmission to
    /// <c>dapps/audit/tx</c> for operators with an MQTT-shaped
    /// monitoring stack. Non-retained because each row is a discrete
    /// event - a late subscriber doesn't need to see yesterday's
    /// transmissions, they want live tail.
    /// </summary>
    public async Task<bool> PublishAsync(string topic, byte[] payload, CancellationToken ct = default)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        try
        {
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT: publish to {0} failed", topic);
            return false;
        }
    }

    public async Task InjectInboundMessage(DbMessage message)
    {
        // The app is everything before the last '@', the same split the
        // ack and replay queries use (app@CALL patterns), so whatever is
        // delivered on dapps/in/<app> can be acked on dapps/ack/<app>.
        var at = message.Destination.LastIndexOf('@');
        if (at <= 0) return;
        var app = message.Destination[..at];

        var topic = InTopicPrefix + app;
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(message.Payload)
            .WithUserProperty("dapps-id", message.Id)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        // dapps-source is who the message is from, the station to reply
        // to: the originator, or the last hop when the originator is
        // unknown. dapps-via is the last hop, the neighbour that handed
        // it to us. dapps-origin, the originator alone, is still sent
        // for apps written for it.
        if (!string.IsNullOrEmpty(message.FromCallsign))
        {
            builder = builder.WithUserProperty("dapps-source", message.FromCallsign);
        }
        if (!string.IsNullOrEmpty(message.SourceCallsign))
        {
            builder = builder.WithUserProperty("dapps-via", message.SourceCallsign);
        }
        if (!string.IsNullOrEmpty(message.OriginatorCallsign))
        {
            builder = builder.WithUserProperty("dapps-origin", message.OriginatorCallsign);
        }
        if (message.Ttl is { } ttl)
        {
            // Residual TTL the originator advertised (or what's left of
            // it after queue dwell). Apps that care about freshness
            // can drop / deprioritise near-expiry messages.
            var residual = TtlMath.Residual(ttl, message.CreatedAt, DateTime.UtcNow) ?? ttl;
            if (residual > 0)
            {
                builder = builder.WithUserProperty(
                    "dapps-ttl", residual.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        // Opt-in ordering: surface the stream id + seq the inbox
        // delivered this on. Apps that opted into ordering by setting
        // dapps-stream on outbound see the same property on inbound,
        // so they can correlate cursors and (if they want) reject
        // out-of-stream-id deliveries.
        if (!string.IsNullOrEmpty(message.StreamId))
        {
            builder = builder.WithUserProperty("dapps-stream", message.StreamId);
            if (message.StreamSeq is { } sn)
            {
                builder = builder.WithUserProperty(
                    "dapps-stream-seq", sn.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        var msg = builder.Build();

        try
        {
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg));
            logger.LogDebug("MQTT: injected message {0} onto {1}", message.Id, topic);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT: failed to inject {0} onto {1}", message.Id, topic);
        }
    }

    /// <summary>
    /// MQTT CONNECT validation. Username = app name, password = token
    /// plaintext. When <see cref="SystemOptions.AuthRequired"/> is true,
    /// rejects with BadUserNameOrPassword (0x04 on MQTT 3.1.1, 0x86 on
    /// MQTT 5) on missing or invalid creds.
    /// A client that sends an empty ClientId is given one first (see
    /// <see cref="AssignClientId"/>), so a plain <c>mosquitto_pub</c>
    /// without <c>-i</c> isn't refused as "identifier rejected".
    /// On accept, records ClientId → app-name so the publish/subscribe
    /// interceptors can enforce topic-app scoping. A connection accepted
    /// without valid credentials (auth off) is recorded with no app, so
    /// it never inherits the app of an earlier connection with the same
    /// ClientId that it takes over.
    /// </summary>
    private async Task OnValidatingConnection(ValidatingConnectionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.ClientId) && !AssignClientId(e))
            return;

        var authRequired = options.CurrentValue.AuthRequired;

        var hasCreds = !string.IsNullOrEmpty(e.UserName)
            && !string.IsNullOrEmpty(e.Password);

        if (!hasCreds)
        {
            if (authRequired)
            {
                e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                logger.LogInformation("MQTT: rejected client {0} - no username or password; app tokens are required, so connect with username = app name, password = its token from the App tokens page", e.ClientId);
                return;
            }
            Remember(e, null, 0);
            return;
        }

        // Captured before the token check, which can take a while (one
        // PBKDF2 per stored token): if the app's token changes meanwhile,
        // this connection must not go through on the old one.
        var generation = TokenGeneration(e.UserName!);
        var app = await tokens.VerifyAsync(e.Password!);
        if (app is null
            || !string.Equals(app, e.UserName, StringComparison.OrdinalIgnoreCase)
            || TokenGeneration(app) != generation)
        {
            if (authRequired)
            {
                e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                logger.LogInformation("MQTT: rejected client {0} - the password is not the current token for app '{1}'", e.ClientId, e.UserName);
                return;
            }
            Remember(e, null, 0);
            return;
        }

        Remember(e, app, generation);
        logger.LogInformation("MQTT: client {0} authenticated as {1}", e.ClientId, app);
    }

    private static readonly FieldInfo? ConnectPacketField =
        typeof(ValidatingConnectionEventArgs).GetField("_connectPacket", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Gives a client that sent an empty ClientId a unique one, as both
    /// MQTT versions allow: MQTT 5 always (the id goes back in the CONNACK
    /// as the Assigned Client Identifier), MQTT 3.1.1 when it asked for a
    /// clean session [MQTT-3.1.3-6]. mosquitto_pub and mosquitto_sub send
    /// an empty id unless given <c>-i</c>, and MQTTnet on its own refuses
    /// that with "identifier rejected". MQTTnet 4.3 offers no public way
    /// to set the id for MQTT 3.1.1, so this sets it on the CONNECT packet
    /// the event args wrap. Returns false, with the refusal already set
    /// and logged, when the client can't be given an id.
    /// </summary>
    private bool AssignClientId(ValidatingConnectionEventArgs e)
    {
        var mqtt5 = e.ProtocolVersion == MqttProtocolVersion.V500;
        if (!mqtt5 && !(e.ProtocolVersion == MqttProtocolVersion.V311 && e.CleanSession == true))
        {
            e.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            logger.LogInformation(
                "MQTT: rejected client from {0} - it sent no client id, which MQTT {1} only allows with a clean session; give it one (mosquitto_pub -i <id>)",
                e.Endpoint, ProtocolName(e.ProtocolVersion));
            return false;
        }

        if (ConnectPacketField?.GetValue(e) is not MQTTnet.Packets.MqttConnectPacket packet)
        {
            e.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            logger.LogWarning(
                "MQTT: rejected client from {0} - it sent no client id and this MQTTnet build gives no way to assign one; give it one (mosquitto_pub -i <id>)",
                e.Endpoint);
            return false;
        }

        var id = "dapps-" + Guid.NewGuid().ToString("N")[..16];
        packet.ClientId = id;
        if (mqtt5) e.AssignedClientIdentifier = id;
        logger.LogInformation("MQTT: client from {0} sent no client id; assigned {1}", e.Endpoint, id);
        return true;
    }

    private static string ProtocolName(MqttProtocolVersion version) => version switch
    {
        MqttProtocolVersion.V310 => "3.1",
        MqttProtocolVersion.V311 => "3.1.1",
        MqttProtocolVersion.V500 => "5",
        _ => version.ToString(),
    };

    private void Remember(ValidatingConnectionEventArgs e, string? app, long generation)
    {
        lock (clientAppsLock)
        {
            clientApps[e.ClientId] = new ClientEntry(app, e.ProtocolVersion, e.Endpoint, generation);
        }
    }

    /// <summary>
    /// Second half of the revoke race guard. The token-changed sweep only
    /// finds clients MQTTnet has registered, and a CONNECT can be between
    /// its token check and registration when the token changes. Once the
    /// client is registered, compare the app's token generation with the
    /// one captured before the check, and drop the client if it moved.
    /// A change after this point is caught by the sweep.
    /// </summary>
    private Task OnClientConnected(ClientConnectedEventArgs e)
    {
        var entry = Lookup(e.ClientId);
        if (entry?.App is { } app
            && entry.Endpoint == e.Endpoint
            && TokenGeneration(app) != entry.TokenGeneration)
        {
            logger.LogInformation("MQTT: disconnecting client {0} (app {1}): its token changed while it was connecting",
                e.ClientId, app);
            // Not awaited inline: the connection is still finishing its
            // own setup on this call.
            _ = Task.Run(async () =>
            {
                try { await server.DisconnectClientAsync(e.ClientId, MqttDisconnectReasonCode.NotAuthorized); }
                catch (Exception ex) { logger.LogWarning(ex, "MQTT: could not disconnect client {0}", e.ClientId); }
            });
        }
        return Task.CompletedTask;
    }

    private ClientEntry? Lookup(string clientId)
    {
        lock (clientAppsLock)
        {
            return clientApps.GetValueOrDefault(clientId);
        }
    }

    private Task OnClientDisconnected(MQTTnet.Server.ClientDisconnectedEventArgs e)
    {
        // A session takeover (the same ClientId connecting again while the
        // broker still holds the old socket) disconnects the old
        // connection after the new one has been validated and recorded.
        // Removing the entry then would strip the new connection's app.
        if (e.DisconnectType == MqttClientDisconnectType.Takeover) return Task.CompletedTask;
        lock (clientAppsLock)
        {
            // The new connection's entry is written during its CONNECT
            // validation, before MQTTnet marks the old one as taken over.
            // If the old socket dies in that gap its disconnect is an
            // ordinary one, so only remove the entry if it is this
            // connection's.
            if (clientApps.TryGetValue(e.ClientId, out var entry)
                && (entry.Endpoint is null || e.Endpoint is null || entry.Endpoint == e.Endpoint))
            {
                clientApps.Remove(e.ClientId);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// SUBSCRIBE-time check. When auth is required, a filter starting
    /// <c>dapps/in/</c> must be exactly the authenticated app's own
    /// <c>dapps/in/&lt;app&gt;</c>, so <c>dapps/in/other</c>,
    /// <c>dapps/in/+</c> and <c>dapps/in/#</c> are refused. Broader
    /// filters such as <c>dapps/#</c> or <c>#</c> are not restricted:
    /// apps on one node may read each other's inbound traffic by design;
    /// tokens stop them interfering (publishing and acking), not reading.
    /// </summary>
    private Task OnInterceptingSubscription(InterceptingSubscriptionEventArgs e)
    {
        if (!options.CurrentValue.AuthRequired) return Task.CompletedTask;

        var topic = e.TopicFilter.Topic;
        if (!topic.StartsWith(InTopicPrefix, StringComparison.Ordinal))
        {
            // Non-DAPPS topic - keep MQTTnet's default behaviour for app
            // coordination topics that aren't ours.
            return Task.CompletedTask;
        }

        var authedApp = Lookup(e.ClientId)?.App;
        if (authedApp is null)
        {
            e.ProcessSubscription = false;
            e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
            return Task.CompletedTask;
        }

        var requested = topic[InTopicPrefix.Length..];
        if (!string.Equals(requested, authedApp, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("MQTT: client {0} (auth={1}) blocked from subscribing to {2}",
                e.ClientId, authedApp, topic);
            e.ProcessSubscription = false;
            e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
        }
        return Task.CompletedTask;
    }

    private async Task OnInterceptingPublish(InterceptingPublishEventArgs e)
    {
        // Internal injections (server-side) have null ClientId - let those through
        // without re-routing. Only intercept publishes from real clients.
        if (string.IsNullOrEmpty(e.ClientId)) return;

        var topic = e.ApplicationMessage.Topic;

        // Topics outside dapps/ are not part of the DAPPS protocol; let
        // them flow (apps may use the broker for their own coordination).
        if (topic != DappsTopicRoot && !topic.StartsWith(DappsTopicRoot + "/", StringComparison.Ordinal)) return;

        var authRequired = options.CurrentValue.AuthRequired;
        var client = Lookup(e.ClientId);
        var authedApp = authRequired ? client?.App : null;

        if (topic.StartsWith(OutTopicPrefix, StringComparison.Ordinal))
        {
            // dapps/out/<app>/<dest>
            var rest = topic[OutTopicPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1)
            {
                logger.LogWarning("MQTT: ignoring publish to malformed topic {0}", topic);
                Refuse(e, MqttPubAckReasonCode.TopicNameInvalid);
                return;
            }
            var app = rest[..slash];
            var dest = rest[(slash + 1)..];

            if (authRequired && !string.Equals(authedApp, app, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("MQTT: client {0} (auth={1}) blocked from publishing on {2}",
                    e.ClientId, authedApp ?? "?", topic);
                Refuse(e, MqttPubAckReasonCode.NotAuthorized);
                return;
            }

            // Optional TTL: an app sets the outbound message's residual
            // lifetime via a `dapps-ttl` user property on the publish.
            // Missing or unparseable → null, i.e. "no expiry".
            int? ttl = null;
            var ttlProp = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(p => string.Equals(p.Name, "dapps-ttl", StringComparison.OrdinalIgnoreCase));
            if (ttlProp is not null
                && int.TryParse(ttlProp.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedTtl)
                && parsedTtl > 0)
            {
                ttl = parsedTtl;
            }

            // Optional opt-in ordering: dapps-stream is the stream id;
            // dapps-stream-gap-timeout is the policy in seconds (0 or
            // missing = strict). The daemon allocates the seq.
            string? streamId = null;
            uint? streamGap = null;
            var sidProp = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(p => string.Equals(p.Name, "dapps-stream", StringComparison.OrdinalIgnoreCase));
            if (sidProp is not null && !string.IsNullOrWhiteSpace(sidProp.Value)
                && !sidProp.Value.Contains(' ') && !sidProp.Value.Contains('=')
                && Encoding.UTF8.GetByteCount(sidProp.Value) <= 255)
            {
                streamId = sidProp.Value;
                var gtProp = e.ApplicationMessage.UserProperties?
                    .FirstOrDefault(p => string.Equals(p.Name, "dapps-stream-gap-timeout", StringComparison.OrdinalIgnoreCase));
                if (gtProp is not null
                    && uint.TryParse(gtProp.Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedGap))
                {
                    streamGap = parsedGap;
                }
            }

            try
            {
                var id = await database.SubmitOutboundMessage(
                    app, dest, e.ApplicationMessage.PayloadSegment.ToArray(), ttl,
                    streamId: streamId, streamGapTimeoutSeconds: streamGap);
                logger.LogInformation("MQTT: queued outbound {0} from app {1} to {2} (ttl={3} stream={4})",
                    id, app, dest, ttl?.ToString() ?? "none", streamId ?? "none");
            }
            catch (Exception ex)
            {
                // Never let the app think a message it handed us is safe
                // when it is not stored: report the failure instead of a
                // success PUBACK.
                logger.LogError(ex, "MQTT: failed to persist outbound publish on {0}", topic);
                FailStore(e, client);
                return;
            }

            // Don't fan out the dapps/out/* publish to subscribers - it's
            // app→DAPPS only.
            e.ProcessPublish = false;
            return;
        }

        if (topic.StartsWith(AckTopicPrefix, StringComparison.Ordinal))
        {
            var ackApp = topic[AckTopicPrefix.Length..];
            if (authRequired && !string.Equals(authedApp, ackApp, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("MQTT: client {0} (auth={1}) blocked from publishing on {2}",
                    e.ClientId, authedApp ?? "?", topic);
                Refuse(e, MqttPubAckReasonCode.NotAuthorized);
                return;
            }

            var id = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
            e.ProcessPublish = false;
            if (id.Length > 0)
            {
                try
                {
                    // Only the app's own inbound messages can be acked: never
                    // another app's, and never a queued outbound or transit
                    // message, which would then silently never be forwarded.
                    if (await database.MarkLocallyDeliveredForApp(ackApp, id))
                    {
                        logger.LogInformation("MQTT: ack received for {0}", id);
                    }
                    else
                    {
                        logger.LogInformation("MQTT: client {0} acked {1}, which is not a message for app {2}; ignored",
                            e.ClientId, id, ackApp);
                        Refuse(e, MqttPubAckReasonCode.NotAuthorized);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MQTT: failed to mark {0} delivered", id);
                    FailStore(e, client);
                }
            }
            return;
        }

        // Every other dapps/ topic is written by DAPPS alone: dapps/in/*
        // (DAPPS to app), dapps/metrics/* and dapps/audit/*. A client
        // publish there, retained or not, could fake a delivery or a
        // heartbeat, so it is refused.
        logger.LogInformation("MQTT: client {0} blocked from publishing on {1}", e.ClientId, topic);
        Refuse(e, MqttPubAckReasonCode.NotAuthorized);
    }

    /// <summary>Drop a client publish and say why in the PUBACK (MQTT 5;
    /// MQTT 3.1.1 has no reason codes, so those clients still see a plain
    /// PUBACK).</summary>
    private static void Refuse(InterceptingPublishEventArgs e, MqttPubAckReasonCode reason)
    {
        e.ProcessPublish = false;
        e.Response.ReasonCode = reason;
    }

    /// <summary>
    /// Storing a publish failed. An MQTT 5 client gets an error PUBACK; an
    /// MQTT 3.1.1 PUBACK cannot carry an error, so that connection is
    /// closed instead of acknowledging. A QoS 1 client with a persistent
    /// session (clean session off) re-sends on reconnect; otherwise the
    /// app sees the publish fail.
    /// </summary>
    private static void FailStore(InterceptingPublishEventArgs e, ClientEntry? client)
    {
        Refuse(e, MqttPubAckReasonCode.ImplementationSpecificError);
        if (client?.ProtocolVersion != MqttProtocolVersion.V500)
        {
            e.CloseConnection = true;
        }
    }


    private async Task OnClientSubscribedTopic(ClientSubscribedTopicEventArgs e)
    {
        var topic = e.TopicFilter.Topic;
        if (!topic.StartsWith(InTopicPrefix, StringComparison.Ordinal)) return;

        var app = topic[InTopicPrefix.Length..];
        if (app.Length == 0 || app.Contains('+') || app.Contains('#')) return; // wildcard, skip replay

        try
        {
            var pending = await database.GetUnacknowledgedLocalMessagesForApp(app);
            logger.LogInformation("MQTT: replaying {0} unacked message(s) to {1} for app {2}",
                pending.Count, e.ClientId, app);

            foreach (var m in pending)
            {
                await InjectInboundMessage(m);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MQTT: replay failed for app {0}", app);
        }
    }
}
