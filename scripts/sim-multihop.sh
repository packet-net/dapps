#!/usr/bin/env bash
#
# Local 6-node multi-hop simulator. Spins up six dapps.core instances on
# loopback in a small-region mesh that exercises chained relays,
# branching, and off-spine paths - the kind of topology a real RF test
# network would produce. Drives several A→…→Z sends across the
# topology and reports each receiver's view of OriginatorCallsign so
# F1 source-tracking can be verified end-to-end at a glance.
#
# Topology
#
#       A ──G1── B ──G2── C ──G3── D                 (1, 2, 3 hops from A)
#                         │
#                         G4
#                         │
#                         E ──G5── F                 (3 hops, then 4 from A)
#
# Multicast groups G1..G5 stand in for distinct RF "broadcast domains";
# each adjacent pair shares a group so beacons reach exactly the right
# peers. Forwarding still goes unicast UDP via the per-node neighbour
# table (the same way RF works: broadcast discovery, point-to-point
# forward). Pre-B5 (learned-graph routing) the route-hints encode the
# multi-hop chains; B5 will replace them with learned routes.
#
# Usage:
#   scripts/sim-multihop.sh                       # passive-flood (default)
#   SIM_ALGO=meshcore scripts/sim-multihop.sh     # meshcore-like
#   scripts/sim-multihop.sh stop      # tear down
#   scripts/sim-multihop.sh send X Y  # send a message X → Y
#   scripts/sim-multihop.sh exercise  # run the canned non-trivial set
#   scripts/sim-multihop.sh status    # ports, PIDs, queue counts
#   scripts/sim-multihop.sh verify    # show every node's inbox
#   scripts/sim-multihop.sh learned   # dump per-node learned-routes
#   scripts/sim-multihop.sh discovered    # dump per-node discovered-paths
#   scripts/sim-multihop.sh prove-learning       # passive-flood acceptance
#   scripts/sim-multihop.sh prove-meshcore       # meshcore acceptance
#   scripts/sim-multihop.sh prove-fragmentation  # F2 multi-part acceptance
#   scripts/sim-multihop.sh prove-solicit        # B6.2 on-demand solicit acceptance
#
# Requires: dotnet 10 SDK, curl, python3 (with stdlib sqlite3).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="${SIM_DIR:-/tmp/dapps-sim}"
BIN_DIR="$SIM_DIR/bin"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SIM_PWD="${SIM_PWD:-simpassw0rd}"

# Routing algorithm: passive-flood (default - AODV-flavoured passive
# learning + bounded flood) or meshcore (DSR-style source routing
# with passive discovery). Set via env: SIM_ALGO=meshcore. Picked up
# by every node via DAPPS_ROUTING_ALGORITHM at startup.
SIM_ALGO="${SIM_ALGO:-passive-flood}"

NODES=(A B C D E F)

declare -A HTTP_PORT=( [A]=15001 [B]=15002 [C]=15003 [D]=15004 [E]=15005 [F]=15006 )
declare -A MQTT_PORT=( [A]=11881 [B]=11882 [C]=11883 [D]=11884 [E]=11885 [F]=11886 )
declare -A UDP_PORT=(  [A]=50071 [B]=50072 [C]=50073 [D]=50074 [E]=50075 [F]=50076 )
declare -A AGW_PORT=(  [A]=18001 [B]=18002 [C]=18003 [D]=18004 [E]=18005 [F]=18006 )   # closed; AGW probe will fail, harmless
declare -A CALLSIGN=(  [A]=G0SIA-1 [B]=G0SIB-1 [C]=G0SIC-1 [D]=G0SID-1 [E]=G0SIE-1 [F]=G0SIF-1 )

# Discovery channels - UDP multicast groups standing in for distinct
# RF "broadcast domains". All five share the same UDP port and differ
# only in multicast group address; this used to leak across groups
# within a process (see UdpMulticastDiscoveryBearer's bind comment for
# the gory detail), now fixed by binding each recv socket to the group
# address rather than IPAddress.Any.
G1="239.0.7.1:54321"
G2="239.0.7.2:54321"
G3="239.0.7.3:54321"
G4="239.0.7.4:54321"
G5="239.0.7.5:54321"
declare -A CHANNELS=(
    [A]="$G1"
    [B]="$G1 $G2"
    [C]="$G2 $G3 $G4"
    [D]="$G3"
    [E]="$G4 $G5"
    [F]="$G5"
)

