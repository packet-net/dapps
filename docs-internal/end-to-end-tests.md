# End-to-end tests

`dapps.core.tests/Integration/DappsEndToEndTests.cs` runs the DAPPSv1 protocol for real: two DAPPS daemons, each in its own process on its own linbpq node, the nodes linked over AXIP. The same cases also run on pdn nodes and on a pdn node paired with a BPQ one ([below](#on-pdn-packetnet)).

```
app -> DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B -> app
```

- The daemons are the normal `dapps.core` build, configured with the usual `DAPPS_*` environment variables and a fresh database each (`DappsDaemon`). Tests drive them through the app API, so nothing pokes the forwarder by hand.
- `AirMonitor` records what went over the air from BPQ's own AGW monitor on both nodes. A node's monitor only reports what it receives, so each node shows the other one's transmissions.
- `RawAgwCaller` talks DAPPSv1 by hand over the air, to check the server's replies one at a time.

## Running them

They need Docker (Testcontainers starts `m0lte/linbpq:latest`, and for the pdn tests the pinned `ghcr.io/packet-net/packet.net` image) and a built solution:

```
dotnet build src/dapps/dapps.sln
src/dapps/dapps.core.tests/bin/Debug/net10.0/dapps.core.tests --filter-class dapps.core.tests.Integration.DappsEndToEndTests
```

Each test starts its own daemons, so the class takes a few minutes. CI runs them with the rest of the integration tests.

The cases that don't depend on BPQ live in `DappsExchangeTests`, which takes any pair of nodes (`IDappsNodePair`); `DappsEndToEndTests` adds the two that do (the bare AGW caller and the connect script). The crossed-call, WPS and soak scenarios likewise take a pair (`IDappsScenarioBed`, `NetSimTwoNodeFixture`), BPQ or pdn. A failed exchange test leaves the air transcript and both daemons' logs in `scenario-reports/e2e-*.txt`.

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

So DAPPS keeps one session per peer and AGW port at a node: the newest connected one has the link, and an older one is retired without sending a 'd' or anything else (`PeerSessionRegistry`). A caller sends its `exchange` at once, instead of waiting 10 s for a prompt, when it sees the peer's SABM while its own call is on the way, when its call retired an older session (the link was already up, so nothing at the far end will prompt), or when it hears exchange traffic before any prompt. If its `exchange` gets only other text back for 10 s, as at a node's command prompt, it gives up and hangs up rather than waiting 3 minutes for silence; both ends then redial after their first short cooldown. If it gets nothing at all back for 30 s (three prompt waits), it hangs up too (below, "A redial loop at the edge"). A call's 'd' is never sent once the node has already disconnected it. Retirement is AGW only: over RHPv2 sessions neither retire nor are retired, and a crossing is never spotted (see the pdn section).

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
- **Answer timeouts, and stuck links.** A message failed 3 minutes after it went, whether or not the link was still moving; on the soak link a full window can take longer than that to go on air, and the peer's answers can queue behind its own mail for longer still. Now a session gives up only when, with ours waiting, neither answers nor any other DAPPS traffic from the peer have come for 3 minutes: then it ends at either end, and the oldest message fails, for one cooldown. At 156.75 dB BPQ resent the same frames for 11 minutes on a link that stayed up (the first frame of every resent burst was lost, and REJ recovery doesn't count against RETRIES), and nothing reached DAPPS at all. An earlier version counted only answers, and in 4 soaks it ended 5 sessions whose peers were still sending: both ends then sent everything again.
- **Cooldowns climb when a link keeps breaking.** Each break fails one message, and the neighbour's cooldown steps up (10 s, 30 s, 1 min, then 5 min, each up to half as long again) until a message gets through. In the soaks where links broke every minute or two, each node spent 12 to 25 of the 32 minutes with mail queued and its neighbour in cooldown, and the channel was idle two thirds of the time. The spread itself (a few seconds a break) was not what cost; the breaks were.
- **A redial loop at the edge.** In one soak of the phase 2 code (18 of 62 delivered) and one of three of the phase 3 code before the fix below (21 of 64), the two nodes spent the last 20 minutes redialling each other. A call would get its UA but no prompt: the far BPQ, which had just had its own call reset by this one, left the link attached to nothing. The caller sent its `exchange` after 10 s, heard nothing at all back, and waited for up to 3 minutes, until the other node's next call reset the link, landed on the same kind of dead link, and waited in turn. Each failure set the next dial a cooldown after the other node's, so they stayed interleaved while the cooldowns climbed to minutes. A caller that hears nothing at all for 30 s after its `exchange` now hangs up, which clears the link at both nodes before the other node dials again. In six soaks since, it fired once or twice a soak; in one the loop still formed, for 6 minutes, as the other node's next call came inside the 30 s. It is an open item in the plan.
- **The same messages handed to a session again and again.** Every forwarder run (every 30 s at least) handed an open session the queued messages it hadn't taken yet, so when the session ended each was deferred once per copy: 400 to 900 deferrals in a soak. A message waiting in a session's queue is now left alone for 10 minutes; the soaks since had 80 to 110.
- **Calls that start together collide.** The radios are half duplex, so when both nodes dial at the same moment their SABMs are both lost, and with PERSIST 255 BPQ resends both a FRACK later, still together, until they retry out. In the crossed-call scenario, with both nodes submitting in the same millisecond, that cost 3 or 4 dials and 57 to 90 s in two of three rounds, with 20 transmissions overlapping. With PERSIST 64 and SLOTTIME 100 most rounds took 1 or 2 dials and 9 to 29 s; the rest were the Dire Wolf artifact or the link reset described above. The fixtures use 64 and 100 on both modems.
- **Stream corruption.** Twice in the 156.75 dB soaks, bytes arrived that the sender never sent in that place (a line starting `0AAAe 3b1f348 len=3113 ...`, a payload that didn't decompress), inside one AX.25 connection with no link reset. Most likely a stale resent frame accepted after the sequence numbers wrapped (modulo 8, with frames queued in the TNC for seconds). The hash check caught the payload; the garbled line left that session out of step until it ended.
- **samoyed on v0.4.0** loses everything after about the first 2 s of a transmission (see the start of this section), so the AFSK fixtures use Dire Wolf.

## On pdn (packet.net)

The same tests run with pdn, packet.net's node, in place of BPQ. DAPPS attaches over RHPv2 (`DAPPS_NODE_BEARER=rhpv2`), as it does when it runs as a pdn app. The image is `ghcr.io/packet-net/packet.net`, pinned by digest in `PdnFixtures.cs` (node-v0.55.2); CI pulls whatever that pins. Each node's config is seeded from `/etc/packetnet/packetnet.yaml` on first boot: one port, the RHPv2 server on 0.0.0.0 (a container needs that; pdn's default is loopback), the panel's login off, telnet off. NET/ROM broadcasts and ID beacons are off by default, so only DAPPS's traffic goes on air. The air record is each node's frame feed (`/api/v1/events`), received frames only, written out in BPQ's monitor style so the same assertions read both.

```
app -> DAPPS A -RHPv2- pdn-A -AXUDP- pdn-B -RHPv2- DAPPS B -> app
```

| Class | Nodes | Link |
|---|---|---|
| `PdnEndToEndTests` | two pdn | AXUDP |
| `PdnCrossedCallAxudpTests` | two pdn | AXUDP, the submits 0 to 50 ms apart |
| `PdnBpqEndToEndTests`, `BpqPdnEndToEndTests` | pdn and BPQ: pdn calls, then BPQ calls | pdn's AXUDP port to BPQ's AXIP port |
| `WpsReplicationScenarioPdn*`, `CrossedCallScenarioPdn*` | two pdn | net-sim, AFSK 1200 and QPSK 3600 |
| `NetSimPdnSoakTests` | two pdn | net-sim, the soak's marginal AFSK 1200 link |

They run like the others (`--filter-class dapps.core.tests.Integration.PdnEndToEndTests`), and the pdn soak like the BPQ one, with `DAPPS_SOAK_MINUTES`. Their reports carry `pdn` in the name: `wps-pdn-afsk1200.md`, `crossed-pdn-axudp.md`, `soak-pdn.md` and so on.

Between two pdn nodes the links are AX.25 v2.2: pdn sends an XID, then a SABME, and runs modulo 128. To BPQ the pdn port dials plain v2.0 (`link: dial: v20`), as pdn's docs say for a BPQ neighbour.

Two workarounds for pdn bugs, both in the fixtures:

- Each pdn port is named `1`. DAPPS asks RHPv2 for a port by number, as XRouter numbers them; pdn since its #668 only takes a port's id, so every open failed with errCode 10 (packet.net#841). This also stops DAPPS running as a pdn app from dialling out, unless the node's first port happens to be called `1`.
- BPQ's AXIP port maps only DAPPS's callsign on pdn. With pdn's node callsign mapped to the same address as well, BPQ sends every frame twice, and pdn takes the second UA as a protocol error and resets the link, again and again (packet.net#842).

### pdn's radio-port settings

On net-sim the pdn ports are set as the BPQ fixtures' are, so the two compare:

| pdn setting | AFSK 1200 | QPSK 3600 | Why |
|---|---|---|---|
| `ax25.t1Ms` | 7000 | 4000 | BPQ's `FRACK` |
| `ax25.t2Ms` | 1000 | 1000 | BPQ's `RESPTIME`; pdn's own default, 3000, holds every RR back 3 s |
| `ax25.n2` | 10 | 10 | BPQ's `RETRIES` |
| `ax25.windowSize`, `ax25.n1` | 4, 120 | 7, 236 | BPQ's `MAXFRAME`, `PACLEN` |
| `kiss.txDelay`, `persistence`, `slotTime` | 15, 64, 10 | 15, 64, 10 | BPQ's `TXDELAY`, `PERSIST`, `SLOTTIME` (the soak has 30, as BPQ's has 300 ms) |
| `kiss.txTail` | 10 | 2 | pdn always sends a TX tail, 0 unless set; BPQ sends none, so the BPQ runs had the TNC's own: 100 ms on Dire Wolf, 20 ms on pdn-soundmodem |

No ACKMODE: Dire Wolf doesn't do it, and the BPQ runs didn't use it at QPSK. Over AXUDP the ports run pdn's defaults. `DAPPS_NETSIM_PDN_RADIO` tries other settings without a rebuild, e.g. `T1=10000,WINDOW=2,ACKMODE=1,T1FROMTX=1`, and `DAPPS_PDN_RADIO` does the same for the pdn-and-BPQ port (e.g. `DIAL=auto`).

### pdn against BPQ

On the same net-sim image. BPQ's numbers are phase 3's in `docs-internal/exchange-plan.md` "Results"; medians with ranges.

Kevin's WPS replication:

| | BPQ (4 runs) | pdn (4 runs) |
|---|---|---|
| AFSK 1200: first post to last ack | 44 s (42-46) | 47.5 s (47.5-47.7) |
| AFSK 1200: connections | 1 | 2 |
| AFSK 1200: frames | 63 (60-65) | 57 |
| AFSK 1200: transmissions | 26 (25-28) | 14 |
| AFSK 1200: post delivered, median | 20 s (19-21) | 27 s |
| QPSK 3600: first post to last ack | 22 s (22-27) | 29 s (18-34) |
| QPSK 3600: connections | | 2 (1-3) |
| QPSK 3600: frames | 36 (35-37) | 30 (25-37) |
| QPSK 3600: transmissions | 18 (16-19) | 16 (12-20) |
| QPSK 3600: post delivered, median | 10 s (10-12) | 21 s (8.5-22) |

pdn is steadier (its four AFSK runs are frame for frame the same), fits more into each transmission, and loses about 10 s to one thing: both nodes dial, in every AFSK run and three of the four at QPSK (below). The one QPSK run where only one node dialled took 18.4 s, with posts delivered in 8.5 s, quicker than any BPQ run.

Crossed calls, 3 runs of 3 rounds each, plus each run's cold round:

| | BPQ | pdn |
|---|---|---|
| AFSK 1200 | usually 1 or 2 dials and 14 to 19 s; 2 rounds of 8 took 5 dials and about 70 s (BPQ's link reset) | always 2 dials, 21 to 27 s; cold rounds 26 s |
| QPSK 3600 | 1 or 2 dials, 10 to 19 s | always 2 dials, 17 to 29 s; cold rounds 20 s |

On pdn the two calls always make one link, never a reset; every round pays the 10 s prompt wait instead. Over AXUDP the first round took one dial and 3 s (the other node's mail went on the first link), later rounds 2 dials and 10.5 s.

The soak, 12 minutes of the same traffic (seed 187) on the same link:

| | BPQ, phase 3 (2 runs) | pdn (2 runs) |
|---|---|---|
| Delivered | all, both runs | 63 of 64 before the drain ran out; all |
| Short messages, median delivery | 418 s, 336 s | 71 s, 46 s |
| Long messages, median delivery | 1186 s, 633 s | 487 s, 213 s |
| Total, including the drain | 32.5, 22.1 min | 32.6 min (gave up), 15.6 min |
| Connections | 26, 15 | 12, 3 |

No duplicates or corrupt messages in either pdn run. pdn's links are v2.2, and it recovered lost frames with selective rejects (154 and 101 SREJ, against 8 and 3 REJ), resending only what was lost, where BPQ resends everything from the lost frame on; that is the likely reason its links held (12 and 3 connections, against 26 and 15) and short messages went so much quicker. In the first, the one message left was a 3 KB one on its second try (the offer accepted) when the drain ran out. That run's biggest single delay, 7 minutes, came from the teardown race below (packet.net#844); the rest were sessions that stalled at the edge of range, as BPQ's did. In the second, B's daemon restart and the channel outage each cost about a minute, and nothing else went wrong.

### What pdn showed

- **DAPPS lost the peer's prompt (fixed).** pdn answers an RHPv2 `open` once the far end's UA is in (its deviation D4; XRouter answers at once), so a quick peer's `DAPPSv1>` prompt can follow the open reply in the same read. RhpClient raised that `recv` before DAPPS had attached its handler, and the prompt was dropped: the caller waited 10 s, sent its exchange, heard nothing more (the peer had sent its rules already) and hung up 30 s later. Over AXUDP it broke about one test in four. `Rhpv2OutboundTransport` now listens from before the open.
- **Crossed calls over RHPv2 are handled, never spotted.** When both nodes dial, pdn makes one link of the two calls, as BPQ does: each node's `open` succeeds, neither listener gets an `accept`, so neither end sends a prompt. Over RHPv2 DAPPS has no monitor (pdn doesn't serve `trace` sockets), so it can't see the peer's SABM, and each end waits 10 s before sending its exchange. The crossed-call scenario only asks for crossings to be spotted over AGW. In the WPS scenario it happens nearly every time on pdn: pdn's XID before the SABME adds a turnaround, so A's call isn't up at B until after B's first post, 4 s in. So the pdn WPS classes allow 3 connections, not 2.
- **Who hangs up.** After `quit` and `bye` both ends let go; BPQ's caller usually sends the DISC first, but with pdn answering, pdn does. The exchange tests accept a clean hang-up from either end.
- **An open that races a teardown of the same link (packet.net#844).** In the first soak B's daemon restarted while holding A's call. The old handle's DISC waited for the channel, and the new daemon's `open` to A came in meanwhile: pdn failed it (errCode 15) but went on to connect anyway, so A's prompt arrived on a link no handle owned, and the pair spent 7 minutes on stalled sessions before a fresh call cleared it.
- **A DISC straight after the UA never reaches the open handle (packet.net#843).** Found by hand, not in a test: when the far node answers a call and hangs up at once (as pdn does for an app callsign nobody has bound), the caller's handle stays open, and DAPPS only gives up on its own timeouts.

## Not covered yet

- The link reset with DAPPS at both ends (one node's call goes over a link the other has already heard from): the experiment shows what BPQ does and the unit tests cover each side's part, but no scenario forces the timing. The cold-start round can hit it.
- Relaying and floods across three or more nodes: the fixture has two BPQs.
- XRouter. RHPv2 runs on pdn (above).
- A crossed call over RHPv2 being spotted rather than waited out: RHPv2 gives DAPPS no way to see it.
- The MQTT app interface.
- The UDP and MeshCore bearers, which use the binary datagram codec rather than DAPPSv1 sessions.
- A daemon killed mid-transfer: the soak only restarts one cleanly.

## The linbpq image

`m0lte/linbpq:latest` is built from the `patched` branch of M0LTE/linbpq by its `docker-publish` workflow. It was last built on 26 May 2026, before John's 6.0.25.40.
