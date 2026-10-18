namespace dapps.client.Discovery;

/// <summary>
/// Immutable snapshot of a discovery channel's bearer-relevant
/// configuration. Bearers receive a list of these at <see cref="IDiscoveryBearer.StartAsync"/>;
/// they decide internally how to operate (one socket monitoring N AGW
/// ports, or one UDP socket per multicast group, or one MeshCore radio
/// handle per channel).
/// </summary>
public sealed record DiscoveryChannelInfo(
    int Id,
    string Bearer,
    string ChannelKey,
    LinkClass LinkClass,
    int BeaconIntervalSeconds,
    int AdvertisedTtlSeconds,
    int CostHint,
    int AirtimeBudgetSecondsPerHour = 0,
    int SolicitIntervalSeconds = 0);

/// <summary>
/// Base type for events the discovery service consumes off a bearer's
/// listen stream. <see cref="ReceivedBeacon"/> is "I heard so-and-so
/// announce themselves"; <see cref="ReceivedSolicit"/> is "someone is
/// asking who's around" (Plan B6.2). Bearers stamp the
/// <see cref="ChannelKey"/> based on which channel the frame arrived
/// on - they're the authority since they know AGW port bytes / UDP
/// groups / MeshCore radios.
/// </summary>
public abstract record ReceivedDiscoveryFrame(string ChannelKey);

/// <summary>A beacon yielded by a bearer's listen stream.</summary>
public sealed record ReceivedBeacon(BeaconFrame Beacon, string ChannelKey)
    : ReceivedDiscoveryFrame(ChannelKey);

/// <summary>
/// A solicit yielded by a bearer's listen stream. The discovery
/// service responds with a delayed beacon if its policy allows
/// (currently: respond if we beacon on the same channel anyway).
/// </summary>
public sealed record ReceivedSolicit(SolicitFrame Solicit, string ChannelKey)
    : ReceivedDiscoveryFrame(ChannelKey);

/// <summary>
/// A channel over which DAPPS nodes announce themselves and overhear
/// each other's announcements. One implementation per bearer kind
/// ("agw", "udp", "meshcore"); each implementation can serve many
/// channels (bearer ports / multicast groups / MeshCore radios)
/// from the same connection.
/// </summary>
public interface IDiscoveryBearer : IAsyncDisposable
{
    /// <summary>Stable identifier matching <see cref="DiscoveryChannelInfo.Bearer"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Bring the bearer online with the given channel set. The bearer
    /// is responsible for opening whatever sockets / radios it needs
    /// and starting any internal read loops. Idempotent - can be
    /// called once at startup.
    /// </summary>
    Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct);

    /// <summary>Emit a beacon on the named channel. The daemon
    /// schedules these per channel according to the channel's
    /// <see cref="DiscoveryChannelInfo.BeaconIntervalSeconds"/>.</summary>
    Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct);

    /// <summary>
    /// Plan B6.2 - emit a solicit on the named channel. Receivers
    /// reply with their normal beacon after a small random delay.
    /// Operators trigger this on-demand today (e.g. dashboard "ping
    /// channel" button); a future cadence can have the discovery
    /// service schedule them periodically on HF channels.
    /// </summary>
    Task SolicitAsync(SolicitFrame solicit, string channelKey, CancellationToken ct);

    /// <summary>Stream of discovery frames heard on any of this
    /// bearer's channels, each tagged with the channel key it arrived
    /// on. Today: beacons and solicits; future: anything bearer-level
    /// the service should react to.</summary>
    IAsyncEnumerable<ReceivedDiscoveryFrame> ListenAsync(CancellationToken ct);

    /// <summary>
    /// What the bearer sends over, for one that sends over a connection
    /// it does not own: null while that is down and nothing can go, and a
    /// new value each time it is reopened. The discovery service sends
    /// nothing while it is null, and announces on each channel at once
    /// when it changes, as it does when a bearer starts. A bearer that
    /// owns its sockets keeps the default: itself, always.
    /// </summary>
    object? Connection => this;
}
