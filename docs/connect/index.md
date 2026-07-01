# Connect a node

DAPPS does not move bits on the air itself - it hands off to a packet node which speaks AX.25 (or, eventually, MeshCore / RHPv2 / a long-haul satellite link / whatever else). The only thing DAPPS needs from a bearer is a way to:

1. **Send sessions** to a remote callsign on demand (and learn when they fail).
2. **Receive sessions** from a remote callsign and hand the bytes up to the DAPPS protocol parser.

Today that interface is **AGW** - the long-standing host-side TCP protocol that any AGW-aware host application uses to talk to a TNC or a packet node. BPQ is the most common AGW host in service today, so it's the one with the deepest setup guide. But DAPPS isn't BPQ-specific: any AGW host should work, and we'll add documentation for them as operators report success.

| Bearer        | Status         | Setup guide                |
|---------------|----------------|----------------------------|
| BPQ AGW       | Production     | [BPQ (AGW)](bpq.md)        |
| XRouter (RHPv2) | Production - RHPv2 is required for DAPPS-on-XRouter; XRouter AGW does not work as a DAPPS bearer | [XRouter (RHPv2)](xrouter.md) |
| Other AGW host| Likely works   | [BPQ (AGW)](bpq.md) covers the protocol-shaped bits; only the config-file specifics differ |
| MeshCore Companion (USB) | Available - off by default (`MeshCoreEnabled=true`) | [MeshCore](meshcore.md) |
| MeshCore KISS | Planned        | [MeshCore](meshcore.md)    |
| RHPv2 (other hosts) | Will work as soon as another host ships RHPv2 | [RHPv2](rhp.md) |
| UDP datagram  | Test stand-in  | Not for production; the architectural placeholder that proved out the datagram-bearer seam MeshCore now uses. |

## Why bearer-agnostic matters

DAPPS messages get routed across whatever bearers are available between source and destination - there's no rule that says "DAPPS only runs over BPQ" or "DAPPS only runs over VHF AX.25." A node that has both an AGW-attached BPQ and (eventually) a MeshCore radio can forward DAPPS messages between them, with the routing layer choosing the cheaper / more reliable path per destination. The neighbour table doesn't care which bearer a peer is reached over; the routing algorithm doesn't care; the message format doesn't care.

The reason BPQ is the documented setup is that the on-air ecosystem of operators we want to interoperate with already runs BPQ.

## What's the same regardless of bearer

- The DAPPSv1 session protocol - `ihave` / `send` / `data` / `ack` - is identical over any bearer that gives DAPPS a stream-shaped session.
- The discovery model (channels, beacons, probes) is identical.
- The neighbour table and routing graph are identical.
- The dashboard, REST API, MQTT app interface, MCP tools - identical.

## What changes per bearer

- The setup recipe - how you tell the bearer to dispatch DAPPS-bound traffic to the daemon.
- The session shape - stream (AGW, RHPv2) vs datagram (MeshCore, UDP). For datagram bearers DAPPS does its own fragmentation through a small codec; the rest of the system is unchanged.
- The MTU and link-class hints used by the routing cost model.

If you're setting up your first DAPPS node and run BPQ, [start here](bpq.md).
