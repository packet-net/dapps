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
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly object _recentLock = new();

    public MeshCoreCompanionBackhaul(
        MeshCoreLink link, MeshCoreBearerOptions opts, TxBudget budget, ILogger log, IDappsTxGate? txGate = null)
    {
        _link = link;
        _opts = opts;
        _budget = budget;
        _log = log;
        _txGate = txGate ?? AlwaysOpenTxGate.Instance;
        _region = opts.ResolveRegion();
    }

    /// <summary>Handle routes that carry a MeshCore channel hint (positive
    /// bearer selection — no reliance on AGW's "no UDP endpoint" catch-all).</summary>
    public bool CanHandle(BackhaulRoute route) => !string.IsNullOrEmpty(route.MeshCoreChannel);

    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
    {
        if (!_txGate.TxAllowed)
            return BackhaulSendResult.Fail($"tx-stopped: {_txGate.BlockReason ?? "(no reason)"}");

        // Broadcast coalescing: a channel send reaches every member, so the same
        // message offered for multiple neighbours need only go on air once.
        if (AlreadyBroadcast(message.Id))
            return BackhaulSendResult.Ok();

        var stamped = message with { LinkSourceCallsign = localCallsign };
        var mode = _opts.Compress ? DappsCompression.Mode.ZstdDict : DappsCompression.Mode.None;
        var frames = _tx.ToFrames(stamped, mode);

        for (var i = 0; i < frames.Count; i++)
        {
            var airMs = LoRaAirtime.FrameMs(frames[i].Length, _region);
            if (!_budget.TryReserve(airMs, DateTime.UtcNow, out var reason))
                return BackhaulSendResult.Fail(reason);

            bool sent;
            try { sent = await _link.SendDataAsync(frames[i], ct); }
            catch (Exception ex) { return BackhaulSendResult.Fail($"meshcore send failed: {ex.Message}"); }
            if (!sent) return BackhaulSendResult.Fail($"meshcore link not ready ({_link.State})");

            if (i < frames.Count - 1) { try { await Task.Delay(FramePace, ct); } catch { } }
        }

        MarkBroadcast(message.Id);
        _log.LogInformation("MeshCore: broadcast {0} ({1} frame(s)) dst={2} from={3} duty={4:0.00}%",
            message.Id, frames.Count, message.Destination, localCallsign, _budget.DutyPercent(DateTime.UtcNow));
        return BackhaulSendResult.Ok();
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
