#!/usr/bin/env bash
#
# RF-channel simulator. Spins up a `net-sim` container that emulates a
# real shared radio channel (modem framing, FM capture, per-link path
# loss) and wires linbpq + XRouter containers at it over KISS-over-TCP
# instead of AX.25-over-UDP partner links. DAPPS daemons sit on top
# unchanged.
#
# This is the heaviest sibling of the existing simulators:
#
#   - sim-multihop.sh        - dapps-on-loopback, UDP multicast as the RF
#                              stand-in. Fast, no Docker.
#   - sim-mixed-bearer.sh    - real BPQ + XRouter wired together over
#                              AXUDP point-to-point partner links. The
#                              packet-node layer is real but the RF
#                              channel is faked away.
#   - sim-rf-channel.sh (you - real BPQ + XRouter wired into a real
#     are here)                modem-and-channel simulation. The BPQ
#                              port is `TYPE=ASYNC PROTOCOL=KISS` over
#                              TCP, and net-sim handles framing,
#                              capture-effect, collisions, path loss.
#
# Two scenarios, selected via `SIM_SCENARIO=mesh|chain` (default mesh):
#
# ┌─ mesh ────────────────────────────────────────────────────────────┐
# │ 4 nodes on a single VHF (afsk1200) channel. A is the hub:         │
# │                                                                    │
# │              A (BPQ, hub)                                          │
# │           ╱  │  ╲                                                  │
# │       clean clean  no path                                         │
# │         ╱    │      ╲                                              │
# │        B     C ─clean─ D                                           │
# │       (XR)  (BPQ)    (XR)                                          │
# │        │     │                                                     │
# │        ╰─marginal (loss_db=6)                                      │
# │                                                                    │
# │ FM-capture mixer means concurrent transmissions don't both win;    │
# │ the 6 dB B↔C link is a hidden-terminal pair (audible to A but      │
# │ marginal to each other). Path A→D is two hops via C; B→D is        │
# │ three hops (B→A→C→D). Same DAPPS calls + route-hint shape as       │
# │ sim-mixed-bearer.sh, so the two scripts compare cleanly.           │
# └────────────────────────────────────────────────────────────────────┘
#
# ┌─ chain ───────────────────────────────────────────────────────────┐
# │ 3 BPQ packet-nodes back to back. Each has TWO radio ports. DAPPS  │
# │ runs only on the end nodes; the middle is a plain BPQ that just   │
# │ relays prompt-level connects:                                      │
# │                                                                    │
# │  DAPPS-A          (plain BPQ)         DAPPS-C                      │
# │   on BPQ-A        BPQ-M               on BPQ-C                     │
# │   port1 vhf ────  port1 vhf                                        │
# │   port2 spare     port2 uhf  ──────── port1 uhf                    │
# │                                       port2 spare                  │
# │                                                                    │
# │ The "spare" port on each end-node has its own kiss_port in         │
# │ net-sim but no peer link, so BPQ binds it but it carries no        │
# │ traffic. Different bands per backbone hop (VHF + UHF) reflects     │
# │ how a real packet backbone is laid out.                            │
# │                                                                    │
# │ DAPPS-A and DAPPS-C cannot reach each other directly: the only     │
# │ path is operator-style "connect, then connect, then DAPPS" through │
# │ M's prompt. That's exactly what a connect-script automates (see    │
# │ docs/multi-hop.md). So each end's neighbour row points at the      │
# │ middle BPQ's NODECALL with a script attached, and a route-hint     │
# │ binds the far-end DAPPS callsign to that neighbour.                │
# │                                                                    │
# │ Forward path A → C:                                                │
# │   1. DAPPS-A AGW connect to G0CHM-1 over BPQ-A's KISS-VHF port.    │
# │   2. BPQ-M accepts the L2 connect, presents its node prompt.       │
# │   3. Script sends "C G0CHC-1\r"; M dials BPQ-C over UHF.           │
# │   4. BPQ-C accepts, replies "Connected to G0CHC-1".                │
# │   5. Script sends "DAPPS\r"; BPQ-C dispatches to APPL1CALL=G0CDC-1 │
# │      (the local DAPPS daemon's registered AGW callsign).           │
# │   6. DAPPS-C answers with "DAPPSv1>"; the script terminates and    │
# │      the regular ihave/data/ack exchange runs over the chain.      │
# │                                                                    │
# │ Reverse path C → A is the mirror. Connect-scripts are one-sided    │
# │ so each end has its own.                                           │
# └────────────────────────────────────────────────────────────────────┘
#
# All containers (net-sim + every BPQ/XR) share net-sim's network
# namespace via --network=container:<netsim>. That puts all KISS-TCP
# listeners reachable as 127.0.0.1:<port> from inside any container,
# avoids per-container IP discovery, and works under both Docker
# Desktop and native Linux Docker (where --network=host fails on WSL2).
#
# Usage:
#   scripts/sim-rf-channel.sh up           # bring up net-sim + BPQs/XRs + DAPPS
#   scripts/sim-rf-channel.sh exercise     # canned send-set for the scenario
#   scripts/sim-rf-channel.sh status       # PIDs, ports, container health
#   scripts/sim-rf-channel.sh verify       # show every DAPPS node's inbox
#   scripts/sim-rf-channel.sh down         # tear everything down
#   scripts/sim-rf-channel.sh send X Y     # X -> Y one-shot
#
#   SIM_SCENARIO=chain scripts/sim-rf-channel.sh up
#
# Requires: dotnet 10 SDK, docker, curl, python3, host loopback, the
# BPQ + XRouter + net-sim images, and free TCP ports in the ranges
# the topology tables below select.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SIM_DIR="${SIM_DIR:-/tmp/dapps-rf-sim}"
BIN_DIR="$SIM_DIR/bin"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SIM_PWD="${SIM_PWD:-simpassw0rd}"
SIM_SCENARIO="${SIM_SCENARIO:-mesh}"

