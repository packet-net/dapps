using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Plan F2 - multi-part message reassembly buffer. One row per
/// fragment that's arrived at the FINAL destination but whose siblings
/// haven't all arrived yet. Once the full set is present, the inbox
/// concatenates them by <see cref="FragmentIndex"/>, delivers the
/// assembled message via the regular <c>DbMessage</c> + MQTT path,
/// and drops every fragment row sharing the same <see cref="MasterId"/>.
///
/// Intermediate hops (transit nodes) DO NOT use this table - they
/// forward each fragment as an opaque <c>DbMessage</c> row and let
/// the destination reassemble. Only the destination's inbox routes
/// inbound fragments here.
///
/// Stale-fragment sweep: rows whose <see cref="FirstSeenAt"/> is
/// older than <c>SystemOptions.FragmentReassemblyTimeoutSeconds</c>
/// (default 7 days) are dropped by the TTL sweeper. Long timeout
/// because HF / mesh propagation gaps can run multiple days, and we'd
/// rather hold incomplete state on disk than lose the partial work.
/// </summary>
[Table("fragments")]
public sealed class DbFragment
{
    /// <summary>Composite key <c>{MasterId}:{FragmentIndex}</c>.
    /// SQLite-net doesn't support composite primary keys cleanly, so
    /// we synthesise. Use <see cref="MakeKey"/> to build it.</summary>
    [PrimaryKey]
    public string Key { get; set; } = "";

    /// <summary>F2 grouping id (the <c>mid=</c> from the inbound
    /// <c>ihave</c>). All fragments of one logical message share this.</summary>
    [Indexed]
    public string MasterId { get; set; } = "";

    /// <summary>1-based fragment position within the master.</summary>
    public int FragmentIndex { get; set; }

    /// <summary>Total fragment count expected for this master id.
    /// Stored on every fragment row (rather than once-per-master) so
    /// the reassembly check is a simple count comparison.</summary>
    public int FragmentTotal { get; set; }

    public byte[] Payload { get; set; } = [];

    /// <summary>Carried from the inbound message - the assembled message
    /// uses this when delivered. All fragments of the same master id
    /// should agree on these fields; we keep them per-row rather than
    /// requiring a separate per-master metadata table.</summary>
    public string Destination { get; set; } = "";
    public string SourceCallsign { get; set; } = "";
    public string OriginatorCallsign { get; set; } = "";
    public string AdditionalProperties { get; set; } = "{}";
    public int? Ttl { get; set; }

    /// <summary>UTC instant the FIRST fragment of this master id arrived.
    /// Drives the stale-fragment sweep. Re-arrivals (idempotent retransmit
    /// of the same fragment) don't refresh it - the clock starts when
    /// the originator's window started, not when the latest copy
    /// happens to land.</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>The stream fields the fragment carried, if any (#384).
    /// Every fragment of an ordered message carries them, but one that
    /// came through a forwarder older than #122 has lost them, so the
    /// assembled message takes them from whichever fragment has them.
    /// New columns go on the end of the table, where an older version
    /// ignores them.</summary>
    public string? StreamId { get; set; }
    public uint? StreamSeq { get; set; }
    public uint? StreamGapTimeoutSeconds { get; set; }

    public static string MakeKey(string masterId, int fragmentIndex)
        => $"{masterId}:{fragmentIndex}";
}
