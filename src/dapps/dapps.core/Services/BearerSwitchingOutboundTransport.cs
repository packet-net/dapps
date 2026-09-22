using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Transport.Rhp;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// <see cref="IDappsOutboundTransport"/> facade that chooses between
/// AGW and RHPv2 at the moment of each <see cref="ConnectAsync"/>
/// based on the live <see cref="SystemOptions.NodeBearer"/> value.
///
/// Built so a /Config save that flips the bearer takes effect on the
/// very next outbound forward - no service restart, no factory
/// re-resolution. The forwarder loop opens fresh connections per
/// outbound anyway, so picking the impl at call-time is essentially
/// free.
///
/// Also the seam that paces successive connects to the same
/// destination - see <see cref="LinkSettleDelay"/>.
/// </summary>
public sealed class BearerSwitchingOutboundTransport(
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    IDappsTxGate txGate,
    TimeProvider timeProvider,
    TimeSpan? linkSettleDelay = null) : IDappsOutboundTransport
{
    private readonly ILogger logger = loggerFactory.CreateLogger<BearerSwitchingOutboundTransport>();

    /// <summary>
    /// Minimum gap between disposing a connection to a given (bearer,
    /// local, remote, port) and dialing it again.
    ///
    /// Disconnecting only tells *our own* node (BPQ, XRouter, ...) to
    /// tear the L2/session down; that has to propagate to the remote
    /// end's node (over RF, AXIP, ...) before it will accept a fresh
    /// connect for the same callsign pair. OutboundMessageManager sends
    /// queued messages back-to-back with no gap of its own, so for a
    /// destination with several messages pending this is what supplies
    /// one. Without it, a redial can beat that propagation and get
    /// rejected on the remote node - invisible to us; the symptom is a
    /// forward that times out for no apparent reason, and the remote
    /// operator seeing e.g. "already connected on socket N" in their
    /// own node's log with no corresponding attempt on our side.
    ///
    /// Not currently operator-tunable (unlike most timing knobs in this
    /// codebase, which live on <see cref="SystemOptions"/>) - exposing
    /// it would mean wiring a field through SystemOptionsStore, /Config,
    /// the Setup wizard and the MCP config tools for what's an internal
    /// protocol-timing detail, not something an operator needs to see.
    /// 2s comfortably covers what field logs show it taking (under 1s
    /// in the observed cases) without meaningfully slowing bulk sends -
    /// each full DAPPSv1 session already takes several seconds.
    /// </summary>
    private readonly TimeSpan linkSettleDelay = linkSettleDelay ?? TimeSpan.FromSeconds(2);

    // (System.Threading.Lock is .NET 9+; this project targets net8.0.)
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastDisconnect = new();

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        var isRhpv2 = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase);

        // Bearer tag in the key so switching bearers between two sends to
        // the same callsign doesn't pace against a wait the other bearer
        // never actually needs.
        var key = $"{(isRhpv2 ? "rhpv2" : "agw")}|{localCallsign}|{remoteCallsign}|{bearerPort}";
        await WaitForSettleAsync(key, stoppingToken);

        IDappsConnection inner;
        if (isRhpv2)
        {
            var port = opts.RhpPort > 0 ? opts.RhpPort : 9000;
            var user = string.IsNullOrEmpty(opts.RhpUser) ? null : opts.RhpUser;
            var pass = string.IsNullOrEmpty(opts.RhpPass) ? null : opts.RhpPass;
            var rhp = new Rhpv2OutboundTransport(
                opts.NodeHost, port,
                loggerFactory.CreateLogger<Rhpv2OutboundTransport>(),
                user, pass,
                txGate);
            inner = await rhp.ConnectAsync(localCallsign, remoteCallsign, bearerPort, stoppingToken);
        }
        else
        {
            var agw = new AgwOutboundTransport(opts.NodeHost, opts.AgwPort, loggerFactory, txGate);
            inner = await agw.ConnectAsync(localCallsign, remoteCallsign, bearerPort, stoppingToken);
        }

        return new SettleTrackingConnection(inner, this, key);
    }

    private async Task WaitForSettleAsync(string key, CancellationToken ct)
    {
        if (linkSettleDelay <= TimeSpan.Zero) return;

        TimeSpan wait;
        lock (gate)
        {
            if (!lastDisconnect.TryGetValue(key, out var last)) return;
            wait = linkSettleDelay - (timeProvider.GetUtcNow() - last);
        }

        if (wait > TimeSpan.Zero)
        {
            logger.LogDebug(
                "Outbound: waiting {0}ms before redialing {1} - the previous session's teardown may not have reached the remote node yet",
                (int)wait.TotalMilliseconds, key);
            await Task.Delay(wait, timeProvider, ct);
        }
    }

    private void RecordDisconnect(string key)
    {
        lock (gate) { lastDisconnect[key] = timeProvider.GetUtcNow(); }
    }

    /// <summary>
    /// Wraps the real connection purely to learn *when* it's actually
    /// disposed - the moment our own disconnect frame went out - so the
    /// next connect to the same key knows how long it's been waiting.
    /// </summary>
    private sealed class SettleTrackingConnection(
        IDappsConnection inner, BearerSwitchingOutboundTransport owner, string key) : IDappsConnection
    {
        public Stream Stream => inner.Stream;

        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            finally { owner.RecordDisconnect(key); }
        }
    }
}
