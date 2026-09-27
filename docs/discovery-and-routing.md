# Discovery & routing

How a DAPPS node finds other DAPPS nodes, and how it decides where to send a given message.

## The mental model

Three tables, each with a single job:

1. **Discovery channels** - bearers / frequencies / multicast groups DAPPS will beacon on, listen on, and run scheduled solicits over. You configure these explicitly; nothing is on by default.
2. **Discovered peers** - callsigns heard on a channel, with bearer hints and a "last seen" timestamp. Populated automatically by beacons + solicits.
3. **Neighbours** - callsigns DAPPS will actually forward to. May be hand-added by the operator, or auto-promoted from the discovered-peers table.

Plus two derived stores:

4. **Probed nodes** - per-callsign liveness state from connected-mode probes. Tracks success/failure history, source (`neighbour` for direct, `via:CALL` for transitive, `node-prompt:CALL` for node-prompt-discovered).
5. **Learned routes** - for the `passive-flood` algorithm, observed forwards build up an internal "I know how to reach X via Y" map.

## Discovery channels

A discovery channel is the tuple `(bearer, channel-key)`. For AGW, the channel key is the bearer port. For UDP datagram, it's the multicast endpoint. Each channel carries:

- **Beacon cadence** - how often we transmit our own beacon on this channel.
- **Advertised TTL** - how long peers should consider our beacon valid.
- **Link-class hint** - used by the routing cost model.
- **Optional per-channel airtime budget** - caps tx on this channel independently of the global budget.
- **Optional scheduled-solicit interval** - for HF NVIS where push-only beaconing is too expensive.
- **Enabled flag** + free-form notes.

There is **no default channel**. Operators add channels via the dashboard's **Discovery channels** section once they've thought about which bearer port DAPPS should beacon on. Defaulting to "beacon on port 0" would silently put DAPPS chatter on whatever band that port happens to be - possibly a band the operator's licence class doesn't permit at the relevant power level. Explicit add is the right contract.

## Beacons

When you enable a discovery channel, the beaconer sends a small frame on its cadence - your callsign + bearer hint + cost. Every other DAPPS node that hears it adds (or refreshes) a row in its discovered-peers table.

A beacon is one packet, not a session - it's stateless. The advertised TTL is how long the receiver should remember the row before it ages out.

## Solicits

Beacons are push: I send mine, you happen to be listening. Solicits are pull: I ask "is anyone out there?" and listen for replies for a window.

Useful on **HF NVIS** where the round-trip cost of a beacon (and the airtime budget you'd have to give it) makes "transmit and hope" expensive. Solicits are operator-triggered (via the dashboard) or scheduled per-channel.

## Probes

A probe is a connected-mode session - DAPPS opens a real DAPPS session to a peer's callsign and confirms the round-trip works. Probing is **off by default**; turn on with `DAPPS_PROBING_ENABLED=true`.

Three flavours:

| Flavour            | What it does                                                                                                                                  | Source flag             |
|--------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|-------------------------|
| **Direct**         | Open a DAPPSv1 session, confirm prompt, hang up. Records success/failure.                                                                     | `neighbour`             |
| **Transitive**     | After a successful direct probe, ask the peer "who do you know?" via the `peers` command. Seed each unknown callsign as a candidate.          | `via:<asked-peer>`      |
| **Node-prompt**    | For peers that aren't (yet) DAPPS - connect to the BPQ node prompt, type the application command (`DAPPS` by default), and probe from there.  | `node-prompt:<source>`  |

Node-prompt auto-discovery is gated on `DAPPS_AUTO_DISCOVER_VIA_NODE_CALL=true`. When on, every AGW DAPPS beacon also seeds a node-prompt-probe candidate for the source's base callsign.

## Neighbours

The actual forwarding partners. Two ways a row lands here:

- **Operator-added** via the dashboard's Neighbours panel or the `/Neighbours` REST endpoint.
- **Auto-promoted** from a successful probe (when configured).

Each neighbour row carries: callsign, bearer port (for AGW or RHPv2), UDP endpoint (for the datagram bearer), and an optional cost override.

## The two routing algorithms

Selected via `DAPPS_ROUTING_ALGORITHM` (restart required).

### `passive-flood` (default)

AODV-flavoured. Routes are learned from observed forwards: when a message is forwarded *through* this node, the source becomes a known reach, the next hop becomes the way to reach the destination. The learned-routes table builds up over time without any extra airtime.

Looks at the message destination, walks: explicit per-destination route hint → known-good neighbour → discovered peer → previously-learned route. First match wins.

Works well on small meshes. The trade-off is no proactive route discovery - destinations not yet seen via traffic are unknown until a forward goes through us.

### `meshcore`

DSR-flavoured. The sender stamps the path on the message; intermediate nodes follow it. Adds a few bytes per message; works better when the topology is volatile, when you want explicit per-message path control, or when you're emulating MeshCore-on-AX.25 for testing.

## Route gossip

Once two DAPPS nodes have a session open for real work (a push, a probe, a reverse poll), they piggyback a `routes` exchange to share what they know about further-reach destinations. The receiver writes each gossiped route into its `learnedroutes` table marked `source=gossip`; the existing failure-counter machinery invalidates anything that turns out not to work.

Bounded by a per-(local, remote) staleness gate: at most one pull per `RouteGossipStalenessHours` (default 6h) per neighbour, regardless of session frequency. No scheduled transmission - if there's no traffic and no probes, no gossip exchange happens. Setting `RouteGossipStalenessHours=0` disables it entirely.

The advertiser filters: only routes whose failure counter is zero, only routes the daemon itself has actually traversed, never gossip-imported routes (don't re-export hearsay). Manual neighbours are always advertised since they're the most-trusted class.

The dashboard's learned-routes view marks gossip-sourced rows distinctly from traffic-learned ones. Resolution priority unchanged: hint → neighbour → discovered → learned (gossip and traffic share the learned tier).

## Route hints

A manual override. The `/RouteHints` endpoint (and dashboard panel) let you say "for messages destined for X, always try Y first." Useful for steering around a known-broken link, or for asymmetric routing where the natural route in one direction differs from the other.

## Reaching nodes through non-DAPPS intermediates

When the only path to a peer runs through bare packet nodes that don't speak DAPPS or NET/ROM, DAPPS supports a **connect-script** on the neighbour row - a series of `(send, expect)` steps the daemon plays before falling into the DAPPS prompt. Same use case as the operator's manual `C node1 / C node2 / ... / DAPPS` chain, automated. See [Multi-hop via non-DAPPS nodes](multi-hop.md).

## What you actually do

For most operators, the simplest workable setup is:

1. Add **one or two manual neighbours** for the peers you actually want to talk to. This works without any discovery system at all.
2. **Add a discovery channel** for the bearer port your DAPPS beacon should go on, with a sensible cadence (e.g. every 10 minutes for VHF FM, longer for HF). Set a per-channel airtime budget.
3. Once you've heard from a few peers via beacons and have an idea of the on-air ecosystem, **enable probing** with the `Overnight` strategy so the connectivity matrix is verified during quiet hours.
4. Decide whether you want **scheduled polling** on (you probably don't if your peers are well-behaved; every session carries traffic both ways, which covers the common case).

The dashboard's discovery panels show the live state of all of this - heard peers, probed nodes, learned routes - so you can confirm what's working without guessing.
