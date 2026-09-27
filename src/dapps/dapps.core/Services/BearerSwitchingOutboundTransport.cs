using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Transport.Rhp;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Outbound transport facade that dispatches each ConnectAsync to the
/// concrete impl matching <c>SystemOptions.NodeBearer</c> *at the
/// moment of the call*. Registered as the singleton
/// <see cref="IDappsOutboundTransport"/>, so a live bearer flip in
/// /Config takes effect on the next forwarder tick with no DI
/// re-resolution. The forwarder loop opens fresh connections per
/// outbound anyway, so picking the impl at call-time is essentially
/// free.
///
/// Also the seam that paces successive connects to the same
/// destination - see <see cref="LinkSettleGate"/>. Both bearers'
/// outbound connects already go through here, so the gate sees every
/// redial - and, for the same reason, this is where every outbound
/// link is registered with <see cref="PeerSessionRegistry"/> (#178).
/// </summary>
public sealed class BearerSwitchingOutboundTransport(
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    IDappsTxGate txGate,
    TimeProvider timeProvider,
    TimeSpan? linkSettleDelay = null,
    PeerSessionRegistry? peerSessions = null,
    TimeSpan? linkSettleSpread = null) : IDappsOutboundTransport
{
    /// <summary>
    /// Minimum gap between releasing a link to a given (bearer, local,
    /// remote, port) and dialling it again.
    ///
    /// Not operator-tunable (unlike most timing knobs in this codebase,
    /// which live on <see cref="SystemOptions"/>) - exposing it would
    /// mean wiring a field through SystemOptionsStore, /Config, the
    /// Setup wizard and the MCP config tools for what's an internal
    /// protocol-timing detail. 2s comfortably covers what field logs
    /// show it taking (under 1s in the observed cases) without
    /// meaningfully slowing bulk sends - each full DAPPSv1 session
    /// already takes several seconds.
    /// </summary>
    public static readonly TimeSpan DefaultLinkSettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Up to this much more, at random, on top of the settle delay. When a
    /// link fails, both ends' sessions end together and both forwarders
    /// redial straight away; without a spread they'd dial each other at the
    /// same moment, cross, and fail together again, in lockstep.
    /// </summary>
    public static readonly TimeSpan DefaultLinkSettleSpread = TimeSpan.FromSeconds(3);

    private readonly ILogger logger = loggerFactory.CreateLogger<BearerSwitchingOutboundTransport>();
    private readonly LinkSettleGate settle = new(timeProvider, linkSettleDelay ?? DefaultLinkSettleDelay, linkSettleSpread ?? DefaultLinkSettleSpread);

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        var isRhpv2 = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase);

        // Bearer tag in the key so switching bearers between two sends to
        // the same callsign doesn't pace against a wait the other bearer
        // never actually needs.
        var key = $"{(isRhpv2 ? "rhpv2" : "agw")}|{localCallsign}|{remoteCallsign}|{bearerPort}";

        var wait = settle.PendingWait(key);
        if (wait > TimeSpan.Zero)
        {
            logger.LogDebug(
                "Outbound: waiting {0}ms before redialling {1} - the previous session's teardown may not have reached the remote node yet",
                (int)wait.TotalMilliseconds, key);
        }
        await settle.WaitAsync(key, stoppingToken);

        // #178/#185: register the link so the forwarder leaves this peer
        // alone until it is torn down (a scheduled poll or probe can be
        // mid-session with the same callsign when the forwarder ticks).
        // TryAcquire (rather than an unconditional Acquire) makes this
        // the authoritative check, not just bookkeeping: it runs
        // immediately before the dial, under the same lock as the check
        // itself, so an inbound session that appeared after
        // OutboundMessageManager's own (earlier, advisory) check can
        // still be caught here - the last point at which backing off
        // still means no SABM went out. Released on dispose, or right
        // away when the connect fails.
        PeerSessionLease? lease = null;
        if (peerSessions is not null)
        {
            lease = peerSessions.TryAcquire(remoteCallsign, "outbound", out var openDirection);
            if (lease is null)
            {
                throw new PeerSessionBusyException(remoteCallsign, openDirection!);
            }
        }

        IDappsConnection inner;
        try
        {
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
        }
        catch
        {
            // A connect that failed part-way (refused, timed out, socket
            // dropped) may still have left half-up link state at either
            // node; pace the retry the same way as a clean disconnect.
            lease?.Dispose();
            settle.RecordRelease(key);
            throw;
        }

        // Connected: any older session with this peer at this node has
        // lost the link to this one (BPQ moves it to the newest socket).
        if (lease is not null) peerSessions!.Connected(lease);
        return new SettleTrackingConnection(inner, settle, key, lease);
    }

    /// <summary>
    /// Wraps the real connection to learn *when* it's actually disposed -
    /// the moment our own disconnect frame went out - so the next connect
    /// to the same key knows how long it's been waiting, and so the peer's
    /// session lease is released at that same moment. Also where the
    /// lease's retirement reaches the session: a retired connection is
    /// closed without a disconnect, which would take the link from the
    /// newer session.
    /// </summary>
    private sealed class SettleTrackingConnection(
        IDappsConnection inner, LinkSettleGate settle, string key, PeerSessionLease? lease) : IDappsConnection
    {
        public Stream Stream => inner.Stream;
        public CancellationToken Retired => lease?.Retired ?? CancellationToken.None;
        public Task CrossedCall => inner.CrossedCall;

        public ValueTask AbandonAsync() => CloseAsync(abandon: true);

        public ValueTask DisposeAsync() => CloseAsync(abandon: Retired.IsCancellationRequested);

        private async ValueTask CloseAsync(bool abandon)
        {
            try
            {
                if (abandon) await inner.AbandonAsync();
                else await inner.DisposeAsync();
            }
            finally
            {
                settle.RecordRelease(key);
                lease?.Dispose();
            }
        }
    }
}
