using dapps.client.Backhaul;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.meshcore;

/// <summary>
/// <see cref="IDappsBackhaul"/> over a MeshCore private channel (Phase H1, #154).
/// Carries a <see cref="BackhaulMessage"/> as binary channel-data datagrams:
/// stamps the link source, compresses, fragments, and broadcasts — gated by the
/// TX kill-switch and the airtime governor.
///
/// A private channel is one shared broadcast medium, so a message addressed to a
/// specific node is sent ONCE to the channel and the addressee self-selects (the
/// inbox's IsLocal gate). To stay a good citizen when the router offers the same
/// message for several MeshCore neighbours, identical message ids are coalesced
/// within a short window so we don't re-broadcast (#155).
/// </summary>
public sealed class MeshCoreCompanionBackhaul : IDappsBackhaul
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FramePace = TimeSpan.FromMilliseconds(1000);

    private readonly MeshCoreLink _link;
    private readonly MeshCoreBearerOptions _opts;
    private readonly TxBudget _budget;
    private readonly IDappsTxGate _txGate;
    private readonly RegionPreset _region;
    private readonly ILogger _log;
    private readonly MeshCoreChannelTransport _tx = new();
    private readonly ChannelMonitor _monitor;
    private readonly MeshCoreReliability? _reliability;
    private readonly double _congestionThreshold;
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly object _recentLock = new();

    public MeshCoreCompanionBackhaul(
        MeshCoreLink link, MeshCoreBearerOptions opts, TxBudget budget, ILogger log,
        IDappsTxGate? txGate = null, MeshCoreReliability? reliability = null)
    {
        _link = link;
        _opts = opts;
        _budget = budget;
        _log = log;
        _txGate = txGate ?? AlwaysOpenTxGate.Instance;
        _reliability = reliability;
        _region = opts.ResolveRegion();
        _monitor = new ChannelMonitor(_region);
        link.PacketHeard += (len, _) => _monitor.RecordHeard(len, DateTime.UtcNow);
        // Per-node jitter (±15%) on the congestion threshold so two contending
        // nodes don't back off in lockstep — fairer channel sharing (#157).
        _congestionThreshold = Math.Clamp(opts.CongestionBackoffFraction * (0.85 + 0.30 * Random.Shared.NextDouble()), 0, 1);
    }

    /// <summary>Trailing-window channel occupancy (0..1), for observability.</summary>
    public double Occupancy => _monitor.OccupancyFraction(DateTime.UtcNow);

    /// <summary>Handle routes that carry a MeshCore channel hint (positive
    /// bearer selection — no reliance on AGW's "no UDP endpoint" catch-all).</summary>
    public bool CanHandle(BackhaulRoute route) => !string.IsNullOrEmpty(route.MeshCoreChannel);

    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
    {
        var result = await SendCoreAsync(message, localCallsign, coalesce: true, ct);
        // Reliable delivery (#26): track a successfully-sent data message so we
        // resend it until the destination ACKs (or its lifetime expires).
        if (result.Accepted && _reliability is not null && !MeshCoreReliability.IsAck(message))
            _reliability.Track(message, localCallsign, DateTime.UtcNow);
        return result;
    }

    /// <summary>Re-broadcast a message (a reliability resend, or an ACK) bypassing
    /// the broadcast-coalescing dedup — still gated by the governor + adaptive
    /// controls. Not tracked for reliability.</summary>
    public Task<BackhaulSendResult> ResendAsync(BackhaulMessage message, string localCallsign, CancellationToken ct)
        => SendCoreAsync(message, localCallsign, coalesce: false, ct);

    private async Task<BackhaulSendResult> SendCoreAsync(
        BackhaulMessage message, string localCallsign, bool coalesce, CancellationToken ct)
    {
        if (!_txGate.TxAllowed)
            return BackhaulSendResult.Fail($"tx-stopped: {_txGate.BlockReason ?? "(no reason)"}");

        // Broadcast coalescing: a channel send reaches every member, so the same
        // message offered for multiple neighbours need only go on air once.
        // Resends deliberately bypass this (coalesce=false).
        if (coalesce && AlreadyBroadcast(message.Id))
            return BackhaulSendResult.Ok();

        // Adaptive congestion backoff (#157): don't pile onto a busy shared channel.
        var occ = _monitor.OccupancyFraction(DateTime.UtcNow);
        if (_opts.CongestionBackoffFraction > 0 && occ >= _congestionThreshold)
            return BackhaulSendResult.Fail($"channel congested {occ:P0} (>= {_congestionThreshold:P0}); backing off");

        var stamped = message with { LinkSourceCallsign = localCallsign };
        var mode = _opts.Compress ? DappsCompression.Mode.ZstdDict : DappsCompression.Mode.None;
        var frames = _tx.ToFrames(stamped, mode);

        for (var i = 0; i < frames.Count; i++)
        {
            var airMs = LoRaAirtime.FrameMs(frames[i].Length, _region);
            if (!_budget.TryReserve(airMs, DateTime.UtcNow, out var reason, out var token))
                return BackhaulSendResult.Fail(reason);

            await ListenBeforeTalkAsync(ct);   // avoid colliding with an in-progress flood

            bool sent;
            try { sent = await _link.SendDataAsync(frames[i], ct); }
            catch (Exception ex) { _budget.Refund(token); return BackhaulSendResult.Fail($"meshcore send failed: {ex.Message}"); }
            if (!sent) { _budget.Refund(token); return BackhaulSendResult.Fail($"meshcore link not ready ({_link.State})"); }

            if (i < frames.Count - 1) { try { await Task.Delay(FramePace, ct); } catch { } }
        }

        if (coalesce) MarkBroadcast(message.Id);
        _log.LogInformation("MeshCore: {0} {1} ({2} frame(s)) dst={3} from={4} duty={5:0.00}% occ={6:0.0}%",
            coalesce ? "broadcast" : "resend", message.Id, frames.Count, message.Destination, localCallsign,
            _budget.DutyPercent(DateTime.UtcNow), occ * 100);
        return BackhaulSendResult.Ok();
    }

    /// <summary>Listen-before-talk: if a packet was overheard within the guard,
    /// wait out the remainder plus a little jitter before transmitting.</summary>
    private async Task ListenBeforeTalkAsync(CancellationToken ct)
    {
        if (_opts.LbtGuardMs <= 0) return;
        var since = _monitor.SinceLastHeard(DateTime.UtcNow);
        var guard = TimeSpan.FromMilliseconds(_opts.LbtGuardMs);
        if (since < guard)
        {
            var wait = guard - since + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 150));
            try { await Task.Delay(wait, ct); } catch { }
        }
    }

    private bool AlreadyBroadcast(string id)
    {
        lock (_recentLock)
        {
            Prune();
            return _recent.ContainsKey(id);
        }
    }

    private void MarkBroadcast(string id)
    {
        lock (_recentLock) { _recent[id] = DateTime.UtcNow; }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - CoalesceWindow;
        foreach (var k in _recent.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _recent.Remove(k);
    }
}
