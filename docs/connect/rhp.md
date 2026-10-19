# Connect via RHPv2

**Status: implemented.** RHPv2 (Remote Host Protocol v2) is a more modern host-to-node protocol than AGW. DAPPS speaks it via the [`RhpV2.Client` NuGet package](https://www.nuget.org/packages/RhpV2.Client) and selects between AGW and RHPv2 with a single setting.

[XRouter](xrouter.md) supports RHPv2 natively, and **RHPv2 is the bearer to use with XRouter**: it is what DAPPS-on-XRouter is tested with. XRouter's AGW emulator lets one connection at a time claim a callsign; DAPPS now carries all its AGW traffic over one connection, but has not been tested on XRouter's AGW as its bearer (see [Why not AGW on XRouter?](xrouter.md#why-not-agw-on-xrouter)). The XRouter operator guide is at [Connect via XRouter (RHPv2)](xrouter.md).

[pdn](pdn.md), the packet.net node, serves RHPv2 too, and DAPPS runs on it as an app.

Mainline BPQ does not yet ship RHPv2; when it does, DAPPS will work over it without code changes.

## Why RHPv2

AGW is a 1990s protocol - it works fine on hosts that don't constrain callsign claims per connection (BPQ being the obvious one), but it has limits that show up under modern use. RHPv2 addresses several of them:

- **Better session multiplexing.** RHPv2 multiplexes many sessions over one TCP connection with handle-based addressing. AGW tells sessions on a connection apart only by port and callsign pair, so DAPPS has to work out which session each frame belongs to from that and from the text of the node's messages. DAPPS carries its inbound and outbound sessions over one connection on either.
- **Cleaner inbound dispatch.** AGW's "register a callsign and receive every connect that matches" model has edge cases around shared callsigns and SSIDs. XRouter's per-TCP-connection AGW callsign claim is the most painful manifestation of this; RHPv2's handle-based binding sidesteps it entirely.
- **Actually defined error semantics.** AGW's behaviour on TNC reconnect, port up/down events, etc., is empirically discovered; RHPv2 makes it part of the spec.

## Configure DAPPS to use RHPv2

Set **Node bearer** to RHPv2, plus the node host and RHPv2 port, in the `/Setup` wizard or on the dashboard's Settings page. For an automated deployment, the same settings can be seeded with env vars before the daemon's first start (after that the dashboard owns them):

```
DAPPS_NODE_BEARER=rhpv2
DAPPS_NODE_HOST=<host>
DAPPS_RHP_PORT=<rhp port>
DAPPS_DEFAULT_PORT=1
```

`DAPPS_NODE_BEARER=rhpv2` flips the bearer selector from AGW (the default) to RHPv2. The AGW knobs (`DAPPS_AGW_PORT`) are then unused.

`DAPPS_RHP_PORT` matches the host's RHPv2 listener port (XRouter's `RHPPORT=` directive; default 9000).

`DAPPS_DEFAULT_PORT` is the radio port a neighbour without one of its own is called on, by the node's own number for it: XRouter's `PORT=` number, or pdn's port as its `PORTS` command lists it. DAPPS hands that number to RHPv2 as it is. (`DAPPS_DEFAULT_BEARER_PORT`, its name up to 0.53, counted from 0; it still works, one higher, but is deprecated.)

If RHPv2 requires authentication on your host, also set:

```
DAPPS_RHP_USER=<user>
DAPPS_RHP_PASS=<pass>
```

These are skipped if `DAPPS_RHP_USER` is empty.

## What stays the same

Everything above the bearer seam - the protocol, the app interface, the discovery model, the routing graph, the dashboard - works identically over RHPv2 and AGW. Switching between them is one setting on the Settings page, picked up without a restart; no data migration, no on-air-format change, no neighbour table rebuild.

## Testing

`scripts/sim-mixed-bearer.sh` brings up a 4-node BPQ+XRouter mesh (two BPQ, two XRouter) with DAPPS daemons attached - the BPQ-side daemons use AGW, the XRouter-side daemons use RHPv2. End-to-end exercise covers all five paths including the 3-hop XR->BPQ->BPQ->XR mixed-bearer route. The XRouter integration in CI stays on AGW (single-container frame-format coverage); the mixed-bearer four-container run is operator-driven on a workstation where the XRouter image is already pulled.

The RHPv2 bearer itself runs end to end in CI on [pdn](pdn.md) nodes: two pdn nodes over AXUDP, pdn with a BPQ node, and two pdn nodes on a simulated radio channel.
