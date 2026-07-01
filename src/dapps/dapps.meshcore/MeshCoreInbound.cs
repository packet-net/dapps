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

    /// <summary>Count of fully-decoded BackhaulMessages delivered (observability).</summary>
    public long Delivered { get; private set; }

    public MeshCoreInbound(MeshCoreLink link, IBackhaulInbox inbox, ILogger log)
    {
        _link = link;
        _inbox = inbox;
        _log = log;
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
                    var r = _rx.Ingest(d.Payload, DateTime.UtcNow);
                    if (r.Kind != MeshCoreChannelTransport.Kind.BackhaulComplete) continue;

                    var msg = r.Message!;
                    var source = !string.IsNullOrEmpty(msg.LinkSourceCallsign)
                        ? msg.LinkSourceCallsign!
                        : UnknownSourceCallsign;
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
            }

            if (DateTime.UtcNow >= nextSweep)
            {
                _rx.DropStale(DateTime.UtcNow - ReassemblyTimeout);
                nextSweep = DateTime.UtcNow + SweepInterval;
            }
        }
    }
}
