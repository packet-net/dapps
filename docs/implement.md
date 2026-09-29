# Implement DAPPS

This page is for people writing a second-source DAPPS implementation - a node, a relay, a stripped-down embedded build, or anything else that needs to interoperate with the C# reference daemon. App developers writing client apps should read [App developers](app-developers/index.md) instead; this page is about the protocol on the air between nodes.

The reference implementation in this repo is the canonical source of truth. Where this page is ambiguous, the code wins - file:line citations are given for every behaviour. The current on-air-format families are stable enough that breaking changes get version bumps (`DAPPSv1>` prompt, `Version=7` codec byte) rather than silent edits.

The page is in two parts:

- [**Bare essentials**](#bare-essentials) - the smallest set of behaviours that lets two implementations exchange a message and not deadlock. If you implement only this, you get a node that sends messages, takes messages, and is invisible to discovery / routing optimisations.
- [**Full interoperability**](#full-interoperability) - feature by feature, what to add to that minimum to be fully indistinguishable from the reference daemon: the full exchange between DAPPS nodes, end-to-end source tracking, multi-part fragmentation, opt-in ordering, peer exchange, route gossip, discovery beacons, and the datagram codec.

## Bare essentials

Three flows make a functional node: open a session, send a message, take a message. Everything else is optimisation.

### Session start

Once a transport-level connection (an AGW C-frame, an RHPv2 connect, a TCP socket) lands at your DAPPS implementation, you write the prompt and, straight after it, your rules:

```
DAPPSv1>\n
exchange id=4f2a9c hold=0 inline=0\n
```

The prompt is the literal ASCII string `DAPPSv1>` followed by a single line feed (0x0A). The connecting peer scans the inbound byte stream until it sees `DAPPSv1>` followed by *any* line terminator (`\n`, `\r`, or `\r\n`). All three are accepted because BPQ's Telnet bridge rewrites LF→CR in the apps-to-user direction; strict `\n`-only matching would hang on every BPQ-bridged connect.

The `exchange` line tells another DAPPS node what you take from it; [the exchange](#the-exchange) has the details. `id=` is any short random tag. The line above is the smallest useful one: no hold, and every message offered to you with `ihave` before its payload is sent. A DAPPS node that dials you won't send you anything until it has this line, so don't leave it out.

After that the connecting peer sends commands, below, or if it's a DAPPS node its own `exchange` line. Inactivity timeout is **3 minutes** per read on both sides, matching the AX.25 T3 default. A peer that goes silent past that gets disconnected; a peer that you can't read from past that should be abandoned.

### Send a message: `ihave` / `send` / `data` / `ack`

The simple way to send to a DAPPS node, one message at a time. Four lines and a payload: sender writes `ihave`, receiver replies `send`, sender writes `data` plus the bytes, receiver replies `ack`.

```
S: DAPPSv1>\n
S: exchange id=4f2a9c hold=120 inline=256 z=1\n
C: ihave 7e1f3a2 len=5 fmt=p s=1714982400000 dst=mail@G0RCV\n
S: send 7e1f3a2\n
C: data 7e1f3a2\nhello
S: ack 7e1f3a2\n
```

(Lines marked `S:` are server-to-client; `C:` is client-to-server. Newlines shown as `\n`; the payload after `data 7e1f3a2\n` is the raw 5 bytes `hello`, no terminator.)

A sender that only sends this way can ignore the `exchange` line after the prompt. A session can carry several messages, one after another; after an `ack` the receiver goes back to reading commands.

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

Reserved key names are validated by [IHaveValidator.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs). Any other `key=value` token is treated as an opaque application header and preserved through to the receiving app.

Receiver replies are one of:

| Reply | Meaning | Triggered by |
|---|---|---|
| `send <id>\n` | "Yes, send the payload" | Successful parse + accept |
| `ack <id>\n` | "Already got it" | An offer for a message you already have (same id, `s=` and `len`): the sender counts it delivered and doesn't send the payload |
| `no <id> [reason]\n` | "Won't take it" | Bigger than your `max=`, or refused for a reason of your own. The sender doesn't offer it to you again |
| `error <id>\n` or `error ??\n` | "Reject the offer" | Malformed `ihave` (missing `len`/`dst`, a `fmt` you can't decode, broken `chk`, etc.) |
| `bad <id>\n` | "Payload arrived but was no good" | Sent only after `data`: a compressed payload that doesn't decode to `len` bytes, or `SHA1(salt_le ++ payload)[:7] ≠ id` |
| `ack <id>\n` | "Got it, hash matches" | After `data` succeeds |
| `eh?\n` | "Unrecognised command" | Before an exchange, a verb that isn't `ihave`/`data`/`msg`/`exchange`/`peers`/`routes`/`quit`/`help`. The session then ends |

### Take a message

Implement the receiver mirror:

1. After writing the prompt and your `exchange` line, read a line.
2. If it starts with `ihave `, parse it. If it's a message you already have, write `ack <id>\n` and go back to reading. If valid, write `send <id>\n` and remember the offer for this session. If invalid, write `error <id>\n` (or `error ??\n` if the id couldn't be plucked out) and go back to reading: a sender whose compressed offer you refused offers the same message again plain.
3. When `data <id>\n` arrives for an offer you accepted, read exactly `len` bytes from the stream (no framing - just `len` raw bytes), or `clen` bytes for a compressed format, and decompress those to `len` bytes (`len` is always the *uncompressed* length, `clen` the on-wire byte count). A `data` line for an offer you didn't accept can't be read past: close the session.
4. Compute `SHA1(salt_le_8_bytes ++ payload)[:7]`. If it matches `<id>`, write `ack <id>\n`. Otherwise, write `bad <id>\n`.

When a DAPPS node dials *you* to hand over traffic, it waits for your prompt and `exchange` line, sends its own `exchange` line, and then offers each message with `ihave` (with `inline=0` it never sends one unasked). Take its `exchange` line quietly: you've already sent yours. Answer `quit` with `bye` and hang up. That's all a minimal node needs to receive from the reference daemon.

### Never deliver a message twice

A sender offers a message again whenever it didn't see your `ack`: it restarted mid-transfer, or the link dropped after you'd taken the payload. A second neighbour can also pass on a copy by another path. So a receiver MUST remember the messages it has accepted, whether for a local app or to pass on, and neither deliver nor forward one twice:

- Identify a message by its id together with its salt (`s=`) and `len`. The id alone is 28 bits of hash, and a node remembering weeks of traffic would sometimes mistake a new message for an old one and drop it. A message without `s=` can't be told apart from a later one with the same content, so don't remember it; senders SHOULD always include `s=`.
- Remember it until it would have expired anyway: its `ttl=` at receipt, plus some slack. The reference daemon adds an hour, and remembers for at most 30 days (`DAPPS_RECEIVED_MEMORY_SECONDS`), which is also how long it keeps a message with no `ttl=`. Forgetting early can only cost a repeat, never a message.
- Answer an `ihave` for one with `ack <id>`, so the payload doesn't cross the air again. If one arrives anyway (as a `msg`, or sent before you'd finished storing the first), `ack` it and discard it.
- Never let the memory lose a message. Record the message as "being stored" before storing it, and mark it stored once you have; a record left "being stored" (the node died in between) doesn't count, so the sender's retry is taken. If storing fails, drop the record. And if a second copy arrives while the first is still being stored, wait for the first: if it was stored the second is a repeat, if not the second gets its turn. A crash can then cost a repeat, never a loss.
- A repeat of a message for another node, arriving from a different neighbour from the first copy, usually means a routing loop: the message was passed on and came back. It is still dropped, but it's worth logging.

A sender that gets `ack <id>` in reply to `ihave` treats the message as delivered.

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

Validation is positional too: `chk` MUST be the last KV. The validator rejects any line where `chk=` appears earlier or where `chk=NNNN` isn't followed by end-of-line ([IHaveValidator.ValidateChecksum](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs)). This makes the covered range computable from a single string scan, not from a re-serialisation of the parsed KVs.

### That's the bare essentials

A node that does only:

- Sessions: `DAPPSv1>` and an `exchange` line with `inline=0`, a 3-minute inactivity timeout, line-based commands.
- Send: `ihave` (with required fields and `chk`) + `send` reply + `data` + `ack`.
- Take: the mirror above, with hash validation, and a caller's `exchange` and `quit` answered.
- Hash: SHA1 + 7-char prefix as specified.

…interoperates with the reference daemon for message delivery in both directions. It won't show up in peer-discovery responses, won't be reached by relays, and won't see fragmented or ordered streams. But messages flow.

## Full interoperability

The features below are individually optional. The reference daemon implements all of them; pick the ones your scope needs. Each one is wire-additive: a daemon that doesn't understand `src=` will just ignore it, and the message still delivers.

### The exchange {#the-exchange}

This is how DAPPS nodes talk to each other. Every time a station transmits, it says everything it has to say, in both directions, and the session is the same at both ends, so who dialled stops mattering. On a packet link each transmission costs a fixed TX delay and preamble, and BPQ answers every burst with an RR, so fewer, fuller transmissions are what make a link fast.

**Session start.** A node never sends message contents to a peer before it has that peer's rules.

1. The answering node sends `DAPPSv1>\n` and straight after it its own `exchange` line.
2. Every node sends its own `exchange` line once per session: the answering node right after the prompt; the node that dialled as soon as it sees the prompt, the peer's `exchange`, or any exchange traffic (`msg`, `ihave`, `data`, or an answer), or after 10 seconds with none of those. The last two cases are the link changing under the peer:
   - A crossed call: both nodes dialled each other at once, and the node joined both calls into one link with neither handed an incoming connect, so neither sends a prompt. After 10 seconds of silence both send their `exchange` and carry on as normal. A node that can tell no prompt is coming sends its `exchange` at once: the reference daemon does when it sees the peer's SABM in its node's monitor while its own call is on the way, and when its call went over a link that was already up (see the note on BPQ below).
   - Exchange traffic before any prompt: the node moved a link that was mid-exchange onto this new session, so the peer is still talking to the old one. Our `exchange` has a new tag, which has the peer send its own and everything unanswered again (rule 5). Only clearly DAPPS lines count: an `exchange`, a valid `msg`, `ihave` or `data`, or an answer with a message id. Anything else is whatever answered the call, and still fails for want of a prompt.

   If the caller's `exchange` gets nothing back but other text for 10 seconds, it isn't talking to a DAPPS node (a node's command prompt, say): it hangs up.

   (After a connect script, the script has read the prompt.) The answering node takes answers (`ack`, `send`, `no`, `bad`, `error`) that arrive before the caller's `exchange` too, for the same reason: it doesn't answer them `eh?`.
3. When it wants the peer's routes, the node that dialled asks with `routes` before sending its `exchange`, so the answer can't get mixed up with traffic.
4. Once a node has sent its `exchange` and received the peer's, either side sends whatever it has, whenever it has it, following the other's rules. A `DAPPSv1>` line from then on is ignored.
5. Session tags. Each session picks a random tag and sends it as `id=` in its `exchange`. If the peer sends an `exchange` with a tag you haven't seen in this session, its end of the link is a new session (its node moved the link to a newer session): send your `exchange` again, with your same tag, and send again everything of yours it hasn't answered. The peer's duplicate memory makes that safe. An `exchange` repeating a tag you've seen is just the peer's rules again: take them and send nothing.

**The rules.** `exchange` carries the sending node's rules for what it takes:

| Key | Meaning | Left out |
|---|---|---|
| `id=<tag>` | The session tag (rule 5) | an empty tag |
| `hold=<s>` | Seconds of quiet to keep the link up for. The session holds for the lower of the two | 0 |
| `inline=<bytes>` | Largest payload, as it goes on air, it takes unasked as `msg`. Bigger ones are offered with `ihave` first. The reference daemon says 256 | 0: offer everything first |
| `max=<bytes>` | Largest message (`len`) it takes at all. Bigger ones are neither sent nor offered to it | no limit |
| `z=<list>` | Compression dictionaries it holds, e.g. `z=1` | plain only |

Unknown keys are ignored. `inline` binds the sender: a receiver takes a `msg` over its own limit that arrives anyway, since its payload has already crossed the air. A node can still refuse any message with `no`: an offer before its payload goes on air, or a small unasked one after it arrives (not stored or passed on). A node that wants to judge every message before any payload is sent says `inline=0`.

**Lines.** Either side, any time, once both `exchange` lines have crossed. Header fields are exactly the `ihave` fields above.

| Line | Meaning | Answer |
|---|---|---|
| `msg <id> <fields>\n` then the payload (`clen` bytes if compressed, else `len`) | A message with its payload | `ack`, `bad`, `no`, or `error` |
| `ihave <id> <fields>\n` | Offer without payload | `send`, `ack` (already have it), `no`, or `error` |
| `send <id>\n` | Send your offer's payload | `data <id>\n` then the payload |
| `data <id>\n` then the payload | Payload of an offer we asked for | `ack` or `bad` |
| `ack <id>\n` | Got it, or already had it: delivered | none |
| `bad <id>\n` | Payload didn't decode or hash-check | the sender may send it once more plain |
| `error <id>\n` | Header refused (malformed, unknown `fmt`) | the sender may send it once more plain; if that fails too, it fails for this session |
| `no <id> [reason]\n` | Won't take it. Not stored or passed on | none; the sender doesn't offer it on this link again |
| `quit\n` | Ending | `bye\n`, then hang up |

- A `msg` or `data` whose length can't be made out (no `len`, or `clen` missing for a compressed format, or a `data` for an offer you didn't accept) leaves the stream out of step: close the session. The reference daemon does the same for a payload said to be over 16 MB, rather than wait for it. If the lengths parse but anything else is refused, read past the payload, then answer.
- Window: at most 8 messages sent and not yet answered, in each direction.
- Order: each side sends in its queue order and handles what arrives in order. Ordered streams (`sid`/`sn`) are put in order at the destination.
- Writes: write whatever is ready as soon as it is ready. Everything answered from one burst goes in one write, and so in one frame where it fits.
- Duplicate memory ([above](#never-deliver-a-message-twice)): an `ihave` for a message you have is answered `ack`; a `msg` for one is `ack`ed and dropped. Messages without `s=` aren't remembered, so they're always taken.

**Ending.** The session stays up while anything moves. The node that dialled sends `quit` once there has been no traffic either way for the agreed hold (at least 10 seconds, so the answering node's first traffic, which can follow its `exchange` by a moment, isn't cut off), nothing is unanswered either way, and it has nothing more to send. In a crossed call either may; that's harmless. The reference daemon also ends a session after 30 minutes however busy, and the next message dials afresh. With no bytes at all from the peer for 3 minutes, or the hold plus 30 seconds if that's longer, give up on the link.

A note for nodes on BPQ: a call for a callsign pair that is already connected makes BPQ send a new SABM and move the link to the new call's socket. Data then arrives only there, though the older session can still send, and a disconnect from either ends the link. So when a second session with a peer connects on the same port, the reference daemon stops the older one at once, without a disconnect or anything else, and its unanswered messages go on the newer one. What the far end's BPQ does with that SABM depends on whether it has had any data on the link yet. If not, it keeps its session, which is still waiting for a prompt, so neither end will prompt, and the two calls share the link. If it has, it ends its session there with a disconnect, hands nothing to its DAPPS listener, and the new call lands at its node's command prompt, or on a link attached to nothing. So:

- When a peer's call arrives while the reference daemon's own call to that peer is on its way, the answering session holds its prompt until that call connects (the answering session is then stopped, having sent nothing) or gives up, for up to 10 seconds. Otherwise the prompt would get there first and the far end would reset the link.
- A caller whose `exchange` gets `Invalid command` back hangs up after 10 seconds, and one that sent its `exchange` without a prompt and hears nothing at all back for 30 seconds hangs up too.
- A caller whose call the far end ends before the exchange waits at least 50 to 75 seconds before dialling that peer again, or its usual cooldown if that has already climbed higher. That's for direct calls only: a route through a connect script keeps its usual cooldown. On a direct link that is the peer's own call taking the link, and the peer's call then needs about 45 seconds to give up (a prompt wait, then the 30 seconds of silence) and clear the link. Dialling sooner cuts it off in turn, and two nodes at the edge of range could keep doing that to each other for minutes.

A node never sends a disconnect for a call its node has already disconnected: BPQ matches a disconnect by callsign pair and port, and could find a newer session.

A message still unanswered when a session ends stays queued and goes on the next one. The reference daemon adds two limits of its own. When a session it dialled breaks off without a `quit` (the link failed, or the peer hung up), it counts the oldest unanswered message as failed, so it waits a while before dialling that neighbour again rather than straight away. And when, with messages of its own unanswered, the link has made no progress for as long as the inactivity timeout (no answer to any of them, and no other DAPPS traffic from the peer), it ends the session the same way, whichever end dialled: at the edge of range a link can stay up while nothing gets through, and a new link starts afresh. A slow link that is still moving either way is left alone: near the edge the peer's answers can queue behind its own mail for minutes.

A normal call, with the answering node's traffic going the other way on the same link:

```
S: DAPPSv1>\n
S: exchange id=b71e02 hold=120 inline=256 z=1\n
C: exchange id=3c90aa hold=60 inline=256 z=1\n
C: msg 7e1f3a2 len=5 fmt=p dst=mail@G0RCV s=1714982400000\nhello
S: ack 7e1f3a2\n
S: msg 9aa1234 len=11 fmt=p dst=mail@G0CALLER s=1714982399000\nhello there
C: ack 9aa1234\n
   (a quiet minute: the lower of the two holds)
C: quit\n
S: bye\n
```

The two `C:` lines of the caller's first turn go in one write, as do the answering node's `ack` and `msg`. Reference: [ExchangeSession.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/Backhaul/ExchangeSession.cs), used at both ends.

The reference daemon's own settings for a neighbour are `DAPPS_SESSION_TAIL_SECONDS` (the hold, default 120, 0 = don't hold, at most 600) and `DAPPS_MAX_MESSAGE_BYTES` (the `max`, default no limit); both are in [Configure](configure.md). A mail-only station that rarely sends can have the reference daemon call its neighbours on a schedule (`DAPPS_SCHEDULED_POLL_ENABLED`): that's an ordinary session with nothing of its own to send.

### End-to-end source tracking (`src=`)

Add to `ihave`:

```
ihave 7e1f3a2 len=5 fmt=p s=1714982400000 src=G0ORIG dst=mail@G0RCV chk=a31f
```

`src=<callsign>` is the *originator* of the message - the callsign of the node whose app submitted it. Distinct from the *link source* (the immediate sender, derived from the bearer's session metadata). On a multi-hop relay path, every forwarder preserves `src=` verbatim; only the originator stamps it.

Why have it: without `src=`, a receiver three hops down can't tell whether a message originated at G0FIRST or just transited through G0FIRST. With `src=`, the receiver's app sees the originator (exposed as the `dapps-origin` MQTT user property) and can route replies back to the right source. Forwarders that don't propagate it omit `src=`; receivers treat absent `src=` as "originator unknown".

Reference: [OfferLine.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/OfferLine.cs) (writing it), [IHaveValidator.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs) (reading it).

### Multi-part fragmentation (`mid=` + `frag=`)

A payload that exceeds the operator's fragment threshold (default 4 KB) is split into N chunks at the originator, sent as N independent `ihave` exchanges, and reassembled at the destination.

```
ihave 11aabbc len=1024 fmt=p s=1714982400001 mid=4cf02b1 frag=1/3 dst=mail@G0RCV chk=...
ihave 22ccddd len=1024 fmt=p s=1714982400002 mid=4cf02b1 frag=2/3 dst=mail@G0RCV chk=...
ihave 33eeefe len=512  fmt=p s=1714982400003 mid=4cf02b1 frag=3/3 dst=mail@G0RCV chk=...
```

- `mid=<7hex>` is a master id - opaque grouping key, same hex format as a regular id.
- `frag=N/M` where N is the 1-based index, M is the total. M ≥ 2 (single-fragment messages omit `mid`/`frag` entirely). N ∈ [1, M].
- `mid` and `frag` MUST both be present or both absent. A partial set is rejected as malformed ([IHaveValidator.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs)).
- Each fragment has its own id (hash of its own chunk + its own salt). Intermediate hops forward fragments as opaque messages.
- Only the final destination groups by `mid`, holds fragments in a reassembly buffer, and delivers the assembled payload to the app once all M arrive.

Why two-id'd: each fragment is independently content-addressed so it can be deduplicated, retried, and routed like any other message. The master id only matters at the destination; relays don't care. A receiver that doesn't know `mid`/`frag` will deliver each fragment to the app as a separate message, which is wrong but not corrupt.

Reassembly buffer entries time out after `FragmentReassemblyTimeoutSeconds` (default 7 days) - long because HF / mesh propagation gaps legitimately last days, and we'd rather hold the partial bytes than throw away most of a near-complete message.

Reference: [OfferLine.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/OfferLine.cs), [IHaveValidator.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs), [DatabaseAndMqttInbox.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/DatabaseAndMqttInbox.cs) (reassembly).

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

Reference: [OfferLine.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/OfferLine.cs), [IHaveValidator.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/IHaveValidator.cs), full design in [reference.md "Message ordering"](app-developers/reference.md#message-ordering-opt-in).

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

New dictionaries get new versions (`z2` and so on), and a shipped version never changes. Each node lists the versions it holds in its `exchange` line (`z=1`), and a sender only compresses with one the peer lists. A receiver that's sent a version it doesn't hold replies `error <id>`, and the sender sends the same message again with `fmt=p` on the same session. The same happens after a `bad` for a compressed payload, which usually means something on the path isn't passing binary data through. Either way the rest of that session goes plain.

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

`peers` is a command for before any exchange; the reference daemon's prober reads the prompt, skips the `exchange` line, asks, and hangs up.

Reference: [InboundConnectionHandler.cs](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs), [DappsProtocolClient.RequestPeersAsync](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs).

### `routes` exchange

```
C: routes\n
S: route M0LTE-9 hops=1\n
S: route GB7RDG hops=2 ageSeconds=300\n
S: route G7VVK hops=2 ageSeconds=900\n
S: end\n
```

The connecting peer asks "what destinations can you reach?"; the server emits one `route` line per known-good destination, then `end\n`. A DAPPS node that dials asks before it sends its `exchange` line, when it hasn't asked that neighbour for a while (`DAPPS_ROUTE_GOSSIP_STALENESS_HOURS`).

Per-line format:

```
route <destBaseCallsign> [hops=<int>] [ageSeconds=<int>]
```

- `destBaseCallsign`: the destination's base callsign (no SSID).
- `hops` (optional): a hint for the receiver's cost calculation. The reference daemon emits `1` for direct neighbours, `2` for traffic-learned routes (via one intermediate). Receivers that don't care about hops ignore it.
- `ageSeconds` (optional): how long ago the responder last saw evidence the route works. Helps the receiver decide whether to trust it.

Receivers import each row as a learned route via the responding peer, marked as gossip-sourced. Failures invalidate via the same per-row failure counter the daemon already maintains for traffic-learned routes; gossip-sourced rows don't get re-exported (only direct observation gets advertised, to avoid distance-vector loops).

The reference daemon's emitter filters: only routes whose failure counter is zero, and only routes the daemon itself has actually used (not just heard about). Manual neighbours are always advertised; traffic-learned routes are advertised only when proven; gossip-imported routes are never advertised (don't re-export hearsay).

Implementations that don't care about route gossip should respond `eh?\n` to the command. Senders treat `eh?` as "this peer doesn't gossip" and don't ask again for a while.

Reference: [InboundConnectionHandler.RoutesAsync](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.core/Services/InboundConnectionHandler.cs), [DappsProtocolClient.RequestRoutesAsync](https://github.com/packet-net/dapps/blob/master/src/dapps/dapps.client/DappsProtocolClient.cs).

### Quit / help

```
C: quit\n      (or q, bye, exit; case-insensitive)
S: bye\n       (then closes)

C: help\n      (or info; case-insensitive)
S: This is DAPPS. See https://github.com/packet-net/dapps/blob/master/README.md for details.\n
```

Help text is human-only; programs shouldn't parse it. After `help` the server goes back to reading commands; after `quit` it closes.

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

1. **Connect outbound to the reference daemon, push a message.** Read the `DAPPSv1>` prompt and the `exchange` line after it; you write `ihave …`, expect `send <id>`, write `data … hello`, expect `ack <id>`. Inspect the daemon's `/Recent` page or its MQTT topic to confirm receipt.
2. **Receive inbound from the reference daemon.** Configure the reference daemon to forward to your callsign; queue a message; expect a session in, the daemon's `exchange` after yours, then an `ihave` (or with `inline=` above zero a `msg`), and your `ack` to clear the daemon's queue. Then its `quit`.
3. **Round-trip with `chk`.** Include `chk=NNNN` on outbound; verify the reference daemon validates it (deliberately corrupt one byte and watch the reference reject with `error <id>`).
4. **Round-trip with `ttl`.** Include `ttl=600`; inspect the daemon's stored row to confirm it persisted, then forward via the daemon to a third node and watch the residual decrement.
5. **Reject malformed offers.** Send `ihave x len=oops fmt=p dst=mail@G0X` (bad len) and verify your implementation responds `error x` not `send x`.

Beyond that, each optional feature has its own equivalence test: send/receive with the field set, confirm the reference daemon round-trips the value (visible on `/Recent`, on MQTT user properties, or in the SQLite database directly).

## See also

- [App developers - reference](app-developers/reference.md) - higher-level wire summary, app-interface mappings (MQTT topics, REST endpoints), and worked examples for app authors using DAPPS as a service.
- [Discovery & routing](discovery-and-routing.md) - how the reference daemon turns discovered peers into route decisions; orthogonal to the wire protocol but informs why `peers`/beacons exist.
- The reference implementation source: [`src/dapps/dapps.client/`](https://github.com/packet-net/dapps/tree/master/src/dapps/dapps.client) (wire-level codecs, bearer-neutral) and [`src/dapps/dapps.core/Services/`](https://github.com/packet-net/dapps/tree/master/src/dapps/dapps.core/Services) (session handling, inbox/outbox, discovery).
