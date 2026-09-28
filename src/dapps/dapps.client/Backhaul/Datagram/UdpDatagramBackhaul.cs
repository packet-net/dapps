using System.Net;
using System.Net.Sockets;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul.Datagram;

/// <summary>
/// Datagram-shaped <see cref="IDappsBackhaul"/>. Encodes the
/// <see cref="BackhaulMessage"/> via <see cref="BackhaulMessageCodec"/>,
/// fragments via <see cref="Packetiser"/>, and emits each fragment as a
/// UDP datagram to the route's <c>UdpEndpoint</c>.
///
/// Stand-in for a future MeshCore Companion / KISS bearer (Plan A0.4).
/// Validates the seam end-to-end: nothing here knows about DAPPSv1
/// session protocol, AGW frames, or the AGW outbound transport. The
/// MTU is configurable so tests can dial it down to force aggressive
/// fragmentation of small messages - closer to a real RF datagram
/// constraint than UDP's 65k-byte natural maximum.
///
/// Fire-and-forget: <see cref="SendAsync"/> returns success once every
/// fragment is on the wire. There's no in-bearer ack; reliability is
/// the higher layer's problem (resend on TTL-aware schedule).
/// </summary>
public sealed class UdpDatagramBackhaul : IDappsBackhaul, IDisposable
{
    /// <summary>Default MTU in bytes - leaves headroom under typical
    /// LoRa-style frames (~250 byte payloads). Tests override.</summary>
    public const int DefaultMtu = 200;

    private readonly UdpClient _udp;
    private readonly int _mtu;
    private readonly ILogger _logger;
    private readonly IDappsTxGate _txGate;

    public UdpDatagramBackhaul(ILoggerFactory loggerFactory, int mtu = DefaultMtu, IDappsTxGate? txGate = null)
    {
        if (mtu < Packetiser.MinMtu)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu),
                $"mtu must be >= {Packetiser.MinMtu}");
        }
        _udp = new UdpClient { EnableBroadcast = false };
        _mtu = mtu;
        _logger = loggerFactory.CreateLogger<UdpDatagramBackhaul>();
        _txGate = txGate ?? AlwaysOpenTxGate.Instance;
    }

    public bool CanHandle(BackhaulRoute route) => !string.IsNullOrWhiteSpace(route.UdpEndpoint);

    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        if (!_txGate.TxAllowed)
        {
            return BackhaulSendResult.Fail($"tx-stopped: {_txGate.BlockReason ?? "(no reason)"}");
        }

        if (!TryParseEndpoint(route.UdpEndpoint, out var endpoint, out var parseError))
        {
            return BackhaulSendResult.Fail($"invalid UdpEndpoint '{route.UdpEndpoint}': {parseError}");
        }

        try
        {
            // Stamp the link source with our local callsign before
            // encoding - UDP doesn't carry a session-level sender
            // identity, so we have to put it in-band. Receivers use
            // this for passive routing learning. AGW bearers identify
            // the link source from the AX.25 C-frame and don't need
            // this; setting it here is harmless if the message gets
            // bridged across bearers.
            var stamped = message with { LinkSourceCallsign = localCallsign };
            var encoded = BackhaulMessageCodec.Encode(stamped);
            var fragments = Packetiser.Split(message.Id, encoded, _mtu);

            _logger.LogInformation(
                "UDP backhaul: sending {0} ({1} bytes encoded -> {2} fragment(s) @ mtu={3}) to {4}",
                message.Id, encoded.Length, fragments.Count, _mtu, endpoint);

            foreach (var fragment in fragments)
            {
                await _udp.SendAsync(fragment, endpoint, ct);
            }
            return BackhaulSendResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP backhaul send failed for {0} to {1}", message.Id, route.UdpEndpoint);
            return BackhaulSendResult.Fail(ex.Message);
        }
    }

    public void Dispose() => _udp.Dispose();

    private static bool TryParseEndpoint(string? raw, out IPEndPoint endpoint, out string error)
    {
        endpoint = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty";
            return false;
        }
        var colon = raw.LastIndexOf(':');
        if (colon <= 0 || colon == raw.Length - 1)
        {
            error = "expected host:port";
            return false;
        }
        var host = raw[..colon];
        if (!int.TryParse(raw[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            error = "port must be 1..65535";
            return false;
        }

        // Resolve host. For numeric IPs, IPAddress.Parse short-circuits;
        // for names, fall back to a DNS lookup. Synchronous lookup is
        // fine - the UDP backhaul only resolves once per send and DNS
        // is typically loopback-cached for ham-radio LAN deployments.
        if (IPAddress.TryParse(host, out var ip))
        {
            endpoint = new IPEndPoint(ip, port);
            return true;
        }
        try
        {
            var resolved = Dns.GetHostAddresses(host);
            if (resolved.Length == 0)
            {
                error = $"DNS lookup of '{host}' returned no addresses";
                return false;
            }
            endpoint = new IPEndPoint(resolved[0], port);
            return true;
        }
        catch (SocketException ex)
        {
            error = $"DNS lookup of '{host}' failed: {ex.Message}";
            return false;
        }
    }
}
