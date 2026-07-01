using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.meshcore;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Host glue for the MeshCore bearer (Phase H1, #154). Registered as an
/// <see cref="IDappsBackhaul"/> so <c>OutboundMessageManager</c> can pick it for
/// routes carrying a MeshCore channel hint, and driven by
/// <see cref="MeshCoreBearerService"/> which builds the bearer config from
/// <see cref="SystemOptions"/> and starts the link + inbound loop when enabled.
///
/// When disabled (the default) <see cref="CanHandle"/> returns false and no
/// serial port is opened, so this is inert until <c>MeshCoreEnabled=true</c>.
/// </summary>
public sealed class MeshCoreBearer : IDappsBackhaul, IAsyncDisposable
{
    private readonly IOptionsMonitor<SystemOptions> _sysOpts;
    private readonly IBackhaulInbox _inbox;
    private readonly IDappsTxGate _txGate;
    private readonly Database _database;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MeshCoreBearer> _log;

    private MeshCoreLink? _link;
    private MeshCoreCompanionBackhaul? _backhaul;
    private MeshCoreInbound? _inbound;
    private MeshCoreReliability? _reliability;

    // Passive discovery (#27): throttle DB upserts per peer so a chatty channel
    // doesn't hammer SQLite — we only need to refresh a peer's freshness, not record
    // every frame. Keyed on the source callsign; guarded by its own lock.
    private static readonly TimeSpan DiscoveryUpsertThrottle = TimeSpan.FromSeconds(30);
    private readonly object _discoveryLock = new();
    private readonly Dictionary<string, DateTime> _lastDiscoveryUpsert = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; private set; }
    public MeshCoreLink? Link => _link;
    public MeshCoreInbound? Inbound => _inbound;

    /// <summary>A read-only snapshot of the bearer for the dashboard / API.</summary>
    public MeshCoreStatus GetStatus()
    {
        var link = _link;
        var self = link?.Self;
        return new MeshCoreStatus(
            Enabled: Enabled,
            LinkState: link?.State.ToString() ?? "Down",
            FreqMhz: self?.FreqMhz,
            Sf: self?.Sf,
            Cr: self?.Cr,
            Resets: link?.ResetCount ?? 0,
            Occupancy: _backhaul?.Occupancy ?? 0,
            Delivered: _inbound?.Delivered ?? 0,
            ReliablePending: _reliability?.PendingCount ?? 0,
            ReliableConfirmed: _reliability?.Confirmed ?? 0,
            ReliableExpired: _reliability?.Expired ?? 0);
    }

    /// <summary>Operator device-control: force the radio through a hard reset +
    /// reconfigure. Returns false if the bearer isn't running.</summary>
    public Task<bool> ResetRadioAsync(CancellationToken ct) =>
        _link is { } link ? link.RecoverAsync(ct) : Task.FromResult(false);

    public MeshCoreBearer(
        IOptionsMonitor<SystemOptions> sysOpts,
        IBackhaulInbox inbox,
        IDappsTxGate txGate,
        Database database,
        ILoggerFactory loggerFactory)
    {
        _sysOpts = sysOpts;
        _inbox = inbox;
        _txGate = txGate;
        _database = database;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<MeshCoreBearer>();
    }

    /// <summary>Start the link + inbound loop (no-op when disabled). Runs until
    /// <paramref name="ct"/> fires; the hosted service awaits this.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var s = _sysOpts.CurrentValue;
        if (!s.MeshCoreEnabled)
        {
            _log.LogInformation("MeshCore bearer disabled (MeshCoreEnabled=false)");
            return;
        }

        var opts = BuildOptions(s);
        var budget = new TxBudget(opts.AirtimeBudgetSecPerHour);
        var reliability = opts.ReliableDelivery ? new MeshCoreReliability() : null;
        _reliability = reliability;
        _link = new MeshCoreLink(opts, _loggerFactory.CreateLogger<MeshCoreLink>());

        try
        {
            await _link.StartAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MeshCore bearer failed to start on {0}", opts.SerialPort);
            return;
        }

        var backhaul = new MeshCoreCompanionBackhaul(
            _link, opts, budget, _loggerFactory.CreateLogger<MeshCoreCompanionBackhaul>(), _txGate, reliability);
        _backhaul = backhaul;
        _inbound = new MeshCoreInbound(
            _link, _inbox, _loggerFactory.CreateLogger<MeshCoreInbound>(),
            reliability,
            sendAck: (ack, c) => backhaul.ResendAsync(ack, opts.LocalCallsign, c),
            localCallsign: opts.LocalCallsign,
            onPeerHeard: (src, c) => RecordPeerHeardAsync(src, opts, c));
        Enabled = true;

        // Reliability resend loop runs alongside the inbound drain loop.
        var resendTask = reliability is not null
            ? Task.Run(() => ResendLoopAsync(reliability, backhaul, ct), ct)
            : Task.CompletedTask;
        // Age out stale discovered peers ourselves. On a MeshCore-only node there's no
        // AGW/UDP discovery channel, so DiscoveryService (the usual sweeper) never runs
        // its age-out; without this, passively-recorded rows would accumulate for the
        // node's lifetime (#27 review). Bearer-agnostic and idempotent, so it's harmless
        // to also run on a mixed node where DiscoveryService sweeps too.
        var housekeepingTask = Task.Run(() => AgeOutLoopAsync(ct), ct);
        try { await _inbound.RunAsync(ct); }
        finally
        {
            try { await resendTask; } catch { /* shutdown */ }
            try { await housekeepingTask; } catch { /* shutdown */ }
        }
    }

    private async Task AgeOutLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                var aged = await _database.AgeOutDiscoveredPeers(DateTime.UtcNow);
                if (aged.Count > 0)
                    _log.LogInformation("MeshCore: aged out {0} stale discovered peer(s)", aged.Count);
            }
            catch (Exception ex) { _log.LogDebug("MeshCore: discovered-peer age-out failed: {0}", ex.Message); }
        }
    }

    private async Task ResendLoopAsync(MeshCoreReliability reliability, MeshCoreCompanionBackhaul backhaul, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            foreach (var m in reliability.DropExpired(now))
                _log.LogWarning("MeshCore: reliable delivery gave up on {0} (unacked past lifetime)", m.Id);
            foreach (var (msg, local) in reliability.DueResends(now))
            {
                try
                {
                    var res = await backhaul.ResendAsync(msg, local, ct);
                    if (res.Accepted) reliability.MarkResent(msg.Id, DateTime.UtcNow);
                    else _log.LogDebug("MeshCore: resend of {0} deferred: {1}", msg.Id, res.Error);
                }
                catch (Exception ex) { _log.LogDebug("MeshCore: resend of {0} failed: {1}", msg.Id, ex.Message); }
            }
        }
    }

    /// <summary>Passive discovery (#27): record a peer we heard over MeshCore as a
    /// fresh <see cref="DbDiscoveredPeer"/> so the router can send to it without a
    /// manual neighbour. Throttled per peer, skips ourselves (a repeater may echo our
    /// own frames), and never lets a DB fault break the inbound drain loop.</summary>
    private async Task RecordPeerHeardAsync(string source, MeshCoreBearerOptions opts, CancellationToken ct)
    {
        // A repeater re-broadcasting our own frame would otherwise teach us a route to
        // ourselves. Compare on the base callsign (SSID-insensitive), like the router.
        // Use the LIVE callsign, not the one captured at bearer start: outbound frames
        // are stamped with the current callsign, so after a runtime rename an echo of
        // our own frame must still be recognised as self.
        var localCallsign = _sysOpts.CurrentValue.Callsign ?? "";
        if (string.Equals(source.Split('-')[0], localCallsign.Split('-')[0], StringComparison.OrdinalIgnoreCase))
            return;

        var now = DateTime.UtcNow;
        lock (_discoveryLock)
        {
            if (_lastDiscoveryUpsert.TryGetValue(source, out var last) && now - last < DiscoveryUpsertThrottle)
                return;
            // Bound the dict: an entry older than the throttle window no longer gates
            // anything, so drop stale ones (also caps memory if the channel injects
            // many distinct callsigns). Cheap — runs only when we're about to upsert.
            if (_lastDiscoveryUpsert.Count > 0)
            {
                var cutoff = now - DiscoveryUpsertThrottle;
                foreach (var k in _lastDiscoveryUpsert.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _lastDiscoveryUpsert.Remove(k);
            }
            _lastDiscoveryUpsert[source] = now;
        }

        var peer = new DbDiscoveredPeer
        {
            Callsign = source,
            Bearer = "meshcore",
            ChannelKey = opts.ChannelName,
            ChannelId = 0,
            LinkClass = LinkClass.MeshCore,
            CostHint = LinkClassDefaults.CostHint(LinkClass.MeshCore),
            Hops = 1,
            TtlSeconds = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.MeshCore),
            MeshCoreChannel = opts.ChannelName,
            LastSeen = now,
        };
        try
        {
            await _database.UpsertDiscoveredPeer(peer);
            _log.LogInformation("MeshCore: discovered peer {0} on {1} (cost={2}, ttl={3}s)",
                source, opts.ChannelName, peer.CostHint, peer.TtlSeconds);
        }
        catch (Exception ex)
        {
            // Keep the throttle stamp: on a persistent DB fault, rolling it back would
            // let every subsequent frame from this peer re-attempt (and re-fail) the
            // upsert, defeating the 30s rate-limit exactly when the DB is unhealthy. A
            // retry still happens on the next frame after the window, which is enough.
            _log.LogWarning("MeshCore: failed to record discovered peer {0}: {1}", source, ex.Message);
        }
    }

    public bool CanHandle(BackhaulRoute route) =>
        Enabled && _backhaul is not null && _backhaul.CanHandle(route);

    public Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct) =>
        _backhaul?.SendAsync(message, route, localCallsign, ct)
        ?? Task.FromResult(BackhaulSendResult.Fail("meshcore bearer not started"));

    private static MeshCoreBearerOptions BuildOptions(SystemOptions s) => new()
    {
        Enabled = true,
        SerialPort = s.MeshCorePort,
        Region = s.MeshCoreRegion,
        TxPowerDbm = (byte)Math.Clamp(s.MeshCoreTxPowerDbm, 0, 30),
        ChannelIndex = (byte)Math.Clamp(s.MeshCoreChannelIndex, 0, 255),
        ChannelName = s.MeshCoreChannelName,
        ChannelPsk = s.MeshCoreChannelPsk,
        NodeName = s.MeshCoreNodeName,
        AirtimeBudgetSecPerHour = s.MeshCoreAirtimeBudgetSecondsPerHour,
        Compress = s.MeshCoreCompress,
        CongestionBackoffFraction = s.MeshCoreCongestionBackoffFraction,
        LbtGuardMs = s.MeshCoreLbtGuardMs,
        ReliableDelivery = s.MeshCoreReliableDelivery,
        LocalCallsign = s.Callsign,
        AppName = "dapps",
    };

    public async ValueTask DisposeAsync()
    {
        if (_link is not null) await _link.DisposeAsync();
    }
}

/// <summary>Read-only MeshCore bearer status for the dashboard / API.</summary>
public sealed record MeshCoreStatus(
    bool Enabled, string LinkState, double? FreqMhz, byte? Sf, byte? Cr, int Resets,
    double Occupancy, long Delivered, int ReliablePending, long ReliableConfirmed, long ReliableExpired);
