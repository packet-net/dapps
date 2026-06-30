namespace dapps.client.Backhaul;

/// <summary>
/// A unit of DAPPS traffic - bearer-neutral. Outbound:
/// <see cref="IDappsBackhaul"/> implementations translate this shape into
/// bearer-specific frames (DAPPSv1 stream exchange for AGW today;
/// companion datagrams for MeshCore later). Inbound: bearer-specific
/// receive code constructs one of these from a fully-received-and-
/// validated message and hands it to <see cref="IBackhaulInbox"/>.
///
/// <see cref="Headers"/> carries any non-reserved KVs from the on-air
/// `ihave` line (post-A0 the outbound submission path doesn't populate
/// it, but inbound preserves what the peer sent).
/// </summary>
public sealed record BackhaulMessage(
    string Id,
    string Destination,
    long? Salt,
    int? Ttl,
    byte[] Payload,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Originator = null,
    string? LinkSourceCallsign = null,
    byte? FloodHopsRemaining = null,
    IReadOnlyList<string>? SourceRoute = null,
    IReadOnlyList<string>? TraversedHops = null,
    string? MasterId = null,
    int? FragmentIndex = null,
    int? FragmentTotal = null,
    string? StreamId = null,
    uint? StreamSeq = null,
    uint? StreamGapTimeoutSeconds = null);

// LinkSourceCallsign: the *immediate sender's* callsign, distinct from
// Originator (the F1 end-to-end source). Carried on bearers that don't
// natively identify the sender - UDP being the prime example, since
// the source port is ephemeral and there's no session-level handshake
// that establishes peer identity. Stamped by the bearer's send path
// with the local callsign; consumed by the receive path so the inbox
// (and downstream passive-learning algorithms) can see who handed
// each hop the message.
//
// AGW already identifies the link source from the C-frame's CallFrom
// field, so AGW-bearer SendAsync may leave this null; the inbound
// path uses the AGW-supplied identity directly.
//
// FloodHopsRemaining: when set, this message is a B5 cold-start
// flood. Each forwarding hop decrements before re-flooding; the
// flood stops when the value reaches zero. null means "this is a
// regular routed message, not a flood." The bounded-flood fallback
// (FloodFallbackAlgorithm) is the only thing that originates floods;
// other algorithms / inbox handlers just propagate them.
//
// SourceRoute (MeshCore-flavoured): when set, the message must be
// delivered along this exact ordered list of intermediate hops. Each
// forwarder takes the first entry as its next hop, strips it before
// re-encoding, and forwards. When the list is empty the recipient
// uses the destination's callsign as next hop (or delivers locally).
// null means "no embedded path; let the algorithm pick a hop."
//
// TraversedHops (MeshCore-flavoured discovery): the ordered list of
// intermediate node callsigns the message has visited so far,
// excluding the originator and the local node. Each forwarder
// appends its own callsign before re-encoding. Carried on
// flood-discovery messages so the destination (and every transiting
// node) can derive the reverse path back to the originator -
// MeshCoreLikeRoutingAlgorithm uses this to populate its discovered-
// paths table without explicit RREP frames.
//
// MasterId / FragmentIndex / FragmentTotal (F2 multi-part): when
// MasterId is set, this BackhaulMessage is one fragment of a larger
// logical payload that the originator chunked. Intermediate hops
// forward fragments as opaque messages; only the final destination's
// inbox reassembles. Set together (all-or-none on the wire as
// `mid=…` + `frag=N/M` headers); the receiver's IHaveValidator
// rejects any mismatched-presence combination. FragmentTotal ≥ 2;
// single-fragment messages just omit all three fields.
//
// StreamId / StreamSeq / StreamGapTimeoutSeconds (opt-in ordering):
// when StreamSeq is set the message is part of a per-sender ordered
// stream identified by StreamId. The receiver delivers messages on
// each (sender-callsign, StreamId) cursor in monotonically-increasing
// StreamSeq order; gaps stall until the missing seq arrives or
// StreamGapTimeoutSeconds elapses (gt=0 = strict, never skip).
// Wire form: `sid=`, `sn=`, `gt=` keys on the ihave line; codec flag
// bit 9 on datagram bearers. All three are required together when
// any one is set; intermediate forwarders preserve them verbatim.
