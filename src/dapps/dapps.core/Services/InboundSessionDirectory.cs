using System.Collections.Concurrent;
using dapps.client.Backhaul;

namespace dapps.core.Services;

/// <summary>
/// The established sessions neighbours have open with us, by the calling
/// peer's callsign, so the forwarder can hand traffic for that peer to
/// the session it already has with us. While a peer has a session open
/// we can't dial it (#178), so without this our mail for it would wait
/// until it hung up.
/// </summary>
public sealed class InboundSessionDirectory(ForwarderWakeup? forwarderWakeup = null)
{
    private readonly ConcurrentDictionary<string, ExchangeSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    internal void Register(string peer, ExchangeSession session)
    {
        sessions[peer] = session;
        // Anything held back for this peer can go to its session now.
        forwarderWakeup?.Wake();
    }

    internal void Unregister(string peer, ExchangeSession session) =>
        sessions.TryRemove(new KeyValuePair<string, ExchangeSession>(peer, session));

    /// <summary>
    /// The peer with an established session whose callsign is the one
    /// <paramref name="destination"/> (<c>app@CALL</c>) is addressed to,
    /// SSIDs aside; null when there is none. Mail for a node that has
    /// called us can go on its session even when we have no route to it.
    /// </summary>
    public string? CallerFor(string destination)
    {
        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1) return null;
        var baseCall = destination[(at + 1)..].Split('-')[0];
        return sessions.Keys.FirstOrDefault(peer => string.Equals(peer.Split('-')[0], baseCall, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Give <paramref name="batch"/> to the established session from
    /// <paramref name="peer"/>, which sends it as the window allows.
    /// False when no session from that peer is established, or it is
    /// ending.
    /// </summary>
    public bool TryHand(string peer, IBackhaulBatch batch) =>
        sessions.TryGetValue(peer, out var session) && session.TryTake(batch);
}
