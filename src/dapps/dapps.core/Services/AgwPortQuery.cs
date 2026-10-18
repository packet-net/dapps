using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.Services;

/// <summary>
/// Asks the configured BPQ host, with an AGW <c>'G'</c>, for the list of
/// radio ports it has configured. Used by the dashboard so operators can
/// pick a labelled port ("Port 1 - VHF FM 144.950 1200 baud") instead of
/// typing a 0-indexed integer when adding a neighbour or a discovery
/// channel.
///
/// <para>
/// BPQ replies to a single <c>'G'</c> frame with one <c>'G'</c> frame
/// whose payload is a NUL-terminated ASCII string of the form
/// <c>"COUNT;DESC1;DESC2;...;"</c>. Some implementations don't include
/// the count and just emit the descriptions; we tolerate both.
/// </para>
///
/// <para>
/// While the daemon's one AGW connection to that host and port is open
/// it asks there (<see cref="AgwLink.QueryPortsAsync"/>): the keepalive's
/// last answer if it is fresh, else a <c>'G'</c> of its own, which
/// nothing else on the connection waits for. When it is not open (the
/// setup wizard trying a node before the daemon is set up, the node just
/// restarted, or the settings just changed to another host) it opens a
/// short-lived connection of its own instead, which sends the <c>'G'</c>
/// and nothing else: no <c>'X'</c>, so it never registers a callsign
/// and never takes a call meant for the daemon (#272). Either way the
/// wait is bounded by the same 3 s.
/// </para>
/// </summary>
public class AgwPortQuery(IAgwLinkSource links, ILogger<AgwPortQuery> logger)
{
    private static readonly TimeSpan QueryTimeout = AgwLink.PortQueryTimeout;

    // Virtual so unit tests can substitute a fake; not sealed for the
    // same reason. Callers receive it as a concrete type via DI.
    public virtual async Task<IReadOnlyList<AgwPortInfo>> QueryAsync(
        string host, int agwPort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("AGW host is required", nameof(host));
        if (agwPort <= 0 || agwPort > 65535)
            throw new ArgumentException($"AGW TCP port out of range: {agwPort}", nameof(agwPort));

        if (links.TryGetLink() is { } link
            && string.Equals(link.Host, host, StringComparison.OrdinalIgnoreCase)
            && link.TcpPort == agwPort)
        {
            try
            {
                return Parse((await link.QueryPortsAsync(ct)).Payload);
            }
            catch (TimeoutException)
            {
                throw TimedOut(host, agwPort);
            }
        }

        return await QueryOnItsOwnAsync(host, agwPort, ct);
    }

    /// <summary>The port query on a connection of its own, closed on the
    /// way out. Never sends an <c>'X'</c>.</summary>
    private async Task<IReadOnlyList<AgwPortInfo>> QueryOnItsOwnAsync(string host, int agwPort, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queryCts.CancelAfter(QueryTimeout);

        try
        {
            await tcp.ConnectAsync(host, agwPort, queryCts.Token);
            var framing = new AgwFrameTransport(tcp.GetStream());

            // Send 'G' (ask port count + descriptions). No 'X' register
            // first - the query is a server-side metadata read, not an
            // inbound listener registration.
            await framing.WriteFrameAsync(new AgwFrame(0, 'G', 0, "", "", []), queryCts.Token);

            // Read frames until we see the matching 'G' reply. BPQ
            // sometimes leads with other status frames; ignore them.
            while (!queryCts.Token.IsCancellationRequested)
            {
                var frame = await framing.ReadFrameAsync(queryCts.Token);
                if (frame.Kind == 'G')
                {
                    return Parse(frame.Payload);
                }
            }
            throw new TimeoutException("No 'G' reply received");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(host, agwPort);
        }
    }

    private TimeoutException TimedOut(string host, int agwPort)
    {
        logger.LogDebug("AGW port query timed out after {0}s on {1}:{2}",
            QueryTimeout.TotalSeconds, host, agwPort);
        return new TimeoutException(
            $"AGW port query timed out after {QueryTimeout.TotalSeconds}s on {host}:{agwPort}");
    }

    /// <summary>Parse BPQ's <c>'G'</c> reply payload. The wire format is
    /// <c>"COUNT;DESC0;DESC1;...;"</c> in NUL-terminated ASCII; trailing
    /// NUL and trailing semicolon are both tolerated. Some BPQ builds
    /// omit the leading count and just emit descriptions, so we treat
    /// any first segment that's purely numeric as the count and skip
    /// it; otherwise the first segment is the first port's description.</summary>
    public static IReadOnlyList<AgwPortInfo> Parse(byte[] payload)
    {
        if (payload is null || payload.Length == 0) return Array.Empty<AgwPortInfo>();

        var s = Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
        if (s.Length == 0) return Array.Empty<AgwPortInfo>();

        // Split on semicolons; trailing empty parts come from a final ';'.
        var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<AgwPortInfo>();

        var startAt = 0;
        if (int.TryParse(parts[0], out _))
        {
            // First field is the count. Skip it.
            startAt = 1;
        }

        var result = new List<AgwPortInfo>(parts.Length - startAt);
        for (var i = startAt; i < parts.Length; i++)
        {
            var desc = parts[i].Trim();
            if (desc.Length == 0) continue;
            result.Add(new AgwPortInfo(Index: i - startAt, Description: desc));
        }
        return result;
    }
}

/// <summary>One AGW port as advertised by BPQ. <see cref="Index"/> is the
/// 0-indexed slot used in <c>AgwFrame.Port</c> when originating; the
/// description is whatever BPQ has in <c>PORTNUM</c> /
/// <c>ID=</c> for the port.</summary>
public sealed record AgwPortInfo(int Index, string Description);
