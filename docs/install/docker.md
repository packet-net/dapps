# Install with Docker

There's no published DAPPS image at the moment, but the repository has a Dockerfile, so you can build one yourself. Useful if you already orchestrate with compose, run a homelab stack, or just don't want to manage another systemd unit. It's the same DAPPS as the bare-metal install - same configuration story, same dashboard.

## Build the image

```bash
git clone https://github.com/packet-net/dapps.git
cd dapps/src/dapps
docker build -t dapps .
```

Two things differ from the other installs: inside the container the dashboard listens on port **8080**, and the database lives in `/app/data` (mount a volume there to keep it).

## Quick start

```bash
docker run -d \
  --name dapps \
  -p 5000:8080 \
  -p 1883:1883 \
  -v dapps-data:/app/data \
  -e DAPPS_CALLSIGN=M0LTE-1 \
  -e DAPPS_NODE_HOST=192.168.1.10 \
  -e DAPPS_AGW_PORT=8000 \
  dapps
```

For XRouter (RHPv2 instead of AGW), substitute `-e DAPPS_NODE_BEARER=rhpv2 -e DAPPS_RHP_PORT=9000` for `-e DAPPS_AGW_PORT=8000`. See [Connect via XRouter](../connect/xrouter.md). The `DAPPS_*` variables only seed the first start; after that the dashboard owns those settings.

Then `http://localhost:5000/` for the dashboard. First request goes to `/Setup` to set an admin password.

## docker compose

For a more permanent setup, build the image as above, then:

```yaml
services:
  dapps:
    image: dapps
    container_name: dapps
    restart: unless-stopped
    ports:
      - "5000:8080"   # dashboard / REST / MCP
      - "1883:1883"   # MQTT broker
    volumes:
      - dapps-data:/app/data
    environment:
      DAPPS_CALLSIGN: M0LTE-1
      DAPPS_NODE_HOST: 192.168.1.10   # your packet node's IP
      DAPPS_AGW_PORT: 8000

volumes:
  dapps-data:
```

`docker compose up -d`.

## Connecting to a packet node on the host

If the node (BPQ, etc.) runs on the Docker host itself, the daemon needs to reach it across the container boundary:

- **Linux**: use the host's LAN IP (`hostname -I`), or run with `--network host` to share the host network namespace.
- **Docker Desktop on macOS / Windows**: `host.docker.internal` resolves to the host.

Set `DAPPS_NODE_HOST` accordingly.

## Updates

The supervised in-place update path (the `/Update/apply` button on the dashboard, the `trigger_update` MCP tool) does not work inside the standard image - the privileged updater wants to swap the binary on disk and restart its own service, which Docker doesn't model the same way.

Instead, update by rebuilding the image from a newer checkout and recreating the container:

```bash
git pull
docker build -t dapps .
docker compose up -d
```

The dashboard's update banner still surfaces "v0.X.Y available" so you know when to do this.

## Persistence

Everything DAPPS needs to remember between restarts is in the SQLite database at `/app/data/dapps.db` inside the container. Mount that path to a named volume (or a host bind mount) and you can recreate the container freely without losing state.

## Logs

```bash
docker logs -f dapps
```

Same content as the bare-metal install's stdout - start-up events, decision events, errors.