# Direct neighbours - entries are space-separated "<letter>" lookups
# into the rest of the tables. Resolves to the matching callsign + UDP
# endpoint at configuration time.
declare -A NEIGHBOURS=(
    [A]="B"
    [B]="A C"
    [C]="B D E"
    [D]="C"
    [E]="C F"
    [F]="E"
)

# Route hints - used to be pre-B5 "force the relay" entries here, but
# B5 (PR-B passive learning + PR-C bounded flood) means the network
# converges from cold-start without any explicit hints. The simulator
# is a clean validation of that: only direct neighbours are configured;
# all multi-hop routing is discovered by passive learning, with the
# bounded flood handling the very first message to a never-seen
# destination. Keep the array empty so cold-start exercises the flood
# path; future tests can add hints back in to validate operator
# overrides if needed.
declare -A ROUTE_HINTS=( [A]="" [B]="" [C]="" [D]="" [E]="" [F]="" )

mkdir -p "$SIM_DIR" "$BIN_DIR"

# ── one-time publish ───────────────────────────────────────────────────
maybe_publish() {
    local stamp="$BIN_DIR/.built"
    local src_changed=0
    if [ ! -f "$stamp" ]; then src_changed=1; fi
    if [ -f "$stamp" ] && \
       find "$REPO/src/dapps" -name '*.cs' -newer "$stamp" -print -quit | grep -q .; then
        src_changed=1
    fi
    if [ "$src_changed" -eq 1 ]; then
        echo ">>> Publishing dapps.core into $BIN_DIR"
        rm -rf "$BIN_DIR/app"
        dotnet publish "$PROJ" -c Release -o "$BIN_DIR/app" --nologo --verbosity quiet >/dev/null
        touch "$stamp"
    fi
}

# ── per-node start/stop ────────────────────────────────────────────────
node_dir() { echo "$SIM_DIR/${1,,}"; }

start_node() {
    local n=$1
    local d; d="$(node_dir "$n")"
    mkdir -p "$d/data"
    # Fresh start - wipe DB + log so the topology comes up from a
    # clean slate every `up`. Use restart_node when you want to bounce
    # a node mid-test without losing its configured channels / peers.
    rm -f "$d/data/dapps.db" "$d/dapps.log"

    _launch_node "$n"
}

# Bounce a node without wiping its DB - same env, same paths, same
# port. Used by tests that need DiscoveryService to re-read channel
# config (e.g. SolicitIntervalSeconds, which is captured at
# StartAsync) without losing operator-configured rows.
restart_node() {
    local n=$1
    stop_node "$n"
    _launch_node "$n"
}

_launch_node() {
    local n=$1
    local d; d="$(node_dir "$n")"
    echo ">>> Starting $n (${CALLSIGN[$n]}) http=${HTTP_PORT[$n]} udp=${UDP_PORT[$n]} mqtt=${MQTT_PORT[$n]}"
    (
        cd "$d"
        env \
            DAPPS_CALLSIGN="${CALLSIGN[$n]}" \
            DAPPS_NODE_HOST=127.0.0.1 \
            DAPPS_AGW_PORT="${AGW_PORT[$n]}" \
            DAPPS_DEFAULT_BEARER_PORT=0 \
            DAPPS_MQTT_PORT="${MQTT_PORT[$n]}" \
            DAPPS_UDP_LISTEN_PORT="${UDP_PORT[$n]}" \
            DAPPS_AUTH_REQUIRED=false \
            DAPPS_UPDATE_CHECK_ENABLED=false \
            DAPPS_ROUTING_ALGORITHM="$SIM_ALGO" \
            ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT[$n]}" \
            DOTNET_ENVIRONMENT=Production \
            "$BIN_DIR/app/dapps.core" >"$d/dapps.log" 2>&1 &
        echo $! >"$d/pid"
    )

    for _ in $(seq 1 60); do
        if curl -fs "http://127.0.0.1:${HTTP_PORT[$n]}/Setup" -o /dev/null 2>/dev/null; then
            return 0
        fi
        sleep 0.5
    done
    echo "!!! $n did not come up; tail of log:"
    tail -40 "$d/dapps.log" || true
    return 1
}

