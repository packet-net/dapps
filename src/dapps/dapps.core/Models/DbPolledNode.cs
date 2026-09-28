using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Plan F3b - per-neighbour state for the scheduled poll service.
/// One row per callsign that the poll scheduler has touched (or that
/// an operator has flagged opt-out). Distinct from
/// <see cref="DbProbedNode"/> because the action is different -
/// probes are "is DAPPS reachable here?", polls are "drain queued
/// mail" - and the per-neighbour metadata operators want to see is
/// different too.
/// </summary>
[Table("polledNodes")]
public sealed class DbPolledNode
{
    /// <summary>Full callsign with SSID, e.g. <c>M0LTE-9</c>. Same shape
    /// as <see cref="DbNeighbour.Callsign"/>.</summary>
    [PrimaryKey]
    public string Callsign { get; set; } = "";

    /// <summary>UTC timestamp of the most recent poll attempt, success
    /// or otherwise. Null = never attempted.</summary>
    public DateTime? LastPolledAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful poll
    /// (the session got as far as the exchange, regardless of how many
    /// messages came). Null = never reached.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Last error string, populated on a failed poll.
    /// Empty when the most recent attempt succeeded.</summary>
    public string LastError { get; set; } = "";

    /// <summary>Count of consecutive failed polls. Resets on any
    /// successful poll. Surfaced on the dashboard so a sysop can
    /// spot a peer whose mailbox isn't draining.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Cumulative count of messages drained across all
    /// successful polls. Operator-facing tally only - message-level
    /// state lives in <see cref="DbMessage"/> after delivery.</summary>
    public long MessagesDrained { get; set; }

    /// <summary>Operator opt-out: skip this peer in the scheduled
    /// sweep. On-demand polls via the REST endpoint still go through
    /// - operators sometimes want to test an opted-out peer manually.</summary>
    public bool OptOut { get; set; }
}
