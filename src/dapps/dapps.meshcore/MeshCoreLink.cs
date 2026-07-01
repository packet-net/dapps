using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace dapps.meshcore;

/// <summary>
/// Owns the serial <see cref="MeshCoreClient"/> and keeps it alive: opens and
/// configures the radio (region params, TX power, name, channel), then runs a
/// watchdog that detects a hung/mute companion and recovers it by hard-resetting
/// the ESP32 over CP2102 DTR/RTS, re-opening, and re-applying config (#160).
///
/// Outbound (<see cref="SendDataAsync"/>) and inbound (<see cref="DrainAsync"/>)
/// callers see the link's current state; during a reset they get a soft failure
/// (send returns false, drain returns null) and retry once the link is back.
/// </summary>
public sealed class MeshCoreLink : IAsyncDisposable, IMeshCoreLink
{
    public enum LinkState { Down, Healthy, Resetting, Failed }

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleProbeAfter = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan FailedRetryBackoff = TimeSpan.FromSeconds(30);

    private readonly MeshCoreBearerOptions _opts;
    private readonly RegionPreset _region;
    private readonly byte[] _psk;
    private readonly byte[]? _floodScope;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MeshCoreClient? _client;
    private Task? _watchdog;
    private CancellationTokenSource? _cts;
    private DateTime _nextFailedRetry = DateTime.MinValue;

    public LinkState State { get; private set; } = LinkState.Down;
    public int ResetCount { get; private set; }
    public SelfInfo? Self { get; private set; }
    public event Action? MessageWaiting;
    public event Action<int, sbyte>? PacketHeard;

    public MeshCoreLink(MeshCoreBearerOptions opts, ILogger log)
    {
        _opts = opts;
        _log = log;
        _region = opts.ResolveRegion();
        _psk = opts.ResolvePsk();
        _floodScope = opts.ResolveFloodScopeKey();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await OpenAndConfigureAsync(_cts.Token);
        _watchdog = Task.Run(() => WatchdogLoopAsync(_cts.Token));
    }

    private async Task OpenAndConfigureAsync(CancellationToken ct)
    {
        var client = new MeshCoreClient(_opts.SerialPort);
        client.MessageWaiting += () => MessageWaiting?.Invoke();
        client.PacketHeard += (len, snr) => PacketHeard?.Invoke(len, snr);

        SelfInfo self;
        bool scopeApplied = false;
        try
        {
            client.Open();
            await client.AppStartAsync(_opts.AppName, ct);
            await client.SetRadioParamsAsync(_region.FreqMhz, _region.BwKhz, _region.Sf, _region.Cr, ct);
            await client.SetTxPowerAsync(Math.Min(_opts.TxPowerDbm, _region.MaxPowerDbm), ct);
            await client.SetNameAsync(_opts.NodeName, ct);
            await client.SetChannelAsync(_opts.ChannelIndex, _opts.ChannelName, _psk, ct);
            // Deployment model B: apply (or clear) the flood-scope override every configure
            // - it's RAM-only and reset on the radio reboots our watchdog triggers.
            scopeApplied = await client.SetFloodScopeAsync(_floodScope, ct);
            if (_floodScope is not null)
            {
                if (scopeApplied)
                    _log.LogInformation("MeshCore: flood-scope applied (model B) - floods contained to nodes sharing the scope");
                else
                    _log.LogWarning("MeshCore: radio rejected the flood-scope key - firmware too old to scope floods; "
                        + "traffic will flood UNSCOPED (model A) despite MeshCoreFloodScopeKey being set");
            }
            self = await client.AppStartAsync(_opts.AppName, ct);
        }
        catch
        {
            // Configuration failed after the port was opened / read loop started:
            // dispose the local client so we don't leak the port + read-loop task.
            try { await client.DisposeAsync(); } catch { }
            throw;
        }

        _client = client;
        Self = self;
        State = LinkState.Healthy;
        // Report the EFFECTIVE model: if scoping (B) was requested but the radio rejected
        // the key, the traffic is actually unscoped, so don't headline it as B.
        var model = _opts.DeploymentModel();
        if (model.StartsWith("B", StringComparison.Ordinal) && !scopeApplied)
            model = "A (unscoped - firmware rejected the scope key)";
        _log.LogInformation(
            "MeshCore link up: {0} {1:0.000}MHz/{2:0.#}kHz/SF{3}/CR{4} ch[{5}]='{6}' node='{7}' txp={8}dBm model={9}",
            self.PublicKeyHex[..12], self.FreqMhz, self.BwKhz, self.Sf, self.Cr,
            _opts.ChannelIndex, _opts.ChannelName, _opts.NodeName, self.TxPower, model);
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(WatchdogInterval, ct); }
            catch (OperationCanceledException) { break; }

