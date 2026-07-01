# Connect via MeshCore

**Status: shipped (Companion-over-USB).** MeshCore is a first-class DAPPS backhaul bearer:
a node can carry `BackhaulMessage`s over a MeshCore radio's private channel alongside (or
instead of) AGW/AX.25. It's **off by default** — set `MeshCoreEnabled=true` to turn it on.
The KISS-driven flavour (below) is still future work.

## Why MeshCore

MeshCore is a small, modern mesh radio firmware (LoRa-shaped today) with a built-in routing
layer that's a much better fit for slow, lossy, mostly-unreliable RF than AX.25. As a bearer
for DAPPS it gives:

- A datagram interface (no AX.25 connection setup, no T1/T2/T3 timer dance) — better for
  short, frequent messages.
- A working hop-by-hop mesh underneath, so DAPPS doesn't have to solve "how do I reach a node
  three hops away" itself.
- A real low-cost long-haul story for operators without HF.

## What's implemented (H1 — Companion over USB)

DAPPS talks to a Heltec-class radio running MeshCore **Companion (USB)** firmware (tested on
v1.16.0) over the serial Companion protocol, using the **binary channel-data** path. The bearer
lives in `dapps.meshcore` and is wired in behind the standard bearer seam; see
[`src/dapps/dapps.meshcore/README.md`](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.meshcore/README.md)
for the deep detail. Feature set:

- **Binary transport + heavy compression** — messages are encoded with the real DAPPS codec,
  zstd-compressed against a **versioned shared dictionary** (~3× goodput, usually one LoRa
  packet/message), fragmented to the ~165 B payload, and sent as channel-data floods.
- **Good-citizen controls** — a hard trailing-hour **airtime governor** plus **adaptive**
  listen-before-talk and congestion backoff (from overheard-packet occupancy), so DAPPS rides a
  shared preset politely.
- **End-to-end reliability** — the channel is a fire-and-forget flood, so DAPPS adds its own
  datagram-style ACK + resend with **idempotent** (dedup-by-id) delivery. Not a session protocol.
- **Passive discovery** — a node auto-learns peers it hears over MeshCore and routes to them with
  no manual neighbour config.
- **Watchdog + recovery** — detects a hung radio and hard-resets it over CP2102 DTR/RTS, then
  re-applies config.
- **Device control** — region presets, TX power (region-capped), channel name + PSK, node name,
  all from the dashboard or config; live status + a Reset-radio button.

## Enabling it

Configure via the dashboard **Settings** page, the `systemoptions` table, or `DAPPS_MESH_CORE_*`
environment variables (note the underscore in `MESH_CORE`). The essentials:

```
DAPPS_MESH_CORE_ENABLED=true
DAPPS_MESH_CORE_PORT=/dev/ttyUSB0
DAPPS_MESH_CORE_REGION=uk-test          # preset: uk-narrow | uk-test | eu-legacy | custom
DAPPS_MESH_CORE_CHANNEL_NAME=dapps
DAPPS_MESH_CORE_CHANNEL_PSK=<32-char hex, or a passphrase>
DAPPS_MESH_CORE_TX_POWER_DBM=8          # capped by the region's regulatory max
```

Other options (all optional, sensible defaults): `NODE_NAME`, `CHANNEL_INDEX`,
`AIRTIME_BUDGET_SECONDS_PER_HOUR`, `COMPRESS`, `CONGESTION_BACKOFF_FRACTION`, `LBT_GUARD_MS`,
`RELIABLE_DELIVERY`, plus the deployment-model knobs below. The full table is in the
[library README](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.meshcore/README.md).
Runtime status/control: `GET /MeshCore/status`, `POST /MeshCore/reset`.

## Deployment models — privacy vs containment

A MeshCore private channel gives **privacy** (PSK) but **not containment**: channel messages
flood *unscoped* by default and any same-preset Repeater relays them network-wide *without the
PSK*. Pick a model per node (details + firmware caveats in the library README's "Containment"):

| Model | Config | What you get |
|---|---|---|
| **A** unscoped public preset | `REGION=uk-narrow`, no scope | free public-repeater carriage everywhere — relies on the good-citizen controls; light traffic only |
| **B** scoped public preset | `REGION=uk-narrow` + `FLOOD_SCOPE_KEY=<name>` | your floods are dropped by repeaters that don't share the scope; needs your own scoped repeaters to carry between DAPPS nodes |
| **C** dedicated preset | `REGION=custom` + `CUSTOM_PRESET=freq=868.4;bw=62.5;sf=8;cr=8;pwr=14` | total physical isolation on your own frequency/SF — least config risk |

## What stays the same

- The DAPPSv1 application layer is unchanged — an app doesn't know or care whether the bearer is
  AGW, UDP, or MeshCore.
- Discovered/neighbour peers hold MeshCore reachability alongside other bearers (a
  `MeshCoreChannel` hint on the route); the routing resolver picks per destination by cost.
- Dashboard, REST, MQTT, MCP — unchanged.

## Routing implications

MeshCore does its own mesh routing under DAPPS, so the two hop-count models stack: a message
that's "one hop" to DAPPS may traverse several MeshCore hops underneath. DAPPS routes by **cost
hints** rather than raw hop counts (MeshCore is a mid-cost RF class), so this works out; it's
worth understanding when tuning a multi-bearer setup. Background:
[docs/meshcore-backhaul-routing.md](https://github.com/packet-net/dapps/blob/master/docs/meshcore-backhaul-routing.md).

## How it's been validated

- **On air**, two Heltec V3s: bidirectional exchange, watchdog reset+recovery, the airtime
  governor and adaptive backoff, reliability recovering ~40 % induced loss with no duplicate
  delivery, passive discovery, and both the custom preset and the flood-scope key being accepted
  by real firmware.
- **In simulation** (`dapps.meshcore.sim`), the real bearer runs over an in-process multi-hop mesh
  the two bench radios can't reach: multi-hop flood + dedup-across-paths, flood-scope containment,
  and reliability recovering every message over four hops at 30–40 % per-edge loss (delivered
  exactly once).
- **Not yet validated on hardware:** a repeater actually *dropping* an out-of-scope flood — that
  needs a physical Repeater node + attenuators, which is the next hardware step.

## Future

- **MeshCore KISS (H2)** — for operators with a radio already in KISS-TNC mode, DAPPS driving
  MeshCore frames itself. Planned, not yet implemented.
- **Firmware flash from DAPPS** — managing/upgrading radio firmware from the node.

If you've got a MeshCore radio and want to test, [open an issue](https://github.com/packet-net/dapps/issues).
