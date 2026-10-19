namespace dapps.client.Discovery;

/// <summary>
/// A neighbour-discovery beacon. Bearer-neutral: AX.25 UI frames carry
/// it as their information field, UDP multicast carries it as the
/// datagram body, future bearers carry it however suits them. The wire
/// form is a tiny ASCII line - see <see cref="BeaconCodec"/>.
///
/// <see cref="BearerHint"/> records the channel the beacon was heard on
/// so a peer table can record more than just "we saw G7XYZ-9 somewhere"
/// - it knows whether to talk back over bearer port 1 or UDP 10.0.0.5:1880.
///
/// <see cref="UdpPort"/> is the port the sender's UDP datagram listener
/// is bound to. A UDP beacon comes from an ephemeral socket, so the
/// source port says nothing about where mail should be sent (#299); the
/// sender says it in the beacon, as the optional <c>udpport</c> field.
/// Senders that do not listen on UDP, and older senders, leave it out.
/// </summary>
public sealed record BeaconFrame(
    string Callsign,
    int Hops,
    int Ttl,
    BeaconBearerHint Bearer,
    int? UdpPort = null);

public abstract record BeaconBearerHint(string Kind);

/// <summary>Beacon arrived as an AGW UI frame on the node's port
/// <paramref name="BearerPort"/>, numbered as the node shows it (#233).</summary>
public sealed record AgwBearerHint(int BearerPort) : BeaconBearerHint("agw");

/// <summary>Beacon arrived as a UDP datagram from this host:port.</summary>
public sealed record UdpBearerHint(string Endpoint) : BeaconBearerHint("udp");
