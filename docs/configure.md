# Configure

Every operator-tunable setting on a DAPPS node has three configuration surfaces:

1. **A persisted default** in the SQLite database, seeded on first start.
2. **An environment variable**, of the form `DAPPS_SCREAMING_SNAKE_NAME`, which **replaces the default when the setting is first seeded**. After that the stored value wins.
3. **The dashboard's Settings page** (or the `POST /Config` REST API, which takes the whole config object), which writes a new value to the persisted row at any time.

Most settings take effect within seconds of saving, with no restart. The MQTT and UDP listener ports, the routing algorithm, and MeshCore's enable, port and region need a restart. The dashboard's own address (`ASPNETCORE_URLS`) is env-only.

A few knobs aren't on the Settings page: the default port, the routing algorithm and the multi-part message timings. Change those with `POST /Config`, which changes only the settings you send. Route gossip staleness can be set with its env var before first start, or with `POST /Config` (from 0.48.1).

For scripts, the easy route is to send `Content-Type: application/json` (the cookie from `/Login` is still needed). Calls that carry JSON need nothing else, and neither do body-less ones that you mark that way:

```bash
curl -b jar.txt -H 'Content-Type: application/json' -X POST http://localhost:5000/Update/check
```

`POST /TxControl/stop` never needs a token, so it works from any script or stale page. Any other POST without a JSON content type, such as a plain `curl -X POST /Probes/run` or a form post, needs the dashboard's antiforgery token sent as a `RequestVerificationToken` header; every dashboard page carries it in a `request-verification-token` meta tag.

> **Upgrading with scripts:** since 0.55, a body-less admin POST from a script gets `400` until you add `-H 'Content-Type: application/json'`. JSON calls and `/AppApi` are unchanged.

> **Deployment-managed mode (`DAPPS_ENV_MANAGED=true`)**: when a supervising deployment (a packet.net node host, say) sets `DAPPS_ENV_MANAGED=true`, every *set* `DAPPS_*` env var becomes deployment-managed config instead - re-applied over the persisted row at **every** start, with the matching dashboard fields badged "managed by environment" and rendered read-only. `DAPPS_ENV_MANAGED` itself is a mode switch read each start, never stored. Leave it unset for the standalone installs documented here: with it unset (or `false`) env vars keep the first-start-only semantics above and the dashboard stays in charge.

## Show the current persisted config

```bash
dapps --show-config
```

Prints the saved settings as `DAPPS_SCREAMING_SNAKE=value` pairs, **without booting the host**. Useful when the daemon won't start and you need to see what state it'd boot into.

It finds the database the same way `dapps reset-admin-password` does: the current folder first, then `/var/lib/dapps` and `/var/lib/packetnet/apps/dapps`. If it finds more than one, or none, or a database with no settings in it, it says so and exits non-zero; it never creates a database. Point it at one with `--db PATH` (the file, or the folder dapps runs in).

The state folder belongs to the `dapps` user, so on a packaged install run it as that user:

```bash
sudo -u dapps dapps --show-config
```

Secrets (the admin password hash and salt, the RHP password, MeshCore keys) show as `(set, hidden)`, so the output is safe to paste into a support thread.

## The knobs

### Identity and packet node

| Name             | Env var          | Default     | What it does                                                              |
|------------------|------------------|-------------|---------------------------------------------------------------------------|
| Callsign         | `DAPPS_CALLSIGN` | `N0CALL`    | DAPPS's own callsign with SSID, e.g. `M0LTE-7`, not your node's. On the placeholder the daemon starts but won't bind or transmit until you set one. |
| Node host        | `DAPPS_NODE_HOST`| `localhost` | TCP host of your packet node (BPQ, XRouter, etc.).                        |
| Node bearer      | `DAPPS_NODE_BEARER` | `agw`    | Which host protocol DAPPS speaks to the packet node: `agw` (BPQ, Direwolf, AGWPE, ...) or `rhpv2` (XRouter, pdn, future RHPv2-capable BPQ). See [Connect a node](connect/index.md). |
| AGW port         | `DAPPS_AGW_PORT` | `8000`      | TCP port the packet node's AGW interface listens on. Used when `Node bearer = agw`, and with `rhpv2` to hear AGW discovery channels (their beacons then go out through the RHPv2 host, even if another program serves AGW there; see [Discovery channels](discovery-and-routing.md#discovery-channels)). |
| RHPv2 port       | `DAPPS_RHP_PORT` | `9000`      | TCP port the packet node's RHPv2 listener is on. Used when `Node bearer = rhpv2`. |
| RHPv2 user       | `DAPPS_RHP_USER` | _empty_     | Username for RHPv2 authentication. Leave empty if your packet node accepts unauthenticated RHPv2. |
| RHPv2 password   | `DAPPS_RHP_PASS` | _empty_     | Password matching the RHPv2 user.                                         |
| Default port | `DAPPS_DEFAULT_PORT` | `1` | The radio port a neighbour without one of its own is called on, by the node's own number for it: BPQ's `PORTNUM`, XRouter's `PORT=`, pdn's port as `PORTS` lists it. `DAPPS_DEFAULT_BEARER_PORT` and `DefaultBearerPort`, its names up to 0.52, counted from 0; they still work, one higher, but are deprecated. |

#### Radio port numbers

