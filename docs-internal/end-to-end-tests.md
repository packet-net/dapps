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

## Not covered yet

- Crossed connects: both nodes dialling within one round trip can't be forced.
- Relaying and floods across three or more nodes: the fixture has two BPQs.
- XRouter and the RHPv2 bearer.
- The MQTT app interface.
- The UDP and MeshCore bearers, which use the binary datagram codec rather than DAPPSv1 sessions.

## The linbpq image

`m0lte/linbpq:latest` is built from the `patched` branch of M0LTE/linbpq by its `docker-publish` workflow. It was last built on 26 May 2026, before John's 6.0.25.40.
