#!/usr/bin/env python3
"""
hello.py - minimal DAPPS app.

Listens on dapps/in/hello for inbound messages.  For each message, replies
"hello, <name>!" back to the sender, where <name> is the message payload.
Acks every message it processes (idempotently - a redelivery is recognised
by id and acked without sending a duplicate reply).

Designed as the smallest useful demonstration of the DAPPS app interface,
not as production code.  Install paho-mqtt 2.x first:

    pip install paho-mqtt

Run a DAPPS instance locally (default port 1883), then:

    python hello.py

In another terminal, send "world" to yourself with mosquitto_pub:

    mosquitto_pub -h 127.0.0.1 -V mqttv5 \\
        -t 'dapps/out/hello/<your-callsign>' -m 'world'

Replace <your-callsign> with whatever Callsign your DAPPS is configured
with - the reply will loop back to you.
"""

import paho.mqtt.client as mqtt
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes


HOST = "127.0.0.1"
PORT = 1883
APP = "hello"
INBOX = f"dapps/in/{APP}"
ACK = f"dapps/ack/{APP}"

# Set of message ids we've already replied to.  In a real app this would
# live in SQLite or a similar persistent store so it survives restarts.
seen: set[str] = set()


def user_property(properties: Properties | None, name: str) -> str | None:
    """MQTT 5 user properties arrive as a list of (key, value) tuples."""
    for key, value in (getattr(properties, "UserProperty", None) or []):
        if key == name:
            return value
    return None


def on_connect(client, userdata, flags, reason_code, properties):
    print(f"connected: {reason_code}")
    client.subscribe(INBOX, qos=1)


def on_message(client, userdata, msg):
    props = msg.properties
    msg_id = user_property(props, "dapps-id")
    sender = user_property(props, "dapps-source")
    payload = msg.payload.decode("utf-8", errors="replace")

    if msg_id is None or sender is None:
        # Shouldn't happen against a real DAPPS, but defend against noise
        # on the broker (a misconfigured publisher, say).
        print(f"!! ignoring message with no dapps-id / dapps-source")
        return

    if msg_id in seen:
        # Redelivery - ack again to be safe (acks are idempotent on
        # the DAPPS side too) but don't send a second reply.
        print(f"<- redelivery {msg_id} from {sender}, re-acking")
        client.publish(ACK, msg_id.encode("utf-8"), qos=1)
        return

    print(f"<- {msg_id} from {sender}: {payload!r}")
    reply = f"hello, {payload}!".encode("utf-8")

    # Send the reply.  Optional dapps-ttl user property gives the
    # message a 5-minute residual lifetime - anything older than that
    # at delivery time means the receiver was offline long enough that
    # the greeting is no longer interesting.
    out_props = Properties(PacketTypes.PUBLISH)
    out_props.UserProperty = [("dapps-ttl", "300")]
    client.publish(
        f"dapps/out/{APP}/{sender}",
        reply,
        qos=1,
        properties=out_props,
    )
    print(f"-> reply sent to {sender}")

    seen.add(msg_id)
    client.publish(ACK, msg_id.encode("utf-8"), qos=1)


def main():
    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        protocol=mqtt.MQTTv5,
        client_id="hello-app",
    )
    client.on_connect = on_connect
    client.on_message = on_message
    client.connect(HOST, PORT, keepalive=30)
    client.loop_forever()


if __name__ == "__main__":
    main()
