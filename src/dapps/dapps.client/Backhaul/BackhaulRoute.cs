namespace dapps.client.Backhaul;

/// <summary>
/// Identifies a neighbour DAPPS instance for a backhaul send. Bearer-
/// agnostic identity (callsign) plus optional bearer-specific hints
/// the implementation knows how to consume.
///
/// <see cref="BearerPort"/> is the node's own number for the radio port
/// (#233): XRouter's <c>PORT=</c>, BPQ's <c>PORTNUM</c>, pdn's port as its
/// <c>PORTS</c> command numbers it. <see cref="PortHeld"/> marks a route
/// whose port was saved by an older DAPPS and has not been converted yet
/// (the node's port numbers are needed first): no backhaul takes it, so
/// nothing goes out on a guessed port. As bearer types are added (MeshCore companion, MeshCore
/// KISS, etc.) further fields go here. Each backhaul reads what's
/// relevant and ignores the rest; the queue/router layer doesn't have
/// to know which bearer a given route maps to.
///
/// <see cref="DialAs"/> (#431) is the callsign the node bearer calls from
/// for this neighbour, an SSID of our own callsign; null calls from our
/// own. Each one is a separate AX.25 link, so neighbours reached through
/// one node from different Dial as callsigns don't share a link.
/// </summary>
public sealed record BackhaulRoute(
    string Callsign,
    int? BearerPort = null,
    string? UdpEndpoint = null,
    ConnectScript? ConnectScript = null,
    string? MeshCoreChannel = null,
    string? ConnectVia = null,
    bool PortHeld = false,
    string? DialAs = null)
{
    /// <summary>The callsign the node bearer calls from: <see cref="DialAs"/>,
    /// or <paramref name="ourCallsign"/> when there is none.</summary>
    public string SourceCallsign(string ourCallsign) => DialAs ?? ourCallsign;

    /// <summary>
    /// True when <paramref name="other"/> dials the same AX.25 link at our
    /// node: the same callsign to call, from the same callsign (both our
    /// own, or the same Dial as callsign). The port isn't compared.
    /// </summary>
    public bool SharesLinkWith(BackhaulRoute other) =>
        string.Equals(DialCallsign, other.DialCallsign, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DialAs ?? "", other.DialAs ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The callsign the node bearer dials: <see cref="ConnectVia"/> when
    /// the peer is reached through another node (whose prompt the
    /// <see cref="ConnectScript"/> is played at), otherwise the peer itself.
    /// </summary>
    public string DialCallsign => ConnectVia ?? Callsign;
}
