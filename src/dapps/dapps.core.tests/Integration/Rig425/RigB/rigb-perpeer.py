#!/usr/bin/env python3
"""Per-peer message waits from Rig B run reports (#425).

Usage: rigb-perpeer.py report.md [...]

Reads each report's JSON block and its Deliveries section, and prints one
markdown table per workload: a row per arm and peer, with the median over
runs of that peer's median wait, its worst wait and its last delivery
(each "median (min to max)" across runs). The peer is the alias in the
message tag (rigb-<seed>-<alias>-<n>).
"""
import json
import re
import statistics
import sys
from collections import defaultdict


def fmt(v):
    return f"{v:.0f}" if abs(v) >= 10 or v == int(v) else f"{v:.1f}"


def cell(values):
    values = [v for v in values if v is not None]
    if not values:
        return "-"
    med, lo, hi = statistics.median(values), min(values), max(values)
    return fmt(med) if lo == hi else f"{fmt(med)} ({fmt(lo)} to {fmt(hi)})"


def main(paths):
    by = defaultdict(lambda: defaultdict(list))  # (workload, arm) -> alias -> [(median, worst, last)]
    for p in paths:
        text = open(p).read()
        js = json.loads(re.search(r'```\n(\{.*?\})\n```', text, re.S).group(1))
        if js.get("Failure"):
            continue
        sec = re.search(r'^## Deliveries[^\n]*\n```\n(.*?)```', text, re.S | re.M)
        waits = defaultdict(list)
        for line in (sec.group(1) if sec else "").splitlines():
            m = re.match(r'\s*([\d.]+)\s+rigb-[0-9a-f]+-(\w+)-\d+\s+\(submitted ([\d.]+)\)', line)
            if m:
                waits[m[2]].append((float(m[1]) - float(m[3]), float(m[1])))
        for alias, w in waits.items():
            by[(js["Workload"], js["Arm"])][alias].append(
                (statistics.median(x for x, _ in w), max(x for x, _ in w), max(t for _, t in w)))
    for workload in sorted({w for w, _ in by}):
        print(f"### {workload}\n")
        print("| Arm | Peer | Runs | Median wait, s | Worst wait, s | Last delivery, s |")
        print("|---|---|---|---|---|---|")
        for arm in sorted(a for w, a in by if w == workload):
            for alias, rows in sorted(by[(workload, arm)].items()):
                print(f"| {arm} | {alias} | {len(rows)} | {cell([r[0] for r in rows])} | {cell([r[1] for r in rows])} | {cell([r[2] for r in rows])} |")
        print()


if __name__ == "__main__":
    main(sys.argv[1:])
