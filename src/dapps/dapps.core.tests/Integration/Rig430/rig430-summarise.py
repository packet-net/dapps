#!/usr/bin/env python3
"""Tables for packet-net/dapps#430 from rig430-results.jsonl lines.

    rig430-summarise.py results.jsonl [more.jsonl ...]

One table per workload, one column per arm and LinBPQ build. Latencies are
pooled over every message of every run (median, 90th percentile, worst);
everything else is the median of the runs, with the range in brackets.
"""
import json
import math
import statistics
import sys
from collections import defaultdict


def pct(values, p):
    if not values:
        return None
    s = sorted(values)
    k = max(0, min(len(s) - 1, math.ceil(p * len(s)) - 1))
    return s[k]


def fmt(v, digits=0):
    if v is None:
        return "-"
    if digits == 0:
        return f"{v:.0f}"
    return f"{v:.{digits}f}"


def spread(values, digits=0):
    values = [v for v in values if v is not None]
    if not values:
        return "-"
    med = statistics.median(values)
    lo, hi = min(values), max(values)
    if lo == hi:
        return fmt(med, digits)
    return f"{fmt(med, digits)} ({fmt(lo, digits)} to {fmt(hi, digits)})"


def link(r, name, field):
    l = r.get("Links", {}).get(name)
    if l is None:
        return None
    if field in l:
        return l[field]
    return l.get("Counts", {}).get(field)


def total(r, field):
    return sum((l.get("Counts", {}).get(field) or 0) for l in r.get("Links", {}).values())


def main(paths):
    runs = []
    for p in paths:
        with open(p) as f:
            runs += [json.loads(line) for line in f if line.strip()]
    groups = defaultdict(list)
    for r in runs:
        groups[(r["Workload"], r["Arm"], r.get("Bpq") or "stock")].append(r)
    for workload in sorted({k[0] for k in groups}, reverse=True):
        cols = sorted([k for k in groups if k[0] == workload], key=lambda k: (k[2] != "stock", k[2], "ALS".find(k[1]) if k[1] in "ALS" else 9, k[1]))
        print(f"\n### {workload}\n")
        print("| Measure | " + " | ".join(f"{a} ({b})" for _, a, b in cols) + " |")
        print("|---" * (len(cols) + 1) + "|")

        def row(label, fn):
            print(f"| {label} | " + " | ".join(fn(groups[c]) for c in cols) + " |")

        def pooled(field, p):
            return lambda rs: fmt(pct([x for r in rs for x in r.get(field, [])], p), 1)

        row("Runs", lambda rs: str(len(rs)))
        row("Messages delivered", lambda rs: f"{sum(r['Delivered'] for r in rs)}/{sum(r['Messages'] for r in rs)}")
        row("Latency median, s", pooled("Latencies", 0.5))
        row("Latency p90, s", pooled("Latencies", 0.9))
        row("Latency worst, s", pooled("Latencies", 1.0))
        if any(r.get("Replies") for r in sum((groups[c] for c in cols), [])):
            row("Replies delivered", lambda rs: f"{sum(r['RepliesDelivered'] for r in rs)}/{sum(r['Replies'] for r in rs)}")
            row("Reply latency median, s", pooled("ReplyLatencies", 0.5))
            row("Reply latency p90, s", pooled("ReplyLatencies", 0.9))
        for peer in ["GB7AGM", "GB7LOX", "EI7KX"]:
            row(f"Latency median {peer}, s", lambda rs, peer=peer: spread([r["LatencyMedianByPeer"].get(peer) for r in rs], 1))
        row("Last delivery, s", lambda rs: spread([r["LastDelivery"] for r in rs]))
        row("Lost; duplicates", lambda rs: f"{sum(r['Lost'] for r in rs)}; {sum(r['Duplicates'] for r in rs)}")
        row("MB7NPW dials", lambda rs: spread([r["Dials"] for r in rs]))
        row("Sessions at the peers", lambda rs: spread([r["PeerInboundSessions"] for r in rs]))
        row("NET/ROM connects (all links)", lambda rs: spread([total(r, "NetRomConnects") for r in rs]))
        row("L2 connects (SABM, all links)", lambda rs: spread([total(r, "Sabm") for r in rs]))
        row("4 m airtime, s", lambda rs: spread([link(r, "m4", "Airtime") for r in rs]))
        row("70 cm airtime, s", lambda rs: spread([link(r, "c70", "Airtime") for r in rs]))
        row("70 cm busy, %", lambda rs: spread([link(r, "c70", "BusyPercent") for r in rs]))
        row("70 cm collisions", lambda rs: spread([link(r, "c70", "Collisions") for r in rs]))
        row("NPR frames", lambda rs: spread([link(r, "npr", "Frames") for r in rs]))
        row("I-frames repeated (all links)", lambda rs: spread([total(r, "IFramesRepeated") for r in rs]))
        row("REJ + SREJ", lambda rs: spread([total(r, "Rej") + total(r, "Srej") for r in rs]))
        row("Node-to-node link drops (DISC)", lambda rs: spread([total(r, "NodeLinkDiscs") for r in rs]))
        row("Other link resets", lambda rs: spread([total(r, "Resets") for r in rs]))
        row("Retry-outs", lambda rs: spread([total(r, "RetryOuts") for r in rs]))
        row("Sessions stalled 180 s", lambda rs: spread([r.get("Stalls") for r in rs]))
        row("Give-ways", lambda rs: spread([r["GiveWays"] for r in rs]))
        row("Failed sends", lambda rs: spread([r["SendFailures"] for r in rs]))


if __name__ == "__main__":
    main(sys.argv[1:])
