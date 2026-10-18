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
    /// from <paramref name="from"/> to <paramref name="to"/> on DAPPS's
    /// bearer port <paramref name="bearerPort"/> (counting from 0, as on
    /// AGW). Throws when the node has not taken it.</summary>
    Task SendUiAsync(byte bearerPort, string from, string to, byte[] payload, CancellationToken ct);
}
