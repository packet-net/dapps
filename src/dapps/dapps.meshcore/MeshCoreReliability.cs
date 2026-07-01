using dapps.client.Backhaul;

namespace dapps.meshcore;

/// <summary>
/// End-to-end reliability for the MeshCore bearer (#26). Channel messages are
/// fire-and-forget floods with no link-layer ACK and significant loss, so DAPPS
/// adds its own: the receiver ACKs any data message addressed to it (a small
/// control message carrying the acked id in the <see cref="AckHeader"/>), and the
/// sender tracks each unacked message and resends it on an exponential backoff
/// until it is acked or its lifetime expires.
///
/// This manager is pure bookkeeping (no I/O); the bearer drives it: it calls
/// <see cref="Track"/> on send, <see cref="OnAck"/> when an ACK arrives, and a
/// loop polls <see cref="DueResends"/> / <see cref="DropExpired"/>. Thread-safe.
/// </summary>
public sealed class MeshCoreReliability
{
    /// <summary>Header key carrying the acked message id. Its presence marks a
    /// message as an ACK (control), not app data.</summary>
    public const string AckHeader = "mc-ack";

    public sealed record Options(TimeSpan BaseBackoff, double Multiplier, TimeSpan MaxBackoff, TimeSpan MaxLifetime)
    {
        public static Options Default => new(TimeSpan.FromSeconds(20), 1.6, TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(5));
    }

    private sealed class Pending
    {
        public required BackhaulMessage Message;
        public required string LocalCallsign;
        public DateTime DeadlineUtc;
        public DateTime NextResendUtc;
        public int Attempts;
    }

    private readonly Options _opts;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public long Confirmed { get; private set; }
    public long Expired { get; private set; }
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    public MeshCoreReliability(Options? opts = null) => _opts = opts ?? Options.Default;

    public static bool IsAck(BackhaulMessage m) => m.Headers is not null && m.Headers.ContainsKey(AckHeader);

    public static string? AckedId(BackhaulMessage m) =>
        m.Headers is not null && m.Headers.TryGetValue(AckHeader, out var v) ? v : null;

    /// <summary>Build the ACK control message for a received data message.</summary>
    public static BackhaulMessage BuildAck(BackhaulMessage received, string localCallsign, string ackId) => new(
        Id: ackId,
        Destination: received.Originator ?? received.LinkSourceCallsign ?? "MESHCORE",
        Salt: null,
        Ttl: 60,
        Payload: [],
        Originator: localCallsign,
        LinkSourceCallsign: localCallsign,
        Headers: new Dictionary<string, string> { [AckHeader] = received.Id });

    /// <summary>Register a sent data message as awaiting an ACK (no-op for ACKs).</summary>
    public void Track(BackhaulMessage m, string localCallsign, DateTime now)
    {
        if (IsAck(m)) return;
        var lifetime = _opts.MaxLifetime;
        if (m.Ttl is int t and > 0)
            lifetime = TimeSpan.FromSeconds(Math.Min(t, _opts.MaxLifetime.TotalSeconds));
        lock (_lock)
        {
            _pending[m.Id] = new Pending
            {
                Message = m,
                LocalCallsign = localCallsign,
                DeadlineUtc = now + lifetime,
                NextResendUtc = now + _opts.BaseBackoff,
                Attempts = 0,
            };
        }
    }

    /// <summary>Mark a message confirmed delivered. Returns true if it was pending.</summary>
    public bool OnAck(string ackedId)
    {
        lock (_lock)
        {
            if (_pending.Remove(ackedId)) { Confirmed++; return true; }
            return false;
        }
    }

    /// <summary>Messages currently due for a resend. Does NOT advance backoff — call
    /// <see cref="MarkResent"/> only after a resend actually goes on air, so a refused
    /// resend (congestion / budget) doesn't burn a retransmit slot.</summary>
    public List<(BackhaulMessage message, string localCallsign)> DueResends(DateTime now)
    {
        var due = new List<(BackhaulMessage, string)>();
        lock (_lock)
            foreach (var p in _pending.Values)
                if (now >= p.NextResendUtc && now < p.DeadlineUtc)
                    due.Add((p.Message, p.LocalCallsign));
        return due;
    }

    /// <summary>Record that a message was actually re-sent: advance its attempt count
    /// and next-resend time (exponential backoff).</summary>
    public void MarkResent(string id, DateTime now)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var p)) return;
            p.Attempts++;
            var backoffMs = Math.Min(
                _opts.BaseBackoff.TotalMilliseconds * Math.Pow(_opts.Multiplier, p.Attempts),
                _opts.MaxBackoff.TotalMilliseconds);
            p.NextResendUtc = now + TimeSpan.FromMilliseconds(backoffMs);
        }
    }

    /// <summary>Remove and return messages whose lifetime elapsed unacked (gave up).</summary>
    public List<BackhaulMessage> DropExpired(DateTime now)
    {
        lock (_lock)
        {
            var expired = _pending.Where(kv => now >= kv.Value.DeadlineUtc).ToList();
            foreach (var e in expired) _pending.Remove(e.Key);
            Expired += expired.Count;
            return expired.Select(e => e.Value.Message).ToList();
        }
    }
}
