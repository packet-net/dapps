#!/usr/bin/env python3
"""
pager.py - two-way pager / messenger over DAPPS.

Two modes:

  Receive (default):
      python pager.py

      Sits subscribed to dapps/in/pager, prints incoming messages with
      their source callsign, acks each one.

  Send (one-shot):
      python pager.py send G7XYZ "the eagle has landed"

      Submits a single message and exits.  No subscription, no ack
      handling - useful from cron, scripts, or your shell.

What this demonstrates:
  - Two distinct lifetimes from the same app: a long-running listener
    and a one-shot submitter, sharing the same app name (= the same
    queue slot on this node).  When the listener is up, send-mode
    submissions go straight through DAPPS' submit path and don't
    interfere with the listener's connection.
  - Splitting "what to do with a message" from "how to ack it":
    the listener is the sole acker; send-mode doesn't ack anything.
  - A short TTL (5 min): a pager message older than a few minutes is
    almost always noise.
"""

import sys
import time

import paho.mqtt.client as mqtt
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes


HOST = "127.0.0.1"
PORT = 1883
APP = "pager"
INBOX = f"dapps/in/{APP}"
ACK = f"dapps/ack/{APP}"

seen: set[str] = set()


def user_property(properties, name):
    for key, value in (getattr(properties, "UserProperty", None) or []):
        if key == name:
            return value
    return None


# ── Receive mode ──────────────────────────────────────────────────

def on_connect_recv(client, userdata, flags, reason_code, properties):
    print(f"[pager] listening on {INBOX}")
    client.subscribe(INBOX, qos=1)


def on_message_recv(client, userdata, msg):
    msg_id = user_property(msg.properties, "dapps-id")
    sender = user_property(msg.properties, "dapps-source")
    ttl = user_property(msg.properties, "dapps-ttl")

    if msg_id is None or sender is None:
        return

    if msg_id in seen:
        client.publish(ACK, msg_id.encode("utf-8"), qos=1)
        return
    seen.add(msg_id)

    text = msg.payload.decode("utf-8", errors="replace")
    age_note = f" (residual ttl {ttl}s)" if ttl else ""
    print(f"[{sender}] {text}{age_note}")

    client.publish(ACK, msg_id.encode("utf-8"), qos=1)


def run_receiver():
    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        protocol=mqtt.MQTTv5,
        client_id="pager-recv",
    )
    client.on_connect = on_connect_recv
    client.on_message = on_message_recv
    client.connect(HOST, PORT, keepalive=30)
    client.loop_forever()


# ── Send mode ─────────────────────────────────────────────────────

def run_sender(callsign: str, message: str):
    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        protocol=mqtt.MQTTv5,
        client_id=f"pager-send-{int(time.time())}",
    )
    client.connect(HOST, PORT, keepalive=10)
    client.loop_start()

    props = Properties(PacketTypes.PUBLISH)
    props.UserProperty = [("dapps-ttl", "300")]   # 5 min
    info = client.publish(
        f"dapps/out/{APP}/{callsign}",
        message.encode("utf-8"),
        qos=1,
        properties=props,
    )
    info.wait_for_publish(timeout=5)
    print(f"-> sent to {callsign}: {message!r}")

    client.loop_stop()
    client.disconnect()


def main():
    if len(sys.argv) >= 4 and sys.argv[1] == "send":
        run_sender(sys.argv[2], " ".join(sys.argv[3:]))
        return
    if len(sys.argv) == 1:
        run_receiver()
        return
    print(__doc__)
    sys.exit(1)


if __name__ == "__main__":
    main()
