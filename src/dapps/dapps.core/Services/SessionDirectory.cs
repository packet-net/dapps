using dapps.core.Routing;
using System.Collections.Concurrent;
using dapps.client.Backhaul;

namespace dapps.core.Services;

/// <summary>
/// Established sessions with neighbours, by the peer's callsign, that the
/// forwarder can hand traffic for that peer to: sessions neighbours opened
/// with us, and our polls. (Sessions the session backhaul dialled for
/// traffic are its own, <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul.TryHandToOpenSession"/>.)
/// While a session with a peer is open we can't dial it (#178), so
/// without this our mail for it would wait until the session ended.
/// </summary>
public sealed class SessionDirectory(ForwarderWakeup? forwarderWakeup = null)
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
    /// <paramref name="destination"/> (<c>app@CALL</c>) is addressed to:
    /// the exact call+SSID if it has a session, else one under the same
    /// base callsign that isn't a sibling of ours (#239); null when there
    /// is none. Mail for a node that has called us can go on its session
    /// even when we have no route to it.
    /// </summary>
    public string? CallerFor(string destination, string ourCallsign)
    {
        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1) return null;
        return CallsignMatch.Best(sessions.Keys, peer => peer, destination[(at + 1)..], ourCallsign);
    }

    /// <summary>
    /// Give <paramref name="batch"/> to the established session with
    /// <paramref name="peer"/>, which sends it as the window allows.
    /// A session from the same callsign with another SSID takes it when
    /// there's none with this one, if that station called us (never our
    /// poll of it, and never a sibling of ours): the SSID a
    /// neighbour calls us from can change in transit over the network (a
    /// node's own SSID, or a NET/ROM circuit's), so the neighbour table
    /// can't know it. False
    /// when no session from that station is established, or it is ending.
    /// </summary>
    public bool TryHand(string peer, IBackhaulBatch batch, string ourCallsign) =>
        CallsignMatch.Best(
            // Only a session the station opened with us stands in for it:
            // our poll of another SSID is another neighbour's session.
            sessions.Where(kv => DestinationParser.SameStation(kv.Key, peer) || !kv.Value.Dialled).Select(kv => kv.Key),
            p => p, peer, ourCallsign) is { } caller
        && sessions.TryGetValue(caller, out var session) && session.TryTake(batch);

    /// <summary>The established session with <paramref name="peer"/>, or null.</summary>
    public ExchangeSession? Find(string peer) =>
        sessions.TryGetValue(peer, out var session) ? session : null;
}
