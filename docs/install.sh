#!/usr/bin/env bash
#
# DAPPS one-liner installer.
#
#   curl -sSL https://packet-net.github.io/dapps/install.sh | sudo bash
#
# Detects arch, downloads the latest binary from the GitHub Release,
# creates a system user, drops the systemd units, enables and starts.
# Configuration (callsign, bearer, ports, ...) lives in the dashboard's
# /Setup wizard - this script sets no env vars and leaves no callsign-
# specific state behind.
#
# Idempotent: re-running upgrades the binary in place. DAPPS does not
# update itself; to upgrade, re-run this script (or install from the apt
# repository, which is the recommended path on Debian-family systems).
#
# Linux + systemd only. macOS, Windows, and non-systemd Linux distros
# can install manually - see https://packet-net.github.io/dapps/install/

set -euo pipefail

# ── config ─────────────────────────────────────────────────────────────
PREFIX="${PREFIX:-/opt/dapps}"
STATE_DIR="${STATE_DIR:-/var/lib/dapps}"
SERVICE_USER="${SERVICE_USER:-dapps}"
HTTP_BIND="${HTTP_BIND:-0.0.0.0:5000}"
RELEASE_BASE="https://github.com/packet-net/dapps/releases/latest/download"

# ── helpers ────────────────────────────────────────────────────────────
say()  { printf '\033[1;36m::\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m!!\033[0m %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ ${EUID:-$(id -u)} -eq 0 ]] || die "This installer needs root. Re-run with sudo, e.g. 'curl -sSL https://packet-net.github.io/dapps/install.sh | sudo bash'."
}

require_systemd() {
    command -v systemctl >/dev/null 2>&1 || die "systemd not found. This installer is Linux+systemd only. See https://packet-net.github.io/dapps/install/ for manual install on other platforms."
    [[ -d /run/systemd/system ]] || die "systemd not running. Are you in a container or chroot?"
}

detect_rid() {
    local arch
    arch=$(uname -m)
    case "$arch" in
        x86_64|amd64) echo "linux-x64" ;;
        aarch64|arm64)
            # 32-bit Raspberry Pi OS on a Pi 4/5 ships a 64-bit kernel
            # with a 32-bit (armhf) userland. uname -m reports aarch64
            # because that comes from the kernel, but the userland's
            # dynamic linker lives at /lib/ld-linux-armhf.so.3 and the
            # arm64 binary's interpreter (/lib/ld-linux-aarch64.so.1)
            # is absent. Cross-check the userland bitness via getconf
            # and downgrade to linux-arm if the userland is 32-bit.
            local userbits
            userbits=$(getconf LONG_BIT 2>/dev/null || echo 64)
            if [[ "$userbits" == "32" ]]; then
                echo "linux-arm"
            else
                echo "linux-arm64"
            fi
            ;;
        armv7l|armv7|armhf) echo "linux-arm" ;;
        *) die "Unsupported architecture: $arch. Build from source or open an issue at https://github.com/packet-net/dapps/issues." ;;
    esac
}

# Belt-and-braces: even with a correct RID, an exotic distro could
# lack the dynamic linker the binary asks for (a stripped container
# image, a custom musl-only build, a misconfigured multilib etc.).
# Check up-front and fail with an actionable message rather than
# letting systemd's "203/EXEC: No such file or directory" be the
# operator's first signal.
verify_interpreter() {
    local rid="$1"
    local interp
    case "$rid" in
        linux-x64)   interp="/lib64/ld-linux-x86-64.so.2" ;;
        linux-arm64) interp="/lib/ld-linux-aarch64.so.1" ;;
        linux-arm)   interp="/lib/ld-linux-armhf.so.3" ;;
        *) return 0 ;;  # unknown RID skips the check; download will fail anyway
    esac
    if [[ ! -e "$interp" ]]; then
        die "The $rid binary needs $interp at runtime but it isn't installed.
This usually means a 32-bit userland is running on a 64-bit kernel
(common on Raspberry Pi OS 32-bit) or a slimmed-down container image.
Cross-check with: arch=\$(uname -m); bits=\$(getconf LONG_BIT)
If you're on a Pi and want the 64-bit binary, reinstall on 64-bit
Raspberry Pi OS. Otherwise install the matching libc package."
    fi
}

ensure_user() {
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then
        say "User $SERVICE_USER already exists; reusing."
    else
        say "Creating system user $SERVICE_USER (home=$STATE_DIR, no shell)."
        useradd --system --home "$STATE_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
}

