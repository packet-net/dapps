# dapps.meshcore — MeshCore Companion bearer (Phase H1, #154)

Carries DAPPS `BackhaulMessage`s over a MeshCore radio's **private channel**, integrated behind
the standard DAPPS bearer seam (`IDappsBackhaul` / `IBackhaulInbox`). Proven end-to-end on two
Heltec V3s; see the standalone PoC + characterisation under `poc/MeshCorePoc/` for the wire-shape
evidence this is built on.

## What it does

- **Binary channel-data transport** (`MeshCoreChannelTransport`) — encodes a `BackhaulMessage`
  with the real `dapps.client` codec + packetiser, optionally compresses it, fragments to the
  ~165 B LoRa payload, and sends each fragment as a `SEND_CHANNEL_DATA` (0x3E) datagram. Each
  frame carries a 1-byte rolling nonce so the mesh's dedup table doesn't drop identical frames.
- **Compression** (`DappsCompression`) — zstd with a shared dictionary trained on representative
  DAPPS traffic; collapses most messages to a single LoRa packet (~3× goodput). Versioned (`v1`);
  per-message compressed flag on the wire.
- **Airtime governor** (`TxBudget`) — a hard, self-enforced trailing-hour airtime budget (default
  30 s/hr ≈ 0.83 % duty). Sends over budget are refused (back-pressure), so DAPPS can't burst the
  shared channel. The good-citizen control for riding a shared preset.
