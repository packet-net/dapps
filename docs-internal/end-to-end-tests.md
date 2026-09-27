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

The tests above use AXIP, so frames cost no airtime. `NetSimTwoBpqFixture` puts the two BPQs on a simulated radio channel instead: [net-sim](https://github.com/packet-net/net-sim) runs real modems and mixes their audio, and each BPQ attaches to one simulated radio over KISS, as it would to a real TNC.

```
DAPPS A -AGW- BPQ-A -KISS- [net-sim: modem ~ channel ~ modem] -KISS- BPQ-B -AGW- DAPPS B
```

Frames take real airtime, with TX delay, turnarounds and a shared channel. The image is pinned by digest in `NetSimFixtures.cs`; CI pulls whatever that pins. `ChannelLog` records when each radio is on air, to about 10 ms, from what each simulated receiver hears.

### Kevin's WPS trace

`WpsReplicationScenarioTests` replays the WPS replication exchange Kevin M0AHN captured for #187: four posts from each end, half a second apart, the second end starting four seconds after the first, with cumulative acks five seconds after the first unacked post. Before 0.40.0 that exchange took 77 s, 6 connections and about 150 frames at 1200 baud.

It runs on AFSK 1200 (samoyed) and QPSK 3600 (pdn-soundmodem, 7200 bps). It checks every message arrives exactly once, on at most two connections and at most 11 frames a message. Each run writes a report comparing itself with Kevin's numbers, with the channel timings and the air transcript, to `scenario-reports/` beside the test build. CI keeps that folder as the `scenario-reports` artifact on every run.

To try other BPQ radio settings without a rebuild, set `DAPPS_NETSIM_RADIO`, e.g. `DAPPS_NETSIM_RADIO=PERSIST=64,SLOTTIME=100,MAXFRAME=7`.

The fixture waits until each BPQ has been heard by the other before any test starts, and fails with a message saying which one wasn't heard if that takes over 2 minutes. Until its KISS link to the simulator is up, BPQ holds a connect request for about 8 s; a test's first call then went out late enough for the other side to dial as well, and the calls crossed (seen once in the WPS scenario at 1200 baud, with three SABMs for one link). Every node goes through that after a reboot, so one crossed-call test starts from cold BPQs on purpose (below).

### Crossed calls

`CrossedCallScenarioTests` has both daemons submit two messages to each other at the same moment, so both dial, in three rounds that each start from no link (no hold). Every message must arrive exactly once, with at most two dials a round between the two nodes, and a round finishes within 60 s at QPSK 3600 and 90 s at AFSK 1200. No session may fall back to waiting 10 s for a prompt, and in a round where both nodes dialled, one of them must have spotted it (the calls crossed, or its call went over a link that was already up). A second test restarts both BPQs, as after a reboot, starts the daemons straight away and does one such round, within 30 s more. The reports go to `scenario-reports/crossed-*.md` and `crossed-cold-*.md`. What's bounded is dials, not SABMs: BPQ sends two to four SABMs to set up one crossed link.

`BpqCrossedCallExperiments` drives BPQ's AGW interface by hand, with no DAPPS, to see what BPQ does when calls cross. They only run when `DAPPS_BPQ_EXPERIMENTS` is set, and write what every AGW socket and the air saw to `scenario-reports/bpq-*.md`. Run one at a time (`--filter-method`), as each leaves the link in its own state. What they showed (linbpq image below, QPSK 3600, and AFSK 1200 for the second):

- **B calls A over a link A made, before anything has gone over it** (the #194 sighting): B's listener has A's call; B then calls A. BPQ-B sends a SABM on the live link and confirms B's call ("*** CONNECTED With Station"). BPQ-A has had no I-frame on that link yet, so it takes the SABM for a repeat of its own call's and just answers UA again: A's call stays up. At B, A's data now arrives only on B's new socket. B's listener gets no 'd' and can still send: its data reaches A's call. A 'd' from either B socket disconnects the link, and both B sockets then get a 'd'.
- **The same once A has heard from B's listener**, as it always has with DAPPS (the answering end sends its prompt at once): BPQ-A takes the SABM as a link reset. A's call gets a 'd' ("*** DISCONNECTED From Station"), A's listener gets nothing, and the link is left attached to nothing at A. B's call is confirmed as usual, but what it sends next reaches A's node command prompt: `exchange ...` gets back `AAA:N0AAA} Invalid command - Enter ? for command list`, and a line starting with B is taken for BYE and ends the link. (L2Code.c, "SABM ON EXISTING SESSION", and the SESSACTIVE flag set by the first I-frame.)
- **Both calls at once** (or the second 0.3 s after the first): neither listener gets a connect. Both callers are confirmed with the usual "*** CONNECTED With Station", indistinguishable from an ordinary call, and one link carries data both ways between the two callers' sockets. Setting it up took three or four SABMs and as many UAs.
- Each node's monitor does show the other's SABM arriving (BPQ writes a SABM as ` 1:Fm B To A <C C P>`), which is how a caller tells its call crossed. A SABM through a digipeater (`Via ...`) or a SABME (`<?? C P>`) prints differently; those crossings fall back to the 10 s wait.

So DAPPS keeps one session per peer and AGW port at a node: the newest connected one has the link, and an older one is retired without sending a 'd' or anything else (`PeerSessionRegistry`). A caller sends its `exchange` at once, instead of waiting 10 s for a prompt, when it sees the peer's SABM while its own call is on the way, when its call retired an older session (the link was already up, so nothing at the far end will prompt), or when it hears exchange traffic before any prompt. If its `exchange` gets only other text back for 10 s, as at a node's command prompt, it gives up and hangs up rather than waiting 3 minutes for silence; both ends then redial after their first short cooldown. A call's 'd' is never sent once the node has already disconnected it. Retirement is AGW only: RHPv2 hasn't been measured, so its sessions neither retire nor are retired.

### The soak

`NetSimSoakTests` is a long run on a noisy AFSK 1200 channel, where frames are lost and retried. Both ends send random bursts, with a few messages of 2 to 5 KB. A third of the way in, net-sim stops for a minute (both modems off, so BPQ loses its KISS link too); two thirds in, B's daemon restarts as for an upgrade. Then traffic stops and the test waits for the queues to drain. Every message must arrive exactly once, byte for byte.

It only runs when `DAPPS_SOAK_MINUTES` is set, so CI skips it:

```
DAPPS_SOAK_MINUTES=30 src/dapps/dapps.core.tests/bin/Debug/net10.0/dapps.core.tests --filter-class dapps.core.tests.Integration.NetSimSoakTests
```

`DAPPS_SOAK_SEED` picks the traffic (default 187). The report, the air transcript and both daemons' logs go to `scenario-reports/`, and `soak-progress.log` there shows progress while it runs.

## Not covered yet

- The link reset with DAPPS at both ends (one node's call goes over a link the other has already heard from): the experiment shows what BPQ does and the unit tests cover each side's part, but no scenario forces the timing. The cold-start round can hit it.
- Relaying and floods across three or more nodes: the fixture has two BPQs.
- XRouter and the RHPv2 bearer.
- The MQTT app interface.
- The UDP and MeshCore bearers, which use the binary datagram codec rather than DAPPSv1 sessions.
- A daemon killed mid-transfer: the soak only restarts one cleanly.

## The linbpq image

`m0lte/linbpq:latest` is built from the `patched` branch of M0LTE/linbpq by its `docker-publish` workflow. It was last built on 26 May 2026, before John's 6.0.25.40.