NETSIM_IMAGE="${NETSIM_IMAGE:-ghcr.io/packethacking/net-sim:main}"
BPQ_IMAGE="${BPQ_IMAGE:-m0lte/linbpq:latest}"
XR_IMAGE="${XR_IMAGE:-ghcr.io/packethacking/xrouter:latest}"

NETSIM_CTR="rfsim-netsim"
NETSIM_HTTP_PORT="${NETSIM_HTTP_PORT:-18080}"

# ── topology tables ───────────────────────────────────────────────────
case "$SIM_SCENARIO" in
mesh)
    NODES=(A B C D)
    DAPPS_NODES=(A B C D)
    declare -A KIND=(    [A]=bpq          [B]=xr           [C]=bpq          [D]=xr )
    declare -A CTR=(     [A]=rfsim-bpq-a  [B]=rfsim-xr-b   [C]=rfsim-bpq-c  [D]=rfsim-xr-d )
    declare -A AGW_PORT=( [A]=28001 [B]=28002 [C]=28003 [D]=28004 )
    declare -A RHP_PORT=( [B]=29002 [D]=29004 )
    declare -A TELNET_PORT=( [A]=28011 [C]=28013 )
    # one radio port per node, one kiss_port on net-sim each.
    declare -A KISS_PORT=( [A]=18001 [B]=18002 [C]=18003 [D]=18004 )
    declare -A NODECALL=( [A]=G0NDA-1 [B]=G0NDB-1 [C]=G0NDC-1 [D]=G0NDD-1 )
    declare -A NODEALIAS=( [A]=NDA [B]=NDB [C]=NDC [D]=NDD )
    declare -A DAPPS_CALL=( [A]=G0DPA-1 [B]=G0DPB-1 [C]=G0DPC-1 [D]=G0DPD-1 )
    declare -A HTTP_PORT=( [A]=17001 [B]=17002 [C]=17003 [D]=17004 )
    declare -A MQTT_PORT=( [A]=11881 [B]=11882 [C]=11883 [D]=11884 )

    # Direct DAPPS neighbours (= direct RF reachability with adequate margin).
    declare -A NEIGHBOURS=(
        [A]="B C"
        [B]="A"
        [C]="A D"
        [D]="C"
    )

    # Static route hints - same shape as sim-mixed-bearer.sh so the
    # routing layer behaves identically; what's new is what's underneath
    # (real KISS framing on a real-ish RF channel).
    declare -A ROUTE_HINTS=(
        [A]="D:C"
        [B]="C:A D:A"
        [C]="B:A"
        [D]="A:C B:C"
    )
    ;;
