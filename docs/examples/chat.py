#!/usr/bin/env python3
"""
chat.py - small group-chat app over DAPPS.

Subscribes to dapps/in/chat for incoming lines; broadcasts whatever you
type at the prompt to a fixed list of recipient callsigns. Each
recipient runs the same script, with its own copy of the recipient list
(no central registry - DAPPS doesn't have one).

Usage:

    python chat.py G7XYZ M0LTE-1 GB7AAA-4

The CLI args are the *other* people you want to talk to. This script's
own callsign is whichever Callsign the local DAPPS is configured with -
it's stamped onto outbound messages by DAPPS, so you don't need to pass
it here.

What this demonstrates:
  - One-to-many fan-out (one publish per recipient - DAPPS doesn't have
    multicast at the app layer; you address each peer explicitly).
  - Reading dapps-source to display "who said what."
  - Idempotent redelivery: two arrivals of the same id print only once.

Like hello.py, this is a teaching example, not production code. The
"seen" set is in memory; restart the script and you'll re-print the
backlog. For a real app, persist it.
"""

import sys

import paho.mqtt.client as mqtt
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes


HOST = "127.0.0.1"
PORT = 1883
APP = "chat"
INBOX = f"dapps/in/{APP}"
ACK = f"dapps/ack/{APP}"

seen: set[str] = set()


def user_property(properties, name):
    for key, value in (getattr(properties, "UserProperty", None) or []):
        if key == name:
            return value
    return None


def on_connect(client, userdata, flags, reason_code, properties):
    print(f"[chat] connected: {reason_code}")
    client.subscribe(INBOX, qos=1)


def on_message(client, userdata, msg):
    msg_id = user_property(msg.properties, "dapps-id")
    sender = user_property(msg.properties, "dapps-source")
    if msg_id is None or sender is None:
        return  # noise on the broker - ignore

    if msg_id in seen:
        # Redelivery - just re-ack and skip.
        client.publish(ACK, msg_id.encode("utf-8"), qos=1)
        return
    seen.add(msg_id)

    text = msg.payload.decode("utf-8", errors="replace")
    print(f"\n[{sender}] {text}\n> ", end="", flush=True)

    client.publish(ACK, msg_id.encode("utf-8"), qos=1)


def reader_loop(client, recipients):
    """Read stdin line-by-line, broadcast each line to recipients."""
    print(f"[chat] sending to: {', '.join(recipients)}")
    print("type a line and hit enter; ctrl-c to quit")
    print("> ", end="", flush=True)
    try:
        for line in sys.stdin:
            line = line.rstrip("\n")
            if not line:
                print("> ", end="", flush=True)
                continue
            payload = line.encode("utf-8")
            # 1-hour TTL - chat is more useful than a 5-minute reply
            # window, less useful than a day-old message.
            props = Properties(PacketTypes.PUBLISH)
            props.UserProperty = [("dapps-ttl", "3600")]
            for callsign in recipients:
                client.publish(
                    f"dapps/out/{APP}/{callsign}",
                    payload, qos=1, properties=props,
                )
            print("> ", end="", flush=True)
    except KeyboardInterrupt:
        print()


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    recipients = sys.argv[1:]

    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        protocol=mqtt.MQTTv5,
        client_id="chat-app",
    )
    client.on_connect = on_connect
    client.on_message = on_message
    client.connect(HOST, PORT, keepalive=30)
    client.loop_start()

    try:
        reader_loop(client, recipients)
    finally:
        client.loop_stop()
        client.disconnect()


if __name__ == "__main__":
    main()
