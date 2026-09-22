#!/usr/bin/env bash
#
# Mixed-bearer multi-hop simulator. Spins up real BPQ + XRouter
# containers wired together over AX.25-over-UDP point-to-point partner
# links, with one DAPPS daemon per container, and proves DAPPS routes
# messages across mixed bearers in a non-trivial topology.
#
# This is the heavier sibling of `sim-multihop.sh`. The lighter sim is
# pure dapps-daemons-over-UDP-multicast on loopback - fast to start, no
# Docker, validates the routing layer's bearer-agnostic seam. This one
# runs real packet-node containers in the path; it's slower (Docker
# pulls + container boot + AGW handshake + NODES gossip) but it's the
# only proof that DAPPS messages actually flow end-to-end across a
# BPQ <-> XRouter <-> BPQ <-> XRouter chain.
#
# Topology
#
#                  DAPPS-A
#                  on BPQ-1 (hub)
#                 /        \
#              AXUDP      AXUDP
#               |           |
#             XR-2        BPQ-3 ────── AXUDP ────── XR-4
#              |           |                         |
#           DAPPS-B      DAPPS-C                  DAPPS-D
#
# Path lengths: A<->B 1 hop (BPQ<->XR), A<->C 1 hop (BPQ<->BPQ),
# A<->D 2 hops (BPQ<->BPQ<->XR), B<->C 2 hops (XR<->BPQ<->BPQ),
# B<->D 3 hops, mixed bearers (XR<->BPQ<->BPQ<->XR) - the showpiece.
#
# All containers run with --network=host on the loopback interface.
# DAPPS daemons run on the host directly (not containerised).
#
# Usage:
#   scripts/sim-mixed-bearer.sh up         # bring up containers + DAPPS
#   scripts/sim-mixed-bearer.sh exercise   # send the canned set of messages
#   scripts/sim-mixed-bearer.sh status     # PIDs, ports, container health
#   scripts/sim-mixed-bearer.sh verify     # show every node's inbox
#   scripts/sim-mixed-bearer.sh down       # tear everything down
#   scripts/sim-mixed-bearer.sh send X Y   # X -> Y one-shot (X, Y in {A,B,C,D})
#
# Requires: dotnet 10 SDK, docker, curl, python3, host loopback
# available, ports 17001-17004, 28000-28013, 11001-11004, 11881-11884
# free.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="${SIM_DIR:-/tmp/dapps-mixed-sim}"
BIN_DIR="$SIM_DIR/bin"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SIM_PWD="${SIM_PWD:-simpassw0rd}"

BPQ_IMAGE="${BPQ_IMAGE:-m0lte/linbpq:latest}"
XR_IMAGE="${XR_IMAGE:-ghcr.io/packethacking/xrouter:latest}"

# ── topology tables ────────────────────────────────────────────────────
# Keys are short letters; everything else looks them up.

NODES=(A B C D)

# Bearer kind: bpq | xr.
declare -A KIND=(   [A]=bpq   [B]=xr    [C]=bpq   [D]=xr   )

# Container names (host-side).
declare -A CTR=(    [A]=mxsim-bpq1  [B]=mxsim-xr2  [C]=mxsim-bpq3  [D]=mxsim-xr4 )

# AGW listen port on each container (host loopback). Used by the
# BPQ-side DAPPS daemons. XR-side DAPPS daemons use RHPv2 instead so
# B and D's AGW ports are advisory; the value still has to be unique
# per netns since XR's AGWHOST emulator binds it.
declare -A AGW_PORT=( [A]=28001 [B]=28002 [C]=28003 [D]=28004 )

# RHPv2 listen port on each XR container. BPQ doesn't speak RHPv2
# today so A and C are unused.
declare -A RHP_PORT=( [B]=29002 [D]=29004 )

# AXUDP partner-link listen port for each container.
declare -A AXUDP_PORT=( [A]=11001 [B]=11002 [C]=11003 [D]=11004 )

# BPQ telnet port (for ad-hoc sysop access). XR containers don't need
# one for the sim.
declare -A TELNET_PORT=( [A]=28011 [C]=28013 )

# Container's own NODECALL (the packet node's AX.25 callsign).
# XRouter's callsign validator enforces standard amateur format
# (prefix letters+digit, suffix letters only) - no embedded digits in
# the suffix. So we use 5-char shapes like G0NDA-1, not G0SPK2-1.
declare -A NODECALL=( [A]=G0NDA-1 [B]=G0NDB-1 [C]=G0NDC-1 [D]=G0NDD-1 )

# Container's NODEALIAS (no SSID, max 6 chars).
declare -A NODEALIAS=( [A]=NDA [B]=NDB [C]=NDC [D]=NDD )

# DAPPS daemon callsign attached to this container (must NOT collide
# with NODECALL / CONSOLECALL / CHATCALL on the same container, and
# must satisfy XR's callsign validator on XR containers).
declare -A DAPPS_CALL=( [A]=G0DPA-1 [B]=G0DPB-1 [C]=G0DPC-1 [D]=G0DPD-1 )

# DAPPS daemon HTTP / MQTT ports.
declare -A HTTP_PORT=( [A]=17001 [B]=17002 [C]=17003 [D]=17004 )
declare -A MQTT_PORT=( [A]=11881 [B]=11882 [C]=11883 [D]=11884 )

# Direct AXUDP partners. Space-separated letters.
declare -A PARTNERS=(
    [A]="B C"
    [B]="A"
    [C]="A D"
    [D]="C"
)

# Direct DAPPS neighbours. Same shape - DAPPS-A treats DAPPS-B and
# DAPPS-C as next-hop neighbours, etc.
declare -A NEIGHBOURS=(
    [A]="B C"
    [B]="A"
    [C]="A D"
    [D]="C"
)

# Route hints (force forwarding through a specific neighbour). Each
# entry "<dest>:<hop>" is a static "to reach <dest> base callsign,
# next-hop is <hop>'s full DAPPS callsign". Used so 2- and 3-hop
# exercises are deterministic without waiting on passive learning.
declare -A ROUTE_HINTS=(
    [A]="D:C"      # A -> D goes via C
    [B]="C:A D:A"  # B -> C and B -> D both go via A
    [C]="B:A"      # C -> B goes via A
    [D]="A:C B:C"  # D -> A and D -> B both go via C
)

mkdir -p "$SIM_DIR" "$BIN_DIR"

# ── publish dapps.core once, cache by source mtime ────────────────────
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

# ── BPQ config rendering ──────────────────────────────────────────────
render_bpq_config() {
    local n=$1
    local nodecall=${NODECALL[$n]} alias=${NODEALIAS[$n]}
    local agw=${AGW_PORT[$n]} telnet=${TELNET_PORT[$n]} axudp=${AXUDP_PORT[$n]}
    local maps=""
    for p in ${PARTNERS[$n]}; do
        local p_axudp=${AXUDP_PORT[$p]}
        # Map each remote node's NODECALL + DAPPS callsign through the
        # corresponding AXUDP partner. BPQ uses these to dispatch L2
        # connects originated by DAPPS down the right partner link.
        maps+=" MAP ${NODECALL[$p]} 127.0.0.1 UDP $p_axudp B"$'\n'
        maps+=" MAP ${DAPPS_CALL[$p]} 127.0.0.1 UDP $p_axudp B"$'\n'
    done
    local routes=""
    for p in ${PARTNERS[$n]}; do
        routes+="${NODECALL[$p]},200,2"$'\n'
    done
    # BPQ APPLICATIONS line for the locally-attached DAPPS callsign.
    # Without this, BPQ refuses inbound L2 connects to the DAPPS
    # callsign with RETRYOUT - the APPLICATION declaration is what
    # tells BPQ "accept connects to this call and dispatch them via
    # AGW to whichever client registered it." XR has the opposite
    # rule (no APPL block, just X-frame registration); BPQ requires
    # the explicit declaration.
    local dapps_call=${DAPPS_CALL[$n]}
    cat <<EOF
SIMPLE=1
NODECALL=$nodecall
NODEALIAS=$alias
LOCATOR=NONE
NODESINTERVAL=1
AGWPORT=$agw
AGWSESSIONS=20
AGWMASK=1
APPLICATIONS=DAPPS
APPL1CALL=$dapps_call
APPL1ALIAS=DAPPS

PORT
 ID=Telnet
 DRIVER=Telnet
 CONFIG
 TCPPORT=$telnet
 MAXSESSIONS=20
 USER=test,test,$nodecall,,SYSOP
ENDPORT

PORT
 ID=AXUDP
 DRIVER=BPQAXIP
 QUALITY=200
 MINQUAL=1
 CONFIG
 UDP $axudp
 BROADCAST NODES
$maps
ENDPORT

ROUTES:
$routes
***

EOF
}

