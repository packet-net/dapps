using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using dapps.core.Updater;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using MQTTnet.AspNetCore;
using System.Net.Sockets;
// OpenAPI / Scalar dropped in the .NET 8 rollback - the native
// OpenAPI generation (AddOpenApi / MapOpenApi) is a .NET 9+ API.
// To revisit once we're back on a newer .NET runtime.

// Plan C5.2 - CLI side-doors that don't boot the host.
// Recognised: --version, --check-update, --apply-update, --rollback.
// Returning before CreateBuilder runs means these work even when the
// on-disk dapps.db is incompatible / a port is wedged / the callsign
// is unset, which is exactly when --rollback is most useful.
if (UpdaterCli.TryHandle(args, out var cliExitCode)) return cliExitCode;

// Seed the systemoptions table BEFORE host build. The
// SystemOptions Configure callback (just below) fires during eager
// hosted-service DI graph materialisation - UdpDatagramListener →
// IBackhaulInbox → IRoutingAlgorithm → IOptionsMonitor.CurrentValue -
// which would race a hosted-service seeder and lose, since hosted
// services are CONSTRUCTED in one pass before any of their StartAsync
// runs.
//
// A throwaway console logger (the host's logging isn't built yet)
// makes the seeding decisions visible: which options were seeded from
// env vars, which stored values an env var re-applied (deployment-
// managed config), and a callsign derived from a pdn host's
// PDN_NODE_CALLSIGN.
using (var seedLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole()))
{
    DbStartup.EnsureSchemaAndSeed(seedLoggerFactory.CreateLogger(nameof(DbStartup)));
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
// SystemOptions: hot-reloadable IOptionsMonitor backed by the
// systemoptions SQLite table. ConfigController.Post calls
// store.SaveAsync(...) to persist + fire OnChange; bearer services
// and the rest of the daemon react via OnChange listeners (mostly:
// reread CurrentValue on the next tick). Replaces the earlier
// one-shot AddOptions().Configure() callback that read once at host
// build time and never re-read.
builder.Services.AddSingleton<SystemOptionsStore>();
builder.Services.AddSingleton<IOptionsMonitor<SystemOptions>>(
    sp => sp.GetRequiredService<SystemOptionsStore>());

// TX kill-switch wiring. The gate composes two signals: a local
// operator toggle (SystemOptions.TxEnabled) and a remote
// kill-switch URL polled by TxKillSwitchPoller. The poller URL is
// hardcoded - a development-phase safety net controlled by the
// project author; see docs/dev-time-tx-kill-switch.md for the
// rationale and removal plan. The poller IS the ITxKillSwitchSignal:
// registered both as a singleton (so the gate can read its state)
// and as a hosted service (so the polling loop runs). Register the
// concrete gate as a singleton so the TxControlController can read
// both signals independently for the dashboard banner; the
// IDappsTxGate alias resolves the same instance for bearers.
builder.Services.AddSingleton<TxKillSwitchPoller>();
builder.Services.AddSingleton<ITxKillSwitchSignal>(sp => sp.GetRequiredService<TxKillSwitchPoller>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TxKillSwitchPoller>());
builder.Services.AddSingleton<SystemOptionsBackedTxGate>();
builder.Services.AddSingleton<IDappsTxGate>(sp => sp.GetRequiredService<SystemOptionsBackedTxGate>());

builder.Services.AddHttpClient();

// Dedicated named client for the TX kill-switch poller. Pinned to
// a fresh SocketsHttpHandler so any future global handler tweak
// (e.g. someone adding a "trust all certs" callback to the default
// for testing and forgetting to remove it) cannot weaken the
// validation on the kill-switch fetch. SslOptions is left at
// default = system trust store + chain / hostname / expiry. The
// poller adds a runtime guard that the URL scheme is https://;
// together those two stop a downgrade or a bypassed validation
// from silently rendering the kill-switch ineffective.
builder.Services.AddHttpClient("tx-kill-switch")
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions(),
    });

// Plan A polish - single TimeProvider injected everywhere
// cadence-sensitive code reads time. Tests substitute
// FakeTimeProvider (Microsoft.Extensions.TimeProvider.Testing) so
// `Advance(30s)` deterministically fast-forwards every service that
// uses it. Production wires the system clock.
builder.Services.AddSingleton(TimeProvider.System);

// Plan B7 - single airtime budget shared by every discovery-class
// transmission (beacons, solicit replies, probes). OutboundActivity-
// Tracker is the WhenQuiet probe-strategy oracle; the forwarder
// pings it on every successful send.
builder.Services.AddSingleton<AirtimeAccountant>();
builder.Services.AddSingleton<OutboundActivityTracker>();

