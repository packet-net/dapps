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

The rule that everything else follows: **a node never sends message contents to a peer before it has that peer's rules.** So a node never receives contents it hasn't agreed to take, either through the rules it stated or by answering an offer with `send`.

1. The answering node sends `DAPPSv1>\n` on connect, as now, followed straight away by its own `exchange <rules>\n` line (below). Humans and probes see that extra line once and carry on with commands (`help`, `peers`, `routes`, `quit`) as now; the prober ignores `exchange` lines. `ihave`/`send`/`data`/`ack` also keep working outside exchange mode, so a minimal third-party sender can still push one message at a time.
2. Every node sends its own `exchange` line exactly once per session object: the answering node right after the prompt; a DAPPS caller as soon as it sees the prompt or the peer's `exchange`, or after 10 s with neither (crossed calls, below). After a connect script, the script has consumed the prompt; the caller sends its `exchange` then.
3. When the gossip gate says so, a caller runs `routes` before sending its `exchange` (command/response, as now).
4. A node is in exchange mode once it has both sent its own `exchange` and received the peer's. From then on either side sends its traffic, following the peer's rules; the hold is min(own, peer's).
5. Session tags: each session object picks a random tag at creation and sends it as `id=<tag>` in its `exchange`. An `exchange` carrying a tag not seen before in this session, after we already had one, means the peer's session object changed (BPQ moved the link to a newer session there): send our `exchange` again (same tag as before) and re-send everything of ours that is still unanswered; the peer's duplicate memory makes re-sending safe. An `exchange` repeating a tag we already have is only the peer re-sending its rules: take them, send nothing. This converges without ping-pong whatever mix of old and new session objects is on the link.
6. In exchange mode a `DAPPSv1>` line is ignored.

Crossed calls: two callers each waiting for a prompt hear none; after 10 s each sends its `exchange`, each receives the other's, and the session is symmetric. So the lower-callsign glare timer (`GlareSilenceBudget`, `serveOnGlare`) is no longer needed between DAPPS nodes. Also see "AGW: one session per peer" below.

### The receiver's rules

`exchange` carries the sending node's rules for what it will receive:

- `id=<tag>`: the session tag (rule 5).
- `hold=<s>`: seconds of quiet to keep the link up for (its setting for that peer, today's `SessionTailSeconds`).
- `inline=<bytes>`: largest on-air payload it takes unasked as `msg`. Bigger ones must be offered with `ihave` first. `inline=0`: offer everything first (today's behaviour, and today's cost). Reference default 256.
- `max=<bytes>`: largest message (`len`) it takes at all; bigger ones are neither sent nor offered to it. Omitted: no limit. New setting, default no limit.
- `z=<list>`: compression dictionaries it holds, e.g. `z=1`. Omitted: plain only.

A node can still refuse any message with `no`: an offer before its contents go on air, a small unasked one after it arrives (not stored or passed on). A node that wants to judge every message by its sender, destination or app before any contents are sent sets `inline=0`.

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
- Window: at most 8 messages sent and not yet answered, per direction.
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

Observed (phase 2, `BpqCrossedCallExperiments`, details in `docs-internal/end-to-end-tests.md`): at the node that dialled over the live link, the data goes to the newest socket; the listener sees no `d` when the link moves, can still send on it, and gets a `d` when the link ends; a `d` from either socket ends the link. At the other node it depends on whether it has had an I-frame on the link: if not, it repeats its UA and its call carries on; if it has (always, with DAPPS, as the answering end prompts at once), it resets the link, its call gets a `d`, its listener gets nothing, and the new call lands at its node's command prompt. Two calls made at once give one link between the two callers' sockets, confirmed like any call, after three or four SABMs; neither listener gets a connect. PR #197 review: the dialling node now sends its exchange at once when its call retires an older session, and a caller that gets only non-DAPPS text back for 10 s after its exchange hangs up, so the reset case costs a short cooldown at each end rather than a 3-minute stall.

## Order of work

- [x] 1. Exchange protocol: a shared exchange engine (both ends), session start and handshake rules, the receiver's rules and `no`, window, ending, peer restart; replace `rev`, `tail`, `pending` and the held session's poll; `NodePoller` via exchange; forwarder hands work to open sessions of either direction; Refused result. Spec rewritten in `docs/implement.md`. Unit tests, and the end-to-end tests (`DappsEndToEndTests`) moved from rev/pending/tail to exchange. PR: #196 (in review)
- [x] 2. Crossed calls: AGW one-session-per-peer, confirmed by experiment; random spread on retry and redial timing, so two nodes that fail together can't keep dialling each other in lockstep (the 12-minute soak on #195 did exactly that for 13 minutes after a daemon restart: both had mail, dialled within 2 s, crossed, then retried on the same fixed 10/10/10/30/30 s schedule, 27 connections); a net-sim scenario that forces a crossed call, and the soak's restart recovering in seconds. PR: #197 (in review)
- [ ] 3. Measure on a realistic channel: move the net-sim pin to v0.4.0 (physical FM channel, half-duplex radios; links take `path_loss_db`, about 120 for a clean link and about 157 for the noisy soak; qpsk3600 on `radio: { channel: wide, squelch: hard }`), then re-measure both the old protocol (#195's build) and the exchange on it: WPS scenario on AFSK 1200 and QPSK 3600, the crossed-call scenario, and the soak. Tune what the physical channel shows (the v0.3 soak had 36 moments of both ends transmitting at once, and one reply waited 13 s for the channel). Tighten the scenarios' ceilings to what the new protocol does. Report against Kevin's baseline. PR:
