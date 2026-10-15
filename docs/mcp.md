# MCP for assistants

DAPPS exposes its operator surface to AI assistants via [MCP - the Model Context Protocol](https://modelcontextprotocol.io). An assistant connected to your node's `/mcp` endpoint can do everything an operator can do from the dashboard, plus a few things only they can do (like trigger an `--apply-update` to close the deploy loop after merging a PR).

## When this is useful

- **Operations.** "What's the state of my node? Are there pending updates? Is anything broken?" gets a useful structured answer rather than a long screenshot description.
- **Diagnosis.** "Why didn't message X arrive at G7XYZ?" can be answered by walking the dropped-messages, decision-events, and probed-nodes tables - exactly the path you'd walk by hand on the dashboard.
- **Steering experiments.** "Probe via this NODECALL", "send a test message to that callsign", "flip the routing algorithm for this restart" - the assistant can drive multi-step exploration without round-trips to a human.
- **Closing the deploy loop.** Merge a PR → wait for CI → trigger an update on the destination node → smoke-test the new version, all in one conversation.

## How to connect

Most MCP clients want a URL. Point yours at:

```
http://<node>:5000/mcp
```

The endpoint is **open access** - no admin cookie required, since MCP clients don't carry one. If your node is on the public internet, firewall the path or run the daemon behind an authenticating reverse proxy. (Same applies to `/Health` and `/Operational`.)

For Claude Code specifically:

```bash
claude mcp add --scope user --transport http dapps http://<node>:5000/mcp
```

## What's exposed - by category

### Read-only

| Tool                          | What it returns                                                                                |
|-------------------------------|------------------------------------------------------------------------------------------------|
| `get_system_options`          | The persisted config row.                                                                      |
| `get_operational_snapshot`    | The same JSON document `/Operational` and the heartbeat publish.                               |
| `get_recent_events`           | The decision-events ring with reasons.                                                         |
| `list_neighbours`             | Manual neighbour entries.                                                                      |
| `list_discovered_peers`       | Peers heard via beacons.                                                                       |
| `list_discovery_channels`     | Configured discovery channels with cadences and budgets.                                       |
| `list_probed_nodes`           | Per-callsign probe state, including source flag.                                               |
| `list_polled_nodes`           | Per-target poll state.                                                                         |
| `list_learned_routes`         | The routing graph as the passive-flood algorithm sees it.                                      |
| `list_route_hints`            | Manual routing overrides.                                                                      |
| `list_recent_messages`        | Recent message rows from the queue.                                                            |
| `list_dropped_messages`       | Recently dropped, with reasons.                                                                |
| `list_discovered_paths`       | Discovered hop-paths from the routing layer.                                                   |
| `get_message`                 | Single-message lookup by id.                                                                   |
| `get_update_status`           | Current version + latest known release + last apply phase.                                     |
| `list_transmissions`          | The [transmission audit](audit.md) log, filterable by kind, target and failures.               |

### Actions

| Tool                          | What it does                                                                                   |
|-------------------------------|------------------------------------------------------------------------------------------------|
| `update_config`               | Apply a partial update to the persisted config. Risky knobs (callsign, ports) are excluded.    |
| `send_test_message`           | Submit an outbound message via the same path an app would take.                                |
| `run_probe`                   | Probe a single callsign now (bypasses the airtime budget - explicit human action).             |
| `run_probe_sweep`             | Probe every known target.                                                                      |
| `probe_via_nodecall`          | Phase-2b: connect to a non-DAPPS NODECALL, type the application command, probe.                |
| `run_solicit`                 | Send a solicit on a discovery channel.                                                         |
| `run_poll`                    | Poll a single target for queued messages.                                                      |
| `run_poll_sweep`              | Poll every known target.                                                                       |
| `check_for_updates`           | Check for a newer release now (the apt repository on an apt install, GitHub Releases otherwise). |
| `trigger_update`              | Write the update-request marker - same code path as the dashboard's Apply button.              |

### Diagnostics - composite

| Tool                          | What it does                                                                                   |
|-------------------------------|------------------------------------------------------------------------------------------------|
| `explain_why_message_failed`  | Walk the dropped + decision-events trail for a message id and produce a narrative.             |
| `find_path_to`                | Combine routing graph + probed nodes + neighbours to explain how (or whether) a callsign is reachable. |
| `diagnose_silent_neighbour`   | For a peer that's gone quiet, walk reachability + probe history + recent activity.             |
| `summarize_recent_activity`   | Compact summary of what's happened recently.                                                   |

### Exploration - supervised

| Tool                          | What it does                                                                                   |
|-------------------------------|------------------------------------------------------------------------------------------------|
| `explore_via_neighbour`       | Multi-step traversal: probe a neighbour, ask its peers, record candidates, decide which to probe next. |
| `propose_topology_changes`    | Suggest heard peers to add as neighbours, and probe targets with long failure streaks to look into. |
| `opinion_on_route`            | Rate the quality of a route: cost, hop count, reliability based on history.                    |

## Worked examples

### "Is the node alive and what's it been doing?"

```
get_operational_snapshot
get_recent_events
```

Returns process metrics + a narrative of the last several decision points. The assistant can summarise: "uptime 3 days, 1240 messages forwarded, AGW link healthy, 2 dropped (TTL expired), 1 probe failure to G7XYZ-2 retrying."

### "Apply the latest release"

```
check_for_updates              → confirm latest is what you expect
trigger_update                 → fires the supervised apply
get_update_status              → poll until phase == Success
```

The apply itself takes ~90 s end-to-end (download → swap → restart → verify). The assistant polls and reports back when the new version is live.

### "Why didn't message abc1234 arrive at G7XYZ?"

```
explain_why_message_failed(id="abc1234")
```

Composite tool that walks the dropped table for the id, then the decision-events ring for any reason mentioning it, then the route to G7XYZ at the time of the attempt. Returns a narrative.

### "Find a way to reach M0LTE-2"

```
find_path_to(callsign="M0LTE-2")
```

Checks route hints, learned routes, discovered paths, discovered peers and neighbours, in that order, and returns the first match with where it came from, or "no path".

## Excluded from MCP for safety

A few config knobs are deliberately not in `update_config`:

- **Callsign** - wrong value takes the node off-air.
- **Node host / AGW port / MQTT port / UDP listen port** - all need a daemon restart and a wrong value bricks the daemon's connection to its packet node.

If you need to change those, use the dashboard's Settings page or set the env var and restart. The dashboard form has the field validation; the MCP tool would have to duplicate it, and the kinds of mistakes those knobs invite are exactly the kind an MCP-driven agent shouldn't make at 02:00 on a Saturday.

## Authentication

The MCP endpoint is open by default for the same reason `/Health` is - most MCP clients don't carry the dashboard's admin cookie. If you need authentication (the node is on the public internet), the right answer today is a reverse proxy in front of `/mcp` doing whatever auth your stack supports. A native bearer-token model for MCP clients may land in a future release.
