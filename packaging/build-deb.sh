#!/usr/bin/env bash
# Builds the dapps .deb for one architecture.
#
#   packaging/build-deb.sh <version> [amd64|arm64|armhf] [outdir]
#
# Produces <outdir>/dapps_<version>_<arch>.deb containing a self-contained build of
# dapps.core (no .NET runtime dependency on the target), a systemd unit, and a seed
# environment file. Default outdir is <repo>/artifacts.
#
# WHY NOT THE SINGLE-FILE BINARY the GitHub Release carries: PublishSingleFile bundles
# every native library INSIDE the executable (see dapps.core.csproj), which hides them
# from readelf. The Depends: floors below are derived from the symbol versions the
# shipped ELFs actually ask for, and with everything bundled the only readable ELF is
# Microsoft's outer `singlefilehost` - which asks for a materially older glibc than
# libe_sqlite3.so next to it does. A package that understates its floor installs onto
# machines it cannot run on and dies in the dynamic loader at the first DB write. So the
# .deb ships the ordinary publish tree, where every .so is a file readelf can read.
#
# The side benefit is that IncludeAllContentForSelfExtract - which the single-file build
# needs, and which re-extracts the whole bundle under /tmp on every single start - does
# not apply here. An apt-installed node starts straight off /usr/lib/dapps.
#
# Layout: the payload lives in /usr/lib/dapps/ and /usr/bin/dapps is a symlink into it.
# .NET's apphost resolves dapps.core.dll from the REAL path of the running executable
# (/proc/self/exe), so the symlink is transparent; installing the bare apphost to
# /usr/bin without its tree would not work at all.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [arch] [outdir]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  armhf) RID=linux-arm ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

# dpkg-deb ships in the Essential `dpkg` package, so this only trips on a non-Debian host.
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found - this needs a Debian-family host" >&2; exit 3; }
# readelf reads the library-version floors out of the published tree (see the Depends
# section below). Refuse to build rather than fall back to an unversioned Depends.
command -v readelf >/dev/null || { echo "readelf not found - install binutils" >&2; exit 3; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUTDIR="${3:-$ROOT/artifacts}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

PKGDIR=/usr/lib/dapps
DOCDIR=/usr/share/doc/dapps
DATADIR=/usr/share/dapps
UNITDIR=/usr/lib/systemd/system
CONFDIR=/etc/dapps

# -p:Version stamps AssemblyInformationalVersion, which is what UpdateChecker reports as
# the running version and compares against the latest release tag. A .deb built with the
# wrong value here would tell every dashboard it was out of date forever.
dotnet publish "$ROOT/src/dapps/dapps.core/dapps.core.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  -p:GenerateDocumentationFile=false \
  --output "$STAGE/publish"

mkdir -p "$STAGE/root$PKGDIR" \
         "$STAGE/root/usr/bin" \
         "$STAGE/root$UNITDIR" \
         "$STAGE/root$CONFDIR" \
         "$STAGE/root$DATADIR" \
         "$STAGE/root$DOCDIR" \
         "$STAGE/root/DEBIAN"

# The whole publish tree: managed assemblies, the runtime, wwwroot, and the native shims.
cp -a "$STAGE/publish/." "$STAGE/root$PKGDIR/"
# cp -a preserves the publish directory's modes; normalise them so the package is
# reproducible and lintian-clean regardless of the build host's umask.
find "$STAGE/root$PKGDIR" -type d -exec chmod 0755 {} +
find "$STAGE/root$PKGDIR" -type f -exec chmod 0644 {} +
chmod 0755 "$STAGE/root$PKGDIR/dapps.core"
# .pdb files are not shipped (DebugType=none), but a stray one from an earlier build in
# the same output directory would be, so drop them explicitly.
find "$STAGE/root$PKGDIR" -name '*.pdb' -delete

ln -s "..${PKGDIR#/usr}/dapps.core" "$STAGE/root/usr/bin/dapps"

install -m 0644 "$HERE/dapps.service" "$STAGE/root$UNITDIR/dapps.service"
install -m 0644 "$HERE/dapps.env" "$STAGE/root$DATADIR/dapps.env.example"

# The copyright file is the packaging header plus the full licence text. DAPPS is
# AGPL-3.0, which - unlike GPL-3 - is NOT in Debian's /usr/share/common-licenses, so the
# text has to travel with the package rather than be referenced.
cat "$HERE/copyright" > "$STAGE/root$DOCDIR/copyright"
printf '\nFULL LICENCE TEXT\n-----------------\n\n' >> "$STAGE/root$DOCDIR/copyright"
cat "$ROOT/LICENSE" >> "$STAGE/root$DOCDIR/copyright"
chmod 0644 "$STAGE/root$DOCDIR/copyright"

# Debian changelog. A numeric SOURCE_DATE_EPOCH keeps rebuilds of a tag byte-identical;
# anything else (an ISO string from a CI event payload, say) falls back to now rather
# than failing the build on `date -R`.
case "${SOURCE_DATE_EPOCH:-}" in
  ''|*[!0-9]*) CHANGELOG_DATE="$(date -R)" ;;
  *)           CHANGELOG_DATE="$(date -R --date="@$SOURCE_DATE_EPOCH")" ;;
