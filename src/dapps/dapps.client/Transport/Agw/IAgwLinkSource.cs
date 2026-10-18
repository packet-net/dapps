namespace dapps.client.Transport.Agw;

/// <summary>Where the daemon's one <see cref="AgwLink"/> stands.</summary>
public enum AgwLinkState
{
    /// <summary>Not running: AGW is not the bearer, or no callsign is set.</summary>
    Idle,
    /// <summary>Opening, or about to open, a connection.</summary>
    Connecting,
    /// <summary>Connected and registered.</summary>
    Open,
    /// <summary>Waiting out a reconnect backoff after a failure.</summary>
    Backoff,
}

/// <summary>
/// Publishes the daemon's one AGW connection, for those that need to
/// use it rather than open their own.
/// </summary>
public interface IAgwLinkSource
{
    AgwLinkState State { get; }

    /// <summary>The link if it is open now, else null.</summary>
    AgwLink? TryGetLink();

    /// <summary>The link if it is open now or opens within
    /// <paramref name="wait"/> while <see cref="State"/> is
    /// <see cref="AgwLinkState.Connecting"/>; otherwise null, at once
    /// when the link is idle or backing off.</summary>
    Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct);

    /// <summary>
    /// The link if it is open now; otherwise, unless the link is
    /// <see cref="AgwLinkState.Idle"/>, asks for it to be reopened at once
    /// (cutting short a backoff or the pause after a settings save) and
    /// waits for it, at most <paramref name="wait"/>. Null if it is not
    /// open by then, or the attempt failed.
    /// </summary>
    Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct);
}
