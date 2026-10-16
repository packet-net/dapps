# Transmission audit log

Every outbound transmission a DAPPS node makes is logged to a persistent table along with the reason for the transmission. Useful for post-mortems ("why did we transmit X at Y?"), regulatory compliance, and debugging weird discovery interactions.

## What gets logged

Every site where the daemon puts bytes on a bearer:

| Kind                | What it covers                                                                            |
|---------------------|-------------------------------------------------------------------------------------------|
| `beacon`            | Discovery channel beacon broadcasts.                                                      |
| `solicit`           | Solicit transmissions (scheduled or operator-triggered).                                  |
| `solicit-reply`     | Replying to a solicit we received.                                                        |
| `probe`             | Connected-mode probe sessions (direct or transitive).                                     |
| `probe-nodeprompt`  | Probe via node prompt.                                                                    |
| `forward`           | Outbound forwarder shipping a message to a neighbour.                                     |
| `forward-flood`     | Per-recipient transmission of a flood-routed message.                                     |
| `poll`              | Reverse-poll request to a peer (scheduled or operator-triggered).                         |
| `heartbeat`         | The periodic MQTT publish to `dapps/metrics/heartbeat`.                                   |
| `tx-control`        | Transmitting stopped or resumed, by the dashboard's stop button or the [kill-switch](dev-time-tx-kill-switch.md). Nothing is sent. |

Each row carries: timestamp, kind, bearer (`agw` / `rhpv2` / `udp` / `mqtt`), channel key (e.g. bearer port), target callsign, message id (when forwarding a specific message), bytes, duration, success boolean, reason, and an error tag on failure.

## The "why" field

The reason is the load-bearing field. Examples:

- `scheduled beacon emit`
- `scheduled solicit cadence`
- `solicit-reply to G7XYZ`
- `scheduled probe sweep`
- `operator-triggered probe (REST)`
- `forwarder tick: route via M0LTE-1`
- `flood to neighbour (hop budget 3)`
- `scheduled poll sweep (drained 2)`
- `operator-triggered poll (REST)`
- `periodic heartbeat publish`

## Where to read it

- **Dashboard**: the **Transmissions audit** panel on the Diagnostics page. Filter by kind / target callsign / only-failures, auto-refresh every 10 s.
- **REST**: `GET /Transmissions?kind=probe&target=M0LTE-1&onlyFailures=true&limit=300` returns a JSON array.
- **MQTT** (opt-in): set `TransmissionAuditMqttPublish` to `true` (see below) and every row is also published live to `dapps/audit/tx` as JSON. Useful for scraping into an existing MQTT-shaped monitoring stack.

## Configuration

These three aren't on the Settings page and have no env var. Change them through the REST API: `GET /Config`, edit the field, and `POST /Config` with the whole object back.

| Setting                          | Default | What it does                                                                            |
|----------------------------------|---------|-----------------------------------------------------------------------------------------|
| `TransmissionAuditEnabled`       | `true`  | Master switch. Turn off only if you're storage-constrained on a tiny SD card.           |
| `TransmissionAuditRetentionDays` | `90`    | The TTL sweeper deletes rows older than this. Set to `0` to disable automatic retention.|
| `TransmissionAuditMqttPublish`   | `false` | When true, publishes each row to MQTT topic `dapps/audit/tx` (non-retained).            |

## Storage cost

Roughly: a node beaconing every 10 minutes on one channel = 144 beacon rows/day. Add probes (a sweep can produce dozens), forwards (every message), polls (every poll target), and a 60 s heartbeat (1,440/day), and a busy node lands at a few thousand rows per day. Over the default 90-day retention that's a few hundred thousand rows - SQLite handles that comfortably.

If you're really storage-constrained (Raspberry Pi Zero on a 4 GB SD card), drop the retention to 30 days or less, or disable the audit entirely.

## Worked examples

### "Did this node transmit anything between 02:00 and 03:00 last Tuesday?"

```
GET /Transmissions?limit=500
```

Then filter the response client-side by the `at` field. Or use the Transmissions audit panel on the dashboard's Diagnostics page.

### "Why did message abc1234 fail to ship?"

```
GET /Transmissions?onlyFailures=true&limit=200
```

Look for `forward` or `forward-flood` rows with `messageId=abc1234` in the response. The `reason` and `errorTag` fields explain the failure.

### "Show every probe attempt to G7XYZ this week"

```
GET /Transmissions?kind=probe&kind=probe-nodeprompt&target=G7XYZ&limit=500
```

## What does **not** get logged

- **Inbound** transmissions are not logged here - this is an outbound audit. Use the dashboard's **Messages > Live arrivals** tab (or the `/Events/inbound` SSE feed it reads) for inbound visibility, or the `messages` table for inbound persistence.
- **Acks and naks** the daemon emits inside an existing inbound session aren't currently audited - they ride the same TCP session as the inbound `data` and the outbound forwarder's `ack` - so they'd be high-frequency and tightly correlated with inbound rows. May change in a follow-up if there's demand.
- **Per-frame bearer activity** (AGW frames, UDP datagram bytes) isn't audited at the frame level. The audit log is at the DAPPS-protocol-event level, not the wire-byte level. For wire-level analysis use the bearer's own logs (BPQ's MHEARD, etc.).
