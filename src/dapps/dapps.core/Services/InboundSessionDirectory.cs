using System.Collections.Concurrent;
using dapps.client.Backhaul;

namespace dapps.core.Services;

/// <summary>
/// The inbound DAPPSv1 sessions open right now, by the calling peer's
/// callsign, so the forwarder can hand traffic for that peer to the
/// session it already has with us (#187 proposal 9). While a peer holds
/// a session open we can't dial it (#178), so without this our mail for
/// it would wait until it hung up.
/// </summary>
public sealed class InboundSessionDirectory
{
    private readonly ConcurrentDictionary<string, InboundConnectionHandler> sessions = new(StringComparer.OrdinalIgnoreCase);

    internal void Register(string peer, InboundConnectionHandler handler) => sessions[peer] = handler;

    internal void Unregister(string peer, InboundConnectionHandler handler) =>
        sessions.TryRemove(new KeyValuePair<string, InboundConnectionHandler>(peer, handler));

    /// <summary>
    /// Give <paramref name="batch"/> to the open session from
    /// <paramref name="peer"/>. It tells the peer it has mail
    /// (<c>pending</c>) and sends the batch when the peer asks with
    /// <c>rev</c>. False when no session from that peer is open, or it
    /// is closing.
    /// </summary>
    public bool TryHand(string peer, IBackhaulBatch batch) =>
        sessions.TryGetValue(peer, out var handler) && handler.TryTakeBatch(batch);
}
