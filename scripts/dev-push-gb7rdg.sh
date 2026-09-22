#!/usr/bin/env bash
#
# Local cross-build → gb7rdg-node deploy. Avoids the GitHub Actions
# round-trip during dev iteration: ~1 min cycle vs ~5 min for tag → CI →
# release → curl. Don't use for actual releases - use the master-push
# version-bump path for those.
#
# Usage: scripts/dev-push-gb7rdg.sh
#
# Requires:
#   - dotnet 10.0 SDK locally (any host arch - we cross-compile to ARM)
#   - ssh + scp configured for tf@gb7rdg-node
#   - sudo NOPASSWD on tf@gb7rdg-node for /opt/dapps/dapps and
#     systemctl restart dapps.service (or be ready to type the password)
#
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${DEV_HOST:-tf@gb7rdg-node}"
RID="${DEV_RID:-linux-arm}"
PROJ="$REPO/src/dapps/dapps.core/dapps.core.csproj"
SHA="$(git -C "$REPO" rev-parse --short=8 HEAD)"
DIRTY="$(git -C "$REPO" status --porcelain | head -1)"
TAG="$SHA${DIRTY:+-dirty}"

echo ">>> Building $RID  ($TAG)"
# Stamp the build with the git short SHA + dirty marker, so the
# dashboard's footer / startup log says exactly what's running. Doesn't
# touch <Version> - that drives release tagging, not dev pushes.
rm -rf "$REPO/publish-dev"
dotnet publish "$PROJ" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded \
    -p:InformationalVersion="dev-$TAG" \
    --output "$REPO/publish-dev" \
    --nologo --verbosity quiet

LOCAL_BIN="$REPO/publish-dev/dapps.core"
test -s "$LOCAL_BIN"
SIZE=$(stat -c%s "$LOCAL_BIN" 2>/dev/null || stat -f%z "$LOCAL_BIN")
echo ">>> Built $(numfmt --to=iec-i --suffix=B "$SIZE" 2>/dev/null || echo "${SIZE}B")"

echo ">>> Uploading → $HOST:/tmp/dapps.new"
scp -q "$LOCAL_BIN" "$HOST:/tmp/dapps.new"

echo ">>> Installing + restarting on $HOST"
ssh "$HOST" "sudo install -o root -g root -m 755 /tmp/dapps.new /opt/dapps/dapps \
    && rm /tmp/dapps.new \
    && sudo systemctl restart dapps.service \
    && sleep 3 \
    && sudo systemctl is-active dapps.service"

echo ">>> Tail (Ctrl+C to exit)"
ssh "$HOST" "sudo journalctl -u dapps.service -f --no-pager -n 20"
