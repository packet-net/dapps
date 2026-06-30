# MeshCore bearer characterisation — transport & compression

Goal: choose the transport (text vs binary) and compression for the DAPPS-over-MeshCore bearer,
and quantify the cost in the currency that matters — **LoRa airtime** on a slow, shared, flooded
channel. Numbers below are from the no-radio harness (`meshcore-poc characterise`) plus on-air runs
between two Heltec V3s on the UK-narrow preset (869.618 MHz / BW 62.5 kHz / SF8 / CR4-8).

At this PHY a full ~165-byte packet is **≈1.6 s on air** (≈1 kbit/s) — so packet count and byte
count translate almost linearly into airtime, and airtime is the scarce, shared resource.

## 1. Transport: text (0x03) vs binary (0x3E)

| Path | base64 | name prefix | DAPPS bytes / packet | vs text |
|---|---|---|---:|---:|
| text `SEND_CHANNEL_TXT_MSG` | yes (+33%) | yes (`"<name>: "`) | 95 | 1.00× |
| **binary `SEND_CHANNEL_DATA`** | no | **none** | **151** | **1.59×** |

**On-air confirmation** of the binary path's two wins:
- *No prefix*: sent 21 raw bytes `DAPPS-NO-PREFIX-12345`, received **byte-identical** (`raw: 4441…3435`, SNR 11.8 dB). The `"<name>: "` is purely the chat/text API.
- *Verbatim binary*: a compressed (non-UTF-8) DAPPS frame round-tripped intact.

Decision: **binary**. (Gotcha handled: the binary path has no on-air timestamp, so identical frames
share a packet hash and are dropped by the mesh `hasSeen` dedup — each frame carries a 1-byte
rolling nonce so retransmits stay distinct.)

## 2. Compression of the encoded BackhaulMessage (30-message test corpus)

| Scheme | mean ratio | ≤1 packet (binary) | mean airtime / msg (binary) |
|---|---:|---:|---:|
| None | 1.00 | 93% | 1257 ms |
| Deflate (raw) | 0.85 | 93% | 1109 ms |
| Brotli (q11) | 0.97 | 97% | 1163 ms |
| Zstd (l19, no dict) | 1.00 | 93% | 1275 ms |
| **Zstd + shared dictionary** | **0.32** | **100%** | **645 ms** |

The headline: **generic compressors barely dent a ~100-byte message** (brotli/zstd even expand some).
A **shared dictionary trained on representative DAPPS traffic** collapses messages ~3× and puts
**100% of the corpus into a single packet**. DAPPS frames are highly regular (codec framing,
callsigns, common payloads), so a dictionary is the right lever.

Whole-corpus airtime: text+uncompressed 57.7 s → binary+uncompressed 37.7 s (0.65×) →
**binary + zstd-dict 18.0 s (0.31×)** — i.e. **≈3.2× the goodput** of the text path.

**On-air confirmation** (122 B payload, 168 B encoded):
| | frames | bytes on air | ~airtime |
|---|---:|---:|---:|
| uncompressed | 2 (161 B + 35 B) | 196 | ≈2.1 s |
| **zstd-dict** | **1 (49 B)** | **49** | **≈0.64 s** |

Both decoded byte-perfect on the second radio; compression took a 2-packet message to **1 packet**
over real RF (≈3.3× airtime saving for that message).

> Honesty: the dictionary was trained on synthetic-but-representative traffic, so absolute ratios are
> optimistic; real gains track how well a shipped dictionary matches real traffic. Direction is robust.
> A production bearer needs a **versioned dictionary** negotiated by id so both ends agree.

## 3. Airtime is a network-wide shared budget (not per-link)

Two multipliers make airtime even scarcer than the per-packet numbers suggest:
- **Flooding**: one channel transmission is re-flooded by every relaying repeater in scope — one send
  becomes many on-air transmissions across the network's RF cells.
- **Shared medium**: the 869.618 sub-band is **10 % duty / 500 mW**, shared by *every* originator on
  the preset. The per-packet ~1.6 s is drawn from one common budget.

So compression and parsimony don't just speed one node up — they extend the whole network's capacity.
And see the README "containment" section: on the public preset, unscoped DAPPS floods burn the whole
same-preset network's airtime — the strongest argument for both heavy compression and flood-scoping /
a dedicated preset.

## 4. Recommendation

- **Transport: binary channel-data (`0x3E`/`0x1B`)**, `data_type = 0xFFFF` (DEV), 1-byte nonce/flags
  header, Packetiser fragments ≤160 B (payload ≤165 B/packet).
- **Compression: zstd with a versioned shared dictionary**, applied to the encoded BackhaulMessage
  before fragmentation. Skip it only if it expands a given message (flag per-message — the header bit
  is already there). Generic (no-dict) compression isn't worth the CPU here.
- **Budget airtime** per channel via `AirtimeAccountant` on the *data* path (today discovery-only),
  conservatively (shared, network-wide).
- **Reliability**: end-to-end (no MeshCore ACK for channel msgs), idempotent on `dapps-id`, TTL-aware
  resends with **long backoff** (a resend is another network-wide flood).

## 5. Operational note — firmware hangs

During testing a radio's Companion firmware became unresponsive to `APP_START` until a hard reset
(esptool `--after hard_reset`, i.e. a DTR/RTS toggle). The bearer should **watchdog the serial link**
and reset the radio (DTR/RTS, no buttons) when it goes mute — which ties into the device-control API.
