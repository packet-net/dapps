# Implement DAPPS

This page is for people writing a second-source DAPPS implementation - a node, a relay, a stripped-down embedded build, or anything else that needs to interoperate with the C# reference daemon. App developers writing client apps should read [App developers](app-developers/index.md) instead; this page is about the protocol on the air between nodes.

The reference implementation in this repo is the canonical source of truth. Where this page is ambiguous, the code wins - file:line citations are given for every behaviour. The current on-air-format families are stable enough that breaking changes get version bumps (`DAPPSv1>` prompt, `Version=7` codec byte) rather than silent edits.

The page is in two parts:

- [**Bare essentials**](#bare-essentials) - the smallest set of behaviours that lets two implementations exchange a message and not deadlock. If you implement only this, you get a node that pushes messages, accepts inbound messages, and is invisible to discovery / routing optimisations.
- [**Full interoperability**](#full-interoperability) - feature by feature, what to add to that minimum to be fully indistinguishable from the reference daemon: end-to-end source tracking, multi-part fragmentation, opt-in ordering, polling, peer exchange, route gossip, discovery beacons, and the datagram codec.

## Bare essentials

Three flows make a functional node: open a session, push a message, accept a message. Everything else is optimisation.

### Session prompt

Once a transport-level connection (an AGW C-frame, an RHPv2 connect, a TCP socket) lands at your DAPPS implementation, you write the prompt:

```
DAPPSv1>\n
```

That's the literal ASCII string `DAPPSv1>` followed by a single line feed (0x0A). The connecting peer scans the inbound byte stream until it sees `DAPPSv1>` followed by *any* line terminator (`\n`, `\r`, or `\r\n`). All three are accepted because BPQ's Telnet bridge rewrites LF→CR in the apps-to-user direction; strict `\n`-only matching would hang on every BPQ-bridged connect ([DappsProtocolClient.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L39-L72)).

After the prompt, the connecting peer sends one of the verbs below. After every command finishes, the server may re-emit the prompt and loop, or close the connection. Inactivity timeout is **3 minutes** per read on both sides, matching the AX.25 T3 default ([InboundConnectionHandler.cs:36](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs#L36)). A peer that goes silent past that gets disconnected; a peer that you can't read from past that should be abandoned with a `TimeoutException`.

### Push a message: `ihave` / `send` / `data` / `ack`

Four lines and a payload. Sender writes `ihave`, receiver replies `send`, sender writes `data` plus the bytes, receiver replies `ack`.

```
S: DAPPSv1>\n
C: ihave 7e1f3a2 len=5 fmt=p s=1714982400000 dst=mail@G0RCV\n
S: send 7e1f3a2\n
C: data 7e1f3a2\nhello
S: ack 7e1f3a2\n
```

(Lines marked `S:` are server-to-client; `C:` is client-to-server. Newlines shown as `\n`; the payload after `data 7e1f3a2\n` is the raw 5 bytes `hello`, no terminator.)

A session can carry several messages. The reference daemon sends everything it has queued for a neighbour in one session, one `ihave` exchange after another, so after an `ack` a receiver should go back to reading commands rather than hang up.

Anatomy of the `ihave` line:

| Field | Required | Meaning |
|---|---|---|
| `<id>` (positional, after `ihave`) | yes | 7-character lowercase hex content hash; see [hash format](#message-id) |
| `len=<int>` | yes | Payload length in bytes (non-negative integer) |
| `fmt=<p\|d\|z1>` | yes | `p` for plain bytes, `d` for raw deflate, `z1` for zstd with the shared dictionary; see [compression](#compression) |
| `dst=<callsign>` | yes | Destination, in `app@CALL[-SSID]` form |
| `s=<int64>` | optional | Salt as decimal int64; mixed into the hash if present |
| `clen=<int>` | conditional | Compressed length; required when `fmt` is `d` or `z1`, forbidden when `fmt=p` |
| `chk=<4hex>` | optional | CRC-16/CCITT-FALSE over everything before ` chk=`; see [checksum](#checksum) |

Plus the optional features documented under [Full interoperability](#full-interoperability) (`ttl`, `src`, `mid`, `frag`, `sid`, `sn`, `gt`).

Reserved key names are validated by [IHaveValidator.cs:57](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L57). Any other `key=value` token is treated as an opaque application header and preserved through to the receiving app.

Receiver replies are one of:

| Reply | Meaning | Triggered by |
|---|---|---|
| `send <id>\n` | "Yes, send the payload" | Successful parse + accept |
| `error\n` or `error <id>\n` | "Reject the offer" | Malformed `ihave` (missing `len`/`dst`, a `fmt` you can't decode, broken `chk`, etc.) |
| `bad <id>\n` | "Payload arrived but was no good" | Sent only after `data`: a compressed payload that doesn't decode to `len` bytes, or `SHA1(salt_le ++ payload)[:7] ≠ id` |
| `ack <id>\n` | "Got it, hash matches" | After `data` succeeds |
| `eh?\n` | "Unrecognised command" | Verb wasn't `ihave`/`data`/`peers`/`rev`/`quit`/`help` |

### Accept a message

Implement the receiver mirror:

1. After writing `DAPPSv1>\n`, read a line.
2. If it starts with `ihave `, parse it. If valid, write `send <id>\n` and persist the offer's metadata. If invalid, write `error <id>\n` (or `error\n` if the id couldn't be plucked out) and go back to reading commands: a sender whose compressed offer you refused offers the same message again plain.
3. Read the next line. It must be `data <id>\n` matching the id you just `send`'d. Otherwise, close.
4. Read exactly `len` bytes from the stream (no framing - just `len` raw bytes), or `clen` bytes for a compressed format, and decompress those to `len` bytes (`len` is always the *uncompressed* length, `clen` the on-wire byte count).
5. Compute `SHA1(salt_le_8_bytes ++ payload)[:7]`. If it matches `<id>`, write `ack <id>\n`. Otherwise, write `bad <id>\n`.

The receiver MAY then loop and emit `DAPPSv1>\n` again to await another command on the same session, or close.

### Message id {#message-id}

```
id = sha1( salt_le_8_bytes ++ payload )[:7]
```

Where:
- `salt_le_8_bytes` is the 64-bit salt rendered little-endian into 8 bytes. If the `ihave` carries `s=N`, it's `N`. If there's no `s=`, the salt prefix is omitted entirely (hash is just `SHA1(payload)`).
- The output is taken as the first **7 characters** of the SHA1 lowercase hex digest. SHA1 is 40 hex chars; the first 7 give ~28 bits of identifier space (2^28 ≈ 268M ids).

Reference: [DappsMessage.cs:28](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsMessage.cs#L28).

The salt convention in the reference daemon is "milliseconds since the Unix epoch" so two submissions of the same payload milliseconds apart get distinct ids - but the wire format is just an int64; any value is legal. An implementation that always emits salt 0 will collide on identical payloads, which is its problem to solve via deduplication.

### Checksum {#checksum}

Implementations SHOULD include `chk=NNNN` as the final KV on every `ihave` line and SHOULD validate it on receipt. It catches single-bit corruption that AX.25 framing missed.

```
chk_value = crc16_ccitt_false( bytes_of_line_up_to_and_excluding_" chk=" )
```

CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF, no reflection, no final XOR. Rendered as 4 lowercase hex digits ([Crc16CcittFalse.cs:21](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Crc16CcittFalse.cs#L21)). The covered region is "everything before the literal ` chk=`": including `ihave`, the id, every other KV, and the spaces between them, but not the trailing ` chk=NNNN` itself.

Validation is positional too: `chk` MUST be the last KV. The validator rejects any line where `chk=` appears earlier or where `chk=NNNN` isn't followed by end-of-line ([IHaveValidator.cs:208-228](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L208-L228)). This makes the covered range computable from a single string scan, not from a re-serialisation of the parsed KVs.

### That's the bare essentials

A node that does only:

- Sessions: `DAPPSv1>` + 3-minute inactivity timeout + line-based commands.
- Push: `ihave` (with required fields and `chk`) + `send` reply + `data` + `ack`.
- Accept: the mirror above, with hash validation.
- Hash: SHA1 + 7-char prefix as specified.

…interoperates with the reference daemon for one-shot message delivery in both directions. It won't show up in peer-discovery responses, won't accept polling, won't be reached by relays, and won't see fragmented or ordered streams. But messages flow.

## Full interoperability

The features below are individually optional. The reference daemon implements all of them; pick the ones your scope needs. Each one is wire-additive: a daemon that doesn't understand `src=` will just ignore it, and the message still delivers.

### End-to-end source tracking (`src=`)

Add to `ihave`:

```
ihave 7e1f3a2 len=5 fmt=p s=1714982400000 src=G0ORIG dst=mail@G0RCV chk=a31f
```

`src=<callsign>` is the *originator* of the message - the callsign of the node whose app submitted it. Distinct from the *link source* (the immediate sender, derived from the bearer's session metadata). On a multi-hop relay path, every forwarder preserves `src=` verbatim; only the originator stamps it.

Why have it: without `src=`, a receiver three hops down can't tell whether a message originated at G0FIRST or just transited through G0FIRST. With `src=`, the receiver's app sees the originator (exposed as the `dapps-origin` MQTT user property) and can route replies back to the right source. Forwarders that don't propagate it omit `src=`; receivers treat absent `src=` as "originator unknown".

Reference: [DappsProtocolClient.cs:122-128](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L122-L128), [IHaveValidator.cs:144-152](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L144-L152).

### Multi-part fragmentation (`mid=` + `frag=`)

A payload that exceeds the operator's fragment threshold (default 4 KB) is split into N chunks at the originator, sent as N independent `ihave` exchanges, and reassembled at the destination.

```
ihave 11aabbc len=1024 fmt=p s=1714982400001 mid=4cf02b1 frag=1/3 dst=mail@G0RCV chk=...
ihave 22ccddd len=1024 fmt=p s=1714982400002 mid=4cf02b1 frag=2/3 dst=mail@G0RCV chk=...
ihave 33eeefe len=512  fmt=p s=1714982400003 mid=4cf02b1 frag=3/3 dst=mail@G0RCV chk=...
```

- `mid=<7hex>` is a master id - opaque grouping key, same hex format as a regular id.
- `frag=N/M` where N is the 1-based index, M is the total. M ≥ 2 (single-fragment messages omit `mid`/`frag` entirely). N ∈ [1, M].
- `mid` and `frag` MUST both be present or both absent. A partial set is rejected as malformed ([IHaveValidator.cs:160-165](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L160-L165)).
- Each fragment has its own id (hash of its own chunk + its own salt). Intermediate hops forward fragments as opaque messages.
- Only the final destination groups by `mid`, holds fragments in a reassembly buffer, and delivers the assembled payload to the app once all M arrive.

Why two-id'd: each fragment is independently content-addressed so it can be deduplicated, retried, and routed like any other message. The master id only matters at the destination; relays don't care. A receiver that doesn't know `mid`/`frag` will deliver each fragment to the app as a separate message, which is wrong but not corrupt.

Reassembly buffer entries time out after `FragmentReassemblyTimeoutSeconds` (default 7 days) - long because HF / mesh propagation gaps legitimately last days, and we'd rather hold the partial bytes than throw away most of a near-complete message.

Reference: [DappsProtocolClient.cs:131-134](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L131-L134), [IHaveValidator.cs:154-196](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L154-L196), [DatabaseAndMqttInbox.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/DatabaseAndMqttInbox.cs) (reassembly).

### Opt-in ordering (`sid=`, `sn=`, `gt=`)

The default is unordered delivery (each message independent). Apps that need monotonic order (chat, telemetry, change-log streams) opt in by tagging messages with a stream id; the originating daemon mints a monotonic seq, and the destination daemon delivers in seq order.

```
ihave d2e7f0a len=42 fmt=p s=1714982401000 src=G0ORIG sid=chat sn=1 gt=600 dst=chat@G0RCV chk=...
ihave e3f8a1b len=42 fmt=p s=1714982401500 src=G0ORIG sid=chat sn=2 gt=600 dst=chat@G0RCV chk=...
```

- `sid=<string>`: stream id. UTF-8, no spaces or `=`, max 255 bytes. Sender-scoped: two senders can pick the same id without collision (the receiver keys its cursor on `(originator, sid)`).
- `sn=<uint32>`: sequence number, monotonic per `(sender, sid)`.
- `gt=<uint32>`: gap timeout in seconds. `0` = strict (stall forever waiting for the missing prior); `>0` = skip the gap after that many seconds.

All three travel together or all three are absent. The originator stamps them; intermediate hops re-emit verbatim; the destination's reorder buffer holds messages with `sn > expected` until the gap fills (or, in timeout mode, until the deadline elapses).

Why opt-in: ordering trades latency for predictability. One missing message stalls the whole stream until it arrives or the timeout fires; on lossy radio links that's real cost. Most apps don't need it. The ones that do, get to choose `gt=0` (would rather wait than skip) or `gt=N` (would rather skip than block forever).

Receivers that don't understand `sid`/`sn`/`gt` ignore the keys and deliver each message immediately - the stream survives the per-pair conversation between aware nodes.

Reference: [DappsProtocolClient.cs:138-152](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L138-L152), [IHaveValidator.cs:198-227](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/IHaveValidator.cs#L198-L227), full design in [reference.md "Message ordering"](app-developers/reference.md#message-ordering-opt-in).

### Compression (`fmt=z1`) {#compression}

A payload can travel zstd-compressed with a shared dictionary. `fmt=z1` means zstd with dictionary version 1, and `clen=` is the byte count on the wire:

```
C: ihave 3f9a0c1 len=197 fmt=z1 clen=67 s=1790410266123 dst=wps-repl@G5ALF-3\n
S: send 3f9a0c1\n
C: data 3f9a0c1\n<67 bytes of zstd>
S: ack 3f9a0c1\n
```

`len=` stays the original length and the id is still the hash of the original payload, so compression never changes a message's identity. The payload is one zstd frame that records its decompressed size (zstd does this by default when it compresses a buffer in one go); receivers refuse to decode past `len=`.

The dictionary is a fixed file shipped with DAPPS ([payload-v1.dict](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Compression/payload-v1.dict)), loaded as a zstd raw-content dictionary. It holds typical traffic (WPS replication JSON, chat, telemetry), which is what lets a 200-byte WPS post shrink to about a third of its size, where zstd alone barely manages a quarter off.

The reference daemon compresses only when that saves at least 32 bytes, counting the `clen=` field, so short messages stay readable on a monitor. It's on by default; `DAPPS_COMPRESSION_ENABLED` and a per-neighbour setting turn it off. Receivers always accept it.

New dictionaries get new versions (`z2` and so on), and a shipped version never changes. A receiver that doesn't hold the version it's offered replies `error <id>` (or `no <id>` during a `rev` drain), and the sender offers the same message again with `fmt=p` on the same session. The same happens after a `bad` for a compressed payload, which usually means something on the path isn't passing binary data through. Either way the rest of that session goes plain.

### TTL (`ttl=`)

```
ihave 7e1f3a2 len=5 fmt=p s=1714982400000 ttl=600 dst=mail@G0RCV chk=...
```

`ttl=<seconds>` is *residual lifetime*, not a hop count. The originator sets it; each forwarder recomputes the remaining time as `original_ttl - queue_dwell_seconds` and re-emits with the lower number. A message whose residual goes ≤ 0 gets dropped before being offered ([TtlMath.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/TtlMath.cs)).

Why wall-clock not hop-count: hop-count gives no useful guarantee on packet radio because retries and queue dwell dwarf the hop-count cost. A message with "TTL 5 hops" can sit in a queue for a week. A message with "TTL 600 seconds" tells every forwarder "stop trying after 10 minutes, regardless of how many hops we managed".

Absent `ttl=` means "no expiry" - the message lives in the queue until forwarded, manually deleted, or the operator's cleanup policy kicks in. Apps that care about cleanup should always set a value.

Positive integers only; `ttl=0` is rejected. Forwarders MUST decrement, not pass through unchanged - a compliant node implements `TtlMath.Residual` semantics or the system can't put a bound on stale traffic.

### Custom headers

Any `key=value` token on the `ihave` line whose key isn't in the reserved set (`len`, `fmt`, `s`, `clen`, `dst`, `chk`, `ttl`, `src`, `mid`, `frag`, `sid`, `sn`, `gt`) is preserved verbatim:

```
ihave 7e1f3a2 len=5 fmt=p s=1 priority=high contentType=text/plain dst=mail@G0RCV chk=...
```

The reference daemon stores them as a JSON dict on the receiving message and surfaces them to the app via the existing app interface. They're forward-compatible: future protocol versions can promote custom headers to reserved keys without breaking older senders, because old senders couldn't have collided with the new reserved name (they would have been emitting it as a custom header all along).

Implementations that don't care about app-level headers can drop them on receive without breaking anything.

### `peers` exchange

```
C: peers\n
S: peer M0LTE-9 source=n port=0\n
S: peer GB7RDG source=d\n
S: peer G7VVK-9 source=n port=1\n
S: end\n
```

The connecting peer asks "who do you forward to?"; the server emits one `peer` line per known peer, then `end\n`.

Per-line format:

```
peer <callsign> source=<n|d> [port=<byte>]
```

- `source=n`: a configured neighbour (`DbNeighbour`).
- `source=d`: a beacon-discovered peer (`DbDiscoveredPeer`).
- `port=<0-255>`: optional AGW bearer port. Omitted for UDP-discovered peers (no meaningful port-as-byte concept there).

Unknown lines between `peer` and `end` are silently skipped on both sides - lets a future server add fields without breaking older clients. The alias `who\n` is accepted in place of `peers\n` because that's the verb a sysop already types at a node prompt.

Implementations that don't care about transitive discovery can skip both sides: ignore the `peers` command (return `eh?` if it surfaces) and never call it. Discovery still works via beacons, just slower-to-converge.

Reference: [InboundConnectionHandler.cs:229-260](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs#L229-L260), [DappsProtocolClient.cs:201-244](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L201-L244).

### `rev` reverse forwarding

Polling. The connecting peer asks the server "got mail for me?"; the server pushes any queued messages whose final destination matches the caller's callsign, then re-emits the prompt to signal "drained".

```
C: rev\n
S: ihave 9aa1234 len=128 fmt=p s=1714982399000 dst=mail@G0CALLER chk=...\n
C: send 9aa1234\n
S: data 9aa1234\n[128 bytes]
C: ack 9aa1234\n
S: ihave bbb5678 len=64 fmt=p s=1714982399500 dst=alerts@G0CALLER chk=...\n
C: send bbb5678\n
S: data bbb5678\n[64 bytes]
C: ack bbb5678\n
S: DAPPSv1>\n
```

Note the role flip: during a `rev` drain, the *server* is the message sender and the *client* is the receiver. The same `ihave`/`send`/`data`/`ack` exchange runs in reverse.

Selective form: `rev <id1> <id2> ...\n` drains only the listed ids. Bare `rev\n` drains everything matching the caller's base callsign.

The `DAPPSv1>\n` re-prompt is the "drained" marker - distinct from another `ihave` line because it has a `>` and no spaces. The connecting peer reads lines until it sees the prompt, then either issues another command or closes.

Why `rev` exists: a node behind asymmetric connectivity (RF-only inbound, can't initiate sessions to the wider network) can call out, push its outbound, and pull its inbound on the same session. The reference daemon also runs `rev` opportunistically once it has pushed what it had queued - the connection is open and the acks just landed; might as well drain. If more mail for that neighbour was queued meanwhile, it pushes that too and sends `rev` again before hanging up, so `rev` is always the last exchange of the session.

A receiver that doesn't implement `rev` should respond `eh?\n` to the command. Senders should treat `eh?` as "this peer doesn't poll" and stop trying.

Reference: [InboundConnectionHandler.cs:275-343](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs#L275-L343), [DappsProtocolClient.cs:283-369](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs#L283-L369).

### `routes` exchange

```
C: routes\n
S: route M0LTE-9 hops=1\n
S: route GB7RDG hops=2 ageSeconds=300\n
S: route G7VVK hops=2 ageSeconds=900\n
S: end\n
```

The connecting peer asks "what destinations can you reach?"; the server emits one `route` line per known-good destination, then `end\n`.

Per-line format:

```
route <destBaseCallsign> [hops=<int>] [ageSeconds=<int>]
```

- `destBaseCallsign`: the destination's base callsign (no SSID).
- `hops` (optional): a hint for the receiver's cost calculation. The reference daemon emits `1` for direct neighbours, `2` for traffic-learned routes (via one intermediate). Receivers that don't care about hops ignore it.
- `ageSeconds` (optional): how long ago the responder last saw evidence the route works. Helps the receiver decide whether to trust it.

Receivers import each row as a learned route via the responding peer, marked as gossip-sourced. Failures invalidate via the same per-row failure counter the daemon already maintains for traffic-learned routes; gossip-sourced rows don't get re-exported (only direct observation gets advertised, to avoid distance-vector loops).

The reference daemon's emitter filters: only routes whose failure counter is zero, and only routes the daemon itself has actually used (not just heard about). Manual neighbours are always advertised; traffic-learned routes are advertised only when proven; gossip-imported routes are never advertised (don't re-export hearsay).

Implementations that don't care about route gossip should respond `eh?\n` to the command. Senders treat `eh?` as "this peer doesn't gossip" and stop trying.

Reference: [InboundConnectionHandler.HandleRoutes](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs), [DappsProtocolClient.RequestRoutesAsync](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs).

### Quit / help

```
C: quit\n      (or q, bye, exit; case-insensitive)
S: bye\n       (then closes)

C: help\n      (or info; case-insensitive)
S: This is DAPPS. See https://github.com/packet-net/dapps/blob/master/README.md for details.\n
```

Help text is human-only; programs shouldn't parse it. Both commands loop the prompt afterwards (except `quit`, which closes).

### Datagram bearer (binary codec)

For bearers that don't carry a stream-shaped session - UDP today, MeshCore Companion / KISS in the future - the `ihave`/`data` text exchange is replaced with a self-describing binary frame. One `BackhaulMessage` = one packet; no session, no acks, no prompt. The receiver consumes the bytes and either delivers or doesn't.

Frame layout, all integers little-endian. Current `Version = 7`:

```
[1]   version            = 7 (decoder hard-fails on mismatch)
[2]   flags (UInt16)
        bit 0  HasSalt
        bit 1  HasTtl
        bit 2  HasHeaders
        bit 3  HasOriginator
        bit 4  HasLinkSource
        bit 5  HasFloodHopsRemaining
        bit 6  HasSourceRoute
        bit 7  HasTraversedHops
        bit 8  HasFragment        (mid + frag-index + frag-total)
        bit 9  HasStream          (sid + sn + gt)
[7]   id (ASCII, 7-char hex)

if HasSalt:
  [8]   salt (Int64 LE)

if HasTtl:
  [4]   ttl seconds (Int32 LE)

[2]   destination length (UInt16 LE)
[N]   destination (UTF-8)

if HasOriginator:
  [2]   originator length (UInt16 LE)
  [O]   originator (UTF-8)

if HasLinkSource:
  [2]   link-source length (UInt16 LE)
  [L]   link-source (UTF-8)

if HasFloodHopsRemaining:
  [1]   flood hops (UInt8)

if HasSourceRoute:
  [1]   hop count (UInt8, max 255)
  per hop:
    [1] hop length (UInt8, max 255)
    [N] hop callsign (UTF-8)

if HasTraversedHops:
  [1]   hop count (UInt8)
  per hop:
    [1] hop length (UInt8)
    [N] hop callsign (UTF-8)

if HasFragment:
  [7]   master id (ASCII, 7-char hex)
  [2]   fragment index (UInt16 LE, 1-based)
  [2]   fragment total (UInt16 LE)

if HasStream:
  [1]   stream id length (UInt8, max 255)
  [S]   stream id (UTF-8)
  [4]   stream seq (UInt32 LE)
  [4]   stream gap timeout (UInt32 LE seconds, 0 = strict)

if HasHeaders:
  [2]   header count (UInt16 LE)
  per header:
    [2] key length (UInt16 LE)
    [K] key (UTF-8)
    [2] value length (UInt16 LE)
    [V] value (UTF-8)

[4]   payload length (UInt32 LE)
[P]   payload bytes
```

Version is the first byte on the wire; receivers reject anything other than the current version with a hard error rather than guessing. There are no historical fallback decoders - the project pre-shipping means a peer running an old version is out of date, not in the field forever.

Reference: [BackhaulMessageCodec.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Backhaul/Datagram/BackhaulMessageCodec.cs).

The `LinkSource`, `FloodHopsRemaining`, `SourceRoute`, and `TraversedHops` fields aren't carried on the text protocol - they only matter for bearers that don't natively identify the immediate sender (UDP) or for routing algorithms that ride additional metadata on the envelope (flood / MeshCore-style discovery). A datagram-bearer implementation that stamps `LinkSource` from its socket-level peer info, and decodes / re-encodes the routing metadata transparently, is the minimum.

### Discovery beacons

Optional. When implemented, lets nodes find each other without manual neighbour lists.

A beacon is a single ASCII line, sent on a discovery channel (an AX.25 UI frame on a known frequency, a UDP multicast group, etc.) on a slow cadence (default ~30 minutes per channel):

```
DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300
```

- `DAPPS v1` is the literal magic + version prefix. Required.
- `callsign=<call>` is the originator. Required, non-empty.
- `hops=<int>` is the number of intermediate hops the beacon has been forwarded over. 0 = direct. Required, ≥ 0.
- `ttl=<int>` is how long (seconds) a receiver should treat this peer as fresh. Required, > 0.

No trailing newline. ASCII-only. Unknown KVs are ignored - forward-compat. Reference: [BeaconCodec.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Discovery/BeaconCodec.cs).

The receiver records the beacon as a discovered peer, stamped with the bearer it arrived on (so the routing layer knows how to reach back). The bearer hint is **not** carried in the wire form - it's whatever the receive bearer says it is. Including it on the wire would let a misbehaving peer claim to be reachable on routes it isn't.

Solicits run alongside, on the same channels:

```
DAPPS v1 solicit callsign=M0LTE-9
```

A solicit asks "everyone within earshot, please beacon now (with a small random jitter so we don't all collide)". The reference daemon answers with a normal beacon emission delayed by [0, 5] seconds. Unknown senders that haven't beacon'd yet show up promptly without waiting for the next scheduled beacon. Reference: [SolicitCodec.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Discovery/SolicitCodec.cs).

An implementation can skip beacons entirely and rely on configured neighbours. It can also implement beacons but skip solicits (you'll just converge on the beacon cadence rather than on demand).

## Compliance checklist

If you've implemented the bare essentials and want to verify against the reference daemon, the smallest useful smoke test:

1. **Connect outbound to the reference daemon, push a message.** Reference accepts on `DAPPSv1>` prompt; you write `ihave …`, expect `send <id>`, write `data … hello`, expect `ack <id>`. Inspect the daemon's `/Recent` page or its MQTT topic to confirm receipt.
2. **Receive inbound from the reference daemon.** Configure the reference daemon to forward to your callsign; queue a message; expect a session in, the same four-line exchange in reverse, and your `ack` to clear the daemon's queue.
3. **Round-trip with `chk`.** Include `chk=NNNN` on outbound; verify the reference daemon validates it (deliberately corrupt one byte and watch the reference reject with `error <id>`).
4. **Round-trip with `ttl`.** Include `ttl=600`; inspect the daemon's stored row to confirm it persisted, then forward via the daemon to a third node and watch the residual decrement.
5. **Reject malformed offers.** Send `ihave x len=oops fmt=p dst=mail@G0X` (bad len) and verify your implementation responds `error x` not `send x`.

Beyond that, each optional feature has its own equivalence test: send/receive with the field set, confirm the reference daemon round-trips the value (visible on `/Recent`, on MQTT user properties, or in the SQLite database directly).

## See also

- [App developers - reference](app-developers/reference.md) - higher-level wire summary, app-interface mappings (MQTT topics, REST endpoints), and worked examples for app authors using DAPPS as a service.
- [Discovery & routing](discovery-and-routing.md) - how the reference daemon turns discovered peers into route decisions; orthogonal to the wire protocol but informs why `peers`/beacons exist.
- The reference implementation source: [`src/dapps/dapps.client/`](https://github.com/packet-net/dapps/tree/master/src/dapps/dapps.client) (wire-level codecs, bearer-neutral) and [`src/dapps/dapps.core/Services/`](https://github.com/packet-net/dapps/tree/master/src/dapps/dapps.core/Services) (session handling, inbox/outbox, discovery).
