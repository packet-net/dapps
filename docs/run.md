# Run

Once installed and configured, DAPPS is a long-running daemon. This page describes what it actually *does* in the background - useful for understanding what you're seeing in the logs, in the dashboard, and on the air.

## Background loops

A running DAPPS node has several independent loops, each with a clear job:

| Loop                       | Cadence                                                | Job                                                                                                         |
|----------------------------|--------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| **Outbound forwarder**     | When a message is queued or a neighbour's session ends, else every 30 s (after a 3 s startup grace) | Walk the messages table for anything that needs forwarding to a neighbour. Open a session, ship it, mark forwarded. |
| **TTL sweeper**            | Every 60 s                                             | Soft-delete messages and offers whose TTL has elapsed. Drop reasons recorded for the dashboard's "Recently dropped" panel. |
| **AGW inbound dispatcher** | Continuous (one TCP connection)                        | Listen on the AGW socket for inbound sessions matching our registered callsign. Hand each one to the DAPPS protocol parser. |
| **Probe scheduler**        | Every probe interval (24 h default), gated by strategy | Walk the probed-nodes table; for each row not opted out, open a connected-mode probe session. Records success/failure. Off by default. |
| **Beaconer**               | Per-channel cadence                                    | For each enabled discovery channel, transmit a beacon advertising our callsign + bearer hints + cost.        |
| **Solicitor**              | Per-channel scheduled or operator-triggered            | Transmit a solicit on a channel. Peers that hear it reply, and the replies land in the discovered-peers table. |
| **Poll sweeper**           | Every poll interval (6 h default), if enabled          | Call each neighbour so it can send anything it holds for us. Off by default.                                 |
| **Heartbeat publisher**    | Every 60 s (configurable, ≥ 10 s)                      | Publish an operational snapshot to MQTT topic `dapps/metrics/heartbeat`. On by default.                      |
| **Update checker**         | Every 6 h on an apt install, else every 1 h            | Check packet-net's apt repository (apt installs) or GitHub Releases (everything else). Surface "v0.X.Y available" on the dashboard and `/Update/status`. |

Most are turned on by default. The exceptions are **probing**, **scheduled polling**, and **discovery channels** - you opt in to those once you've thought about your bearer's airtime budget. See [Tune](tune.md).

## Watching it run

### Live log

```bash
sudo journalctl -u dapps.service -f
```

Standard log levels apply (`info` by default). Most operational events are at `info`; pre-handshake protocol events sit at `debug`.

### Dashboard

`http://<node>:5000/` after first-use setup. The Overview page shows packet-node reachability (probes the configured bearer port - AGW or RHPv2), the MQTT broker, whether transmitting is allowed, the running version, a forwards-per-minute activity chart, the recent decisions, queue counts, discovery airtime, anything that needs attention, and a quick-send form. The other pages (Messages, Topology, Diagnostics, Compose, Streams, Settings) are in the sidebar.

The Overview refreshes its live panels every 5 seconds without a full page reload.

### `/Messages` page

Tabs for the outbound queue, the local inbox, recently dropped messages, and live arrivals (`/Inbound` still redirects here). Live arrivals is an SSE-driven view of every message that arrives on this node, filterable by app, source callsign, or destination substring; click any row to inline-expand a payload preview. On the other three tabs, click a message id to do the same, including for dropped messages. Previews show text or hex, capped at 4 KiB, and stay open across the 5-second table refresh.

Useful when you're debugging "is my message arriving?" - gives you live confirmation independent of any application.

### `/Compose` page

A compose form for hand-crafting an outbound message - every operator-relevant field exposed (app, destination, payload, optional TTL). Submits via the same code path as a real app would. Useful for verifying a node-to-node link without writing an app first.

### `/Health` and `/Operational`

`/Health` returns a small JSON document: overall status plus whether the callsign is set, the packet node is reachable and the MQTT broker is up. Designed for watchdog scrapers. Returns 200 when healthy, 503 when any of those three is not. Open access - no admin cookie required.

`/Operational` returns a much richer JSON document: process metrics (uptime, message counts, probe stats, AGW reconnect counts), the recent decision-events ring (with reasons), per-link state. This is what feeds the heartbeat too. Also open access.

Two of its fields describe the packet-node connection's retry state: `nodeReconnectAttempt` (consecutive failed connects, 0 while connected) and `nodeReconnectAt` (when the next automatic attempt is due, null when not waiting). Reconnects back off along a sliding scale, 10 s for the first three failures, then 30 s, then 1 minute, then every 5 minutes, and reset on the next successful connect. The dashboard's Packet node tile shows the countdown and a **Retry now** link; that link posts to `POST /Operational/retry-now`, which collapses the wait and, unlike the reads under `/Operational`, requires the admin login. Saving `/Config` also cuts the wait short, but only when the save changes the bearer, the callsign, the node host, or the bearer's port or login; any other save leaves the connection and its wait alone. To bounce a connection that is up, restart the service.

### MQTT heartbeat

Subscribers to `dapps/metrics/heartbeat` get the same operational snapshot as `/Operational`, every 60 s. Retained - late subscribers see the most recent snapshot immediately.

This is the easiest scraping target if you already have an MQTT collector wired into your monitoring stack.

## Stopping cleanly

`systemctl stop dapps.service` (Linux/systemd) or `Ctrl+C` (interactive). DAPPS uses the standard hosted-service shutdown path: in-flight outbound sessions are cancelled (the receiver will time them out per the inactivity-timeout contract), the database is flushed, the process exits.

There's no "drain mode" yet - a stop is a stop. Pending outbound messages stay in the queue and resume on the next start.

## Restart implications

Most knobs are runtime-changeable - change them on the dashboard's Settings page and the change takes effect within a few seconds. A callsign or packet-node change reconnects to the node straight away. The exceptions, flagged on the Settings page where they appear, need a restart:

- **MQTT port / UDP listen port** - bound at startup.
- **MeshCore enable, port and region** - the radio is set up at startup.
- **Routing algorithm** - switching between `passive-flood` and `meshcore` rebuilds the routing graph from scratch, which is safer to do at a clean restart than mid-run.

Everything else (probing on/off, fragment threshold, airtime budget, heartbeat cadence, etc.) lives loose in the running daemon and picks up changes within a few seconds.

## Process exit codes

| Exit code | Meaning                                                                                                       |
|-----------|---------------------------------------------------------------------------------------------------------------|
| `0`       | Normal shutdown.                                                                                              |
| `78`      | Operationally-fatal config error (e.g. MQTT port already in use). Paired with systemd `RestartPreventExitStatus=78` so we don't hot-loop on a problem that won't fix itself. |
| Other     | Unexpected. Check the journal for the exception detail.                                                        |
