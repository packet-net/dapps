using dapps.client.Transport.Agw;

namespace dapps.client.Discovery;

/// <summary>
/// Sends AGW discovery's UI frames while the daemon's AGW connection only
/// listens (AGW is not the bearer, so it registers no callsign). XRouter's
/// AGW drops an 'M' from a socket that has registered no callsign, without
/// a word, so such a connection cannot send them itself: they go through
/// the bearer, from the same callsign.
/// </summary>
public interface IUiFrameSender
{
    /// <summary>Sends <paramref name="payload"/> as one UI frame (PID 0xF0)
    /// from <paramref name="from"/> to <paramref name="to"/> on the radio
    /// port the AGW connection numbers <paramref name="agwPort"/> (counting
    /// from 0). <paramref name="agwPorts"/> is that connection's latest
    /// answer to a 'G', which the sender uses to find the same port on the
    /// bearer. Throws <see cref="UiFrameNotSentException"/> when nothing
    /// went to the node, and another exception when the frame may have
    /// gone.</summary>
    Task SendUiAsync(byte agwPort, string from, string to, byte[] payload, AgwPortList? agwPorts, CancellationToken ct);
}

/// <summary>
/// A discovery frame that was not sent and is known not to have gone on
/// air: the node refused it, could not be reached, or the port it would go
/// out on could not be matched to the channel's. The discovery service
/// gives its airtime back and logs one line.
/// </summary>
public sealed class UiFrameNotSentException(string message, Exception? inner = null) : IOException(message, inner);
