# DAPPS

**Distributed Asynchronous Packet Pub-Sub.** An asynchronous messaging overlay for packet-radio networks: applications queue messages destined for `app@CALLSIGN`, DAPPS finds a path, delivers when it can. Real-time connectivity is not a goal - think of it as packet mail for application developers, with proper delivery semantics and a modern app interface (MQTT or REST).

## 60-second pitch

A DAPPS node is a small daemon you run alongside your packet node (BPQ today; MeshCore and RHPv2 in flight). It exposes:

- **An app interface** - local applications publish and subscribe over MQTT (or REST). They name their destination as `app@CALLSIGN` and DAPPS handles routing, forwarding, fragmenting, retrying, and acking.
- **A backhaul** - DAPPS opens sessions to other DAPPS nodes (over AGW today) to move messages towards their destination, hop by hop, with TTL.

Bearer-agnostic by design: anything that exposes an AGW-compatible session bearer works the same way, and once RHPv2 lands in mainstream BPQ it'll plug in alongside.

## Install

On Debian, Ubuntu or Raspberry Pi OS, from the [packet-net apt repository](https://github.com/packet-net/apt):

```bash
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
sudo apt update && sudo apt install dapps
```

Then open `http://<host>:5000/` and the `/Setup` wizard takes it from there. `amd64`, `arm64` and `armhf` are published; for anything else - non-Debian Linux, Docker, Windows, macOS - see [Install](https://packet-net.github.io/dapps/install/).

## Documentation

**The full operator and developer manual is at [https://packet-net.github.io/dapps/](https://packet-net.github.io/dapps/).**

It covers:

- [Getting started](https://packet-net.github.io/dapps/getting-started/) - 10-minute install-to-message tour.
- [Install](https://packet-net.github.io/dapps/install/) - Linux/systemd, Docker, Windows, macOS.
- [Connect a node](https://packet-net.github.io/dapps/connect/) - BPQ via AGW and XRouter via RHPv2 supported today; MeshCore in flight.
- [Configure](https://packet-net.github.io/dapps/configure/), [Run](https://packet-net.github.io/dapps/run/), [Tune](https://packet-net.github.io/dapps/tune/) - every operator knob, what each background loop does, what to leave alone.
- [Discovery & routing](https://packet-net.github.io/dapps/discovery-and-routing/), [Operate](https://packet-net.github.io/dapps/operate/), [Update](https://packet-net.github.io/dapps/update/) - the day-to-day surfaces.
- [MCP for assistants](https://packet-net.github.io/dapps/mcp/) - let an AI assistant drive the operator surface.
- [App developers](https://packet-net.github.io/dapps/app-developers/) - concepts, hello-world tutorial, full reference, sample gallery.
- [Troubleshooting](https://packet-net.github.io/dapps/troubleshooting/) and [Glossary](https://packet-net.github.io/dapps/glossary/).

The roadmap and engineering notes (including the design discussion behind the bearer seam, MeshCore-as-backhaul tradeoffs, etc.) live in [`plan.md`](plan.md) and [`docs/`](docs/) in this repo.

## Local simulators

Three sim scripts in [`scripts/`](scripts/) layer on top of each other from cheapest to most realistic - DAPPS-on-loopback over UDP multicast, real BPQ + XRouter over AX.25-over-UDP partner links, and real BPQ + XRouter over real KISS framing into a simulated RF channel (FM capture, collisions, hidden terminals, multi-band paths, multi-hop AX.25 chains via connect-scripts). See [`docs-internal/simulators.md`](docs-internal/simulators.md) for the topology of each, what they validate, and the bring-up nuances captured during the diagnosis runs.

## Credits

To all at OARC who participated in the RFC, helping take this from a rough idea to a workable system.
