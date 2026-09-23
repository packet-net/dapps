# Reference

Complete shape of the local app interface - every endpoint, every topic, every property.

If you're new, read [Concepts](concepts.md) first and walk through the [tutorial](tutorial.md). This page assumes you know what `app@CALLSIGN` means and have written one app already.

The on-air protocol DAPPS speaks **between nodes** is summarised in the section on [DAPPSv1 wire format](#dappsv1-wire-format-summary) at the bottom; most apps never need it.

The daemon also serves a generated OpenAPI description of its REST surface at `/openapi/v1.json`, with an interactive explorer at `/scalar`. Both sit behind the admin login like the rest of the dashboard; the `/AppApi` endpoints they describe take the app token as documented below.

## MQTT

Embedded broker, default port 1883 (TCP). Speak MQTT 5 - clean session is fine and recommended. The broker holds no persistent state; messages are durable in DAPPS's SQLite queue, the broker is just the real-time delivery channel.

The same broker is also exposed at **`/mqtt` over WebSocket** on the dashboard's HTTP listener (default `:5000`) - same topics, same CONNECT auth, same interceptors. Browsers connect with sub-protocol `mqtt`:

```js
// mqtt.js, in a browser
const client = mqtt.connect("ws://<host>:5000/mqtt", {
    protocolVersion: 5,
    clientId: "myapp-" + crypto.randomUUID(),
});
```

paho-mqtt with `transport='websockets'` works the same way. A worked browser example is in [`examples/file-transfer/`](https://github.com/packet-net/dapps/tree/master/examples/file-transfer) on the repo.

### Topics

#### `dapps/in/<app>` - inbox

DAPPS publishes one message here for each pending delivery to `<app>` on this node. Subscribe with QoS 1.

**Replay on subscribe**: every pending message arrives the moment you subscribe - even ones submitted while the app was offline. There is no separate "fetch backlog" call.

**Payload**: the raw bytes the sender submitted. No envelope, no length prefix, no encoding imposed by DAPPS.

**User properties**:

| Name           | Type                  | Meaning                                                                  |
|----------------|-----------------------|--------------------------------------------------------------------------|
| `dapps-id`     | string (7 hex chars)  | The message id. Use this as the idempotency key.                         |
| `dapps-source` | string (callsign)     | The originating callsign. Use as destination for replies.                |
| `dapps-ttl`    | string (decimal int)  | *Optional.* Residual lifetime in seconds. Absent if the sender didn't set a TTL. |

#### `dapps/out/<app>/<dest-callsign>` - outbox

Publish here to submit a message for `<app>` running at `<dest-callsign>`. Use QoS 1.

The destination is encoded in the topic, not the payload. So submitting to `dapps/out/chat/G7XYZ` is "send to the `chat` app at G7XYZ"; submitting to `dapps/out/chat/M0LTE-1` is a different message to a different node, even if the bytes are identical.

**Payload**: the raw bytes you want delivered. DAPPS does not transform them.

**User properties** (all optional):

| Name        | Type                          | Meaning                                                                                                  |
|-------------|-------------------------------|----------------------------------------------------------------------------------------------------------|
| `dapps-ttl` | string (positive decimal int) | Initial TTL in seconds. Decremented at each forwarder by the time the message dwelt in that hop's queue. Malformed values (non-integer, zero, negative) are silently ignored - the message is queued without a TTL. |

#### `dapps/ack/<app>` - acknowledge

Publish a 7-character message id (as the UTF-8 payload) to mark it processed. Idempotent - re-acking is a no-op. Until you ack, DAPPS treats the message as still pending and will redeliver on next subscribe.

QoS 1 is fine; QoS 0 is acceptable since at worst the message gets redelivered (and your idempotency check will catch it).

### Authentication

When the operator turns on `auth-required` (off by default):

- CONNECT must include `username=<app>` and `password=<token-plaintext>`.
- An app authenticated as `chat` may only publish to `dapps/out/chat/*` and `dapps/ack/chat`, and only subscribe to `dapps/in/chat`. Cross-app traffic is rejected with `NotAuthorized`.

Tokens are minted from the dashboard's `/AppTokens` page (admin-cookie gated).

## REST

Same queue, pull-based. All endpoints under `/AppApi`. Default port 5000; set `ASPNETCORE_URLS` to bind elsewhere.

### `POST /AppApi/outbound`

Submit a message.

**Request body**:

```json
{
  "app": "chat",
  "destCallsign": "G7XYZ",
  "payload": "<base64-encoded bytes>",
  "ttl": 300
}
```

| Field          | Type                  | Required | Notes                                                  |
|----------------|-----------------------|----------|--------------------------------------------------------|
| `app`          | string                | yes      | App slug. Must not be blank.                           |
| `destCallsign` | string                | yes      | Destination callsign. Must not be blank.               |
| `payload`      | bytes (base64 in JSON)| yes      | Must be at least one byte. Empty payloads return `400`.|
| `ttl`          | int seconds           | no       | Must be positive if present. `0` or negative returns `400`. |

**Response**: `200 OK` with `{ "id": "abc1234" }`.

**Errors**: `400` on missing/empty fields or non-positive TTL. `403` if `auth-required` is on and the bearer token doesn't match `app`.

```bash
curl -X POST http://localhost:5000/AppApi/outbound \
    -H 'Content-Type: application/json' \
    -d '{"app":"chat","destCallsign":"G7XYZ","payload":"aGVsbG8=","ttl":300}'
# {"id":"abc1234"}
```

### `GET /AppApi/inbound/{app}`

List currently unacknowledged messages for `{app}`.

**Response**: `200 OK` with a JSON array. Each entry:

```json
{
  "id": "abc1234",
  "sourceCallsign": "G7XYZ",
  "payload": "<base64-encoded bytes>",
  "ttl": 287
}
```

| Field            | Type                  | Notes                                                                                  |
|------------------|-----------------------|----------------------------------------------------------------------------------------|
| `id`             | string                | Stable across redeliveries. Use as idempotency key.                                    |
| `sourceCallsign` | string                | Same semantics as `dapps-source` on MQTT - originating callsign.                       |
| `payload`        | bytes (base64)        | Raw payload.                                                                           |
| `ttl`            | int seconds, nullable | **Residual** lifetime - initial TTL minus dwell time on this node. `null` if no TTL.    |

```bash
curl http://localhost:5000/AppApi/inbound/chat
# [{"id":"abc1234","sourceCallsign":"G7XYZ","payload":"aGVsbG8=","ttl":287}]
```

### `POST /AppApi/inbound/{app}/{id}/ack`

Mark `{id}` as processed. Idempotent.

**Response**: `204 No Content`.

```bash
curl -X POST http://localhost:5000/AppApi/inbound/chat/abc1234/ack
```

### Authentication

When `auth-required` is on, send `Authorization: Bearer <token>` on every `/AppApi/*` request. The token's app scope must match the `{app}` path segment (or the `app` field in the body for `POST /AppApi/outbound`); mismatches return `403`.

## Idempotency contract

DAPPS guarantees **at-least-once** delivery. The same message can arrive twice or more in the following scenarios:

1. The app processed the message but DAPPS crashed before recording the ack.
2. The app processed the message but failed to publish the ack (broker disconnect, network blip).
3. The app published the ack but DAPPS hasn't yet seen it (rare, but possible during shutdown).

In every case, the redelivered message arrives with **the same `dapps-id`**. The id is content-addressed:

```
id = sha1(salt_le_8_bytes ++ payload)[:7]
```

Where `salt` is set to the submission timestamp in milliseconds since epoch. Two submissions of the same payload, milliseconds apart, get different ids.

The recommended app pattern is:

```
on receive (id, payload):
    if seen(id):
        ack(id)               # drain the queue without re-doing work
        return
    do_real_work(payload)
    record_seen(id)           # before the ack, in the same transaction if possible
    ack(id)
```

The `seen` set should be persistent (SQLite, Redis, a file). An in-memory `set` works for demos but loses every memory of past work on restart, which means on the first connect after a restart the app will redo every still-unacked message.

A `seen` set can be size-bounded:

- **Bounded by TTL**: keep `(id, expiry)` rows; sweep periodically. The TTL on the row should comfortably exceed the longest expected redelivery window.
- **Bounded by count** (LRU): cheap, works in practice, fails badly if a backlog of old still-pending messages exceeds the LRU capacity.

If you process a message and the work is itself idempotent (e.g. UPSERT-shaped database mutation), you can skip the `seen` set entirely - at-least-once redelivery becomes a non-issue. Often the easiest path.

## Limits

DAPPS does not impose a hard payload-size limit at the app interface - submit any byte array. In practice:

- **The on-air bearer.** AX.25 frames are typically 256 bytes; a 4 KB payload becomes ~16 frames per hop, which under realistic packet-radio conditions might mean retries and noticeable latency.
- **The fragment threshold** (default 4 KB; operator-configurable). Above this, the message is split into N fragments at submit and reassembled at the receiver. This works transparently - your app sees one inbound message regardless - but very large messages (MB-scale) are not what DAPPS is for.
- **The id collision space** is 2^28 (~268M unique ids). At any realistic app traffic level, collisions are not a concern. If your app submits millions of distinct messages per day from a single source, think about it; otherwise don't.

## Topic / endpoint summary

| What you want to do            | MQTT                                              | REST                                                |
|--------------------------------|---------------------------------------------------|-----------------------------------------------------|
| Receive incoming messages      | Subscribe `dapps/in/<app>` (QoS 1)                | `GET /AppApi/inbound/{app}` (poll)                  |
| Send a message                 | Publish `dapps/out/<app>/<dest>` (QoS 1)          | `POST /AppApi/outbound`                             |
| Acknowledge a message          | Publish id to `dapps/ack/<app>`                   | `POST /AppApi/inbound/{app}/{id}/ack`               |
| Read message id                | `dapps-id` user property                          | `id` field on inbound JSON                          |
| Read source callsign           | `dapps-source` user property                      | `sourceCallsign` field                              |
| Read residual TTL              | `dapps-ttl` user property (absent if no TTL)      | `ttl` field (`null` if no TTL)                      |
| Set TTL on submit              | `dapps-ttl` user property                         | `ttl` field                                         |
| Opt into ordered delivery      | `dapps-stream` user property                      | `streamId` field                                    |
| Set ordering gap policy        | `dapps-stream-gap-timeout` user property          | `streamGapTimeoutSeconds` field                     |
| Read delivered stream / seq    | `dapps-stream`, `dapps-stream-seq` properties     | (not surfaced on REST inbound today)                |

## DAPPSv1 wire format (summary)

What DAPPS speaks between nodes. Most apps never need this; it's here for the curious.

A session over the bearer (AGW today) starts with a prompt:

```
DAPPSv1>
```

Then a back-and-forth of one-line commands and responses:

| Command                                         | Direction        | Meaning                                              |
|-------------------------------------------------|------------------|------------------------------------------------------|
| `ihave id=<7hex> dst=<callsign> sz=<bytes> ttl=<seconds> [src=<callsign>] [mid=<id> frag=<n>/<m>] [sid=<stream> sn=<seq> gt=<seconds>]` | sender → receiver | "I have this message; do you want it?"             |
| `send`                                          | receiver → sender | "Yes, send it."                                     |
| `?`                                             | receiver → sender | "Already have it / don't recognise this command."   |
| `data <bytes>`                                  | sender → receiver | The payload, exactly `<sz>` bytes.                  |
| `ack <id>`                                      | receiver → sender | "Got it; hash matches."                             |
| `peers`                                         | either           | "Tell me your known peers."                         |
| `peer <callsign> source=<n\|d> [port=<byte>]`   | reply            | One per known peer.                                 |
| `end`                                           | reply            | End of `peers` response.                            |
| `rev <id>[,<id>...]`                            | either           | "Send me anything you're holding for these callsigns." |

Headers on `ihave` are forward-compatible - receivers ignore unknown ones. New optional fields (e.g. `src=` for source tracking, `mid=` + `frag=N/M` for multi-part, `sid=`/`sn=`/`gt=` for opt-in ordering) ride the existing `DAPPSv1>` prompt. Breaking changes bump the prompt to `DAPPSv2>`.

The full wire spec - prompt protocol, every line format, the binary datagram codec, beacons - lives in [Implement DAPPS](../implement.md). Read that one if you're writing a second-source node implementation; the summary table here is enough for app authors.

## Message ordering (opt-in)

DAPPS doesn't order messages by default. Each submission is independent; under retries and routing reconvergence the receiver can see them in any order. For most apps this is correct: idempotent or content-addressed work doesn't care.

When an app does care - chat transcripts, telemetry sequences, change-log streams - opt-in ordering is available. Setting `streamId` on a submission tags the message as part of a per-sender ordered stream; the daemon mints a monotonic sequence number and the receiving daemon delivers messages on that stream in submit order.

### Opting in

REST:

```bash
curl -sS -X POST http://localhost:5086/AppApi/outbound \
  -H 'content-type: application/json' \
  -d '{
    "app": "chat",
    "destCallsign": "M0LTE",
    "payload": "aGVsbG8=",
    "streamId": "c1",
    "streamGapTimeoutSeconds": 600
  }'
```

MQTT:

```
publish dapps/out/chat/M0LTE
  user-property dapps-stream=c1
  user-property dapps-stream-gap-timeout=600
  payload <bytes>
```

`streamId` is sender-scoped: two senders can pick the same id without colliding because the receiver keys its cursor on `(originator-callsign, streamId)`. Pick something short (it travels on every wire frame for that stream).

`streamGapTimeoutSeconds` chooses the policy when a message is missing:

- **`0` (default, "strict")**: stall forever waiting for the missing seq. Later messages park until the gap fills. Use when you'd rather wait than skip.
- **`>0` ("timeout")**: stall for that many seconds, then skip past the gap and deliver waiting messages. Use when stale data is worse than missing data.

### What the receiver sees

Inbound messages tagged with a stream show two extra MQTT user properties:

- `dapps-stream` - the stream id the sender chose.
- `dapps-stream-seq` - the seq within that stream, ascending.

Apps that don't care can ignore them; apps that opted in can use them to detect stream id changes (the sender rotated to a fresh stream after a reset) or to assert seq monotonicity for their own bookkeeping.

### Tradeoffs

- **Latency cost**. One missing message stalls the whole stream until it arrives or the timeout fires. On lossy radio links this is real - opt-in is the right default.
- **Sender resets**. The sender persists its counter to disk; a fresh install / wiped database starts back at `sn=1`. Re-using the same `streamId` after a reset will cause receivers to drop the new messages as `stream-stale` (their cursor is well past `sn=1`). Mitigate by appending a short epoch suffix to the stream id when you reset (e.g. `chat:tom.2`).
- **End-to-end semantics**. Ordering is enforced at the receiving daemon, not at intermediate forwarders. Hops can reorder, retry, and flood freely - the trio rides the envelope verbatim.
- **Forward compatibility**. A daemon that doesn't understand `sid`/`sn`/`gt` ignores the keys and delivers each message immediately. Apps subscribed to a partially-ordering-aware mesh see ordered delivery only between aware nodes.

The dashboard's `/Streams` page surfaces both sender-side counters and receiver-side cursors plus pending row counts; a stalled stream shows up as a non-empty pending column.

## See also

- [Concepts](concepts.md) - the mental model.
- [Tutorial](tutorial.md) - hello-world end-to-end.
- [Sample gallery](gallery.md) - worked examples for common app shapes.
