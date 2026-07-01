# DAPPS

**Distributed Asynchronous Packet Pub-Sub** - an asynchronous messaging overlay for packet-radio networks. Apps queue messages destined for `app@CALLSIGN`, DAPPS finds a path and delivers when it can. Real-time connectivity is not a goal; think of it as packet mail for application developers, with proper delivery semantics and a modern app interface.

## What this manual covers

- [**Getting started**](getting-started.md) - what DAPPS is, why you might want it, and a 10-minute tour from install to first message.
- [**Install**](install/index.md) - Linux/systemd, Docker, Windows, macOS.
- [**Configure**](configure.md) - every operator-tunable knob, set via environment variable or the dashboard.
- [**Connect a node**](connect/index.md) - wire DAPPS up to your packet node. BPQ via AGW and XRouter via RHPv2 supported today; MeshCore in flight.
- [**Run**](run.md) - what each background loop does and how to watch it.
- [**Tune**](tune.md) - airtime budgets, probe strategies, fragment thresholds, routing algorithm.
- [**Discovery & routing**](discovery-and-routing.md) - channels, beacons, probes, neighbours, route hints.
- [**Operate**](operate.md) - dashboard, `/Health` and `/Operational`, MQTT heartbeat.
- [**Audit log**](audit.md) - persistent record of every transmission, with the reason for it.
- [**Update**](update.md) - banner, one-click apply, MCP-driven, rollback.
- [**MCP for assistants**](mcp.md) - let an AI assistant drive the operator surface.
- [**App developers**](app-developers/index.md) - concepts, tutorial, reference, sample gallery.
- [**Troubleshooting**](troubleshooting.md) - common failure modes and how to diagnose.
- [**Glossary**](glossary.md) - terms.

## Bearer-agnostic by design

DAPPS does **not** require BPQ. The default setup guide is BPQ because that's where the on-air ecosystem lives today, but DAPPS talks to the bearer through a small interface - anything that exposes an AGW-compatible session bearer works the same way. RHPv2 (Remote Host Protocol v2) is already in tree alongside AGW: XRouter operators set `DAPPS_NODE_BEARER=rhpv2` and DAPPS uses RHPv2 instead. A non-stream bearer is also in tree (UDP datagram, used as the test stand-in for what MeshCore will become). The bits that change per bearer are isolated; the rest of the system doesn't care whether your link is 1200-baud VHF AX.25 or LoRa mesh.

## Status

- **Protocol**: DAPPSv1 specified end-to-end. App authors want [the protocol reference](app-developers/reference.md); second-source implementers want [Implement DAPPS](implement.md).
- **Implementation**: in active development. The [versioning policy](configure.md#versioning) describes where breaking changes are still fair game versus where compatibility is preserved.
- **Bearers**: AGW (BPQ today, any AGW host in principle) and RHPv2 (XRouter today; mainline BPQ when it ships RHPv2) are production-quality. [MeshCore](connect/meshcore.md) Companion-over-USB has shipped (off by default; binary bearer with compression, good-citizen controls, reliability, discovery, and A/B/C deployment models); the MeshCore KISS flavour is still planned.

If you've never heard of DAPPS before, [start here](getting-started.md).