# ── XRouter config rendering ──────────────────────────────────────────
render_xr_config() {
    local n=$1
    local nodecall=${NODECALL[$n]} alias=${NODEALIAS[$n]}
    local agw=${AGW_PORT[$n]} axudp=${AXUDP_PORT[$n]}
    # XR requires CONSOLECALL / CHATCALL distinct from NODECALL. Derive
    # by stripping the SSID and adding new ones.
    local base=${nodecall%-*}
    local consolecall=$base
    local chatcall=$base-8
    # PORT blocks: one per AXUDP partner. Each has a unique
    # INTERFACENUM (its INTERFACE definition) and UDPLOCAL/UDPREMOTE
    # pair. Numbering starts at 1.
    local interfaces=""
    local ports=""
    local portnum=1
    for p in ${PARTNERS[$n]}; do
        local p_axudp=${AXUDP_PORT[$p]}
        # XR requires UDPLOCAL to be unique per port; use the
        # container's main AXUDP port for the first partner, then
        # synthesise additional ports if there are more partners.
        # In our topology XR containers only have one partner each,
        # so this simplifies to one PORT per XR container.
        local local_udp=$axudp
        if [ $portnum -gt 1 ]; then
            # Bump to a side port. Convention: AXUDP base + 100*idx.
            local_udp=$((axudp + (portnum - 1) * 100))
        fi
        interfaces+="INTERFACE=$portnum"$'\n'
        interfaces+=$'\t'"TYPE=AXUDP"$'\n'
        interfaces+=$'\t'"MTU=256"$'\n'
        interfaces+="ENDINTERFACE"$'\n'$'\n'

        ports+="PORT=$portnum"$'\n'
        ports+=$'\t'"ID=\"AXUDP partner $p\""$'\n'
        ports+=$'\t'"INTERFACENUM=$portnum"$'\n'
        ports+=$'\t'"UDPLOCAL=$local_udp"$'\n'
        ports+=$'\t'"IPLINK=127.0.0.1"$'\n'
        ports+=$'\t'"UDPREMOTE=$p_axudp"$'\n'
        ports+="ENDPORT"$'\n'$'\n'
        portnum=$((portnum + 1))
    done
    cat <<EOF
DNS=8.8.8.8
NODECALL=$nodecall
NODEALIAS=$alias
LOCATOR=IO91PM
CONSOLECALL=$consolecall
CHATCALL=$chatcall
CHATALIAS=${alias:0:5}C
AGWPORT=$agw
RHPPORT=${RHP_PORT[$n]}
IPADDRESS=44.128.0.1

$interfaces
$ports
EOF
}

# Permissive ACCESS.SYS for XR so loopback AGW connections from the
# host's DAPPS daemons land without password.
render_xr_access_sys() {
    cat <<'EOF'
127.0.0.0/8	1
44.0.0.0/8	1
192.168.0.0/24	1
0.0.0.0/0	7
EOF
}

# ── container start / stop ────────────────────────────────────────────
ctr_dir() { echo "$SIM_DIR/$1"; }

