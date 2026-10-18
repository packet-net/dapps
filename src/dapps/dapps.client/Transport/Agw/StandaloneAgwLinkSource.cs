using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// An <see cref="IAgwLinkSource"/> for a caller with no listener of its
/// own (the client harness, tests): opens one <see cref="AgwLink"/> on
/// first use, and a fresh one if that has closed. Inbound calls are not
/// taken: each is logged and disconnected. In the daemon the link is
/// the inbound service's, never one of these.
/// </summary>
public sealed class StandaloneAgwLinkSource(
    string host,
    int port,
    string callsign,
    ILoggerFactory loggerFactory,
    IDappsTxGate? txGate = null,
    TimeProvider? clock = null) : IAgwLinkSource, IAsyncDisposable
{
    private readonly ILogger logger = loggerFactory.CreateLogger<StandaloneAgwLinkSource>();
    private readonly SemaphoreSlim opening = new(1, 1);
    private AgwLink? link;

    public AgwLinkState State => TryGetLink() is null ? AgwLinkState.Connecting : AgwLinkState.Open;

    public AgwLink? TryGetLink()
    {
        var current = Volatile.Read(ref link);
        return current is { Closed.IsCompleted: false } ? current : null;
    }

    /// <summary>Opens the link if it is not open; <paramref name="wait"/>
    /// is not applied, as the open has its own limits.</summary>
    public async Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct)
    {
        await opening.WaitAsync(ct);
        try
        {
            if (TryGetLink() is { } open) return open;
            if (link is { } old) await old.DisposeAsync();
            var opened = await AgwLink.OpenAsync(host, port, callsign, Refuse, logger, txGate, clock, ct: ct);
            Volatile.Write(ref link, opened);
            return opened;
        }
        finally
        {
            opening.Release();
        }
    }

    private void Refuse(AgwLinkSession session)
    {
        logger.LogWarning("AGW: {0} called {1}, but nothing here takes calls; disconnecting it", session.Remote, session.Local);
        session.Remove();
        _ = session.Stream.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (link is { } open) await open.DisposeAsync();
    }
}