ensure_dirs() {
    mkdir -p "$PREFIX" "$STATE_DIR"
    chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_DIR"
}

download_binary() {
    local rid="$1"
    local url="$RELEASE_BASE/dapps-$rid"
    local target="$PREFIX/dapps"

    say "Downloading $url"
    if [[ -x "$target" ]]; then
        # Re-running: keep a sidecar copy of the previous binary so an
        # operator who realises the new release doesn't work for them
        # has a one-step rollback to the predecessor.
        cp -f "$target" "$target.previous"
    fi

    # curl with -fsSL: fail on 4xx/5xx, silent, follow redirects.
    if ! curl -fsSL "$url" -o "$target.tmp"; then
        die "Download failed. Check network connectivity and the release page at https://github.com/packet-net/dapps/releases/latest"
    fi

    chmod +x "$target.tmp"
    mv "$target.tmp" "$target"
}

write_unit() {
    local target="/etc/systemd/system/dapps.service"
    say "Writing $target"
    cat > "$target" <<EOF
[Unit]
Description=DAPPS daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$STATE_DIR
ExecStart=$PREFIX/dapps
Environment=ASPNETCORE_URLS=http://$HTTP_BIND
Restart=on-failure
RestartSec=5s
# Exit code 78 = fatal config error; don't restart in a tight loop,
# leave it down so the journal message is actionable.
RestartPreventExitStatus=78

[Install]
WantedBy=multi-user.target
EOF
}

# Older versions of this script also installed a dapps-updater timer for the
# since-removed self-updater. Remove it so it stops firing.
remove_old_updater_units() {
    if [[ -e /etc/systemd/system/dapps-updater.timer || -e /etc/systemd/system/dapps-updater.service ]]; then
        say "Removing the old dapps-updater units (DAPPS no longer updates itself)"
        systemctl disable --now dapps-updater.timer 2>/dev/null || true
        rm -f /etc/systemd/system/dapps-updater.service /etc/systemd/system/dapps-updater.timer
    fi
}

start_service() {
    say "systemctl daemon-reload"
    systemctl daemon-reload

    # `systemctl enable --now` starts a unit only if it's NOT already
    # active, so on a re-run it'd leave the old PID running against the
    # new on-disk binary - the operator sees the previous version on
    # the dashboard until the next reboot. Detect already-active units
    # and restart them explicitly.
    if systemctl is-active --quiet dapps.service; then
        say "Restarting dapps.service to pick up the new binary"
        systemctl restart dapps.service
    else
        say "Enabling and starting dapps.service"
        systemctl enable --now dapps.service
    fi
}

# Block until the HTTP listener is actually accepting connections, so
# the URL we print at the end is clickable rather than a "wait a few
# seconds and try again" surprise. Self-contained dotnet binaries
# can take a few seconds to JIT to ready on first start, especially
# on Pi-class hardware.
#
# Probes loopback regardless of HTTP_BIND - the listener binds to
# the configured iface, but loopback always works once it's up.
wait_for_http() {
    local port="${HTTP_BIND##*:}"
    # 90s rather than 30 to accommodate Pi-class hardware first-run
    # JIT off an SD card. On amd64 the listener typically comes up in
    # well under 5s.
    local timeout=90
    say "Waiting for HTTP listener on :$port (up to ${timeout}s)"
    local i=0
    while (( i < timeout )); do
        if (echo > /dev/tcp/127.0.0.1/"$port") 2>/dev/null; then
            return 0
        fi
        sleep 1
        ((i++))
    done
    warn "HTTP listener didn't respond within ${timeout}s. Check 'journalctl -u dapps.service -e' - the daemon may still be starting, or have hit a config error."
    return 0   # don't abort the install; still print the URL.
}

print_next_steps() {
    local host
    host=$(hostname -f 2>/dev/null || hostname)
    local port="${HTTP_BIND##*:}"
    cat <<EOF

DAPPS is running.

  Open  http://$host:$port/  in a browser.

  Logs:    journalctl -u dapps.service -f
  Upgrade: re-run the install command above to fetch the latest
           release. DAPPS never updates itself.
EOF
}

# ── main ───────────────────────────────────────────────────────────────
require_root
require_systemd

rid=$(detect_rid)
say "Architecture: $rid (kernel $(uname -m), userland $(getconf LONG_BIT 2>/dev/null || echo '?')-bit)"

verify_interpreter "$rid"
ensure_user
ensure_dirs
download_binary "$rid"
write_unit
remove_old_updater_units
start_service
wait_for_http
print_next_steps
