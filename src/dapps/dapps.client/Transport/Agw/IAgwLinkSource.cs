namespace dapps.client.Transport.Agw;

/// <summary>Where the daemon's one <see cref="AgwLink"/> stands.</summary>
public enum AgwLinkState
{
    /// <summary>Not running: AGW is not the bearer and no AGW discovery
    /// channel needs the connection, or no callsign is set.</summary>
    Idle,
    /// <summary>Opening, or about to open, a connection.</summary>
    Connecting,
    /// <summary>Connected: registered, or listening only
    /// (<see cref="AgwLink.ListenOnly"/>).</summary>
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

    /// <summary>
    /// Has <paramref name="onUi"/> called, on the link's reader, with every
    /// UI frame ('U') the node's monitor shows, on whichever link is open,
    /// until the returned handle is disposed. The subscription is the
    /// source's, not one link's: it hears every link opened after it. It
    /// must not block. A monitored frame's CallTo is whatever BPQ last put
    /// there; filter by its port and CallFrom, never its CallTo.
    /// </summary>
    IDisposable SubscribeUi(Action<AgwFrame> onUi);
}
