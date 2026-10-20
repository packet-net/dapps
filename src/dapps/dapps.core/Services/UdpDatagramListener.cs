using System.Net;
using System.Net.Sockets;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.client.Discovery;
using Microsoft.Extensions.Options;
using dapps.core.Models;

namespace dapps.core.Services;

/// <summary>
/// UDP receiver for the datagram backhaul (Plan A0.4 stand-in for
/// MeshCore-style bearers). Binds to <see cref="SystemOptions.UdpListenPort"/>
/// when that's > 0; reads fragments, reassembles via
/// <see cref="Reassembler"/>, decodes via
/// <see cref="BackhaulMessageCodec"/>, hands the result to
/// <see cref="IBackhaulInbox"/>.
///
/// The bearer is fire-and-forget - we don't ack the sender. We
/// advertise our port in UDP discovery beacons (udpport), and the
/// beacons go out from this socket (<see cref="UdpListenerSocket"/>,
/// #383) so their source port is this port too. Discovery over UDP and
/// reliable delivery are higher-layer concerns (Phase B).
/// </summary>
public sealed class UdpDatagramListener(
    IOptionsMonitor<SystemOptions> options,
    IBackhaulInbox inbox,
    Database database,
    TimeProvider timeProvider,
    ILogger<UdpDatagramListener> logger,
    UdpListenerSocket? sharedSocket = null) : BackgroundService
{
    /// <summary>Source address used for inbound delivery's sourceCallsign
    /// when the bearer doesn't carry an explicit one. The DAPPSv1
    /// session reader sees the connecting peer's callsign as the first
    /// line; UDP has no equivalent. Using the IP:port of the sender as
    /// a fallback keeps the audit trail meaningful without inventing a
    /// fake callsign.</summary>
    private const string UnknownSourceCallsign = "UDP";

    /// <summary>Drop reassembly state for a message we haven't completed
    /// within this window. Keeps a forever-leaky neighbour from pinning
    /// memory.</summary>
    private static readonly TimeSpan ReassemblyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often to sweep the reassembler. Cheap operation, low
    /// frequency suffices.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.CurrentValue.UdpListenPort;
        if (port <= 0)
        {
            logger.LogInformation("UDP datagram listener disabled (UdpListenPort=0)");
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        sharedSocket?.Attach(udp.Client);
        try
        {
            await ReceiveLoopAsync(udp, stoppingToken);
        }
        finally
        {
            sharedSocket?.Detach(udp.Client);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken stoppingToken)
    {
        var reassembler = new Reassembler();
        var nextSweep = timeProvider.GetUtcNow().UtcDateTime + SweepInterval;
        logger.LogInformation("UDP datagram listener bound on :{0}", ((IPEndPoint)udp.Client.LocalEndPoint!).Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UDP receive failed; continuing");
                continue;
            }

            // A discovery beacon or solicit is never mail. One only lands
            // here if the listener shares a port with a discovery group;
            // drop it quietly rather than fail to parse it as a fragment.
            if (IsDiscoveryFrame(received.Buffer))
                continue;

            try
            {
                var assembled = reassembler.Accept(received.Buffer, timeProvider.GetUtcNow().UtcDateTime);
                if (assembled is null)
                {
                    continue;
                }

                BackhaulMessage decoded;
                try
                {
                    decoded = BackhaulMessageCodec.Decode(assembled);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "UDP datagram reassembled to {0} bytes but failed to decode",
                        assembled.Length);
                    continue;
                }

                // Prefer the in-band link-source callsign (codec v3+)
                // - it's authoritative because the sender stamps it
                // from its own configured callsign. Fall back to
                // IP:port→neighbour mapping for v2/v1 messages and
                // for cases where the sender didn't stamp it.
                var sourceCallsign = !string.IsNullOrEmpty(decoded.LinkSourceCallsign)
                    ? decoded.LinkSourceCallsign!
                    : await ResolveSourceCallsignAsync(received.RemoteEndPoint);
                logger.LogInformation("UDP datagram delivered: {0} from {1} (dst={2}, source={3})",
                    decoded.Id, received.RemoteEndPoint, decoded.Destination, sourceCallsign);
                await inbox.DeliverAsync(decoded, sourceCallsign, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UDP fragment dispatch failed");
            }

            if (timeProvider.GetUtcNow().UtcDateTime >= nextSweep)
            {
                var dropped = reassembler.DropOlderThan(timeProvider.GetUtcNow().UtcDateTime - ReassemblyTimeout);
                if (dropped > 0)
                {
                    logger.LogInformation("UDP reassembler dropped {0} stale partial(s)", dropped);
                }
                nextSweep = timeProvider.GetUtcNow().UtcDateTime + SweepInterval;
            }
        }
    }

    /// <summary>
    /// Map an inbound IP:port to the configured neighbour's callsign.
    /// Tries an exact <c>UdpEndpoint</c> match first, then falls back
    /// to port-only when nothing matches exactly - handles cases like
    /// loopback-vs-eth0 where the source IP we observe differs from
    /// the host literal the neighbour was configured with (common on
    /// WSL2 / containerised dev setups). Returns the literal "UDP"
    /// when no neighbour matches or the port is shared by multiple
    /// neighbours (genuinely ambiguous).
    /// </summary>
    private async Task<string> ResolveSourceCallsignAsync(IPEndPoint remote)
    {
        var neighbours = await database.GetNeighbours();
        if (neighbours.Count == 0) return UnknownSourceCallsign;

        var exactKey = $"{remote.Address}:{remote.Port}";
        var exact = neighbours.FirstOrDefault(n =>
            !string.IsNullOrEmpty(n.UdpEndpoint)
            && string.Equals(n.UdpEndpoint, exactKey, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Callsign;

        var byPort = neighbours
            .Where(n => !string.IsNullOrEmpty(n.UdpEndpoint) && ParsePort(n.UdpEndpoint!) == remote.Port)
            .ToList();
        if (byPort.Count == 1) return byPort[0].Callsign;

        // No match, or multiple neighbours share that port. Without a
        // bearer-level callsign in the datagram we can't disambiguate;
        // fall back to the placeholder so the message still gets
        // delivered (passive learning will skip it, which is correct
        // behaviour when the link source is genuinely unknown).
        return UnknownSourceCallsign;
    }

    internal static bool IsDiscoveryFrame(ReadOnlySpan<byte> datagram)
    {
        ReadOnlySpan<byte> magic = "DAPPS v1"u8;
        return datagram.StartsWith(magic)
            && (datagram.Length == magic.Length || datagram[magic.Length] == (byte)' ');
    }

    private static int? ParsePort(string endpoint)
    {
        var i = endpoint.LastIndexOf(':');
        if (i < 0 || i == endpoint.Length - 1) return null;
        return int.TryParse(endpoint.AsSpan(i + 1), out var p) ? p : null;
    }
}
