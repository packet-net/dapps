#!/bin/sh
# run.sh dup|badnr: one LinBPQ (the DAPPS test pin, or $IMG) against repro.py over KISS TCP.
# Needs docker and host networking (KISS on 127.0.0.1:18101). Prints the frame transcript and LinBPQ's debug log.
here=$(cd "$(dirname "$0")" && pwd)
img=${IMG:-m0lte/linbpq@sha256:6db237b9466320830ce74151d7737f6851d4fe5c2c0162136dd9f5da9dff1588}
run=$(mktemp -d /tmp/linbpq-repro-XXXXXX)
mkdir -p "$run/logs" && cp "$here/bpq32.cfg" "$run/"
python3 -I "$here/repro.py" "$1" > "$run/$1.txt" 2>&1 &
py=$!
sleep 1
name=srej-repro-$1-$$
if ! docker run -d --rm --name $name --network host -v "$run:/data" "$img" >/dev/null; then kill $py; exit 1; fi
wait $py
docker stop $name >/dev/null 2>&1
cat "$run/$1.txt"
echo "--- LinBPQ debug log"
cat "$run"/logs/NodeDebuglog_* 2>/dev/null | tr -d '\r' | grep -v "^$" | grep -v "Resolving\|semaphore\|PORTS 0x\|UIUtil\|Monitor Thread\|CTEXT Read" | tail -30
