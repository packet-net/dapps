namespace dapps.client.Transport;

/// <summary>
/// Thrown by an <see cref="IDappsOutboundTransport"/> that declines to
/// dial because a session with <see cref="PeerCallsign"/> is already
/// open in the other direction (#178, #185). Not a failure of the
/// route: <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/>
/// catches this and returns
/// <see cref="dapps.client.Backhaul.BackhaulSendResult.Defer"/> so the
/// caller leaves the message queued rather than recording an outcome
/// against the neighbour. <see cref="LocalCallsign"/> is set when the
/// session is held from one of our Dial as callsigns (#431), and null for
/// our own.
/// </summary>
public sealed class PeerSessionBusyException(string peerCallsign, string openDirection, string? localCallsign = null) : Exception(
    localCallsign is null
        ? $"{peerCallsign} already has an {openDirection} session open; not dialling to avoid AX.25 SABM glare"
        : $"{peerCallsign} already has an {openDirection} session open from {localCallsign}; not dialling to avoid AX.25 SABM glare")
{
    public string PeerCallsign { get; } = peerCallsign;
    public string OpenDirection { get; } = openDirection;
    public string? LocalCallsign { get; } = localCallsign;
}
