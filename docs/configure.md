# Configure

Every operator-tunable setting on a DAPPS node has three configuration surfaces:

1. **A persisted default** in the SQLite database, seeded on first start.
2. **An environment variable**, of the form `DAPPS_SCREAMING_SNAKE_NAME`, which **overrides the persisted default at first start** and updates the persisted row.
3. **The dashboard's `/Config` form** (or `POST /Config` REST API), which writes a new value to the persisted row at any time.

For runtime-changeable knobs (probing on/off, fragment threshold, airtime budget, etc.), the dashboard is the right surface - changes pick up without a restart. For startup-only knobs (callsign, packet-node host/port, MQTT port, dashboard URL), the env var is the right surface - they're read once at boot.

> **Deployment-managed mode (`DAPPS_ENV_MANAGED=true`)**: when a supervising deployment (a packet.net node host, say) sets `DAPPS_ENV_MANAGED=true`, every *set* `DAPPS_*` env var becomes deployment-managed config instead - re-applied over the persisted row at **every** start, with the matching dashboard fields badged "managed by environment" and rendered read-only. `DAPPS_ENV_MANAGED` itself is a mode switch read each start, never stored. Leave it unset for the standalone installs documented here: with it unset (or `false`) env vars keep the first-start-only semantics above and the dashboard stays in charge.

## Show the current persisted config

```bash
dapps --show-config
```

Walks the persisted `systemoptions` table and prints `DAPPS_SCREAMING_SNAKE=value` pairs for every set knob, **without booting the host**. Useful when the daemon won't start and you need to see what state it'd boot into.

## The knobs

### Identity (startup-only)

| Name             | Env var          | Default     | What it does                                                              |
|------------------|------------------|-------------|---------------------------------------------------------------------------|
| Callsign         | `DAPPS_CALLSIGN` | `N0CALL`    | Your callsign with SSID, e.g. `M0LTE-1`. **Refuses to start on the placeholder.** |
| Node host        | `DAPPS_NODE_HOST`| `localhost` | TCP host of your packet node (BPQ, XRouter, etc.).                        |
| Node bearer      | `DAPPS_NODE_BEARER` | `agw`    | Which host protocol DAPPS speaks to the packet node: `agw` (BPQ, Direwolf, AGWPE, ...) or `rhpv2` (XRouter, future RHPv2-capable BPQ). See [Connect a node](connect/index.md). |
| AGW port         | `DAPPS_AGW_PORT` | `8000`      | TCP port the packet node's AGW interface listens on. Used when `Node bearer = agw`. |
| RHPv2 port       | `DAPPS_RHP_PORT` | `9000`      | TCP port the packet node's RHPv2 listener is on. Used when `Node bearer = rhpv2`. |
| RHPv2 user       | `DAPPS_RHP_USER` | _empty_     | Username for RHPv2 authentication. Leave empty if your packet node accepts unauthenticated RHPv2. |
| RHPv2 password   | `DAPPS_RHP_PASS` | _empty_     | Password matching the RHPv2 user.                                         |
| Default bearer port | `DAPPS_DEFAULT_BEARER_PORT` | `0` | Bearer port (0-indexed) for outbound sessions when a neighbour has no per-row override. For AGW this is the AGW port byte; for RHPv2 DAPPS adds 1 internally to derive XRouter's 1-indexed `PORT=N` name. |

### App-interface ports

| Name              | Env var                  | Default | What it does                                              |
|-------------------|--------------------------|---------|-----------------------------------------------------------|
| MQTT broker port  | `DAPPS_MQTT_PORT`        | `1883`  | Port the embedded MQTT broker binds.                      |
| UDP listener port | `DAPPS_UDP_LISTEN_PORT`  | `0`     | UDP datagram bearer listen port. `0` disables.            |

The dashboard / REST / MCP endpoint port is configured via `ASPNETCORE_URLS` (a .NET convention), e.g. `ASPNETCORE_URLS=http://127.0.0.1:5000`.

### Authentication

| Name         | Env var             | Default | What it does                                                              |
|--------------|---------------------|---------|---------------------------------------------------------------------------|
| Auth required| `DAPPS_AUTH_REQUIRED` | `false` | When true, MQTT / REST app-interface clients must present a per-app token. |

The admin password (for the dashboard cookie) is set on `/Setup` first-run flow, not via env var.

### Discovery & routing