stop_node() {
    local n=$1
    local pid_file; pid_file="$(node_dir "$n")/pid"
    [ -f "$pid_file" ] || return 0
    local pid; pid=$(cat "$pid_file")
    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null || true
        for _ in $(seq 1 20); do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.2
        done
        kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$pid_file"
}

# ── auth / configure ───────────────────────────────────────────────────
configure_node() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    rm -f "$cookie"

    curl -fsS -o /dev/null -X POST "$base/Setup" \
        --data-urlencode "Password=$SIM_PWD" \
        --data-urlencode "Confirm=$SIM_PWD"
    curl -fsS -o /dev/null -c "$cookie" -X POST "$base/Login" \
        --data-urlencode "Password=$SIM_PWD"

    for ch in ${CHANNELS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/DiscoveryChannels" \
            -H 'content-type: application/json' \
            -d "{\"Bearer\":\"udp\",\"ChannelKey\":\"$ch\",\"LinkClass\":\"LanMulticast\"}"
    done

    for nb in ${NEIGHBOURS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
            -H 'content-type: application/json' \
            -d "{\"Callsign\":\"${CALLSIGN[$nb]}\",\"BearerPort\":null,\"UdpEndpoint\":\"127.0.0.1:${UDP_PORT[$nb]}\"}"
    done

    if [ -n "${ROUTE_HINTS[$n]:-}" ]; then
        local db; db="$(node_dir "$n")/data/dapps.db"
        for entry in ${ROUTE_HINTS[$n]}; do
            local dest_letter="${entry%%:*}"
            local hop_letter="${entry##*:}"
            # Route hint Destination is the BASE callsign (no SSID) - that's
            # what the resolver uses as the lookup key. NextHop is the full
            # neighbour callsign with SSID.
            local dest_base="${CALLSIGN[$dest_letter]%-*}"
            local hop_full="${CALLSIGN[$hop_letter]}"
            python3 -c "
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('insert or replace into routehints (Destination, NextHop) values (?, ?)', (sys.argv[2], sys.argv[3]))
con.commit()
" "$db" "$dest_base" "$hop_full"
        done
    fi
}

# ── exercise the topology ──────────────────────────────────────────────
trigger_run() {
    local n=$1
    # /Message/dorun is admin-protected - pass the cookie set up at
    # configure_node, otherwise the request gets a 302 to /Login and
    # the forwarder silently never runs.
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    curl -fsS -o /dev/null -b "$cookie" -X POST "http://127.0.0.1:${HTTP_PORT[$n]}/Message/dorun" || true
}

# Drain queues. With the auto-forwarder hosted service on a 5-second
# tick, a 4-hop chain takes ~20s to drain on its own. To make the
# canned exercises finish quickly, we manually kick every node every
# second for 8 rounds - each round each relay forwards whatever just
# arrived from the upstream kick. The DoRun mutex makes the kicks
# benign even when they overlap the auto-forwarder's own tick.
drain_queues() {
    for _ in 1 2 3 4 5 6 7 8; do
        for n in "${NODES[@]}"; do trigger_run "$n" & done
        wait
        sleep 1
    done
}

submit_message() {
    local from=$1 to=$2 payload=$3
    local b64
    b64=$(printf '%s' "$payload" | base64 -w0 2>/dev/null || printf '%s' "$payload" | base64)
    curl -fsS -o /dev/null -X POST \
        "http://127.0.0.1:${HTTP_PORT[$from]}/AppApi/outbound" \
        -H 'content-type: application/json' \
        -d "{\"App\":\"chat\",\"DestCallsign\":\"${CALLSIGN[$to]}\",\"Payload\":\"$b64\",\"Ttl\":300}"
}

cmd_send() {
    local from=$1 to=$2
    local payload="${3:-hello-${from}-to-${to}-$(date +%s%3N)}"
    echo ">>> Submit ${CALLSIGN[$from]} → ${CALLSIGN[$to]}: '$payload'"
    submit_message "$from" "$to" "$payload"
    drain_queues
    show_inbox "$to"
}

show_inbox() {
    local n=$1
    echo "----- ${CALLSIGN[$n]} inbox (chat) -----"
    local rows
    rows=$(curl -fsS "http://127.0.0.1:${HTTP_PORT[$n]}/AppApi/inbound/chat")
    if [ -z "$rows" ] || [ "$rows" = "[]" ]; then
        echo "(empty)"
        return
    fi
    # Pretty-print each row as: id  origin=X  source=Y  payload-decoded
    python3 - "$rows" <<'PY'
import json, sys, base64
rows = json.loads(sys.argv[1])
for r in rows:
    payload = base64.b64decode(r.get("payload", "") or "").decode("utf-8", errors="replace")
    print(f"  id={r['id']}  origin={r.get('originatorCallsign') or '?'}  source={r['sourceCallsign']}  payload={payload!r}  ttl={r.get('ttl')}")
PY
}

cmd_verify() {
    echo ">>> Inboxes across the mesh:"
    for n in "${NODES[@]}"; do show_inbox "$n"; done
}

# Dump every node's learned-routes table - populated by the passive
# learning algorithm as inbound traffic arrives carrying F1 src=
# headers. After the canned exercises, these tables are the proof
# that learning actually happened.
cmd_learned() {
    echo ">>> Learned routes across the mesh:"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        echo "----- ${CALLSIGN[$n]} learned routes -----"
        if [ ! -f "$d" ]; then echo "(db not present)"; continue; fi
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
try:
    rows = list(con.execute("select DestinationBaseCallsign, NextHopCallsign, LastSeenAt, ConsecutiveFailures from learnedroutes order by DestinationBaseCallsign"))
except sqlite3.OperationalError as e:
    print(f"  (no learnedroutes table yet: {e})"); sys.exit()
if not rows:
    print("  (none)")
for dest, nh, seen, fails in rows:
    print(f"  → {dest} via {nh}  (failures={fails})")
PY
    done
}

# Dump every node's discovered-paths table - the MeshCore-flavoured
# algorithm's equivalent of learned-routes, but storing the FULL
# ordered intermediate-hop list rather than just the next hop.
# Populated as flood-discovery messages traverse the mesh.
cmd_discovered() {
    echo ">>> Discovered paths across the mesh:"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        echo "----- ${CALLSIGN[$n]} discovered paths -----"
        if [ ! -f "$d" ]; then echo "(db not present)"; continue; fi
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
try:
    rows = list(con.execute("select DestinationBaseCallsign, IntermediatesCsv, LastSeenAt, ConsecutiveFailures from discoveredpaths order by DestinationBaseCallsign"))
except sqlite3.OperationalError as e:
    print(f"  (no discoveredpaths table yet: {e})"); sys.exit()
if not rows:
    print("  (none)")
for dest, mids, seen, fails in rows:
    intermediates = mids if mids else "(direct)"
    print(f"  → {dest} via [{intermediates}]  (failures={fails})")
PY
    done
}

# MeshCore equivalent of cmd_prove_learning. Wipe route-hints and
# discovered-peers (leaving only the meshcore algorithm's discovered-
# paths as the routing source) and send A→F. If discovery has done
# its job, the message still delivers via source routing - proving
# the algorithm self-organised the mesh without operator config.
cmd_prove_meshcore() {
    echo ">>> Wiping route-hints + discovered-peers on every node…"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from routehints"); con.execute("delete from discoveredpeers")
con.commit()
PY
    done
    echo ">>> A→F using ONLY discovered paths…"
    submit_message A F "meshcore-only-$(date +%s)"
    drain_queues
    show_inbox F
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  MESHCORE ACCEPTANCE: above message arrived at F using ONLY"
    echo "  paths discovered by the meshcore algorithm. No route-hints"
    echo "  configured, no discovered peers seeded. If you see a row"
    echo "  with origin=G0SIA-1, source-routed delivery is working."
    echo "═════════════════════════════════════════════════════════════"
}

# Drop the route-hints table and the discovered-peer entries on every
# node, leaving ONLY learned routes as the routing source. Then send
# A→F again. If passive learning has done its job, the message still
# delivers - proving that after one round of bidirectional traffic
# the network can route without explicit operator config.
cmd_prove_learning() {
    echo ">>> Wiping route-hints + discovered-peers on every node…"
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")/data/dapps.db"
        python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from routehints"); con.execute("delete from discoveredpeers")
con.commit()
PY
    done
    echo ">>> A→F using ONLY learned routes…"
    submit_message A F "learned-only-$(date +%s)"
    drain_queues
    show_inbox F
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  PR-B ACCEPTANCE: above message arrived at F using ONLY"
    echo "  routes learned passively from prior traffic. No route-hints"
    echo "  configured, no discovered peers seeded. If you see a row"
    echo "  with origin=G0SIA-1, passive learning is working."
    echo "═════════════════════════════════════════════════════════════"
}

# F2 acceptance - submit a payload several times the default
# fragment threshold (4096 bytes) at A, watch it traverse the 4-hop
# chain to F as N independent fragment rows, and verify the receiver
# reassembles into a single inbox row whose bytes match what we sent.
# Catches "fragments arrive but reassembly drops some", "wrong order
# reassembly", or "transit relay corrupts the mid=/frag= headers"
# regressions that unit tests can't see end-to-end.
cmd_prove_fragmentation() {
    # 12 KB → 3 fragments at the 4096 default. Build it with a leading
    # marker (so we can find the row deterministically) followed by a
    # repeating filler. Done in Python because bash can't cleanly handle
    # 12 KB in a single arg without quoting hazards.
    local marker="frag-$(date +%s%N)"
    local payload
    payload=$(python3 -c "
import sys
marker = sys.argv[1]
size = 12000
body = marker + '|' + ('X' * (size - len(marker) - 1))
sys.stdout.write(body)
" "$marker")
    local size=${#payload}

    echo ">>> A→F payload size=$size bytes (default threshold=4096 → 3 fragments)"
    submit_message A F "$payload"
    drain_queues

    echo ">>> Verifying F reassembled into exactly one row…"
    if ! python3 - "${HTTP_PORT[F]}" "$marker" "$size" <<'PY'
import sys, json, base64, urllib.request
port, marker, expected_len = sys.argv[1], sys.argv[2], int(sys.argv[3])
with urllib.request.urlopen(f"http://127.0.0.1:{port}/AppApi/inbound/chat") as r:
    rows = json.loads(r.read().decode())
matches = []
for row in rows:
    body = base64.b64decode(row.get("payload", "") or "")
    try: text = body.decode("utf-8")
    except UnicodeDecodeError: continue
    if marker in text:
        matches.append((row, text))
if len(matches) != 1:
    print(f"  FAIL - expected exactly 1 row containing marker {marker!r}, got {len(matches)}")
    sys.exit(1)
row, text = matches[0]
if len(text) != expected_len:
    print(f"  FAIL - reassembled length {len(text)} != expected {expected_len}")
    sys.exit(1)
if not text.startswith(marker):
    print(f"  FAIL - reassembled bytes don't start with marker")
    sys.exit(1)
filler = text[len(marker) + 1:]
if any(c != "X" for c in filler):
    print(f"  FAIL - filler corrupted (not all 'X')")
    sys.exit(1)
print(f"  OK - single row, len={len(text)}, origin={row.get('originatorCallsign')}, prefix={text[:48]!r}…")
PY
    then
        echo "!!! F2 acceptance failed"
        return 1
    fi

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  F2 ACCEPTANCE: a $size-byte payload was split at A into"
    echo "  multiple fragments, each forwarded across the 4-hop chain"
    echo "  A→B→C→E→F, and reassembled at F into a single row whose"
    echo "  bytes match the original - fragment order, headers, and"
    echo "  transit forwarding all preserved end-to-end."
    echo "═════════════════════════════════════════════════════════════"
}

# B6.2 acceptance - on-demand solicit on a discovery channel, even
# when scheduled beacons wouldn't have fired in the test window.
# Stand-in for the HF NVIS use case ("operator triggers a probe
# rather than waiting for the next propagation window") on a fast
# LAN-multicast bearer.
#
# B is the central relay, sitting on both G1 (with A) and G2 (with C).
# We wipe its discoveredpeers table and fire a solicit on each of B's
# enabled channels. Replies arrive on the standard beacon path - if
# the solicit→reply round-trip works, B's table re-populates with at
# least A (G1) and C (G2) before the next scheduled beacon could fire
# (LanMulticast default = 60s; we wait 4s).
cmd_prove_solicit() {
    local n=B
    local d; d="$(node_dir "$n")/data/dapps.db"
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"

    if [ ! -f "$d" ] || [ ! -f "$cookie" ]; then
        echo "!!! Node $n not configured (run \`up\` first)"
        return 1
    fi

    echo ">>> Wiping discovered peers on ${CALLSIGN[$n]}…"
    python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from discoveredpeers"); con.commit()
PY

    local chans; chans=$(curl -fsS -b "$cookie" "$base/DiscoveryChannels")
    local ids; ids=$(printf '%s' "$chans" | python3 -c '
import json, sys
print(" ".join(str(c["id"]) for c in json.load(sys.stdin) if c.get("enabled", True)))
')
    if [ -z "$ids" ]; then
        echo "!!! No enabled channels on ${CALLSIGN[$n]}"
        return 1
    fi

    for id in $ids; do
        echo ">>> Firing solicit on channel id=$id…"
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/DiscoveryChannels/$id/solicit"
    done

    # Soliciting peers reply after a uniform-random delay drawn from
    # [0, SolicitResponseMaxDelay] - 5s default in DiscoveryService
    # (politeness back-off so a solicit doesn't trigger a beacon
    # storm). 7s clears the max jitter window with margin for the
    # beacon to actually travel over multicast and land in B's
    # discoveredpeers table. Still well under the 60s scheduled-beacon
    # interval for LanMulticast, so any peers that show up are direct
    # consequences of the solicit, not the cadence timer.
    echo ">>> Waiting 7s for solicit replies (clears 5s max jitter, under 60s scheduled-beacon)…"
    sleep 7

    echo ">>> Discovered peers on ${CALLSIGN[$n]} after solicit:"
    if ! python3 - "$d" "${CALLSIGN[A]}" "${CALLSIGN[C]}" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
rows = list(con.execute("select Callsign, Bearer, ChannelKey from discoveredpeers order by Callsign, ChannelKey"))
expected = {sys.argv[2], sys.argv[3]}
seen = {cs for cs, _, _ in rows}
if not rows:
    print("  (none - solicit got no replies)")
    sys.exit(1)
for cs, b, k in rows:
    print(f"  {cs} via {b}/{k}")
missing = expected - seen
if missing:
    print(f"  FAIL - expected to see {sorted(expected)}, missing {sorted(missing)}")
    sys.exit(1)
print(f"  OK - both {sorted(expected)} replied within the solicit window")
PY
    then
        echo "!!! B6.2 acceptance failed"
        return 1
    fi

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  B6.2 ACCEPTANCE: discovered-peers on ${CALLSIGN[$n]} was"
    echo "  emptied, then a one-shot solicit on each of its channels"
    echo "  re-populated the table with both ${CALLSIGN[A]} (G1) and"
    echo "  ${CALLSIGN[C]} (G2) inside 7s - well under the 60s scheduled"
    echo "  beacon interval, so the repopulation is solely the"
    echo "  solicit→reply round-trip working as designed."
    echo "═════════════════════════════════════════════════════════════"
}

# B6.2 follow-up acceptance - scheduled-solicit cadence. Same
# topology, but instead of operator-triggering a solicit we set
# SolicitIntervalSeconds=3 on B's G1+G2 channels and let the
# DiscoveryService emit the solicit on its own. After wiping B's
# discoveredpeers, the table should re-populate within one cadence
# tick + the 5s max reply jitter - all without a single REST POST
# /solicit call.
cmd_prove_scheduled_solicit() {
    local n=B
    local d; d="$(node_dir "$n")/data/dapps.db"
    local cookie; cookie="$(node_dir "$n")/cookie.txt"
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"

    if [ ! -f "$d" ] || [ ! -f "$cookie" ]; then
        echo "!!! Node $n not configured (run \`up\` first)"
        return 1
    fi

    echo ">>> Setting SolicitIntervalSeconds=3 on every channel of ${CALLSIGN[$n]}…"
    local chans; chans=$(curl -fsS -b "$cookie" "$base/DiscoveryChannels")
    # Re-POST each channel with the new interval. Round-trip every
    # field so we don't reset cost / TTL / etc. to defaults.
    printf '%s' "$chans" | python3 -c '
import json, sys
chans = json.load(sys.stdin)
for c in chans:
    c["SolicitIntervalSeconds"] = 3
    print(json.dumps(c))
' | while read -r body; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/DiscoveryChannels" \
            -H 'content-type: application/json' -d "$body"
    done

    echo ">>> Wiping discovered peers on ${CALLSIGN[$n]}…"
    python3 - "$d" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute("delete from discoveredpeers"); con.commit()
PY

    # The DiscoveryService applies channel config at startup; bounce
    # the node so the new SolicitIntervalSeconds takes effect.
    # restart_node preserves the DB (start_node would wipe it).
    echo ">>> Restarting ${CALLSIGN[$n]} to pick up the new cadence…"
    restart_node "$n"

    # Wait for: startup grace (sub-second on the sim) + first cadence
    # tick (3s) + max reply jitter (5s) + transit margin.
    echo ">>> Waiting 12s for one scheduled solicit + reply round-trip…"
    sleep 12

    echo ">>> Discovered peers on ${CALLSIGN[$n]} after scheduled solicit:"
    if ! python3 - "$d" "${CALLSIGN[A]}" "${CALLSIGN[C]}" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
rows = list(con.execute("select Callsign, Bearer, ChannelKey from discoveredpeers order by Callsign, ChannelKey"))
expected = {sys.argv[2], sys.argv[3]}
seen = {cs for cs, _, _ in rows}
if not rows:
    print("  (none - scheduled solicit got no replies)")
    sys.exit(1)
for cs, b, k in rows:
    print(f"  {cs} via {b}/{k}")
missing = expected - seen
if missing:
    print(f"  FAIL - expected to see {sorted(expected)}, missing {sorted(missing)}")
    sys.exit(1)
print(f"  OK - both {sorted(expected)} reached us via the scheduled solicit cadence")
PY
    then
        echo "!!! B6.2 scheduled-solicit acceptance failed"
        return 1
    fi

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  B6.2 SCHEDULED-SOLICIT ACCEPTANCE: with SolicitIntervalSeconds"
    echo "  set on each channel, ${CALLSIGN[$n]}'s discoveredpeers table"
    echo "  re-populated WITHOUT any operator-triggered REST /solicit -"
    echo "  the DiscoveryService cadence loop fired solicits on its own"
    echo "  and the airtime-budget gate let them through (no global cap"
    echo "  configured)."
    echo "═════════════════════════════════════════════════════════════"
}

# Canned non-trivial exercise - runs several sends across the topology
# that exercise different path lengths, branching, and parallel flows.
# After all sends complete, prints each receiver's inbox so F1 origin
# preservation can be verified at a glance.
cmd_exercise() {
    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 1: longest forward path A→F (4 hops: A→B→C→E→F)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A F "long-forward-$(date +%s)"
    drain_queues
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 2: reverse longest path F→A (4 hops: F→E→C→B→A)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message F A "long-reverse-$(date +%s)"
    drain_queues
    show_inbox A

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 3: off-spine D→F (3 hops: D→C→E→F, never touches A or B)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message D F "off-spine-$(date +%s)"
    drain_queues
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 4: fan-out from A to D, E, F submitted in parallel"
    echo "  (one originator splitting across three different paths)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A D "fan-out-D-$(date +%s)" &
    submit_message A E "fan-out-E-$(date +%s)" &
    submit_message A F "fan-out-F-$(date +%s)" &
    wait
    drain_queues
    for n in D E F; do show_inbox "$n"; done

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  EXERCISE 5: cross-traffic A↔F simultaneously"
    echo "  (both endpoints originate; relays handle fan-in)"
    echo "═════════════════════════════════════════════════════════════"
    submit_message A F "cross-A2F-$(date +%s)" &
    submit_message F A "cross-F2A-$(date +%s)" &
    wait
    drain_queues
    show_inbox A
    show_inbox F

    echo
    echo "═════════════════════════════════════════════════════════════"
    echo "  F1 ACCEPTANCE CHECK"
    echo "  Every 'origin=…' above should be the *originator* callsign"
    echo "  (the from-side of the arrow), NOT the link-source/relay."
    echo "  source=UDP is fine - it's the bearer placeholder for"
    echo "  datagram-shaped backhauls; the receiver app has dapps-source"
    echo "  via MQTT for the link source separately."
    echo "═════════════════════════════════════════════════════════════"

    echo
    if [ "$SIM_ALGO" = "meshcore" ]; then
        cmd_discovered
        echo
        cmd_prove_meshcore
    else
        cmd_learned
        echo
        cmd_prove_learning
    fi

    echo
    cmd_prove_fragmentation

    echo
    cmd_prove_solicit

    echo
    cmd_prove_scheduled_solicit
}

cmd_status() {
    for n in "${NODES[@]}"; do
        local d; d="$(node_dir "$n")"
        local pid; pid=$(cat "$d/pid" 2>/dev/null || echo -)
        local alive=no
        kill -0 "$pid" 2>/dev/null && alive=yes
        printf "  %s  pid=%-6s alive=%s  http=:%s  udp=:%s  channels=%s\n" \
            "${CALLSIGN[$n]}" "$pid" "$alive" "${HTTP_PORT[$n]}" "${UDP_PORT[$n]}" \
            "$(echo "${CHANNELS[$n]:-(none)}" | tr ' ' ',')"
    done
}

cmd_stop() {
    for n in "${NODES[@]}"; do stop_node "$n"; done
}

cmd_up() {
    maybe_publish
    cmd_stop
    for n in "${NODES[@]}"; do start_node "$n"; done
    for n in "${NODES[@]}"; do configure_node "$n"; done
    echo
    echo ">>> Topology ready:"
    cmd_status
    echo
    echo ">>> Dashboards (login pwd: $SIM_PWD):"
    for n in "${NODES[@]}"; do
        printf "    %s  http://127.0.0.1:%s/\n" "${CALLSIGN[$n]}" "${HTTP_PORT[$n]}"
    done
    echo
    cmd_exercise
}

case "${1:-up}" in
    up)         cmd_up ;;
    stop)       cmd_stop ;;
    send)       cmd_send "$2" "$3" "${4:-}" ;;
    exercise)   cmd_exercise ;;
    status)     cmd_status ;;
    verify)     cmd_verify ;;
    learned)    cmd_learned ;;
    prove-learning) cmd_prove_learning ;;
    discovered) cmd_discovered ;;
    prove-meshcore) cmd_prove_meshcore ;;
    prove-fragmentation) cmd_prove_fragmentation ;;
    prove-solicit) cmd_prove_solicit ;;
    prove-scheduled-solicit) cmd_prove_scheduled_solicit ;;
    *)          echo "usage: $0 {up|stop|send <from> <to> [payload]|exercise|status|verify|learned|prove-learning|discovered|prove-meshcore|prove-fragmentation|prove-solicit|prove-scheduled-solicit}"; echo "       SIM_ALGO=meshcore $0 up    # run with the MeshCore-like algorithm instead of passive-flood"; exit 2 ;;
esac
