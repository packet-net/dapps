using System.Globalization;
using System.Net.Sockets;
using System.Threading.Channels;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Discovery;

/// <summary>
/// Discovery over AGW UI (unconnected) frames. One AGW TCP socket
/// monitors all configured AGW channels (one per bearer port),
/// emits an `M` frame on whichever port the channel names, and
/// yields parsed `U` frames stamped with the matching channel key.
///
/// AGW protocol primitives:
///   `X` - register our callsign (BPQ won't accept emitted frames otherwise)
///   `m` - toggle monitor mode for the whole socket (sent once: BPQ
///         keeps one flag per socket, ignores the port, and flips it on
///         every plain `m`, so one per port would leave it off with an
///         even number of ports; the read loop filters by port instead)
///   `M` - emit a UI frame on a port
///   `U` - inbound monitored UI frame (BPQ → us)
///
/// Channels for this bearer have <c>ChannelKey</c> = the bearer port
/// byte stringified ("0", "1", …). The bearer rejects any other key
/// shape on start.
/// </summary>
public sealed class AgwUiDiscoveryBearer : IDiscoveryBearer
{
    public string Name => "agw";

    public const string DefaultBroadcastCall = "DAPPS";

    private readonly string _host;
    private readonly int _port;
    private readonly string _ourCallsign;
    private readonly string _broadcastCall;
    private readonly ILogger _logger;
    private readonly IDappsTxGate _txGate;

    private TcpClient? _tcp;
    private AgwFrameTransport? _framing;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;
    private readonly Dictionary<byte, string> _portToChannelKey = new();

    private readonly Channel<ReceivedDiscoveryFrame> _incoming
        = Channel.CreateUnbounded<ReceivedDiscoveryFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    public AgwUiDiscoveryBearer(
        string host, int port,
        string ourCallsign,
        ILoggerFactory loggerFactory,
        string broadcastCall = DefaultBroadcastCall,
        IDappsTxGate? txGate = null)
    {
        _host = host;
        _port = port;
        _ourCallsign = ourCallsign;
        _broadcastCall = broadcastCall;
        _logger = loggerFactory.CreateLogger<AgwUiDiscoveryBearer>();
        _txGate = txGate ?? AlwaysOpenTxGate.Instance;
    }

    public async Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct)
    {
        if (_tcp is not null) return;
        if (channels.Count == 0)
        {
            throw new ArgumentException("AgwUiDiscoveryBearer requires at least one channel", nameof(channels));
        }

        // Channel-key parsing: all keys must be valid bearer ports.
        _portToChannelKey.Clear();
        foreach (var ch in channels)
        {
            var portByte = ParsePortByte(ch.ChannelKey);
            _portToChannelKey[portByte] = ch.ChannelKey;
        }

        var tcp = new TcpClient();
        await tcp.ConnectAsync(_host, _port, ct);
        var framing = new AgwFrameTransport(tcp.GetStream(), _txGate, _logger);

        try
        {
            // Register our callsign once for the whole connection. The
            // reply is awaited under a timeout: the discovery loop awaits
            // this start inline, so a host that never answers would stall
            // beacons on every bearer.
            await framing.WriteFrameAsync(
                new AgwFrame(0, 'X', 0, _ourCallsign, "", []),
                ct);
            var registerReply = await framing.ReadRegisterReplyAsync(_ourCallsign, TimeProvider.System, ct);
            if (registerReply.Payload is not [0x01])
            {
                throw new IOException(
                    $"AGW register {_ourCallsign} for discovery failed: callsign refused (reply [{string.Join(',', registerReply.Payload)}])");
            }

            // Turn the monitor on, once for the socket: it covers every
            // port (see the class doc).
            await framing.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _framing = framing;
        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

        _logger.LogInformation(
            "AGW UI discovery monitoring ports [{0}] as {1}",
            string.Join(",", _portToChannelKey.Keys),
            _ourCallsign);
    }

    public async Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct)
    {
        if (_framing is null) throw new InvalidOperationException("AgwUiDiscoveryBearer not started");
        var portByte = ParsePortByte(channelKey);
        var payload = BeaconCodec.Encode(beacon);
        await _framing.WriteFrameAsync(
            new AgwFrame(portByte, 'M', 0xF0, _ourCallsign, _broadcastCall, payload),
            ct);
    }

    public async Task SolicitAsync(SolicitFrame solicit, string channelKey, CancellationToken ct)
    {
        if (_framing is null) throw new InvalidOperationException("AgwUiDiscoveryBearer not started");
        var portByte = ParsePortByte(channelKey);
        var payload = SolicitCodec.Encode(solicit);
        await _framing.WriteFrameAsync(
            new AgwFrame(portByte, 'M', 0xF0, _ourCallsign, _broadcastCall, payload),
            ct);
    }

    public async IAsyncEnumerable<ReceivedDiscoveryFrame> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rb in _incoming.Reader.ReadAllAsync(ct))
        {
            yield return rb;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var framing = _framing ?? throw new InvalidOperationException();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AgwFrame frame;
                try
                {
                    frame = await framing.ReadFrameAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AGW UI discovery read failed; ending loop");
                    break;
                }

                if (frame.Kind != 'U') continue;
                if (string.Equals(frame.CallFrom, _ourCallsign, StringComparison.OrdinalIgnoreCase))
                    continue; // self-echo

                if (!_portToChannelKey.TryGetValue(frame.Port, out var channelKey))
                {
                    // Frame on a port we didn't ask to monitor - odd; skip.
                    continue;
                }

                // Try the solicit codec first - its magic prefix is
                // strictly longer than the beacon's, so a beacon never
                // matches it. Beacons rejected by the solicit parser
                // (no "solicit" keyword) fall through to the beacon
                // parser.
                if (SolicitCodec.TryParse(frame.Payload, out var solicit) && solicit is not null)
                {
                    await _incoming.Writer.WriteAsync(new ReceivedSolicit(solicit, channelKey), ct);
                    continue;
                }

                var hint = new AgwBearerHint(frame.Port);
                if (!BeaconCodec.TryParse(frame.Payload, hint, out var beacon) || beacon is null)
                    continue;

                await _incoming.Writer.WriteAsync(new ReceivedBeacon(beacon, channelKey), ct);
            }
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _readLoopCts?.Cancel();
            if (_readLoop is not null)
            {
                try { await _readLoop; } catch { /* expected on cancel */ }
            }
        }
        finally
        {
            _tcp?.Dispose();
            _readLoopCts?.Dispose();
        }
    }

    private static byte ParsePortByte(string channelKey)
    {
        if (!byte.TryParse(channelKey, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
        {
            throw new FormatException(
                $"AGW discovery channel-key must be a bearer port (e.g. '0', '1'); got '{channelKey}'");
        }
        return b;
    }
}
