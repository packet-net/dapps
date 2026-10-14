# Tutorial: hello-world DAPPS app in Python

Build a tiny DAPPS app called `hello`. It does one thing: when someone sends it a name, it replies with `hello, <name>!`. By the end you'll have:

- A working Python script that talks to your local DAPPS instance over MQTT.
- A clear understanding of how submit, deliver, and ack flow through the queue.
- An idempotent message handler - the reflex you'll reach for in every DAPPS app.

If you haven't yet, read [Concepts](concepts.md) first.

## Prerequisites

- A DAPPS instance running on `127.0.0.1:1883` (the default MQTT port). [Getting started](../getting-started.md) walks through bringing one up.
- Python 3.11+.
- `paho-mqtt` 2.x: `pip install paho-mqtt`.
- `mosquitto-clients` (for `mosquitto_pub`) on the side, so we can poke the app from the command line. On Debian/Ubuntu: `apt install mosquitto-clients`. macOS: `brew install mosquitto`.

We'll use MQTT in this tutorial. The same flow works against the REST endpoints - there's a `curl`-only version at the end.

## The full script

The complete script lives in the repo at [`docs/examples/hello.py`](https://github.com/packet-net/dapps/blob/master/docs/examples/hello.py). Drop it into a directory, run it, and skip ahead to [Try it out](#try-it-out) if you'd rather see it working before reading the explanation. Otherwise, read on - the next sections walk through it piece by piece.

## Step 1: connect to the broker

DAPPS speaks MQTT 5. We'll use paho-mqtt's v5 mode and pick a stable client id so we can debug from the broker side later if needed.

```python
import paho.mqtt.client as mqtt

client = mqtt.Client(
    callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
    protocol=mqtt.MQTTv5,
    client_id="hello-app",
)
client.on_connect = on_connect
client.on_message = on_message
client.connect("127.0.0.1", 1883, keepalive=30)
client.loop_forever()
```

`loop_forever()` blocks until the process is killed. For a real app you'd run it under systemd, supervisord, or a container restart policy - DAPPS is built to tolerate the app being down, but we still want it running when there's work to do.

## Step 2: subscribe to your inbox

When the connection completes, subscribe to your app's inbox topic - the place DAPPS publishes messages addressed to you:

```python
def on_connect(client, userdata, flags, reason_code, properties):
    client.subscribe("dapps/in/hello", qos=1)
```

`dapps/in/<app>` is the canonical inbox for app `<app>`. QoS 1 (at-least-once) is the right choice - DAPPS is already at-least-once on the wire, and matching it on the broker hop avoids "I delivered the bytes but you never acked the broker frame" surprises. Don't use QoS 0 unless you have a specific reason.

The moment you subscribe, DAPPS replays any **already-pending messages** for `hello` that arrived before the app came online. The "queue persists across app restarts" guarantee shows up here as ordinary MQTT delivery on subscribe.

## Step 3: read the user properties

Each delivery carries three MQTT 5 user properties:

| Property        | Meaning                                                                                                |
|-----------------|--------------------------------------------------------------------------------------------------------|
| `dapps-id`      | The 7-char message id. Use this to detect duplicates.                                                  |
| `dapps-source`  | The originating callsign. Use it as the destination if you want to reply.                              |
| `dapps-ttl`     | (Optional) Residual lifetime in seconds. Present only if the sender set a TTL.                         |

Paho exposes user properties as a list of `(key, value)` tuples on `msg.properties.UserProperty`. The script has a small helper:

```python
def user_property(properties, name):
    for key, value in (getattr(properties, "UserProperty", None) or []):
        if key == name:
            return value
    return None
```

In the `on_message` callback:

```python
msg_id = user_property(msg.properties, "dapps-id")
sender = user_property(msg.properties, "dapps-source")
payload = msg.payload.decode("utf-8", errors="replace")
```

## Step 4: be idempotent

A message you've already processed will sometimes arrive a second time - DAPPS or your app crashed before the ack was recorded. **Don't trust that every delivery is a fresh one.** Check the id against a "seen" store before doing real work:

```python
seen: set[str] = set()  # in real code: SQLite / Redis / a file
...
if msg_id in seen:
    # Re-ack to drain the queue but don't send a duplicate reply.
    client.publish("dapps/ack/hello", msg_id.encode("utf-8"), qos=1)
    return
```

For this tutorial an in-memory set is fine - restart the script and it'll happily reply to old messages again, but that's exactly the bug the persistent store fixes when you're ready for a real app.

## Step 5: reply

Replying is symmetric to receiving - publish to `dapps/out/<app>/<destination-callsign>`. The destination is whoever sent us the message; that's the `dapps-source` user property we just read.

```python
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes

reply = f"hello, {payload}!".encode("utf-8")

out_props = Properties(PacketTypes.PUBLISH)
out_props.UserProperty = [("dapps-ttl", "300")]

client.publish(
    f"dapps/out/hello/{sender}",
    reply,
    qos=1,
    properties=out_props,
)
```

The optional `dapps-ttl` user property gives the message a 5-minute residual lifetime. If the receiver doesn't pull this from their queue within 5 minutes, DAPPS drops it - no point greeting somebody an hour late.

## Step 6: acknowledge

The final step is publishing the message id back to `dapps/ack/<app>`. Until you do this, DAPPS thinks the message is still pending and will redeliver it on the next subscribe.

```python
seen.add(msg_id)
client.publish("dapps/ack/hello", msg_id.encode("utf-8"), qos=1)
```

That's the whole loop: receive → check-seen → reply → mark-seen → ack.

## Try it out

Run the script:

```bash
python hello.py
```

In another terminal, send a message to `hello` at your own callsign (replace `<your-callsign>` with whatever your DAPPS is configured with):

```bash
mosquitto_pub -h 127.0.0.1 -V mqttv5 \
    -t 'dapps/out/hello/<your-callsign>' -m 'world'
```

You should see, in the script's terminal:

```
<- abc1234 from <your-callsign>: 'world'
-> reply sent to <your-callsign>
```

The script published a reply, and because the destination was your own callsign, your DAPPS forwarded it straight back into `dapps/in/hello`. To watch it round-trip without re-triggering your handler, subscribe with `mosquitto_sub` in a separate terminal:

```bash
mosquitto_sub -h 127.0.0.1 -V mqttv5 -t 'dapps/in/hello' -F '%P\n%p'
```

## Same thing in `curl`

If you'd rather not pull in an MQTT library, the REST endpoints offer the same surface. Submit:

```bash
curl -X POST http://127.0.0.1:5000/AppApi/outbound \
    -H 'Content-Type: application/json' \
    -d '{
        "app": "hello",
        "destCallsign": "G7XYZ",
        "payload": "d29ybGQ=",
        "ttl": 300
    }'
```

`payload` is base64-encoded bytes (`d29ybGQ=` is `world`). The response is `{"id": "abc1234"}`.

Poll the inbox:

```bash
curl http://127.0.0.1:5000/AppApi/inbound/hello
# [{"id":"abc1234","sourceCallsign":"G7XYZ","payload":"d29ybGQ=","ttl":287}]
```

`ttl` is the *residual* - the same 5-minute window you set, minus however long the message dwelt on this node. `null` means the sender didn't set one.

Ack:

```bash
curl -X POST http://127.0.0.1:5000/AppApi/inbound/hello/abc1234/ack
```

Both surfaces share the same queue. An app written against MQTT and a cron job hitting REST can run side by side and won't see each other's traffic - they're both authenticated as `hello` in the queue's view, but the queue handles concurrency for them.

## Where to go next

- **Read [the gallery](gallery.md)** for working examples of the other common app shapes - group chat, sensor publisher, two-way pager.
- **Read [the reference](reference.md)** for the full MQTT/REST surface, every user property's semantics, and the idempotency contract spelled out explicitly.
- **Persist `seen` to disk.** SQLite (`sqlite3` stdlib) is easiest. Insert the id under `INSERT OR IGNORE` and use the `changes()` count to decide whether to reply.
- **Handle malformed payloads.** `decode("utf-8", errors="replace")` keeps the demo from crashing on a binary blob, but a real app should validate input and ack-without-replying on garbage rather than ignoring it (otherwise garbage piles up in your queue).
- **Set sensible TTLs.** "How fresh does this still need to be?" is a useful design question. Match the value to what your app does.
- **Authenticate.** When the operator turns on `auth-required`, the MQTT CONNECT needs `username=hello` + a per-app token, and REST needs an `Authorization: Bearer ...` header. Tokens are minted from the dashboard's `/AppTokens` page.
