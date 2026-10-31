"""Per-run stall analysis for Rig B reports (#425): is DAPPS stuck, or waiting on the node link?
Usage: stall.py report.md [...]"""
import json, re, sys
from datetime import datetime, timedelta

def sections(text):
    parts = re.split(r'^## (.+)$', text, flags=re.M)
    return {parts[i].strip(): parts[i+1] for i in range(1, len(parts), 2)}

def ts(line, day):
    m = re.match(r'(\d\d):(\d\d):(\d\d)\.(\d+)', line)
    if not m: return None
    t = day.replace(hour=int(m[1]), minute=int(m[2]), second=int(m[3]), microsecond=int(m[4][:6].ljust(6, '0')))
    return t

for path in sys.argv[1:]:
    text = open(path).read()
    js = json.loads(re.search(r'```\n(\{.*?\})\n```', text, re.S).group(1))
    m0 = re.match(r'(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?', js['StartedUtc'])
    t0 = datetime.fromisoformat(m0[1]).replace(microsecond=int((m0[2] or '0')[:6].ljust(6, '0')))
    sec = sections(text)
    log = sec.get('rigb-NPW log', '')
    ev = []
    for line in log.splitlines():
        t = ts(line, t0)
        if t is None: continue
        if t < t0 - timedelta(hours=1): t += timedelta(days=1)
        rel = (t - t0).total_seconds()
        if rel < -5: continue
        for key, pat in [('dial', 'AGW: requesting'), ('up', 'connect confirmed'), ('nodial', 'did not answer the connect'),
                         ('closed', r'Session with \S+ closed'), ('fail', 'Failed to forward'), ('defer-busy', 'dialling now would reset it'),
                         ('stuck', r'Nothing answered by|Nothing from|isn.t reaching it'), ('giveway', 'before its'), ('script', 'connect-script failed'),
                         ('ok', 'Remote end accepted')]:
            if re.search(pat, line):
                ev.append((rel, key, line[13:200]))
    # Outbound activity windows: a dial until its session closes or the dial fails.
    active, start, idle_gaps, busy_while_idle = False, None, [], 0
    last_end = 0.0
    last_ok = max([e[0] for e in ev if e[1] == 'ok'], default=0)
    end_of_interest = js.get('LastDelivery') or js.get('QuietAt') or 0
    if js['Lost']: end_of_interest = js['QuietAt']
    for rel, key, line in ev:
        if key == 'dial':
            if not active and rel <= end_of_interest: idle_gaps.append((round(last_end), round(rel - last_end)))
            active = True
        elif key in ('closed', 'nodial', 'script') or (key == 'fail' and 'broke off' not in line and 'no answer' not in line):
            if active: last_end = rel
            active = False
        elif key == 'defer-busy' and not active:
            busy_while_idle += 1
    counts = {k: sum(1 for e in ev if e[1] == k) for k in ('dial', 'up', 'nodial', 'script', 'stuck', 'fail', 'giveway', 'defer-busy')}
    bpqlog = (sec.get('LinBPQ MB7NPW debug log', '') + sec.get('LinBPQ CALNE debug log', ''))
    linklost = len(re.findall(r'L2 Link to Neighbour .* lost|Too many repeats', bpqlog))
    worst = max(idle_gaps, key=lambda g: g[1], default=(0, 0))
    print(f"{js['Arm']:>3} {js.get('Bpq') or 'stock':<22} rep {js['Rep']:>2}: last {js['LastDelivery'] or 0:6.0f} s, {js['Delivered']}/{js['Messages']}, "
          f"srej {js.get('Srejs')}, resets {js.get('NodeLinkResets')}, bpq link-lost {linklost}; dials {counts['dial']}, "
          f"dial timeouts {counts['nodial']}, script fails {counts['script']}, stuck sessions {counts['stuck']}, give-ways {counts['giveway']}, "
          f"busy deferrals {counts['defer-busy']} ({busy_while_idle} with no session of ours running), longest gap with no dial or session before the end {worst[1]} s at {worst[0]} s")
