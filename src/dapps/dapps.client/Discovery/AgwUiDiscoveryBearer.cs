using System.Globalization;
using System.Threading.Channels;
using dapps.client.Transport.Agw;
using Microsoft.Extensions.Logging;

namespace dapps.client.Discovery;

/// <summary>
/// Discovery over AGW UI (unconnected) frames, on the daemon's one AGW
/// connection (<see cref="IAgwLinkSource"/>, #272): it opens no socket
/// and registers nothing of its own. It hears the UI frames of all
/// configured AGW channels (one per bearer port), emits an `M` frame on
/// whichever port the channel names, and yields parsed `U` frames
/// stamped with the matching channel key.
///
/// The link turns the node's monitor on, once (BPQ keeps one flag per
/// socket and flips it on every plain `m`), and it covers every port;
/// frames on ports with no channel are dropped here. The subscription is
/// the source's, so it carries on across reconnects. While the link is
/// down nothing is sent: an announce fails rather than queue for later,
/// and <see cref="Connection"/> tells the discovery service not to try.
/// UI frames go out from the link's own callsign, so a callsign change
/// takes effect when the link reconnects under it.
///
/// A listen-only link (AGW is not the bearer) registers no callsign, and
/// XRouter's AGW sends no 'M' from such a socket, so while the link only
/// listens the frames go out through the <see cref="IUiFrameSender"/>
/// (the node's RHPv2 port) instead, from the same callsign. Hearing stays
/// on the link's monitor.
///
/// Channels for this bearer have <c>ChannelKey</c> = the node's port
/// number as the node shows it ("1", "7", ...; #233), which the
/// <see cref="INodePortMaps"/> turns into the AGW port byte and back. The
/// bearer rejects any other key shape on start. A heard beacon's
/// <see cref="AgwBearerHint"/> carries that number too.
/// </summary>
public sealed class AgwUiDiscoveryBearer : IDiscoveryBearer
{
    public string Name => "agw";

    public const string DefaultBroadcastCall = "DAPPS";

    private readonly IAgwLinkSource _links;
    private readonly IUiFrameSender? _listenOnlySender;
    private readonly INodePortMaps? _portMaps;
    private readonly string _broadcastCall;
    private readonly ILogger _logger;

    private IReadOnlyDictionary<int, string> _numberToChannelKey = new Dictionary<int, string>();
    private int _unmappedLogged;
    private IDisposable? _subscription;

    private readonly Channel<ReceivedDiscoveryFrame> _incoming
        = Channel.CreateUnbounded<ReceivedDiscoveryFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public AgwUiDiscoveryBearer(
        IAgwLinkSource links,
        ILoggerFactory loggerFactory,
        IUiFrameSender? listenOnlySender = null,
        string broadcastCall = DefaultBroadcastCall,
        INodePortMaps? portMaps = null)
    {
        _links = links;
        _listenOnlySender = listenOnlySender;
        _portMaps = portMaps;
        _broadcastCall = broadcastCall;
        _logger = loggerFactory.CreateLogger<AgwUiDiscoveryBearer>();
    }

