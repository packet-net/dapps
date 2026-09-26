# Troubleshooting

Failure modes, in rough order of how often you'll hit them.

## DAPPS won't start

### "Refusing to start: callsign is N0CALL"

You haven't set `DAPPS_CALLSIGN`. Set it in the systemd unit / docker compose / shell environment:

```
DAPPS_CALLSIGN=M0LTE-1
```

The placeholder is a deliberate safety net - DAPPS won't transmit frames stamped with `N0CALL` because that would propagate garbage onto the air.

### Exit code 78

Operationally-fatal config error: a port is already in use, or another configured resource isn't available. The journal (or stdout, if you're running interactively) will have the actual error one line above. Common causes:

- **MQTT port (1883) already in use** by another broker on the same host. Either stop the other broker or set `DAPPS_MQTT_PORT` to a free port.
- **Dashboard port (5000) already in use** - set `ASPNETCORE_URLS=http://0.0.0.0:5001` (or whatever).
- **UDP listen port already in use** - disable the UDP datagram bearer with `DAPPS_UDP_LISTEN_PORT=0`, or pick a different port.

The unit file's `RestartPreventExitStatus=78` keeps systemd from hot-looping on a problem that won't fix itself; the journal message tells you what to fix.

### Database errors on first start

DAPPS expects to be able to create a SQLite file at `data/dapps.db` (relative to the working directory) or wherever you've pointed it. If the working directory isn't writable, startup fails. On Linux/systemd the recommended unit uses `WorkingDirectory=/var/lib/dapps`; make sure that exists and is owned by the runtime user.

## Packet-node connection problems

DAPPS connects to the packet node over either AGW (`DAPPS_NODE_BEARER=agw`, the default - works with BPQ, Direwolf, AGWPE, ...) or RHPv2 (`DAPPS_NODE_BEARER=rhpv2`, required for XRouter). The dashboard's **Packet node** card on the home page shows the bearer + host:port DAPPS is using and a reachability dot.

### "Packet-node connection refused" / repeated reconnects

DAPPS can't reach the bearer port on `DAPPS_NODE_HOST` (`DAPPS_AGW_PORT` for AGW, `DAPPS_RHP_PORT` for RHPv2). Check:

- The packet node is actually running and the bearer is enabled.
    - **BPQ AGW**: `AGWPORT=8000` in `bpq32.cfg`, no `#` commenting it out, BPQ restarted since.
    - **XRouter RHPv2**: `RHPPORT=9000` in `XROUTER.CFG`, XRouter restarted.
- Network connectivity from where DAPPS runs to where the node runs. `nc -vz <node-host> <port>` from the DAPPS host should connect.
- No firewall in the way.
- For non-loopback XRouter, `ACCESS.SYS` allows your DAPPS host's subnet (flag `1` for callsign-only). Loopback is allowed without an entry.

### "Packet-node connection drops repeatedly"

DAPPS reconnects automatically, backing off from 10 s to 30 s to 1 minute to every 5 minutes as failures pile up, and the dashboard's Packet node tile shows the countdown with a **Retry now** link (saving `/Config` also retries straight away). If it's flapping every few seconds:

- Check the packet node's logs for whether it's actively closing the connection. Some BPQ misconfigurations cause AGW to disconnect clients on every L2 event.
- Check for two DAPPS instances accidentally sharing a callsign - the second binding wins and the first one sees its inbound dispatch evaporate.

### Inbound sessions never arrive (BPQ AGW)

A remote node can `c <your-callsign>` and lands at the BPQ node prompt, but not at the `DAPPSv1>` prompt. Check:

- The `APPLICATION` line is in `bpq32.cfg` with the DAPPS callsign in the **APPLCALL** field - `APPLICATION 1,DAPPS,,M0LTE-7,DAPPS,0` - and BPQ has been restarted since adding it. The runtime AGW registration alone is **not** enough on BPQ: without the APPLCALL, BPQ ignores inbound connects to the DAPPS callsign entirely and the remote caller sees RETRYOUT.
- The CMD field on the `APPLICATION` line (the third field) is **empty**. Older recipes had `C N HOST K TRANS S` here - for DAPPS, leave it empty so BPQ doesn't run a node command on inbound, just dispatches the L2 'C' frame to the registered AGW client.
- DAPPS is registering the right callsign. The startup log shows `AGW: registered <callsign> for inbound dispatch`. If the callsign there doesn't match what the remote is connecting to (and the APPLCALL), it won't route.
- AGW exact-match is by call+SSID. `M0LTE-1` is different from `M0LTE-7`.

### A peer connects, gets no `DAPPSv1>` prompt, and has to redial (BPQ AGW)

Symptom on the calling side: the L2 connect is accepted, then silence until the caller times out and tries again. Your BPQ console may show `Rejected <caller> to <you> - callsign is already connected on socket N`. Your DAPPS log shows the inbound `'C'` followed by a warning ending `retiring the stale entry`.

Both come from the same place. AGW frames carry no session id, so a session is only ever identified by (your callsign, their callsign, port). BPQ stamps its disconnect notification with the port of the last frame it received *from DAPPS*, which is usually the 15-second keepalive on port 0, rather than the port the session was on. DAPPS now matches such a notification by callsign pair, so the previous session closes properly and the redial is a clean new session. If the `retiring the stale entry` warning still appears, the previous session's disconnect notification never reached DAPPS at all; the warning is DAPPS recovering, and the new session is fully usable. If it recurs, an issue with the BPQ log alongside the DAPPS journal is the right next step.

The BPQ-side `already connected` rejection means the caller redialled before your BPQ's AGW poll loop had processed the previous disconnect. A DAPPS caller waits two seconds between sessions to the same destination for exactly this reason.

### Two DAPPS nodes keep reconnecting to each other and land on the node prompt (BPQ AGW)

Symptom on a monitor: a session between two DAPPS neighbours completes an `ihave` / `send` / `data` exchange, then one side sends a fresh connect request (`<C C P>`, a SABM) to the other with no disconnect in between, and what comes back is the BPQ node's welcome banner (`Welcome to BPQ Node ...`) instead of `DAPPSv1>`. From then on both nodes redial each other every few seconds. Both DAPPS logs show forwards failing with `no DAPPSv1> prompt`.

This is two forwarders dialling each other at the same moment. AX.25 has one link per callsign pair per port, whichever end set it up. When both nodes have traffic queued for each other, each one's forwarder dials out on its own tick. BPQ doesn't refuse the outbound connect just because a link to that station is already up; it sends a SABM down the live link. The other node's BPQ treats a SABM on an established link as a link reset: it drops the DAPPS session attached to that link and re-attaches the link at the node's command level, hence the banner. Both sides then fail, requeue and redial in step.

DAPPS keeps track of which peers it has a session open with, in either direction, and leaves outbound traffic for such a peer queued until that session ends. The log line is `Deferring <n> message(s) for <peer>: an inbound session with it is already open`. If the peer has opportunistic poll enabled, its `rev` on the existing session collects the queued traffic anyway; otherwise the first forwarder tick after the session closes dials as normal. The guard is one-sided (each node only stops itself dialling into a live session), so if you still see the pattern above, check that the *other* node is also on a DAPPS version that has it.

That check originally ran once, early in the forwarder tick - before the two-second link-settle wait and the AGW/RHP connect round trip that follow it, both of which take real time. A peer's inbound session could still be handed to DAPPS in that gap, after the early check passed but before the outbound SABM actually reached BPQ, producing the exact pattern above with both nodes on a version that has the guard. DAPPS now repeats the check immediately before the dial, under the same lock that registers the session, so a peer that only becomes busy after the early check still gets caught - the log line there is `Deferring <id>: <peer> already has an inbound session open, dialling now would reset it`.