esac
cat > "$STAGE/changelog.Debian" <<EOF
dapps ($VERSION) unstable; urgency=medium

  * Release $VERSION. See https://github.com/packet-net/dapps/releases/tag/v$VERSION

 -- Tom Fanning M0LTE <tom@m0lte.uk>  $CHANGELOG_DATE
EOF
gzip -9n -c "$STAGE/changelog.Debian" > "$STAGE/root$DOCDIR/changelog.Debian.gz"
chmod 0644 "$STAGE/root$DOCDIR/changelog.Debian.gz"

INSTALLED_SIZE="$(du -k -s --exclude=DEBIAN "$STAGE/root" | cut -f1)"

# --- library version floors, read from the tree we just published ------------
# The runtime pack's floor is whatever Microsoft built THIS RID against, not anything
# this repo controls, and it moves between .NET versions without notice. Derive it from
# the ELFs rather than asserting one here, and let apt refuse the install with a clear
# reason instead of letting the loader do it with an obscure one.
#
# Every ELF in the package, not just the apphost: the native shims are linked separately
# and do not share its floor (libe_sqlite3.so wants a materially newer glibc than the
# apphost on some RIDs). Detect ELF by magic bytes rather than shelling out to `file`,
# which is not Essential and need not be on a build host.
elf_files() {
  find "$STAGE/root" -type f -print | while IFS= read -r f; do
    [ "$(od -An -tx1 -N4 "$f" 2>/dev/null | tr -d ' \n')" = "7f454c46" ] && printf '%s\n' "$f"
  done
}

# .gnu.version_r is the authoritative record of which symbol versions of which libraries
# the loader must satisfy. Take the highest of one family (GLIBC, GLIBCXX) across the lot.
# "GLIBC_" cannot match inside "GLIBCXX_", so the two families do not overlap.
max_needed() {
  local family="$1" max="" v f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    v="$(readelf --version-info "$f" 2>/dev/null \
      | awk '/Version needs section/,0' \
      | grep -oE "${family}_[0-9][0-9.]*" \
      | sed "s/^${family}_//" \
      | sort -uV \
      | tail -1)"
    [ -n "$v" ] && max="$(printf '%s\n%s\n' "$max" "$v" | sort -uV | tail -1)"
  done <<EOF
$(elf_files)
EOF
  printf '%s' "$max"
}

# A glibc symbol version is the glibc release that introduced it, and libc6's package
# version is that same release, so this maps straight onto a Debian version constraint.
GLIBC_MIN="$(max_needed GLIBC)"
GLIBCXX_MIN="$(max_needed GLIBCXX)"
[ -n "$GLIBC_MIN" ] || { echo "could not read a GLIBC floor from the staged package" >&2; exit 4; }
[ -n "$GLIBCXX_MIN" ] || { echo "could not read a GLIBCXX floor from the staged package" >&2; exit 4; }

# libstdc++ versions its symbols by C++ ABI, not by package version, so this needs a table.
# Anchors measured against the distributions themselves: Debian 10 ships GCC 8 and tops out
# at 3.4.25, Debian 11 / GCC 10 at 3.4.28, Debian 12 / GCC 12 at 3.4.30, Debian 13 / GCC 14
# at 3.4.33. Unmeasured points round up to the next anchor, because the failure modes are
# not symmetric: too high refuses an install that would have worked and says why, too low
# ships the loader crash this whole block exists to prevent. An unknown value is a new GCC
# ABI nobody has checked, so stop and make someone extend the table.
case "$GLIBCXX_MIN" in
  3.4|3.4.[0-9]|3.4.1[0-9]|3.4.2[01]) STDCXX_MIN=5 ;;
  3.4.22)     STDCXX_MIN=6 ;;
  3.4.23|3.4.24) STDCXX_MIN=7 ;;
  3.4.25)     STDCXX_MIN=8 ;;
  3.4.26)     STDCXX_MIN=9 ;;
  3.4.27|3.4.28) STDCXX_MIN=10 ;;
  3.4.29)     STDCXX_MIN=11 ;;
  3.4.30)     STDCXX_MIN=12 ;;
  3.4.31|3.4.32) STDCXX_MIN=13 ;;
  3.4.33)     STDCXX_MIN=14 ;;
  3.4.34)     STDCXX_MIN=15 ;;
  *) echo "unknown GLIBCXX_$GLIBCXX_MIN - extend the table in $0" >&2; exit 4 ;;