Every radio port in DAPPS is the node's own number for it, the one you'd type in `C 2 CALL` at its prompt: BPQ's `PORTNUM`, XRouter's `PORT=`, pdn's port as `PORTS` lists it, and on Dire Wolf the `PortN` of its AGW port list (channel 0 is `1`). That goes for the dashboard, the logs and the REST API: `POST /Neighbours` takes `Port`, an AGW discovery channel's `ChannelKey` is the port number, `GET /Probes` shows `LastPort`, and `POST /Config` takes `DefaultPort`.

Up to 0.52 DAPPS counted ports from 0. The old REST names still work, counting from 0, but are deprecated: `BearerPort` on `POST /Neighbours` and `DefaultBearerPort` on `POST /Config`. A port saved by an older DAPPS is converted on upgrade, once, so the same radio ports are used; see the upgrading notes on the [BPQ](connect/bpq.md#upgrading-from-052-or-earlier) and [XRouter](connect/xrouter.md#upgrading-from-052-or-earlier) pages.

### App-interface ports

| Name              | Env var                  | Default | What it does                                              |
|-------------------|--------------------------|---------|-----------------------------------------------------------|
| MQTT broker port  | `DAPPS_MQTT_PORT`        | `1883`  | Port the embedded MQTT broker binds.                      |
| UDP listener port | `DAPPS_UDP_LISTEN_PORT`  | `0`     | UDP datagram bearer listen port. `0` disables.            |

The dashboard / REST endpoint port is configured via `ASPNETCORE_URLS` (a .NET convention), e.g. `ASPNETCORE_URLS=http://127.0.0.1:5000`.

### Authentication

| Name         | Env var             | Default | What it does                                                              |
|--------------|---------------------|---------|---------------------------------------------------------------------------|
| Auth required| `DAPPS_AUTH_REQUIRED` | `true` on a new install; an upgrade keeps its stored value | When true, MQTT / REST app-interface clients must present a per-app token. |

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
| Node-prompt application command | `DAPPS_NODE_PROMPT_APPLICATION_COMMAND` | `DAPPS`         | The application name typed at the BPQ node prompt to enter the DAPPS slot. Override if your `APPLICATION` line uses a different name. One word: letters, digits, `-` or `_`, up to 16 characters. |
| Discovery airtime budget (s/hr) | `DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR` | `0` | Trailing-hour cap on discovery transmissions (beacons + solicits + probes). `0` disables the cap. |

### Links and polling

| Name                       | Env var                          | Default | What it does                                                  |
|----------------------------|----------------------------------|---------|---------------------------------------------------------------|
| Scheduled poll enabled     | `DAPPS_SCHEDULED_POLL_ENABLED`   | `false` | Periodically calls every neighbour so it can send what it holds for us. |
| Poll interval (hours)      | `DAPPS_POLL_INTERVAL_HOURS`      | `6`     | Sweep cadence when scheduled polling is on.                   |
| Compression enabled        | `DAPPS_COMPRESSION_ENABLED`      | `true`  | Sends message payloads to neighbours zstd-compressed when that saves at least 32 bytes. Can be overridden per neighbour on the Topology page. |
| Link hold (s)              | `DAPPS_SESSION_TAIL_SECONDS`     | `120`   | Keeps a link to a neighbour open until it has been idle this long, so follow-up messages in either direction go without a new connection. The link is held for the lower of both ends' values. 0 = off; at most 600. Can be overridden per neighbour on the Topology page. |
| Largest message (bytes)    | `DAPPS_MAX_MESSAGE_BYTES`        | `0`     | The largest message this node takes from a neighbour. Neighbours don't send bigger ones. `0` = no limit, apart from the 16 MB per hop every node holds to; a value above 16 MB acts as 16 MB. |

### Multi-part messages

| Name                              | Env var                                  | Default     | What it does                                                                            |
|-----------------------------------|------------------------------------------|-------------|-----------------------------------------------------------------------------------------|
| Fragment threshold (bytes)        | `DAPPS_FRAGMENT_THRESHOLD_BYTES`         | `4096`      | Payloads strictly larger than this get split into N fragments at submit. `0` disables, but each message is still limited to 16 MB per hop.  |
| Fragment reassembly timeout (s)   | `DAPPS_FRAGMENT_REASSEMBLY_TIMEOUT_SECONDS` | `604800` (7 d) | Drop incomplete reassembly buffers older than this.                                  |
| Received-message memory (s)       | `DAPPS_RECEIVED_MEMORY_SECONDS`             | `2592000` (30 d) | Longest a received message is remembered, so a repeat isn't delivered or passed on twice: a message with a TTL is remembered until that runs out plus an hour, capped at this; one with no TTL for this long. |

### Route gossip

| Name                              | Env var                                  | Default     | What it does                                                                            |
|-----------------------------------|------------------------------------------|-------------|-----------------------------------------------------------------------------------------|
| Route gossip staleness (hours)    | `DAPPS_ROUTE_GOSSIP_STALENESS_HOURS`     | `6`         | Minimum hours between consecutive `routes` pulls from the same neighbour. The piggyback gate skips gossip if the previous pull is younger than this. `0` disables route gossip entirely. |

### Updates

| Name                  | Env var                       | Default | What it does                                                              |
|-----------------------|-------------------------------|---------|---------------------------------------------------------------------------|
| Update check enabled  | `DAPPS_UPDATE_CHECK_ENABLED`  | `true`  | Periodically check for a newer release (the apt repository on an apt install, GitHub Releases otherwise). Powers the dashboard banner. |

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

Everything is in the SQLite database at `/var/lib/dapps/dapps.db` (Linux/systemd) or `dapps.db` in the working directory, inside `data/` if that folder exists (run-from-anywhere). The file is the only state worth backing up.
