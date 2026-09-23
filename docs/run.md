# Run

Once installed and configured, DAPPS is a long-running daemon. This page describes what it actually *does* in the background - useful for understanding what you're seeing in the logs, in the dashboard, and on the air.

## Background loops

A running DAPPS node has several independent loops, each with a clear job:

| Loop                       | Cadence                                                | Job                                                                                                         |
|----------------------------|--------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| **Outbound forwarder**     | Every 5 s (after a 3 s startup grace)                  | Walk the messages table for anything that needs forwarding to a neighbour. Open a session, ship it, mark forwarded. |
| **TTL sweeper**            | Every 60 s                                             | Soft-delete messages and offers whose TTL has elapsed. Drop reasons recorded for the dashboard's "Recently dropped" panel. |
| **AGW inbound dispatcher** | Continuous (one TCP connection)                        | Listen on the AGW socket for inbound sessions matching our registered callsign. Hand each one to the DAPPS protocol parser. |
| **Probe scheduler**        | Every probe interval (24 h default), gated by strategy | Walk the probed-nodes table; for each row not opted out, open a connected-mode probe session. Records success/failure. Off by default. |
| **Beaconer**               | Per-channel cadence                                    | For each enabled discovery channel, transmit a beacon advertising our callsign + bearer hints + cost.        |
| **Solicitor**              | Per-channel scheduled or operator-triggered            | Transmit a solicit on a channel; collect replies for `solicit-window-seconds`.                              |
| **Reverse-poll sweeper**   | Every poll interval (6 h default), if enabled          | For each known forward target, request anything they hold for us via `rev`. Off by default.                |
| **Heartbeat publisher**    | Every 60 s (configurable, ≥ 10 s)                      | Publish an operational snapshot to MQTT topic `dapps/metrics/heartbeat`. On by default.                      |
| **Update checker**         | Every 1 h                                              | Poll GitHub Releases. Surface "v0.X.Y available" on the dashboard / heartbeat / `/Operational`.              |

Most are turned on by default. The exceptions are **probing**, **scheduled polling**, and **discovery channels** - you opt in to those once you've thought about your bearer's airtime budget. See [Tune](tune.md).

## Watching it run

### Live log

```bash
sudo journalctl -u dapps.service -f
```

Standard log levels apply (`info` by default). Most operational events are at `info`; pre-handshake protocol events sit at `debug`.

### Dashboard

`http://<node>:5000/` after first-use setup. The home page is a single dense view: callsign + version, packet-node reachability (probes the configured bearer port - AGW or RHPv2), MQTT broker / UDP listener status, queue depths, neighbours, recent messages, recent dropped, recent activity (the decision-events ring), and the Live inbound feed.

The dashboard auto-refreshes the queue snapshot panels every 5 seconds without a full page reload.

### `/Inbound` page

A dedicated SSE-driven view of every message that arrives on this node. Filter by app, source callsign, or destination substring. Click any row to inline-expand a payload preview (text or hex; capped at 4 KiB).

Useful when you're debugging "is my message arriving?" - gives you live confirmation independent of any application.

### `/IHave` page

A compose form for hand-crafting an outbound message - every operator-relevant field exposed (app, destination, payload, optional TTL). Submits via the same code path as a real app would. Useful for verifying a node-to-node link without writing an app first.

### `/Health` and `/Operational`

`/Health` returns a small JSON document with overall up/down + per-component status. Designed for watchdog scrapers. Returns 200 when healthy, 503 when at least one component is unhealthy. Open access - no admin cookie required.

`/Operational` returns a much richer JSON document: process metrics (uptime, message counts, probe stats, AGW reconnect counts), the recent decision-events ring (with reasons), per-link state. This is what feeds the heartbeat too. Also open access.

Two of its fields describe the packet-node connection's retry state: `nodeReconnectAttempt` (consecutive failed connects, 0 while connected) and `nodeReconnectAt` (when the next automatic attempt is due, null when not waiting). Reconnects back off along a sliding scale, 10 s for the first three failures, then 30 s, then 1 minute, then every 5 minutes, and reset on the next successful connect. The dashboard's Packet node tile shows the countdown and a **Retry now** link; that link posts to `POST /Operational/retry-now`, which collapses the wait and, unlike the reads under `/Operational`, requires the admin login. Saving `/Config` also cuts the wait short.

### MQTT heartbeat

Subscribers to `dapps/metrics/heartbeat` get the same operational snapshot as `/Operational`, every 60 s. Retained - late subscribers see the most recent snapshot immediately.

This is the easiest scraping target if you already have an MQTT collector wired into your monitoring stack.

## Stopping cleanly

`systemctl stop dapps.service` (Linux/systemd) or `Ctrl+C` (interactive). DAPPS uses the standard hosted-service shutdown path: in-flight outbound sessions are cancelled (the receiver will time them out per the inactivity-timeout contract), the database is flushed, the process exits.

There's no "drain mode" yet - a stop is a stop. Pending outbound messages stay in the queue and resume on the next start.

## Restart implications

Most knobs are runtime-changeable - flip them in the dashboard's `/Config` form and the change takes effect immediately. The exceptions are flagged on the form; they require a restart:

- **Callsign** - the AGW registration uses it, and changing it would orphan in-flight sessions.
- **Node host / AGW port / MQTT port / UDP listen port** - bound at startup.
- **Routing algorithm** - switching between `passive-flood` and `meshcore` rebuilds the routing graph from scratch, which is safer to do at a clean restart than mid-run.

Everything else (probing on/off, fragment threshold, airtime budget, heartbeat cadence, etc.) lives loose in the running daemon and picks up changes within a few seconds.

## Process exit codes

| Exit code | Meaning                                                                                                       |
|-----------|---------------------------------------------------------------------------------------------------------------|
| `0`       | Normal shutdown.                                                                                              |
| `78`      | Operationally-fatal config error (e.g. MQTT port already in use). Paired with systemd `RestartPreventExitStatus=78` so we don't hot-loop on a problem that won't fix itself. |
| Other     | Unexpected. Check the journal for the exception detail.                                                        |
