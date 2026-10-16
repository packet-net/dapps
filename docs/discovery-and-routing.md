# Discovery & routing

How a DAPPS node finds other DAPPS nodes, and how it decides where to send a given message.

## The mental model

Three tables, each with a single job:

1. **Discovery channels** - bearers / frequencies / multicast groups DAPPS will beacon on, listen on, and run scheduled solicits over. You configure these explicitly; nothing is on by default.
2. **Discovered peers** - callsigns heard on a channel, with bearer hints and a "last seen" timestamp. Populated automatically by beacons + solicits.
3. **Neighbours** - callsigns DAPPS will actually forward to. You add these yourself; DAPPS never adds one on its own.

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
- **Enabled flag** + free-form notes. The dashboard form sets the bearer, port, link class, beacon cadence, airtime budget, solicit interval and enabled flag, and a row can be edited, enabled or disabled from the table, taking effect within about 10 seconds; the TTL and cost take the link class defaults and notes stay empty unless set through `POST /DiscoveryChannels`. If the packet node is down when DAPPS starts, or drops later, discovery retries it: every 10 seconds at first, backing off to every 5 minutes.

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

If DAPPS already has a session open with the peer, a probe doesn't dial: a second call would reset the link. It sends a `ping` down that session instead and waits up to a minute for the `pong`. The answer says how long the round trip took, or that none came, along with who called whom, how long the session has been up, when the peer was last heard, and how many messages are waiting for an answer. That tells apart a link that works, one that is up at your node but gets nothing through, and a peer on a version before 0.50.0, which doesn't answer pings. When DAPPS holds the peer but has no session running to ask, the probe says so and for how long. The dashboard shows all this in a pop-up after **probe now**.

Node-prompt auto-discovery is gated on `DAPPS_AUTO_DISCOVER_VIA_NODE_CALL=true`. When on, every AGW DAPPS beacon also seeds a node-prompt-probe candidate for the source's base callsign.

## Neighbours

The actual forwarding partners. You add them on the dashboard's **Topology > Neighbours** tab or with the `/Neighbours` REST endpoint. DAPPS doesn't add neighbours by itself; you pick them from the peers you've heard.

Each neighbour row carries: callsign, bearer port (for AGW or RHPv2), UDP endpoint (for the datagram bearer), and optionally a connect-script plus per-neighbour compression and session-tail overrides.

Enter a neighbour under its DAPPS callsign with the SSID. Mail goes to the neighbour with the exact call+SSID first. One entered under another SSID of the same callsign still works as a stand-in, and the Neighbours panel marks it so you can tidy it up.

## The two routing algorithms

Selected via `DAPPS_ROUTING_ALGORITHM` (restart required).

### `passive-flood` (default)

AODV-flavoured. Routes are learned from observed forwards: when a message is forwarded *through* this node, the source becomes a known reach, the next hop becomes the way to reach the destination. The learned-routes table builds up over time without any extra airtime.

Looks at the message destination, walks: neighbour -> fresh discovered peer -> route hint -> learned route. First match wins. A message passing through never goes back to the neighbour it came from, whichever of these suggests it.

If nothing matches, what happens depends on where the message started:

- **Sent from this node:** it floods. The message goes once to every neighbour, and the replies teach the routes.
- **Passing through:** it waits in the queue. Each forwarder run tries again, so it goes as soon as a route turns up, and it expires by its ttl like any other message. Only the node a message started at floods it.

On UDP and MeshCore links the flood carries a hop limit, and a node that receives a flood still holding hops passes it on to its other neighbours. AGW and RHP sessions carry no hop count, so a node that gets a flooded message over one treats it like any other message passing through.

Learned routes are kept per station, SSID included: a route to `G6FCI-7` says nothing about `G6FCI-8`, and two nodes under one callsign learn routes to each other like any other pair.

