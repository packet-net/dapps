using SQLite;

namespace dapps.core.Models;

/// <summary>
/// A path this node has learned by observing the
/// <c>TraversedHops</c> field on an inbound flood-discovery message -
/// the MeshCore-flavoured equivalent of <see cref="DbLearnedRoute"/>,
/// but storing the full ordered intermediate-hop list rather than
/// just the next hop. When this node later wants to send to
/// <see cref="DestinationCallsign"/>, the intermediates can be
/// embedded in the outbound <c>BackhaulMessage.SourceRoute</c>,
/// avoiding a fresh flood per destination.
///
/// One row per destination station, SSID included; new observations overwrite
/// the previous path. As with <see cref="DbLearnedRoute"/>, future
/// work could maintain a candidate set with weighting; today the
/// most-recent flood wins.
/// </summary>
// "discoveredpaths2": "discoveredpaths", keyed by base callsign, until
// 0.47.0 (#242); a new name so a downgrade still starts.
[Table("discoveredpaths2")]
public class DbDiscoveredPath
{
    /// <summary>The destination's full callsign, SSID included, in
    /// <see cref="dapps.core.Services.DestinationParser.StationKey"/> form
    /// (upper case, no "-0"): G0FOO-1 and G0FOO-9 are two stations and
    /// keep a path each (#242). Before 0.47.0 this was the base
    /// callsign, in the old "discoveredpaths" table.</summary>
    [PrimaryKey]
    public string DestinationCallsign { get; set; } = "";

    /// <summary>Ordered list of full intermediate-hop callsigns
    /// between us and the destination, comma-separated. Empty string
    /// means the destination is direct (no intermediates needed).
    /// Stored as a string column because SQLite-net-pcl doesn't
    /// natively serialise list types; callsigns are alphanumeric +
    /// hyphen so commas are a safe delimiter.</summary>
    public string IntermediatesCsv { get; set; } = "";

    /// <summary>UTC timestamp of the most recent inbound flood-
    /// discovery that confirmed this path. Updated on every
    /// <c>ObserveInboundAsync</c> matching this destination.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful forward
    /// using this path. Distinguishes "path was discovered" from
    /// "path is actually working."</summary>
    public DateTime LastUsedAt { get; set; }

    /// <summary>Consecutive forward failures since the last success.
    /// When this hits the algorithm's invalidation threshold the row
    /// is deleted and the next forward attempt falls back to either a
    /// fresh flood-discovery (originate) or the static layer.</summary>
    public int ConsecutiveFailures { get; set; }

    public IReadOnlyList<string> GetIntermediates() =>
        string.IsNullOrEmpty(IntermediatesCsv)
            ? []
            : IntermediatesCsv.Split(',');

    public static string ToCsv(IReadOnlyList<string> intermediates)
        => string.Join(',', intermediates);
}
