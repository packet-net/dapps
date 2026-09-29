namespace dapps.client.Backhaul;

/// <summary>
/// Outcome of a backhaul send. <c>Accepted=true</c> means the neighbour
/// confirmed receipt per the bearer's ack contract; the caller can mark
/// the message as forwarded and stop retrying. Otherwise <c>Error</c>
/// carries a human-readable reason for logging. Two outcomes are neither
/// a success nor a failure of the link:
/// <list type="bullet">
/// <item><c>Deferred</c>: the message was not sent and nothing failed
/// either (the session ended before it went, or the peer was busy), so
/// the caller leaves it queued and records nothing against the route.</item>
/// <item><c>Refused</c>: the neighbour said it won't take this message
/// (<c>no</c>), for a reason of its own such as its size limit. The link
/// is fine; the message needs another route or is dropped.</item>
/// </list>
/// </summary>
public sealed record BackhaulSendResult(bool Accepted, string? Error, bool Deferred = false, bool Refused = false)
{
    public static BackhaulSendResult Ok() => new(true, null);
    public static BackhaulSendResult Fail(string error) => new(false, error);

    /// <summary>A failure after which the neighbour is left alone for at
    /// least <paramref name="minCooldown"/>, however short its cooldown
    /// would otherwise be. Null: the usual cooldown.</summary>
    public static BackhaulSendResult Fail(string error, TimeSpan? minCooldown) => new(false, error) { MinCooldown = minCooldown };

    /// <summary>For a failure: the least time before the neighbour is
    /// dialled again, when the bearer knows the usual cooldown is too
    /// short (a peer whose own call has taken the link, say).</summary>
    public TimeSpan? MinCooldown { get; init; }

    /// <summary>Not sent, not failed: <paramref name="reason"/> says
    /// why, for the log.</summary>
    public static BackhaulSendResult Defer(string reason) => new(false, reason, Deferred: true);
    /// <summary>The neighbour won't take it: <paramref name="reason"/> is
    /// what it said.</summary>
    public static BackhaulSendResult Refuse(string reason) => new(false, reason, Refused: true);
}
