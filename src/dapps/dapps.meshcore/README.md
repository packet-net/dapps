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
- **Broadcast semantics** — a private channel is one shared medium, so a message is broadcast once
  and the addressee self-selects (the inbox `IsLocal` gate). Identical message ids offered for
  multiple neighbours are coalesced within a window so we don't re-broadcast.
- **Watchdog + recovery** (`MeshCoreLink`, #160) — opens and configures the radio, then detects a
  hung/mute companion (idle liveness probe) and recovers it by hard-resetting the ESP32 over
  CP2102 DTR/RTS, re-opening, and re-applying the radio/channel config. Bounded attempts + backoff;
  link state surfaced (`Healthy`/`Resetting`/`Failed`).
- **Device control** — region presets (`Regions`: `uk-narrow`, `uk-test`, `eu-legacy`), TX power
  (region-capped), channel name + PSK, node name.

## Enabling it in a dapps node

The bearer is registered in `Program.cs` (before the AGW catch-all) and driven by
`MeshCoreBearerService`. It is **inert (no serial port opened) unless `MeshCoreEnabled=true`**.
Configure via `DAPPS_MESHCORE_*` env vars (or the `systemoptions` table):

| Env var | Default | Meaning |
|---|---|---|
| `DAPPS_MESHCORE_ENABLED` | `false` | turn the bearer on |
| `DAPPS_MESHCORE_PORT` | `/dev/ttyUSB0` | radio serial port |
| `DAPPS_MESHCORE_REGION` | `uk-test` | localisation preset (freq/BW/SF/CR + power cap) |
| `DAPPS_MESHCORE_TX_POWER_DBM` | `8` | TX power (capped by region) |
| `DAPPS_MESHCORE_CHANNEL_INDEX` | `1` | radio channel slot |
| `DAPPS_MESHCORE_CHANNEL_NAME` | `dapps` | channel label |
| `DAPPS_MESHCORE_CHANNEL_PSK` | `dapps-default-channel` | 32-char hex (16 B) or a passphrase |
| `DAPPS_MESHCORE_NODE_NAME` | `DAPPS` | radio advert name |
| `DAPPS_MESHCORE_AIRTIME_BUDGET_SECONDS_PER_HOUR` | `30` | governor budget |
| `DAPPS_MESHCORE_COMPRESS` | `true` | zstd-dict compression |

Inbound is fully wired: received messages are decoded and delivered to `IBackhaulInbox` (DB + MQTT),
sender derived from the in-band `LinkSourceCallsign`. Outbound is selected for routes carrying a
MeshCore channel hint (`BackhaulRoute.MeshCoreChannel`); wiring that hint onto neighbour rows
(`DbNeighbour`/`RouteBuilder`) is the remaining "usable from a configured neighbour" step (#155).

## Soak harness

`dapps.meshcore.soak` drives the **real** bearer classes through the real seam between two radios
(continuous sequence-numbered traffic, loss/airtime stats, optional forced watchdog recovery):

```
dapps-meshcore-soak --self GB7AAA-1 --peer GB7BBB-1 --region uk-test \
  --channel-index 2 --channel dapps-soak --duration-sec 600 --interval-sec 25 \
  --budget 120 [--force-reset-at-sec 200]
```

Run it symmetrically on both radios (self/peer swapped). `MESHCORE_TRACE=1` logs every serial frame.
