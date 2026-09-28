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
/// look at existing links, so a new SABM goes down the live link, and
/// this end's link now belongs to the new call's socket. What the far
/// end's BPQ does with a SABM on a link it already has (L2Code.c, "SABM
/// ON EXISTING SESSION") depends on whether it has had an I-frame on
/// that link yet. If not, it takes the SABM for a repeat and just
/// answers UA again, keeping its session. If it has, as with any DAPPS
/// session (the answering end sends its prompt straight away), it takes
/// the SABM as a reset: the session attached there gets a 'd', nothing
/// new is handed to the DAPPS listener, and whatever arrives next on the
/// link goes to the node's command prompt (in the field, the node's
/// welcome banner where the caller expected DAPPSv1>). Both nodes seen
/// in the experiments, docs-internal/end-to-end-tests.md.
///
/// The inbound bearers register a session when the node hands it to
/// us and release it when the handler finishes; the outbound transport
/// registers before it dials and releases when the link is torn down.
/// <see cref="OutboundMessageManager"/> asks <see cref="IsActive"/> and
/// never dials into a live session: it hands the traffic to that session
/// instead once it is established (one we dialled for traffic, or one
/// in <see cref="SessionDirectory"/>), or leaves it
/// queued. The last session with a peer ending wakes the
/// forwarder, so anything still queued goes straight away.
///
/// One session per peer and port (phase 2 of the exchange plan): the node
/// can still end up with two, as when both ends dial at the same moment,
/// or our dial goes out just as the peer's call reaches our listener and
/// BPQ moves the link to our new socket. The newest connected session is the
/// one the link belongs to, so whenever a session becomes connected (an
/// inbound connect, or our dial confirmed), every older connected
/// session with that peer on the same port is retired: its lease's
/// <see cref="PeerSessionLease.Retired"/> fires, and its owner stops it
/// without sending a disconnect (BPQ applies an app's disconnect by
/// callsign pair, and would take the link from the newer session). A
/// session on another port is another link, and is left alone. Only AGW
/// sessions take part, as only AGW has been measured: a lease with no
/// port (RHPv2) never retires anything and is never retired.
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
public sealed class PeerSessionRegistry(ForwarderWakeup? forwarderWakeup = null)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, List<PeerSessionLease>> open = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Marks a session with <paramref name="peerCallsign"/> as
    /// open, and connected, until the returned lease is disposed: what an
    /// inbound bearer does when the node hands it a connect. Any older
    /// connected session with the peer on <paramref name="linkPort"/> is
    /// retired. <paramref name="direction"/> is "inbound" or "outbound",
    /// for the log line when a dial is deferred. Disposing a lease twice
    /// is harmless.</summary>
    /// <param name="linkPort">The AGW port the link is on; null for a
    /// bearer that takes no part in retirement.</param>
    public PeerSessionLease Acquire(string peerCallsign, string direction, int? linkPort = null)
    {
        var lease = new PeerSessionLease(this, peerCallsign, direction, linkPort);
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
        Connected(lease);
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

    /// <summary>Atomically acquires a lease only if no session with the
    /// peer is open yet; otherwise returns null and reports the
    /// direction of whatever is already open. Unlike calling
    /// <see cref="IsActive"/> and then <see cref="Acquire"/> separately,
    /// nothing can register a session for the peer in between the check
    /// and the acquire - both happen under the same lock. The lease isn't
    /// connected until the dial is confirmed (<see cref="Connected"/>).
    ///
    /// Why this exists (#185): <see cref="OutboundMessageManager"/> asks
    /// <see cref="IsActive"/> once, early, before the settle-gate wait
    /// and the AGW/RHP connect round trip that follow it - both take
    /// real time. An inbound session from the very peer being dialled
    /// can be handed to us in that window, after the early check passed
    /// but before our SABM actually reaches BPQ, and nothing re-checks.
    /// This is that re-check, called from
    /// <see cref="BearerSwitchingOutboundTransport"/> immediately before
    /// the dial - the last point at which declining still means we
    /// never sent a frame.</summary>
    public PeerSessionLease? TryAcquire(string peerCallsign, string direction, out string? openDirection, int? linkPort = null)
    {
        lock (gate)
        {
            if (open.TryGetValue(peerCallsign, out var existing) && existing.Count > 0)
            {
                openDirection = existing[0].Direction;
                return null;
            }
            openDirection = null;
            var lease = new PeerSessionLease(this, peerCallsign, direction, linkPort);
            open[peerCallsign] = [lease];
            Signal();
            return lease;
        }
    }

    /// <summary>
    /// The session <paramref name="lease"/> stands for is connected now:
    /// every other connected session with the same peer on the same port
    /// is older, and is retired (its <see cref="PeerSessionLease.Retired"/>
    /// fires, outside the registry's lock). True if there was one: the
    /// link was already up when this session connected.
    /// </summary>
    public bool Connected(PeerSessionLease lease)
    {
        List<PeerSessionLease> retiring;
        lock (gate)
        {
            lease.IsConnected = true;
            retiring = lease.LinkPort is { } port && open.TryGetValue(lease.Peer, out var leases)
                ? [.. leases.Where(l => !ReferenceEquals(l, lease) && l.LinkPort == port && l.IsConnected && !l.IsRetired)]
                : [];
            foreach (var old in retiring) old.IsRetired = true;
        }
        foreach (var old in retiring) old.Retire();
        return retiring.Count > 0;
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

    internal void Release(PeerSessionLease lease)
    {
        lock (gate)
        {
            if (!open.TryGetValue(lease.Peer, out var leases) || !leases.Remove(lease)) return;
            if (leases.Count == 0)
            {
                open.Remove(lease.Peer);
                // The forwarder held back anything for this peer while the
                // session was open; it can go now.
                forwarderWakeup?.Wake();
            }
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
}

/// <summary>
/// One DAPPS session with a peer, as <see cref="PeerSessionRegistry"/>
/// knows it. Disposing it ends the registration.
/// </summary>
public sealed class PeerSessionLease : IDisposable
{
    private readonly PeerSessionRegistry owner;
    private readonly CancellationTokenSource retired = new();

    internal PeerSessionLease(PeerSessionRegistry owner, string peer, string direction, int? linkPort)
    {
        this.owner = owner;
        Peer = peer;
        Direction = direction;
        LinkPort = linkPort;
    }

    public string Peer { get; }
    public string Direction { get; }

    /// <summary>The AGW port the link is on; null when the session takes
    /// no part in retirement.</summary>
    public int? LinkPort { get; }

    /// <summary>
    /// Fires when a newer session with the same peer has connected on the
    /// same port at this node: the link is that one's now. Stop using this
    /// session, and close it without a disconnect. Never fires for a lease
    /// with no port.
    /// </summary>
    public CancellationToken Retired => LinkPort is null ? CancellationToken.None : retired.Token;

    internal bool IsConnected { get; set; }
    internal bool IsRetired { get; set; }

    internal void Retire()
    {
        try { retired.Cancel(); }
        catch (AggregateException) { /* an owner's callback failed; the lease is retired all the same */ }
    }

    public void Dispose() => owner.Release(this);
}
