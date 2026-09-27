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
| A message goes from one app to the other | `ihave`/`send`/`data`/`ack` on air, `src=`, TTL counting down, the `routes` pull and `rev` at the end of the session, a clean hang-up |
| A burst goes on one connection | Five queued messages, one connect |
| Mail the other node can't deliver comes back by `rev` | A node with no route to its peer hands its mail over on the peer's session |
| Payloads | A WPS post compressed with the dictionary (`fmt=z1`), a 9.6 KB message split into three compressed parts and reassembled, and 1.5 KB of random binary |
| A held link | Traffic both ways on one connection, `pending` from the far end |
| A quiet held link | `quit` and hang-up when the hold runs out, then a fresh connect |
| An ordered stream | `sid=`/`sn=` on air, all parts delivered |
| A probe | The `peers` exchange |
| The server's replies | Prompt, `help`, `error`, `send`/`ack`, `bad`, `peers`, `routes`, `tail`, `rev`, `pending` mid-session, and `eh?` then hang-up |
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

### The soak

`NetSimSoakTests` is a long run on a noisy AFSK 1200 channel, where frames are lost and retried. Both ends send random bursts, with a few messages of 2 to 5 KB. A third of the way in, net-sim stops for a minute (both modems off, so BPQ loses its KISS link too); two thirds in, B's daemon restarts as for an upgrade. Then traffic stops and the test waits for the queues to drain. Every message must arrive exactly once, byte for byte.

It only runs when `DAPPS_SOAK_MINUTES` is set, so CI skips it:

```
DAPPS_SOAK_MINUTES=30 src/dapps/dapps.core.tests/bin/Debug/net10.0/dapps.core.tests --filter-class dapps.core.tests.Integration.NetSimSoakTests
```

`DAPPS_SOAK_SEED` picks the traffic (default 187). The report, the air transcript and both daemons' logs go to `scenario-reports/`, and `soak-progress.log` there shows progress while it runs.

## Not covered yet

- Crossed connects: both nodes dialling within one round trip can't be forced.
- Relaying and floods across three or more nodes: the fixture has two BPQs.
- XRouter and the RHPv2 bearer.
- The MQTT app interface.
- The UDP and MeshCore bearers, which use the binary datagram codec rather than DAPPSv1 sessions.
- A daemon killed mid-transfer: the soak only restarts one cleanly.

## The linbpq image

`m0lte/linbpq:latest` is built from the `patched` branch of M0LTE/linbpq by its `docker-publish` workflow. It was last built on 26 May 2026, before John's 6.0.25.40.