chain)
    # Three BPQ containers; DAPPS only on the ends. M is a plain BPQ
    # that the connect-scripts steer through.
    NODES=(A M C)
    DAPPS_NODES=(A C)
    declare -A KIND=(    [A]=bpq          [M]=bpq          [C]=bpq )
    declare -A CTR=(     [A]=rfsim-bpq-a  [M]=rfsim-bpq-m  [C]=rfsim-bpq-c )
    declare -A AGW_PORT=( [A]=28101 [M]=28102 [C]=28103 )
    declare -A TELNET_PORT=( [A]=28111 [M]=28112 [C]=28113 )

    # Two radio ports per BPQ. Port 1 (KISS_PORT_1) is the live
    # backbone interface; port 2 (KISS_PORT_2) is the spare. Each gets
    # its own kiss_port on net-sim.
    declare -A KISS_PORT_1=( [A]=18101 [M]=18102 [C]=18103 )
    declare -A KISS_PORT_2=( [A]=18201 [M]=18202 [C]=18203 )

    declare -A NODECALL=( [A]=G0CHA-1 [M]=G0CHM-1 [C]=G0CHC-1 )
    declare -A NODEALIAS=( [A]=CHA [M]=CHM [C]=CHC )
    # DAPPS callsigns only for the ends.
    declare -A DAPPS_CALL=( [A]=G0CDA-1 [C]=G0CDC-1 )
    declare -A HTTP_PORT=(  [A]=17101 [C]=17103 )
    declare -A MQTT_PORT=(  [A]=11891 [C]=11893 )
    ;;
*)
    echo "Unknown SIM_SCENARIO: $SIM_SCENARIO (expected mesh or chain)" >&2
    exit 1
    ;;
esac

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

# ── net-sim YAML rendering ────────────────────────────────────────────
render_netsim_mesh() {
    # net-sim picks a TNC backend per port via the `tnc:` field
    # (samoyed by default, direwolf opt-in). Both speak the same
    # modem flags; they differ in how the router pumps TX audio
    # (samoyed → UDP, direwolf → ALSA file-plugin via FIFO). We
    # drop direwolf on the two XR-side ports so each scenario
    # exercises both backends and we don't have a single-engine
    # blast radius (a samoyed crash, like the IDINTERVAL=0
    # workaround addresses, only takes the samoyed-driven ports
    # with it).
    cat <<EOF
mixer_mode: fm_capture
capture_db: 6.0
collision_mode: silence

nodes:
  - id: a
    ports:
      - { id: vhf, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT[A]} }
  - id: b
    ports:
      - { id: vhf, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT[B]}, tnc: direwolf }
  - id: c
    ports:
      - { id: vhf, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT[C]} }
  - id: d
    ports:
      - { id: vhf, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT[D]}, tnc: direwolf }

links:
  # A is the hub - clean to B and C, can't hear D at all.
  - { from: a.vhf, to: b.vhf, loss_db: 0 }
  - { from: b.vhf, to: a.vhf, loss_db: 0 }
  - { from: a.vhf, to: c.vhf, loss_db: 0 }
  - { from: c.vhf, to: a.vhf, loss_db: 0 }
  # B↔C is the marginal hidden-terminal pair.
  - { from: b.vhf, to: c.vhf, loss_db: 6 }
  - { from: c.vhf, to: b.vhf, loss_db: 6 }
  # D hangs off C only (no A↔D, no B↔D path).
  - { from: c.vhf, to: d.vhf, loss_db: 0 }
  - { from: d.vhf, to: c.vhf, loss_db: 0 }
EOF
}

render_netsim_chain() {
    cat <<EOF
# Chain backbone. Each BPQ has two radio ports; only the live ones are
# linked. The "spare" ports get a kiss_port so BPQ has something to
# bind to, but no peer link in net-sim - they sit idle.
mixer_mode: fm_capture
capture_db: 6.0
collision_mode: silence

nodes:
  - id: a
    ports:
      - { id: vhf,   modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT_1[A]}, tnc: direwolf }
      - { id: spare, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT_2[A]} }
  - id: m
    ports:
      - { id: vhf,   modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT_1[M]} }
      - { id: uhf,   modem: { mode: gfsk9600 }, kiss_port: ${KISS_PORT_2[M]} }
  - id: c
    ports:
      - { id: uhf,   modem: { mode: gfsk9600 }, kiss_port: ${KISS_PORT_1[C]}, tnc: direwolf }
      - { id: spare, modem: { mode: afsk1200 }, kiss_port: ${KISS_PORT_2[C]} }

