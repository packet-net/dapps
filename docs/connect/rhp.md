# Connect via RHPv2

**Status: implemented.** RHPv2 (Remote Host Protocol v2) is a more modern host-to-node protocol than AGW. DAPPS speaks it via the [`RhpV2.Client` NuGet package](https://www.nuget.org/packages/RhpV2.Client) and selects between AGW and RHPv2 with a single env var.

[XRouter](xrouter.md) supports RHPv2 natively, and **RHPv2 is required for DAPPS-on-XRouter** - XRouter's AGW emulator is not usable as a DAPPS bearer because XRouter scopes the AGW callsign claim per-TCP-connection, which collides with DAPPS's per-outbound-fresh-connection pattern. The XRouter operator guide is at [Connect via XRouter (RHPv2)](xrouter.md).

[pdn](pdn.md), the packet.net node, serves RHPv2 too, and DAPPS runs on it as an app.

Mainline BPQ does not yet ship RHPv2; when it does, DAPPS will work over it without code changes.

## Why RHPv2

AGW is a 1990s protocol - it works fine on hosts that don't constrain callsign claims per connection (BPQ being the obvious one), but it has limits that show up under modern use. RHPv2 addresses several of them:

- **Better session multiplexing.** AGW assumes one TCP connection per host application. RHPv2 multiplexes many sessions over one TCP connection, with handle-based addressing. DAPPS's inbound listener and per-outbound-forward sends share one socket on RHPv2; on AGW each outbound forward needs its own TCP connection.
- **Cleaner inbound dispatch.** AGW's "register a callsign and receive every connect that matches" model has edge cases around shared callsigns and SSIDs. XRouter's per-TCP-connection AGW callsign claim is the most painful manifestation of this; RHPv2's handle-based binding sidesteps it entirely.
- **Actually defined error semantics.** AGW's behaviour on TNC reconnect, port up/down events, etc., is empirically discovered; RHPv2 makes it part of the spec.

## Configure DAPPS to use RHPv2

In DAPPS's environment (systemd unit, shell, etc.):

```
DAPPS_NODE_BEARER=rhpv2
DAPPS_NODE_HOST=<host>
DAPPS_RHP_PORT=<rhp port>
DAPPS_DEFAULT_BEARER_PORT=0
```

`DAPPS_NODE_BEARER=rhpv2` flips the bearer selector from AGW (the default) to RHPv2. The AGW knobs (`DAPPS_AGW_PORT`) are then unused.

`DAPPS_RHP_PORT` matches the host's RHPv2 listener port (XRouter's `RHPPORT=` directive; default 9000).

`DAPPS_DEFAULT_BEARER_PORT` is the same 0-indexed port byte used in the AGW path. DAPPS adds 1 internally to derive the RHPv2 port name (which is 1-indexed in XRouter, matching `PORT=N` in `XROUTER.CFG`). So `DAPPS_DEFAULT_BEARER_PORT=0` -> RHPv2 port "1" -> `PORT=1` in XRouter's config.

If RHPv2 requires authentication on your host, also set:

```
DAPPS_RHP_USER=<user>
DAPPS_RHP_PASS=<pass>
```

These are skipped if `DAPPS_RHP_USER` is empty.

## What stays the same

Everything above the bearer seam - the protocol, the app interface, the discovery model, the routing graph, the dashboard - works identically over RHPv2 and AGW. Switching between them is a restart with one env var changed; no data migration, no on-air-format change, no neighbour table rebuild.

## Testing

`scripts/sim-mixed-bearer.sh` brings up a 4-node BPQ+XRouter mesh (two BPQ, two XRouter) with DAPPS daemons attached - the BPQ-side daemons use AGW, the XRouter-side daemons use RHPv2. End-to-end exercise covers all five paths including the 3-hop XR->BPQ->BPQ->XR mixed-bearer route. The XRouter integration in CI stays on AGW (single-container frame-format coverage); the mixed-bearer four-container run is operator-driven on a workstation where the XRouter image is already pulled.

The RHPv2 bearer itself runs end to end in CI on [pdn](pdn.md) nodes: two pdn nodes over AXUDP, pdn with a BPQ node, and two pdn nodes on a simulated radio channel.
