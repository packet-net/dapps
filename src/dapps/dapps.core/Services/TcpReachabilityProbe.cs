using System.Collections.Concurrent;
using System.Net.Sockets;

namespace dapps.core.Services;

/// <summary>
/// "Can I open a TCP connection to host:port?", remembered for a short
/// while (#320). /Health and /Operational answer without a login, so
/// without this an outside poller could make the daemon open a
/// connection to the packet node on every request. Successes and
/// failures are both remembered; callers arriving while a probe is in
/// flight share its answer.
/// </summary>
public sealed class TcpReachabilityProbe(TimeProvider? timeProvider = null, TimeSpan? lifetime = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(15);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan ttl = lifetime ?? DefaultLifetime;
    private readonly ConcurrentDictionary<(string, int), Entry> cache = new();

    private sealed record Entry(DateTimeOffset At, Task<bool> Result);

    /// <summary>How many real connection attempts have been made.</summary>
    public int Attempts => attempts;
    private int attempts;

    public Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return Task.FromResult(false);
        var now = clock.GetUtcNow();
        var key = (host.ToLowerInvariant(), port);
        while (true)
        {
            if (cache.TryGetValue(key, out var existing))
            {
                if (now - existing.At < ttl) return existing.Result;
                var fresh = new Entry(now, ProbeAsync(host, port, timeout));
                if (cache.TryUpdate(key, fresh, existing)) return fresh.Result;
                continue;
            }
            var created = new Entry(now, ProbeAsync(host, port, timeout));
            if (cache.TryAdd(key, created)) return created.Result;
        }
    }

    private async Task<bool> ProbeAsync(string host, int port, TimeSpan timeout)
    {
        Interlocked.Increment(ref attempts);
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}