// Plan C3 PR-B - operational snapshot composer used by both
// /Operational and HeartbeatPublisher. Singleton so the two
// consumers see consistent state.
builder.Services.AddSingleton<OperationalSnapshotBuilder>();
builder.Services.AddHostedService<HeartbeatPublisher>();

// Plan G (MCP) - Model Context Protocol server, exposing operator-
// facing tools to Claude / other MCP clients on /mcp. Bootstrap
// registers a single tool wrapping the operational snapshot;
// subsequent PRs add the full read / action / diagnostic toolkits.
// AdminAuthMiddleware allowlists /mcp the same way it does /Health
// - these endpoints are designed for clients that don't have the
// admin cookie.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<dapps.core.Mcp.DappsHealthTools>()
    .WithTools<dapps.core.Mcp.DappsNetworkTools>()
    .WithTools<dapps.core.Mcp.DappsRoutingTools>()
    .WithTools<dapps.core.Mcp.DappsMessageTools>()
    .WithTools<dapps.core.Mcp.DappsActionTools>()
    .WithTools<dapps.core.Mcp.DappsConfigTools>()
    .WithTools<dapps.core.Mcp.DappsDiagnosticTools>()
    .WithTools<dapps.core.Mcp.DappsExplorationTools>()
    .WithTools<dapps.core.Mcp.DappsUpdateTools>()
    .WithTools<dapps.core.Mcp.DappsAuditTools>();

builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());

// Plan C5.2 - the unprivileged dapps daemon only writes the request
// marker / reads the status file. The actual update work runs in the
// privileged dapps-updater.service via `dapps --apply-update`.
builder.Services.AddSingleton<IUpdaterFileSystem, RealUpdaterFileSystem>();

// Embedded MQTT broker. Register via MQTTnet.AspNetCore so a single
// MqttServer instance backs both the TCP listener (port from
// SystemOptions.MqttPort) and the WebSocket endpoint mounted at
// /mqtt below. Browsers can speak MQTT directly over the WS endpoint -
// no REST round-trip per message.
//
// AddHostedMqttServerWithServices registers MqttServer + an IHostedService
// that owns its lifecycle, and runs the options-builder callback at
// singleton-resolution time so SystemOptions is fully populated before
// we read MqttPort. AddMqttConnectionHandler + AddMqttTcpServerAdapter
// supply the WS / TCP transports respectively. MqttBrokerService runs
// AFTER the broker is up and just attaches DAPPS-specific event handlers
// (auth, topic-scope enforcement, queue persistence, replay-on-subscribe).
builder.Services.AddHostedMqttServerWithServices(builder =>
{
    var sysOpts = builder.ServiceProvider.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue;
    builder.WithDefaultEndpoint().WithDefaultEndpointPort(sysOpts.MqttPort);
});
builder.Services.AddMqttConnectionHandler();
builder.Services.AddMqttTcpServerAdapter();
builder.Services.AddConnections();
builder.Services.AddSingleton<MqttBrokerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBrokerService>());

// Both inbound bearer services are registered unconditionally. Each
// gates itself on SystemOptions.NodeBearer at runtime - the active
// bearer's service runs its connect-and-dispatch loop, the inactive
// one idles. Switching bearers via /Config fires the OnChange handler
// on each, which cancels the active connection cycle so the loop
// re-evaluates with the new value (hot-reload, no restart).
builder.Services.AddHostedService<AgwInboundService>();
builder.Services.AddHostedService<Rhpv2InboundService>();
builder.Services.AddHostedService<TtlSweeperService>();
builder.Services.AddHostedService<StreamGapSweeperService>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<AppTokenStore>();
builder.Services.AddSingleton<AdminPasswordStore>();
builder.Services.AddSingleton<InboundEventBus>();
builder.Services.AddSingleton<OperationalMetrics>();
builder.Services.AddSingleton<TransmissionAuditService>();
builder.Services.AddSingleton<AgwPortQuery>();

// Cookie auth for the dashboard / admin endpoints. Long sliding
// expiry (90 days) - this is a sysop's home node, the cookie's
// "remember me indefinitely" by design. /AppApi/* doesn't use this
// scheme; it has its own bearer-token middleware.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "dapps.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;
    });
