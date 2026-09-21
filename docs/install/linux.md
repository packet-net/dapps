# Install on Linux (systemd)

The recommended deployment.

## apt (Debian, Ubuntu, Raspberry Pi OS)

```bash
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
sudo apt update
sudo apt install dapps
```

`amd64`, `arm64` and `armhf` are all published, so this is the same two commands on a Pi as on a server. The repository is the [packet-net apt repo](https://github.com/packet-net/apt), signed with the key you just installed, and it carries the rest of the stack too (`pdn-soundmodem`, `packetnet`, `axcall`, the pdn-\* services) - you only add it once.

Installing gives you:

- The payload under `/usr/lib/dapps`, with `/usr/bin/dapps` a symlink into it.
- A `dapps` system user, and `/var/lib/dapps` for the SQLite database (created by systemd, kept across upgrades **and across `apt purge`** - the message store is never something a package removal should take with it).
- `dapps.service`, enabled and started.
- `/etc/dapps/dapps.env`, seeded on first install and never touched again by an upgrade.

Then open `http://<node>:5000/`. The first request lands on `/Setup` - a two-step wizard for the admin password and your callsign + bearer. See [Getting started](../getting-started.md) for the full walk-through.

### Updating

```bash
sudo apt update && sudo apt upgrade
```

The package restarts the service for you. There is no `dapps-updater` unit in the apt install and the hourly update check is seeded off, because apt is the update path here - see [Update](../update.md).

### Configuration

Callsign, bearer, ports, forwarding, discovery and the rest live in the dashboard and are stored in the database. `/etc/dapps/dapps.env` is only for the things the dashboard cannot own - principally where the HTTP listener binds:

```bash
sudo nano /etc/dapps/dapps.env      # e.g. ASPNETCORE_URLS=http://127.0.0.1:5000
sudo systemctl restart dapps
```

To change the unit itself, use a drop-in rather than editing `/usr/lib/systemd/system/dapps.service` - a package upgrade replaces that file, and drop-ins survive it:

```bash
sudo systemctl edit dapps
```

which opens `/etc/systemd/system/dapps.service.d/override.conf`. Add only what you want to change:

```ini
[Service]
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
```

If you bind below port 1024 (e.g. the dashboard on `:80`), grant the binary the capability rather than running as root - and note this has to be applied to the real binary, not the symlink, and re-applied after each upgrade:

```bash
sudo setcap 'cap_net_bind_service=+ep' /usr/lib/dapps/dapps.core
```

### Migrating from an install.sh install

`apt install dapps` on a host that was set up with the one-liner installer will warn you, and it is worth reading: the installer writes its unit to `/etc/systemd/system/dapps.service`, which **overrides** the package's unit, so systemd would quietly keep running the old binary out of `/opt/dapps` while apt believes it owns the service. To finish the move:

```bash
sudo systemctl disable --now dapps-updater.timer
sudo rm -f /etc/systemd/system/dapps.service \
           /etc/systemd/system/dapps-updater.service \
           /etc/systemd/system/dapps-updater.timer
sudo systemctl daemon-reload
sudo systemctl restart dapps
sudo rm -rf /opt/dapps
```

Your database in `/var/lib/dapps` is untouched and carries straight over, so the node keeps its callsign, peers and queued messages.

### Uninstalling

```bash
sudo apt remove dapps      # stops and removes the service, keeps config and database
sudo apt purge dapps       # also removes /etc/dapps and the dapps user
```

Neither touches `/var/lib/dapps`. Remove it by hand once you are sure you want the message store gone.

## One-liner installer (non-Debian hosts)

On a systemd Linux that is not Debian-family - or where you would rather not add a repository:

```bash
curl -sSL https://packet-net.github.io/dapps/install.sh | sudo bash
```

Detects your architecture (`x86_64` / `aarch64` / `armv7l`), downloads the matching single-file binary from the [latest GitHub Release](https://github.com/packet-net/dapps/releases/latest) to `/opt/dapps/dapps`, creates the `dapps` system user and `/var/lib/dapps` state directory, drops `dapps.service` plus the privileged `dapps-updater.service` + `.timer`, and enables both. No env vars, no callsign yet - configuration happens in the dashboard once the daemon is up.

This path keeps the in-app update story: re-run the installer to upgrade in place, or use the dashboard's **Apply update** button, which goes through the supervised updater. See [Update](../update.md).

Use apt if you can. This exists so that "not Debian" does not mean "build it yourself", and it is the same binary either way.

## Manual install

If you would rather see what is happening, or your distro has something unusual about its systemd setup, here is what the one-liner does step by step.

### 1. Drop the binary

Pick the right binary for your architecture from the [latest release](https://github.com/packet-net/dapps/releases/latest):

```bash
sudo mkdir -p /opt/dapps /var/lib/dapps
sudo curl -L \
  https://github.com/packet-net/dapps/releases/latest/download/dapps-linux-x64 \
  -o /opt/dapps/dapps
sudo chmod +x /opt/dapps/dapps
```

Substitute `dapps-linux-arm64` (Pi 4/5, Apple Silicon Linux) or `dapps-linux-arm` (Pi Zero, Cubie) as needed.

### 2. Set up the runtime user

DAPPS runs as a non-privileged user. The updater service runs as root because it needs to swap a binary that's currently executing - but the daemon itself does not.

```bash
sudo useradd --system --home /var/lib/dapps --shell /usr/sbin/nologin dapps
sudo chown -R dapps:dapps /var/lib/dapps
```

### 3. Install the systemd units

There are two: `dapps.service` (the daemon) and `dapps-updater.service` plus `dapps-updater.timer` (the privileged updater).

#### dapps.service

```ini
[Unit]
Description=DAPPS daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=dapps
Group=dapps
WorkingDirectory=/var/lib/dapps
ExecStart=/opt/dapps/dapps
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Restart=on-failure
RestartSec=5s
# Exit code 78 = fatal config error - don't restart in a tight
# loop, leave it down so the journal message is actionable.
RestartPreventExitStatus=78

[Install]
WantedBy=multi-user.target
```

Note: **no `DAPPS_CALLSIGN` env var.** The daemon starts with the placeholder callsign and the dashboard's `/Setup` wizard configures the real one. (If you'd rather pre-set it for an automated deployment, `Environment=DAPPS_CALLSIGN=M0LTE-1` still works as a first-run seed - just include it before the unit starts the first time.)

If you bind to a port below 1024 (e.g. you want the dashboard on `:80`), either run as root (not recommended) or grant the binary `CAP_NET_BIND_SERVICE`:

```bash
sudo setcap 'cap_net_bind_service=+ep' /opt/dapps/dapps
```

#### dapps-updater.service + .timer

Privileged. Polls a marker file (`/var/lib/dapps/update-requested`) every 60 seconds; when present, runs `dapps --apply-update`, which downloads the latest release for this architecture, swaps `/opt/dapps/dapps`, restarts `dapps.service`, verifies the new daemon stays up for 60 seconds, and rolls back to `/opt/dapps/dapps.previous` on any failure.

```ini
# /etc/systemd/system/dapps-updater.service
[Unit]
Description=DAPPS supervised updater
After=network-online.target

[Service]
Type=oneshot
ExecStart=/opt/dapps/dapps --apply-update
```

```ini
# /etc/systemd/system/dapps-updater.timer
[Unit]
Description=Poll for DAPPS update requests

[Timer]
OnBootSec=2min
OnUnitActiveSec=1min
Unit=dapps-updater.service

[Install]
WantedBy=timers.target
```

The timer fires the service every minute; the service does nothing if no marker file exists, so the steady state is a no-op heartbeat.

Do **not** install these alongside the apt package. They swap a binary in `/opt/dapps` that an apt install does not use, and the unit in `/etc/systemd/system` silently wins over the packaged one.

### 4. Enable and start

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now dapps.service
sudo systemctl enable --now dapps-updater.timer
sudo systemctl status dapps.service
```

You should see `Active: active (running)` and a journal line `Now listening on: http://0.0.0.0:5000`.

```bash
sudo journalctl -u dapps.service -f
```

### 5. Open the dashboard

`http://<node>:5000/`. The first request lands on `/Setup` - a two-step wizard for the admin password and your callsign + bearer. See [Getting started](../getting-started.md) for the walk-through.

The `/Health` and `/Operational` endpoints are intentionally not behind the cookie - they're designed to be scraped by watchdogs and your own monitoring. The MCP endpoint at `/mcp` is also open for the same reason.

## Logs

DAPPS logs to stdout, which systemd captures into the journal:

```bash
# Tail the live stream
sudo journalctl -u dapps.service -f

# Last 24 hours
sudo journalctl -u dapps.service --since '24h ago'

# Search a single message id end-to-end
sudo journalctl -u dapps.service | grep abc1234
```

The journal also captures the structured decision-events that the `/Operational` endpoint surfaces - so an "what happened to message X two weeks ago" investigation is a `journalctl --grep` away.

## Backups

The state worth backing up is `/var/lib/dapps/dapps.db` - the SQLite database. The binary is recoverable from apt or GitHub Releases; everything else is derived from defaults. Stop DAPPS before copying for a consistent snapshot, or use SQLite's online backup API.

```bash
sudo systemctl stop dapps.service
sudo cp /var/lib/dapps/dapps.db /backup/dapps.db
sudo systemctl start dapps.service
```

## Uninstall (one-liner installs)

```bash
sudo systemctl disable --now dapps.service dapps-updater.timer
sudo rm /etc/systemd/system/dapps.service /etc/systemd/system/dapps-updater.{service,timer}
sudo systemctl daemon-reload
sudo rm -rf /opt/dapps /var/lib/dapps
sudo userdel dapps
```

For an apt install, see [Uninstalling](#uninstalling) above.