    public Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct)
    {
        if (_subscription is not null) return Task.CompletedTask;
        if (channels.Count == 0)
        {
            throw new ArgumentException("AgwUiDiscoveryBearer requires at least one channel", nameof(channels));
        }

        // Channel-key parsing: all keys must be port numbers.
        var numberToChannelKey = new Dictionary<int, string>();
        foreach (var ch in channels)
        {
            var number = ParsePortNumber(ch.ChannelKey);
            numberToChannelKey[number] = ch.ChannelKey;
        }
        _numberToChannelKey = numberToChannelKey;
        _subscription = _links.SubscribeUi(OnUi);

        _logger.LogInformation(
            "AGW UI discovery monitoring ports [{0}] on the node's AGW connection",
            string.Join(",", _numberToChannelKey.Keys));
        return Task.CompletedTask;
    }

    /// <summary>The link open now, if any: a new one after each reconnect.</summary>
    public object? Connection => _links.TryGetLink();

    public Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct) =>
        SendAsync(BeaconCodec.Encode(beacon), channelKey, ct);

    public Task SolicitAsync(SolicitFrame solicit, string channelKey, CancellationToken ct) =>
        SendAsync(SolicitCodec.Encode(solicit), channelKey, ct);

    private async Task SendAsync(byte[] payload, string channelKey, CancellationToken ct)
    {
        if (_subscription is null) throw new InvalidOperationException("AgwUiDiscoveryBearer not started");
        var number = ParsePortNumber(channelKey);
        var link = _links.TryGetLink()
            ?? throw new AgwLinkDownException($"AGW: the connection to the node is not open; nothing sent on port {number}");
        if (link.ListenOnly)
        {
            var sender = _listenOnlySender
                ?? throw new InvalidOperationException($"AGW: the connection only listens and nothing else sends UI frames; nothing sent on port {number}");
            // The frame goes out on the RHPv2 port by its number, and is
            // heard on the AGW port the map gives that number: only when
            // the map came from the same node's RHPv2 port (XRouter's AGW)
            // do the two name the same radio. pdn has no AGW of its own,
            // so another program's AGW port numbers say nothing about it.
            var map = await MapAsync(link, ct);
            if (map is null || !map.IsXrouter)
            {
                throw new UiFrameNotSentException(
                    $"RHP: not sending on port {number}: the AGW port list is not XRouter's, so its ports cannot be matched to the RHPv2 host's");
            }
            if (map.ToAgwPort(number) is null)
            {
                throw new UiFrameNotSentException(
                    $"RHP: not sending on port {number}: the node has no port {number} (its ports are {string.Join(",", map.Numbers ?? [])})");
            }
            await sender.SendUiAsync(number, link.Callsign, _broadcastCall, payload, ct);
            return;
        }
        var registeredMap = await MapAsync(link, ct)
            ?? throw new UiFrameNotSentException(
                $"AGW: the node's port numbers could not be read, so nothing was sent on port {number} (XRouter: check RHPPORT and DAPPS's RHPv2 settings)");
        var portByte = registeredMap.ToAgwPort(number)
            ?? throw new UiFrameNotSentException(
                $"AGW: the node has no port {number}; nothing sent");
        await link.SendUiAsync(portByte, _broadcastCall, payload, ct);
    }

    public async IAsyncEnumerable<ReceivedDiscoveryFrame> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rb in _incoming.Reader.ReadAllAsync(ct))
        {
            yield return rb;
        }
    }

    /// <summary>A monitored UI frame, on the link's reader: never waits.</summary>
    private void OnUi(AgwFrame frame)
    {
        var map = _portMaps is null ? NodePortMap.Plain()
            : _links.TryGetLink() is { } current ? _portMaps.TryGet(current) : null;
        if (map is null)
        {
            // An XRouter whose port numbers are still being read (or
            // could not be): which channel this is can't be told yet.
            if (Interlocked.Exchange(ref _unmappedLogged, 1) == 0)
            {
                _logger.LogInformation("AGW UI discovery: the node's port numbers are not known yet; frames heard meanwhile are skipped");
            }
            return;
        }
        if (map.FromAgwPort(frame.Port) is not { } number
            || !_numberToChannelKey.TryGetValue(number, out var channelKey))
        {
            // A port with no channel: the monitor covers them all.
            return;
        }
        if (_links.TryGetLink() is { } link
            && string.Equals(frame.CallFrom, link.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            return; // self-echo
        }

        var payload = InfoField(frame.Payload);

        // Try the solicit codec first - its magic prefix is
        // strictly longer than the beacon's, so a beacon never
        // matches it. Beacons rejected by the solicit parser
        // (no "solicit" keyword) fall through to the beacon
        // parser.
        if (SolicitCodec.TryParse(payload, out var solicit) && solicit is not null)
        {
            _incoming.Writer.TryWrite(new ReceivedSolicit(solicit, channelKey));
            return;
        }

        var hint = new AgwBearerHint(number);
        if (!BeaconCodec.TryParse(payload, hint, out var beacon) || beacon is null)
            return;

        _incoming.Writer.TryWrite(new ReceivedBeacon(beacon, channelKey));
    }

    /// <summary>
    /// The frame's own data. BPQ's monitor (and Dire Wolf's) hands a UI
    /// frame over as text: a header line,
    /// " 2:Fm N0BBB-9 To DAPPS &lt;UI pid=F0 Len=40&gt;[12:00:00]", a CR,
    /// then the data and a CR. A payload with no such header is taken
    /// whole. Before this, no beacon from a real node was ever recognised.
    /// </summary>
    internal static byte[] InfoField(byte[] monitored)
    {
        var cr = Array.IndexOf(monitored, (byte)'\r');
        if (cr < 0) return monitored;
        var header = System.Text.Encoding.ASCII.GetString(monitored, 0, cr);
        if (!header.Contains(":Fm ", StringComparison.Ordinal) || !header.Contains("<UI", StringComparison.Ordinal)) return monitored;
        var end = monitored.Length;
        while (end > cr + 1 && monitored[end - 1] == 0) end--;
        return monitored[(cr + 1)..end];
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private Task<NodePortMap?> MapAsync(AgwLink link, CancellationToken ct) =>
        _portMaps is null ? Task.FromResult<NodePortMap?>(NodePortMap.Plain()) : _portMaps.GetAsync(link, ct);

    private static int ParsePortNumber(string channelKey)
    {
        if (!int.TryParse(channelKey, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 255)
        {
            throw new FormatException(
                $"AGW discovery channel-key must be the node's port number (e.g. '1', '7'); got '{channelKey}'");
        }
        return n;
    }
}
