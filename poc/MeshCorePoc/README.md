# DAPPS ⇄ MeshCore private-channel PoC

Independent proof-of-concept for **issue #137 / Phase H1** (MeshCore Companion-over-USB
as a DAPPS bearer). Standalone .NET 8 console app — **not** wired into DAPPS — that drives
two Heltec WiFi LoRa 32 V3 radios over the MeshCore **Companion** serial protocol and
round-trips a real DAPPS `BackhaulMessage` over a **private (PSK) channel**.

## What it proves (verified on hardware, 2026-06-30)

Two Heltec V3s on Raspberry Pis (`radio1`, `radio2`), same private channel:

```
radio1 send-backhaul  →  LoRa 869.618 MHz / 62.5 kHz / SF8 / CR8  →  radio2 listen

[ch1] >>> BACKHAUL id=aaa0001 DAPPS-R1 -> DAPPS-R2 ttl=3600 linksrc=DAPPS-R1
          payload="single fragment backhaul payload"
[ch1] >>> BACKHAUL id=bbb0002 DAPPS-R1 -> DAPPS-R2 ttl=3600 linksrc=DAPPS-R1
          payload="This is a deliberately long DAPPS BackhaulMessage payload designed to
          span several MeshCore LoRa packets ... reassembly over the air."   (194 B, 4 fragments)
```

The bytes on the wire are the **real DAPPS encoding**: `BackhaulMessageCodec` (v7) +
`Packetiser` (13-byte fragment header), copied verbatim into `vendored/` (see
`vendored/VENDORED.md`). Encode → fragment → base64 → MeshCore channel text → OTA LoRa →
reassemble → decode, with every field (`id`, `dest`, `originator`, `linkSource`, `ttl`,
`payload`) intact, including out-of-order delivery (`selftest`).

## Hardware / firmware / radio setup

| | |
|---|---|
| Board | Heltec WiFi LoRa 32 V3 (ESP32-S3, 8 MB, CP2102 → `/dev/ttyUSB0`) |
| Firmware | MeshCore **Companion (USB)** `v1.16.0` — `Heltec_v3_companion_radio_usb-…-merged.bin`, flashed at `0x0` with esptool |
| Region | **UK 868 "narrow"**: 869.618 MHz / BW 62.5 kHz / SF 8 / **CR 8** (firmware default is CR 5; the live UK net uses CR 8) |
| Channel | slot **1**, name `dapps-poc`, 16-byte PSK = `SHA256("dapps-poc-channel-v1")[:16]` (`3135135f…aae9`) |
| TX power | **8 dBm** on the bench — see "near-field overload" below |

`869.618 MHz` sits in the UK **869.4–869.65 MHz** sub-band: **10 % duty cycle, up to 500 mW ERP**
(more generous than the 1 % / 25 mW 868.0–868.6 band).

## Build / deploy / run

```bash
# build + self-test (no radio needed) — roll-forward if only a newer runtime is installed
dotnet build -c Release
DOTNET_ROLL_FORWARD=LatestMajor dotnet bin/Release/net8.0/meshcore-poc.dll selftest

# publish a self-contained arm64 single-file binary for the Pi
dotnet publish -c Release -r linux-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# on each Pi (CP2102 = /dev/ttyUSB0)
./meshcore-poc info        /dev/ttyUSB0
./meshcore-poc provision   /dev/ttyUSB0 --name DAPPS-R1 --channel-index 1 --channel-name dapps-poc --tx-power 8
./meshcore-poc listen      /dev/ttyUSB0 --channel-index 1            # radio2
./meshcore-poc send-backhaul /dev/ttyUSB0 --from DAPPS-R1 --dest DAPPS-R2 --text "hi"   # radio1
```

`MESHCORE_TRACE=1` logs every serial frame (hex) to stderr.

## Code shape (how it maps onto DAPPS later)

- `MeshCoreClient.cs` — the genuinely new code: Companion serial framing
  (`[0x3C|0x3E][len-LE16][payload]`), opcode set, async request/response, push handling,
  and the `MSG_WAITING → SYNC_NEXT_MESSAGE` inbound drain. Zero DAPPS dependency — this is
  the reusable "MeshCore link" library.
- `PrivateChannelTransport.cs` — carries a `BackhaulMessage` over a channel:
  `Encode → Packetiser.Split → base64 → "D1:"-marked channel text`, and the reverse with a
  `Reassembler`. This is the thin shim that a real `IDappsBackhaul` would own.
- `vendored/` — the real DAPPS wire format, copied so the PoC stays standalone.

## Wire-shape findings (the questions H1 existed to answer)

