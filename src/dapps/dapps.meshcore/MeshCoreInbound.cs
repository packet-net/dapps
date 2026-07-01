using dapps.client.Backhaul;
using Microsoft.Extensions.Logging;

namespace dapps.meshcore;

/// <summary>
/// Inbound side of the MeshCore bearer: drains binary channel-data datagrams from
/// the <see cref="MeshCoreLink"/>, reassembles + decodes them into
/// <see cref="BackhaulMessage"/>s, and hands each to <see cref="IBackhaulInbox"/>.
///
/// The channel is anonymous, so the sender is taken from the in-band
/// <c>LinkSourceCallsign</c> (stamped by the sending bearer), falling back to a
/// sentinel — the same pattern as the UDP datagram listener.
/// </summary>
public sealed class MeshCoreInbound
{
    public const string UnknownSourceCallsign = "MESHCORE";
    private static readonly TimeSpan ReassemblyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly MeshCoreLink _link;
    private readonly IBackhaulInbox _inbox;
    private readonly ILogger _log;
    private readonly MeshCoreChannelTransport _rx = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly MeshCoreReliability? _reliability;
    private readonly Func<BackhaulMessage, CancellationToken, Task<BackhaulSendResult>>? _sendAck;
    private readonly string _localCallsign;
    private readonly Func<BackhaulMessage, bool>? _dropForTest;
    // Idempotency (#26): ids already delivered to the app, so a resend (after a lost
    // ACK) isn't delivered twice. Single-threaded (drained on one loop); window > the
    // reliability lifetime so we remember long enough to cover resends.
    private readonly Dictionary<string, DateTime> _delivered = new(StringComparer.Ordinal);
    private static readonly TimeSpan DeliveredDedupWindow = TimeSpan.FromMinutes(10);

    /// <summary>Count of fully-decoded BackhaulMessages delivered (observability).</summary>
    public long Delivered { get; private set; }

    public MeshCoreInbound(
        MeshCoreLink link, IBackhaulInbox inbox, ILogger log,
        MeshCoreReliability? reliability = null,
        Func<BackhaulMessage, CancellationToken, Task<BackhaulSendResult>>? sendAck = null,
        string? localCallsign = null,
        Func<BackhaulMessage, bool>? dropForTest = null)
    {
        _link = link;
        _inbox = inbox;
        _log = log;
        _reliability = reliability;
        _sendAck = sendAck;
        _localCallsign = localCallsign ?? "";
        _dropForTest = dropForTest;
        _link.MessageWaiting += () => { try { _wake.Release(); } catch { } };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var nextSweep = DateTime.UtcNow + SweepInterval;
        while (!ct.IsCancellationRequested)
        {
            // Wake on the MSG_WAITING push, or poll every 800ms as a safety net.
            // Cancel the loser so we don't leak an orphaned semaphore waiter each
            // iteration (which would also steal future MSG_WAITING releases).
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var wake = _wake.WaitAsync(linked.Token);
                var poll = Task.Delay(800, linked.Token);
                await Task.WhenAny(wake, poll);
                linked.Cancel();
                try { await Task.WhenAll(wake, poll); } catch { /* loser cancelled */ }
            }
            if (ct.IsCancellationRequested) break;

            var batch = await _link.DrainAsync(ct);
            if (batch is not null)
            {
                foreach (var d in batch.Data)
                {
                    var now = DateTime.UtcNow;
                    var r = _rx.Ingest(d.Payload, now);
                    if (r.Kind != MeshCoreChannelTransport.Kind.BackhaulComplete) continue;
                    var msg = r.Message!;

                    // ACK control frames are consumed here UNCONDITIONALLY (never
                    // delivered to the app), whether or not local reliability is on.
                    if (MeshCoreReliability.IsAck(msg))
                    {
                        var acked = MeshCoreReliability.AckedId(msg);
                        if (acked is not null && _reliability is not null && _reliability.OnAck(acked))
                            _log.LogInformation("MeshCore: ACK confirmed {0}", acked);
                        continue;
                    }

                    // Test-only induced loss (soak): drop before deliver + ack.
                    if (_dropForTest is not null && _dropForTest(msg))
                    {
                        _log.LogWarning("MeshCore: [test] dropped {0}", msg.Id);
                        continue;
                    }

                    var source = !string.IsNullOrEmpty(msg.LinkSourceCallsign)
                        ? msg.LinkSourceCallsign!
                        : UnknownSourceCallsign;

                    // Idempotency (#26): a resend after a lost ACK reassembles into the
                    // same id — deliver to the app only once, but still ACK every copy
                    // so the sender can stop resending.
                    if (MarkDeliveredIfNew(msg.Id, now))
                    {
                        try
                        {
                            await _inbox.DeliverAsync(msg, source, ct);
                            Delivered++;
                            _log.LogInformation("MeshCore: delivered {0} from {1} (dst={2}, snr={3:0.0}dB)",
                                msg.Id, source, msg.Destination, d.SnrDb);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "MeshCore inbox delivery failed for {0}", msg.Id);
                        }
                    }
                    else
                    {
                        _log.LogDebug("MeshCore: duplicate {0} already delivered - re-ACK only", msg.Id);
                    }

                    // ACK a data message addressed to us (even duplicates).
                    if (_reliability is not null && _sendAck is not null && IsForLocal(msg))
                    {
                        try
                        {
                            var ack = MeshCoreReliability.BuildAck(msg, _localCallsign, Guid.NewGuid().ToString("N")[..7]);
                            var res = await _sendAck(ack, ct);
                            if (!res.Accepted)
                                _log.LogDebug("MeshCore: ACK for {0} not sent: {1}", msg.Id, res.Error);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning("MeshCore: failed to ACK {0}: {1}", msg.Id, ex.Message);
                        }
                    }
                }
            }

            if (DateTime.UtcNow >= nextSweep)
            {
                _rx.DropStale(DateTime.UtcNow - ReassemblyTimeout);
                nextSweep = DateTime.UtcNow + SweepInterval;
            }
        }
    }

    private bool IsForLocal(BackhaulMessage m) =>
        !string.IsNullOrEmpty(_localCallsign)
        && string.Equals(m.Destination, _localCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this id hasn't been delivered within the dedup window (and
    /// records it); false if it's a duplicate. Prunes stale ids opportunistically.</summary>
    private bool MarkDeliveredIfNew(string id, DateTime now)
    {
        if (_delivered.Count > 0)
        {
            var cutoff = now - DeliveredDedupWindow;
            foreach (var k in _delivered.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _delivered.Remove(k);
        }
        if (_delivered.ContainsKey(id)) return false;
        _delivered[id] = now;
        return true;
    }
}
