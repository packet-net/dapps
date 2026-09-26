namespace dapps.client.Backhaul;

/// <summary>
/// A run of queued messages for one route, handed to
/// <see cref="IDappsBackhaul.SendBatchAsync"/>. The bearer pulls the
/// messages one at a time and reports what happened to each as soon as
/// it knows, so the caller records every outcome (mark forwarded, feed
/// routing, start a cooldown) as it happens rather than at the end: a
/// link that drops half way through leaves the messages it already
/// carried marked as done.
/// </summary>
public interface IBackhaulBatch
{
    /// <summary>
    /// The next message for the route, or null when there is nothing
    /// more to send right now. May look at the queue again, so a later
    /// call can return a message that was queued after an earlier call
    /// returned null: that is how a session picks up traffic that
    /// arrives while it is open.
    /// </summary>
    ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct);

    /// <summary>
    /// What happened to a message <see cref="NextAsync"/> handed out.
    /// Called exactly once for each of them. <paramref name="elapsed"/>
    /// is how long its send took, including the connect for the first
    /// message of a session.
    /// </summary>
    ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct);
}
