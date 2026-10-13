using SQLite;

namespace dapps.core.Models;

/// <summary>
/// A route this node has *learned* by observing inbound traffic - as
/// opposed to <see cref="DbNeighbour"/> (manual operator config) and
/// <see cref="DbRouteHint"/> (manual operator override). Populated by
/// <see cref="dapps.core.Routing.PassiveLearningAlgorithm"/>: every
/// inbound message whose F1 originator is reachable via the link
/// source teaches us the reverse direction. <c>To reach the
/// originator, send via the neighbour that handed me this message.</c>
///
/// One row per destination station, SSID included - newer observations
/// overwrite older ones. Future work could keep a candidate list per
/// destination with weighting, but a single best-effort entry is
/// enough for the v0.x phase.
/// </summary>
// "learnedroutes2": the table was "learnedroutes", keyed by base
// callsign, until 0.47.0 (#242). A new name rather than a migration, so a
// downgrade finds no table of the wrong shape and starts.
[Table("learnedroutes2")]
public class DbLearnedRoute
{
    /// <summary>The destination's full callsign, SSID included, in
    /// <see cref="dapps.core.Services.DestinationParser.StationKey"/> form
    /// (upper case, no "-0"): G0FOO-1 and G0FOO-9 are two stations and
    /// keep a route each (#242). Before 0.47.0 this was the base
    /// callsign, in the old "learnedroutes" table.</summary>
    [PrimaryKey]
    public string DestinationCallsign { get; set; } = "";

    /// <summary>Full callsign with SSID of the next-hop neighbour -
    /// matches a <see cref="DbNeighbour.Callsign"/> row so the
    /// algorithm can resolve the bearer hint at use-time.</summary>
    public string NextHopCallsign { get; set; } = "";

    /// <summary>UTC timestamp of the most recent inbound message that
    /// confirmed this route. Updated on every <c>ObserveInboundAsync</c>
    /// matching this destination - a quiet route ages out implicitly
    /// when nothing's been heard for a while (sweep policy TBD).</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful forward
    /// using this route. Used by the dashboard / debug tooling to
    /// distinguish "route was learned" from "route is actually
    /// working."</summary>
    public DateTime LastUsedAt { get; set; }

    /// <summary>Count of consecutive forward failures since the last
    /// success. Once this hits the algorithm's invalidation threshold
    /// (default 3), the row is deleted and the next forward attempt
    /// falls back to whatever's available (other static sources, or
    /// the bounded flood fallback).</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>How this row was learned. <c>"traffic"</c> (or empty
    /// for legacy rows) - the passive-learning algorithm observed an
    /// inbound message with this originator. <c>"gossip"</c> - a peer
    /// advertised this destination via the <c>routes</c> command.
    /// Gossip-learned rows ride the same invalidation/failure path as
    /// traffic-learned rows but the dashboard surfaces the source so an
    /// operator can tell hearsay from observed.</summary>
    public string Source { get; set; } = "";
}
