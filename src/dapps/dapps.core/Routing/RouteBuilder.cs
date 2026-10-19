using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// Single source of truth for building a <see cref="BackhaulRoute"/>
/// from a <see cref="DbNeighbour"/>. Routing algorithms call this
/// instead of constructing routes inline so all bearer hints
/// (BearerPort, UdpEndpoint, ConnectScript, ConnectVia) flow through automatically
/// when new ones are added to the neighbour row.
/// </summary>
public static class RouteBuilder
{
    /// <summary>
    /// The route to <paramref name="neighbour"/>. Its port is held
    /// (<see cref="BackhaulRoute.PortHeld"/>) while it, or the default it
    /// falls back to, was saved by an older DAPPS and not yet converted to
    /// the node's own number (#233): no backhaul takes the route then.
    /// </summary>
    public static BackhaulRoute FromNeighbour(DbNeighbour neighbour, int? defaultPort)
    {
        var port = neighbour.BearerPort ?? defaultPort;
        // A default of 0 is an older DAPPS's, waiting to be converted.
        var held = neighbour.PortPending == true || (neighbour.BearerPort is null && defaultPort is < 1);
        return new(
            Callsign: neighbour.Callsign,
            BearerPort: port,
            UdpEndpoint: neighbour.UdpEndpoint,
            ConnectScript: ConnectScript.FromJson(neighbour.ConnectScriptJson),
            MeshCoreChannel: neighbour.MeshCoreChannel,
            ConnectVia: neighbour.ConnectVia,
            PortHeld: held && neighbour.UdpEndpoint is null && neighbour.MeshCoreChannel is null);
    }

    /// <summary>The port a stored neighbour is called on, or null while it
    /// is held (see <see cref="FromNeighbour"/>).</summary>
    public static int? PortOf(DbNeighbour neighbour, int defaultPort)
    {
        if (neighbour.PortPending == true) return null;
        if (neighbour.BearerPort is { } own) return own;
        return defaultPort >= 1 ? defaultPort : null;
    }
}
