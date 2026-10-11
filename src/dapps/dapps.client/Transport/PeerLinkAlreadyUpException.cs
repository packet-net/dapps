namespace dapps.client.Transport;

/// <summary>
/// The node refused a dial because a link between the two callsigns is
/// already up, held at our node by something other than this call. pdn
/// (node-v0.65.0 on) answers such an open with its own RHPv2 error 18,
/// "Already connected", rather than resetting the link (packet.net#862),
/// and the holder can be an accept or a console: the peer's own call to
/// us, which our listener has, or a local sysop session or another app on
/// the same callsign. A call that lands while the dial is under way is
/// not this: the dial takes that link, and the open reply says
/// <c>crossed</c>.
///
/// <see cref="HeldByPeersCall"/> says which, once someone has looked. The
/// RHPv2 transport can't tell, so it throws with null; the outbound
/// transport that knows DAPPS's own sessions waits a few seconds for the
/// peer's call to reach our listener, and throws again with true (the
/// calls crossed: <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/>
/// defers the message, with no cooldown, and the forwarder hands it to
/// that session) or false (an ordinary failed dial, with the usual
/// cooldown).
/// </summary>
public sealed class PeerLinkAlreadyUpException(
    string localCallsign, string peerCallsign, string port, bool? heldByPeersCall = null) : Exception(heldByPeersCall switch
    {
        true => $"the link {localCallsign}->{peerCallsign} on port {port} is already up, held by {peerCallsign}'s call to us: the calls crossed",
        false => $"the node says the link {localCallsign}->{peerCallsign} on port {port} is already up, but not to DAPPS (a console or another app holds it)",
        null => $"the node says the link {localCallsign}->{peerCallsign} on port {port} is already up, held by something other than this call",
    })
{
    public string LocalCallsign { get; } = localCallsign;
    public string PeerCallsign { get; } = peerCallsign;
    public string Port { get; } = port;

    /// <summary>True when the peer's own call to us holds the link (the
    /// calls crossed), false when something else does, null when nobody
    /// has looked yet.</summary>
    public bool? HeldByPeersCall { get; } = heldByPeersCall;
}
