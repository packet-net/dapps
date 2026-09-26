# DAPPSv1 session traffic optimisations - PROPOSAL

## TL;DR

A captured exchange between G5ALF-3 and M0AHN-3 (AGW, 1200 baud, wps-repl
traffic) moved **11 messages of ~200 bytes in 77 s over 6 separate AX.25
connections, using about 150 frames**. Most of that is overhead:

- **One connection per message.** The forwarder opens a fresh session for
  every queued message, even when several are waiting for the same peer.
- **Four turnarounds per message.** `ihave` → `send` → `data`+payload → `ack`,
  at roughly 1 s per turnaround on this link.
- **An extra frame per message.** `data <id>\n` and the payload are sent as
  two I-frames.

Proposals 1-3 need no wire-protocol change and would bring the same exchange
down to 1-2 connections and about 85 frames. Adding 4 and 5 gets it to about
40 frames. A connection tail (9) holds the link open through a burst, so
messages mid-burst skip the reconnect entirely; it needs a small negotiated
protocol addition so the called node can say it has traffic. Deflate
compression (10) is already in the protocol and every daemon can receive
it; only sending is missing. Both 9 and 10 can be set per neighbour, so
they can be turned off for debugging.

All code references are to `c6b62e8` (0.39.0).

## The captured exchange

| Observation | Where in the trace |
|---|---|
| G5ALF had sn=22, 23 and 24 queued by ~08:11:03 (worked back from `ttl=`), but sent each in its own session | Sessions at 08:11:26, 08:11:33, 08:11:48 |
| Each session costs SABM/UA, `DAPPSv1>`, `rev`, a closing `DAPPSv1>` and DISC/UA (about 8 frames) before any payload moves | Every session |
| The 2 s gap between DISC and the next SABM | `LinkSettleGate`, 2 s default |
| `data <id>` and the payload go out as separate transmissions, with an RR in between | e.g. 08:11:03 / 08:11:04 |
| Bare RRs are sent even when the same station's I-frame follows within a second | e.g. 08:11:02 `RR R1` then `I S0 R1` |
| 3 of the 11 messages are wps-repl `{"op":"ack"}`; one of them needed a whole session to itself | 08:12:12 |
| `ihave` header is ~150 bytes for a 203-byte payload | Every offer |
| `rev` with nothing queued still costs a round trip | 08:11:53, 08:12:02 |

The two code paths behind most of this:

- `OutboundMessageManager.DoRunCore` loops over pending messages and calls
  `ForwardAndObserveAsync` once each (`OutboundMessageManager.cs:85`, `:152`).
- `Dappsv1SessionBackhaul.SendAsync` takes a single `BackhaulMessage` and does
  connect → prompt → offer → data → `routes` → `rev` → disconnect for it.

## Proposals

Ranked by benefit for effort. "Protocol" says whether peers need to change.

### 1. Batch all pending messages for a next hop into one session

**Change.** In `DoRunCore`, group `NextHop` decisions by route callsign and
pass the list to a batched `SendAsync(route, IReadOnlyList<BackhaulMessage>)`
that connects once, loops offer/data/ack, then does `routes` and `rev` once.