links:
  # VHF backbone hop A ↔ M.
  - { from: a.vhf, to: m.vhf, loss_db: 0 }
  - { from: m.vhf, to: a.vhf, loss_db: 0 }
  # UHF backbone hop M ↔ C.
  - { from: m.uhf, to: c.uhf, loss_db: 0 }
  - { from: c.uhf, to: m.uhf, loss_db: 0 }
EOF
}

render_netsim_yaml() {
    case "$SIM_SCENARIO" in
        mesh)  render_netsim_mesh ;;
        chain) render_netsim_chain ;;
    esac
}

# ── net-sim container ────────────────────────────────────────────────
netsim_dir() { echo "$SIM_DIR/netsim"; }

publish_kiss_ports() {
    # Echo the host-published port flags net-sim's container needs.
    # Every BPQ/XR container shares net-sim's netns, so any port any
    # container in the topology will bind has to be published here on
    # net-sim itself. That includes:
    #   - net-sim's own kiss_ports + HTTP API
    #   - every BPQ/XR's AGW + Telnet + RHPv2 listeners
    case "$SIM_SCENARIO" in
    mesh)
        for n in "${NODES[@]}"; do
            printf -- " -p 127.0.0.1:%s:%s" "${KISS_PORT[$n]}" "${KISS_PORT[$n]}"
            printf -- " -p 127.0.0.1:%s:%s" "${AGW_PORT[$n]}" "${AGW_PORT[$n]}"
        done
        for n in "${!TELNET_PORT[@]}"; do
            printf -- " -p 127.0.0.1:%s:%s" "${TELNET_PORT[$n]}" "${TELNET_PORT[$n]}"
        done
        for n in "${!RHP_PORT[@]}"; do
            printf -- " -p 127.0.0.1:%s:%s" "${RHP_PORT[$n]}" "${RHP_PORT[$n]}"
        done
        ;;
    chain)
        for n in "${NODES[@]}"; do
            printf -- " -p 127.0.0.1:%s:%s" "${KISS_PORT_1[$n]}" "${KISS_PORT_1[$n]}"
            printf -- " -p 127.0.0.1:%s:%s" "${KISS_PORT_2[$n]}" "${KISS_PORT_2[$n]}"
            printf -- " -p 127.0.0.1:%s:%s" "${AGW_PORT[$n]}" "${AGW_PORT[$n]}"
            printf -- " -p 127.0.0.1:%s:%s" "${TELNET_PORT[$n]}" "${TELNET_PORT[$n]}"
        done
        ;;
    esac
    printf -- " -p 127.0.0.1:%s:8080" "$NETSIM_HTTP_PORT"
}

start_netsim() {
    local d; d="$(netsim_dir)"
    mkdir -p "$d"
    render_netsim_yaml > "$d/network.yaml"

    # shellcheck disable=SC2046
    set -- $(publish_kiss_ports)
    echo ">>> Starting net-sim ($NETSIM_CTR) with scenario=$SIM_SCENARIO"
    docker run -d --name "$NETSIM_CTR" "$@" \
        -v "$d/network.yaml:/etc/sim/network.yaml:ro" \
        "$NETSIM_IMAGE" >/dev/null
}

wait_for_netsim() {
    # 30 seconds for the HTTP API + every kiss_port to bind.
    for _ in $(seq 1 60); do
        if curl -fs "http://127.0.0.1:$NETSIM_HTTP_PORT/healthz" -o /dev/null 2>/dev/null; then
            break
        fi
        sleep 0.5
    done
    local ports=()
    case "$SIM_SCENARIO" in
        mesh)
            for n in "${NODES[@]}"; do ports+=("${KISS_PORT[$n]}"); done ;;
        chain)
            for n in "${NODES[@]}"; do
                ports+=("${KISS_PORT_1[$n]}" "${KISS_PORT_2[$n]}")
            done ;;
    esac
    for p in "${ports[@]}"; do
        for _ in $(seq 1 60); do
            if (echo > "/dev/tcp/127.0.0.1/$p") 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
    done
}

stop_netsim() {
    docker rm -f "$NETSIM_CTR" >/dev/null 2>&1 || true
}

