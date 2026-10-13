using SQLite;

namespace dapps.core.Models;

[Table("messages")]
public class DbMessage
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public long? Salt { get; init; }
    public string Destination { get; init; } = "";

    /// <summary>
    /// The callsign that handed us this message. For an inbound message
    /// from a neighbour DAPPS instance this is the AX.25 connecting station
    /// (the link source - last hop, not necessarily the original sender if
    /// the message was relayed). For a message submitted by a local app
    /// this is our own NODECALL.
    /// </summary>
    public string SourceCallsign { get; init; } = "";

    /// <summary>
    /// F1 end-to-end source tracking: the *originating* callsign, parsed
    /// from the inbound <c>ihave</c>'s <c>src=</c> field. Empty when the
    /// sender pre-dates F1 - in that case the originator is unknown
    /// (could be the link source on a single hop, or any upstream hop on
    /// a relay path). For a locally submitted message this is our own
    /// callsign. Preserved verbatim across re-forwards.
    /// </summary>
    public string OriginatorCallsign { get; init; } = "";

    public string AdditionalProperties { get; init; } = "";

    /// <summary>True once OutboundMessageManager has handed this off to a
    /// neighbour DAPPS instance (only relevant when Destination is remote).</summary>
    public bool Forwarded { get; init; }

    /// <summary>True once a local app has acked receipt via the MQTT or REST
    /// app interface (only relevant when Destination is local).</summary>
    public bool LocallyDelivered { get; init; }

    /// <summary>Residual lifetime in seconds at the moment we received this
    /// message (or null if no ttl= was supplied). The wire-side `ttl=` value
    /// frozen at receive time; remaining lifetime is computed against
    /// <see cref="CreatedAt"/>.</summary>
    public int? Ttl { get; init; }

    /// <summary>UTC instant the row was created - when the message arrived
    /// or was submitted locally. Used with <see cref="Ttl"/> to compute
    /// residual lifetime on forward and to age out expired rows.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// B5 cold-start flood state: when set, this message is in
    /// flood-propagation mode and the value is the remaining hop
    /// budget. Each forwarding hop decrements before re-flooding;
    /// the flood stops at zero. Null means this is a regular routed
    /// message, not a flood. Persisted on the message row so a
    /// flood mid-propagation survives a forwarder tick / process
    /// restart - the next forwarder run picks it back up and
    /// continues at the same hop budget.
    /// </summary>
    public byte? FloodHopsRemaining { get; init; }

    /// <summary>
    /// MeshCore-flavoured source route: comma-separated list of
    /// remaining intermediate-hop callsigns between this node and
    /// the destination. The forwarder picks the first entry as next
    /// hop and strips it before encoding the outbound message.
    /// Empty/null means the algorithm is free to pick a hop.
    /// Stored as CSV because callsigns can't contain commas.
    /// </summary>
    public string? SourceRouteCsv { get; init; }

    /// <summary>
    /// MeshCore-flavoured flood-discovery path accumulator: comma-
    /// separated ordered list of intermediate-hop callsigns the
    /// message has visited so far (excluding originator and the
    /// local node). Each forwarder appends its own callsign before
    /// encoding the outbound copy. Carried only on flood-discovery
    /// messages - the destination uses the inbound value reversed
    /// to populate its <c>discoveredpaths2</c> table.
    /// </summary>
    public string? TraversedHopsCsv { get; init; }

    /// <summary>F2 multi-part: <c>mid=</c> from the on-air <c>ihave</c>,
    /// or set at submit time when local fragmentation kicks in. Null
    /// for non-fragmented messages. Carried on every fragment row of
    /// a logical multi-part message so the forwarder can re-emit
    /// <c>mid=</c> on outbound and the destination's inbox can group
    /// them for reassembly.</summary>
    public string? MasterId { get; init; }

    /// <summary>F2 multi-part: 1-based fragment index. Null when not
    /// fragmented; non-null only when <see cref="MasterId"/> is set.</summary>
    public int? FragmentIndex { get; init; }

    /// <summary>F2 multi-part: total fragment count for the master id.
    /// Null when not fragmented; ≥ 2 when set.</summary>
    public int? FragmentTotal { get; init; }

    /// <summary>Opt-in ordering stream id. Null when this message
    /// isn't part of an ordered stream. The receiver's reorder buffer
    /// keys on (originator-callsign, StreamId).</summary>
    public string? StreamId { get; init; }

    /// <summary>Opt-in ordering: monotonic seq within
    /// (originator, StreamId). Null when not ordered.</summary>
    public uint? StreamSeq { get; init; }

    /// <summary>Opt-in ordering: gap timeout in seconds at originator
    /// time. 0 = strict, &gt;0 = skip gap after that many seconds.
    /// Carried verbatim across re-forwards so the destination sees the
    /// originator's policy regardless of intermediate hops.</summary>
    public uint? StreamGapTimeoutSeconds { get; init; }

    /// <summary>True when the inbox parked this row awaiting an
    /// earlier StreamSeq. Set on arrival when seq &gt; expected;
    /// cleared as the cursor advances and the row drains to MQTT.
    /// Always false for non-ordered messages.</summary>
    public bool PendingInOrder { get; set; }
}
