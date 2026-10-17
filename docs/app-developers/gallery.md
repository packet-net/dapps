# Sample gallery

Five small apps that demonstrate common shapes you'll build on top of DAPPS. Each is short, runnable, and chosen to surface a specific design point. None are production-ready - they're written to be read.

If you haven't yet, read [Concepts](concepts.md) and walk through the [tutorial](tutorial.md) first.

The first three are Python + paho-mqtt:

```bash
pip install paho-mqtt
```

If your node requires app tokens (new installs do), mint one on the dashboard's **App tokens** page for the script's app (`chat`, `sensor` or `pager`) and pass it in `DAPPS_TOKEN`, for example `DAPPS_TOKEN='<chat token>' python chat.py ...`. With tokens off, leave it unset.

The last two are browser apps (HTML + vanilla JS, MQTT-over-WebSocket via mqtt.js).

Run a DAPPS instance locally, replace `<your-callsign>` with your own throughout, and try the examples in any order.

The Python sources live in the repo at [`docs/examples/`](https://github.com/packet-net/dapps/tree/master/docs/examples). The browser ones live under [`examples/`](https://github.com/packet-net/dapps/tree/master/examples) at the top level.

## Group chat - `chat.py`

[chat.py on GitHub](https://github.com/packet-net/dapps/blob/master/docs/examples/chat.py)

A line-mode chat app. Each line you type at the prompt is broadcast to a fixed list of peer callsigns; incoming lines from anyone are printed inline. Run the same script (with appropriate args) on every node that wants to participate - there is no central server, no registry, no membership protocol.

```bash
# On node A (G7XYZ):
python chat.py M0LTE-1 GB7AAA

# On node B (M0LTE-1):
python chat.py G7XYZ GB7AAA

# On node C (GB7AAA):
python chat.py G7XYZ M0LTE-1
```

Type a line on any node; the others see it.

**What this demonstrates**:

- **One-to-many fan-out** - DAPPS doesn't have multicast at the app layer, so the app loops and publishes once per recipient. Each recipient sees the same `dapps-id`.
- **Reading `dapps-source`** to display "who said what."
- **TTL of 1 hour** - chat is more useful than a 5-minute reply window, less useful than yesterday's transcript.

**What this glosses over**:

- The `seen` set is in-memory; restart the script and you'll re-print the backlog. Persist it to SQLite for a real app.
- No formatting, no nicknames, no slash-commands - the wire format is "raw UTF-8 bytes." A real chat app would put a small JSON envelope around each line for sender display name, format, etc.
- Membership is hard-coded. A self-organising version would have nodes announce themselves on a "join" topic and others react.

## Sensor publisher - `sensor.py`

[sensor.py on GitHub](https://github.com/packet-net/dapps/blob/master/docs/examples/sensor.py)

A periodic publisher with no listening surface. Reads a (faked) sensor every N seconds and pushes a JSON reading to a list of subscriber callsigns. Pure submit-and-go - no `on_message`, no acks.

```bash
# Long-running mode (publishes every 60s):
python sensor.py --interval 60 --subscribers G7XYZ M0LTE-1

# One-shot from cron / a systemd timer:
python sensor.py --once --subscribers G7XYZ
```

A subscriber on the receiving side would look like the `hello.py` shape: subscribe to `dapps/in/sensor`, decode the JSON, do something with it (graph it, log it, raise an alarm). The example doesn't ship a subscriber because it's identical in shape to `hello.py` minus the reply.

**What this demonstrates**:

- **Submit-only apps.** Not every DAPPS app cares about responses. The shape is a normal MQTT publisher with QoS 1; DAPPS handles all the radio-side awkwardness.
- **Long TTL (24 h)** - a sensor reading is *useful information* well past the next reading. Compare to chat (1 h) or pager (5 m). TTL isn't "freshness urgency," it's "after this point, the message is more clutter than signal."
- **`--once` mode** - DAPPS tolerates the publisher coming and going. Submit, exit, the queue takes care of delivery.

**What this glosses over**:

- The fake sensor - `read_sensor()` returns random numbers. Replace with `psutil`, a serial-port read, an I²C call - whatever you actually want to publish.
- One JSON object per message. For higher-rate sensors you'd batch readings into a single payload to reduce per-message overhead on the radio side.

## Two-way pager - `pager.py`

[pager.py on GitHub](https://github.com/packet-net/dapps/blob/master/docs/examples/pager.py)

A messenger app with two distinct CLI modes. Run with no arguments to listen for incoming pages; run `pager.py send <callsign> <message>` to fire off a single page and exit.

```bash
# Receiver - long-running, on the recipient's node:
python pager.py
[pager] listening on dapps/in/pager

# Sender - one-shot, from anywhere:
python pager.py send G7XYZ "the eagle has landed"
-> sent to G7XYZ: 'the eagle has landed'
```

The receiver prints incoming messages with `[<source>] <text>` and notes the residual TTL when one is set.

**What this demonstrates**:

- **Two lifetimes, one app slot.** Both modes use `app=pager`; the receiver owns the `dapps/in/pager` subscription, the sender publishes to `dapps/out/pager/<dest>` once and exits. Multiple processes coexist cleanly because submit and deliver are independent paths.
- **Surfacing TTL to the user.** The receiver shows residual TTL when it's set, so the operator knows whether they're looking at a fresh page or one that survived a long backlog.
- **Short TTL (5 m).** A pager message that arrives an hour late is almost always noise.

**What this glosses over**:

- No reply-to / threading. A real messenger would build that on top - perhaps using a header in the payload to correlate.
- No history; restart the receiver and old pages re-arrive (because `seen` is in-memory). In production, persist `seen` and on startup also fetch the queue once via REST to display the backlog before the broker replay fires.

## File transfer (browser) - `examples/file-transfer/`

[examples/file-transfer/index.html on GitHub](https://github.com/packet-net/dapps/blob/master/examples/file-transfer/index.html)

A single-page browser app: pick a file, type a destination callsign, click Send. The receiving end - any other browser tab pointed at any DAPPS daemon that the message can route to - shows the file with an inline preview if it's `image/*`, or a Download link otherwise.

Open `index.html` directly from disk; no static server needed. The app speaks MQTT-over-WebSocket to the daemon's `/mqtt` endpoint, so there's no CORS surface to deal with.

```html
<!-- in the HTML, abbreviated -->
<script src="https://unpkg.com/mqtt@5/dist/mqtt.min.js"></script>
<script>
  const client = mqtt.connect("ws://localhost:5000/mqtt", { protocolVersion: 5 });
  client.on("connect", () => client.subscribe("dapps/in/files", { qos: 1 }));
  client.on("message", (topic, payload, packet) => {
    // split off a one-line JSON envelope, render the rest as the file
  });
</script>
```

**What this demonstrates**:

- **Browser-native DAPPS app**. No REST round-trip per message; mqtt.js handles the WebSocket transport.
- **Binary payloads + an app-defined envelope**. DAPPS carries `byte[]`; this app sticks a one-line JSON envelope on the front (`{name, mime, size}` + `\n` + bytes) so the receiver knows what the file is.
- **Inline preview when the MIME type allows**. Browsers natively render `image/*` from a `Blob` URL. The app falls back to a `<a download>` link otherwise.
- **TTL of 24 hours**. File transfer is "useful for a while" - longer than chat (1h), shorter than indefinite.

**What this glosses over**:

- The `seen` set is in-memory; if you reload the receive tab while a message is unacked, you'll see it again. A real app would persist `seen` (e.g. IndexedDB).
- No app-layer chunking / progressive UX. DAPPS fragments under the hood and reassembles before delivery, so the receiver gets the whole file in one event - no "I've got 30% of it" intermediate state. If you wanted progressive render, you'd split the source into N independent DAPPS messages at the app layer.
- Auth-required mode is supported by pasting a token into the form, but there's no operator-friendly first-run flow for minting / sharing the token.

## Long-form messenger (browser) - `examples/letters/`

[examples/letters/index.html on GitHub](https://github.com/packet-net/dapps/blob/master/examples/letters/index.html)

A browser app for **long-form correspondence** - subject + multi-paragraph body, conversations grouped per peer, persistent across reloads via IndexedDB. Like email, not chat. Each DAPPS message is one letter; the wire envelope is JSON: `{v, subject, body, ts}`.

```js
// the app's outbound, abbreviated
const envelope = { v: 1, subject, body, ts: new Date().toISOString() };
client.publish(`dapps/out/letters/${dest}`,
    new TextEncoder().encode(JSON.stringify(envelope)),
    { qos: 1, properties: { userProperties: { "dapps-ttl": "604800" } } });
```

The receive subscription on `dapps/in/letters` parses the same envelope and renders into the conversation timeline. The app self-loopback-dedups on `dapps-source = my-callsign` (so sending to your own callsign for testing only shows the letter once).

**What this demonstrates**:

- **MQTT-over-WebSocket from a browser** - same transport pattern as `file-transfer/`.
- **IndexedDB persistence** - conversations + read/unread state + first-run config (your callsign, the connect URL) all survive reloads.
- **App-defined envelope on top of a DAPPS payload** - one ASCII line of compact JSON for the metadata, body inside the JSON. Reader splits / parses; sender builds.
- **Sender-side composition timestamp** carried in the envelope so messages within a thread sort sensibly even when DAPPS's at-least-once delivery delivers them out of order across reconnects.
- **Long TTL (7 days)** - long-form correspondence is "still useful next week if not delivered today."

**What this glosses over**:

- Read receipts. Adding "I read your letter" notifications would be a reverse DAPPS message per inbound; out of scope.
- Multi-thread per peer. v1 is per-peer-only - if you want to discuss two unrelated topics with the same person they're in one timeline.
- Attachments. Combining with `file-transfer/`'s binary-envelope shape is a future exercise.
- Auth-required UX polish. The connect form has a Token field but no first-run helper.

## When to write your own

These five apps cover the major DAPPS-shaped patterns:

| Pattern                       | Example                | Distinguishing feature                                                              |
|-------------------------------|------------------------|-------------------------------------------------------------------------------------|
| Request → reply               | `hello.py`             | Both subscribes and publishes; replies on `dapps-source`.                           |
| Many-to-many                  | `chat.py`              | Loops `publish()` per peer; participants are symmetric.                             |
| One-shot submit               | `sensor.py` (`--once`) | No subscription; submit and exit.                                                   |
| Periodic submit               | `sensor.py` (default)  | Publisher only; no `on_message`.                                                    |
| Mixed listener + sender       | `pager.py`             | Two CLI modes share the same app slot.                                              |
| Browser-native binary file    | `file-transfer/`       | MQTT-over-WebSocket from a browser; binary payload + app-defined envelope.          |
| Browser-native long-form text | `letters/`             | MQTT-over-WebSocket; IndexedDB persistence; subject + body envelope; conversations. |

If your app fits one of these shapes, copy the closest example and adapt. If it doesn't, the [reference](reference.md) has the full surface - almost any DAPPS app boils down to combinations of subscribe, publish, and ack against your own `<app>` slot.