Works well on small meshes. The trade-off is no proactive route discovery. Until traffic, gossip or a hint has given a route, mail to a far station goes out as a flood from the sender, and over AGW or RHP that flood reaches only the sender's neighbours: one with no route of its own keeps the message until a route turns up. Add a route hint if you need mail to get further sooner.

### `meshcore`

DSR-flavoured. The sender stamps the path on the message; intermediate nodes follow it. Adds a few bytes per message; works better when the topology is volatile, when you want explicit per-message path control, or when you're emulating MeshCore-on-AX.25 for testing.

The rules above about only the sender flooding and never sending a message back where it came from belong to `passive-flood`. The `meshcore` stack doesn't follow them yet: a relay with no path still starts a fresh flood of its own (tracked in #295 and #296).

## Route gossip

Once two DAPPS nodes have a session open for real work (a push, a probe, a reverse poll), they piggyback a `routes` exchange to share what they know about further-reach destinations. The receiver writes each gossiped route into its learned-routes table marked `source=gossip`; the existing failure-counter machinery invalidates anything that turns out not to work.

Bounded by a per-(local, remote) staleness gate: at most one pull per `RouteGossipStalenessHours` (default 6h) per neighbour, regardless of session frequency. No scheduled transmission - if there's no traffic and no probes, no gossip exchange happens. Setting `RouteGossipStalenessHours=0` disables it entirely.

The advertiser filters: only routes whose failure counter is zero, only routes the daemon itself has actually traversed, never gossip-imported routes (don't re-export hearsay). Manual neighbours are always advertised since they're the most-trusted class.

Each route is advertised under the destination's full callsign, SSID included, so two SSIDs of one callsign are two routes. Every node needs 0.47.0 or later for this to help; with an older node on either end the route simply goes unused, and mail falls back to the other ways of finding a path.

Learned routes have no dashboard view or REST endpoint: they live in the `learnedroutes2` table of the DAPPS database, each with its source (`gossip` or `traffic`). Resolution priority unchanged: neighbour -> discovered -> hint -> learned (gossip and traffic share the learned tier).

## Route hints

A manual "for messages destined for X, send via neighbour Y" entry. It's used when X isn't itself a neighbour or a freshly heard peer, and before any learned route. Useful for asymmetric routing where the natural route in one direction differs from the other.

There's no dashboard page or REST endpoint for route hints. Listing or adding one means reading or writing a row in the `routehints` table of the DAPPS database. Y must already be one of your neighbours.

A hint for a callsign without an SSID (`G6FCI`) covers every SSID of that callsign. One with an SSID (`G6FCI-8`) covers that station only, and wins over one without. A hint for `*` catches anything else.

## Reaching nodes through non-DAPPS intermediates

When the only path to a peer runs through bare packet nodes that don't speak DAPPS or NET/ROM, DAPPS supports a **connect-script** on the neighbour row - a series of `(send, expect)` steps the daemon plays before falling into the DAPPS prompt. Same use case as the operator's manual `C node1 / C node2 / ... / DAPPS` chain, automated. See [Multi-hop via non-DAPPS nodes](multi-hop.md).

## What you actually do

For most operators, the simplest workable setup is:

1. Add **one or two manual neighbours** for the peers you actually want to talk to. This works without any discovery system at all.
2. **Add a discovery channel** for the bearer port your DAPPS beacon should go on. The link class sets the cadence (every 30 minutes on VHF/UHF FM, every 2 hours on HF) unless you set **Beacon every**. Set a per-channel airtime budget.
3. Once you've heard from a few peers via beacons and have an idea of the on-air ecosystem, **enable probing** with the `Overnight` strategy so the connectivity matrix is verified during quiet hours.
4. Decide whether you want **scheduled polling** on (you probably don't if your peers are well-behaved; every session carries traffic both ways, which covers the common case).

The dashboard's **Topology** tabs show the live state of most of this - neighbours, discovery channels, heard peers, probed and polled nodes - so you can confirm what's working without guessing. Learned routes and route hints are only visible in the DAPPS database.
