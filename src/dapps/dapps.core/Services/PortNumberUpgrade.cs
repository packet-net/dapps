using System.Globalization;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// Converts the radio ports an older DAPPS saved into the node's own port
/// numbers, once (#233, #370).
///
/// Up to 0.52 DAPPS counted the node's ports from 0. A neighbour's port
/// (and the default) was dialled over RHPv2 as that number plus one, and
/// over AGW as that byte; an AGW discovery channel and a peer heard on one
/// held the AGW byte. Every stored port now holds the number the node
/// shows (XRouter's <c>PORT=</c>, BPQ's <c>PORTNUM</c>, pdn's port as
/// <c>PORTS</c> lists it), and each is converted so that the same radio
/// port is used as before:
/// <list type="bullet">
/// <item>RHPv2 dial ports (neighbours, the default and probe candidates
/// on an RHPv2 node) become one higher, at start-up
/// (<see cref="ApplyAtStartup"/>).</item>
/// <item>AGW bytes (every port on an AGW node, AGW discovery channels, and
/// peers heard on them) need the node's port numbering: on XRouter AGW
/// byte k is its k-th lowest <c>PORT=</c> number, anywhere else k + 1. They
/// are marked pending at start-up and converted when the AGW connection
/// first answers (<see cref="ConvertPending"/>). Until then nothing is
/// sent on them: a pending neighbour is not called, a pending channel
/// neither beacons nor hears, a pending peer is not routed to.</item>
/// </list>
/// The one deliberate change: a peer heard on a gapped XRouter was called
/// on the wrong port before (#370) and is called on the port it was heard
/// on now. A marker row (<see cref="MarkerKey"/>) keeps all of this from
/// running twice.
/// </summary>
public static class PortNumberUpgrade
{
    /// <summary>The systemoptions row saying where the conversion stands.</summary>
    public const string MarkerKey = "PortNumbering";

    /// <summary>Every stored port is the node's own number.</summary>
    public const string Done = "node";

    /// <summary>Some stored ports are still AGW bytes, waiting for the
    /// AGW connection's first answer.</summary>
    public const string Pending = "pending-agw";

    /// <summary>The option the default port was stored under up to 0.52,
    /// counting from 0.</summary>
    public const string LegacyDefaultKey = "DefaultBearerPort";

