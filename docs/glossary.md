# Glossary

Terms used throughout this manual.

**ack** - Acknowledgement. In DAPPS, the protocol-level confirmation that a complete message was received and its content hash matched the offer.

**AGW** - A long-standing TCP host protocol for talking to a packet node / TNC, originating with AGWPE in the 1990s. One of the two bearers DAPPS speaks to packet nodes (the other is RHPv2). DAPPS uses AGW to dispatch sessions in and out of BPQ (and other AGW-aware hosts).

**airtime budget** - Trailing-hour cap on discovery-class transmissions (beacons + solicits + probes), set with `DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR`. Both global (one cap for all channels) and per-channel knobs exist.

**AODV** - Ad-hoc On-Demand Distance Vector - a class of mesh routing protocol where routes are discovered when needed and learned passively from observed traffic. The default DAPPS routing algorithm (`passive-flood`) is AODV-flavoured.

**app** - A piece of software using DAPPS as a transport. Identified by an `app` slug (e.g. `mail`, `chat`, `hello`) - the destination is `app@CALLSIGN`.

**app interface** - The MQTT and REST surfaces DAPPS exposes to local applications.

**`app@CALLSIGN`** - DAPPS's destination addressing scheme. Names a specific app on a specific node; SSID is part of the callsign (e.g. `chat@M0LTE-1` is different from `chat@M0LTE-7`).

**asynchronous** - Messages are queued; delivery happens when the network allows. Real-time connectivity is not a goal.

**backhaul** - The bearer-side communication between two DAPPS nodes (as distinct from the app interface, which is local). AGW and RHPv2 today; MeshCore in flight.

**bearer** - The underlying medium DAPPS hands off to: AGW (via BPQ or another AGW host), RHPv2 (via XRouter or another RHPv2 host), MeshCore Companion (USB, shipped; KISS planned), UDP datagram (test stand-in). The bearer-agnostic seam means apps and routing don't care which.

**beacon** - A small periodic transmission on a discovery channel advertising "I am here, I am callsign X, I'm reachable on this bearer at this cost." Stateless, no session.

**BPQ** - A long-running packet-radio multi-protocol stack, the most common AGW host in service. DAPPS supports BPQ as the production backhaul. Not a hard dependency - anything with an AGW interface should work.

