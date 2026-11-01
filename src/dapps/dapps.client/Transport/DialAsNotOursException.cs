namespace dapps.client.Transport;

/// <summary>
/// Thrown by an <see cref="IDappsOutboundTransport"/>, with nothing sent,
/// when a neighbour's Dial as callsign (#431) is no longer an SSID of this
/// node's callsign: the callsign changed after the neighbour was saved.
/// Nothing is called from a callsign that isn't this station's, and the
/// call isn't made from our own callsign instead. Not a failure of the
/// route: the operator has to edit the neighbour.
/// </summary>
public sealed class DialAsNotOursException(string dialAs, string ourCallsign, string remoteCallsign) : Exception(
    $"Dial as {dialAs} is not an SSID of this node's callsign {ourCallsign}, so {remoteCallsign} was not called; edit the neighbour's Dial as on the Topology page")
{
    public string DialAs { get; } = dialAs;
}
