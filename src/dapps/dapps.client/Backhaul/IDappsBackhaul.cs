namespace dapps.client.Backhaul;

/// <summary>
/// Sends DAPPS backhaul messages to neighbours. The seam between
/// queue/router logic (which knows about messages, neighbours, residual
/// TTL) and bearer mechanics (which know about wire formats and session
/// protocols).
///
/// Plan A0.1: implementations own the bearer's session/ack contract;
/// callers don't see streams, frames, or retry quirks. The DAPPSv1
/// `prompt` / `ihave` / `send` / `data` / `ack` exchange is one such
/// implementation (<see cref="Dappsv1SessionBackhaul"/>); a future
/// MeshCore companion or KISS bearer would be another, carrying the
/// same <see cref="BackhaulMessage"/> over different wire frames.
/// </summary>
public interface IDappsBackhaul
{
    /// <summary>
    /// True when this implementation knows how to deliver to the route
    /// - typically because the route carries the bearer-specific hint
    /// the impl needs (bearer port for AGW, UDP endpoint for datagram, etc).
    /// The caller iterates registered backhauls and picks the first one
    /// that returns true. Order of registration determines priority
    /// when multiple backhauls could handle the same route.
    /// </summary>
    bool CanHandle(BackhaulRoute route);

    /// <summary>
    /// Forward <paramref name="message"/> to <paramref name="route"/>.
    /// Returns whether the neighbour accepted the message; whatever
    /// "accepted" means is the bearer's contract (an explicit ack frame
    /// for stream bearers, "delivered to wire" for fire-and-forget
    /// datagram bearers, etc).
    /// </summary>
    Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct);

    /// <summary>
    /// Forward every message <paramref name="batch"/> hands out to
    /// <paramref name="route"/>, reporting each outcome back to it.
    /// Stops at the first message that isn't accepted; the rest stay
    /// queued for the next run, as they would behind that failure's
    /// cooldown anyway.
    ///
    /// This default sends each message on its own, which suits
    /// datagram bearers where every send stands alone. A bearer with a
    /// session to set up (<see cref="Dappsv1SessionBackhaul"/>)
    /// overrides it to carry the whole batch on one session.
    /// </summary>
    async Task SendBatchAsync(
        BackhaulRoute route,
        string localCallsign,
        IBackhaulBatch batch,
        CancellationToken ct)
    {
        while (await batch.NextAsync(ct) is { } message)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await SendAsync(message, route, localCallsign, ct);
            await batch.CompleteAsync(message, result, sw.Elapsed, ct);
            if (!result.Accepted) return;
        }
    }
}
