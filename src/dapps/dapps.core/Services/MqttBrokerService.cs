using dapps.core.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
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

    /// <summary>Tracks which app a connected MQTT client authenticated as,
    /// keyed on ClientId. When auth is required, publish/subscribe
    /// interceptors check this to enforce topic-app scoping.</summary>
    private readonly Dictionary<string, string> clientApps = new(StringComparer.Ordinal);
    private readonly object clientAppsLock = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;

        server.ValidatingConnectionAsync += OnValidatingConnection;
        server.InterceptingPublishAsync += OnInterceptingPublish;
        server.InterceptingSubscriptionAsync += OnInterceptingSubscription;
        server.ClientSubscribedTopicAsync += OnClientSubscribedTopic;
        server.ClientDisconnectedAsync += OnClientDisconnected;

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
        server.ClientDisconnectedAsync -= OnClientDisconnected;
        return Task.CompletedTask;
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
        var (app, _) = DestinationParser.Parse(message.Destination);
        if (app.Length == 0) return;

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
    /// rejects with NotAuthorized on missing or invalid creds.
    /// On accept, records ClientId → app-name so the publish/subscribe
    /// interceptors can enforce topic-app scoping.
    /// </summary>
    private async Task OnValidatingConnection(ValidatingConnectionEventArgs e)
    {
        var authRequired = options.CurrentValue.AuthRequired;

        var hasCreds = !string.IsNullOrEmpty(e.UserName)
            && !string.IsNullOrEmpty(e.Password);

        if (!hasCreds)
        {
            if (authRequired)
            {
                e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                logger.LogInformation("MQTT: rejected client {0} - missing credentials", e.ClientId);
            }
            return;
        }

        var app = await tokens.VerifyAsync(e.Password!);
        if (app is null || !string.Equals(app, e.UserName, StringComparison.OrdinalIgnoreCase))
        {
            if (authRequired)
            {
                e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                logger.LogInformation("MQTT: rejected client {0} - invalid credentials", e.ClientId);
            }
            return;
        }

        lock (clientAppsLock)
        {
            clientApps[e.ClientId] = app;
        }
        logger.LogInformation("MQTT: client {0} authenticated as {1}", e.ClientId, app);
    }

    private Task OnClientDisconnected(MQTTnet.Server.ClientDisconnectedEventArgs e)
    {
        lock (clientAppsLock)
        {
            clientApps.Remove(e.ClientId);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// SUBSCRIBE-time topic scope. When auth is required, only the
    /// authenticated app may subscribe to its own <c>dapps/in/&lt;app&gt;</c>;
    /// other DAPPS topics aren't subscribable by clients (they're
    /// app→DAPPS direction).
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

        string? authedApp;
        lock (clientAppsLock)
        {
            clientApps.TryGetValue(e.ClientId, out authedApp);
        }
        if (authedApp is null)
        {
            e.ProcessSubscription = false;
            return Task.CompletedTask;
        }

        var requested = topic[InTopicPrefix.Length..];
        if (!string.Equals(requested, authedApp, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("MQTT: client {0} (auth={1}) blocked from subscribing to {2}",
                e.ClientId, authedApp, topic);
            e.ProcessSubscription = false;
        }
        return Task.CompletedTask;
    }

    private async Task OnInterceptingPublish(InterceptingPublishEventArgs e)
    {
        // Internal injections (server-side) have null ClientId - let those through
        // without re-routing. Only intercept publishes from real clients.
        if (string.IsNullOrEmpty(e.ClientId)) return;

        var topic = e.ApplicationMessage.Topic;
        var authRequired = options.CurrentValue.AuthRequired;
        string? authedApp = null;
        if (authRequired)
        {
            lock (clientAppsLock) clientApps.TryGetValue(e.ClientId, out authedApp);
        }

        if (topic.StartsWith(OutTopicPrefix, StringComparison.Ordinal))
        {
            // dapps/out/<app>/<dest>
            var rest = topic[OutTopicPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1)
            {
                logger.LogWarning("MQTT: ignoring publish to malformed topic {0}", topic);
                e.ProcessPublish = false;
                return;
            }
            var app = rest[..slash];
            var dest = rest[(slash + 1)..];

            if (authRequired && !string.Equals(authedApp, app, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("MQTT: client {0} (auth={1}) blocked from publishing on {2}",
                    e.ClientId, authedApp ?? "?", topic);
                e.ProcessPublish = false;
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
                logger.LogError(ex, "MQTT: failed to persist outbound publish on {0}", topic);
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
                e.ProcessPublish = false;
                return;
            }

            var id = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
            if (id.Length > 0)
            {
                try
                {
                    await database.MarkLocallyDelivered(id);
                    logger.LogInformation("MQTT: ack received for {0}", id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MQTT: failed to mark {0} delivered", id);
                }
            }

            e.ProcessPublish = false;
            return;
        }

        if (topic.StartsWith(InTopicPrefix, StringComparison.Ordinal))
        {
            // Apps shouldn't be publishing to in/* - it's DAPPS→app only.
            // Block but don't error noisily.
            logger.LogDebug("MQTT: blocking client publish to in-topic {0}", topic);
            e.ProcessPublish = false;
            return;
        }

        // Any other topics are not part of the DAPPS protocol; let them flow
        // (apps may use the broker for their own coordination if they want).
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
