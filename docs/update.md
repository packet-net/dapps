# Upgrade

DAPPS never updates itself. You upgrade it the way you installed it, and the dashboard tells you when there is something new.

## apt installs

```bash
sudo apt update && sudo apt upgrade
```

That is the upgrade. The package stops the old daemon, replaces the payload under `/usr/lib/dapps`, and restarts the service; `/etc/dapps/dapps.env` and the database in `/var/lib/dapps` are left alone.

A release reaches the apt repository within about a minute of the tag: the release workflow tells [packet-net/apt](https://github.com/packet-net/apt) to reindex as its last step, and a nightly rebuild catches anything that dispatch missed.

## Docker

There is no published image yet, so rebuild it from a newer checkout and recreate the container. Your state lives in the volume, so it carries over:

```bash
git pull
docker build -t dapps .
docker compose up -d
```

See [Docker](install/docker.md).

## One-liner installer, or a binary you dropped in yourself

Re-run the installer; it fetches the latest release and restarts the service, keeping the old binary as `/opt/dapps/dapps.previous`:

```bash
curl -sSL https://packet-net.github.io/dapps/install.sh | sudo bash
```

Or do it by hand: download the binary for your platform from the [release page](https://github.com/packet-net/dapps/releases/latest), stop the service, swap the file, start it again. On macOS and Windows that is your only route; the install pages show the service commands.

To go back, put the old binary back the same way.

If you installed with an older `install.sh`, it also set up a `dapps-updater` timer. That is no longer used; the installer removes it when you re-run it, or remove it yourself:

```bash
sudo systemctl disable --now dapps-updater.timer
sudo rm -f /etc/systemd/system/dapps-updater.service /etc/systemd/system/dapps-updater.timer
sudo systemctl daemon-reload
```

## Knowing there is a new version

The dashboard tells you. It checks now and then (every six hours on an apt install, hourly otherwise) and nothing is downloaded or installed:

- On an apt install it reads the repository's package list, so the notice appears exactly when apt can install the new version. A banner across the top of every page links the release notes and gives the command, with a Copy button. If the `.deb` was installed by hand, the banner links the new `.deb` instead.
- Anywhere else it asks GitHub Releases. The banner links the release page and reminds you to replace the binary or rebuild the Docker image.

To check straight away, press **Check now** on the Overview's Build tile, **Check for updates now** in Settings, or `POST /Update/check`. When the check can't reach the server it shows nothing and says why in the log, once.

Don't want the check (an offline node, say)? Untick **Check for new releases** in Settings, or seed `DAPPS_UPDATE_CHECK_ENABLED=false` on first start.

A dev build (anything not built from a tagged release) reports `isDevBuild: true` and never claims to be out of date.
