using SQLite;

namespace dapps.core.Models;

[Table("routehints")]
public class DbRouteHint
{
    [PrimaryKey]
    public string Destination { get; set; } = "";
    public string NextHop { get; set; } = "";
}

[Table("neighbours")]
public class DbNeighbour
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Unique = true)]
    public string Callsign { get; set; } = "";

    /// <summary>
    /// Optional override for which bearer port (0-indexed) to use when
    /// connecting to this neighbour. Null falls back to
    /// SystemOptions.DefaultBearerPort.
    /// </summary>
    public int? BearerPort { get; set; }

    /// <summary>
    /// Optional UDP endpoint (<c>host:port</c>) for the datagram
    /// backhaul (stand-in for MeshCore-style bearers). When set, the
    /// UDP backhaul handles forwarding to this neighbour and the AGW
    /// path is not used. Null = use AGW.
    /// </summary>
    public string? UdpEndpoint { get; set; }

    /// <summary>
    /// Optional connect-script for reaching this neighbour through a
    /// chain of intermediate non-DAPPS packet nodes. JSON shape:
    /// <c>{"steps":[{"send":"C G0NODE2","expect":"Connected to G0NODE2"},...]}</c>.
    /// When non-null, the AGW backhaul plays the script after the
    /// initial connect, before falling into the DAPPSv1 prompt. Null =
    /// direct connection (the usual case). See <see cref="dapps.client.ConnectScript"/>.
    /// </summary>
    public string? ConnectScriptJson { get; set; }

    /// <summary>
    /// Optional MeshCore channel name. When set, this neighbour is reachable over
    /// the MeshCore bearer (#154): the backhaul broadcasts on the configured
    /// private channel and this neighbour self-selects by destination callsign.
    /// Null = not a MeshCore neighbour. (sqlite-net adds this column on upgrade.)
    /// </summary>
    public string? MeshCoreChannel { get; set; }

    /// <summary>
    /// Per-neighbour override for <see cref="dapps.core.Models.SystemOptions.CompressionEnabled"/>.
    /// Null = defer to the system-wide setting. False = never compress
    /// payloads to this neighbour, even if the system setting is on.
    /// True = compress when it saves bytes even if the system setting
    /// is off. (sqlite-net adds this column on upgrade.)
    /// </summary>
    public bool? CompressionEnabled { get; set; }

    /// <summary>
    /// Per-neighbour override for <see cref="dapps.core.Models.SystemOptions.SessionTailSeconds"/>.
    /// Null = defer to the system-wide setting. 0 = never hold the link
    /// open to this neighbour after a session, even if the system
    /// setting is nonzero. Otherwise the number of idle seconds to hold
    /// the link for. Clamped 0-600. (sqlite-net adds this column on
    /// upgrade.)
    /// </summary>
    public int? SessionTailSeconds { get; set; }
}
