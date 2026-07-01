using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// Single source of truth for building a <see cref="BackhaulRoute"/>
/// from a <see cref="DbNeighbour"/>. Routing algorithms call this
/// instead of constructing routes inline so all bearer hints
/// (BearerPort, UdpEndpoint, ConnectScript) flow through automatically
/// when new ones are added to the neighbour row.
/// </summary>
public static class RouteBuilder
{
    public static BackhaulRoute FromNeighbour(DbNeighbour neighbour, int? defaultBearerPort)
        => new(
            Callsign: neighbour.Callsign,
            BearerPort: neighbour.BearerPort ?? defaultBearerPort,
            UdpEndpoint: neighbour.UdpEndpoint,
            ConnectScript: ConnectScript.FromJson(neighbour.ConnectScriptJson),
            MeshCoreChannel: neighbour.MeshCoreChannel);
}
