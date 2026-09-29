# End-to-end tests

`dapps.core.tests/Integration/DappsEndToEndTests.cs` runs the DAPPSv1 protocol for real: two DAPPS daemons, each in its own process on its own linbpq node, the nodes linked over AXIP.

```
app -> DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B -> app
```

- The daemons are the normal `dapps.core` build, configured with the usual `DAPPS_*` environment variables and a fresh database each (`DappsDaemon`). Tests drive them through the app API, so nothing pokes the forwarder by hand.
- `AirMonitor` records what went over the air from BPQ's own AGW monitor on both nodes. A node's monitor only reports what it receives, so each node shows the other one's transmissions.
- `RawAgwCaller` talks DAPPSv1 by hand over the air, to check the server's replies one at a time.

## Running them

They need Docker (Testcontainers starts `m0lte/linbpq:latest`) and a built solution:

```
dotnet build src/dapps/dapps.sln
src/dapps/dapps.core.tests/bin/Debug/net10.0/dapps.core.tests --filter-class dapps.core.tests.Integration.DappsEndToEndTests
```

Each test starts its own daemons, so the class takes a few minutes. CI runs them with the rest of the integration tests.

## What they cover

| Test | Checks |
|---|---|
| A message goes from one app to the other | B's prompt and `exchange`, the `routes` pull, A's `exchange` and `msg` in one frame, the `ack`, `src=`, TTL counting down, `quit`/`bye` and a clean hang-up |
| A burst goes on one connection | Five queued messages, one connect |
| Mail the other node can't deliver comes back on the caller's session | A node with no route to its peer hands its mail to the peer's session when it calls |
| Payloads | A WPS post compressed with the dictionary (`fmt=z1`), a 9.6 KB message split into three compressed parts and reassembled, and 1.5 KB of random binary offered with `ihave` |
| An open link | Traffic both ways on one connection, the reply sent by the node that answered |
| A quiet link | `quit` and hang-up once the hold runs out, then a fresh connect |
| An ordered stream | `sid=`/`sn=` on air, all parts delivered |
| A probe | The `peers` exchange, and no `exchange` from the prober |
| The server's replies | Prompt and `exchange`, `help`, `error`, `send`/`ack`, `bad`, `peers`, `routes`, the caller's `exchange` and `msg`, mail sent unasked mid-session, `quit`/`bye`, and `eh?` then hang-up |
| A connect script | Reaching a peer through its node's prompt |

`AgwLargePayloadIntegrationTests` covers payloads around BPQ's 256-byte AGW data limit, which these tests found.

## On a simulated radio channel

