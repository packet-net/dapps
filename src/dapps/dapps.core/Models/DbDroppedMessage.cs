using SQLite;

namespace dapps.core.Models;

/// <summary>
/// A message that DAPPS dropped - TTL expiry, hash mismatch on receive,
/// no-route give-up, etc. Soft-deletion target: rows move from
/// <see cref="DbMessage"/> to here rather than being hard-deleted, so
/// the operator can see what was thrown away during RF testing.
///
/// Mirrors <see cref="DbMessage"/>'s shape plus <see cref="DroppedAt"/>
/// + <see cref="Reason"/>. Kept for 90 days after <see cref="DroppedAt"/>,
/// then deleted by the retention sweep (<see cref="Services.TtlSweeperService"/>).
/// </summary>
[Table("dropped_messages")]
public class DbDroppedMessage
{
    [PrimaryKey, NotNull]
    public string Id { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public long? Salt { get; init; }
    public string Destination { get; init; } = "";
    public string SourceCallsign { get; init; } = "";
    public string OriginatorCallsign { get; init; } = "";

    /// <summary>As <see cref="DbMessage.FromCallsign"/>.</summary>
    [Ignore]
    public string FromCallsign => string.IsNullOrEmpty(OriginatorCallsign) ? SourceCallsign : OriginatorCallsign;
    public string AdditionalProperties { get; init; } = "";
    public bool Forwarded { get; init; }
    public bool LocallyDelivered { get; init; }
    public int? Ttl { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>UTC instant we decided this message was dead.</summary>
    [Indexed]
    public DateTime DroppedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Short tag explaining the drop. Currently used:
    /// <c>ttl-expired</c>, <c>stalled-awaiting-prior</c>,
    /// <c>gap-skipped</c>. Stable across releases - kept short for
    /// dashboard display.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Opt-in ordering: stream id, seq, and gap-timeout
    /// preserved from the live row so the dropped-messages dashboard
    /// can show why a stream stalled. Null on non-stream rows.
    /// <c>dropped_messages_stream</c> serves the ordering cursor's check
    /// for seqs the operator dropped (#389) or that expired by TTL before
    /// delivery (#398); sqlite-net creates it on
    /// start-up, and an older version simply ignores it.</summary>
    [Indexed(Name = "dropped_messages_stream", Order = 1)]
    public string? StreamId { get; init; }
    [Indexed(Name = "dropped_messages_stream", Order = 2)]
    public uint? StreamSeq { get; init; }
    public uint? StreamGapTimeoutSeconds { get; init; }
}
