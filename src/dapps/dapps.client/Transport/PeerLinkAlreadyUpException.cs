namespace dapps.client.Transport;

/// <summary>
/// Thrown by an <see cref="IDappsOutboundTransport"/> when the node refuses
/// a dial because a link between the two callsigns is already up and held
/// at our node by something else: in practice the peer's own call to us,
/// which our listener has accepted. pdn (node-v0.65.0 on) answers such an
/// open with its own RHPv2 error 18, "Already connected", rather than
/// resetting the link (packet.net#862). A call that lands while the dial is
/// under way is not this: the dial takes that link, and the open reply says
/// <c>crossed</c>.
///
/// Either way the calls crossed, and this is not a failure of the route:
/// <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/> catches it and
/// returns <see cref="dapps.client.Backhaul.BackhaulSendResult.Defer"/>, so
/// the message stays queued, with no cooldown, and the forwarder hands it
/// to the session the peer's call opened as soon as that session is
/// established (establishing it wakes the forwarder).
/// </summary>
public sealed class PeerLinkAlreadyUpException(
    string localCallsign, string peerCallsign, string port) : Exception(
    $"the link {localCallsign}->{peerCallsign} on port {port} is already up, held by {peerCallsign}'s call to us")
{
    public string LocalCallsign { get; } = localCallsign;
    public string PeerCallsign { get; } = peerCallsign;
}