# ── BPQ config rendering ──────────────────────────────────────────────
# KISS-over-TCP port block. linbpq has two drivers that look like they
# fit (both expose KISS over TCP): KISSHF and TCPKISS. The difference
# matters: KISSHF treats the port as a raw modem with no AX.25 stack
# wrapping, so `C 2 <call>` from the node prompt comes back with
# "Sorry, port is not an a.25 port" - useless for our chain demo.
# `TYPE=ASYNC PROTOCOL=KISS` selects the TCPKISS driver, which IS a
# real AX.25 carrier. The L2 parameters (MAXFRAME, FRACK, RESPTIME,
# RETRIES, PACLEN) are needed; without them BPQ logs "Driver
# installation failed" at startup.
render_bpq_kiss_port() {
    local kiss_port=$1 id=$2
    cat <<EOF
PORT
 ID=$id
 TYPE=ASYNC
 PROTOCOL=KISS
 IPADDR=127.0.0.1
 TCPPORT=$kiss_port
 QUALITY=255
 MAXFRAME=6
 FRACK=5000
 RESPTIME=1000
 RETRIES=10
 PACLEN=236
ENDPORT
EOF
}

render_bpq_telnet_port() {
    local telnet=$1 nodecall=$2
    cat <<EOF
PORT
 ID=Telnet
 DRIVER=Telnet
 CONFIG
 TCPPORT=$telnet
 MAXSESSIONS=20
 USER=test,test,$nodecall,,SYSOP
ENDPORT
EOF
}

render_bpq_mesh() {
    local n=$1
    local nodecall=${NODECALL[$n]} alias=${NODEALIAS[$n]}
    local agw=${AGW_PORT[$n]} kiss=${KISS_PORT[$n]}
    local dapps_call=${DAPPS_CALL[$n]}
    local telnet=${TELNET_PORT[$n]:-}
    local routes=""
    for nb in ${NEIGHBOURS[$n]}; do
        routes+="${NODECALL[$nb]},200,2"$'\n'
        routes+="${DAPPS_CALL[$nb]},200,2"$'\n'
    done
    local telnet_block=""
    if [ -n "$telnet" ]; then
        telnet_block=$(render_bpq_telnet_port "$telnet" "$nodecall")
    fi
    cat <<EOF
SIMPLE=1
NODECALL=$nodecall
NODEALIAS=$alias
LOCATOR=NONE
NODESINTERVAL=1
IDINTERVAL=0
AGWPORT=$agw
AGWSESSIONS=20
AGWMASK=1
APPLICATIONS=DAPPS
APPL1CALL=$dapps_call
APPL1ALIAS=DAPPS

$telnet_block

$(render_bpq_kiss_port "$kiss" "KISSTCP-net-sim")

ROUTES:
$routes***

EOF
}

render_bpq_chain() {
    local n=$1
    local nodecall=${NODECALL[$n]} alias=${NODEALIAS[$n]}
    local agw=${AGW_PORT[$n]} telnet=${TELNET_PORT[$n]}
    local kiss1=${KISS_PORT_1[$n]} kiss2=${KISS_PORT_2[$n]}
    # APPLICATIONS block only on end nodes. The middle BPQ runs no
    # DAPPS, so it has nothing to declare - it's a plain BPQ that
    # accepts inbound connects on its NODECALL and presents the
    # standard node prompt for the connect-script to drive.
    local app_block=""
    if [ -n "${DAPPS_CALL[$n]:-}" ]; then
        local dapps_call=${DAPPS_CALL[$n]}
        app_block=$(cat <<APP
APPLICATIONS=DAPPS
APPL1CALL=$dapps_call
APPL1ALIAS=DAPPS
APP
        )
    fi
    # Routes: seed each node's directly-adjacent BPQ. BPQ ROUTES
    # format is `CALLSIGN,QUALITY,PORT` - the third field is the
    # port number, not obscount, and IS required (without it BPQ
    # picks a default port that's wrong for multi-port nodes). On
    # the chain BPQs, port 2 = KISS-radio-1, port 3 = KISS-radio-2;
    # M's port 2 reaches A on VHF, port 3 reaches C on UHF.
    local routes=""
    case "$n" in
        A) routes+="${NODECALL[M]},200,2"$'\n' ;;
        M) routes+="${NODECALL[A]},200,2"$'\n'
           routes+="${NODECALL[C]},200,3"$'\n' ;;
        C) routes+="${NODECALL[M]},200,2"$'\n' ;;
    esac
    cat <<EOF
SIMPLE=1
NODECALL=$nodecall
NODEALIAS=$alias
LOCATOR=NONE
NODESINTERVAL=1
IDINTERVAL=0
AGWPORT=$agw
AGWSESSIONS=20
AGWMASK=1
$app_block

