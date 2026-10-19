using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Plan B6.1 - connected-mode probe-and-map. One row per known
/// callsign that the prober has either tried, or that an operator
/// has flagged opt-out. The schedule runs L2 connects to each
/// row's <see cref="Callsign"/> on the chosen <see cref="LastBearerPort"/>
/// and confirms a <c>DAPPSv1&gt;</c> prompt comes back; the result
/// updates this row.
///
/// Distinct from <see cref="DbDiscoveredPeer"/> (which is "I heard a
/// beacon on this channel"): this row says "I successfully reached
/// the DAPPS layer at this callsign over a connected-mode L2 path."
/// </summary>
[Table("probednodes")]
public sealed class DbProbedNode
{
    /// <summary>Full callsign with SSID (e.g. <c>M0LTE-9</c>) - same
    /// shape as <see cref="DbNeighbour.Callsign"/>. Case-normalised
    /// to upper at write time.</summary>
    [PrimaryKey]
    public string Callsign { get; set; } = "";

    /// <summary>The radio port, numbered as the node shows it (#233),
    /// used on the most recent probe attempt. Null if we've never
    /// attempted, or if the row was operator-created opt-out without a
    /// probe.</summary>
    public int? LastBearerPort { get; set; }

    /// <summary>
    /// True while the port above was saved by an older DAPPS, which
    /// counted the node's AGW ports from 0, and has not yet been turned
    /// into the node's own number (#233, #370). That needs the node's AGW
    /// port list, so it happens when the AGW connection first answers
    /// after the upgrade; until then nothing is sent on this port.
    /// </summary>
    public bool? PortPending { get; set; }

    /// <summary>UTC timestamp of the most recent probe attempt, success
    /// or otherwise. Null = never attempted.</summary>
    public DateTime? LastProbedAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful probe
    /// (DAPPSv1&gt; prompt observed). Null = never reached.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Last error string, populated on a failed probe.
    /// Empty when the most recent attempt succeeded.</summary>
    public string LastError { get; set; } = "";

    /// <summary>Count of consecutive failed probes. Resets to 0 on
    /// any successful probe. The dashboard surfaces this so a sysop
    /// can spot peers going dark.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Cumulative count of successful probes - purely for
    /// the dashboard's "this peer's been reachable N times" stat.</summary>
    public int SuccessCount { get; set; }

    /// <summary>Operator opt-out: skip this peer in the scheduled
    /// sweep. On-demand probes via the REST endpoint still go through
    /// - operators sometimes want to test an opted-out peer manually.</summary>
    public bool OptOut { get; set; }

    /// <summary>How this row first ended up on our radar. Set on the
    /// initial upsert and preserved across subsequent probe-result
    /// updates. Plan B6.1 Phase 2 introduces <c>via:&lt;callsign&gt;</c>
    /// to mark transitively-discovered rows (callsigns a probed peer
    /// told us they forward to via the new <c>peers</c> command). The
    /// dashboard surfaces the source so a sysop can tell a manual
    /// neighbour apart from a hearsay candidate.
    /// <list type="bullet">
    /// <item><c>neighbour</c> - manual <see cref="DbNeighbour"/> row.</item>
    /// <item><c>discovered</c> - heard via beacon
    /// (<see cref="DbDiscoveredPeer"/>).</item>
    /// <item><c>via:&lt;callsign&gt;</c> - learned from a peers query
    /// addressed to that callsign.</item>
    /// <item>Empty - pre-Phase-2 row written before the column existed.
    /// Treated as <c>neighbour</c> for display purposes.</item>
    /// </list></summary>
    public string Source { get; set; } = "";
}