The tests above use AXIP, so frames cost no airtime. `NetSimTwoBpqFixture` puts the two BPQs on a simulated radio channel instead: [net-sim](https://github.com/packet-net/net-sim) runs real modems, gives each an FM radio (a Tait TM8100 at 25 W by default) and puts a physical FM channel between them (its `docs/fm-channel.md`). Each BPQ attaches to one simulated radio over KISS, as it would to a real TNC.

```
DAPPS A -AGW- BPQ-A -KISS- [net-sim: modem ~ radio ~ channel ~ radio ~ modem] -KISS- BPQ-B -AGW- DAPPS B
```

Frames take real airtime, with TX delay, turnarounds and a shared channel. The radios are half duplex: while one transmits it hears nothing, so when both ends transmit at once, both transmissions are lost. The image is pinned by digest in `NetSimFixtures.cs` (net-sim v0.4.0, pdn-soundmodem 0.80.0); CI pulls whatever that pins.

`ChannelLog` records when each radio is on air, to about 10 ms, from what each simulated receiver hears. On v0.4.0 that is still real airtime: a single 136-byte frame at 1200 baud with 150 ms of TX delay shows as 1.20 s, which is its 0.96 s of data, the TX delay and the TNC's 0.1 s tail. The reports now list every transmission, with the quiet before it or how far it overlapped the other side's.

The links:

- **AFSK 1200** runs on Dire Wolf with the squelch open, as 1200 baud stations usually do: the TNC's own carrier detect decides when the channel is busy. Dire Wolf's looks for AFSK, not audio, so hiss doesn't hold it off: on an idle open-squelch channel a frame starts 21 ms after it reaches the TNC, every time. Not samoyed: on v0.4.0 samoyed's transmit audio reaches the simulator over UDP faster than the simulator reads it, and everything after about the first 2 s of a transmission is dropped (the socket's drop counter shows it). Two full I-frames in one transmission lost the second every time, so BPQ's bursts at 1200 baud stalled after two frames. Dire Wolf's audio goes through a pipe, and four full frames in one transmission all arrived.
- **QPSK 3600** (pdn-soundmodem, 7200 bps) runs on a wide (25 kHz) channel with the squelch closed (`radio: { channel: wide, squelch: hard }`), as net-sim's docs recommend: on an open-squelch radio pdn's qpsk receiver loses frames.
- Clean links are 120 dB of path loss. The soak's is 156.75 dB (below).

### Kevin's WPS trace

`WpsReplicationScenarioTests` replays the WPS replication exchange Kevin M0AHN captured for #187: four posts from each end, half a second apart, the second end starting four seconds after the first, with cumulative acks five seconds after the first unacked post. Before 0.40.0 that exchange took 77 s, 6 connections and about 150 frames at 1200 baud.

It runs on AFSK 1200 (Dire Wolf) and QPSK 3600 (pdn-soundmodem, 7200 bps). It checks every message arrives exactly once, on at most two connections (one in every run on v0.4.0; the second allows for a lost SABM or UA), within 8 frames a message and 75 s at AFSK 1200, and 7 frames a message and 45 s at QPSK 3600: about two frames and twice the time more than the exchange takes there. Each run writes a report comparing itself with Kevin's numbers, with the channel timings, every transmission and the air transcript, to `scenario-reports/` beside the test build. CI keeps that folder as the `scenario-reports` artifact on every run.

The fixtures use the BPQ radio-port settings `docs/tune.md` recommends. To try others without a rebuild, set `DAPPS_NETSIM_RADIO`, e.g. `DAPPS_NETSIM_RADIO=PERSIST=255,SLOTTIME=10,MAXFRAME=7` (it also takes `ACKMODE=1`, for `KISSOPTIONS=ACKMODE`), and to try another path loss, `DAPPS_NETSIM_PATH_LOSS`, e.g. `DAPPS_NETSIM_PATH_LOSS=156`.

The fixture waits until each BPQ has been heard by the other before any test starts, and fails with a message saying which one wasn't heard if that takes over 2 minutes. Until its KISS link to the simulator is up, BPQ holds a connect request for about 8 s; a test's first call then went out late enough for the other side to dial as well, and the calls crossed (seen once in the WPS scenario at 1200 baud, with three SABMs for one link). Every node goes through that after a reboot, so one crossed-call test starts from cold BPQs on purpose (below).

### Crossed calls

`CrossedCallScenarioTests` has both daemons submit two messages to each other, one a random 0.3 to 2 s after the other, so both dial, in three rounds that each start from no link (no hold). Every message must arrive exactly once. At QPSK 3600 a round finishes within 30 s with at most two dials between the two nodes; at AFSK 1200, within 100 s and six dials.

The stagger is for a simulator artifact. Dire Wolf picks its transmit slot with `rand()` and nothing seeds it over KISS, so two simulated Dire Wolfs draw the same numbers, and two calls made in the same millisecond collided at every repeat until both retried out (about 1 round in 8 when both submitted at once: 3 to 5 dials and up to 90 s). Real TNCs don't share a random sequence; pdn-soundmodem seeds its own, and QPSK 3600 never locked up.

The looser AFSK limit is for something real. With the stagger, the second node's call can land just as the first node's call connects, over a link the first node has already heard on, and BPQ resets it (the second case in the experiments below). Recovering takes a cooldown, a prompt wait, the 30 s silent-peer wait and a second crossing: 5 dials and about 70 s, in 2 of 8 rounds at 1200 baud on v0.4.0, and never at QPSK 3600. Other rounds took 1 or 2 dials and 10 to 19 s. No session in a round of one or two dials may fall back to waiting 10 s for a prompt, and in a round where both nodes dialled, one of them must have spotted it (the calls crossed, or its call went over a link that was already up). A second test restarts both BPQs, as after a reboot, starts the daemons straight away and does one such round, within 30 s more. The reports go to `scenario-reports/crossed-*.md` and `crossed-cold-*.md`. What's bounded is dials, not SABMs: BPQ sends two to four SABMs to set up one crossed link.

`BpqCrossedCallExperiments` drives BPQ's AGW interface by hand, with no DAPPS, to see what BPQ does when calls cross. They only run when `DAPPS_BPQ_EXPERIMENTS` is set, and write what every AGW socket and the air saw to `scenario-reports/bpq-*.md`. Run one at a time (`--filter-method`), as each leaves the link in its own state. What they showed (linbpq image below, QPSK 3600, and AFSK 1200 for the second):

- **B calls A over a link A made, before anything has gone over it** (the #194 sighting): B's listener has A's call; B then calls A. BPQ-B sends a SABM on the live link and confirms B's call ("*** CONNECTED With Station"). BPQ-A has had no I-frame on that link yet, so it takes the SABM for a repeat of its own call's and just answers UA again: A's call stays up. At B, A's data now arrives only on B's new socket. B's listener gets no 'd' and can still send: its data reaches A's call. A 'd' from either B socket disconnects the link, and both B sockets then get a 'd'.
- **The same once A has heard from B's listener**, as it always has with DAPPS (the answering end sends its prompt at once): BPQ-A takes the SABM as a link reset. A's call gets a 'd' ("*** DISCONNECTED From Station"), A's listener gets nothing, and the link is left attached to nothing at A. B's call is confirmed as usual, but what it sends next reaches A's node command prompt: `exchange ...` gets back `AAA:N0AAA} Invalid command - Enter ? for command list`, and a line starting with B is taken for BYE and ends the link. (L2Code.c, "SABM ON EXISTING SESSION", and the SESSACTIVE flag set by the first I-frame.)
- **Both calls at once** (or the second 0.3 s after the first): neither listener gets a connect. Both callers are confirmed with the usual "*** CONNECTED With Station", indistinguishable from an ordinary call, and one link carries data both ways between the two callers' sockets. Setting it up took three or four SABMs and as many UAs.
- Each node's monitor does show the other's SABM arriving (BPQ writes a SABM as ` 1:Fm B To A <C C P>`), which is how a caller tells its call crossed. A SABM through a digipeater (`Via ...`) or a SABME (`<?? C P>`) prints differently; those crossings fall back to the 10 s wait.

So DAPPS keeps one session per peer and AGW port at a node: the newest connected one has the link, and an older one is retired without sending a 'd' or anything else (`PeerSessionRegistry`). A caller sends its `exchange` at once, instead of waiting 10 s for a prompt, when it sees the peer's SABM while its own call is on the way, when its call retired an older session (the link was already up, so nothing at the far end will prompt), or when it hears exchange traffic before any prompt. If its `exchange` gets only other text back for 10 s, as at a node's command prompt, it gives up and hangs up rather than waiting 3 minutes for silence; both ends then redial after their first short cooldown. If it gets nothing at all back for 30 s (three prompt waits), it hangs up too (below, "A redial loop at the edge"). A call's 'd' is never sent once the node has already disconnected it. Retirement is AGW only: RHPv2 hasn't been measured, so its sessions neither retire nor are retired.

### The soak

`NetSimSoakTests` is a long run on a marginal AFSK 1200 link, where frames are lost and retried: 156.5 dB of path loss, just above the FM threshold, where 13% of full-length frames (136 bytes) and 1% of short ones are lost. That was measured over KISS between two Dire Wolfs on the pinned image, 100 frames of each size; the edge is steep (5% at 156 dB, 18% at 156.75, 40% at 157). BPQ there has 300 ms of TX delay: a receiver that has just stopped transmitting needs the longer preamble to catch what comes straight back. Both ends send random bursts, with a few messages of 2 to 5 KB. A third of the way in, net-sim stops for a minute (both modems off, so BPQ loses its KISS link too); two thirds in, B's daemon restarts as for an upgrade. Then traffic stops and the test waits for the queues to drain. Every message must arrive exactly once, byte for byte.

It only runs when `DAPPS_SOAK_MINUTES` is set, so CI skips it:

```
DAPPS_SOAK_MINUTES=30 src/dapps/dapps.core.tests/bin/Debug/net10.0/dapps.core.tests --filter-class dapps.core.tests.Integration.NetSimSoakTests
```

`DAPPS_SOAK_SEED` picks the traffic (default 187). The report, the air transcript, every transmission (`soak-channel.txt`) and both daemons' logs go to `scenario-reports/`, and `soak-progress.log` there shows progress while it runs.

### What the physical channel showed

Phase 3 of `docs-internal/exchange-plan.md`, on net-sim v0.4.0; the numbers are in the plan's "Results".

- **BPQ's FRACK decided most of it.** BPQ starts FRACK when it hands a burst to the TNC, not when the burst has gone (`KISSOPTIONS=ACKMODE` changes that where the TNC supports it; pdn-soundmodem does, and with FRACK long enough it made no difference). At 1200 baud four full frames take about 4 s, so with FRACK 3000 BPQ polled into the far end's answer: both lost, and a stall of a few seconds each time. FRACK 7000 took the WPS scenario from 52-65 s to 38-39 s with no collisions at all (with PERSIST 255; see below). QPSK 3600 had the same problem with FRACK 2000 (BPQ resent whole bursts); 4000 fixed it. `docs/tune.md` has the rule.
- **The edge of range.** A receiver that has just stopped transmitting misses the start of what comes straight back: at 156.5 dB the first frame of a burst sent straight after hearing the other end was lost 32% of the time, against 8% on an idle channel, with 300 ms of TX delay; with 150 ms it was worse. BPQ resends everything from a lost frame, so every change of direction is expensive there.
- **Both ends streaming at once breaks BPQ links at the edge.** A version of the exchange that wrote its answers and new mail while a long message was still arriving (so they rode the RR its node sends after each of the peer's bursts) was a little quicker on a clean link, but on the soak link it had both nodes streaming I-frames at each other, and BPQ dropped the link every minute or two: 12 of 62 messages in 32 minutes, against 62 of 62 in 19 minutes without it. It was taken out.
- **Answer timeouts, and stuck links.** A message failed 3 minutes after it went, whether or not the link was still moving; on the soak link a full window can take longer than that to go on air, and the peer's answers can queue behind its own mail for longer still. Now a session gives up only when, with ours waiting, neither answers nor any other DAPPS traffic from the peer have come for 3 minutes: then it ends at either end, and the oldest message fails, for one cooldown. At 156.75 dB BPQ resent the same frames for 11 minutes on a link that stayed up (the first frame of every resent burst was lost, and REJ recovery doesn't count against RETRIES), and nothing reached DAPPS at all. An earlier version counted only answers, and in 4 soaks it ended 5 sessions whose peers were still sending: both ends then sent everything again. Could a session see such a loop sooner (#206)? BPQ answers the AGW `Y` query (not `y`, which it doesn't implement) with the frames it holds for one connection, waiting or sent and not yet acknowledged, so a count that never goes down means nothing is being acknowledged. In the air transcripts of 23 soaks at 156.5 and 156.75 dB, a side with frames outstanding always had one acknowledged within 59 s on a link that stayed up (one such stretch was 14 resent frames, then it recovered), so a 90 s limit on `Y` would have ended no healthy session. But no resend loop turned up in any of them to catch, and the 3-minute rule hasn't fired in the 8 soaks since it went in, so DAPPS doesn't ask for `Y` yet. TXDELAY 500 at 156.75 dB, one soak against one with 300: the first frame resent after a REJ(F) was lost 32% of the time against 36%, and 62 against 45 of 64 messages arrived, inside the spread of the earlier 156.75 dB soaks (12 to 61). Not enough to change `docs/tune.md`.
- **Cooldowns climb when a link keeps breaking.** Each break fails one message, and the neighbour's cooldown steps up (10 s, 30 s, 1 min, then 5 min, each up to half as long again) until a message gets through. In the soaks where links broke every minute or two, each node spent 12 to 25 of the 32 minutes with mail queued and its neighbour in cooldown, and the channel was idle two thirds of the time. The spread itself (a few seconds a break) was not what cost; the breaks were.
- **A redial loop at the edge.** In one soak of the phase 2 code (18 of 62 delivered) and one of three of the phase 3 code before the fix below (21 of 64), the two nodes spent the last 20 minutes redialling each other. A call would get its UA but no prompt: the far BPQ, which had just had its own call reset by this one, left the link attached to nothing. The caller sent its `exchange` after 10 s, heard nothing at all back, and waited for up to 3 minutes, until the other node's next call reset the link, landed on the same kind of dead link, and waited in turn. Each failure set the next dial a cooldown after the other node's, so they stayed interleaved while the cooldowns climbed to minutes. A caller that hears nothing at all for 30 s after its `exchange` now hangs up, which clears the link at both nodes before the other node dials again. In six soaks since, it fired once or twice a soak; in one the loop still formed, for 6 minutes, as the other node's next call came inside the 30 s. It is an open item in the plan.
- **The same messages handed to a session again and again.** Every forwarder run (every 30 s at least) handed an open session the queued messages it hadn't taken yet, so when the session ended each was deferred once per copy: 400 to 900 deferrals in a soak. A message waiting in a session's queue is now left alone for 10 minutes; the soaks since had 80 to 110.
- **Calls that start together collide.** The radios are half duplex, so when both nodes dial at the same moment their SABMs are both lost, and with PERSIST 255 BPQ resends both a FRACK later, still together, until they retry out. In the crossed-call scenario, with both nodes submitting in the same millisecond, that cost 3 or 4 dials and 57 to 90 s in two of three rounds, with 20 transmissions overlapping. With PERSIST 64 and SLOTTIME 100 most rounds took 1 or 2 dials and 9 to 29 s; the rest were the Dire Wolf artifact or the link reset described above. The fixtures use 64 and 100 on both modems.
- **Stream corruption.** In one 156.75 dB soak, bytes arrived twice that the sender never sent in that place, inside one AX.25 connection with no link reset: a payload that didn't decompress followed by a line starting `0AAAe 3b1f348 len=3113 ...` (where `ihave 3b1f348 ...` was sent), and later another payload that didn't decompress. The cause is almost certainly BPQ's receive side (L2Code.c, the N(S) check in `SDIFRM`, around lines 2585 to 2650 on the `patched` branch). BPQ keeps each out-of-sequence I-frame in a slot per N(S) (`RXFRAMES`), and when the next frame it needs is missing it takes whatever is in that slot, without checking that it belongs to the current turn of the modulo-8 sequence numbers. A copy of a frame it already has lands in a slot when the sender goes back after a stale REJ (a REJ queued in the TNC behind the burst that made it out of date), and stays there until a frame with that N(S) arrives in sequence. If that new frame is lost, BPQ hands DAPPS the old copy in its place, one sequence turn late. Replaying the air transcript through that logic (`docs-internal/tools/rxsim.py`) finds a stale copy taken at the exact second of both corruptions (18:26:55 and 18:28:46), and nowhere else in the three 156.75 dB soaks. In the first case the stale frame ended in `src=N0AAA`, the same length as the frame it replaced, so the payload read took all but its last four bytes and the next line began `0AAA` instead of `ihav`. DAPPS now ends the session at once, with a `quit`, when what arrives can't have been sent that way: a payload that doesn't decode or hash to its id, a line that isn't DAPPS, or an offer or answer without an id. Nothing counts as failed, and everything goes on the next link, which starts in step. A fault that comes back is bounded: the same message arriving damaged a second time is answered `bad` (a message whose id doesn't hash from its payload, or a path that isn't 8-bit clean, not a stale frame), and a second out-of-step ending in a row with one neighbour counts as a break. One 156.5 dB soak (f5-soak-1) had two more payloads that didn't decode, in one session; the replay finds no stale frame there, but it loses track of BPQ's state often enough in that run that this doesn't rule one out. No stream went out of step in the four soaks since (two at 156.75 dB).
- **samoyed on v0.4.0** loses everything after about the first 2 s of a transmission (see the start of this section), so the AFSK fixtures use Dire Wolf.

## Not covered yet

- The link reset with DAPPS at both ends (one node's call goes over a link the other has already heard from): the experiment shows what BPQ does and the unit tests cover each side's part, but no scenario forces the timing. The cold-start round can hit it.
- Relaying and floods across three or more nodes: the fixture has two BPQs.
- XRouter and the RHPv2 bearer.
- The MQTT app interface.
- The UDP and MeshCore bearers, which use the binary datagram codec rather than DAPPSv1 sessions.
- A daemon killed mid-transfer: the soak only restarts one cleanly.

## The linbpq image

`m0lte/linbpq:latest` is built from the `patched` branch of M0LTE/linbpq by its `docker-publish` workflow. It was last built on 26 May 2026, before John's 6.0.25.40.