esac

# libgcc-s1 is deliberately left unversioned: the binary asks it only for GCC_3.0 and
# GCC_3.5, which every distribution in scope has carried for twenty years.
echo "floors for $ARCH: libc6 >= $GLIBC_MIN, libstdc++6 >= $STDCXX_MIN (GLIBCXX_$GLIBCXX_MIN)"

# --- dlopen'd dependencies, which readelf CANNOT see -------------------------
# .NET does not link ICU or OpenSSL; it probes for them by soname at runtime and dlopens
# whichever it finds. They are therefore absent from every DT_NEEDED in the package, and
# the derivation above cannot discover them - but without ICU the runtime refuses to start
# at all ("Couldn't find a valid ICU package installed on the system") and without OpenSSL
# every HTTPS call fails, which on this daemon takes the update check with it. Neither
# showed up as a problem on the raw-binary install path only because the distros in scope
# happen to ship both already.
#
# The soname is versioned per release and the package name follows it, so this has to be a
# disjunction. apt picks the FIRST alternative that is installable on the target, so list
# them newest-first: bookworm resolves libicu72, bullseye falls through to libicu67, and a
# machine that already has any of them (almost all do - ICU is pulled in very widely) sees
# the dependency as already satisfied and installs nothing.
ICU_DEP="libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu69 | libicu68 | libicu67 | libicu66 | libicu63"
# libssl3t64 is the time_t-64 rename carried by trixie and noble; libssl3 is bookworm,
# libssl1.1 bullseye and focal.
SSL_DEP="libssl3t64 | libssl3 | libssl1.1"

cat > "$STAGE/root/DEBIAN/control" <<EOF
Package: dapps
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $INSTALLED_SIZE
Depends: libc6 (>= $GLIBC_MIN), libgcc-s1, libstdc++6 (>= $STDCXX_MIN), $ICU_DEP, $SSL_DEP, ca-certificates, tzdata, adduser
Section: hamradio
Priority: optional
Homepage: https://github.com/packet-net/dapps
Description: Store-and-forward messaging overlay for packet radio
 DAPPS - Distributed Asynchronous Packet Pub-Sub - is a daemon you run beside
 your packet node. Local applications publish and subscribe over MQTT or REST,
 naming their destination as app@CALLSIGN; DAPPS finds a path and delivers when
 it can, handling routing, forwarding, fragmenting, retrying and acking.
 .
 Backhaul is bearer-agnostic: BPQ over AGW, XRouter over RHPv2, and MeshCore.
 .
 Ships a systemd unit, enabled and started on install. There is no callsign to
 configure up front - open the dashboard on http://<host>:5000/ and the /Setup
 wizard takes the admin password, the callsign and the bearer. Deployment-level
 overrides (bind address, ports) live in /etc/dapps/dapps.env.
 .
 Upgrades come from apt, so the in-app updater is off in this package: there is
 no dapps-updater unit and the update check is seeded off. "apt upgrade" is the
 update path.
 .
 AGPL-3.0-or-later.
EOF

# --- maintainer scripts -------------------------------------------------------
# The systemd stanzas follow dh_installsystemd's default output: enable the unit and
# start it on install, restart it on upgrade.

cat > "$STAGE/root/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e

EXAMPLE=/usr/share/dapps/dapps.env.example
ENVFILE=/etc/dapps/dapps.env