# All four containers share node A's netns (pattern lifted from
# TwoInstanceLinbpqFixture). A publishes every port we want exposed to
# the WSL2 / host side; B/C/D run with --network=container:<A's-id>.
# Loopback (127.0.0.1) reaches all four containers' listeners. The
# BPQ AXUDP MAP entries can address partners as 127.0.0.1:<UDPLOCAL>
# without container-IP discovery, and AGW ports are reachable from
# the host-side DAPPS daemons.
#
# --network=host fails on WSL2 / Docker Desktop because Docker
# Desktop's "host" is its own Linux VM, not WSL2 userspace. The
# shared-netns pattern works under both Docker Desktop and native
# Linux Docker, so it's the portable choice.
start_container() {
    local n=$1
    local kind=${KIND[$n]} ctr=${CTR[$n]}
    local d; d="$(ctr_dir "$n")"
    if [ -d "$d" ]; then
        docker run --rm -v "$d:/wipe" alpine sh -c 'rm -rf /wipe/* /wipe/.[!.]* 2>/dev/null' 2>/dev/null || true
    fi
    mkdir -p "$d"

    # Network-mode arg: A creates a fresh netns + publishes every port
    # any container in the topology will bind. B/C/D join A's netns.
    local netflags=()
    if [ "$n" = "A" ]; then
        netflags+=( -p "127.0.0.1:${AGW_PORT[A]}:${AGW_PORT[A]}" )
        netflags+=( -p "127.0.0.1:${AGW_PORT[B]}:${AGW_PORT[B]}" )
        netflags+=( -p "127.0.0.1:${AGW_PORT[C]}:${AGW_PORT[C]}" )
        netflags+=( -p "127.0.0.1:${AGW_PORT[D]}:${AGW_PORT[D]}" )
        netflags+=( -p "127.0.0.1:${TELNET_PORT[A]}:${TELNET_PORT[A]}" )
        netflags+=( -p "127.0.0.1:${TELNET_PORT[C]}:${TELNET_PORT[C]}" )
        # XR-side DAPPS daemons reach their containers via RHPv2.
        netflags+=( -p "127.0.0.1:${RHP_PORT[B]}:${RHP_PORT[B]}" )
        netflags+=( -p "127.0.0.1:${RHP_PORT[D]}:${RHP_PORT[D]}" )
    else
        netflags+=( --network "container:${CTR[A]}" )
    fi

    if [ "$kind" = "bpq" ]; then
        render_bpq_config "$n" > "$d/bpq32.cfg"
        echo ">>> Starting BPQ container $ctr (AGW :${AGW_PORT[$n]}, telnet :${TELNET_PORT[$n]:-(none)}, AXUDP :${AXUDP_PORT[$n]})"
        docker run -d --name "$ctr" "${netflags[@]}" \
            -v "$d:/data" "$BPQ_IMAGE" >/dev/null
    elif [ "$kind" = "xr" ]; then
        render_xr_config "$n" > "$d/XROUTER.CFG"
        echo ">>> Starting XRouter container $ctr (AGW :${AGW_PORT[$n]}, AXUDP :${AXUDP_PORT[$n]})"
        docker run -d --name "$ctr" "${netflags[@]}" \
            -v "$d:/data" "$XR_IMAGE" >/dev/null
        # ACCESS.SYS for permissive loopback AGW. Entrypoint seeds the
        # skel ACCESS.SYS on first run; we overwrite then restart.
        sleep 3
        render_xr_access_sys | docker cp - "$ctr:/data/ACCESS.SYS" 2>/dev/null \
            || echo "    (warning: ACCESS.SYS write failed; loopback should still work)"
        docker restart "$ctr" >/dev/null
    else
        echo "!!! unknown kind '$kind' for $n" >&2; return 1
    fi
}

