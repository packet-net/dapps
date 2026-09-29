#!/usr/bin/env python3
"""Replay a soak's air transcript through BPQ's AX.25 receive logic, to find
where BPQ handed the application a stale frame (issue #207).

BPQ's I-frame N(S) check (L2Code.c, SDIFRM, around lines 2585 to 2650 on
M0LTE/linbpq's patched branch) keeps each out-of-sequence I-frame in a slot
per N(S), RXFRAMES. When the frame it needs next is missing, it takes
whatever is in that slot, without checking that it belongs to the current
turn of the modulo-8 sequence numbers. A slot is emptied only when a frame
with that N(S) arrives in sequence, or by an in-sequence I-frame with P.

This follows that logic for each direction, using what the receiving node's
monitor heard (the [A->B] lines are what B heard from A). A frame saved when
it was 4 to 7 behind V(R) (MAXFRAME 4) was a copy of one already taken; if
it is later handed over as the missing frame, that's a stale frame accepted,
reported as STALE with the time. Compare those times with the daemons' logs
("does not decode", "doesn't hash to its id", "Out of step").

It also prints each time the receiver's own RR/REJ N(R) disagrees with the
replayed V(R). A few are normal (an RR queued in the TNC behind a newer
one); many mean the replay has lost track, and a run with many is weak
evidence either way.

usage: rxsim.py scenario-reports/soak-air.txt
"""
import re
import sys

text = open(sys.argv[1], 'rb').read().decode('latin1')
parts = re.split(r'(?m)^(\[(?:A->B|B->A)\] [^\n]*)$', text)
frames = []
for i in range(1, len(parts), 2):
    m = re.match(r'\[(A->B|B->A)\] \d+:Fm \S+ To \S+ <([^>]*)>\[(\d\d:\d\d:\d\d)\]', parts[i])
    if not m:
        continue
    direction, ctl, t = m.groups()
    body = parts[i + 1].strip('\n') if i + 1 < len(parts) else ''
    toks = ctl.split()
    f = dict(dir=direction, kind=toks[0], t=t, body=body, pf='P' in toks[1:] or 'F' in toks[1:])
    for tk in toks:
        if re.fullmatch(r'S\d', tk) and f['kind'] == 'I':
            f['ns'] = int(tk[1])
        if re.fullmatch(r'R\d', tk):
            f['nr'] = int(tk[1])
    frames.append(f)


class Receiver:
    def __init__(self, name):
        self.name = name
        self.reset()

    def reset(self):
        self.vr = 0
        self.saved = {}  # N(S) -> (time heard, body, was behind V(R) when saved)


receivers = {'A->B': Receiver('B'), 'B->A': Receiver('A')}
reverse = {'A->B': 'B->A', 'B->A': 'A->B'}
disagreements = 0
stale = []

for f in frames:
    d, kind = f['dir'], f['kind']
    if kind in ('C', 'D', 'UA', 'DM'):
        # SABM (shown as C), DISC (D), UA, DM: the link starts afresh.
        for r in receivers.values():
            r.reset()
        continue
    if 'nr' in f and kind in ('RR', 'REJ', 'RNR'):
        r = receivers[reverse[d]]
        if r.vr != f['nr']:
            disagreements += 1
            print(f"  [{f['t']}] {r.name} says N(R)={f['nr']} ({kind}), replayed V(R)={r.vr}")
    if kind != 'I':
        continue
    r = receivers[d]
    ns = f['ns']
    if ((ns + 1) & 7) == r.vr and f['pf']:
        continue  # BPQ takes the previous frame with P as a poll (IPOLL)
    while ns != r.vr:
        if r.vr in r.saved:
            heard, body, behind = r.saved.pop(r.vr)
            tag = 'STALE' if behind else 'ok'
            print(f"  [{f['t']}] {r.name} takes saved S{r.vr} (heard {heard}) {tag}: {body[:70]!r}")
            if behind:
                stale.append((f['t'], r.name, r.vr, heard, body))
            r.vr = (r.vr + 1) & 7
            continue
        r.saved[ns] = (f['t'], f['body'], ((ns - r.vr) & 7) >= 4)
        break
    else:
        r.saved.pop(ns, None)
        r.vr = (r.vr + 1) & 7
        if f['pf']:
            r.saved.clear()

print(f"\nN(R) disagreements: {disagreements}")
print(f"stale frames taken: {len(stale)}")
for t, name, ns, heard, body in stale:
    print(f"\n[{t}] {name} took S{ns} saved at {heard}: {body[:120]!r}")
