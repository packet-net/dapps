# Install

On Debian or Ubuntu - including Raspberry Pi OS - install from the packet-net apt repository:

```bash
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
sudo apt update
sudo apt install dapps
```

That creates the `dapps` user and `/var/lib/dapps`, installs a systemd unit, and starts it. Configuration lives in the dashboard's `/Setup` wizard - open `http://<host>:5000/`. Updates arrive with `apt upgrade` like anything else on the machine.

The same repository carries the rest of the packet-net stack (`pdn-soundmodem`, `packetnet`, `axcall`, the pdn-\* services); adding it once is enough for all of them.

On a Linux box that is not Debian-family, or where apt is not how you want to manage this, the [one-liner installer](linux.md#one-liner-installer-non-debian-hosts) drops the same binary straight from the GitHub Release.

## What ships

The `.deb` carries a self-contained build - the DAPPS code, its dependencies and the .NET runtime, under `/usr/lib/dapps` with `/usr/bin/dapps` pointing at it. No .NET install on the target, and `apt` resolves the handful of system libraries .NET loads at runtime (ICU, OpenSSL) so an unusually slim machine gets told what is missing rather than failing at start-up.

| Platform     | apt architecture | Release binary                  |
|--------------|------------------|---------------------------------|
| Linux x86-64 | `amd64`          | `dapps-linux-x64`               |
| Linux ARM64  | `arm64`          | `dapps-linux-arm64`             |
| Linux ARM32  | `armhf`          | `dapps-linux-arm` (Pi, Cubie)   |
| Windows x64  | -                | `dapps-win-x64.exe`             |
| macOS ARM64  | -                | `dapps-osx-arm64`               |

The right-hand column is the single-file binary attached to [every release](https://github.com/packet-net/dapps/releases/latest) - what the one-liner installer downloads, and the only option on Windows and macOS.

## Pick a platform

- [**Linux (systemd)**](linux.md) - recommended; apt, and the manual equivalent.
- [**Docker**](docker.md) - published image; useful if you already orchestrate with compose / a homelab stack.
- [**Windows**](windows.md) - runs as a console app today; service install is manual.
- [**macOS**](macos.md) - same shape as Windows; `launchd` plist optional.

## Compatibility notes

- **.NET 10** is the baseline runtime. Everything is self-contained - you do not need .NET installed.
- **glibc 2.34 or newer** on Linux, which means Debian 12 (Bookworm), Raspberry Pi OS 12, Ubuntu 22.04 and anything newer. Raspberry Pi OS 11 (Bullseye, glibc 2.31) is no longer supported: we stayed on .NET 8 to keep it in scope for as long as that runtime was supported, and .NET 8 leaves support in November 2026. The last release that runs on Bullseye is 0.35.0. The `.deb` declares the floor it actually needs, derived from the shipped binaries at build time, so apt refuses an install that would not run rather than letting it fail in the dynamic loader.
- **Windows**: any 64-bit Windows 10 / Server 2016 or newer.
- **macOS**: Apple Silicon (M-series). Intel macs are not in the matrix; build from source if you need them.

## Storage and ports

A running DAPPS node writes:

- A SQLite database at `/var/lib/dapps/dapps.db` under the systemd recipe, or `data/dapps.db` relative to the working directory if you run it by hand.
- An MQTT broker on TCP 1883 (configurable; bind to localhost in most cases).
- An HTTP listener on TCP 5000 (configurable; this is the dashboard, REST API, and MCP endpoint).
- Optional UDP datagram listener on a port of your choosing (off by default; used by the test stand-in for MeshCore).

It opens an outbound TCP connection to your packet node's bearer port - AGW (default 8000 on BPQ) or RHPv2 (default 9000 on XRouter), depending on `DAPPS_NODE_BEARER`.

The full list of operator-tunable knobs is on the [Configure](../configure.md) page.
