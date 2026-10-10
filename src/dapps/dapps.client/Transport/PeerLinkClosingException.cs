namespace dapps.client.Transport;

/// <summary>
/// Thrown by an <see cref="IDappsOutboundTransport"/> when the node refuses
/// a new connect because it still has a link for the same callsign pair and
/// port: in practice the last session's link, still being disconnected.
/// XRouter keeps that link until the far end acknowledges the disconnect,
/// and when the acknowledgement is lost on air it retries the disconnect
/// for some seconds more. Not a failure of the route:
/// <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/> catches this
/// and returns <see cref="dapps.client.Backhaul.BackhaulSendResult.Defer"/>,
/// so the message stays queued for the next run.
/// </summary>
public sealed class PeerLinkClosingException(
    string localCallsign, string peerCallsign, string port, int errorCode, string? errorText) : Exception(
    $"the node still has a link {localCallsign}->{peerCallsign} on port {port} (RHP error {errorCode}: {errorText}), most likely the last session's, still closing")
{
    public string LocalCallsign { get; } = localCallsign;
    public string PeerCallsign { get; } = peerCallsign;
}