**Benefit.** In the trace, G5ALF's 5 sessions become 1: about 36 frames and
~30 s of airtime saved. Each extra message in a batch costs only its own
exchange, not a new connection. It also means fewer chances for two nodes to
dial each other at once (the #178 crossed connect), and `LinkSettleGate`
rarely has a redial left to hold back.

**Protocol.** None. The receiver already accepts repeated `ihave` on one
session, and its `rev` drain already sends several messages per session.

**Must handle.**
- Record outcomes per message (routing feedback, `MarkMessageAsForwarded`,
  metrics), so messages already acked stay marked as done if the link drops
  mid-batch.
- A crossed connect at the prompt, or `PeerSessionBusyException`, defers the
  whole batch: no failure, no cooldown.
- The per-destination backoff and the `WouldDialIntoOpenSession` check move
  from per message to per batch.
- Flood copies can be batched per neighbour too (second priority).

### 2. Check the queue again before disconnecting

**Change.** After the `rev` drain, if messages for this peer were queued
during the session, offer them before DISC. Keeping the link open for
longer than that is proposal 9.

**Benefit.** Covers messages that arrive while a session is open, which
batching alone misses. In the trace, the reconnect 2 s after each DISC would
have been avoided.

**Protocol.** None.

**Must handle.** While we hold the session, `PeerSessionRegistry` makes the
peer's forwarder defer anything it has for us, so its mail only reaches us
through our `rev`. Send `rev` again just before disconnecting, not only once
after pushing.

### 3. Write the `data` line and payload in one call

**Change.** Build one buffer and call `WriteAsync` once in
`DappsProtocolClient.SendMessageAsync` (`DappsProtocolClient.cs:228-229`).
The inbound `rev` drain uses the same method, so both directions are fixed.

**Benefit.** One I-frame and one key-up saved per message, and often an RR
too. That's 11 frames and 11 key-ups in the trace. A 203-byte payload plus
the 13-byte `data` line fits inside a 256-byte PACLEN.

**Protocol.** None. About 3 lines of code.

**Why keep the `data` line.** It's redundant in the current strict flow, but
it marks where the unframed payload bytes start. It also lets the receiver
reject stray text (node banners after a link reset, errors) rather than
reading `len` bytes of garbage. And it's what makes pipelining (4a) work.
It costs 13 bytes and no turnaround.

### 4. Pipeline offers, or send small payloads straight after `ihave`

**Change.** Two options, both behind capability negotiation (e.g. a
`DAPPSv1.1>` prompt or a `caps` command) so older peers are unaffected:

- **4a. Pipelining.** Send N `ihave` lines in one burst, read N `send`/`no`
  replies, send all the data, read N acks.
- **4b. Payload with the offer.** When `len` is below a threshold, send
  `ihave` + `data` + payload without waiting for `send`.

**Benefit.**
- 4a: four turnarounds per *batch* instead of per message, and the AX.25
  window (MAXFRAME up to 7) gets used properly.
- 4b: two turnarounds per message instead of four. The payload is sometimes
  wasted when the receiver already has the message (flood duplicates), but at
  ~200 bytes that's cheaper than a 1 s turnaround.

**Protocol.** Yes, negotiated.

### 5. Cumulative, delayed wps-repl acks (app side)

**Change.** Wait a few seconds, then send one ack for the highest contiguous
seq per origin. Better still, carry it on outgoing replication data.

**Benefit.** The separate acks for seq 48 and 50 in the trace become one, sent
in an existing session. That removes the 6th session entirely. Ack traffic
grows with bursts rather than with every message.

**Protocol.** None for DAPPS; the change is in wps-repl.

### 6. AX.25 tuning (operator docs)

**Change.** Document in `docs/tune.md`:
- Raise T2/RESPTIME so the node waits for the app's reply and carries the
  ack in it, not in a bare RR.
- Raise MAXFRAME so that, once 1 and 4 are in, several I-frames go out per RR.

**Benefit.** Removes most standalone RRs. Mostly pays off after 1 and 4.

**Protocol.** None; node configuration only.

### 7. Shrink the `ihave` header (later, versioned)

**Change.** Options:
- Drop fields that are at their defaults (`gt=0`, `fmt=p`).
- Encode `s=` and `ttl=` more compactly.
- Once batching exists, compress all payloads in a batch together.
  Per-message payload compression is proposal 10.

**Benefit.** The header is ~75% of payload size for small messages;
trimming it is the largest remaining per-message byte saving.

**Protocol.** Yes, negotiated.

### 8. Skip `rev` when nothing is waiting (minor)

**Change.** The callee says how many messages it has queued for the caller,
in its `ack` line or prompt. The caller skips `rev` when there are none.

**Benefit.** One round trip per session when the peer has nothing queued
(sessions 4 and 5 in the trace).

**Protocol.** Yes, negotiated.

### 9. Connection tail: hold the link open through a burst

**Change.** After a session that carried application traffic, the caller
keeps the link open for a configurable idle time (`SessionTailSeconds`,
default 540 s, overridable per neighbour; see "Configuration" below). The timer restarts whenever a message moves in either
direction. When it runs out, the caller sends a final `rev`, then `quit`
and DISC. Both ends can send while the link is held:

- **Caller side (no protocol change).** When the forwarder finds a peer with
  an open *outbound* session, it hands the message to that session instead
  of deferring it. The session wakes and runs `ihave`/`send`/`data`/`ack`
  straight away. `PeerSessionRegistry` becomes the place to find the open
  session, not just a flag that one exists.
- **Callee side (negotiated).** The callee can only send inside a `rev`
  drain, and the caller is sitting silent waiting for input. So a new
  unsolicited line: when the callee is idle at its prompt and has something
  for the caller, it writes `pending\n`. The caller answers with `rev` and
  the existing drain runs.

**Why 9 minutes.** The live network already uses a 9-minute application
keepalive to stay under the 10-minute idle timeouts some nodes enforce on
circuits. A tail that ends after 9 minutes of inactivity uses the same
margin: the caller closes cleanly just before a node would cut the link.

Negotiation: the caller sends `tail <seconds>\n` after the first prompt (or
as part of the capability negotiation in proposal 4). A new callee replies
`ok tail <seconds>`, capped at its own setting for the caller, and from then on may send
`pending`. An old callee replies `eh?`, and the caller does not hold the
link: holding would only keep the callee's own traffic for us stuck behind
`PeerSessionRegistry` until we disconnect.

**Why `pending` rather than fully symmetric sessions.** Letting either end
send `ihave` at any time means both sides must multiplex one reader across
their own exchange and the peer's, and two `ihave`s crossing on the air
leave each side reading an offer where it expected `send`. With `pending`,
the callee stays the command server and the caller stays the only one
issuing commands. If `pending` crosses with a caller `ihave`, nothing
breaks: the callee handles the `ihave` as normal, and the caller notes the
`pending` whenever it reads it and issues `rev` once its exchange finishes.
It costs one extra turnaround per callee burst compared with symmetric
sessions.

**Benefit.**
- In the trace, all traffic falls within about 70 s (08:11:03 to 08:12:12),
  so the tail makes the 6 sessions one, including the session that carried
  only the wps-repl ack. On its own that's about 45 frames saved;
  on top of 1 and 2 it saves the one remaining reconnect (about 8 frames).
- Latency during a burst: a message queued mid-tail starts moving at once,
  instead of waiting for the next forwarder tick, `LinkSettleGate`, SABM/UA
  and the `DAPPSv1>` prompt (several seconds per message at 1200 baud). This
  is where users notice it.
- With 9 minutes, bursts that are minutes apart (a conversation, a
  replication catch-up in stages) also share one session, not only messages
  seconds apart.
- An idle AX.25 link costs almost no airtime: nothing is sent except the T3
  keepalive (an RR poll and reply every 3 min by default). A full 9-minute
  tail that catches no more traffic costs about 6 frames, less than one
  reconnect.
- While the link is held, neither node dials the other, so the crossed
  connects from #178/#185 can't happen for that pair.

**Protocol.** Yes, negotiated (`tail`, `ok tail`, `pending`). The caller-side
half works with any peer but, as above, shouldn't be used without the
callee half.

**Must handle.**
- **Read timeouts.** Both ends drop a session after 3 min without a line,
  well short of the 9-minute default. In tail mode both ends' idle read
  timeout becomes the agreed tail plus a margin (e.g. 30 s), so the caller's
  `quit` always arrives before the callee gives up. The 3-min timeout still
  applies while an exchange is in progress (waiting for `send`, `data`,
  `ack`), so a dead peer mid-exchange is noticed as quickly as today.
- **Caller waits on two things.** The idle caller must wait for a line from
  the peer *and* for a message from its own forwarder, without cancelling a
  half-read line. That means one long-running read loop feeding the session
  logic, not a read call per step.
- **Node timeouts.** 9 minutes sits under the common 10-minute node
  timeouts, but some nodes or multi-hop NET/ROM paths may use shorter ones.
  The T3 RR polls are link-layer only and can't be relied on to count as
  activity for a node's idle timer. So: the callee's `ok tail` can cap the
  tail to fit its own node, the sysop can set a lower value, and a link the
  node drops mid-tail is treated as the normal end of the tail, not a
  failure (no routing penalty, no backoff).
- **Closing races.** If `pending` crosses with the caller's `quit`, the
  callee's message stays queued and goes on its next forwarder tick. A final
  `rev` just before `quit` keeps that window small.
- **Which sessions hold.** Only sessions that moved application messages.
  Reverse-poll sweeps, route exchanges and discovery sessions close as they
  do today, so a node doesn't keep links open to every neighbour it polls.
- **Node resources.** A held link takes up a node stream/circuit for up to
  9 minutes after the last message, which matters more on a node with many
  neighbours. Cap the number of tails held at once, and cap total session
  length (e.g. `SessionMaxMinutes`) so a chatty pair can't hold a link
  forever.
- **Configuration.** A system default (`DAPPS_SESSION_TAIL_SECONDS`, 540)
  and a nullable `SessionTailSeconds` on the neighbour row, following the
  `BearerPort` pattern: null uses the default, 0 turns the tail off for that
  peer. Each end applies its own row for the other, and the agreed tail is
  the lower of the two. Typical uses: a shorter tail through a busy shared
  node or a path with a shorter timeout; 0 while debugging, so every
  exchange is its own session, as today; the default on a quiet
  point-to-point link.

### 10. Compress payloads when it saves bytes

**Change.** Send `fmt=d` (raw deflate) for a payload when the compressed
form is actually smaller, and plain `fmt=p` otherwise. The decision is made
per message by trying it: compress, and use the result only if `clen` plus
the ` clen=NNN` field it adds saves at least a minimum (e.g. 16 bytes).
Don't bother trying below about 64 bytes, where deflate can't win. There's
no guessing by content type: already-compressed or encrypted payloads just
fail the test and go plain.

The receiver has handled `fmt=d` since v0.1.0
(`InboundConnectionHandler.HandleData`). Only the sending side is missing:
`DappsProtocolClient.SendMessageAsync` throws `NotImplementedException` for
anything other than plain (`DappsProtocolClient.cs:148-151`). The `rev`
drain uses the same method, so both directions get it.

**Benefit.** Deflate (level 9) on sample payloads of the kinds in use:

| Payload | Plain | Deflate | Sent as |
|---|---|---|---|
| wps-repl data, ~200 B JSON | 205 B | 164 B | `d`, saves ~32 B after `clen=` |
| wps-repl `{"op":"ack"}` | 40 B | 39 B | `p` (below threshold) |
| Mail/bulletin text | 300 B+ | typically a third smaller or better, improving with length | `d` |

In the trace that's about 8 × 32 ≈ 250 bytes, about 2 s of airtime at 1200
baud: worthwhile but modest. The gain is much bigger for longer text (mail,
bulletins), which is where users feel the transfer time. Small JSON gains
far more from a shared dictionary, as MeshCore already does (zstd with a
dictionary trained on DAPPS traffic, `dapps.meshcore/DappsCompression.cs`).
Offering that on DAPPSv1 as a second format (e.g. `fmt=z` plus a dictionary
version) is a later, negotiated step alongside 7.

**Protocol.** None for deflate: it's in the spec (`fmt=d`, `clen=`) and every
reference daemon accepts it. The message id is the hash of the uncompressed
payload and `len` is the uncompressed length, so ids and deduplication are
unaffected, and each hop decides for itself whether to compress when it
forwards.

**Configuration.** Like the tail, a system default plus a per-neighbour
override on the neighbour row:

| Value | Behaviour |
|---|---|
| `auto` (default) | Compress when it saves at least the minimum |
| `off` | Always send plain |
| `on` | Always compress, even when it doesn't save anything (interop testing of the receive path) |

System default `DAPPS_COMPRESSION` (`auto`); neighbour row `Compression`,
null = use the default. It governs only what this node *sends* to that peer;
compressed payloads we receive are always accepted. Reasons to turn it off:
- Debugging: payloads stay readable on a monitor or in a trace. The analysis
  in this document depended on that.
- A third-party implementation that mishandles `fmt=d`.
- Ruling compression out while chasing a fault.

The existing global `MeshCoreCompress` could later move to the same
per-neighbour setting.

**Must handle.**
- Log and audit both `len` and `clen`, so the saving is visible and a
  compressed message can be matched to its plain content when debugging.
- Metrics: bytes saved per peer, so operators can see whether `auto` is
  doing anything on their traffic.
- The PACLEN arithmetic from proposal 3 uses the bytes on the wire (`clen`),
  not `len`.

## Configuration

Proposals 9 and 10 add two per-peer settings. Both follow the existing
`BearerPort` pattern: a system default in `SystemOptions`, plus a nullable
column on `DbNeighbour` where null means "use the default".

| Setting | System default | Neighbour column | Off |
|---|---|---|---|
| Connection tail | `DAPPS_SESSION_TAIL_SECONDS` = 540 | `SessionTailSeconds` (int?) | `0` |
| Payload compression | `DAPPS_COMPRESSION` = `auto` | `Compression` (`auto`/`off`/`on`, null) | `off` |

A peer that calls in without a neighbour row gets the system defaults. Both
need to be settable through the web UI and the MCP config tools, like the
other neighbour fields.

## Expected effect on the captured exchange

These are estimates from the trace, not measurements.

| | Connections | Turnarounds per message | Approx. frames |
|---|---|---|---|
| Today | 6 | 4 | ~150 |
| 1 + 2 + 3 | 1-2 | 4 | ~85 |
| + 4a + 5 | 1 | ~1 (per batch of 4) | ~40 |
| 1 + 2 + 3 + 9 | 1 | 4 | ~75 |
| 1 + 2 + 3 + 9 + 4a + 5 | 1 | ~1 (per batch of 4) | ~40, with no reconnect delay for traffic mid-burst |

Compression (10) doesn't change the frame counts here much: it saves about
250 bytes (about 2 s of airtime) on this exchange, and more on longer text.

## Suggested order

1. **3** and **10** - small, no protocol change, immediate saving. Do them
   together, since both change `SendMessageAsync`.
2. **1**, then **2** - the largest saving, no protocol change.
3. **5** - raise with wps-repl.
4. **6** - docs, once 1 is in.
5. **9**, **4**, **7**, **8** - together, behind one capability negotiation.
   Of these, 9 does most for how responsive things feel during a burst, and
   it's the one to do first if the negotiation is built in stages.
