using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per outbound transmission the daemon makes. Every beacon,
/// solicit, probe attempt, message forward, poll request, ack, and
/// heartbeat publish is logged here with the reason it was sent.
///
/// The point is operator-side traceability: regulatory compliance
/// ("what did this node put on the air last Tuesday between 02:00
/// and 03:00"), post-mortems ("which probe sweep last touched the
/// route to GB7XYZ before message abc1234 went missing"), and
/// debugging weird discovery interactions.
///
/// Persisted (unlike the in-memory decision-events ring) and queryable
/// (unlike the systemd journal mirror). Aged out by the retention
/// sweeper using <see cref="SystemOptions.TransmissionAuditRetentionDays"/>
/// to keep the table from growing without bound.
/// </summary>
[Table("transmissions")]
public sealed class DbTransmission
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>UTC instant the daemon decided to transmit. For
    /// session-based transmissions (probes, forwards, polls) this is
    /// captured before the connect; for fire-and-forget (beacons,
    /// solicits, heartbeats) it's the moment the bytes hit the bearer.</summary>
    [Indexed]
    public DateTime At { get; init; } = DateTime.UtcNow;

    /// <summary>What kind of transmission. Constrained set so the
    /// dashboard can colour-code and the MCP / REST filter can be
    /// tight. Values: <c>beacon</c>, <c>solicit</c>, <c>solicit-reply</c>,
    /// <c>probe</c>, <c>probe-nodeprompt</c>, <c>forward</c>,
    /// <c>forward-flood</c>, <c>poll</c>, <c>ack</c>, <c>nak</c>,
    /// <c>heartbeat</c>.</summary>
    [Indexed]
    public string Kind { get; init; } = "";

    /// <summary>Bearer the bytes went over. Values: <c>agw</c>,
    /// <c>udp</c>, <c>mqtt</c>. Free-form so a future bearer (MeshCore,
    /// RHPv2) just adds a value rather than mutating an enum.</summary>
    public string Bearer { get; init; } = "";

    /// <summary>Discovery channel key when this is a discovery-class
    /// transmission (beacon, solicit, solicit-reply). For AGW that's
    /// the bearer port as a decimal string; for UDP it's the
    /// multicast endpoint. Empty for non-discovery transmissions.</summary>
    public string ChannelKey { get; init; } = "";

    /// <summary>Target of the transmission. Single callsign for
    /// directed sends (probe, forward, poll, ack); empty for broadcast
    /// (beacon, solicit, heartbeat); the requester's callsign for
    /// solicit-reply.</summary>
    [Indexed]
    public string TargetCallsign { get; init; } = "";

    /// <summary>The DAPPS message id when this transmission relates to
    /// a specific message (forward, ack, nak). Empty
    /// otherwise. Lets an operator follow a single message id across
    /// multiple transmission events.</summary>
    [Indexed]
    public string MessageId { get; init; } = "";

    /// <summary>Bytes put on the wire, when known. <c>0</c> for sites
    /// where the bearer doesn't surface a count (e.g. AGW session
    /// payload, where the framing happens below us). Best-effort, not
    /// regulatory-grade.</summary>
    public int Bytes { get; init; }

    /// <summary>How long the transmission took in milliseconds. For
    /// fire-and-forget transmissions this is the syscall time; for
    /// session-based transmissions (probe, forward, poll) it covers
    /// the connect + protocol round-trip.</summary>
    public int DurationMs { get; init; }

    /// <summary>Did the transmission succeed end-to-end? For probes
    /// this means we got the <c>DAPPSv1&gt;</c> prompt; for forwards
    /// it means we got the <c>ack</c>; for beacons it means the bytes
    /// hit the bearer without throwing.</summary>
    public bool Success { get; init; }

    /// <summary>Free-form reason for the transmission. The "why".
    /// Examples: <c>scheduled beacon emit</c>,
    /// <c>operator-triggered probe</c>,
    /// <c>scheduled poll sweep</c>,
    /// <c>solicit reply to G7XYZ</c>,
    /// <c>forwarder tick: route via M0LTE-1</c>.
    /// Stable across releases; kept short enough for the dashboard
    /// table view (~80 chars).</summary>
    public string Reason { get; init; } = "";

    /// <summary>On failure, the error tag (e.g. <c>RETRYOUT</c> from
    /// AGW connect, <c>timeout</c>, <c>refused</c>). Empty on success.</summary>
    public string ErrorTag { get; init; } = "";
}