builder.Services.AddSingleton<OutboundMessageManager>();
// B5 routing seam - IRoutingAlgorithm is the strategy, IRoutingContext
// is the slice of node state it reads. Two stacks shipped today;
// SystemOptions.RoutingAlgorithm picks one at startup. Both wrap
// StaticRoutingAlgorithm so manual operator overrides always win.
//
//   passive-flood (default): FloodFallbackAlgorithm →
//     PassiveLearningAlgorithm → StaticRoutingAlgorithm. Stores per-
//     destination next-hop only; floods on cold-start.
//
//   meshcore: MeshCoreLikeRoutingAlgorithm → StaticRoutingAlgorithm.
//     Stores full discovered paths in DbDiscoveredPath; subsequent
//     sends embed the route on the wire as SourceRoute.
builder.Services.AddSingleton<IRoutingContext, DatabaseRoutingContext>();
builder.Services.AddSingleton<StaticRoutingAlgorithm>();
builder.Services.AddSingleton<PassiveLearningAlgorithm>();
builder.Services.AddSingleton<IRoutingAlgorithm>(sp =>
{
    var optsValue = sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue;
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var startupLog = sp.GetRequiredService<ILogger<Program>>();
    var staticAlg = sp.GetRequiredService<StaticRoutingAlgorithm>();
    switch ((optsValue.RoutingAlgorithm ?? "passive-flood").ToLowerInvariant())
    {
        case "meshcore":
            startupLog.LogInformation("Routing stack: MeshCoreLikeRoutingAlgorithm → StaticRoutingAlgorithm");
            return new MeshCoreLikeRoutingAlgorithm(staticAlg, lf.CreateLogger<MeshCoreLikeRoutingAlgorithm>());
        case "passive-flood":
        default:
            // Unknown values fall through to the safe default rather
            // than failing startup - operators editing the option by
            // hand will see a recognisable algorithm running and a
            // log line they can grep for.
            if (!string.Equals(optsValue.RoutingAlgorithm, "passive-flood", StringComparison.OrdinalIgnoreCase))
            {
                startupLog.LogWarning(
                    "Unknown RoutingAlgorithm '{0}'; falling back to passive-flood",
                    optsValue.RoutingAlgorithm);
            }
            startupLog.LogInformation("Routing stack: FloodFallback → PassiveLearning → Static");
            return new FloodFallbackAlgorithm(
                sp.GetRequiredService<PassiveLearningAlgorithm>(),
                lf.CreateLogger<FloodFallbackAlgorithm>());
    }
});
// Auto-forwarder: ticks DoRun on a short cadence so submitted messages
// move without a manual /Message/dorun poke. Manual poke still works.
builder.Services.AddHostedService<OutboundForwarderService>();
// Outbound transport facade: dispatches each ConnectAsync to the
// concrete impl matching SystemOptions.NodeBearer at the moment of
// the call. Hot-reloadable - no need to invalidate / re-resolve when
// the operator switches bearer; the next forwarder tick picks up the
// new value automatically.
builder.Services.AddSingleton<IDappsOutboundTransport, BearerSwitchingOutboundTransport>();
// Order of registration matters: OutboundMessageManager picks the first
// IDappsBackhaul whose CanHandle returns true for the route. UDP wins
// when the neighbour has a UdpEndpoint set; AGW handles everything else.
builder.Services.AddSingleton<UdpDatagramBackhaul>(sp =>
    new UdpDatagramBackhaul(
        sp.GetRequiredService<ILoggerFactory>(),
        txGate: sp.GetRequiredService<IDappsTxGate>()));
builder.Services.AddSingleton<IDappsBackhaul>(sp => sp.GetRequiredService<UdpDatagramBackhaul>());
builder.Services.AddSingleton<IRouteGossipPort, RouteGossipPort>();
// MeshCore Companion bearer (#154). Registered before the AGW catch-all so a
// route carrying a MeshCore channel hint is handled here, not by AGW. Inert
// (CanHandle=false, no serial port opened) unless MeshCoreEnabled=true;
// MeshCoreBearerService starts the link + inbound loop.
builder.Services.AddSingleton<MeshCoreBearer>();
builder.Services.AddSingleton<IDappsBackhaul>(sp => sp.GetRequiredService<MeshCoreBearer>());
builder.Services.AddHostedService<MeshCoreBearerService>();
builder.Services.AddSingleton<IDappsBackhaul>(sp => new Dappsv1SessionBackhaul(
    sp.GetRequiredService<IDappsOutboundTransport>(),
    sp.GetRequiredService<ILoggerFactory>(),
    // Opportunistic poll: hand the backhaul the inbox so it can
    // deliver any messages the remote has queued for us, plus a
    // live read of the operator toggle (re-checked per push so a
    // /Config flip takes effect on the next session).
    opportunisticInbox: sp.GetRequiredService<IBackhaulInbox>(),
    opportunisticEnabled: () => sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue.OpportunisticPollEnabled,
    // Route gossip: piggyback `routes` pulls from neighbours when
    // the per-(local, remote) staleness gate allows. Bounded airtime,
    // no scheduled transmission.
    routeGossip: sp.GetRequiredService<IRouteGossipPort>()));