wait_for_agw() {
    local n=$1
    local port=${AGW_PORT[$n]}
    # 90 seconds. BPQ on a non-standard AGW port can take a bit longer
    # than 8000 to bind, and XRouter does an entrypoint config-file
    # seed on first run that adds a few seconds.
    for _ in $(seq 1 180); do
        if (echo > "/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
            return 0
        fi
        sleep 0.5
    done
    echo "!!! AGW on $n did not come up on :$port" >&2
    docker ps -a --filter "name=${CTR[$n]}" >&2
    docker logs "${CTR[$n]}" 2>&1 | tail -30 >&2
    return 1
}

stop_container() {
    local ctr=${CTR[$1]}
    docker rm -f "$ctr" >/dev/null 2>&1 || true
}

# ── DAPPS daemon launch ───────────────────────────────────────────────
dapps_dir() { echo "$SIM_DIR/dapps-$1"; }

start_dapps() {
    local n=$1
    local d; d="$(dapps_dir "$n")"
    mkdir -p "$d/data"
    rm -f "$d/data/dapps.db" "$d/dapps.log"

    # Bearer + port byte are derived from the container kind. BPQ
    # daemons use AGW (port byte 1 = AXUDP, port byte 0 = Telnet); XR
    # daemons use RHPv2 (port name "1" = the AXUDP partner, which is
    # PORT=1 in XROUTER.CFG, mapping to byte 0 internally because
    # Rhpv2OutboundTransport adds 1 to the port byte for RHPv2's
    # 1-indexed port names).
    local bearer="agw" port_byte=1 rhp=""
    if [ "${KIND[$n]}" = "xr" ]; then
        bearer="rhpv2"
        port_byte=0
        rhp="${RHP_PORT[$n]}"
    fi
    echo ">>> Starting DAPPS-$n (${DAPPS_CALL[$n]}) http=${HTTP_PORT[$n]} bearer=$bearer"
    (
        cd "$d"
        env \
            DAPPS_CALLSIGN="${DAPPS_CALL[$n]}" \
            DAPPS_NODE_HOST=127.0.0.1 \
            DAPPS_NODE_BEARER="$bearer" \
            DAPPS_AGW_PORT="${AGW_PORT[$n]}" \
            DAPPS_RHP_PORT="${rhp:-9000}" \
            DAPPS_DEFAULT_BEARER_PORT="$port_byte" \
            DAPPS_MQTT_PORT="${MQTT_PORT[$n]}" \
            DAPPS_UDP_LISTEN_PORT=0 \
            DAPPS_AUTH_REQUIRED=false \
            DAPPS_UPDATE_CHECK_ENABLED=false \
            DAPPS_HEARTBEAT_ENABLED=false \
            DAPPS_PROBING_ENABLED=false \
            ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT[$n]}" \
            DOTNET_ENVIRONMENT=Production \
            "$BIN_DIR/app/dapps.core" >"$d/dapps.log" 2>&1 &
        echo $! >"$d/pid"
    )

    for _ in $(seq 1 120); do
        if curl -fs "http://127.0.0.1:${HTTP_PORT[$n]}/Setup" -o /dev/null 2>/dev/null; then
            return 0
        fi
        sleep 0.5
    done
    echo "!!! DAPPS-$n did not come up; log tail:"
    tail -40 "$d/dapps.log" || true
    return 1
}

