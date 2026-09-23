namespace dapps.core.Services;

/// <summary>
/// Which peers this node currently has a DAPPS session open with, in
/// either direction, so nothing dials a peer that is already talking
/// to us.
///
/// Why this exists (#178): AX.25 allows one link per (our call, their
/// call, port), whichever end set it up. Two DAPPS nodes that list each
/// other as neighbours routinely have traffic queued for each other at
/// the same moment, and each one's forwarder dialled out on its own
/// tick with no idea an inbound session from that very peer was open.
/// BPQ does not refuse the outbound connect: its AGW 'C' handling
/// allocates a fresh stream and issues a node-level connect with no
/// look at existing links, so a new SABM goes down the live link. The
/// far end's BPQ treats a SABM on an established link as a link reset
/// (L2Code.c, "SABM ON EXISTING SESSION"): it disconnects whatever was
/// attached, which is the DAPPS session, and re-attaches the link at
/// the node's command level, so the caller gets the node's welcome
/// banner where it expected DAPPSv1>. Both sides fail, requeue, redial
/// on the same cadence and collide again for as long as both queues
/// stay non-empty.
///
/// The inbound bearers register a session when the node hands it to
/// us and release it when the handler finishes; the outbound transport
/// registers before it dials and releases when the link is torn down.
/// <see cref="OutboundMessageManager"/> asks <see cref="IsActive"/> and
/// leaves a message queued for the next tick rather than dial into a
/// live session; if the peer has opportunistic poll on, its <c>rev</c>
/// on that session drains our queue for it anyway.
///
/// Keyed on the peer's full callsign (SSID included), case-insensitive:
/// the identity the inbound 'C' frame and the neighbour table share.
/// Reference-counted so an entry being retired while its replacement
/// registers (rule 2 in <see cref="AgwInboundService"/>) never reads as
/// idle in between. A session whose peer vanished without a disconnect
/// holds its lease until the handler's inactivity timeout (3 min) or
/// the node's own link timeout ends it, whichever comes first; the
/// cost is latency on the next dial to that peer, not lost traffic.
/// </summary>
public sealed class PeerSessionRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, List<Lease>> open = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Marks a session with <paramref name="peerCallsign"/> as
    /// open until the returned lease is disposed. <paramref name="direction"/>
    /// is "inbound" or "outbound", for the log line when a dial is
    /// deferred. Disposing a lease twice is harmless.</summary>
    public IDisposable Acquire(string peerCallsign, string direction)
    {
        var lease = new Lease(this, peerCallsign, direction);
        lock (gate)
        {
            if (!open.TryGetValue(peerCallsign, out var leases))
            {
                leases = [];
                open[peerCallsign] = leases;
            }
            leases.Add(lease);
            Signal();
        }
        return lease;
    }

    /// <summary>True while at least one session with the peer is open.
    /// <paramref name="direction"/> is that of the longest-open one.</summary>
    public bool IsActive(string peerCallsign, out string? direction)
    {
        lock (gate)
        {
            if (open.TryGetValue(peerCallsign, out var leases) && leases.Count > 0)
            {
                direction = leases[0].Direction;
                return true;
            }
        }
        direction = null;
        return false;
    }

    /// <summary>Completes once no session with the peer is open;
    /// immediately if none is. Exposed for tests, which otherwise have
    /// no sleep-free way to wait for a handler's teardown to release
    /// its lease.</summary>
    internal async Task WaitUntilIdleAsync(string peerCallsign, CancellationToken ct)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (!open.TryGetValue(peerCallsign, out var leases) || leases.Count == 0) return;
                next = changed.Task;
            }
            await next.WaitAsync(ct);
        }
    }

    private void Release(Lease lease)
    {
        lock (gate)
        {
            if (!open.TryGetValue(lease.Peer, out var leases) || !leases.Remove(lease)) return;
            if (leases.Count == 0) open.Remove(lease.Peer);
            Signal();
        }
    }

    /// <summary>Wakes any <see cref="WaitUntilIdleAsync"/>; called under <see cref="gate"/>.</summary>
    private void Signal()
    {
        var previous = changed;
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private sealed class Lease(PeerSessionRegistry owner, string peer, string direction) : IDisposable
    {
        public string Peer { get; } = peer;
        public string Direction { get; } = direction;

        public void Dispose() => owner.Release(this);
    }
}
