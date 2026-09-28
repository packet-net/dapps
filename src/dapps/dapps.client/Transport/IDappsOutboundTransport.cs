namespace dapps.client.Transport;

/// <summary>
/// A transport that originates a stream-style AX.25 connection from a local
/// callsign to a remote one through a packet node, and hands back a duplex
/// byte stream over which DAPPS protocol bytes can flow.
///
/// Today this is implemented over AGW. The shape is deliberately thin so
/// other implementations (RHP via WebSocket; future node interfaces) can
/// slot in alongside. The transport is responsible for:
///   - reaching the node
///   - identifying the local callsign as the source
///   - asking the node to connect to the remote callsign on a given bearer port
///   - returning a Stream over which raw bytes flow once connected
/// It is not responsible for any DAPPS-protocol semantics - that lives in
/// <see cref="DappsProtocolClient"/>.
/// </summary>
public interface IDappsOutboundTransport
{
    Task<IDappsConnection> ConnectAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken stoppingToken);
}

/// <summary>
/// A live stream-style connection to a remote callsign. Disposing closes the
/// underlying network resources.
/// </summary>
public interface IDappsConnection : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>
    /// Fires when this node has taken up a newer session with the same
    /// peer, so the link is that one's now (BPQ moves a link to the newest
    /// socket for the callsign pair). The owner stops using this one and
    /// closes it with <see cref="AbandonAsync"/>.
    /// </summary>
    CancellationToken Retired => CancellationToken.None;

    /// <summary>
    /// Completes if the peer's own call to us crossed ours, both nodes
    /// dialling at once: then no prompt is coming, as neither end was
    /// answering. Never completes on a transport that can't tell.
    /// </summary>
    Task CrossedCall => NeverCrossed;

    /// <summary>
    /// Close without asking the node to disconnect: after
    /// <see cref="Retired"/> the link belongs to a newer session, and a
    /// disconnect is applied by callsign pair, so it would take the link
    /// from that one.
    /// </summary>
    ValueTask AbandonAsync() => DisposeAsync();

    /// <summary>What <see cref="CrossedCall"/> is when a transport can't tell.</summary>
    protected static readonly Task NeverCrossed = new TaskCompletionSource().Task;
}
