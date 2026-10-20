#!/usr/bin/env bash
#
# Pre-seeded SQLite + dotnet watch run for sub-second dashboard
# iteration. Drops a baked admin password, callsign, and a handful
# of sample neighbours / discovery channels / discovered peers so
# /Setup redirects to / on first hit. Edit any .cshtml or .css and
# dotnet watch hot-reloads in the browser.
#
# Usage:    scripts/dev-ui.sh
# Login:    sysop / devpass        (override DAPPS_DEV_PASSWORD)
# Reseed:   rm src/dapps/dapps.core/data/dapps.db && scripts/dev-ui.sh
#
# Why the project dir as cwd (and not a temp dir): ASP.NET Core's
# default ContentRoot is the cwd, and Pages/wwwroot need to resolve
# from there. DbInfo.GetPath() prefers data/dapps.db when data/ exists,
# so we get an isolated dev DB without touching the legacy dapps.db
# in the project root.
#
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PROJ_DIR="$REPO/src/dapps/dapps.core"
DB_DIR="$PROJ_DIR/data"
DB="$DB_DIR/dapps.db"
URL="${DAPPS_DEV_URL:-http://localhost:5086}"
PASSWORD="${DAPPS_DEV_PASSWORD:-devpass}"
CALLSIGN="${DAPPS_DEV_CALLSIGN:-TEST-7}"
SEED_LOG=/tmp/dapps-dev-seed.log

mkdir -p "$DB_DIR"

needs_seed() {
    [[ ! -s "$DB" ]] && return 0
    local cs
    cs=$(sqlite3 "$DB" \
        "select value from systemoptions where option='Callsign'" \
        2>/dev/null || true)
    [[ -z "$cs" || "$cs" == "N0CALL" ]]
}

create_schema_via_dotnet() {
    # No clean side-door for "create schema and exit" - the --version /
    # --show-config CLI shortcuts return before DbStartup runs. So we
    # boot the daemon, wait until HTTP answers (proves DbStartup
    # finished and the host is up), and kill it. SQLite is crash-safe
    # via its journal; the schema is fully written by the time the
    # listener accepts.
    echo ">>> Bootstrapping schema (one-shot dotnet run, ~10s)"
    cd "$PROJ_DIR"
    : > "$SEED_LOG"
    dotnet run --no-launch-profile --urls "$URL" >>"$SEED_LOG" 2>&1 &
    local pid=$!
    # shellcheck disable=SC2064
    trap "kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true" EXIT INT TERM

    local i
    for i in $(seq 1 240); do
        if curl --silent --fail --max-time 1 "$URL/Login" >/dev/null 2>&1; then
            break
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "dotnet exited during seed; see $SEED_LOG" >&2
            exit 1
        fi
        sleep 0.5
    done

    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    trap - EXIT INT TERM

    [[ -s "$DB" ]] || { echo "schema bootstrap produced no DB; see $SEED_LOG" >&2; exit 1; }
}

seed_data() {
    echo ">>> Seeding admin password ($PASSWORD), callsign $CALLSIGN, sample peers"

    # PBKDF2-HMAC-SHA256, 16-byte salt, 32-byte hash, 100k iterations,
    # uppercase hex - mirrors AdminPasswordStore.Pbkdf2 / Convert.ToHexString.
    read -r SALT_HEX HASH_HEX < <(python3 - "$PASSWORD" <<'PY'
import hashlib, os, sys
pwd = sys.argv[1].encode('utf-8')
salt = os.urandom(16)
h = hashlib.pbkdf2_hmac('sha256', pwd, salt, 100_000, 32)
print(salt.hex().upper(), h.hex().upper())
PY
)

    # DbDiscoveredPeer.LastSeen is a .NET DateTime which sqlite-net-pcl
    # stores as ticks-since-0001-01-01. Compute one "now" tick value and
    # offset the sample rows from it so the dashboard's freshness logic
    # doesn't mark them all stale.
    NOW_TICKS=$(python3 -c "from datetime import datetime; print(int((datetime.utcnow() - datetime(1,1,1)).total_seconds() * 10_000_000))")
    FIVE_MIN=3000000000
    THIRTY_MIN=18000000000
    TWO_MIN=1200000000

    sqlite3 "$DB" <<SQL
begin;
insert or replace into systemoptions(option, value) values
    ('Callsign',           '$CALLSIGN'),
    ('AdminPasswordHash',  '$HASH_HEX'),
    ('AdminPasswordSalt',  '$SALT_HEX'),
    ('NodeHost',           'localhost'),
    ('NodeBearer',         'agw'),
    ('AgwPort',            '8000'),
    ('DefaultPort',        '1'),
    ('PortNumbering',      'node');

insert or ignore into neighbours(Callsign, BearerPort, UdpEndpoint) values
    ('M0LTE-1', 1, NULL),
    ('GB7RDG',  1, NULL),
    ('G7VVK-9', 2, NULL);

insert or ignore into discoverychannels
    (Bearer, ChannelKey, LinkClass, BeaconIntervalSeconds, AdvertisedTtlSeconds, CostHint, Enabled, Notes, AirtimeBudgetSecondsPerHour, SolicitIntervalSeconds)
    values
    ('agw', '1',                   1, 600, 3600,  10, 1, 'VHF 144.800 (sample)',     0, 0),
    ('udp', '239.42.42.42:1881',   4, 300, 1800, 100, 1, 'LAN multicast (sample)',   0, 0);

insert or ignore into discoveredpeers
    (PeerKey, Callsign, Bearer, ChannelKey, ChannelId, LinkClass, CostHint, Hops, TtlSeconds, BearerPort, UdpEndpoint, LastSeen)
    values
    ('M0LTE|agw|1',                  'M0LTE',   'agw', '1',                 1, 1,  10, 1, 3600, 1,    NULL,                $((NOW_TICKS - FIVE_MIN))),
    ('G7VVK-9|agw|1',                'G7VVK-9', 'agw', '1',                 1, 1,  10, 2, 3600, 1,    NULL,                $((NOW_TICKS - THIRTY_MIN))),
    ('GB7RDG|udp|239.42.42.42:1881', 'GB7RDG',  'udp', '239.42.42.42:1881', 2, 4, 100, 1, 1800, NULL, '239.42.42.42:1881', $((NOW_TICKS - TWO_MIN)));
commit;
SQL
}

if needs_seed; then
    create_schema_via_dotnet
    seed_data
    echo ">>> Seeded $DB"
else
    echo ">>> Reusing $DB (delete it to reseed)"
fi

cd "$PROJ_DIR"
echo ">>> dotnet watch run  →  $URL  (login: sysop / $PASSWORD)"
exec dotnet watch run --no-launch-profile --urls "$URL"
