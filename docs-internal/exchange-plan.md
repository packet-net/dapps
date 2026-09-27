# DAPPSv1 exchange: plan

The design and the order of work for replacing the per-message session protocol between DAPPS nodes with a symmetric exchange. Tom asked for it on 2026-09-27 ("rather get it fully right now, while it's on our benches"); nothing is deployed, so there is no compatibility with 0.41.x to keep. Built on the duplicate memory (received ledger) and measured with the net-sim scenarios (#194).

## Why

Measured on net-sim (#194, `docs-internal/end-to-end-tests.md`):

- Every message costs four DAPPS turns (`ihave`, `send`, `data`, `ack`), and BPQ turns each into two transmissions: it sets P on the last I-frame of every burst and the far end answers with an immediate RR (L2Code.c; no setting changes it). So about nine transmissions a message, each with a fixed TX-delay-and-preamble cost. Kevin's WPS exchange (8 posts and their acks) takes 85 s at AFSK 1200 and 60 s at QPSK 3600 on one connection: six times the bit rate buys 30%.
- The callee's mail waits for the caller to finish and ask (`pending`, then `rev`).
- On GitHub's slower runners both ends dialled within a second (both had traffic), BPQ attached the link to a different DAPPS session at one end from the one the peer was talking to, and nothing moved for three minutes.

The fix: every time a station transmits, it says everything it has to say, in both directions, and the session is symmetric so that who dialled stops mattering.

## Design

### Session start

1. The answering node sends `DAPPSv1>\n` on connect, as now. Humans and probes carry on with commands (`help`, `peers`, `routes`, `quit`) as now; `ihave`/`send`/`data`/`ack` also keep working outside exchange mode, so a minimal third-party sender still can push one message at a time.
2. A DAPPS caller waits for the prompt (it avoids a collision on air with the prompt, which the callee sends straight after UA), but no longer than 10 s. After a connect script the script has consumed the prompt already.
3. When the gossip gate says so, the caller runs `routes` first (command/response, as now).
4. The caller sends `exchange <rules>\n` and in the same write its first messages (window permitting), without waiting for a reply.
5. Handshake rule, the same for both ends: on receiving `exchange <rules>`, a node that has not yet sent its own `exchange` in this session sends it now. A node that has already sent its own does not reply. Either way it takes the peer's rules, and the hold becomes min(own, peer's). Result: normal call, the callee replies once; crossed call, both sent theirs and neither replies; no ping-pong in any case.
6. A second `exchange` from the peer in the same session means the peer's session object restarted (BPQ moved the link to a newer session there). Send our `exchange` again and re-send everything of ours that is still unanswered; the peer's duplicate memory makes re-sending safe.
7. In exchange mode a `DAPPSv1>` line is ignored.

Crossed calls: a caller that got no prompt within 10 s sends `exchange` anyway, which is harmless whatever the peer is doing (a callee processes it as a command; a caller that is itself waiting for a prompt answers it by rule 5). So the lower-callsign glare timer (`GlareSilenceBudget`, `serveOnGlare`) is no longer needed between DAPPS nodes. Also see "AGW: one session per peer" below.

### The receiver's rules

`exchange` carries the sending node's rules for what it will receive:

- `hold=<s>`: seconds of quiet to keep the link up for (its setting for that peer, today's `SessionTailSeconds`).
- `inline=<bytes>`: largest on-air payload it takes unasked as `msg`. Bigger ones must be offered with `ihave` first. `inline=0`: offer everything first. Reference default 256.
- `max=<bytes>`: largest message (`len`) it takes at all; bigger ones are neither sent nor offered to it. Omitted: no limit. New setting, default no limit.
- `z=<list>`: compression dictionaries it holds, e.g. `z=1`. Omitted: plain only.

Until the peer's `exchange` arrives, a caller's first turn uses defaults: `inline=256`, compression as the operator set for that neighbour, no `max`, and at most 4 messages. So a receiver's own rules apply from its first reply; the worst it can be sent unasked is 4 small payloads once per session. A message the rules exclude that arrives anyway is answered `no` and dropped.

### Lines in exchange mode

Either side, any time. Header fields are exactly today's `ihave` fields (`len`, `fmt`, `clen`, `s`, `ttl`, `dst`, `src`, `mid`, `frag`, `sid`, `sn`, `gt`, app headers, `chk` last).

| Line | Meaning | Answer |
|---|---|---|
| `msg <id> <fields>\n` then the payload (`clen` bytes if compressed, else `len`) | A message with its payload | `ack`, `bad`, `no`, or `error` |
| `ihave <id> <fields>\n` | Offer without payload | `send`, `ack` (already have it), `no`, or `error` |
| `send <id>\n` | Send your offer's payload | `data <id>\n` then the payload |
| `data <id>\n` then the payload | Payload of an offer we asked for | `ack` or `bad` |
| `ack <id>\n` | Got it, or already had it: delivered | none |
| `bad <id>\n` | Payload didn't decode or hash-check | sender may send it once more plain |
| `error <id>\n` | Header refused (malformed, unknown `fmt`) | sender may send it once more plain; else it fails for this session |
| `no <id> [reason]\n` | Won't take it (bigger than `max`, or a policy). Not stored or passed on | none; the sender stops offering it on this link, and the forwarder treats it as refused by this next hop: another route if there is one, else dropped with the reason (not retried here until it expires) |
| `quit\n` | Ending | `bye\n`, then hang up |

- A `msg` or `data` whose header can't be parsed far enough to know how many payload bytes follow desynchronises the stream: close the session (the sender's messages stay queued). If `len`/`clen` parse but anything else is refused, read and discard the payload, then answer.
- Window: at most 8 messages sent and not yet answered, per direction (4 in a caller's first turn).
- Order: each side writes in queue order and handles received lines in order. Ordered streams (`sid`/`sn`) keep their reordering at the destination.
- Writes: one writer per session; write whatever is ready as soon as it is ready. BPQ packs it into frames for its next over, so no DAPPS batching timer.
- Duplicate memory: `ihave` for a held message is answered `ack`; a `msg` for one is `ack`ed and discarded; the inbox never delivers twice.

### Where each side's traffic comes from

Both sides take work from the forwarder as `IBackhaulBatch`es: the caller from the batch that made it dial, and either side from batches the forwarder hands to an open exchange session for that neighbour (today's `TryHandToOpenSession` for held outbound sessions, and `InboundSessionDirectory.TryTakeBatch` for inbound ones). A session registers as open for its peer once in exchange mode, and wakes the forwarder (`ForwarderWakeup`), so anything already queued with that peer as next hop is handed over at once. An open session is a live link: hand work to it even if that neighbour is in failure cooldown.

This replaces `rev` (and its final-destination-only `GetMessagesForCaller`), `pending`, `tail` and the held session's poll. `NodePoller` (F3 polls) becomes: dial, `exchange`, nothing of our own to send, take what arrives, end on the hold rule.

Per message, reported to its batch: `ack` -> Ok; `no` -> a new Refused result; `error`/`bad` after one plain retry -> Fail; session ends with it unanswered -> Defer (stays queued, no cooldown).

### Ending

- The session stays up while anything moves. It ends when for `hold` seconds there has been no traffic either way, nothing is unanswered either way, and nothing is queued for the peer. The node that dialled sends `quit` (in a crossed call both may; harmless).
- `hold=0` still waits 10 s after the last traffic, so the callee's first turn (its queued mail, handed over a moment after `exchange`) isn't cut off.
- Longest session 30 minutes (today's `MaxHold`), then `quit`; the next message dials afresh.
- Inactivity timeout (no bytes at all): max(3 min, hold + 30 s).

### AGW: one session per peer

Observed on BPQ (CI run of #194): a node's outbound AGW connect for a callsign pair already connected inbound (on the listener socket) makes BPQ send a SABM and move the link to the outbound socket; data then goes there, and the inbound session hears nothing more, while still holding the forwarder's hand-offs. So when a session for peer P becomes connected at a node (inbound `C`, or an outbound connect confirmed), any older DAPPS session for P at that node is retired silently: stop its handler, never send a `d` for it (AgwInboundService rule 3), and Defer its unanswered work. Confirm with an experiment on the net-sim fixture (force a crossed call: both ends submit at the same instant) which socket gets the data and whether the listener sees a `d`.

## Order of work

- [ ] 1. Exchange protocol: a shared exchange engine (both ends), session start and handshake rules, the receiver's rules and `no`, window, ending, peer restart; replace `rev`, `tail`, `pending` and the held session's poll; `NodePoller` via exchange; forwarder hands work to open sessions of either direction; Refused result. Spec rewritten in `docs/implement.md`. Unit tests, and the end-to-end tests (`DappsEndToEndTests`) moved from rev/pending/tail to exchange. PR:
- [ ] 2. Crossed calls: AGW one-session-per-peer, confirmed by experiment; a net-sim scenario that forces a crossed call. PR:
- [ ] 3. Measure: WPS scenario on AFSK 1200 and QPSK 3600, and the soak. Tighten the scenario's ceilings to what the new protocol does. Report against Kevin's baseline. PR (or with 2):
