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
/// The address is whatever <c>Connection.RemoteIpAddress</c> says. Today
/// that is the TCP peer: behind a local reverse proxy or gateway every
/// client looks like loopback. A loopback address is never locked out,
/// so one bad client there can't lock out the rest; guessing through it
/// is still limited by the cost of one password hash per try. If the app
/// is later taught to trust forwarded headers from a known proxy, that
/// rewrites this address and real clients are then limited individually.
/// IPv6 clients are keyed by their /64.
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

    /// <summary>
    /// Called before checking a credential. If the address is locked out,
    /// returns the time left and the caller must refuse without checking.
    /// Otherwise the attempt is counted as a failure straight away, under
    /// the lock, and returns null: concurrent guesses can not slip past
    /// while the slow hash runs, because each one is counted before it
    /// starts. Call <see cref="Succeeded"/> when the credential turns out
    /// to be right.
    /// </summary>
    public TimeSpan? Begin(string scope, IPAddress? address)
    {
        if (Exempt(address)) return null;
        var key = Key(scope, address);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (entries.TryGetValue(key, out var e))
            {
                if (now - e.Last > Forget) e.Failures = 0;
                if (e.LockedUntil > now) return e.LockedUntil - now;
            }
            else
            {
                if (entries.Count >= MaxEntries) Evict(now);
                entries[key] = e = new Entry();
            }
            e.Failures++;
            e.Last = now;
            Relock(e, now);
            return null;
        }
    }

    /// <summary>
    /// The credential was right: take back the failure <see cref="Begin"/>
    /// counted. With <paramref name="clearAll"/> (the dashboard, where a
    /// right password proves the operator is at the keyboard) the address
    /// is forgiven completely. For app tokens it is not: someone holding
    /// one valid token could otherwise interleave it with guesses at
    /// another app's token and never be slowed down.
    /// </summary>
    public void Succeeded(string scope, IPAddress? address, bool clearAll = false)
    {
        if (Exempt(address)) return;
        var key = Key(scope, address);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var e)) return;
            if (clearAll) { entries.Remove(key); return; }
            if (e.Failures > 0) e.Failures--;
            Relock(e, time.GetUtcNow());
        }
    }

    private static void Relock(Entry e, DateTimeOffset now)
    {
        if (e.Failures <= FreeFailures) { e.LockedUntil = default; return; }
        var seconds = Math.Min(Math.Pow(2, e.Failures - FreeFailures), MaxLockout.TotalSeconds);
        e.LockedUntil = now + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Parses the "ip:port" form MQTT endpoints use.</summary>
    public static IPAddress? AddressOf(string? endpoint) =>
        IPEndPoint.TryParse(endpoint ?? "", out var ep) ? ep.Address : null;

    private static bool Exempt(IPAddress? a) => a is null || IPAddress.IsLoopback(a);

    private static string Key(string scope, IPAddress? a)
    {
        if (a!.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // One customer usually holds a whole /64; key on that so
            // rotating through its addresses doesn't dodge the limit.
            var b = a.GetAddressBytes();
            Array.Clear(b, 8, 8);
            a = new IPAddress(b);
        }
        return scope + "|" + a;
    }

    /// <summary>Drops the quiet entries; if that frees nothing, the oldest
    /// tenth, so a flood of addresses can't wipe everyone's lockouts at once.</summary>
    private void Evict(DateTimeOffset now)
    {
        foreach (var k in entries.Where(kv => now - kv.Value.Last > Forget).Select(kv => kv.Key).ToList())
            entries.Remove(k);
        if (entries.Count < MaxEntries) return;
        foreach (var k in entries.OrderBy(kv => kv.Value.Last).Take(MaxEntries / 10).Select(kv => kv.Key).ToList())
            entries.Remove(k);
    }
}