**bearer port** - A 0-indexed identifier for which port on the bearer DAPPS should use for outbound (= which physical radio in `bpq32.cfg`'s `PORTNUM` layout for AGW, or which `PORT=N` block for RHPv2 / XRouter, with DAPPS handling the +1 conversion internally). Per-neighbour or default (`DAPPS_DEFAULT_BEARER_PORT`).

**callsign** - Your radio licence callsign, optionally with an SSID (`-1`, `-7`, etc). DAPPS treats the full call+SSID as a unique identifier for routing; the base callsign without SSID is used for some discovery features.

**channel** (discovery channel) - A `(bearer, channel-key)` tuple DAPPS will beacon on / listen on / run scheduled solicits over. Operators add channels explicitly; nothing is on by default.

**cookie auth** - How the dashboard identifies you. One admin password for the whole node, set on `/Setup`, hashed with PBKDF2-HMAC-SHA256.

**dapps-id** - User property on MQTT publishes / REST submits that DAPPS uses for idempotent at-least-once delivery. Same `dapps-id` twice → second submit is a no-op.

**dapps-source** - User property carrying the originating callsign. Set by DAPPS at the source node and propagated end-to-end across forwarding hops.

**dapps-ttl** - User property carrying the residual TTL in seconds. Decremented on each forwarding hop; messages are dropped on zero.

**DAPPSv1** - The current on-air session-protocol version. The version is the suffix on the prompt; future incompatible cuts would be `DAPPSv2>` etc.

**DSR** - Dynamic Source Routing - a routing class where the sender stamps the path on the message. The `meshcore` algorithm is DSR-flavoured.

**fragment** - A piece of a multi-part message. Payloads larger than the fragment threshold are split into N fragments at submit, each forwarded / acked independently and reassembled at the destination. Each carries its master ID, fragment index, and fragment total.

**forwarder** - The background loop that walks the messages table and dispatches outbound forwards over the bearer. Ticks every 5 s.

**heartbeat** - Periodic operational snapshot published to MQTT topic `dapps/metrics/heartbeat`. Same content as `/Operational`.

**`ihave`** - The first command in a DAPPSv1 session: the sender announces the message's hash, destination, size, and TTL. Receiver responds `send` (ship it) or `?` (already have it).

**inbox** (local inbox) - Messages that have arrived at this node for an app on this node, but the app hasn't acked yet.

**KISS** - A simple framing protocol for talking to a TNC over a serial / TCP link. MeshCore-as-DAPPS-bearer will support a KISS variant for operators who already drive their MeshCore radio in TNC mode.

**link class** - Discovery-channel hint about the bearer's characteristics (1200-baud VHF FM, HF NVIS, IP backbone, etc.). Feeds the routing cost model.

**master ID** - The shared identifier across the fragments of one multi-part message. The receiver uses it to reassemble.

**MCP** - Model Context Protocol. The endpoint at `/mcp` exposes operator-facing tools to AI assistants.

**MeshCore** - A modern small-mesh radio firmware (LoRa-shaped today). Shipped DAPPS bearer via the Companion-over-USB path (binary channel-data, compression, good-citizen controls, reliability, discovery, deployment models A/B/C); off by default. The KISS-driven flavour is still planned.

**MQTT** - A pub-sub messaging protocol; DAPPS embeds an MQTT broker for the local app interface.

**neighbour** - A callsign DAPPS will actually forward to. May be hand-added or auto-promoted from a successful probe.

**node-prompt** - The text prompt presented when an L2 connect lands on a packet node (BPQ et al). DAPPS uses this to probe peers that aren't directly DAPPS - connect, type the application command (`DAPPS` by default), then run a normal probe from the resulting prompt.

**NODECALL** - A callsign assigned to a packet node itself (as distinct from the operator's own callsign). For example, `GB7XYZ` might be the node call and `M0LTE` the operator's call.

**NVIS** - Near-Vertical Incidence Skywave. An HF propagation mode used for short-to-medium-range communication when ground-wave doesn't reach. DAPPS's solicit-and-listen discovery is shaped around its asymmetric-coverage characteristics.

**`peers`** - A DAPPSv1 command for transitive discovery. After a successful probe, the prober asks "who do you know?" and seeds each unknown callsign as a candidate to probe later.

**probe** - A connected-mode session opened to a peer to confirm reachability. Three flavours: direct, transitive (via the peer's `peers` response), and node-prompt-discovered (via the BPQ node prompt for peers that aren't directly DAPPS).

**`rev`** - A DAPPSv1 command meaning "send me anything you're holding for me." Used by both opportunistic and scheduled polling.

**REST** - DAPPS's HTTP-based app interface, alongside MQTT. Same submit shape; different protocol.

**RHPv2** - Remote Host Protocol v2 - a more modern host-to-node protocol than AGW. Required for DAPPS-on-XRouter (AGW outbound on XRouter is structurally broken for DAPPS); selectable with `DAPPS_NODE_BEARER=rhpv2`. Mainline BPQ does not yet ship RHPv2; will work with no DAPPS-side changes when it does.

**route hint** - A manual override that says "for messages destined for X, always try Y first." Stored in the route-hints table.

**session protocol** - The DAPPSv1 session shape: prompt → `ihave` → `send` / `?` → `data` → `ack`. The on-air conversation between two DAPPS nodes.

**solicit** - On-demand or scheduled "is anyone out there?" transmission on a discovery channel. Replies collected for a window. Useful on HF NVIS where push beaconing's airtime is too expensive.

**source-routing** - Stamping the full path on a message at the sender, instead of letting each hop pick the next one. The `meshcore` routing algorithm.

**SSID** - The number after the dash on a callsign (`-1`, `-7`, `-15`). Lets one operator distinguish multiple stations / applications. AGW dispatches by exact call+SSID match.

**TTL** - Time To Live. In DAPPS, the residual lifetime of a message (in seconds). Decremented on each forwarding hop; messages are soft-deleted on zero.

**watchdog** - An external monitoring process scraping `/Health`, `/Operational`, or the MQTT heartbeat to detect when the daemon is unhealthy.