- **Adaptive airtime** (`ChannelMonitor`, #157) — estimates channel occupancy from the radio's
  `LOG_RX_DATA` (0x88) overheard-packet events, then does listen-before-talk and refuses sends when
  the channel is congested (a *dynamic* good-citizen control on top of the static budget). A per-node
  threshold jitter keeps two contending nodes from backing off in lockstep.
- **End-to-end reliability** (`MeshCoreReliability`, #26) — the channel is a fire-and-forget flood
  with no link ACK, so DAPPS adds its own, **datagram-style, not session-based**: the receiver ACKs
  any data message addressed to it (a tiny `mc-ack` control broadcast), the sender resends unacked
  messages on exponential backoff until acked or their lifetime expires, and the receiver **dedups by
  message id** so a resend after a lost ACK is delivered to the app only once (idempotent). Resends
  and ACKs are ordinary channel traffic, subject to the governor + adaptive controls.
- **Broadcast semantics** — a private channel is one shared medium, so a message is broadcast once
  and the addressee self-selects (the inbox `IsLocal` gate). Identical message ids offered for
  multiple neighbours are coalesced within a window so we don't re-broadcast.
- **Passive discovery** (#27) — a node auto-records peers it hears over MeshCore as routable
  `DbDiscoveredPeer`s (throttled, self-skipping), so outbound to them works with no manual neighbour.
- **Watchdog + recovery** (`MeshCoreLink`, #160) — opens and configures the radio, then detects a
  hung/mute companion (idle liveness probe) and recovers it by hard-resetting the ESP32 over
  CP2102 DTR/RTS, re-opening, and re-applying the radio/channel config (including the flood-scope,
  which is RAM-only). Bounded attempts + backoff; link state surfaced (`Healthy`/`Resetting`/`Failed`).
- **Device control** — region presets (`Regions`: `uk-narrow`, `uk-test`, `eu-legacy`, or `custom`),
  TX power (region-capped), channel name + PSK, node name.
- **Deployment models A/B/C** (#24) — first-class preset + flood-scope config to trade off public-
  repeater carriage vs containment vs physical isolation. See **Containment** below.

For scale-testing this bearer over multi-hop topologies without radios, see the in-process mesh in
[`../dapps.meshcore.sim`](../dapps.meshcore.sim/README.md).

## Enabling it in a dapps node

The bearer is registered in `Program.cs` (before the AGW catch-all) and driven by
`MeshCoreBearerService`. It is **inert (no serial port opened) unless `MeshCoreEnabled=true`**.
Configure via `DAPPS_MESH_CORE_*` env vars (or the `systemoptions` table):

| Env var | Default | Meaning |
|---|---|---|
| `DAPPS_MESH_CORE_ENABLED` | `false` | turn the bearer on |
| `DAPPS_MESH_CORE_PORT` | `/dev/ttyUSB0` | radio serial port |
| `DAPPS_MESH_CORE_REGION` | `uk-test` | localisation preset (freq/BW/SF/CR + power cap); `custom` = model C |
| `DAPPS_MESH_CORE_CUSTOM_PRESET` | _(empty)_ | model C: when region=`custom`, `freq=868.4;bw=62.5;sf=8;cr=8;pwr=14` |
| `DAPPS_MESH_CORE_FLOOD_SCOPE_KEY` | _(empty)_ | model B: blank = unscoped; set to contain floods (see Containment) |
| `DAPPS_MESH_CORE_TX_POWER_DBM` | `8` | TX power (capped by region) |
| `DAPPS_MESH_CORE_CHANNEL_INDEX` | `1` | radio channel slot |
| `DAPPS_MESH_CORE_CHANNEL_NAME` | `dapps` | channel label |
| `DAPPS_MESH_CORE_CHANNEL_PSK` | `dapps-default-channel` | 32-char hex (16 B) or a passphrase |
| `DAPPS_MESH_CORE_NODE_NAME` | `DAPPS` | radio advert name |
| `DAPPS_MESH_CORE_AIRTIME_BUDGET_SECONDS_PER_HOUR` | `30` | governor budget |
| `DAPPS_MESH_CORE_COMPRESS` | `true` | zstd-dict compression |
| `DAPPS_MESH_CORE_CONGESTION_BACKOFF_FRACTION` | `0.5` | adaptive: refuse sends when channel occupancy ≥ this (0 disables) |
| `DAPPS_MESH_CORE_LBT_GUARD_MS` | `400` | adaptive: listen-before-talk guard in ms (0 disables) |
| `DAPPS_MESH_CORE_RELIABLE_DELIVERY` | `true` | end-to-end ACK + resend of unacked messages (#26) |

Inbound is fully wired: received messages are decoded and delivered to `IBackhaulInbox` (DB + MQTT),
sender derived from the in-band `LinkSourceCallsign`. Outbound is selected for routes carrying a
MeshCore channel hint (`BackhaulRoute.MeshCoreChannel`); wiring that hint onto neighbour rows
(`DbNeighbour`/`RouteBuilder`) is the remaining "usable from a configured neighbour" step (#155).

## Containment — deployment models (#24)

**A private channel gives privacy (PSK) but NOT containment.** Channel messages flood
**unscoped** by default, and any same-preset **Repeater/Room-Server** relays them network-wide
*without needing the PSK* (source-verified: `simple_repeater` only decrypts to display, it
forwards regardless). So on the public UK-narrow preset our traffic can be carried across the
whole MeshCore net. Three deployment models, selectable per node:

| Model | Config | Isolation | When |
|---|---|---|---|
| **A** unscoped public preset | `REGION=uk-narrow`, no scope key | none — public repeaters carry our floods everywhere | light traffic only; leans on the good-citizen controls (airtime governor, LBT, congestion backoff) |
| **B** scoped public preset | `REGION=uk-narrow` + `FLOOD_SCOPE_KEY=<name>` | floods dropped by repeaters that don't share the scope | free public-repeater carriage between *our* scoped repeaters |
| **C** dedicated preset | `REGION=custom` + `CUSTOM_PRESET=freq=…;bw=…;sf=…;cr=…;pwr=…` | total physical isolation (own frequency/SF) | own infra; least config risk; the clean long-term option |

**Model B — flood-scope**, source-verified against `companion-v1.16.0`:
- The bearer sends Companion `CMD_SET_FLOOD_SCOPE_KEY (0x36)` at every (re)configure. The 16-byte
  key is **never transmitted** — the radio HMACs it to a 2-byte transport code and marks our
  floods `ROUTE_TYPE_TRANSPORT_FLOOD`. A repeater/room-server that lacks a matching region with
  flood permission **silently drops** the packet (`allowPacketForward` → `false`). Real containment,
  not advisory.
- `FLOOD_SCOPE_KEY` is treated as a **public region name** and hashed `SHA256("#"+name)[..16]` —
  the same derivation MeshCore uses — so you can carry traffic between your own repeaters by
  configuring each with `region put <name>` (flood-allowed). A 32-char hex value is used verbatim.
- **Caveats (verified):** the key is a *routing label, not a secret* — anyone who knows the name
  derives it. True `$`-private scope keys are **stubbed** in v1.16.x (non-functional). Scope is
  **global per node**, not per-channel. Scoping is **RAM-only** (reset on reboot), so the bearer
  re-applies it after every watchdog recovery. It's **off by default**. If the firmware is too old
  to accept `0x36`, the bearer logs a warning and traffic stays unscoped (falls back to model A).
- Model B needs your **own scoped Repeater-firmware nodes** to carry traffic between non-adjacent
  DAPPS nodes (a plain Companion doesn't repeat floods at all).

## Soak harness

`dapps.meshcore.soak` drives the **real** bearer classes through the real seam between two radios
(continuous sequence-numbered traffic, loss/airtime stats, optional forced watchdog recovery):

```
dapps-meshcore-soak --self GB7AAA-1 --peer GB7BBB-1 --region uk-test \
  --channel-index 2 --channel dapps-soak --duration-sec 600 --interval-sec 25 \
  --budget 120 [--force-reset-at-sec 200]
```

Run it symmetrically on both radios (self/peer swapped). `MESHCORE_TRACE=1` logs every serial frame.