    /// <summary>
    /// At start-up, before options are seeded. A fresh install, or a
    /// database already converted, is only marked. Otherwise RHPv2 dial
    /// ports go one higher now and AGW bytes are marked pending.
    /// <paramref name="options"/> is the systemoptions rows read before
    /// seeding; rows added here are added to it.
    /// </summary>
    public static void ApplyAtStartup(SQLiteConnection db, List<DbSystemOption> options, bool isFreshInstall, ILogger? logger)
    {
        if (Find(options, MarkerKey) is not null) return;
        if (isFreshInstall)
        {
            Insert(db, options, MarkerKey, Done);
            return;
        }

        var rhpv2 = string.Equals(Find(options, "NodeBearer")?.Value?.Trim(), "rhpv2", StringComparison.OrdinalIgnoreCase);
        var legacyDefault = int.TryParse(Find(options, LegacyDefaultKey)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0;
        int neighbours = 0, probes = 0, channels = 0, peers = 0;
        string? defaultPort = null;
        db.RunInTransaction(() =>
        {
            if (Find(options, nameof(SystemOptions.DefaultPort)) is null)
            {
                defaultPort = rhpv2 ? (legacyDefault + 1).ToString(CultureInfo.InvariantCulture) : "0";
                Insert(db, options, nameof(SystemOptions.DefaultPort), defaultPort);
            }
            if (rhpv2)
            {
                neighbours = db.Execute("update neighbours set BearerPort = BearerPort + 1 where BearerPort is not null");
                probes = db.Execute("update probednodes set LastBearerPort = LastBearerPort + 1 where LastBearerPort is not null");
            }
            else
            {
                neighbours = db.Execute("update neighbours set PortPending = 1 where BearerPort is not null");
                probes = db.Execute("update probednodes set PortPending = 1 where LastBearerPort is not null");
            }
            channels = db.Execute("update discoverychannels set PortPending = 1 where lower(Bearer) = 'agw'");
            peers = db.Execute("update discoveredpeers set PortPending = 1 where lower(Bearer) = 'agw'");
            var pending = !rhpv2 || channels > 0 || peers > 0;
            Insert(db, options, MarkerKey, pending ? Pending : Done);
        });

        if (rhpv2)
        {
            logger?.LogInformation(
                "Port numbers: ports are now the node's own numbers (XRouter's PORT=, pdn's port number). {0} neighbour port(s) and {1} probe port(s) went up by one, which calls the same ports as before{2}",
                neighbours, probes, defaultPort is null ? "" : $"; the default port is {defaultPort}");
        }
        else
        {
            logger?.LogInformation(
                "Port numbers: ports are now the node's own numbers (BPQ's PORTNUM, XRouter's PORT=). {0} neighbour port(s), {1} probe port(s) and the default port are converted when the AGW connection first answers, and nothing is sent on them before that",
                neighbours, probes);
        }
        if (channels > 0 || peers > 0)
        {
            logger?.LogInformation(
                "Port numbers: {0} AGW discovery channel(s) and {1} peer(s) heard on them are converted when the AGW connection first answers",
                channels, peers);
        }
    }

    /// <summary>True when some stored ports still wait for the AGW
    /// connection.</summary>
    public static bool IsPending(SQLiteConnection db) =>
        string.Equals(ReadOption(db, MarkerKey), Pending, StringComparison.Ordinal);

    /// <summary>True when an AGW discovery channel waits to be converted:
    /// the AGW connection must open for it even when AGW is not the
    /// bearer.</summary>
    public static bool HasPendingChannels(SQLiteConnection db) =>
        db.ExecuteScalar<int>("select count(*) from discoverychannels where PortPending = 1") > 0;

    /// <summary>What a conversion did.</summary>
    public sealed record Result(int Neighbours, int Probes, int Channels, int Peers, string? DefaultPort, int Held)
    {
        public int Converted => Neighbours + Probes + Channels + Peers + (DefaultPort is null ? 0 : 1);
    }

    /// <summary>
    /// Converts every pending AGW byte with <paramref name="map"/>, the
    /// node's port numbering, in one transaction. A byte the map has no
    /// port for (XRouter lists fewer ports than before) stays pending, so
    /// it is still not used; the marker is set to <see cref="Done"/> once
    /// none is left. Null when nothing was pending.
    /// </summary>
    public static Result? ConvertPending(SQLiteConnection db, NodePortMap map)
    {
        Result? result = null;
        db.RunInTransaction(() =>
        {
            if (!string.Equals(ReadOption(db, MarkerKey), Pending, StringComparison.Ordinal)) return;
            int neighbours = 0, probes = 0, channels = 0, peers = 0, held = 0;

            foreach (var n in db.Query<DbNeighbour>("select * from neighbours where PortPending = 1"))
            {
                if (Convert(map, n.BearerPort) is { } number)
                {
                    db.Execute("update neighbours set BearerPort = ?, PortPending = null where Id = ?", number, n.Id);
                    neighbours++;
                }
                else held++;
            }
            foreach (var p in db.Query<DbProbedNode>("select * from probednodes where PortPending = 1"))
            {
                if (Convert(map, p.LastBearerPort) is { } number)
                {
                    db.Execute("update probednodes set LastBearerPort = ?, PortPending = null where Callsign = ?", number, p.Callsign);
                    probes++;
                }
                else held++;
            }
            foreach (var c in db.Query<DbDiscoveryChannel>("select * from discoverychannels where PortPending = 1"))
            {
                var old = int.TryParse(c.ChannelKey, NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : (int?)null;
                if (Convert(map, old) is { } number)
                {
                    db.Execute("update discoverychannels set ChannelKey = ?, PortPending = null where Id = ?",
                        number.ToString(CultureInfo.InvariantCulture), c.Id);
                    channels++;
                }
                else held++;
            }
            foreach (var p in db.Query<DbDiscoveredPeer>("select * from discoveredpeers where PortPending = 1"))
            {
                var old = p.BearerPort ?? (int.TryParse(p.ChannelKey, NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : null);
                if (Convert(map, old) is not { } number)
                {
                    held++;
                    continue;
                }
                db.Execute("delete from discoveredpeers where PeerKey = ?", p.PeerKey);
                p.BearerPort = number;
                p.ChannelKey = number.ToString(CultureInfo.InvariantCulture);
                p.PeerKey = DbDiscoveredPeer.MakeKey(p.Callsign, p.Bearer, p.ChannelKey);
                p.PortPending = null;
                var clash = db.Query<DbDiscoveredPeer>("select * from discoveredpeers where PeerKey = ?", p.PeerKey).FirstOrDefault();
                if (clash is null) db.Insert(p);
                else if (clash.LastSeen < p.LastSeen) db.Update(p);
                peers++;
            }

            string? defaultPort = null;
            if (ReadOption(db, nameof(SystemOptions.DefaultPort)) == "0")
            {
                var legacy = int.TryParse(ReadOption(db, LegacyDefaultKey), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0;
                if (Convert(map, legacy) is { } number)
                {
                    defaultPort = number.ToString(CultureInfo.InvariantCulture);
                    db.Execute("update systemoptions set Value = ? where Option = ?", defaultPort, nameof(SystemOptions.DefaultPort));
                }
                else held++;
            }

            if (held == 0)
            {
                db.Execute("update systemoptions set Value = ? where Option = ?", Done, MarkerKey);
            }
            result = new Result(neighbours, probes, channels, peers, defaultPort, held);
        });
        return result;
    }

    private static int? Convert(NodePortMap map, int? agwByte) =>
        agwByte is { } b && b is >= 0 and <= 255 ? map.FromAgwPortUnbounded((byte)b) : null;

    private static DbSystemOption? Find(List<DbSystemOption> options, string key) =>
        options.FirstOrDefault(o => string.Equals(o.Option, key, StringComparison.OrdinalIgnoreCase));

    private static void Insert(SQLiteConnection db, List<DbSystemOption> options, string key, string value)
    {
        var row = new DbSystemOption { Option = key, Value = value };
        db.InsertOrReplace(row);
        options.Add(row);
    }

    private static string? ReadOption(SQLiteConnection db, string key) =>
        db.Query<DbSystemOption>("select * from systemoptions where Option = ? collate nocase", key).FirstOrDefault()?.Value;
}

/// <summary>
/// Runs <see cref="PortNumberUpgrade.ConvertPending"/> when the AGW
/// connection first opens after an upgrade, with the node's port numbering
/// (<see cref="NodePortNumbers"/>), and keeps that connection wanted while
/// an AGW discovery channel waits for it (with RHPv2 as the bearer the
/// connection otherwise opens only for running channels, and a pending one
/// doesn't run). It also says, for the dashboard, what is still held.
/// </summary>
public sealed class PortNumberUpgradeService(
    INodePortMaps portMaps,
    IOptionsMonitor<SystemOptions> options,
    ILogger<PortNumberUpgradeService> logger,
    IAgwLinkSource? agwLinks = null,
    IServiceProvider? services = null) : IHostedService
{
    // Resolved at start, not injected: the AGW service is the link source
    // and calls back into this one when its connection opens.
    private IAgwLinkSource? links = agwLinks;

    private readonly SemaphoreSlim oneAtATime = new(1, 1);
    private IDisposable? keepLinkOpen;
    private volatile bool pending;

    /// <summary>Why ports are still held, for the dashboard; null when
    /// none is.</summary>
    public string? HeldReason { get; private set; }

    /// <summary>True while ports saved by an older DAPPS still wait to be
    /// converted.</summary>
    public bool Pending => pending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var db = DbInfo.GetConnection();
        pending = PortNumberUpgrade.IsPending(db);
        if (pending)
        {
            HeldReason = "Ports saved by an older DAPPS wait for the node's AGW connection to answer, to be converted to the node's own numbers; nothing is sent on them until then.";
            links ??= services?.GetService(typeof(IAgwLinkSource)) as IAgwLinkSource;
            if (links is not null && PortNumberUpgrade.HasPendingChannels(db))
            {
                keepLinkOpen = links.SubscribeUi(static _ => { });
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        keepLinkOpen?.Dispose();
        keepLinkOpen = null;
        return Task.CompletedTask;
    }

    /// <summary>Called by the AGW service each time its connection opens:
    /// converts what is pending, in the background. Never throws.</summary>
    public void LinkOpened(AgwLink link)
    {
        if (!pending) return;
        _ = Task.Run(() => ConvertWhileOpenAsync(link));
    }

    /// <summary>How long to wait before trying again when the node's port
    /// numbering could not be read (no answer to its port list yet, or an
    /// XRouter whose RHPv2 port can't be reached).</summary>
    internal TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tries the conversion, and again every
    /// <see cref="RetryInterval"/> while it can't be done and the link is
    /// open. Never converts from a guessed numbering.</summary>
    internal async Task ConvertWhileOpenAsync(AgwLink link)
    {
        while (pending && !link.Closed.IsCompleted)
        {
            await ConvertAsync(link);
            // Done, or converted with some ports the node no longer has
            // (those wait for the operator): nothing to retry.
            if (!pending || !couldNotRead) return;
            await Task.WhenAny(link.Closed, Task.Delay(RetryInterval));
        }
    }

    private volatile bool couldNotRead;

    /// <summary>Converts what is pending with <paramref name="link"/>'s
    /// node's port numbering. Exposed for tests.</summary>
    internal async Task ConvertAsync(AgwLink link)
    {
        await oneAtATime.WaitAsync();
        try
        {
            if (!pending) return;
            NodePortMap? map;
            try
            {
                map = await portMaps.GetAsync(link, CancellationToken.None);
            }
            catch (Exception ex)
            {
                couldNotRead = true;
                logger.LogWarning("Port numbers: could not read the node's port numbers ({0}); ports saved by an older DAPPS stay held", ex.Message);
                return;
            }
            if (map is null)
            {
                couldNotRead = true;
                HeldReason = "Ports saved by an older DAPPS can't be converted yet: DAPPS couldn't read the node's port numbers (no answer to its port list, or an XRouter whose RHPv2 port it can't reach: set RHPPORT in XROUTER.CFG and DAPPS's RHPv2 settings to match). It keeps trying; nothing is sent on those ports until then.";
                logger.LogWarning("Port numbers: {0}", HeldReason);
                return;
            }
            couldNotRead = false;

            PortNumberUpgrade.Result? result;
            using (var db = DbInfo.GetConnection())
            {
                result = PortNumberUpgrade.ConvertPending(db, map);
            }
            if (result is null)
            {
                pending = false;
                HeldReason = null;
            }
            else
            {
                logger.LogInformation(
                    "Port numbers: converted to the node's own numbers ({0}): {1} neighbour(s), {2} probe port(s), {3} discovery channel(s), {4} heard peer(s){5}; the same radio ports are used as before",
                    map, result.Neighbours, result.Probes, result.Channels, result.Peers,
                    result.DefaultPort is null ? "" : $", default port {result.DefaultPort}");
                if (result.Held == 0)
                {
                    pending = false;
                    HeldReason = null;
                }
                else
                {
                    HeldReason = $"{result.Held} port(s) saved by an older DAPPS name AGW ports this node doesn't have now ({map}); nothing is sent on them. Set them again on the Topology page.";
                    logger.LogWarning("Port numbers: {0}", HeldReason);
                }
            }
            if (options is SystemOptionsStore store) store.Reload();
            if (!pending)
            {
                keepLinkOpen?.Dispose();
                keepLinkOpen = null;
            }
        }
        finally
        {
            oneAtATime.Release();
        }
    }
}
