#!/usr/bin/env python3
"""
sensor.py - periodic sensor publisher over DAPPS.

Every N seconds, reads a "sensor" (a fake CPU-load number for the
purposes of this example) and publishes a JSON reading to a list of
subscriber callsigns.

Usage:

    python sensor.py --interval 60 --subscribers G7XYZ M0LTE-1

Or as a one-shot from cron / systemd-timer:

    python sensor.py --once --subscribers G7XYZ

What this demonstrates:
  - A "publisher" app that doesn't subscribe to anything itself - pure
    submit-and-go. No on_message callback, no ack handling.
  - Long TTL (24h) - old sensor readings are still useful information
    ("temp was 19C four hours ago"); much better than dropping them.
  - Structured payload (JSON). DAPPS treats it as opaque bytes; the
    receiver decodes.

A companion subscriber would be a script using the hello.py pattern
that subscribes to dapps/in/sensor and pretty-prints the JSON.
"""

import argparse
import json
import random
import time

import paho.mqtt.client as mqtt
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes


HOST = "127.0.0.1"
PORT = 1883
APP = "sensor"


def read_sensor() -> dict:
    """Stand-in for a real sensor read.  Fakes a CPU-load-ish number."""
    return {
        "timestamp": int(time.time()),
        "cpu_load_pct": round(random.uniform(0.0, 100.0), 1),
        "temp_c": round(20.0 + random.uniform(-2.0, 2.0), 1),
    }


def publish_one(client: mqtt.Client, subscribers: list[str]) -> None:
    reading = read_sensor()
    payload = json.dumps(reading).encode("utf-8")

    # 24h TTL - sensor data is interesting for a long time, but a
    # week-old reading is just clutter.  Pick whatever matches the
    # cadence: roughly 1.5–2x the publish interval is a sensible floor.
    props = Properties(PacketTypes.PUBLISH)
    props.UserProperty = [("dapps-ttl", "86400")]

    for callsign in subscribers:
        client.publish(
            f"dapps/out/{APP}/{callsign}",
            payload, qos=1, properties=props,
        )
        print(f"-> {callsign}: {reading}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--subscribers", nargs="+", required=True,
                        help="callsigns to publish readings to")
    parser.add_argument("--interval", type=int, default=60,
                        help="seconds between publishes (default: 60)")
    parser.add_argument("--once", action="store_true",
                        help="publish a single reading and exit")
    args = parser.parse_args()

    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        protocol=mqtt.MQTTv5,
        client_id="sensor-app",
    )
    client.connect(HOST, PORT, keepalive=30)
    client.loop_start()

    try:
        if args.once:
            publish_one(client, args.subscribers)
            time.sleep(1)  # let paho flush before disconnect
        else:
            while True:
                publish_one(client, args.subscribers)
                time.sleep(args.interval)
    except KeyboardInterrupt:
        pass
    finally:
        client.loop_stop()
        client.disconnect()


if __name__ == "__main__":
    main()