There is a narrower variant neither check can see coming: both nodes dial within the same round trip, so neither has an inbound session yet when it decides to dial. Both links come up, but each BPQ attaches its link to its own outgoing session, so neither DAPPS is handed an inbound connect and neither sends `DAPPSv1>`. On a monitor this looks like a connect that succeeds and then goes quiet. DAPPS resolves this too: after 60 seconds of silence the node with the lower callsign sends the prompt itself and serves the session (log line `Nothing from <peer> for 60s after connecting: assuming a crossed connect`), and the other node, still waiting for its prompt, sees it and carries on as the caller. The lower node's own message stays queued for the next tick, unless the caller's `rev` collects it on that same session.

### Inbound sessions never arrive (XRouter RHPv2)

A remote node can `c <your-callsign>` and lands at the XRouter node prompt, but not at the `DAPPSv1>` prompt. Check:

- DAPPS bound the callsign at startup. Look for `RHP inbound: listener bound to <callsign> on handle <N>` in the log.
- The DAPPS callsign is **not** declared in any `APPL` block, `NODECALL`, `CONSOLECALL`, or `CHATCALL` in `XROUTER.CFG`. XRouter claims those callsigns for its own internal use; DAPPS's runtime bind would fail.
- Authentication: if XRouter requires it, `DAPPS_RHP_USER` / `DAPPS_RHP_PASS` must match an entry the RHPv2 listener accepts.

## Discovery / routing problems

### "list_discovered_peers / list_neighbours empty, no traffic moving"

A fresh DAPPS install has **no discovery channels configured by default** - discovery is opt-in. Without a channel and an enabled beacon, you don't hear other nodes and they don't hear you. Two ways forward:

- **Manual neighbour**: dashboard → Neighbours → add a row for the peer you want to talk to. This is the fastest path to "first message" - no discovery needed.
- **Discovery channel**: dashboard → Discovery channels → add a channel for the bearer port DAPPS should beacon on. Set a sensible cadence (10 minutes for VHF FM is a reasonable starting point) and a per-channel airtime budget if you're on a shared band.

### "I added a discovery channel but I'm not hearing anyone"

Check, in order:

