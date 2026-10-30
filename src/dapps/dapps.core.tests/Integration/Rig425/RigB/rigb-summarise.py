#!/usr/bin/env python3
"""Summarise Rig B runs (#425): median and range per arm and workload.

Usage: rigb-summarise.py rigb-results.jsonl [more.jsonl ...]

Reads the JSON lines RigBTests writes and prints one markdown table per
workload, a row per measure and a column per arm, each cell
"median (min to max)". Runs with a failure are counted but left out.
"""
import json
import statistics
import sys
from collections import defaultdict

MEASURES = [
    ("Runs", None),
    ("Last delivery, s", lambda r: r["LastDelivery"]),
    ("Last delivery PEER1, s", lambda r: r["LastDeliveryByPeer"].get("PEER1")),
    ("Last delivery PEER2, s", lambda r: r["LastDeliveryByPeer"].get("PEER2")),
    ("Last delivery PEER3, s", lambda r: r["LastDeliveryByPeer"].get("PEER3")),
    ("After the last message was handed in, s", lambda r: None if r["LastDelivery"] is None else r["LastDelivery"] - r["LastSubmitted"]),
    ("Message latency median, s", lambda r: r["LatencyMedian"]),
    ("Message latency worst, s", lambda r: r["LatencyMax"]),
    ("Airtime to last delivery, s", lambda r: r["AirtimeToLast"]),
    ("Channel busy to last delivery, %", lambda r: r["BusyPercent"]),
    ("Airtime until quiet (holds included), s", lambda r: r["AirtimeTotal"]),
    ("Collisions (overlapping transmissions)", lambda r: r["Collisions"]),
    ("Frames lost on air", lambda r: max(0, r["FramesSentA"] - r["FramesHeardFromA"]) + max(0, r["FramesSentB"] - r["FramesHeardFromB"])),
    ("I-frames heard twice", lambda r: r["Retransmissions"]),
    ("REJs", lambda r: r["Rejs"]),
    ("Supervisory polls (RR/RNR/REJ with P)", lambda r: r["Polls"]),
    ("L2 timeouts (BPQ, both ends)", lambda r: r["L2TimeoutsA"] + r["L2TimeoutsB"]),
    ("L4 frames resent (BPQ, both ends)", lambda r: r["L4ResentA"] + r["L4ResentB"]),
    ("Retry-outs", lambda r: r["RetryOuts"]),
    ("L2 connects on air (SABM)", lambda r: r["L2Connects"]),
    ("NET/ROM connects on air", lambda r: r["NetRomConnects"]),
    ("DAPPS sessions", lambda r: r["DappsSessions"]),
    ("Messages lost", lambda r: r["Lost"]),
    ("Duplicates", lambda r: r["Duplicates"]),
]


def fmt(v):
    return f"{v:.0f}" if abs(v) >= 10 or v == int(v) else f"{v:.1f}"


def cell(values):
    values = [v for v in values if v is not None]
    if not values:
        return "-"
    med = statistics.median(values)
    lo, hi = min(values), max(values)
    return fmt(med) if lo == hi else f"{fmt(med)} ({fmt(lo)} to {fmt(hi)})"


def main(paths):
    runs = []
    for p in paths:
        with open(p) as f:
            runs.extend(json.loads(line) for line in f if line.strip())
    by = defaultdict(list)
    failed = defaultdict(int)
    for r in runs:
        if r.get("Failure"):
            failed[(r["Workload"], r["Arm"])] += 1
            continue
        by[(r["Workload"], r["Arm"])].append(r)
    for workload in sorted({w for w, _ in by} | {w for w, _ in failed}):
        arms = sorted({a for w, a in by if w == workload} | {a for w, a in failed if w == workload})
        print(f"### {workload}\n")
        print("| | " + " | ".join(f"Arm {a}" for a in arms) + " |")
        print("|---|" + "---|" * len(arms))
        for name, get in MEASURES:
            if get is None:
                row = [f"{len(by[(workload, a)])}" + (f" (+{failed[(workload, a)]} failed)" if failed[(workload, a)] else "") for a in arms]
            else:
                row = [cell([get(r) for r in by[(workload, a)]]) for a in arms]
            print(f"| {name} | " + " | ".join(row) + " |")
        print()


if __name__ == "__main__":
    main(sys.argv[1:])
