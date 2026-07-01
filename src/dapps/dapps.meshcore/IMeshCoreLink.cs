namespace dapps.meshcore;

/// <summary>
/// The data-path surface the bearer needs from a MeshCore radio link:
/// send a channel-data datagram, drain received ones, and observe wake/occupancy
/// signals. <see cref="MeshCoreLink"/> is the real serial implementation;
/// a simulated implementation (dapps.meshcore.sim) backs an in-process multi-hop
/// mesh so the real bearer classes (transport, reliability, inbound, governor)
/// can be tested at scale without radios.
/// </summary>
public interface IMeshCoreLink
{
    /// <summary>Broadcast one channel-data payload as a flood. Returns false if the
    /// link isn't ready (the caller refunds airtime and reports "not ready").</summary>
    Task<bool> SendDataAsync(byte[] payload, CancellationToken ct);

    /// <summary>Drain all queued inbound datagrams, or null if none.</summary>
    Task<MeshCoreClient.InboundBatch?> DrainAsync(CancellationToken ct);

    /// <summary>Raised when a datagram becomes available to drain (inbound wake).</summary>
    event Action? MessageWaiting;

    /// <summary>Raised when a packet is overheard on the channel (len, snr) - feeds
    /// the occupancy estimate for the adaptive controls.</summary>
    event Action<int, sbyte>? PacketHeard;

    /// <summary>Current link health (used in diagnostics / send-not-ready messages).</summary>
    MeshCoreLink.LinkState State { get; }
}
