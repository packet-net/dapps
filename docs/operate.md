# Operate

The day-to-day surfaces of a running DAPPS node - what's there, what each one's for, and which to look at when.

## The dashboard

`http://<node>:5000/` after first-use setup. The sidebar has seven pages.

### Overview

The home page, refreshing its live panels every 5 seconds:

- **Status tiles** - packet-node reachability (probes the configured bearer port - AGW or RHPv2, with a **Retry now** link while it's reconnecting), the MQTT broker, whether transmitting is allowed or stopped, and the running build with **Check now** and, when there's one to apply, **Apply update**. See [Update](update.md).
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
- **Local inbox** - messages that arrived for apps on this node and are waiting for the app to ack them via MQTT (`dapps/ack/<app>`) or REST. If this is growing, the app isn't acking - typically a misconfigured subscriber.
- **Dropped** - messages soft-deleted because TTL expired, hash mismatch, or another drop reason. Useful when you're investigating "where did message X go" - if it's here, you have the reason. Drop records are kept for 90 days.
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

Forgotten the password? On the node, run:

```bash
sudo dapps reset-admin-password
```

It asks for a new password twice (at least 8 characters) and signs every browser out, so log in again with the new one. dapps can be running or stopped; it doesn't need a restart. To script it, pipe the password in on one line instead. It finds the database in the folder you run it in, or the apt install's `/var/lib/dapps`; anywhere else, `cd` to the folder dapps runs in first, or add `--db <that folder>`. In Docker, it's `docker exec -it dapps dotnet dapps.core.dll reset-admin-password`.

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

HTTP 200 when healthy, 503 when the callsign isn't set or isn't valid, the packet node is unreachable or the MQTT broker is down. **Open access** - no admin cookie required, since watchdog units don't carry one.

## /Operational - for monitoring

`GET http://<node>:5000/Operational`. Much richer JSON: process metrics, the decision-events ring, per-link state, message counts, probe stats. The same snapshot is published to the MQTT heartbeat topic - pick whichever ingestion path your monitoring stack prefers.

Open access for the same reason as `/Health`.

## MQTT heartbeat

Subscribers to `dapps/metrics/heartbeat` receive the operational snapshot every `HeartbeatIntervalSeconds` (60 s by default; minimum 10). **Retained** on the broker, so a late subscriber gets the most recent snapshot immediately.

If you're already running an MQTT-shaped monitoring pipeline, this is the easiest way to get DAPPS into it - one `mosquitto_sub -h <node> -t dapps/metrics/heartbeat -u <app> -P '<token>'` subscription gets you a ticking JSON document (leave out `-u` and `-P` if app tokens are off; any app's token will do).

## What to look at when

- **"Is the node alive?"** - `/Health` (open access; quickest).
- **"Is anything wedged?"** - the dashboard's Overview. Recent decisions and Needs attention show the recent narrative.
- **"Why didn't message X arrive?"** - the Dropped tab on the Messages page, or `journalctl --grep <message-id>` for older history.
- **"Is the link to peer Y working?"** - Topology > Probed nodes, or trigger a probe via the dashboard / REST.
- **"What's my airtime usage right now?"** - the Discovery airtime panel on the Overview.
- **"What's queued and not going anywhere?"** - the Outbound queue / Local inbox tabs on the Messages page.
- **"Is there an update?"** - the Build tile on the Overview, or `GET /Update/status`.

## Logs

Systemd journal on Linux (`journalctl -u dapps.service`), Docker logs in a container, NSSM-captured stdout file on Windows. Same content everywhere: start-up events, decision events, errors. The Recent decisions panel on the dashboard is the same data, just bounded; the journal is the long-tail store.
