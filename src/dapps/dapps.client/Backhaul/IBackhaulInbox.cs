namespace dapps.client.Backhaul;

/// <summary>
/// The seam where bearer-specific receive code hands a complete,
/// validated DAPPS message off to bearer-neutral processing
/// (persistence, local-app delivery, future forwarding decisions).
///
/// Plan A0 inbound counterpart to <see cref="IDappsBackhaul"/>: the
/// DAPPSv1 session reader, a future MeshCore datagram reader, and any
/// other bearer all converge here once they have a hashed-and-checked
/// message. Anything that DAPPS does to a received message - write to
/// the queue, push to MQTT, decide to forward onwards - happens behind
/// this interface, not in the bearer code.
/// </summary>
public interface IBackhaulInbox
{
    Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct);

    /// <summary>
    /// Whether this node already has the message with this id, salt and
    /// length, so a session can answer an offer of it with <c>ack</c> and
    /// keep its payload off the air. A repeat delivered anyway is dropped
    /// by <see cref="DeliverAsync"/>; this only saves the airtime.
    /// </summary>
    Task<bool> HasAsync(string id, long? salt, int length, CancellationToken ct) => Task.FromResult(false);
}