$(render_bpq_telnet_port "$telnet" "$nodecall")

$(render_bpq_kiss_port "$kiss1" "KISS-radio-1")
$(render_bpq_kiss_port "$kiss2" "KISS-radio-2")

ROUTES:
$routes***

EOF
}

render_bpq_config() {
    case "$SIM_SCENARIO" in
        mesh)  render_bpq_mesh "$1" ;;
        chain) render_bpq_chain "$1" ;;
    esac
}

# ── XRouter config rendering (mesh only) ──────────────────────────────
render_xr_config() {
    local n=$1
    local nodecall=${NODECALL[$n]} alias=${NODEALIAS[$n]}
    local agw=${AGW_PORT[$n]} kiss=${KISS_PORT[$n]} rhp=${RHP_PORT[$n]}
    local base=${nodecall%-*}
    local consolecall=$base
    local chatcall=$base-8
    cat <<EOF
DNS=8.8.8.8
NODECALL=$nodecall
NODEALIAS=$alias
LOCATOR=IO91PM
CONSOLECALL=$consolecall
CHATCALL=$chatcall
CHATALIAS=${alias:0:5}C
AGWPORT=$agw
RHPPORT=$rhp
IPADDRESS=44.128.0.1

INTERFACE=1
	TYPE=TCP
	ID=KISSTCP-net-sim
	IOADDR=127.0.0.1
	INTNUM=$kiss
	PROTOCOL=KISS
	KISSOPTIONS=NONE
	MTU=256
ENDINTERFACE

PORT=1
	ID="net-sim KISS"
	INTERFACENUM=1
ENDPORT
EOF
}

render_xr_access_sys() {
    cat <<'EOF'
127.0.0.0/8	1
44.0.0.0/8	1
192.168.0.0/24	1
0.0.0.0/0	7
EOF
}

# ── per-node container start ─────────────────────────────────────────
ctr_dir() { echo "$SIM_DIR/$1"; }

start_node_container() {
    local n=$1
    local kind=${KIND[$n]} ctr=${CTR[$n]}
    local d; d="$(ctr_dir "$n")"
    if [ -d "$d" ]; then
        docker run --rm -v "$d:/wipe" alpine sh -c \
            'rm -rf /wipe/* /wipe/.[!.]* 2>/dev/null' 2>/dev/null || true
    fi
    mkdir -p "$d"

    if [ "$kind" = "bpq" ]; then
        render_bpq_config "$n" > "$d/bpq32.cfg"
        echo ">>> Starting BPQ container $ctr (AGW :${AGW_PORT[$n]})"
        docker run -d --name "$ctr" \
            --network "container:$NETSIM_CTR" \
            -v "$d:/data" "$BPQ_IMAGE" >/dev/null
    elif [ "$kind" = "xr" ]; then
        render_xr_config "$n" > "$d/XROUTER.CFG"
        echo ">>> Starting XRouter container $ctr (AGW :${AGW_PORT[$n]} RHPv2 :${RHP_PORT[$n]})"
        docker run -d --name "$ctr" \
            --network "container:$NETSIM_CTR" \
            -v "$d:/data" "$XR_IMAGE" >/dev/null
        sleep 3
        render_xr_access_sys | docker cp - "$ctr:/data/ACCESS.SYS" 2>/dev/null \
            || echo "    (warning: ACCESS.SYS write failed; loopback should still work)"
        docker restart "$ctr" >/dev/null
    else
        echo "!!! unknown kind '$kind' for $n" >&2
        return 1
    fi
}

