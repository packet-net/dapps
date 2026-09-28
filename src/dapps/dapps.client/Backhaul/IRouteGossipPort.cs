namespace dapps.client.Backhaul;

/// <summary>
/// Plumbing seam for the bearer-level session code (in dapps.client)
/// to ask the daemon (in dapps.core) "should I pull <c>routes</c> from
/// this neighbour right now?" and "here are the routes the neighbour
/// returned, please persist." Keeps dapps.client free of database
/// concerns while still letting a session (<see cref="ExchangeSession"/>)
/// piggyback gossip on a call made for other work.
///
/// Implementations live in dapps.core; the gate consults the
/// <c>routegossipstate</c> table and the import upserts into
/// <c>learnedroutes</c>.
/// </summary>
public interface IRouteGossipPort
{
    /// <summary>True when the staleness gate would let a pull from
    /// <paramref name="remoteCallsign"/> proceed right now. Pure read;
    /// the caller follows up with <see cref="RecordPulledAsync"/> if
    /// the pull actually happens.</summary>
    Task<bool> ShouldPullAsync(string remoteCallsign, CancellationToken ct);

    /// <summary>Persist a fresh routes pull. <paramref name="routes"/>
    /// is what the neighbour returned; the importer writes them as
    /// gossip-sourced learned routes via the advertiser as next-hop.</summary>
    Task ImportAsync(string advertiserCallsign, IReadOnlyList<DappsProtocolClient.GossipedRoute> routes, CancellationToken ct);

    /// <summary>Record that the pull happened (whether routes came back
    /// or not). Bumps the staleness clock so the gate suppresses
    /// repeated pulls within the configured window.</summary>
    Task RecordPulledAsync(string remoteCallsign, CancellationToken ct);
}
