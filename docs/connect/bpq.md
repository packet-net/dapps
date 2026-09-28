# Connect via BPQ (AGW)

This page is the full BPQ recipe for getting DAPPS on-air. Same shape applies to any AGW host (Direwolf, AGWPE, etc.) - only the specifics of the config file change.

## What we're wiring up

DAPPS uses **one** BPQ surface - AGW - for both inbound and outbound traffic. Outbound: DAPPS opens an AGW client connection to BPQ, registers its callsign, and dials remote callsigns when it has something to ship. Inbound: when a remote node L2-connects to your callsign with the application command `DAPPS`, BPQ dispatches the session over AGW to the registered DAPPS client.

This is one TCP connection from DAPPS to BPQ (not co-located requirements; DAPPS can run on a different host as long as it can reach BPQ's AGW port over TCP).

## Step 1: configure BPQ

Add an `APPLICATION` line to your `bpq32.cfg`, carrying the DAPPS callsign (the call+SSID you'll give DAPPS in step 3) in the APPLCALL field:

```
APPLICATION 1,DAPPS,,M0LTE-7,DAPPS,0
```

Field-by-field:

- `1` - application slot number. Bump this if slot 1 is already in use; the value doesn't matter beyond uniqueness.
- `DAPPS` - the command operators type at the BPQ node prompt to enter the DAPPS slot. If you'd rather use a different name (`MSG`, `APPS`, whatever), put it here and update **Node-prompt application command** under the dashboard's **Edit configuration** to match.
- (empty third field) - the CMD field, empty on purpose. Older recipes used `C N HOST K TRANS S` here; for DAPPS, leave it empty so BPQ doesn't run any node command on inbound - it just dispatches the L2 'C' frame to the registered AGW client.
- `M0LTE-7` - the **APPLCALL**: the DAPPS callsign. This is the field that makes BPQ accept inbound L2 connects addressed to the DAPPS callsign. DAPPS registering the same callsign over AGW at runtime tells BPQ *which AGW client* gets those sessions; the APPLCALL declaration is what makes BPQ accept them at all. Without it, BPQ ignores connects to the DAPPS callsign and the remote caller times out with RETRYOUT.
- `DAPPS` - the APPLALIAS. Lets stations connect to the alias as well as the callsign.
- `0` - quality. Zero keeps the application callsign out of NODES broadcasts; raise it only if you want the DAPPS call advertised as a node.

Restart BPQ for the change to land.

The division of labour is worth spelling out, because it trips people up: an inbound connect to the **DAPPS callsign** (`M0LTE-7` above) goes straight to DAPPS and never sees the node menu; an inbound connect to your **node call** lands at the node menu, where typing `DAPPS` enters the application. Both paths end at the same place; the APPLCALL route is the one DAPPS itself uses for node-to-node sessions.

You can also enable AXIP / AXUDP / serial ports as you would for any application - DAPPS doesn't care which physical link AGW is fronting, as long as BPQ delivers sessions over the AGW socket.

On a radio port, check its `FRACK` against [Tune](../tune.md#your-nodes-radio-port): it makes more difference to how fast mail moves than anything in DAPPS.

## Step 2: confirm AGW is reachable from where DAPPS will run

```bash
# From the host DAPPS runs on
nc -vz <bpq-host> 8000
```

Should connect immediately. If not, check BPQ's AGW listener is enabled (`AGWPORT 8000` in `bpq32.cfg`) and that no firewall is in the way.

## Step 3: tell DAPPS where BPQ is

In the dashboard's `/Setup` wizard (first run) or under **Edit configuration** on the dashboard (later):

| Field         | Value                                          |
|---------------|------------------------------------------------|
| Callsign      | your callsign with SSID, e.g. `M0LTE-1`        |
| Node host     | `<bpq-host>` (usually `localhost`)             |
| Node bearer   | **AGW**                                        |
| AGW port      | `8000`                                         |
| Default bearer port | `0` (the bearer port used for outbound when a neighbour has no per-row override; 0-indexed in the order they appear in `bpq32.cfg`'s `PORTNUM` lines) |

The wizard's **Detect packet node** button probes localhost:8000 and pre-selects AGW automatically when BPQ is on the same host. SSID matters: BPQ dispatches based on the full call+SSID, so `M0LTE-1` is different from `M0LTE-2`.

Saved settings hot-reload - the daemon picks up the new callsign and bearer port within a few seconds, no restart needed.

(The `DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, `DAPPS_AGW_PORT`, `DAPPS_DEFAULT_BEARER_PORT` env vars still work as **first-run seeds** for automated deployments - set them before the daemon's first start and they populate the equivalent rows. After first start, the persisted values win and env vars stop mattering.)

## Step 4: watch for AGW registration

A successful start logs:

```
AGW: connected to <host>:8000, registered M0LTE-1 for inbound dispatch
```

If you see repeated reconnect attempts instead, AGW isn't listening or the callsign is busy elsewhere.

## Step 5: prove the inbound side

From another packet node (or from the BPQ console):

```
c <your-callsign>
```

You should land at the `DAPPSv1>` prompt - that's DAPPS having taken the inbound session from AGW. Disconnect.

## Step 6: prove the outbound side

Add a manual neighbour from the dashboard's **Neighbours** panel (or the [discovery system](../discovery-and-routing.md), but a hand-add gets you there fastest):

| Field          | Value                                       |
|----------------|---------------------------------------------|
| Callsign       | another DAPPS node's callsign with SSID     |
| bearer port       | the bearer port to use for AGW originates |
| UDP endpoint   | leave blank                                 |

Send a test message via the dashboard's **Send a test message** form. The forwarder service will pick it up within a few seconds, dial the remote callsign over AGW, and you'll see the session in BPQ's MHEARD / connections view.

## Multi-port setups

If your BPQ has multiple radio ports (e.g. port 1 = VHF FM, port 2 = HF), every neighbour record can specify which bearer port to use for that peer. The dashboard's add-neighbour form has the bearer-port field; the discovery system records the port a peer was heard on automatically.

You don't need an `APPLICATION` line per port - the single `APPLICATION DAPPS` line covers all ports. The port choice is per-neighbour for outbound; for inbound, BPQ dispatches the same way no matter which port the connect arrived on.

## AXIP / AXUDP links

If the port a neighbour is reached over is an AXIP/AXUDP port with per-callsign `MAP` entries (common for LAN or internet links between two BPQ nodes), remember that the L2 connect DAPPS originates is addressed to the remote **DAPPS callsign**, not the remote node call. Add a `MAP` for the DAPPS callsign alongside the node call:

```
MAP MB7XXX 192.168.1.20 UDP 10093 B
MAP M0XYZ-7 192.168.1.20 UDP 10093 B
```

Without the second entry, the SABM to the DAPPS callsign has nowhere to go and the connect retries out on the *originating* side.

## Sharing a callsign between BPQ and DAPPS

The recommended pattern is to give DAPPS its own SSID under your callsign - `M0LTE-1` for the BBS, `M0LTE-7` for DAPPS, etc. AGW dispatches by exact call+SSID, so they coexist cleanly. You can run as many DAPPS instances against one BPQ as you have spare SSIDs.

Same-callsign-same-SSID with another application is **not** supported - AGW will dispatch to whichever client registered last and the first registration silently stops getting inbound traffic.

## Troubleshooting

See the [Troubleshooting](../troubleshooting.md) page for the AGW-specific failure modes (registration timeouts, port-byte confusion, BPQ refusing the inbound dispatch).

## Other AGW hosts

The AGW protocol is well-defined and stable; any host that implements it should work. Direwolf in particular ships an AGW host as a built-in option. The configuration shape is the same: enable AGW, give DAPPS the callsign and host:port, register on connect.

If you've got a non-BPQ AGW setup working, please [open an issue](https://github.com/packet-net/dapps/issues) so we can document it here.
