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
        sessions[DestinationParser.StationKey(peer)] = session;
        // Anything held back for this peer can go to its session now.
        forwarderWakeup?.Wake();
    }

    internal void Unregister(string peer, ExchangeSession session) =>
        sessions.TryRemove(new KeyValuePair<string, ExchangeSession>(DestinationParser.StationKey(peer), session));

    public string? CallerFor(string destination)
    {
        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1) return null;
        var peer = DestinationParser.StationKey(destination[(at + 1)..]);
        return sessions.ContainsKey(peer) ? peer : null;
    }

    /// <summary>
    /// Give <paramref name="batch"/> to the established session with
    /// <paramref name="peer"/>, which sends it as the window allows.
    /// False when no session from that peer is established, or it is
    /// ending.
    /// </summary>
    public bool TryHand(string peer, IBackhaulBatch batch) =>
        sessions.TryGetValue(DestinationParser.StationKey(peer), out var session) && session.TryTake(batch);

    /// <summary>The established session with <paramref name="peer"/>, or null.</summary>
    public ExchangeSession? Find(string peer) =>
        sessions.TryGetValue(DestinationParser.StationKey(peer), out var session) ? session : null;
}
