using System.Security.Cryptography;

namespace dapps.meshcore.sim;

/// <summary>Node behaviour in the mesh, mirroring MeshCore firmware roles.</summary>
public enum MeshRole
{
    /// <summary>A Companion node (a DAPPS node): originates + receives channel traffic
    /// but does NOT relay floods (client_repeat=0).</summary>
    Leaf,

    /// <summary>A Repeater/Room-Server: relays floods it hears (subject to hop cap +
    /// flood-scope), but has no DAPPS app of its own.</summary>
    Relay,
}

/// <summary>
/// An in-process model of a multi-hop MeshCore mesh. Nodes are connected by
/// (optionally lossy) undirected edges; a datagram broadcast by one node floods
/// hop-by-hop across the graph, mirroring the firmware's verified behaviour:
///
/// <list type="bullet">
/// <item>Every node that hears a packet delivers it to its app (broadcast; the DAPPS
///   inbox self-selects the addressee) - relays have no app so only leaves consume.</item>
/// <item>Per-node dedup by packet identity (the 160-entry no-expiry ring): a packet
///   heard again via another path is dropped, not re-delivered or re-flooded.</item>
/// <item>Only <see cref="MeshRole.Relay"/> nodes re-flood, and only within the 64-hop
///   cap.</item>
/// <item>Flood-scope: a scoped flood is re-flooded only by relays that share the
///   origin's scope, so nodes reachable only through an out-of-scope relay never hear
///   it (deployment model B containment).</item>
/// </list>
///
/// The bearer's per-frame rolling nonce means two <i>distinct</i> messages never share
/// a packet id (both flood); a single flood arriving by multiple paths has identical
/// bytes (one id) and is deduped - exactly the property we want to test at scale.
/// </summary>
public sealed class MeshFabric
{
    private sealed class Node
    {
        public required string Id;
        public required MeshRole Role;
        public string Scope = "";
        public SimulatedMeshCoreLink Link = null!;
        public readonly List<(string to, double loss)> Neighbours = [];
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);   // packet ids (the dedup ring)
    }

    /// <summary>Firmware flood hop cap.</summary>
    public const int HopCap = 64;

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Random _rng;
    private readonly sbyte _snr;

    /// <summary>Total packet-deliveries the fabric has made (a hop that a node heard a
    /// new packet on), and total re-floods - coarse observability for scenarios.</summary>
    public long Deliveries { get; private set; }
    public long Refloods { get; private set; }

    /// <param name="seed">Seed for the loss RNG so scenarios are reproducible.</param>
    /// <param name="snrQuarterDb">Reported SNR in quarter-dB (default 40 = 10 dB).</param>
    public MeshFabric(int seed = 1, sbyte snrQuarterDb = 40)
    {
        _rng = new Random(seed);
        _snr = snrQuarterDb;
    }

    public SimulatedMeshCoreLink AddLeaf(string id, string scope = "") => Add(id, MeshRole.Leaf, scope);
    public SimulatedMeshCoreLink AddRelay(string id, string scope = "") => Add(id, MeshRole.Relay, scope);

    private SimulatedMeshCoreLink Add(string id, MeshRole role, string scope)
    {
        if (_nodes.ContainsKey(id)) throw new ArgumentException($"duplicate node id '{id}'");
        var node = new Node { Id = id, Role = role, Scope = scope };
        node.Link = new SimulatedMeshCoreLink(this, id);
        _nodes[id] = node;
        return node.Link;
    }

    /// <summary>Connect two nodes with an undirected edge. <paramref name="loss"/> is the
    /// per-transmission drop probability on that edge (0 = perfect, 1 = never delivers).</summary>
    public void Connect(string a, string b, double loss = 0.0)
    {
        if (a == b) throw new ArgumentException("cannot connect a node to itself");
        var na = Get(a);
        var nb = Get(b);
        na.Neighbours.Add((b, loss));
        nb.Neighbours.Add((a, loss));
    }

    /// <summary>Build a linear chain <c>a0 - a1 - ... - a(n-1)</c> with the given ids and a
    /// uniform per-edge loss. Convenience for the common "string of relays" topology.</summary>
    public void ConnectChain(IReadOnlyList<string> ids, double loss = 0.0)
    {
        for (var i = 0; i + 1 < ids.Count; i++) Connect(ids[i], ids[i + 1], loss);
    }

    public SimulatedMeshCoreLink Link(string id) => Get(id).Link;

    private Node Get(string id) =>
        _nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"unknown node '{id}'");

    /// <summary>Flood a payload originated by <paramref name="fromNode"/> across the mesh.
    /// Called by <see cref="SimulatedMeshCoreLink.SendDataAsync"/>. Runs synchronously so a
    /// no-loss scenario is fully deterministic.</summary>
    internal void Flood(string fromNode, byte[] payload)
    {
        lock (_lock)
        {
            var origin = Get(fromNode);
            var scope = origin.Scope;
            var packetId = PacketId(payload);
            // The origin doesn't hear its own transmission, but remembers it so a flood
            // that loops back is deduped.
            origin.Seen.Add(packetId);

            // BFS over relays that re-flood. Track path_len exactly as the firmware does:
            // the origin transmits with path_len=0 (it's not a forwarder); a relay that
            // RECEIVED path_len P forwards iff P < HopCap (MAX_PATH_SIZE=64), appending
            // itself so its transmission carries P+1. So relays R1..R64 forward and R65
            // (which would receive path_len=64) is dropped. A relay re-floods a packet
            // at most once (the dedup ring).
            var reflood = new Queue<(Node relay, int receivedPathLen)>();
            Transmit(origin, receiversPathLen: 0, packetId, scope, payload, reflood);
            while (reflood.Count > 0)
            {
                var (relay, receivedPathLen) = reflood.Dequeue();
                if (receivedPathLen >= HopCap) continue;   // path full - can't forward further
                Transmit(relay, receiversPathLen: receivedPathLen + 1, packetId, scope, payload, reflood);
            }
        }
    }

    // One transmission from `tx`: every neighbour may hear it (subject to loss), dedups,
    // delivers-if-a-leaf, and re-floods-if-an-in-scope-relay. `receiversPathLen` is the
    // path_len value the neighbours receive (the count of forwarders before them).
    private void Transmit(Node tx, int receiversPathLen, string packetId, string scope, byte[] payload, Queue<(Node, int)> reflood)
    {
        foreach (var (toId, loss) in tx.Neighbours)
        {
            if (loss > 0 && _rng.NextDouble() < loss) continue;   // lost on this edge
            var nbr = _nodes[toId];

            // Overhearing is a PHY event: it happens whenever RF arrives, even for a
            // duplicate we'll dedup - that's what the occupancy estimate should see.
            nbr.Link.Overhear(payload.Length, _snr);

            if (!nbr.Seen.Add(packetId)) continue;   // dedup ring: already processed this packet

            // Deliver to the app only for leaves (relays are infrastructure with no app).
            if (nbr.Role == MeshRole.Leaf)
            {
                nbr.Link.Deliver(payload, _snr);
                Deliveries++;
            }

            // Re-flood only from relays, and only if the flood's scope is carried: unscoped
            // floods propagate through any relay; a scoped flood only through relays that
            // share the scope (model B containment).
            bool scopeCarried = scope.Length == 0 || nbr.Scope == scope;
            if (nbr.Role == MeshRole.Relay && scopeCarried)
            {
                Refloods++;
                reflood.Enqueue((nbr, receiversPathLen));   // the path_len this relay received
            }
        }
    }

    private static string PacketId(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));
}
