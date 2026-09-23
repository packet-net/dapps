namespace dapps.client.Backhaul;

/// <summary>
/// Outcome of a backhaul send. <c>Accepted=true</c> means the neighbour
/// confirmed receipt per the bearer's ack contract; the caller can mark
/// the message as forwarded and stop retrying. Otherwise <c>Error</c>
/// carries a human-readable reason for logging. <c>Deferred=true</c>
/// is the third case: the message was not sent, but nothing failed
/// either - the bearer used the link for something else (see
/// <see cref="Defer"/>), so the caller leaves the message queued and
/// records neither a success nor a failure against the route.
/// </summary>
public sealed record BackhaulSendResult(bool Accepted, string? Error, bool Deferred = false)
{
    public static BackhaulSendResult Ok() => new(true, null);
    public static BackhaulSendResult Fail(string error) => new(false, error);
    /// <summary>Not sent, not failed: <paramref name="reason"/> says
    /// what the bearer did with the link instead. Today that is #178's
    /// crossed connect, where the caller ended up serving the peer's
    /// session rather than pushing its own message.</summary>
    public static BackhaulSendResult Defer(string reason) => new(false, reason, Deferred: true);
}