- Channel is **enabled** (the row's enabled flag).
- Beacon cadence isn't unreasonably long (one beacon per hour means you hear nothing for an hour after start).
- Other DAPPS nodes are actually transmitting on the same channel-key (same bearer port). A node beaconing on port 1 won't be heard by a node listening on port 0.
- Airtime budget isn't exhausted. Dashboard → Discovery channels heading shows trailing-hour consumption; if it's ≥ 100 % of the global cap, beacons will defer. Either raise the cap or wait.

### "Probes failing"

The probed-nodes table shows the recent failure reason. Common ones:

- **`RETRYOUT`**: the bearer retried the connect to the remote callsign but never got a response. Three common causes: the remote isn't reachable on the link; the bearer port for that peer is wrong (per-neighbour `BearerPort` or `DAPPS_DEFAULT_BEARER_PORT`); or the remote BPQ doesn't declare the DAPPS callsign in an `APPLICATION` line's APPLCALL field - in that case BPQ silently ignores connects to the callsign even though the remote DAPPS registered it over AGW.
- **Connect accepted (UA) but then silence, ending in `timeout: ... no data from peer`**: the classic signature of probing the remote's **node call** instead of its DAPPS callsign. The node accepts the L2 connect and then sits at its (silent) command prompt waiting for input, while the probe waits for a `DAPPSv1>` banner that will never come. Check the neighbour row's callsign is the remote *DAPPS* callsign (e.g. `M0XYZ-7`), not the NODECALL.
- **Connect timeout**: the connect went out but the protocol parser never saw the `DAPPSv1>` prompt. The peer either isn't running DAPPS, or you're connecting to the wrong application (their `APPLICATION` line might use a different command name).
- **TIMEOUT after `DAPPSv1>`**: probe got the prompt but the session hung. Network is dropping packets mid-session, or one side has a serious clock skew.

### "Forwarding loops"

The default `passive-flood` algorithm has a hop-count cap and won't loop indefinitely, but if you have a complex topology where it's mis-deciding routes, switch to the `meshcore` algorithm (DSR-style source routing) for more deterministic forwarding. `DAPPS_ROUTING_ALGORITHM=meshcore` + restart.

## App-interface problems

### "MQTT subscriber not receiving messages"

Check, in order:

- The subscriber is connected to the right port (default 1883, configurable).
- The subscriber is subscribed to the right topic (`dapps/in/<app>` for incoming).
- Your MQTT client supports MQTT 5 user properties. DAPPS publishes `dapps-id`, `dapps-source`, `dapps-ttl` etc. as user properties - MQTT 3.1.1 clients will get the payload but not the metadata.
- The subscriber is acking (`dapps/ack/<app>`). Without acks the messages stay in the local-inbox queue (dashboard → Local inbox panel) and re-deliver on the next subscription.

### "REST submit returns 200 but message never arrives"

Look at the dashboard:

- Outbound queue panel: is the message there? If yes, it's queued but the forwarder hasn't shipped it yet. It normally goes within a second; if it sits there, the neighbour is probably in reconnect cooldown or has a session open with us (see the journal). If no, the submit didn't actually write to the messages table - check the response body, it'll have an error.
- Recently dropped panel: did it get dropped? If yes, reason will be there (usually TTL too short, or no route to destination).
- Per-link state panel: is the link to the destination's first-hop neighbour actually working?

### "TTL expired" drops

The default TTL on a submit is open-ended (no expiry). If you're seeing TTL drops, you're explicitly setting one. Either raise it on submit, or accept that mail-style multi-day delivery won't work with a sub-hour TTL.

## Update problems

### "Apply update button does nothing"

Three possibilities:

- The `dapps-updater.service` / `.timer` unit isn't installed. `systemctl status dapps-updater.timer` should show `active (waiting)`. If not, install per the [Linux install page](install/linux.md).
- The marker file write succeeded but the timer hasn't ticked yet. Wait up to 60 s.
- The updater ran but failed. `journalctl -u dapps-updater.service` will have the error.

### "Update applied, but rolled back"

The new binary started but didn't pass the 60 s verify. Look at:

- `journalctl -u dapps.service --since '5 minutes ago'` for the new binary's startup messages - there'll be a clear error.
- The dashboard's update card - if rollback succeeded, the phase pill shows "rolled back" with the reason.

The previous binary is restored automatically; you don't need to do anything to recover. Investigate the new version's failure mode and either wait for a fix or stay on the previous version.

### "Update banner shows the same version as I'm running"

The poll cadence is hourly. To force an immediate re-poll, click **Check now** on the dashboard, or `POST /Update/check`, or call the `check_for_updates` MCP tool.

## Investigating "where did message X go?"

A worked path:

1. **Dashboard → Recently dropped panel.** If the message id is there, the reason is the answer.
2. **`journalctl -u dapps.service | grep <message-id>`** - the decision-events ring is mirrored to the journal as structured log lines. Every step the daemon took with this id will be there: submit, queue insert, forward attempt, ack received / failed, etc.
3. **MCP `explain_why_message_failed`** if you have an assistant connected - it walks the same trail and produces a narrative.

If nothing turns up, the message was never submitted (typo on the topic / endpoint). Check the submitting application's logs.

## Getting help

- **Dashboard not showing what you expect** → screenshot it and [open an issue](https://github.com/packet-net/dapps/issues). The UI is dense, behaviour-vs-expectation reports are useful.
- **On-air protocol question** → see the [protocol reference](app-developers/reference.md), then open an issue if it's not answered.
- **Bearer-specific weirdness** (BPQ doing something odd, AGW edge case) → likely either a config issue documented above or a real bug; an issue with the BPQ logs + DAPPS journal is the right next step.