| Name                       | Env var                                    | Default          | What it does                                                                       |
|----------------------------|--------------------------------------------|------------------|------------------------------------------------------------------------------------|
| Routing algorithm          | `DAPPS_ROUTING_ALGORITHM`                  | `passive-flood`  | `passive-flood` (AODV-style, learns from observed forwards) or `meshcore` (DSR-style source routing). Restart required. |
| Probing enabled            | `DAPPS_PROBING_ENABLED`                    | `false`          | Periodically open a connected-mode session to known peers to confirm reachability. |
| Probe interval (hours)     | `DAPPS_PROBE_INTERVAL_HOURS`               | `24`             | Sweep cadence when probing is on.                                                  |
| Probe strategy             | `DAPPS_PROBE_STRATEGY`                     | `FixedInterval`  | One of `FixedInterval`, `Overnight`, `WhenQuiet`. See [Tune](tune.md).             |
| Overnight start hour       | `DAPPS_PROBE_OVERNIGHT_START_HOUR`         | `2`              | Local-time hour the Overnight strategy's window opens.                             |
| Overnight end hour         | `DAPPS_PROBE_OVERNIGHT_END_HOUR`           | `6`              | Local-time hour the Overnight strategy's window closes. Wraps midnight if `end < start`. |
| Quiet window (s)           | `DAPPS_PROBE_QUIET_WINDOW_SECONDS`         | `300`            | Seconds of forwarder-quiet required for `WhenQuiet` to fire.                       |
| Auto-discover via NODECALL | `DAPPS_AUTO_DISCOVER_VIA_NODE_CALL`        | `false`          | When true, AGW DAPPS beacons auto-seed node-prompt-probe candidates for the source's base callsign. |
| Node-prompt application command | `DAPPS_NODE_PROMPT_APPLICATION_COMMAND` | `DAPPS`         | The application name typed at the BPQ node prompt to enter the DAPPS slot. Override if your `APPLICATION` line uses a different name. |
| Discovery airtime budget (s/hr) | `DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR` | `0` | Trailing-hour cap on discovery transmissions (beacons + solicits + probes). `0` disables the cap. |

### Polling

| Name                       | Env var                          | Default | What it does                                                  |
|----------------------------|----------------------------------|---------|---------------------------------------------------------------|
| Scheduled poll enabled     | `DAPPS_SCHEDULED_POLL_ENABLED`   | `false` | Periodic reverse-poll of every known forward target.          |
| Poll interval (hours)      | `DAPPS_POLL_INTERVAL_HOURS`      | `6`     | Sweep cadence when scheduled polling is on.                   |
| Opportunistic poll enabled | `DAPPS_OPPORTUNISTIC_POLL_ENABLED` | `true`  | Drains a peer's queued mail at the end of every push session. |

### Multi-part messages

| Name                              | Env var                                  | Default     | What it does                                                                            |
|-----------------------------------|------------------------------------------|-------------|-----------------------------------------------------------------------------------------|
| Fragment threshold (bytes)        | `DAPPS_FRAGMENT_THRESHOLD_BYTES`         | `4096`      | Payloads strictly larger than this get split into N fragments at submit. `0` disables.  |
| Fragment reassembly timeout (s)   | `DAPPS_FRAGMENT_REASSEMBLY_TIMEOUT_SECONDS` | `604800` (7 d) | Drop incomplete reassembly buffers older than this.                                  |

### Route gossip

| Name                              | Env var                                  | Default     | What it does                                                                            |
|-----------------------------------|------------------------------------------|-------------|-----------------------------------------------------------------------------------------|
| Route gossip staleness (hours)    | `DAPPS_ROUTE_GOSSIP_STALENESS_HOURS`     | `6`         | Minimum hours between consecutive `routes` pulls from the same neighbour. The piggyback gate skips gossip if the previous pull is younger than this. `0` disables route gossip entirely. |

### Updates

| Name                  | Env var                       | Default | What it does                                                              |
|-----------------------|-------------------------------|---------|---------------------------------------------------------------------------|
| Update check enabled  | `DAPPS_UPDATE_CHECK_ENABLED`  | `true`  | Periodically poll GitHub Releases. Powers the dashboard banner.           |

### Heartbeat

| Name                   | Env var                          | Default | What it does                                                          |
|------------------------|----------------------------------|---------|-----------------------------------------------------------------------|
| Heartbeat enabled      | `DAPPS_HEARTBEAT_ENABLED`        | `true`  | Publishes an operational snapshot to MQTT topic `dapps/metrics/heartbeat`. |
| Heartbeat interval (s) | `DAPPS_HEARTBEAT_INTERVAL_SECONDS` | `60`  | Cadence; minimum 10 s.                                                |

## Versioning

The session-protocol version is the suffix on the prompt - `DAPPSv1>` today; `DAPPSv2>` for any future incompatible cut. The policy:

- **Forward-compatible additions stay on the current version.** New optional `ihave` headers and new commands ride the existing prompt; receivers ignore unknown headers and respond `?` to unrecognised commands.
- **Bump the prompt on any incompatible wire change.** A clean version cut beats sticking a patch on `v1` and hoping every implementation interprets it the same way.
- **Newer implementations should speak older versions** for one-way compatibility.
- **The UDP / MeshCore datagram bearer codec versions independently** - different format, different schedule. (MeshCore also versions its shared compression dictionary on the wire, so a dictionary change can't silently corrupt a mixed-version fleet.)

**Pre-shipping caveat**: while DAPPS has no non-author operators on the air, breaking changes to either format are still fair game without a version bump. The cost of compatibility tape that nobody benefits from is real. The policy fully kicks in when the first independent operator picks DAPPS up.

## Where the config lives on disk

Everything is in the SQLite database at `/var/lib/dapps/dapps.db` (Linux/systemd) or `data/dapps.db` (run-from-anywhere). The file is the only state worth backing up.