stop_dapps() {
    local n=$1
    local pid_file; pid_file="$(dapps_dir "$n")/pid"
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

# ── DAPPS configuration via REST ───────────────────────────────────────
configure_dapps() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"
    rm -f "$cookie"

    curl -fsS -o /dev/null -X POST "$base/Setup" \
        --data-urlencode "Password=$SIM_PWD" \
        --data-urlencode "Confirm=$SIM_PWD"
    curl -fsS -o /dev/null -c "$cookie" -X POST "$base/Login" \
        --data-urlencode "Password=$SIM_PWD"

    # Add neighbours: each direct partner's DAPPS callsign with the
    # right AGW port byte for the local container kind. BPQ has
    # Telnet (byte 0) + AXUDP (byte 1) so DAPPS uses byte 1. XR has
    # only the AXUDP port so DAPPS uses byte 0.
    local port_byte=1
    if [ "${KIND[$n]}" = "xr" ]; then port_byte=0; fi
    for nb in ${NEIGHBOURS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
            -H 'content-type: application/json' \
            -d "{\"Callsign\":\"${DAPPS_CALL[$nb]}\",\"BearerPort\":$port_byte,\"UdpEndpoint\":null}"
    done

    # Static route hints so multi-hop paths are deterministic. Format
    # in ROUTE_HINTS is "<dest-letter>:<hop-letter>"; persisted as
    # base callsign -> next-hop full callsign.
    if [ -n "${ROUTE_HINTS[$n]:-}" ]; then
        local db; db="$(dapps_dir "$n")/data/dapps.db"
        for entry in ${ROUTE_HINTS[$n]}; do
            local dest_letter="${entry%%:*}"
            local hop_letter="${entry##*:}"
            local dest_base="${DAPPS_CALL[$dest_letter]%-*}"
            local hop_full="${DAPPS_CALL[$hop_letter]}"
            python3 -c "
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('insert or replace into routehints (Destination, NextHop) values (?, ?)', (sys.argv[2], sys.argv[3]))
con.commit()
" "$db" "$dest_base" "$hop_full"
        done
    fi
}

# ── exercise / send helpers ────────────────────────────────────────────
trigger_run() {
    local n=$1
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"
    curl -fsS -o /dev/null -b "$cookie" -X POST "http://127.0.0.1:${HTTP_PORT[$n]}/Message/dorun" || true
}

drain_queues() {
    for _ in 1 2 3 4 5 6 7 8 9 10; do
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
        -d "{\"App\":\"chat\",\"DestCallsign\":\"${DAPPS_CALL[$to]}\",\"Payload\":\"$b64\",\"Ttl\":600}"
}

show_inbox() {
    local n=$1
    echo "----- ${DAPPS_CALL[$n]} inbox (chat) -----"
    local rows
    rows=$(curl -fsS "http://127.0.0.1:${HTTP_PORT[$n]}/AppApi/inbound/chat" 2>/dev/null || echo "[]")
    if [ -z "$rows" ] || [ "$rows" = "[]" ]; then
        echo "(empty)"
        return
    fi
    python3 - "$rows" <<'PY'
import json, sys, base64
rows = json.loads(sys.argv[1])
for r in rows:
    payload = base64.b64decode(r.get("payload", "") or "").decode("utf-8", errors="replace")
    print(f"  id={r['id']}  origin={r.get('originatorCallsign') or '?'}  source={r['sourceCallsign']}  payload={payload!r}  ttl={r.get('ttl')}")
PY
}

cmd_send() {
    local from=$1 to=$2
    local payload="${3:-hello-${from}-to-${to}-$(date +%s%3N)}"
    echo ">>> Submit ${DAPPS_CALL[$from]} -> ${DAPPS_CALL[$to]}: '$payload'"
    submit_message "$from" "$to" "$payload"
    drain_queues
    show_inbox "$to"
}

cmd_exercise() {
    echo "=== Mixed-bearer exercise ==="
    echo ""
    echo "Working paths (BPQ-side originated):"
    cmd_send A B "1-hop-BPQ-to-XR"
    cmd_send A C "1-hop-BPQ-to-BPQ"
    cmd_send A D "2-hop-BPQ-BPQ-XR"
    echo ""
    echo "XR-side originated paths (via the RHPv2 bearer):"
    cmd_send B C "2-hop-XR-BPQ-BPQ"
    cmd_send B D "3-hop-XR-BPQ-BPQ-XR-mixed"
    echo ""
    echo "=== Final inboxes ==="
    cmd_verify
}

cmd_verify() {
    for n in "${NODES[@]}"; do show_inbox "$n"; done
}

cmd_status() {
    echo "=== Containers ==="
    docker ps --filter name=mxsim- --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
    echo ""
    echo "=== DAPPS daemons ==="
    for n in "${NODES[@]}"; do
        local pid_file; pid_file="$(dapps_dir "$n")/pid"
        local state="(no pid)"
        if [ -f "$pid_file" ]; then
            local pid; pid=$(cat "$pid_file")
            if kill -0 "$pid" 2>/dev/null; then state="running pid=$pid"; else state="(dead)"; fi
        fi
        printf "  %s  %-10s  %s  http=:%s  agw=:%s\n" "$n" "${DAPPS_CALL[$n]}" "$state" "${HTTP_PORT[$n]}" "${AGW_PORT[$n]}"
    done
}

# ── top-level ─────────────────────────────────────────────────────────
cmd_up() {
    maybe_publish
    echo "=== Phase 1: containers ==="
    for n in "${NODES[@]}"; do start_container "$n"; done
    for n in "${NODES[@]}"; do wait_for_agw "$n"; done
    # AXUDP partners take a moment to bind and exchange initial frames.
    sleep 3
    echo "=== Phase 2: DAPPS daemons ==="
    for n in "${NODES[@]}"; do start_dapps "$n"; done
    echo "=== Phase 3: DAPPS configuration ==="
    for n in "${NODES[@]}"; do configure_dapps "$n"; done
    echo "=== Up. Run 'scripts/sim-mixed-bearer.sh exercise' or 'send X Y'. ==="
}

cmd_down() {
    for n in "${NODES[@]}"; do stop_dapps "$n"; done
    for n in "${NODES[@]}"; do stop_container "$n"; done
    echo "=== Down ==="
}

case "${1:-}" in
    up)       cmd_up ;;
    down)     cmd_down ;;
    status)   cmd_status ;;
    verify)   cmd_verify ;;
    exercise) cmd_exercise ;;
    send)     cmd_send "$2" "$3" "${4:-}" ;;
    "")       echo "Usage: $0 {up|down|status|verify|exercise|send X Y}"; exit 1 ;;
    *)        echo "Unknown command: $1"; exit 1 ;;
esac
