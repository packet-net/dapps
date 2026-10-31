using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// The AGW connections for neighbours' Dial as callsigns (#431).
/// </summary>
public interface IAgwDialAsLinks
{
    /// <summary>
    /// The connection registered as <paramref name="callsign"/>, opened
    /// now if there isn't one. Hold the result for as long as a call on it
    /// is in progress or connected, and dispose it after. Throws
    /// <see cref="AgwLinkDownException"/> when it can't be opened.
    /// </summary>
    Task<AgwDialAsUse> AcquireAsync(string callsign, CancellationToken ct);
}

/// <summary>
/// One AGW connection per Dial as callsign (#431). LinBPQ sends every call
/// on an AGW connection from the first callsign registered on it, whatever
/// the 'C' frame says, so a call from another callsign needs a connection
/// of its own. Each is registered ('X') with its callsign, opened on first
/// use, and closed once nothing has used it for <see cref="IdleClose"/>;
/// the next call opens it again.
///
/// These connections only make calls (<see cref="AgwLink.RefusesCalls"/>):
/// DAPPS answers on its own callsign alone, on the main AGW connection,
/// which is unchanged. A call to a Dial as callsign is disconnected. On
/// LinBPQ such a call never reaches DAPPS anyway, as the node only hands
/// AGW the calls for an APPLICATION callsign; nor can one take a call meant
/// for our own callsign, as BPQ picks the connection by the callsign it
/// registered (#272). XRouter hands a call to whichever connection
/// registered the callsign called.
///
/// Every frame goes through the same TX gate as the main connection, so
/// the kill switch and Stop TX cover these calls too. A save that changes
/// the node's host or AGW port closes a connection opened to the old one
/// at its next use.
/// </summary>
public sealed class AgwDialAsLinks(
    IOptionsMonitor<SystemOptions> options,
    ILogger<AgwDialAsLinks> logger,
    IDappsTxGate? txGate = null,
    TimeProvider? clock = null) : IAgwDialAsLinks, IAsyncDisposable
{
    /// <summary>How long a connection stays open with no call on it.</summary>
    public static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(2);

    /// <summary>Longest the node may take to accept the connection and
    /// answer its registration.</summary>
    public static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(15);

    /// <summary><see cref="IdleClose"/>, shorter in tests.</summary>
    internal TimeSpan Linger { get; init; } = IdleClose;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>The Dial as callsigns with a connection open or opening, for tests and logs.</summary>
    internal IReadOnlyCollection<string> Open
    {
        get { lock (gate) return [.. entries.Keys]; }
    }

    public async Task<AgwDialAsUse> AcquireAsync(string callsign, CancellationToken ct)
    {
        var key = DestinationParser.StationKey(callsign);
        var opts = options.CurrentValue;
        Entry entry;
        Entry? stale = null;
        lock (gate)
        {
            if (disposed) throw new AgwLinkDownException($"AGW: DAPPS is stopping; nothing was called from {key}");
            if (entries.TryGetValue(key, out var existing)
                && (!string.Equals(existing.Host, opts.NodeHost, StringComparison.OrdinalIgnoreCase) || existing.Port != opts.AgwPort))
            {
                entries.Remove(key);
                stale = existing;
                existing = null;
            }
            if (existing is null)
            {
                existing = new Entry(key, opts.NodeHost, opts.AgwPort);
                entries[key] = existing;
                existing.Opening = Task.Run(() => OpenAsync(existing));
            }
            entry = existing;
            entry.Users++;
            entry.Idle?.Dispose();
            entry.Idle = null;
        }
        if (stale is not null)
        {
            logger.LogInformation("AGW: the node's address has changed; closing the Dial as {0} connection to the old one", key);
            _ = CloseAsync(stale);
        }

        try
        {
            var link = await entry.Opening.WaitAsync(ct);
            return new AgwDialAsUse(link, () => Release(entry));
        }
        catch
        {
            Release(entry);
            throw;
        }
    }

    private async Task<AgwLink> OpenAsync(Entry entry)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            bounded.CancelAfter(OpenWait);
            logger.LogInformation("AGW: opening a connection to the node for Dial as {0}", entry.Callsign);
            var link = await AgwLink.OpenAsync(
                entry.Host, entry.Port, entry.Callsign, _ => { }, logger, txGate, time,
                stopping: lifetime.Token, ct: bounded.Token, refuseCalls: true);
            lock (gate) entry.Link = link;
            _ = link.Closed.ContinueWith(_ => OnClosed(entry, link), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return link;
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (entries.TryGetValue(entry.Callsign, out var held) && ReferenceEquals(held, entry)) entries.Remove(entry.Callsign);
            }
            var why = ex is OperationCanceledException && !lifetime.IsCancellationRequested
                ? $"the node didn't answer within {OpenWait.TotalSeconds:F0}s"
                : ex.Message;
            logger.LogWarning("AGW: couldn't open a connection to the node for Dial as {0}: {1}", entry.Callsign, why);
            throw new AgwLinkDownException($"AGW: couldn't open a connection to the node for Dial as {entry.Callsign} ({why}); nothing was called");
        }
    }

    private void OnClosed(Entry entry, AgwLink link)
    {
        bool current;
        lock (gate)
        {
            current = entries.TryGetValue(entry.Callsign, out var held) && ReferenceEquals(held, entry);
            if (current)
            {
                entries.Remove(entry.Callsign);
                entry.Idle?.Dispose();
                entry.Idle = null;
            }
        }
        if (current && link.CloseReason is { } reason and not OperationCanceledException)
        {
            logger.LogWarning("AGW: the Dial as {0} connection to the node closed ({1}); the next call reopens it", entry.Callsign, reason.Message);
        }
    }

    private void Release(Entry entry)
    {
        lock (gate)
        {
            if (--entry.Users > 0 || disposed) return;
            if (!entries.TryGetValue(entry.Callsign, out var held) || !ReferenceEquals(held, entry)) return;
            entry.Idle?.Dispose();
            entry.Idle = time.CreateTimer(_ => IdleExpired(entry), null, Linger, Timeout.InfiniteTimeSpan);
        }
    }

    private void IdleExpired(Entry entry)
    {
        lock (gate)
        {
            if (entry.Users > 0) return;
            if (!entries.TryGetValue(entry.Callsign, out var held) || !ReferenceEquals(held, entry)) return;
            entries.Remove(entry.Callsign);
        }
        logger.LogInformation("AGW: closing the Dial as {0} connection: no call on it for {1:F0}s", entry.Callsign, Linger.TotalSeconds);
        _ = CloseAsync(entry);
    }

    private static async Task CloseAsync(Entry entry)
    {
        entry.Idle?.Dispose();
        try
        {
            var link = await entry.Opening;
            await link.DisposeAsync();
        }
        catch
        {
            // It never opened, or is already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            all = [.. entries.Values];
            entries.Clear();
        }
        await lifetime.CancelAsync();
        foreach (var entry in all) await CloseAsync(entry);
        lifetime.Dispose();
    }

    private sealed class Entry(string callsign, string host, int port)
    {
        public string Callsign { get; } = callsign;
        public string Host { get; } = host;
        public int Port { get; } = port;
        public Task<AgwLink> Opening { get; set; } = Task.FromException<AgwLink>(new InvalidOperationException());
        public AgwLink? Link { get; set; }
        public int Users { get; set; }
        public ITimer? Idle { get; set; }
    }
}

/// <summary>
/// A hold on a Dial as callsign's AGW connection (<see cref="IAgwDialAsLinks"/>):
/// it stays open while any is held. <see cref="Source"/> offers it to an
/// <see cref="AgwOutboundTransport"/>. Disposing it twice is harmless.
/// </summary>
public sealed class AgwDialAsUse : IDisposable
{
    private Action? release;

    internal AgwDialAsUse(AgwLink link, Action release)
    {
        Link = link;
        this.release = release;
        Source = new OneLinkSource(link);
    }

    public AgwLink Link { get; }

    public IAgwLinkSource Source { get; }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();

    /// <summary>The one connection, as a link source: open until it closes.</summary>
    private sealed class OneLinkSource(AgwLink link) : IAgwLinkSource
    {
        public AgwLinkState State => link.Closed.IsCompleted ? AgwLinkState.Backoff : AgwLinkState.Open;

        public AgwLink? TryGetLink() => link.Closed.IsCompleted ? null : link;

        public Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult(TryGetLink());

        public Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult(TryGetLink());

        public IDisposable SubscribeUi(Action<AgwFrame> onUi) => throw new NotSupportedException("A Dial as connection carries no discovery");
    }
}
