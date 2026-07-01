using System.Collections.Concurrent;
using dapps.meshcore;

namespace dapps.meshcore.sim;

/// <summary>
/// An <see cref="IMeshCoreLink"/> backed by a <see cref="MeshFabric"/> instead of a
/// serial radio. SendDataAsync hands the payload to the fabric to flood across the
/// virtual mesh; the fabric calls <see cref="Deliver"/>/<see cref="Overhear"/> back on
/// the nodes that hear it. The real bearer classes run on top unchanged.
/// </summary>
public sealed class SimulatedMeshCoreLink : IMeshCoreLink
{
    private readonly MeshFabric _fabric;
    private readonly ConcurrentQueue<ChannelData> _inbound = new();

    /// <summary>Cap the per-node inbound backlog so a busy/relay node can't grow the
    /// queue without bound in a long run. Oldest are dropped (as a real radio would).</summary>
    private const int MaxBacklog = 4096;

    public string NodeId { get; }

    public event Action? MessageWaiting;
    public event Action<int, sbyte>? PacketHeard;

    // The simulated radio is always healthy; watchdog/recovery is out of scope for the
    // fabric (it models the channel, not the serial link's liveness).
    public MeshCoreLink.LinkState State => MeshCoreLink.LinkState.Healthy;

    /// <summary>Total datagrams this node has been given to send (observability).</summary>
    public long Sent { get; private set; }

    internal SimulatedMeshCoreLink(MeshFabric fabric, string nodeId)
    {
        _fabric = fabric;
        NodeId = nodeId;
    }

    public Task<bool> SendDataAsync(byte[] payload, CancellationToken ct)
    {
        Sent++;
        _fabric.Flood(NodeId, payload);
        return Task.FromResult(true);
    }

    public Task<MeshCoreClient.InboundBatch?> DrainAsync(CancellationToken ct)
    {
        if (_inbound.IsEmpty) return Task.FromResult<MeshCoreClient.InboundBatch?>(null);
        var data = new List<ChannelData>();
        while (_inbound.TryDequeue(out var d)) data.Add(d);
        return Task.FromResult<MeshCoreClient.InboundBatch?>(new MeshCoreClient.InboundBatch(new(), data));
    }

    /// <summary>Fabric hook: queue a received datagram for the app and wake the drain loop.</summary>
    internal void Deliver(byte[] payload, sbyte snr)
    {
        _inbound.Enqueue(new ChannelData(snr, 0, 0xFF, MeshCoreClient.DATA_TYPE_DEV, payload));
        while (_inbound.Count > MaxBacklog && _inbound.TryDequeue(out _)) { }
        MessageWaiting?.Invoke();
    }

    /// <summary>Fabric hook: this node overheard a packet on the channel (occupancy).</summary>
    internal void Overhear(int len, sbyte snr) => PacketHeard?.Invoke(len, snr);
}
