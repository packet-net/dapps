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

    public bool Enabled { get; private set; }
    public MeshCoreLink? Link => _link;
    public MeshCoreInbound? Inbound => _inbound;

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

        _backhaul = new MeshCoreCompanionBackhaul(
            _link, opts, budget, _loggerFactory.CreateLogger<MeshCoreCompanionBackhaul>(), _txGate);
        _inbound = new MeshCoreInbound(_link, _inbox, _loggerFactory.CreateLogger<MeshCoreInbound>());
        Enabled = true;

        await _inbound.RunAsync(ct);
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
        AppName = "dapps",
    };

    public async ValueTask DisposeAsync()
    {
        if (_link is not null) await _link.DisposeAsync();
    }
}
