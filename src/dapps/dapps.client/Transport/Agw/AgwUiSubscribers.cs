using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// The UI-frame subscribers of an <see cref="IAgwLinkSource"/>: held by
/// the source rather than by any one <see cref="AgwLink"/>, so a
/// subscription outlives the link it started on and hears every link
/// the source opens after it.
/// </summary>
public sealed class AgwUiSubscribers(ILogger logger)
{
    private readonly Lock gate = new();
    private readonly List<Action<AgwFrame>> subscribers = [];
    private TaskCompletionSource emptied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while anyone is subscribed.</summary>
    public bool Any
    {
        get { lock (gate) return subscribers.Count > 0; }
    }

    /// <summary>Has <paramref name="onUi"/> called with every UI frame
    /// handed to <see cref="Deliver"/> until the returned handle is
    /// disposed.</summary>
    public IDisposable Add(Action<AgwFrame> onUi)
    {
        lock (gate) subscribers.Add(onUi);
        return new Subscription(this, onUi);
    }

    /// <summary>Completes once nobody is subscribed: at once if nobody is.</summary>
    public Task WhenEmptyAsync()
    {
        lock (gate) return subscribers.Count == 0 ? Task.CompletedTask : emptied.Task;
    }

    /// <summary>Has <paramref name="link"/>'s UI frames handed to the
    /// subscribers until the returned handle is disposed or the link ends.</summary>
    public IDisposable Attach(AgwLink link) => link.SubscribeUi(Deliver);

    /// <summary>Hands <paramref name="frame"/> to every subscriber, on the
    /// link's reader. One that throws is logged, and the others still
    /// hear the frame.</summary>
    public void Deliver(AgwFrame frame)
    {
        Action<AgwFrame>[] current;
        lock (gate) current = [.. subscribers];
        foreach (var onUi in current)
        {
            try
            {
                onUi(frame);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AGW: a UI frame subscriber failed on a frame from {0} on port {1}; carrying on", frame.CallFrom, frame.Port);
            }
        }
    }

    private void Remove(Action<AgwFrame> onUi)
    {
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (!subscribers.Remove(onUi) || subscribers.Count > 0) return;
            done = emptied;
            emptied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    private sealed class Subscription(AgwUiSubscribers owner, Action<AgwFrame> onUi) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Remove(onUi);
        }
    }
}
