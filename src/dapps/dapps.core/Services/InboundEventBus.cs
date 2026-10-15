using System.Threading.Channels;
using dapps.client.Backhaul;

namespace dapps.core.Services;

/// <summary>
/// Tiny in-process pub/sub for inbound deliveries. The
/// <see cref="DatabaseAndMqttInbox"/> publishes after persisting +
/// MQTT-injecting; SSE controllers (and any other interested
/// component) subscribe to get a live stream.
///
/// Implementation: per-subscriber bounded <see cref="Channel{T}"/>.
/// Slow subscribers are dropped on overflow rather than blocking
/// publishers - the inbox's job is to persist messages, not to wait
/// for browser tabs to keep up.
/// </summary>
public sealed class InboundEventBus
{
    private readonly object _lock = new();
    private readonly List<Channel<InboundEvent>> _subscribers = new();

    public IDisposable Subscribe(out ChannelReader<InboundEvent> reader)
    {
        var channel = Channel.CreateBounded<InboundEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_lock) _subscribers.Add(channel);
        reader = channel.Reader;
        return new Subscription(this, channel);
    }

    public void Publish(InboundEvent ev)
    {
        Channel<InboundEvent>[] snapshot;
        lock (_lock) snapshot = _subscribers.ToArray();
        foreach (var sub in snapshot)
        {
            // TryWrite never blocks; on a full channel with DropOldest
            // policy the bounded buffer makes room.
            sub.Writer.TryWrite(ev);
        }
    }

    private void Unsubscribe(Channel<InboundEvent> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription(InboundEventBus bus, Channel<InboundEvent> channel) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bus.Unsubscribe(channel);
        }
    }
}

/// <summary>One inbound message, in the shape the SSE stream wants.
/// Detached from <see cref="BackhaulMessage"/> so the bus's contract
/// stays browser-shaped.</summary>
public sealed record InboundEvent(
    DateTime ReceivedAt,
    string Id,
    string SourceCallsign,
    string Destination,
    int PayloadLength,
    int? Ttl,
    string ViaCallsign = "");
