using System.Text;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// AGW-protocol implementation of <see cref="IDappsOutboundTransport"/>.
/// Each call to <see cref="ConnectAsync"/> asks the node for an AX.25
/// connection over the daemon's one AGW connection (<see cref="IAgwLinkSource"/>),
/// and returns a Stream once the remote confirms. It opens no socket
/// and registers nothing of its own: BPQ hands an inbound call to the
/// first socket registered for the callsign, so a second one would take
/// calls meant for the listener (#272).
///
/// The link's monitor is on, to catch a crossed call: the peer's SABM
/// to us arriving while ours is on its way to it, both ends having
/// dialled at once. Neither end is answering then, so no prompt is
/// coming; <see cref="IDappsConnection.CrossedCall"/> says so, and the
/// session doesn't wait for one.
/// </summary>
public sealed class AgwOutboundTransport(IAgwLinkSource links, ILoggerFactory loggerFactory) : IDappsOutboundTransport
{
    private readonly ILogger logger = loggerFactory.CreateLogger<AgwOutboundTransport>();

    /// <summary>Longest wait for the node to confirm or refuse a connect
    /// request, from the moment it goes out. Monitored frames arriving
    /// meanwhile do not extend it.</summary>
    public static readonly TimeSpan ConnectConfirmDeadline = TimeSpan.FromMinutes(3);

    /// <summary>Longest a dial waits for the link to open when it is not
    /// open: at start-up, just after a settings save, or while it backs
    /// off after a failure (the dial asks for it to be retried at once).</summary>
    public static readonly TimeSpan LinkOpenWait = TimeSpan.FromSeconds(5);

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken stoppingToken)
    {
        if (bearerPort < 0 || bearerPort > AgwFrameTransport.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(bearerPort), $"AGW port must be 0 to {AgwFrameTransport.MaxPort}");
        }
        var portByte = (byte)bearerPort;

        // Not open: a node that is back should not wait out the rest of a
        // backoff (up to 5 minutes) before our mail can go.
        var link = links.TryGetLink() ?? await links.ReopenAsync(LinkOpenWait, stoppingToken);
        if (link is null) throw new AgwLinkDownException(remoteCallsign, links.State);

        // The link registered the callsign in the settings when it opened;
        // after a change it reconnects under the new one shortly.
        if (!string.Equals(link.Callsign, localCallsign, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwLinkDownException(
                $"AGW: the connection to the node is registered as {link.Callsign}, not {localCallsign}; {remoteCallsign} was not called");
        }

        var dial = await link.DialAsync(portByte, localCallsign, remoteCallsign, ConnectConfirmDeadline, stoppingToken, logger);
        return new AgwConnection(dial);
    }

    /// <summary>
    /// A monitored frame that is the peer's SABM to us, direct, on the port
    /// we're calling it on. BPQ's monitor shows that as
    /// " 1:Fm PEER To US &lt;C C P&gt;[12:00:00]" (a connect, as a command).
    /// A SABM through digipeaters (" Via ...") or a SABME ("&lt;?? C P&gt;")
    /// prints differently and isn't counted: the session then falls back
    /// to waiting out the prompt.
    /// </summary>
    internal static bool IsPeerCallingUs(AgwFrame monitored, byte port, string localCallsign, string remoteCallsign) =>
        monitored.Port == port
        && Encoding.ASCII.GetString(monitored.Payload).Contains(
            $":Fm {remoteCallsign} To {localCallsign} <C C", StringComparison.OrdinalIgnoreCase);

    private sealed class AgwConnection(AgwDial dial) : IDappsConnection
    {
        public Stream Stream => dial.Stream;

        public Task CrossedCall => dial.CrossedCall;

        /// <summary>Close without a 'd': the link is a newer session's now.</summary>
        public ValueTask AbandonAsync() => dial.AbandonAsync();

        public ValueTask DisposeAsync() => dial.DisposeAsync();
    }
}

/// <summary>
/// The daemon's AGW connection to the node is not open (AGW is not the
/// bearer, no callsign is set, or it is waiting to reconnect), so no
/// call was made.
/// </summary>
public sealed class AgwLinkDownException : IOException
{
    public AgwLinkDownException(string remoteCallsign, AgwLinkState state)
        : base(state switch
        {
            AgwLinkState.Backoff => $"AGW: the connection to the node is down and did not reopen when retried; {remoteCallsign} was not called",
            AgwLinkState.Connecting => $"AGW: the connection to the node did not open within {AgwOutboundTransport.LinkOpenWait.TotalSeconds:F0}s; {remoteCallsign} was not called",
            _ => $"AGW: there is no connection to the node (AGW is not the bearer, or no callsign is set); {remoteCallsign} was not called",
        })
    {
        State = state;
    }

    public AgwLinkDownException(string message) : base(message)
    {
        State = AgwLinkState.Open;
    }

    /// <summary>Where the link stood when the dial was refused.</summary>
    public AgwLinkState State { get; }
}
