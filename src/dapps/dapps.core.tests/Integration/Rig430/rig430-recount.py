#!/usr/bin/env python3
"""Recount link resets from rig 430 run reports.

    rig430-recount.py out.jsonl report.md [report.md ...]

Runs before the counter fix counted a SABM after a crossed DISC (both ends
sending DISC, then both UA) as a reset. This reads the frame transcripts in
each report, counts again (a UA brings a link up only after a SABM), adds
NodeLinkDiscs (DISCs between two node calls: a node-to-node link dropped)
and Stalls (sessions DAPPS ended after hearing nothing for 180 s), and writes the result lines with those counts replaced.
"""
import json
import re
import sys

FRAME = re.compile(r"^\s*-?[\d.]+\s+(?:[AB]>[AB]\s+)?([A-Z0-9-]+)>([A-Z0-9-]+) <(\w+)")
SECTIONS = {"4 m channel: frames": "m4", "70 cm channel: frames": "c70", "NPR frames": "npr"}


def node_call(c):
    return "-" not in c or c == "G9NDH-2"


def count(frames):
    up, asked, resets, discs = set(), set(), 0, 0
    for a, b, t in frames:
        pair = tuple(sorted((a, b)))
        if t in ("SABM", "SABME"):
            if pair in up:
                resets += 1
                up.discard(pair)
            asked.add(pair)
        elif t == "UA":
            if pair in asked:
                asked.discard(pair)
                up.add(pair)
        elif t == "DISC":
            up.discard(pair)
            asked.discard(pair)
            if node_call(a) and node_call(b):
                discs += 1
        elif t in ("DM", "FRMR"):
            if pair in up:
                resets += 1
                up.discard(pair)
    return resets, discs


def main(out, paths):
    lines = []
    for p in paths:
        text = open(p).read()
        result = json.loads(text.split("```")[1])
        frames = {k: [] for k in SECTIONS.values()}
        section = None
        for line in text.splitlines():
            if line.startswith("## "):
                section = next((v for k, v in SECTIONS.items() if k in line), None)
                continue
            if section and (m := FRAME.match(line)):
                frames[section].append(m.groups())
        for link, fs in frames.items():
            if link in result["Links"]:
                resets, discs = count(fs)
                result["Links"][link]["Counts"]["Resets"] = resets
                result["Links"][link]["Counts"]["NodeLinkDiscs"] = discs
        # Sessions that went silent until DAPPS gave up on them (180 s), any daemon.
        result["Stalls"] = len(re.findall(r"and nothing else from it, for \d+s; ending the session", text))
        lines.append(json.dumps(result))
    with open(out, "w") as f:
        f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