wait_for_agw() {
    local n=$1
    local port=${AGW_PORT[$n]}
    for _ in $(seq 1 180); do
        if (echo > "/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
            return 0
        fi
        sleep 0.5
    done
    echo "!!! AGW on $n did not come up on :$port" >&2
    docker ps -a --filter "name=${CTR[$n]}" >&2
    docker logs "${CTR[$n]}" 2>&1 | tail -40 >&2
    return 1
}

stop_node_container() {
    docker rm -f "${CTR[$1]}" >/dev/null 2>&1 || true
}

# ── DAPPS daemon launch ───────────────────────────────────────────────
dapps_dir() { echo "$SIM_DIR/dapps-$1"; }

start_dapps() {
    local n=$1
    local d; d="$(dapps_dir "$n")"
    mkdir -p "$d/data"
    rm -f "$d/data/dapps.db" "$d/dapps.log"

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

# ── DAPPS configuration via REST ──────────────────────────────────────
dapps_login() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"
    rm -f "$cookie"
    # First-run path: /Setup is a Razor page with named handlers, so
    # the password POST has to be ?handler=Password or it silently
    # no-ops. On a re-run where Setup is already complete, this
    # redirects to / harmlessly. Either way, /Login then signs us in
    # against the persisted password and writes the dapps.admin
    # cookie that the sysop endpoints (/Neighbours etc.) require.
    curl -sS -o /dev/null -X POST "$base/Setup?handler=Password" \
        --data-urlencode "Password=$SIM_PWD" \
        --data-urlencode "Confirm=$SIM_PWD" || true
    curl -fsS -o /dev/null -c "$cookie" -X POST "$base/Login" \
        --data-urlencode "Password=$SIM_PWD"
}

configure_dapps_mesh() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"
    local port_byte=1
    if [ "${KIND[$n]}" = "xr" ]; then port_byte=0; fi
    for nb in ${NEIGHBOURS[$n]}; do
        curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
            -H 'content-type: application/json' \
            -d "{\"Callsign\":\"${DAPPS_CALL[$nb]}\",\"BearerPort\":$port_byte,\"UdpEndpoint\":null}"
    done
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

# Chain scenario neighbour install: DAPPS-A's only neighbour is the
# middle BPQ (G0CHM-1), with a connect-script that drives the M→C hop
# and the application command on C. DAPPS-C's mirror points at G0CHM-1
# with the C→A direction. A route-hint at each end binds the far-end
# DAPPS callsign to the M-fronted neighbour row so outbound forwarding
# resolves "to G0CDC-1, next-hop is G0CHM-1, run the script".
configure_dapps_chain() {
    local n=$1
    local base="http://127.0.0.1:${HTTP_PORT[$n]}"
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"

    # The "far end" is the other DAPPS-running node in the chain.
    local far
    case "$n" in A) far=C ;; C) far=A ;; *) return 0 ;; esac

    local m_call="${NODECALL[M]}"
    local far_node_call="${NODECALL[$far]}"   # BPQ NODECALL on the far end
    local far_node_alias="${NODEALIAS[$far]}" # BPQ NODEALIAS on the far end
    local far_dapps_call="${DAPPS_CALL[$far]}"

    # Connect script: run from M's prompt onwards. Step 1 connects from
    # M's prompt to the far BPQ; step 2 invokes DAPPS at the far end.
    # The "C <call>" command targets the BPQ NODECALL. BPQ's reply for
    # a successful connect is `Connected to <ALIAS>:<CALL>` (e.g.
    # "Connected to CHC:G0CHC-1"), so the expect substring matches on
    # the alias - more reliable than matching on the SSID-bearing
    # callsign which BPQ might or might not include.
    local script
    script=$(printf 'C %s|Connected to %s\nDAPPS|DAPPSv1>|60' \
        "$far_node_call" "$far_node_alias")

    # JSON-encode the multi-line script via python so embedded \n + |
    # round-trip cleanly.
    local body
    body=$(python3 -c "
import json, sys
print(json.dumps({
    'Callsign': sys.argv[1],
    'BearerPort': 1,
    'UdpEndpoint': None,
    'ConnectScript': sys.argv[2],
}))" "$m_call" "$script")

    curl -fsS -o /dev/null -b "$cookie" -X POST "$base/Neighbours" \
        -H 'content-type: application/json' -d "$body"

    # Route hint: dest base callsign of far DAPPS -> next-hop is the
    # M-fronted neighbour we just created.
    local db; db="$(dapps_dir "$n")/data/dapps.db"
    python3 -c "
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('insert or replace into routehints (Destination, NextHop) values (?, ?)', (sys.argv[2], sys.argv[3]))
con.commit()
" "$db" "${far_dapps_call%-*}" "$m_call"
}

configure_dapps() {
    local n=$1
    dapps_login "$n"
    case "$SIM_SCENARIO" in
        mesh)  configure_dapps_mesh "$n" ;;
        chain) configure_dapps_chain "$n" ;;
    esac
}

# ── exercise / send helpers ──────────────────────────────────────────
trigger_run() {
    local n=$1
    local cookie; cookie="$(dapps_dir "$n")/cookie.txt"
    curl -fsS -o /dev/null -b "$cookie" -X POST \
        "http://127.0.0.1:${HTTP_PORT[$n]}/Message/dorun" || true
}

