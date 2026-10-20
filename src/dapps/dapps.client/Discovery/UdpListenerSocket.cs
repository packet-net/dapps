using System.Net;
using System.Net.Sockets;

namespace dapps.client.Discovery;

/// <summary>
/// The UDP datagram listener's bound socket, shared with UDP discovery
/// (#383). While the listener holds its port, beacons and solicits go
/// out from that socket, so their source port is the port we listen
/// on and an older node that records the source port (no udpport
/// field) learns an endpoint that works. Beacons advertise
/// <see cref="BoundPort"/>, so they only ever name a port something is
/// actually listening on.
///
/// Thread safety: the listener's receive loop and discovery sends run
/// on different threads. One receive and one send in flight together
/// on a datagram socket is safe; sends are serialised here so two
/// beacons never overlap on it. A send that races the listener
/// stopping fails and the caller falls back to its own socket.
/// </summary>
public sealed class UdpListenerSocket
{
    private sealed record Bound(Socket Socket, int Port);

    private volatile Bound? _bound;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>The port the listener is bound to, or null when it is
    /// disabled, failed to bind or has stopped.</summary>
    public int? BoundPort => _bound?.Port;

    /// <summary>Called by the listener once its socket is bound. Enables
    /// multicast loopback so other instances on the same host still
    /// hear our beacons, as they did from the old dedicated socket.
    /// TTL and outgoing interface stay at the system defaults, the
    /// same as before.</summary>
    public void Attach(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        if (socket.AddressFamily == AddressFamily.InterNetwork)
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        _bound = new Bound(socket, port);
    }

    /// <summary>Called by the listener before it closes its socket.
    /// Only clears the slot if it still holds <paramref name="socket"/>.</summary>
    public void Detach(Socket socket)
    {
        var current = _bound;
        if (current is not null && ReferenceEquals(current.Socket, socket))
            Interlocked.CompareExchange(ref _bound, null, current);
    }

    /// <summary>Send <paramref name="bytes"/> to <paramref name="destination"/>
    /// from the listener's socket. Returns false, having sent nothing,
    /// when no socket is attached, the address family doesn't match, or
    /// the socket went away mid-send; the caller then sends from its own
    /// socket instead.</summary>
    public async Task<bool> TrySendAsync(byte[] bytes, IPEndPoint destination, CancellationToken ct)
    {
        var bound = _bound;
        if (bound is null || bound.Socket.AddressFamily != destination.AddressFamily)
            return false;
        await _sendLock.WaitAsync(ct);
        try
        {
            await bound.Socket.SendToAsync(bytes, SocketFlags.None, destination, ct);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException) when (!ReferenceEquals(_bound, bound))
        {
            // The listener stopped while we were sending.
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
