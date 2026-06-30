namespace dapps.client.Backhaul;

/// <summary>
/// Identifies a neighbour DAPPS instance for a backhaul send. Bearer-
/// agnostic identity (callsign) plus optional bearer-specific hints
/// the implementation knows how to consume.
///
/// Today only <see cref="BearerPort"/> is meaningful, and only to the AGW
/// backhaul. As bearer types are added (MeshCore companion, MeshCore
/// KISS, etc.) further fields go here. Each backhaul reads what's
/// relevant and ignores the rest; the queue/router layer doesn't have
/// to know which bearer a given route maps to.
/// </summary>
public sealed record BackhaulRoute(
    string Callsign,
    int? BearerPort = null,
    string? UdpEndpoint = null,
    ConnectScript? ConnectScript = null,
    string? MeshCoreChannel = null);
