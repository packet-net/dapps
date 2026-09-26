# Operate

The day-to-day surfaces of a running DAPPS node - what's there, what each one's for, and which to look at when.

## The dashboard

`http://<node>:5000/` after first-use setup. Single dense view, auto-refreshing the live panels every 5 seconds.

Sections, top to bottom:

### Node card

Callsign, version, packet-node reachability indicator (probes the configured bearer port - AGW or RHPv2), MQTT broker / UDP listener status, default outbound port. Quick "is this node alive and configured" glance.

### Update card

Currently-running version + latest known release. If they differ, the **Apply update** button is enabled - one click triggers the supervised in-place update path (Linux/systemd only). See the [Update](update.md) page for the detail.

### Process metrics

Per-link state for the configured bearers (last successful tx, last error, reconnect counts), uptime, message counters, decision-events ring (a rolling buffer of recent operational decisions with reasons - "rejected forward, no route to X" etc.).

The decision-events ring is also mirrored into the systemd journal as structured log lines, so a `journalctl --grep` works for retrospective lookups beyond what the in-memory ring holds.

### Queue at a glance

Three counters: total messages in the table, pending outbound (forwards in flight), undelivered local (messages for apps on this node that haven't been ack'd yet).

### Outbound queue

Live table of messages waiting to be forwarded - id, destination, source, bytes, residual TTL, age. Updates without a page reload as messages are sent.

### Local inbox

Messages that arrived for apps on this node and are waiting for the app to ack them via MQTT (`dapps/ack/<app>`) or REST. If this is growing, the app isn't acking - typically a misconfigured subscriber.

### Neighbours

The forwarding-partner table. Add / edit / delete from here.

### Discovery channels

The bearers DAPPS beacons / listens on. Heading shows trailing-hour airtime consumption against the global cap (warns at ≥ 90 %). Per-channel rows show beacon cadence, advertised TTL, link-class, channel-specific airtime budget if set. Add new channels from the form below the table.

### Probed nodes

Per-callsign probe state. Source flag distinguishes direct (`neighbour`), transitive (`via:CALL`), and node-prompt-discovered (`node-prompt:CALL`) rows. Last probed, last success, consecutive failures, opt-out flag.

### Polled nodes

If scheduled polling is on, the per-target poll state.

### Recently dropped

Messages soft-deleted because TTL expired, hash mismatch, or another drop reason. Useful when you're investigating "where did message X go" - if it's here, you have the reason.

### Live inbound feed

The same SSE feed as `/Inbound`, condensed. Click a message to expand its payload preview inline.

### Send a test message

A small form that submits straight into the same path an app would take. Useful for verifying a node-to-node link without writing an app first.

### Config

A `<details>` block with every operator-tunable knob. Restart-required fields are flagged in the form blurb. Save POSTs the full settings row.

## /Inbound - focused live tail

`http://<node>:5000/Inbound`. Dedicated page for watching messages arrive in real time, with filter inputs (app, source callsign, destination substring) and click-to-expand payload preview. Useful when the dashboard's condensed live panel isn't enough and you want to focus on inbound traffic with a wider table and easier filtering.

## /IHave - manual compose

`http://<node>:5000/IHave`. A bigger compose form than the dashboard's quick send-test, with all header fields exposed (app, destination, payload, optional TTL). After submit it shows the on-air `ihave` line preview so you can see exactly how the message will appear on the wire.

Useful for debugging payload edge cases (TTL boundaries, salt collisions, fragment-threshold crossings) without writing an app first.

## /Health - for watchdogs

`GET http://<node>:5000/Health`. Returns a small JSON document:

```json
{
  "status": "healthy",          // or "unhealthy"
  "checkedAt": "2026-05-03T07:58:08Z",
  "components": {
    "database": "healthy",
    "agw": "healthy",
    "mqtt": "healthy"
  }
}
```

HTTP 200 when healthy, 503 when at least one component is down. **Open access** - no admin cookie required, since watchdog units don't carry one.

## /Operational - for monitoring

`GET http://<node>:5000/Operational`. Much richer JSON: process metrics, the decision-events ring, per-link state, message counts, probe stats. The same snapshot is published to the MQTT heartbeat topic - pick whichever ingestion path your monitoring stack prefers.

Open access for the same reason as `/Health`.

## MQTT heartbeat

Subscribers to `dapps/metrics/heartbeat` receive the operational snapshot every `heartbeat-interval-seconds` (60 s by default; minimum 10). **Retained** on the broker, so a late subscriber gets the most recent snapshot immediately.

If you're already running an MQTT-shaped monitoring pipeline, this is the easiest way to get DAPPS into it - one `mosquitto_sub -h <node> -t dapps/metrics/heartbeat` subscription gets you a ticking JSON document.

## /mcp - for AI assistants

`http://<node>:5000/mcp`. The MCP (Model Context Protocol) endpoint exposes 26 operator-facing tools to a connected assistant - listing peers, triggering probes, sending test messages, applying config, applying updates, and so on. See the [MCP for assistants](mcp.md) page.

Also open access (no admin cookie). MCP clients have their own auth model that the daemon stays out of.

## What to look at when

- **"Is the node alive?"** - `/Health` (open access; quickest).
- **"Is anything wedged?"** - dashboard. The Process metrics + Decision-events panels show the recent narrative.
- **"Why didn't message X arrive?"** - Recently dropped on the dashboard, or `journalctl --grep <message-id>` for older history.
- **"Is the link to peer Y working?"** - Probed nodes table, or trigger a probe via the dashboard / MCP / REST.
- **"What's my airtime usage right now?"** - Discovery channels heading on the dashboard.
- **"What's queued and not going anywhere?"** - Outbound queue / Local inbox panels on the dashboard.
- **"Is there an update?"** - Update card on the dashboard, or the heartbeat's `update_available` field.

## Logs

Systemd journal on Linux (`journalctl -u dapps.service`), Docker logs in a container, NSSM-captured stdout file on Windows. Same content everywhere: start-up events, decision events, errors. The decision-events ring on the dashboard is the same data, just bounded; the journal is the long-tail store.