case "$1" in
  configure)
    if ! getent passwd dapps >/dev/null; then
        adduser --system --no-create-home --group --home /var/lib/dapps dapps
    fi
    if [ ! -e "$ENVFILE" ] && [ -f "$EXAMPLE" ]; then
        cp "$EXAMPLE" "$ENVFILE"
        chmod 0644 "$ENVFILE"
    fi
    # An install.sh-era install leaves a unit at /etc/systemd/system/dapps.service, and
    # /etc beats /usr/lib in systemd's unit search path - so that file would silently keep
    # /opt/dapps/dapps running while apt believes it owns the service. Say so loudly; do
    # not delete an admin's file from under them.
    if [ -e /etc/systemd/system/dapps.service ]; then
        echo "dapps: WARNING - /etc/systemd/system/dapps.service exists and OVERRIDES the" >&2
        echo "dapps:           unit in this package. It is almost certainly left over from" >&2
        echo "dapps:           the install.sh install. Until you remove it, systemd keeps" >&2
        echo "dapps:           running the old binary from /opt/dapps. To finish migrating:" >&2
        echo "dapps:             systemctl disable --now dapps-updater.timer" >&2
        echo "dapps:             rm -f /etc/systemd/system/dapps.service \\" >&2
        echo "dapps:                   /etc/systemd/system/dapps-updater.service \\" >&2
        echo "dapps:                   /etc/systemd/system/dapps-updater.timer" >&2
        echo "dapps:             systemctl daemon-reload && systemctl restart dapps" >&2
        echo "dapps:             rm -rf /opt/dapps" >&2
        echo "dapps:           Your database in /var/lib/dapps is untouched and carries over." >&2
    fi
    echo "dapps: the dashboard is on port 5000; the first visit lands on /Setup for the"
    echo "dapps: admin password, callsign and bearer."
    ;;
esac

if [ "$1" = "configure" ] || [ "$1" = "abort-upgrade" ] || [ "$1" = "abort-deconfigure" ] || [ "$1" = "abort-remove" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        # Undo the mask that postrm sets on remove.
        deb-systemd-helper unmask 'dapps.service' >/dev/null || true
        # was-enabled reports true for a unit the helper has never seen, so this enables on
        # first install and re-creates symlinks on upgrade if [Install] changed. The else
        # branch records current symlinks so purge cleans up after an admin who disabled it.
        if deb-systemd-helper --quiet was-enabled 'dapps.service'; then
            deb-systemd-helper enable 'dapps.service' >/dev/null || true
        else
            deb-systemd-helper update-state 'dapps.service' >/dev/null || true
        fi
    fi
    if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
        systemctl --system daemon-reload >/dev/null || true
        # $2 is the previously-configured version: set on upgrade, empty on first install.
        if [ -n "${2:-}" ]; then _action=restart; else _action=start; fi
        if command -v deb-systemd-invoke >/dev/null; then
            deb-systemd-invoke "$_action" 'dapps.service' >/dev/null || true
        fi
    fi
fi

exit 0
POSTINST

cat > "$STAGE/root/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e

if [ -d /run/systemd/system ] && [ "$1" = "remove" ] && command -v deb-systemd-invoke >/dev/null; then
    deb-systemd-invoke stop 'dapps.service' >/dev/null || true
fi

exit 0
PRERM

cat > "$STAGE/root/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e

if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
    systemctl --system daemon-reload >/dev/null || true
fi

if [ "$1" = "remove" ] && command -v deb-systemd-helper >/dev/null; then
    deb-systemd-helper mask 'dapps.service' >/dev/null || true
fi

if [ "$1" = "purge" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        deb-systemd-helper purge 'dapps.service' >/dev/null || true
        deb-systemd-helper unmask 'dapps.service' >/dev/null || true
    fi
    # dapps.env is seeded by postinst, not shipped by dpkg, so dpkg will not remove it
    # on purge - do it here. /var/lib/dapps is deliberately NOT removed: it holds
    # dapps.db, which is the node's entire state and the only thing worth backing up.
    # Losing a message store to an "apt purge" would be indefensible.
    rm -f /etc/dapps/dapps.env
    rmdir --ignore-fail-on-non-empty /etc/dapps 2>/dev/null || true
    if getent passwd dapps >/dev/null; then
        deluser --system --quiet dapps >/dev/null 2>&1 || true
    fi
    echo "dapps: /var/lib/dapps (the message database) has been left in place."
fi

exit 0
POSTRM

chmod 0755 "$STAGE/root/DEBIAN/postinst" "$STAGE/root/DEBIAN/prerm" "$STAGE/root/DEBIAN/postrm"
for s in postinst prerm postrm; do sh -n "$STAGE/root/DEBIAN/$s"; done

mkdir -p "$OUTDIR"
DEB="$OUTDIR/dapps_${VERSION}_${ARCH}.deb"
# -Zxz: pin xz - dpkg-deb's zstd default (dpkg >= 1.21.18) can't be unpacked by
# Debian Bullseye's dpkg, so a zstd .deb refuses to install there.
dpkg-deb --build --root-owner-group -Zxz "$STAGE/root" "$DEB"
echo "built: $DEB"
