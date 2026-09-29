using System.Collections.Concurrent;

namespace dapps.client.Backhaul;

/// <summary>
/// What a node remembers between sessions about streams that went out of
/// step (<see cref="ExchangeSession"/>), so that a fault which comes back
/// every time can't have two nodes redial each other with no cooldown
/// until the message expires. A stale frame from BPQ at the edge of range
/// is a one-off: it damages one message, once, and the next link is fine.
/// Two kinds of fault repeat:
/// <list type="bullet">
/// <item>One message's payload arrives damaged every time: a message whose
/// id doesn't hash from its payload, or a path that isn't 8-bit clean
/// (BPQ's Telnet bridge rewrites LF to CR). The second time the same id
/// arrives damaged it is answered <c>bad</c>, as before the out-of-step
/// rule: the sender sends it once more plain, and fails it with a
/// cooldown if that's no good either.</item>
/// <item>Something other than DAPPS keeps answering on the link, such as a
/// node's "Returned to node" once the far end has gone, or a path that
/// adds or drops bytes. The second session in a row with one neighbour
/// that ends out of step counts as a break: the oldest of ours fails, for
/// one cooldown, and the session hangs up without a <c>quit</c>, so a
/// peer that dialled counts it as a break too.</item>
/// </list>
/// In memory only, and small: a restart forgets it, as it does cooldowns.
/// </summary>
public sealed class OutOfStepMemory
{
    /// <summary>The node's own, which every session uses unless given another.</summary>
    public static OutOfStepMemory Shared { get; } = new();

    /// <summary>Damaged ids kept; the oldest go first.</summary>
    public const int MaxIds = 256;

    private readonly ConcurrentDictionary<string, long> damaged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> inARow = new(StringComparer.OrdinalIgnoreCase);
    private long order;

    /// <summary>
    /// Records that the payload of <paramref name="id"/> arrived damaged.
    /// True if it had before: the fault is in that message or the path,
    /// not a stale frame.
    /// </summary>
    public bool ArrivedDamaged(string id)
    {
        var before = damaged.ContainsKey(id);
        damaged[id] = Interlocked.Increment(ref order);
        if (damaged.Count > MaxIds)
        {
            foreach (var old in damaged.OrderBy(kv => kv.Value).Take(damaged.Count - MaxIds).ToList())
            {
                damaged.TryRemove(old.Key, out _);
            }
        }
        return before;
    }

    /// <summary>A session with <paramref name="peer"/> ended out of step:
    /// how many have now, in a row.</summary>
    public int EndedOutOfStep(string peer) => inARow.AddOrUpdate(peer, 1, (_, n) => n + 1);

    /// <summary>How many sessions with <paramref name="peer"/> in a row have ended out of step so far.</summary>
    public int InARow(string peer) => inARow.TryGetValue(peer, out var n) ? n : 0;

    /// <summary>A session with <paramref name="peer"/> that got going ended any other way.</summary>
    public void EndedInStep(string peer) => inARow.TryRemove(peer, out _);
}