1. **Companion framing is dead simple** — `0x3C` (host→radio) / `0x3E` (radio→host), then a
   little-endian uint16 length, then `[opcode][payload]`. No SLIP, no CRC. 115200 8N1, hold
   DTR/RTS low so opening the port doesn't reset the board.
2. **Inbound is pull, not push.** The radio emits a 1-byte `MSG_WAITING` (0x83) tickle; you
   then loop `SYNC_NEXT_MESSAGE` (0x0A) until `NO_MORE_MESSAGES`. A bearer's receive loop must
   model this (we also poll as a safety net).
3. **v1.16.0 sends the *legacy* channel-message frame (0x08), not V3 (0x11)** — even when you
   negotiate protocol version 3 in `APP_START`. V3 only seems to apply to contact/direct
   messages here. **A bearer must handle 0x08** (no SNR field). This cost us the first run.
4. **The firmware auto-prepends `"<nodename>: "` to channel text.** We send `"hi"`, it
   transmits `"DAPPS-R1: hi"`. Consequences for a bearer: (a) it eats the byte budget, and
   (b) your framing marker is **not at offset 0** on receive — find it as a substring.
5. **Channel text is UTF-8; `BackhaulMessage` is binary** → base64 (≈ 33 % inflation) *or* the
   binary `SEND_CHANNEL_DATA` (0x3E) / `CHANNEL_DATA_RECV` (0x1B) path. We used base64-text
   (robust, most-tested). Effective payload at `mtu=90`: **77 DAPPS-encoded bytes per LoRa
   packet** (90 raw → 120 b64 + `"D1:"` + `"<name>: "` ≈ 133 chars on air). The binary path
   would recover the base64 overhead (~165 usable bytes/packet) and is the likely production
   choice.
6. **MeshCore does NOT fragment a message** — one text = one packet, hard ceiling ~150–160
   bytes of text (OTA group plaintext cap 165). So DAPPS's `Packetiser` is doing the real work;
   reuse it unchanged.
7. **Near-field overload is real.** At the firmware default **22 dBm** with the two radios
   ~tens of cm apart, **nothing decoded** — the RX front-end saturates. Dropping to **8 dBm**
   fixed it instantly. Worth a note in operator docs for bench setups.

## Slotting into DAPPS (recommended next step)

The seam is ready (see the fresh-look review). To land H1:

1. **Outbound** — a `MeshCoreCompanionBackhaul : IDappsBackhaul`. `CanHandle(route)` matches a
   MeshCore-channel route hint; `SendAsync` = `MeshCoreClient` + `PrivateChannelTransport`
   (reuse `BackhaulMessageCodec`/`Packetiser` directly — drop `vendored/`). Stamp
   `LinkSourceCallsign` on TX exactly like `UdpDatagramBackhaul`.
2. **Inbound** — a `HostedService` owning the `MeshCoreClient` read loop; on each reassembled
   `BackhaulMessage` call `IBackhaulInbox.DeliverAsync(msg, sourceCallsign)`, deriving
   `sourceCallsign` from `LinkSourceCallsign` (sentinel `"MESHCORE"` fallback) — the channel
   is anonymous.
3. **Config** — `DAPPS_MESHCORE_ENABLED`, `DAPPS_MESHCORE_PORT`, `DAPPS_MESHCORE_CHANNEL_INDEX`,
   `DAPPS_MESHCORE_PSK`, radio params. `SystemOptions` keys get `DAPPS_*` binding for free.

Three seam issues to resolve *as part of* this (from the fresh-look review), in priority order:
- **Broadcast fan-out** — a private channel is one shared medium; the OMM's one-`SendAsync`-per-
  neighbour flood would transmit N times where one suffices. Coalesce identical `(msgId,
  channel)` sends, or model a first-class broadcast route. Most important on a duty-cycled band.
- **Positive bearer selection** — replace AGW's `route.UdpEndpoint is null` catch-all with an
  explicit `route.Bearer` discriminator so adding MeshCore doesn't require editing AGW.
- **Airtime budget on the data path** — wire `AirtimeAccountant.TryReserve` (today discovery-only)
  into the MeshCore `SendAsync`, keyed on the channel. The 869.618 sub-band is 10 % duty cycle —
  finite, must be enforced (back-pressure, not drop).

## The MeshCore network is ONE shared broadcast segment (key design constraint)

DAPPS nodes will be sparse relative to MeshCore nodes — long chains of pure-MeshCore relays
between MeshCore-equipped DAPPS nodes — and all DAPPS nodes likely share **one** private channel
across the whole MeshCore network.

**Verified against firmware source** (`Mesh.cpp`, `BaseChatMesh.cpp`, `docs/faq.md`): a group/channel
message (`PAYLOAD_TYPE_GRP_TXT`/`GRP_DATA`) is an **unaddressed, PSK-encrypted FLOOD broadcast** — a
1-byte channel hash, no node addresses, no path, **no ACK and no retransmit** at the MeshCore layer.
Specifics that matter:

