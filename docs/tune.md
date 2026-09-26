# Tune

Out of the box, DAPPS picks defaults that are safe on a low-traffic VHF FM port. If you're running on something faster (full-time IP backbone, fast Ethernet between two co-located nodes) you can probably leave most knobs alone. If you're on something slower, smaller, or shared with other operators (1200-baud VHF in a busy area, HF NVIS, satellite), the knobs in this section let you back off.

## Airtime budget

The single biggest knob. By default DAPPS imposes **no cap** on discovery transmissions - beacons go out on their per-channel cadence, probes fire on their interval, solicit replies happen whenever a solicit arrives. If you turn discovery features on without thinking about airtime, this can be a lot.

```
DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR=120
```

This caps **all** discovery-class transmissions (beacons + solicits + probes) at a trailing-hour total. When the cap would be exceeded, beacons reschedule a quarter-cadence later, probe sweeps stop early and resume next sweep, solicit replies are deferred. The dashboard's Discovery channels heading shows current consumption against the cap with a warning at ≥ 90 %.

There's also a **per-channel** cap, set on the discovery channel row itself (not via env var). When both global and per-channel caps are set, a transmission must fit under both. Use this when a single shared HF channel needs to be capped tighter than your overall budget.

Operator-triggered probes from the REST surface bypass the budget - that's an explicit human action on a single callsign and rate-limiting it would be hostile to the "I'm debugging a peer right now" path.

## Probe strategies

Probing is **off by default**. Once you enable it (`DAPPS_PROBING_ENABLED=true`), a strategy decides *when* to actually run a sweep:

| Strategy        | When it fires                                                                                                                                                                              |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `FixedInterval` | Every `DAPPS_PROBE_INTERVAL_HOURS` hours. The simplest. Same cadence regardless of context.                                                                                                |
| `Overnight`     | Once per local-time day inside the `[probe-overnight-start-hour, probe-overnight-end-hour)` window. Default 02:00–06:00. Wraps midnight if `end < start`. Good for shared bands where you want to be quiet during peak hours. |
| `WhenQuiet`     | Fixed cadence, but defers each tick if the forwarder has seen activity in the last `probe-quiet-window-seconds` (default 5 minutes). Good for nodes where probe traffic shouldn't compete with real traffic. |

Switch strategy via `DAPPS_PROBE_STRATEGY=Overnight` (case-insensitive) or the `/Config` form.

## Fragment threshold

Payloads strictly larger than `DAPPS_FRAGMENT_THRESHOLD_BYTES` get split into N fragments at submit, each delivered independently and reassembled at the receiver. Default is 4096 bytes - sized for VHF. Bump it higher (or set `0` to disable) on faster, more reliable bearers; drop it lower if your bearer's MTU is smaller than 4 KiB.

The dashboard's outbound queue shows fragment status; the master ID groups the fragments back together for the sender.

Reassembly buffers age out after 7 days by default (`DAPPS_FRAGMENT_REASSEMBLY_TIMEOUT_SECONDS`). Drop this if you're storage-constrained; raise it if you have peers that go offline for long stretches.

## Routing algorithm

Two choices, switched with `DAPPS_ROUTING_ALGORITHM` (restart required):

- **`passive-flood`** (default) - AODV-style: routes are learned from observed forwards. Cheap, no extra airtime, works well on small meshes.
- **`meshcore`** - DSR-style source routing inspired by MeshCore's algorithm: the sender stamps the path on the message, intermediate nodes follow it. Adds a few bytes per message; works better when the topology is volatile or you want explicit per-message path control.

For most setups, leave `passive-flood` on. Switch to `meshcore` if you have a reason - for instance, if you're emulating MeshCore-on-AX.25 to test the bearer integration shape, or if you're seeing forwarding loops on a complex topology that the passive learner is mis-handling.

## Polling

Two poll knobs, both off by default:

- **Opportunistic poll** (`DAPPS_OPPORTUNISTIC_POLL_ENABLED`, default **true**) - at the end of every push session to a peer, request anything they hold for us. Free, since the session is already open. The recommended setting; only turn off if you have a peer that genuinely shouldn't be polled.
- **Link hold** (`DAPPS_SESSION_TAIL_SECONDS`, default **120**) - after a session that moved messages, keep the link to that neighbour open until it has been quiet this long, so a follow-up in either direction goes at once instead of after a new connect. Worth raising on a quiet point-to-point link where messages come in conversations; lower it, or set 0 for that neighbour on the Topology page, where a node is short of circuits or drops idle ones sooner. Needs opportunistic poll on.
- **Scheduled poll** (`DAPPS_SCHEDULED_POLL_ENABLED`, default **false**) - periodically open a session to every known forward target, just to ask. Use this on bearers where peers can't push to you reliably (asymmetric link, peer behind NAT, peer that doesn't know our bearer hint).

If both are on, opportunistic covers the common case and scheduled fills in the gap for peers we never push to.

## Heartbeat cadence

`DAPPS_HEARTBEAT_INTERVAL_SECONDS` (≥ 10 s, default 60 s) controls how often the heartbeat is published to MQTT. On a quiet node, 60 s is fine. If you're scraping the heartbeat into a high-resolution monitoring system, drop to 10–30 s; if you're storage-constrained on the MQTT broker side and only want trends, raise to 300 s.

The MQTT message is **retained** so a late subscriber gets the most recent snapshot immediately - cadence is about freshness for active subscribers, not "did I miss it."

## What *not* to tune blindly

- **Don't lower `fragment-reassembly-timeout-seconds` below a day** unless you're really sure no peer of yours will ever go off the air for that long. The cost of a longer timeout is a few KB of buffer; the cost of a too-short timeout is silent message loss.
- **Don't raise `probe-interval-hours` very high while keeping probing on** - long intervals plus stale routing data is a recipe for confidently sending to peers that have moved on.
- **Don't disable `update-check-enabled`** unless your node really has no internet - the cost is one HTTPS request per hour and the win is knowing when there's a fix.
- **Don't disable the heartbeat publisher** if you're using it for monitoring - there's no "just-once" knob, and disabling the loop means losing the entire scrape source.
