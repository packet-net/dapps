# Operate

The day-to-day surfaces of a running DAPPS node - what's there, what each one's for, and which to look at when.

## The dashboard

`http://<node>:5000/` after first-use setup. The sidebar has seven pages.

### Overview

The home page, refreshing its live panels every 5 seconds:

- **Status tiles** - packet-node reachability (probes the configured bearer port - AGW or RHPv2, with a **Retry now** link while it's reconnecting), the MQTT broker, whether transmitting is allowed or stopped, and the running build with **Check now**, which says when a newer version is out. See [Upgrade](update.md).
- **Activity** - forwards per minute since the page loaded.
- **Recent decisions** - the last 20 entries of the decision-events ring ("rejected forward, no route to X" etc.).
- **Outbound queue / Local inbox** - pending and undelivered counts, with links to the Messages tabs.
- **Discovery airtime** - trailing-hour discovery transmissions against the global budget.
- **Needs attention** - anything the node thinks you should look at.
- **Quick send** - a small form that submits straight into the same path an app would take, with a 24-hour expiry. Useful for verifying a node-to-node link without writing an app first.

The decision-events ring is also mirrored into the systemd journal as log lines, so a `journalctl --grep` works for retrospective lookups beyond what the in-memory ring holds.

### Messages

Four tabs:

- **Outbound queue** - messages waiting to be forwarded. Updates without a page reload as messages are sent.
- **Local inbox** - messages that arrived for apps on this node and are waiting for the app to ack them via MQTT (`dapps/ack/<app>`) or REST. If this is growing, the app isn't acking - typically a misconfigured subscriber. Each message shows how long it has left before its TTL runs out (or "no expiry"). To get rid of one you no longer want, press **Drop**: it moves to the Dropped tab as "dropped by operator" and the app never gets it. On an ordered stream, later messages don't wait for it.
- **Dropped** - messages soft-deleted because TTL expired, hash mismatch, you dropped them, or another drop reason. Useful when you're investigating "where did message X go" - if it's here, you have the reason. Drop records are kept for 90 days.
- **Live arrivals** - every message as it arrives, with filters (app, source callsign, destination substring) and click-to-expand payload preview. `/Inbound` redirects here.

A message that has been forwarded, or acked by its app, is kept for 30 days and then deleted. A message with a TTL still goes when its TTL runs out, if that comes first.

### Topology

Five tabs:

- **Neighbours** - the forwarding-partner table. Add / edit / remove, probe one, and set per-neighbour radio port, compression, link hold and connect-script.
- **Discovery channels** - the bearers DAPPS beacons / listens on: beacon cadence, advertised TTL, link class, channel-specific airtime budget and solicit interval. Add channels from the form; **edit**, **enable** / **disable** and **remove** on a row; **solicit** sends a one-off "who's there?".
- **Discovered peers** - callsigns heard via beacons or solicit replies.
- **Probed nodes** - per-callsign probe state. Source flag distinguishes direct (`neighbour`), transitive (`via:CALL`), and node-prompt-discovered (`node-prompt:CALL`) rows. Last probed, last success, consecutive failures, opt-out flag.
- **Polled nodes** - if scheduled polling is on, the per-target poll state.

### Diagnostics

The [transmission audit](audit.md) log, plus **Probe everyone** and **Poll everyone** buttons.

### Compose

A bigger compose form than Quick send, with all header fields exposed (app, destination, payload, optional TTL). After submit it shows the on-air `ihave` line preview so you can see exactly how the message will appear on the wire.

Useful for debugging payload edge cases (TTL boundaries, salt collisions, fragment-threshold crossings) without writing an app first.

### Streams

Send and receive state for apps that use opt-in message ordering.

### Settings

The operator-tunable knobs (see [Configure](configure.md)), plus changing the admin password. Changing it needs the current password, and the new one must be at least 8 characters. The dashboard always has a password; there's no way to turn the login off. Restart-required fields are flagged in each section's blurb.

Forgotten the password? Reset it from a shell on the node. How you run `dapps` depends on how you installed it:

- **apt**: `sudo dapps reset-admin-password`
- **packet.net node app**: `sudo /usr/share/packetnet/apps/dapps/dapps reset-admin-password` (or `/var/lib/packetnet/apps/dapps/dapps` if you installed the app yourself)
- **Install script or manual install (Linux, macOS)**: `sudo /opt/dapps/dapps reset-admin-password`
- **Windows**, in an elevated PowerShell: `cd C:\ProgramData\dapps; & "C:\Program Files\dapps\dapps.exe" reset-admin-password` (`cd` to wherever your service's working folder is)
- **Docker**: `docker exec -it dapps dotnet dapps.core.dll reset-admin-password`

It asks for a new password twice (at least 8 characters), says which database it changed, and signs every browser out, so log in again with the new one. dapps can be running or stopped; no restart needed. On Linux and macOS it finds the database in `/var/lib/dapps` or the packet.net app folder; anywhere else, run it in the folder dapps runs in or add `--db <that folder>`. To script it, feed the password from a file: `sudo dapps reset-admin-password < newpass.txt`.

Changing the password in Settings also signs out every other browser.

## /Health - for watchdogs

`GET http://<node>:5000/Health`. Returns a small JSON document:

```json
{
  "status": "healthy",          // or "degraded"
  "callsign": "M0LTE-7",
  "version": "0.47.0",
  "uptimeSeconds": 86400,
  "callsignConfigured": true,
  "callsignProblem": null,      // or why the callsign is not valid
  "nodeReachable": true,
  "mqttBrokerUp": true,
  "lastForwardSuccessAt": "2026-05-03T07:58:08Z",
  "pendingOutboundCount": 0
}
```

HTTP 200 when healthy, 503 when the callsign isn't set or isn't valid, the packet node is unreachable or the MQTT broker is down. **Open access** - no admin cookie required, since watchdog units don't carry one. The packet-node and MQTT checks are remembered for 15 seconds, so polling it often doesn't mean opening a connection to your node each time.

## /Operational - for monitoring

`GET http://<node>:5000/Operational`. Much richer JSON: process metrics, the decision-events ring, per-link state, message counts, probe stats. The same snapshot is published to the MQTT heartbeat topic - pick whichever ingestion path your monitoring stack prefers.

Open access for the same reason as `/Health`, with the same 15 second memory for its node and MQTT checks.

## /metrics - for Prometheus

`GET http://<node>:5000/metrics`. The same statistics as `/Operational`, in the Prometheus text format. Point a Prometheus, VictoriaMetrics or Grafana Agent scrape job at it:

```yaml
scrape_configs:
  - job_name: dapps
    static_configs:
      - targets: ["<node>:5000"]
```

Open access, like `/Health`. The node and MQTT checks are remembered for 15 seconds, so a 15 second scrape interval costs one TCP connect per check. Every series name starts with `dapps_`. Counters reset when the service restarts, as Prometheus expects; use `rate()` or `increase()` over them.

Link state comes out three ways, because the node has three kinds of link:

| Series | What it measures |
|---|---|
| `dapps_node_link_up{bearer}`, `dapps_node_link_uptime_seconds{bearer}` | The socket to the packet node (AGW or RHPv2). `dapps_node_link_up` is 0 from the moment the connection closes until it is open again, and uptime is seconds since the last connect, 0 while down. `dapps_node_link_connects_total` counts connects, `dapps_node_link_reconnect_failures` is the current failure streak, and `dapps_node_link_next_retry_timestamp_seconds` says when the next attempt is due. |
| `dapps_peer_sessions_open{peer,direction}`, `dapps_peer_session_duration_seconds{peer,direction}` | DAPPS sessions open with a peer right now, and how long the oldest one has been open. A session exists only while an exchange runs, so these are often absent. |
| `dapps_neighbour_link_ok{neighbour}`, `dapps_neighbour_last_success_timestamp_seconds{neighbour}`, `dapps_neighbour_last_failure_timestamp_seconds{neighbour}` | The forwarding record per neighbour: 1 when the last forward to it succeeded. `time() - dapps_neighbour_last_success_timestamp_seconds` is how long since a message last got through. |

Messages passed: `dapps_forward_success_total`, `dapps_forward_failure_total` and `dapps_forward_bytes_total` count what left this node, in total and per neighbour (`dapps_neighbour_forward_*`). `dapps_inbound_messages_total` and `dapps_inbound_bytes_total` count what arrived; each piece of a split message counts once, and the reassembled whole does not count again. `dapps_ttl_expired_total`, `dapps_no_route_total` and `dapps_hash_mismatches_total` count drops by cause. `dapps_messages_pending_outbound` and `dapps_messages_undelivered_local` are the queue depths now.

Also there: `dapps_healthy` and the three checks behind it, `dapps_probes_total{result}`, `dapps_polls_total{result}`, `dapps_routes_learned_total`, `dapps_discovery_airtime_used_seconds` against `dapps_discovery_airtime_budget_seconds`, `dapps_update_available`, and the `dapps_meshcore_*` series when that bearer is on. `dapps_build_info{version,callsign,bearer}` carries the version. Each series has a `# HELP` line that says what it measures; `curl http://<node>:5000/metrics` lists them all.

## Behind a reverse proxy

A proxy on the same machine (the pdn app gateway, Caddy, nginx) can tell DAPPS what the browser used: `X-Forwarded-Proto: https` gets the login cookie the Secure flag, and `X-Forwarded-Prefix: /apps/dapps` puts the dashboard under that path. DAPPS believes these headers only from a proxy connecting from `127.0.0.1` or `::1`; from anywhere else they are ignored. A proxy on another machine or in another container isn't trusted, so run it on the node.

Known limit: behind the pdn gateway every app shares one origin, so the dashboard's antiforgery check cannot keep one pdn app from posting to another.

## MQTT heartbeat

Subscribers to `dapps/metrics/heartbeat` receive the operational snapshot every `HeartbeatIntervalSeconds` (60 s by default; minimum 10). **Retained** on the broker, so a late subscriber gets the most recent snapshot immediately.

If you're already running an MQTT-shaped monitoring pipeline, this is the easiest way to get DAPPS into it - one `mosquitto_sub -h <node> -t dapps/metrics/heartbeat -u <app> -P '<token>'` subscription gets you a ticking JSON document (leave out `-u` and `-P` if app tokens are off; any app's token will do).

## What to look at when

- **"Is the node alive?"** - `/Health` (open access; quickest).
- **"Graph it over time"** - `/metrics`, scraped by Prometheus.
- **"Is anything wedged?"** - the dashboard's Overview. Recent decisions and Needs attention show the recent narrative.
- **"Why didn't message X arrive?"** - the Dropped tab on the Messages page, or `journalctl --grep <message-id>` for older history.
- **"Is the link to peer Y working?"** - Topology > Probed nodes, or trigger a probe via the dashboard / REST.
- **"What's my airtime usage right now?"** - the Discovery airtime panel on the Overview.
- **"What's queued and not going anywhere?"** - the Outbound queue / Local inbox tabs on the Messages page.
- **"Is there an update?"** - the Build tile on the Overview, or `GET /Events/version`. To upgrade, see [Upgrade](update.md).

## Logs

Systemd journal on Linux (`journalctl -u dapps.service`), Docker logs in a container, NSSM-captured stdout file on Windows. Same content everywhere: start-up events, decision events, errors. The Recent decisions panel on the dashboard is the same data, just bounded; the journal is the long-tail store.