- **Relay is by infrastructure, not leaves.** Stock **Companion** nodes do **not** relay
  (`allowPacketForward` → `client_repeat == 0` by default); only **Repeater / Room-Server / Sensor**
  roles re-flood. So two DAPPS Companion nodes out of direct range interwork **only via intervening
  MeshCore repeaters** — and crucially **repeaters forward channel packets without holding the PSK**,
  so the public MeshCore repeater network carries our private DAPPS traffic for free. (A DAPPS node
  *can* opt to relay via `client_repeat` to extend coverage.)
- **Bounded flood:** hard 64-hop cap (`MAX_PATH_SIZE`) plus each repeater's `flood.max` (default 64).
- **Loop suppression:** 8-byte SHA256 packet-hash dedup in a **160-entry cyclic ring with no time
  expiry** — which is exactly why the binary path needs the per-frame nonce (identical bytes = same
  hash = dropped until 160 newer packets evict it).
- **The MeshCore mesh IS the routing layer.** To DAPPS the whole MeshCore network is a single
  broadcast segment — every DAPPS node is effectively a one-hop neighbour, however many MeshCore
  hops away it physically is. All DAPPS nodes tend to hear all DAPPS traffic (subject to flood
  loss, which is *very* significant — there is no link-layer reliability).

Commitments to avoid painting into a corner:

1. **One broadcast per message, never per-neighbour.** A message for a specific DAPPS node is a
   single channel broadcast; the addressee self-selects (`IsLocal`), others ignore or relay across
   *other* bearers. (The fresh-look review's issue #1, now central — not a nice-to-have.)
2. **DAPPS must NOT re-flood within the segment.** MeshCore already flooded it to all channel
   members; re-broadcasting on the same channel doubles airtime and starts a DAPPS-layer flood
   storm. DAPPS-level forwarding applies only at **gateways crossing to another bearer** (MeshCore
   → AX.25, …). The bearer should advertise "shared broadcast segment; intra-segment delivery is
   mine, not yours."
3. **Airtime is a single network-wide shared budget.** One broadcast floods through many nodes,
   burning airtime in each one's RF cell; the 10 % duty / ~1.6 s-per-packet ceiling is shared by
   *every* originator. So heavy compression (≈3× here) directly multiplies network capacity, and
   per-channel airtime budgets must be conservative.
4. **Discovery = passive learning, not flooded beacons.** You learn a DAPPS node exists just by
   hearing its broadcasts; flooding beacons across the whole network is far too costly.
5. **Reliability over an unreliable flood.** No per-hop acks (fire-and-forget), high loss,
   asymmetric paths. Favour end-to-end idempotency + TTL-aware resends with **long backoff** (a
   resend is another network-wide flood); rate-limit hard to avoid flood storms. Both the
   per-frame nonce (mesh dedup) and DAPPS `(Id, source)` dedup matter.
6. **Keep partitioning open.** Assume one channel today but don't hard-code it — multiple private
   channels (regional sub-nets) are the escape valve if one channel saturates.

### Containment: private ≠ contained (verified from source)

A private channel gives **privacy** (PSK) but **not containment**: channel messages flood **unscoped
by default** (`companion_radio/MyMesh.cpp:486–520`; `DEFAULT_FLOOD_SCOPE_NAME` unset in all builds),
and repeaters relay floods **without the PSK** — so on a shared preset our private traffic is
re-flooded network-wide, burning others' airtime invisibly. MeshCore's **flood scope**
(`ROUTE_TYPE_TRANSPORT_FLOOD` + a 2-byte keyed-hash transport code) does contain it — a stock public
repeater with no matching region **drops** a scoped flood (`simple_repeater/MyMesh.cpp:436–439`) — but
(a) the scope is **not secret** (hashtag-derived public key; the `$`-private keystore is stubbed,
`TransportKeyStore.cpp:52–91`), (b) it's **global per device, off by default, not per-channel**
(`MyMesh.cpp:510` TODO), and (c) **carrying** traffic between non-adjacent DAPPS nodes still needs
**repeaters configured with the scope** — i.e. our own. There is no sender hop-TTL, only binary
zero-hop (local-only) vs full flood, and channel sends can't be zero-hopped via the companion API.

**Three deployment models — the bearer must support all three as operator config (preset + scope):**

| Model | Public-repeater carriage | Burdens public net | Needs own repeaters |
|---|---|---|---|
| A. public preset, **unscoped** | free | **yes (antisocial)** | no — only OK for featherweight traffic |
| B. public preset, **scoped** | none (dropped) | no | **yes** (scoped Repeater-firmware nodes) |
| C. **dedicated preset** (freq/SF) | n/a | no | **yes** |

Sustainable DAPPS-over-MeshCore at volume ⇒ **deploy DAPPS-aware repeaters** (scoped-on-public, or
dedicated preset). The free public ride (A) suits only trivially light traffic — another argument for
heavy compression. (A Companion `client_repeat` relays **unscoped** — no region filter — so a proper
scoped backbone needs Repeater firmware with the region configured, not Companion leaves.)

The PoC already embodies the right primitive (send = one channel broadcast; receive = promiscuous
+ filter by destination), so we are **not** cornered — these commitments are mostly about what
DAPPS *core* must not do when the bearer lands.

## Good-citizen controls for Model A (chosen first — ride the public preset)

Model A (public preset, unscoped) gets free public-repeater carriage but floods the whole same-preset
network, so it is only acceptable with **strong, self-enforced** controls. The contract:

| Control | Status | Detail |
|---|---|---|
| **Airtime governor** | **implemented** (`TxBudget`) | Hard trailing-hour budget, **back-pressure not drop**, per node. Default **30 s/hr ≈ 0.83% duty** (12× under the 10% regulatory cap) — a policy knob (`--tx-budget-sec-per-hour`). Wired into the send path; `budget-test` shows it admit 50 compressed msgs/hr then refuse the rest. |
| **Heavy compression** | **implemented** | zstd + shared dictionary ≈3× fewer packets → directly ≈3× fewer floods. The governor + compression compound: 1 pkt/msg is what makes 50 msgs/hr fit in 30 s. |
| **No flooded beacons** | design | Discovery = passive learning off real traffic only; never periodically flood the network for discovery. |
| **Bounded retransmits** | design | End-to-end idempotency on `dapps-id`; **long exponential backoff**, capped attempts — a resend is another network-wide flood. No blind per-hop resend. |
| **Size discipline** | partial (warns >4 pkts) | Cap message size on the public preset; route bulk/large transfers to Model B/C. |
| **No app-layer re-flood** | inherent | Companion leaves don't relay at the MeshCore layer; DAPPS must also never re-broadcast a received message onto the channel (forward only across *other* bearers). |
| **Observability + kill switch** | partial | Surface airtime-used / duty (the send path prints it); the existing master TX kill-switch enforced at the bearer chokepoint. |

The budget number is the key policy dial: on a mesh shared with N public users, DAPPS should take a
small, bounded slice. 30 s/hr is a conservative starting point — tune per deployment. **B and C remain
the path for higher volume / guaranteed isolation** (see the deployment-models table above).

## Device-control & firmware API (DAPPS-driven)

Radio settings must be controllable from DAPPS, not just hand-provisioned. The PoC client
already exposes the primitives; the bearer should surface a small control interface:

- `GetSelfInfo()` — identity (pubkey/name) + current radio params (read-back).
- `SetRegion(preset)` — **localisation**: push freq/bw/sf/cr for a named preset and enforce the
  region's max power. `Presets.cs` + the `regions` command are the seed; a production build
  should pull the live preset table from MeshCore upstream rather than hard-code regulatory values.
- `SetTxPower(dBm)` — capped by the active region.
- channel management — `SetChannel` / `GetChannel` / `ListChannels` (name + 16-byte PSK).
- `SetName(name)`.

These map 1:1 to companion opcodes already implemented (`SET_RADIO_PARAMS` 0x0B,
`SET_RADIO_TX_POWER` 0x0C, `SET_CHANNEL` 0x20, `GET_CHANNEL` 0x1F, `SET_ADVERT_NAME` 0x08,
`APP_START`→`SELF_INFO`). DAPPS persists these in `SystemOptions` and applies on startup / on
operator change, exactly like AGW port config today.

**Firmware flashing / upgrade** is heavier but in scope. The working flow is `esptool` driving the
ESP32-S3 over the same USB serial (download mode via CP2102 DTR/RTS — no buttons), flashing the
per-board MeshCore `*-merged.bin` at `0x0` (see `../flash-meshcore.sh`). A DAPPS-managed updater
would: detect the board, fetch the right release asset (verify sha256), **release the serial port**,
flash, verify, restart the bearer. Caveats: the port is exclusive (bearer must let go), the radio is
offline ~30–60 s, and it needs the esptool toolchain present — so this is a deliberate operator
action, not silent auto-update.

## Open questions for iteration

- Binary `SEND_CHANNEL_DATA` vs base64-text — measure goodput/airtime delta.
- Does the `"<name>: "` prefix survive across firmware versions? Pin the bearer to a release tag.
- Discovery: lean on B5 flood-then-learn over this bearer, or add a `MeshCoreDiscoveryBearer`?
