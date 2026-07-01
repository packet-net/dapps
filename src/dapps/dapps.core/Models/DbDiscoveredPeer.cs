using dapps.client.Discovery;
using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per (callsign, bearer, channelKey) - the same peer can be
/// heard on multiple channels (different bearer ports, MeshCore radios,
/// LAN multicast, …) and we record each independently so the resolver
/// can pick the cheapest one on a per-message basis.
///
/// <see cref="LinkClass"/> and <see cref="CostHint"/> are denormalized
/// from the <c>DbDiscoveryChannel</c> row at upsert time. The router
/// sorts candidate rows by <c>CostHint</c> directly without joining,
/// and a channel reconfigured later doesn't retroactively rewrite the
/// view of past observations.
/// </summary>
[Table("discoveredpeers")]
public sealed class DbDiscoveredPeer
{
    /// <summary>Synthetic primary key formed as
    /// <c>{Callsign}|{Bearer}|{ChannelKey}</c>. SQLite-net doesn't do
    /// composite primary keys cleanly, so we synthesise one. Callers
    /// use <see cref="MakeKey"/> rather than building it by hand.</summary>
    [PrimaryKey]
    public string PeerKey { get; set; } = "";

    [Indexed]
    public string Callsign { get; set; } = "";

    /// <summary>Bearer name - matches <c>IDiscoveryBearer.Name</c>
    /// and <see cref="DbDiscoveryChannel.Bearer"/>.</summary>
    public string Bearer { get; set; } = "";

    /// <summary>Bearer-specific channel id (bearer port stringified
    /// for AGW, multicast endpoint for UDP).</summary>
    public string ChannelKey { get; set; } = "";

    /// <summary>FK to <see cref="DbDiscoveryChannel.Id"/>. 0 if the
    /// channel was removed after a peer was heard on it.</summary>
    public int ChannelId { get; set; }

    public LinkClass LinkClass { get; set; }

    /// <summary>Lower wins. Denormalized from the channel.</summary>
    public int CostHint { get; set; }

    public int Hops { get; set; }

    /// <summary>Freshness window in seconds the originator advertised.
    /// The discovery service ages the row out when
    /// <c>now - LastSeen &gt; TtlSeconds</c>.</summary>
    public int TtlSeconds { get; set; }

    /// <summary>bearer port the beacon arrived on (AGW only).</summary>
    public int? BearerPort { get; set; }

    /// <summary>UDP host:port the beacon datagram was sourced from
    /// (UDP only).</summary>
    public string? UdpEndpoint { get; set; }

    /// <summary>MeshCore channel name the peer was heard on (MeshCore bearer only),
    /// so the router can build a MeshCore route back to a passively-discovered peer
    /// without a manual neighbour (#27). Null for other bearers.</summary>
    public string? MeshCoreChannel { get; set; }

    public DateTime LastSeen { get; set; }

    public static string MakeKey(string callsign, string bearer, string channelKey)
        => $"{callsign}|{bearer}|{channelKey}";
}
