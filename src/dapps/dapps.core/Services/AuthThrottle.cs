using System.Net;

namespace dapps.core.Services;

/// <summary>
/// Slows down guessing of credentials (dashboard password, app tokens),
/// per remote address. A few failures are free; after that each further
/// failure locks that address out for a doubling time (2 s, 4 s, ... up
/// to <see cref="MaxLockout"/>). The cap is short on purpose: someone
/// hammering from their own address can never keep the real operator
/// locked out for more than a minute, and only the address they are
/// sending from is affected. Failures are forgotten after
/// <see cref="Forget"/> without any, and a success clears the address.
///
/// The address is whatever <c>Connection.RemoteIpAddress</c> says, so
/// it is the real client only where forwarded headers are trusted from
/// a known proxy (the forwarded-header handling does that rewrite). A
/// loopback address is never locked out: behind a local gateway every
/// client looks like 127.0.0.1, and one bad client must not lock out
/// the rest. Guessing through it is still limited by the cost of one
/// password hash per try.
/// </summary>
public sealed class AuthThrottle(TimeProvider time)
{
    public const int FreeFailures = 5;
    public static readonly TimeSpan MaxLockout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Forget = TimeSpan.FromMinutes(15);
    private const int MaxEntries = 10_000;

    private sealed class Entry { public int Failures; public DateTimeOffset LockedUntil; public DateTimeOffset Last; }

    private readonly Dictionary<string, Entry> entries = new();
    private readonly object gate = new();

    /// <summary>Not null (the time left) when <paramref name="address"/>
    /// is locked out for <paramref name="scope"/> ("login", "rest", "mqtt").</summary>
    public TimeSpan? LockedFor(string scope, IPAddress? address)
    {
        if (Exempt(address)) return null;
        var key = Key(scope, address);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var e)) return null;
            var now = time.GetUtcNow();
            if (now - e.Last > Forget) { entries.Remove(key); return null; }
            return e.LockedUntil > now ? e.LockedUntil - now : null;
        }
    }

    public void Failed(string scope, IPAddress? address)
    {
        if (Exempt(address)) return;
        var key = Key(scope, address);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (entries.Count >= MaxEntries) Prune(now);
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry();
            if (now - e.Last > Forget) e.Failures = 0;
            e.Failures++;
            e.Last = now;
            if (e.Failures > FreeFailures)
            {
                var seconds = Math.Min(Math.Pow(2, e.Failures - FreeFailures), MaxLockout.TotalSeconds);
                e.LockedUntil = now + TimeSpan.FromSeconds(seconds);
            }
        }
    }

    public void Succeeded(string scope, IPAddress? address)
    {
        if (Exempt(address)) return;
        lock (gate) entries.Remove(Key(scope, address));
    }

    /// <summary>Parses the "ip:port" form MQTT endpoints use.</summary>
    public static IPAddress? AddressOf(string? endpoint) =>
        IPEndPoint.TryParse(endpoint ?? "", out var ep) ? ep.Address : null;

    private static bool Exempt(IPAddress? a) => a is null || IPAddress.IsLoopback(a);

    private static string Key(string scope, IPAddress? a)
    {
        if (a!.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return scope + "|" + a;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var k in entries.Where(kv => now - kv.Value.Last > Forget).Select(kv => kv.Key).ToList())
            entries.Remove(k);
        if (entries.Count >= MaxEntries) entries.Clear();
    }
}
