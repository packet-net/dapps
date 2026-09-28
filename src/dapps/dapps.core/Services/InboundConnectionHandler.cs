using System.Text;
using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Services;

/// <summary>
/// The answering side of a DAPPSv1 session. Bearer-neutral: takes a
/// duplex byte <see cref="Stream"/> already connected to a peer and the
/// peer's callsign already determined (each bearer figures the callsign
/// out its own way - AGW reads it off the inbound 'C' frame's CallFrom
/// field; RHP from the accepted connection).
///
/// The session itself is an <see cref="ExchangeSession"/>, the same one
/// the calling node runs: it sends the prompt and our <c>exchange</c>
/// line, answers commands from probes and people (<c>help</c>,
/// <c>peers</c>, <c>routes</c>, <c>quit</c>) and single messages pushed
/// with <c>ihave</c>/<c>data</c>, and once the caller's <c>exchange</c>
/// arrives carries traffic both ways. Received messages go to the
/// <see cref="IBackhaulInbox"/>, where DAPPS-level concerns (queue
/// persistence, MQTT injection, forwarding decisions) live.
/// </summary>
public class InboundConnectionHandler(
    Stream stream,
    string sourceCallsign,
    ILoggerFactory loggerFactory,
    Database database,
    IBackhaulInbox inbox,
    OperationalMetrics? metrics = null,
    Func<string, CancellationToken, Task<ExchangeSettings>>? settingsFor = null,
    SessionDirectory? directory = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();

    public async Task Handle(CancellationToken stoppingToken)
    {
        ExchangeSession? session = null;
        try
        {
            logger.LogInformation("Inbound session from {0}", sourceCallsign);
            session = new ExchangeSession(stream, sourceCallsign, dialled: false, await SettingsAsync(stoppingToken),
                new ReceivedLedgerInbox(inbox, database), loggerFactory)
            {
                Commands = CommandAsync,
                // Established: the forwarder can hand this session our
                // traffic for the caller from now on.
                Opened = s => directory?.Register(sourceCallsign, s),
                HashMismatch = id => metrics.RecordHashMismatch(id, sourceCallsign),
            };
            await session.RunAsync(stoppingToken);
        }
        finally
        {
            if (session is not null) directory?.Unregister(sourceCallsign, session);
            await stream.DisposeAsync();
        }
    }

    private async Task<ExchangeSettings> SettingsAsync(CancellationToken ct)
    {
        if (settingsFor is null) return new ExchangeSettings();
        try
        {
            return await settingsFor(sourceCallsign, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the settings for {0}; no hold, sending plain", sourceCallsign);
            return new ExchangeSettings();
        }
    }

    private static readonly string[] helpCommands = ["info", "help"];

    /// <summary>
    /// The commands a probe or a person can give before any exchange. The
    /// answer, or null for a command we don't know.
    /// </summary>
    private async Task<string?> CommandAsync(string input, CancellationToken ct)
    {
        var command = input.Trim().ToLowerInvariant();
        if (helpCommands.Contains(command))
        {
            return "This is DAPPS. See https://github.com/packet-net/dapps/blob/master/README.md for details.\n";
        }
        if (command is "peers" or "who")
        {
            // Accept either form; the spec doc canonicalises on `peers`,
            // but `who` is the verb a sysop already types at a node prompt
            // so it's a natural alias.
            logger.LogInformation("{0} is asking for our peers", sourceCallsign);
            return await PeersAsync();
        }
        if (command == "routes")
        {
            logger.LogInformation("{0} is asking for our known routes (gossip)", sourceCallsign);
            return await RoutesAsync();
        }
        return null;
    }

    /// <summary>
    /// Plan B6.1 Phase 2 - emit the set of callsigns we forward to.
    /// One <c>peer &lt;callsign&gt; source=&lt;n|d&gt;[ port=&lt;byte&gt;]</c>
    /// line per known forward target, then <c>end</c>. Callers (today:
    /// dapps probers populating their own <c>DbProbedNode</c> table for
    /// transitive discovery) parse line-by-line until <c>end</c>.
    ///
    /// Sources reported:
    /// <list type="bullet">
    /// <item><c>n</c> - manual <see cref="Models.DbNeighbour"/> row,
    /// AGW-routable (UDP-only neighbours are skipped; the asker can't
    /// reach them over the same bearer).</item>
    /// <item><c>d</c> - AGW-bearer <see cref="Models.DbDiscoveredPeer"/>
    /// row. We've heard a beacon but never been asked to forward to
    /// them ourselves; useful as an exploration hint for the asker.</item>
    /// </list>
    ///
    /// Read-only - emitting a peer is not an endorsement, doesn't bind
    /// us to forward, and doesn't leak anything more sensitive than
    /// what already shows up on the air via beacons or DAPPS forwarding.
    /// </summary>
    private async Task<string> PeersAsync()
    {
        var sb = new StringBuilder();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var neighbours = await database.GetNeighbours();
        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            if (n.UdpEndpoint is not null) continue;   // UDP-only - not AGW-reachable for the asker
            if (!emitted.Add(n.Callsign)) continue;
            sb.Append("peer ").Append(n.Callsign).Append(" source=n");
            if (n.BearerPort is { } port) sb.Append(" port=").Append(port);
            sb.Append('\n');
        }

        var peers = await database.GetDiscoveredPeers();
        foreach (var p in peers)
        {
            if (!string.Equals(p.Bearer, "agw", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (!emitted.Add(p.Callsign)) continue;     // already reported as a neighbour
            sb.Append("peer ").Append(p.Callsign).Append(" source=d");
            if (p.BearerPort is { } port) sb.Append(" port=").Append(port);
            sb.Append('\n');
        }

        sb.Append("end\n");
        logger.LogInformation("Sending {0} peer record(s) to {1}", emitted.Count, sourceCallsign);
        return sb.ToString();
    }

    /// <summary>
    /// Route gossip - emit one <c>route &lt;dest&gt;</c> line per
    /// destination this node believes it can reach, then <c>end</c>.
    /// Receivers import each row as a learned route (with
    /// <c>Source = "gossip"</c>) and use the existing failure-counter
    /// invalidation if it turns out not to work.
    ///
    /// <para>
    /// Filter: only routes whose <see cref="DbLearnedRoute.ConsecutiveFailures"/>
    /// is zero. Skips rows we ourselves think are broken; an
    /// imported-via-gossip row is suppressed too (we don't re-export
    /// hearsay - that's how distance-vector loops form).
    /// </para>
    /// </summary>
    private async Task<string> RoutesAsync()
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Manual neighbours - the most trusted class. Highest priority
        // in the gossip output too. Gossip-importers will re-validate
        // by attempting forwards; a poisoned advert here is bounded by
        // their own ConsecutiveFailures threshold.
        var neighbours = await database.GetNeighbours();
        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            var baseCall = n.Callsign.Split('-')[0];
            if (!emitted.Add(baseCall)) continue;
            sb.Append("route ").Append(baseCall).Append(" hops=1\n");
        }

        // Traffic-learned routes. Only export rows we ourselves trust:
        // ConsecutiveFailures = 0 AND not gossip-imported (don't
        // re-export hearsay). LastUsedAt unset means we've never
        // actually traversed it; suppress those too.
        var learned = await database.GetLearnedRoutesAsync();
        foreach (var r in learned)
        {
            if (r.ConsecutiveFailures > 0) continue;
            if (string.Equals(r.Source, "gossip", StringComparison.OrdinalIgnoreCase)) continue;
            if (r.LastUsedAt == DateTime.MinValue) continue;
            if (string.IsNullOrWhiteSpace(r.DestinationBaseCallsign)) continue;
            if (!emitted.Add(r.DestinationBaseCallsign)) continue;
            var ageSeconds = (int)Math.Max(0, (now - r.LastSeenAt).TotalSeconds);
            sb.Append("route ").Append(r.DestinationBaseCallsign)
              .Append(" hops=2 ageSeconds=").Append(ageSeconds).Append('\n');
        }

        sb.Append("end\n");
        logger.LogInformation("Sending {0} route record(s) to {1}", emitted.Count, sourceCallsign);
        return sb.ToString();
    }

    /// <summary>
    /// The inbox, answering "do we have it already?" from the received
    /// ledger (<see cref="DbReceived"/>), so an offer of a message we hold
    /// is answered <c>ack</c> whatever inbox the bearer was given. Only a
    /// stored message counts, and never one without a salt: the ledger
    /// doesn't remember those.
    /// </summary>
    private sealed class ReceivedLedgerInbox(IBackhaulInbox inner, Database database) : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct) =>
            inner.DeliverAsync(message, sourceCallsign, ct);

        public async Task<bool> HasAsync(string id, long? salt, int length, CancellationToken ct) =>
            salt is { } s && await database.HasReceivedAsync(DbReceived.MakeKey(id, s, length));
    }
}
