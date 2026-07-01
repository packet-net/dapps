using System.Collections.Concurrent;
using System.Text;
using dapps.client.Backhaul;
using dapps.meshcore;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.meshcore.sim;

/// <summary>
/// A full DAPPS MeshCore node running on a <see cref="MeshFabric"/> leaf: the REAL
/// <see cref="MeshCoreCompanionBackhaul"/> + <see cref="MeshCoreInbound"/> (+ reliability)
/// wired to a <see cref="SimulatedMeshCoreLink"/>, exactly as the host wires them to a
/// serial link. Lets a scenario exercise the actual transport / dedup / reliability /
/// discovery code over a multi-hop virtual mesh.
/// </summary>
public sealed class MeshDappsNode
{
    private readonly SimulatedMeshCoreLink _link;
    private readonly MeshCoreCompanionBackhaul _backhaul;
    private readonly MeshCoreInbound _inbound;
    private readonly MeshCoreReliability? _reliability;
    private readonly RecordingInbox _inbox;
    private readonly string _channel;
    private readonly TimeSpan _resendPoll;
    private readonly ConcurrentDictionary<string, int> _discovered = new(StringComparer.OrdinalIgnoreCase);

    public string Callsign { get; }

    /// <summary>Distinct (id) messages delivered to this node's app.</summary>
    public long Delivered => _inbox.Delivered;
    /// <summary>True if a message with this sequence number was delivered (addressed to us).</summary>
    public bool GotSeq(int seq) => _inbox.HasSeq(seq);
    public int DistinctSeqs => _inbox.DistinctSeqs;
    /// <summary>Peers this node learned purely by hearing their traffic (passive discovery).</summary>
    public IReadOnlyCollection<string> DiscoveredPeers => _discovered.Keys.ToList();

    /// <param name="reliabilityOptions">Override the ACK/resend timings. Null uses the
    /// production defaults (20 s base backoff); an accelerated profile lets a loss-recovery
    /// scenario run in CI-time instead of minutes.</param>
    /// <param name="resendPoll">How often the resend loop checks for due retransmits.
    /// Should be no slower than the backoff, or it becomes the bottleneck.</param>
    public MeshDappsNode(MeshFabric fabric, string callsign, string channel = "dapps-sim", bool reliable = true, string scope = "",
        MeshCoreReliability.Options? reliabilityOptions = null, TimeSpan? resendPoll = null)
    {
        Callsign = callsign;
        _channel = channel;
        _resendPoll = resendPoll ?? TimeSpan.FromSeconds(2);
        _link = fabric.AddLeaf(callsign, scope);
        var opts = new MeshCoreBearerOptions
        {
            Region = "uk-test", ChannelName = channel, ChannelIndex = 1,
            LocalCallsign = callsign, NodeName = callsign, ReliableDelivery = reliable,
            LbtGuardMs = 0,          // no radio timing to wait on in the sim
            AirtimeBudgetSecPerHour = 3600,   // don't let the governor gate the scenario
            // Propagation is instantaneous here, so the occupancy estimate (from overheard
            // packet airtime over wall-clock) is an artifact - disable congestion backoff
            // so it can't confound reliability-recovery scenarios by refusing resends.
            CongestionBackoffFraction = 0,
        };
        _reliability = reliable ? new MeshCoreReliability(reliabilityOptions) : null;
        _inbox = new RecordingInbox(callsign);
        _backhaul = new MeshCoreCompanionBackhaul(_link, opts, new TxBudget(opts.AirtimeBudgetSecPerHour),
            NullLogger.Instance, reliability: _reliability);
        _inbound = new MeshCoreInbound(
            _link, _inbox, NullLogger.Instance, _reliability,
            sendAck: (ack, c) => _backhaul.ResendAsync(ack, callsign, c),
            localCallsign: callsign,
            onPeerHeard: (src, c) => { _discovered.AddOrUpdate(src, 1, (_, n) => n + 1); return Task.CompletedTask; });
    }

    /// <summary>Run the inbound drain loop and (if reliable) the resend loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var resend = _reliability is not null ? Task.Run(() => ResendLoopAsync(ct), ct) : Task.CompletedTask;
        try { await _inbound.RunAsync(ct); }
        finally { try { await resend; } catch { } }
    }

    /// <summary>Originate a sequence-numbered message to <paramref name="peer"/>.</summary>
    public Task<BackhaulSendResult> SendAsync(string peer, int seq, string text, CancellationToken ct)
    {
        var msg = new BackhaulMessage(
            Id: Guid.NewGuid().ToString("N")[..7], Destination: peer, Salt: null, Ttl: 3600,
            Payload: Encoding.UTF8.GetBytes(text), Originator: Callsign, LinkSourceCallsign: Callsign,
            Headers: new Dictionary<string, string> { ["seq"] = seq.ToString() });
        return _backhaul.SendAsync(msg, new BackhaulRoute(peer, MeshCoreChannel: _channel), Callsign, ct);
    }

    private async Task ResendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_resendPoll, ct); } catch { break; }
            var now = DateTime.UtcNow;
            _reliability!.DropExpired(now);
            foreach (var (m, local) in _reliability.DueResends(now))
            {
                try { if ((await _backhaul.ResendAsync(m, local, ct)).Accepted) _reliability.MarkResent(m.Id, DateTime.UtcNow); }
                catch { }
            }
        }
    }

    private sealed class RecordingInbox(string self) : IBackhaulInbox
    {
        private readonly object _l = new();
        private readonly HashSet<int> _seqs = new();
        private long _delivered;

        public long Delivered { get { lock (_l) return _delivered; } }
        public int DistinctSeqs { get { lock (_l) return _seqs.Count; } }
        public bool HasSeq(int seq) { lock (_l) return _seqs.Contains(seq); }

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            // Broadcast medium: only count messages actually addressed to us.
            if (!string.Equals(message.Destination, self, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            lock (_l)
            {
                _delivered++;
                if (message.Headers is not null && message.Headers.TryGetValue("seq", out var sv) && int.TryParse(sv, out var s))
                    _seqs.Add(s);
            }
            return Task.CompletedTask;
        }
    }
}