            if (State == LinkState.Healthy)
            {
                var client = _client;
                if (client is null) continue;
                if (DateTime.UtcNow - client.LastOkUtc < IdleProbeAfter) continue; // traffic flowing
                try { await client.AppStartAsync(_opts.AppName, ct); }              // idle liveness probe
                catch (Exception ex)
                {
                    _log.LogWarning("MeshCore liveness probe failed: {0}; recovering", ex.Message);
                    await RecoverAsync(ct);
                }
            }
            else if (State is LinkState.Failed or LinkState.Down && DateTime.UtcNow >= _nextFailedRetry)
            {
                if (!await RecoverAsync(ct)) _nextFailedRetry = DateTime.UtcNow + FailedRetryBackoff;
            }
        }
    }

    /// <summary>One reset + reconfigure attempt. Returns whether the link is back.</summary>
    public async Task<bool> RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            State = LinkState.Resetting;
            var old = _client;
            _client = null;
            if (old is not null) { try { await old.DisposeAsync(); } catch { } }
            try { await Task.Delay(300, ct); } catch { } // let the OS release the port

            try
            {
                HardReset(_opts.SerialPort, _log);
                await Task.Delay(1500, ct);            // boot
                await OpenAndConfigureAsync(ct);
                ResetCount++;
                _log.LogInformation("MeshCore link recovered (reset #{0})", ResetCount);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning("MeshCore recovery failed: {0}", ex.Message);
                if (_client is not null) { try { await _client.DisposeAsync(); } catch { } _client = null; }
                State = LinkState.Failed;
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hard-reset the ESP32 over CP2102 DTR/RTS (no buttons): pulse EN via
    /// RTS while IO0 (DTR) stays high → boots firmware (not the ROM bootloader).</summary>
    public static void HardReset(string portName, ILogger log)
    {
        using var p = new SerialPort(portName, 115200);
        p.Open();
        p.DtrEnable = false;   // IO0 high → run firmware
        p.RtsEnable = true;    // EN low → reset
        Thread.Sleep(120);
        p.RtsEnable = false;   // EN high → boot
        p.Close();
        log.LogInformation("MeshCore: hard-reset {0} via DTR/RTS", portName);
    }

    /// <summary>Send one channel-data datagram. False if the link is mid-recovery.</summary>
    public async Task<bool> SendDataAsync(byte[] payload, CancellationToken ct)
    {
        var client = _client;
        if (client is null || State != LinkState.Healthy) return false;
        try
        {
            await client.SendChannelDataAsync(_opts.ChannelIndex, payload, MeshCoreClient.DATA_TYPE_DEV, ct);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // A concurrent recovery disposed the client between our State check and
            // the write; treat as a soft failure so the caller retries.
            return false;
        }
    }

    /// <summary>Drain queued inbound messages, or null if the link is unavailable.</summary>
    public async Task<MeshCoreClient.InboundBatch?> DrainAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return null;
        try { return await client.DrainAsync(ct); }
        catch (Exception ex) { _log.LogDebug("MeshCore drain error: {0}", ex.Message); return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_watchdog is not null) { try { await _watchdog; } catch { } }
        if (_client is not null) { try { await _client.DisposeAsync(); } catch { } }
        _cts?.Dispose();
    }
}
