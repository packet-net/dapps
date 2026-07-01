using dapps.client.Backhaul;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MeshCoreBearer> _log;

    private MeshCoreLink? _link;
    private MeshCoreCompanionBackhaul? _backhaul;
    private MeshCoreInbound? _inbound;
    private MeshCoreReliability? _reliability;

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
        ILoggerFactory loggerFactory)
    {
        _sysOpts = sysOpts;
        _inbox = inbox;
        _txGate = txGate;
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
            localCallsign: opts.LocalCallsign);
        Enabled = true;

        // Reliability resend loop runs alongside the inbound drain loop.
        var resendTask = reliability is not null
            ? Task.Run(() => ResendLoopAsync(reliability, backhaul, ct), ct)
            : Task.CompletedTask;
        try { await _inbound.RunAsync(ct); }
        finally { try { await resendTask; } catch { /* shutdown */ } }
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