drain_queues() {
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        for n in "${DAPPS_NODES[@]}"; do trigger_run "$n" & done
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

cmd_exercise_mesh() {
    echo "=== RF-channel mesh exercise ==="
    echo ""
    echo "BPQ-side originated (A is the hub):"
    cmd_send A B "1-hop-BPQ-to-XR"
    cmd_send A C "1-hop-BPQ-to-BPQ"
    cmd_send A D "2-hop-via-C-no-direct-AD-RF-path"
    echo ""
    echo "XR-side originated (RHPv2 bearer):"
    cmd_send B C "2-hop-XR-via-A"
    cmd_send B D "3-hop-XR-A-C-XR-mixed"
    echo ""
    echo "=== Final inboxes ==="
    cmd_verify
}

cmd_exercise_chain() {
    echo "=== RF-channel chain exercise ==="
    echo ""
    echo "End-to-end via the connect-script through plain BPQ M:"
    cmd_send A C "A-to-C-via-M-connect-script"
    cmd_send C A "C-to-A-via-M-connect-script-reverse"
    echo ""
    echo "=== Final inboxes ==="
    cmd_verify
}

cmd_exercise() {
    case "$SIM_SCENARIO" in
        mesh)  cmd_exercise_mesh ;;
        chain) cmd_exercise_chain ;;
    esac
}

cmd_verify() {
    for n in "${DAPPS_NODES[@]}"; do show_inbox "$n"; done
}

cmd_status() {
    echo "=== net-sim ==="
    docker ps --filter "name=$NETSIM_CTR" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
    echo ""
    echo "=== Packet-node containers ==="
    docker ps --filter name=rfsim-bpq- --filter name=rfsim-xr- \
        --format 'table {{.Names}}\t{{.Status}}'
    echo ""
    echo "=== DAPPS daemons (scenario=$SIM_SCENARIO) ==="
    for n in "${DAPPS_NODES[@]}"; do
        local pid_file; pid_file="$(dapps_dir "$n")/pid"
        local state="(no pid)"
        if [ -f "$pid_file" ]; then
            local pid; pid=$(cat "$pid_file")
            if kill -0 "$pid" 2>/dev/null; then
                state="running pid=$pid"
            else
                state="(dead)"
            fi
        fi
        printf "  %-2s  %-10s  %s  http=:%s  agw=:%s\n" \
            "$n" "${DAPPS_CALL[$n]}" "$state" "${HTTP_PORT[$n]}" "${AGW_PORT[$n]}"
    done
}

# ── top-level ────────────────────────────────────────────────────────
cmd_up() {
    maybe_publish
    echo "=== Phase 1: net-sim (RF channel) ==="
    start_netsim
    wait_for_netsim
    echo "=== Phase 2: BPQ / XRouter containers ==="
    for n in "${NODES[@]}"; do start_node_container "$n"; done
    for n in "${NODES[@]}"; do wait_for_agw "$n"; done
    # Let initial NODES gossip exchange a couple of cycles - chain
    # routing in particular needs M to have heard from both ends so
    # `C G0CH?-1` from M's prompt resolves to the right port.
    echo "=== Phase 3: NODES gossip warm-up (90s) ==="
    sleep 90
    echo "=== Phase 4: DAPPS daemons ==="
    for n in "${DAPPS_NODES[@]}"; do start_dapps "$n"; done
    echo "=== Phase 5: DAPPS configuration ==="
    for n in "${DAPPS_NODES[@]}"; do configure_dapps "$n"; done
    echo "=== Up. Run 'scripts/sim-rf-channel.sh exercise' or 'send X Y'. ==="
}

cmd_down() {
    for n in "${DAPPS_NODES[@]}"; do stop_dapps "$n"; done
    for n in "${NODES[@]}"; do stop_node_container "$n"; done
    stop_netsim
    echo "=== Down ==="
}

case "${1:-}" in
    up)       cmd_up ;;
    down)     cmd_down ;;
    status)   cmd_status ;;
    verify)   cmd_verify ;;
    exercise) cmd_exercise ;;
    send)     cmd_send "$2" "$3" "${4:-}" ;;
    "")       echo "Usage: $0 {up|down|status|verify|exercise|send X Y}  (SIM_SCENARIO=mesh|chain)"; exit 1 ;;
    *)        echo "Unknown command: $1"; exit 1 ;;
esac
