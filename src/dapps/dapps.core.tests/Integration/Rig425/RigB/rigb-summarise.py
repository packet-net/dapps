#!/usr/bin/env python3
"""Summarise Rig B runs (#425): median and range per arm and workload.

Usage: rigb-summarise.py [--per-arm] rigb-results.jsonl [more.jsonl ...]

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
    ("Last delivery PEER4, s", lambda r: r["LastDeliveryByPeer"].get("PEER4")),
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
    ("SREJs", lambda r: r.get("Srejs")),
    ("Node-to-node link resets (DISC)", lambda r: r.get("NodeLinkResets")),
    ("L2 connects on air (SABM)", lambda r: r["L2Connects"]),
    ("NET/ROM connects on air", lambda r: r["NetRomConnects"]),
    ("DAPPS sessions", lambda r: r["DappsSessions"]),
    ("Dials MB7NPW asked its node for", lambda r: r.get("Dials")),
    ("Dials the node connected", lambda r: r.get("DialsConfirmed")),
    ("Failed sends (dial or script)", lambda r: r.get("SendFailures")),
    ("DAPPS sessions the peers dialled", lambda r: r.get("NpwInboundSessions")),
    ("Sessions ended early for other mail (give-way)", lambda r: r.get("GiveWays")),
    ("Deferrals: dialling would reset an open session", lambda r: r.get("BusyDeferrals")),
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


def per_arm(by, failed):
    arms = sorted({a for _, a in by} | {a for _, a in failed})
    workloads = sorted({w for w, _ in by} | {w for w, _ in failed})
    for arm in arms:
        print(f"### Arm {arm}\n")
        print("| | " + " | ".join(workloads) + " |")
        print("|---|" + "---|" * len(workloads))
        for name, get in MEASURES:
            if get is None:
                row = [f"{len(by[(w, arm)])}" + (f" (+{failed[(w, arm)]} failed)" if failed[(w, arm)] else "") for w in workloads]
            else:
                row = [cell([get(r) for r in by[(w, arm)]]) for w in workloads]
            print(f"| {name} | " + " | ".join(row) + " |")
        print()


def label(r):
    """The arm, marked when only some peers had mail."""
    mp, rp = r.get("MailPeers"), r.get("RigPeers")
    return r["Arm"] + (f" ({mp} peer{'s' if mp != 1 else ''})" if mp and rp and mp < rp else "")


def main(paths):
    arm_tables = "--per-arm" in paths
    paths = [p for p in paths if p != "--per-arm"]
    runs = []
    for p in paths:
        with open(p) as f:
            runs.extend(json.loads(line) for line in f if line.strip())
    by = defaultdict(list)
    failed = defaultdict(int)
    for r in runs:
        if r.get("Failure"):
            failed[(r["Workload"], label(r))] += 1
            continue
        by[(r["Workload"], label(r))].append(r)
    if arm_tables:
        per_arm(by, failed)
        return
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