builder.Services.AddSingleton<DatabaseAndMqttInbox>();
builder.Services.AddSingleton<IBackhaulInbox>(sp => sp.GetRequiredService<DatabaseAndMqttInbox>());
builder.Services.AddHostedService<UdpDatagramListener>();

// B6.1 - connected-mode probe-and-map. NodeProber is stateless and
// reuses the singleton AGW transport; the scheduler drives it on a
// slow cadence when SystemOptions.ProbingEnabled is true.
builder.Services.AddSingleton<NodeProber>();
builder.Services.AddSingleton<ProbeSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbeSchedulerService>());

// F3b - scheduled poll. NodePoller is stateless, opens a session,
// drains via rev. The scheduler walks neighbours when
// SystemOptions.ScheduledPollEnabled is true (off by default).
builder.Services.AddSingleton<NodePoller>();
builder.Services.AddSingleton<PollSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollSchedulerService>());

// DiscoveryService constructs its bearers itself in StartAsync rather
// than receiving them via IEnumerable<IDiscoveryBearer>, so the bearer
// factory's SystemOptions read happens after the host is fully running.
//
// Registered as a singleton so the /DiscoveryChannels controller can
// inject it for B6.2 on-demand solicits. AddHostedService binds the
// same instance to the host lifecycle.
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
builder.Services.AddLogging(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });
});
var app = builder.Build();

// pdn app-gateway support (packet.net docs/app-gateway.md). When the
// dashboard is reverse-proxied under /apps/dapps/, the gateway strips
// the prefix from the forwarded path and injects
// `X-Forwarded-Prefix: /apps/dapps`. Setting Request.PathBase from the
// header makes every framework-generated URL (Razor tag helpers, `~/`
// hrefs, Url.Content, RedirectToPage, LocalRedirect("~/..."), the
// cookie scheme's /Login challenge) come out prefixed, so links and
// redirects rendered through the proxy stay inside the mount. The
// path itself is NOT touched - the gateway already stripped the
// prefix, so PathBase is purely additive for URL generation. Without
// the header (standalone direct access) PathBase stays empty and
// every URL renders exactly as before.
//
// MUST run first in the pipeline - before auth (the cookie challenge
// builds its redirect from PathBase) and before any endpoint that
// generates URLs.
app.Use((ctx, next) =>
{
    var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString().TrimEnd('/');
    if (!string.IsNullOrEmpty(prefix) && prefix.StartsWith('/'))
    {
        ctx.Request.PathBase = new PathString(prefix);
    }
    return next(ctx);
});

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<BearerAuthMiddleware>();
app.UseMiddleware<AdminAuthMiddleware>();
// MQTT-over-WebSocket. Same broker instance as the TCP :MqttPort
// listener; same auth interceptors. Browsers connect to ws://host/mqtt
// with sub-protocol "mqtt" and speak MQTT 5 directly. Allowlisted in
// AdminAuthMiddleware alongside /Health and /mcp - WebSocket clients
// don't carry the admin cookie.
app.UseWebSockets();
app.MapMqtt("/mqtt");
app.MapControllers();
app.MapRazorPages();
// Plan G - mount the MCP endpoint at /mcp. The MCP transport
// negotiates streamable-HTTP / SSE itself; we just need the route
// reachable. Allowlisted in AdminAuthMiddleware alongside /Health.
app.MapMcp("/mcp");

try
{
    app.Run();
}
catch (Exception ex) when (IsFatalConfigError(ex))
{
    // Operationally-fatal config errors that won't fix themselves on
    // restart. Exit with code 78 - paired with
    // RestartPreventExitStatus=78 in the systemd unit (see
    // scripts/dapps.service) so systemd stops the crash-loop and
    // surfaces the actionable journal message instead. Drop a single
    // operator-facing hint into the journal so the cause is obvious
    // even without scrolling past the stack trace - typical case is
    // a co-located mosquitto holding :MqttPort or a stray daemon on
    // ASPNETCORE_URLS' port.
    var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("dapps");
    var mqttPort = app.Services.GetService<IOptionsMonitor<SystemOptions>>()?.CurrentValue.MqttPort;
    logger?.LogCritical(
        "Fatal startup config error: a port is already in use. Check DAPPS_MQTT_PORT (currently {0}) and ASPNETCORE_URLS, " +
        "free whichever is held, or POST {{\"MqttPort\":<free-port>}} to /Config and restart.",
        mqttPort?.ToString() ?? "?");
    return 78;
}
return 0;

static bool IsFatalConfigError(Exception ex)
{
    // Walk the inner-exception chain - the host's RunAsync wraps
    // service-startup exceptions, so the SocketException can be one
    // or two levels deep.
    for (var e = ex; e is not null; e = e.InnerException)
    {
        if (e is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }
    return false;
}
