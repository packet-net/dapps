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
- `DAPPS` - the command operators type at the BPQ node prompt to enter the DAPPS slot. If you'd rather use a different name (`MSG`, `APPS`, whatever), put it here and update **Node-prompt application command** on the dashboard's **Settings** page to match.
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

In the dashboard's `/Setup` wizard (first run) or on the dashboard's **Settings** page (later):

| Field         | Value                                          |
|---------------|------------------------------------------------|
| Callsign      | your callsign with SSID, e.g. `M0LTE-1`        |
| Node host     | `<bpq-host>` (usually `localhost`)             |
| Node bearer   | **AGW**                                        |
| AGW port      | `8000`                                         |

Neither page has a default port field: set each neighbour's **Radio port** when you add it on the **Topology > Neighbours** tab, picked from the list DAPPS reads from BPQ. Ports are BPQ's own numbers, the ones `PORTS` lists and `C 2 CALL` uses. A neighbour without one uses port `1` (change that with `DAPPS_DEFAULT_PORT` before first start, or `POST /Config`).

The wizard's **Detect packet node** button probes localhost:8000 and pre-selects AGW automatically when BPQ is on the same host. SSID matters: BPQ dispatches based on the full call+SSID, so `M0LTE-1` is different from `M0LTE-2`.

Saved settings hot-reload - the daemon picks up the new callsign within a few seconds, no restart needed.

(The `DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, `DAPPS_AGW_PORT`, `DAPPS_DEFAULT_PORT` env vars still work as **first-run seeds** for automated deployments - set them before the daemon's first start and they populate the equivalent rows. After first start, the persisted values win and env vars stop mattering.)

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

Add a manual neighbour on the dashboard's **Topology > Neighbours** tab (or the [discovery system](../discovery-and-routing.md), but a hand-add gets you there fastest):

| Field          | Value                                       |
|----------------|---------------------------------------------|
| Callsign       | another DAPPS node's callsign with SSID     |
| Radio port     | the BPQ port the peer is reached on, picked from the list DAPPS reads from BPQ |
| UDP endpoint   | leave blank                                 |

Send a test message with the **Quick send** form on the dashboard's Overview. The forwarder service will pick it up within a few seconds, dial the remote callsign over AGW, and you'll see the session in BPQ's MHEARD / connections view.

## Multi-port setups

If your BPQ has multiple radio ports (e.g. port 1 = VHF FM, port 2 = HF), every neighbour can say which port to use for that peer, by BPQ's own port number. The discovery system records the port a peer was heard on automatically.

**Number the ports DAPPS uses 1, 2, 3 in `bpq32.cfg` order, and hide none of them.** BPQ's AGW interface sends and connects by a port's position in the config (counting hidden ports for some commands and not for others), but reports the frames it hears by `PORTNUM`. These only agree when `PORTNUM`s run 1 to N in config order with no hidden ports, which is what BPQ does when you don't set `PORTNUM` at all. Otherwise DAPPS could hear a peer on one port and call it on another. DAPPS can't see `PORTNUM` over AGW, but it can see hidden ports (BPQ counts more ports than it lists), and warns about them on the Topology page and in its log. It never sends on a port number past the ones BPQ lists.

### Upgrading from 0.52 or earlier

Older DAPPS counted ports from 0, so BPQ's port 2 showed as `1`. On upgrade DAPPS converts what you saved, once, as soon as BPQ's AGW connection answers (a few seconds after start), and everything shows one higher: your port 2 now shows as `2`. The same ports are used on air as before. Until the conversion, nothing is sent on those ports and the Topology page marks them **waiting**.

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

See the [Troubleshooting](../troubleshooting.md) page for the AGW-specific failure modes (registration timeouts, port numbering, BPQ refusing the inbound dispatch).

## Other AGW hosts

The AGW protocol is well-defined and stable; any host that implements it should work. Direwolf in particular ships an AGW host as a built-in option. The configuration shape is the same: enable AGW, give DAPPS the callsign and host:port, register on connect.

On these hosts DAPPS numbers ports as the host's AGW port list labels them: `Port1` is `1`. On Dire Wolf that makes channel 0 port `1`, channel 1 port `2`, and with soundcards on channels 0 and 2, ports `1` and `3`.

If you've got a non-BPQ AGW setup working, please [open an issue](https://github.com/packet-net/dapps/issues) so we can document it here.
