# Connect via XRouter (RHPv2)

This page is the full XRouter recipe for getting DAPPS on-air. If you're already running XRouter as your packet node, here's what you need to do to add DAPPS.

DAPPS connects to XRouter over **RHPv2** (Remote Host Protocol v2). RHPv2 is required for DAPPS-on-XRouter; XRouter's AGW emulator is not usable as a DAPPS bearer because XRouter scopes the AGW callsign claim per-TCP-connection - DAPPS's per-outbound-fresh-connection pattern collides with its own standing inbound registration. RHPv2 has no equivalent constraint, multiplexes inbound and outbound over a single TCP connection, and is what XRouter ships natively. See [RHPv2](rhp.md) for the protocol-level overview.

## What we're wiring up

DAPPS opens one TCP connection to XRouter's RHPv2 listener and binds its callsign for inbound dispatch. When a remote node L2-connects to that callsign, XRouter routes the session to DAPPS over the same TCP connection, multiplexed by handle. Outbound is the mirror image: DAPPS asks XRouter to dial a remote callsign over a chosen port, the session is established at L2, DAPPS speaks DAPPSv1 over the per-handle byte stream.

DAPPS does not need to be on the same host as XRouter, as long as it can reach XRouter's RHPv2 port over TCP.

## XRouter versions

DAPPS can't receive calls over RHPv2 on XRouter 505o. When DAPPS claims its callsign, 505o's reply isn't valid JSON, so DAPPS never starts listening: nothing can connect to it, and its log stops at `RHP inbound: connecting to`. That's an XRouter bug, and XRouter is being fixed. Until a fixed build is out, stay on an earlier XRouter: 505c works.

## Step 1: enable RHPv2 in XRouter

XRouter's RHPv2 listener is opt-in. Add the directive to `XROUTER.CFG`:

```
RHPPORT=9000
```

Pick any free TCP port; 9000 is the convention. Restart XRouter.

If you also have `AGWPORT=` set, you can leave it in place - it doesn't conflict, it just isn't used by DAPPS.

## Step 2: pick a callsign for DAPPS

DAPPS needs its own callsign with SSID. Convention is to use your own callsign with an unused SSID, e.g. `M0LTE-7`.

Make sure your `NODECALL`, `CONSOLECALL`, `CHATCALL`, and any `APPL` blocks all use *different* callsigns from the one you'll give DAPPS. RHPv2 binds the callsign dynamically at runtime, so no `APPL` declaration is needed (and you should not add one - it would claim the callsign for an internal XRouter application before DAPPS could bind it).

## Step 3: ACCESS.SYS, if non-loopback

If DAPPS runs on a different host from XRouter, your `ACCESS.SYS` needs to allow connections from DAPPS's IP. Add a line for the subnet DAPPS connects from, with flag `1` (callsign-only, no password):

```
192.168.1.0/24    1
```

If DAPPS is on the same host as XRouter (loopback), you don't need to touch `ACCESS.SYS`.

Restart XRouter to pick up `ACCESS.SYS` changes.

## Step 4: tell DAPPS where XRouter is

In the dashboard's `/Setup` wizard (first run) or on the dashboard's **Settings** page (later):

| Field         | Value                                                      |
|---------------|------------------------------------------------------------|
| Callsign      | your callsign with SSID, e.g. `M0LTE-7`                    |
| Node host     | `<xrouter-host>` (`localhost` if same host as DAPPS)       |
| Node bearer   | **RHPv2**                                                  |
| RHPv2 port    | `9000` (matches `RHPPORT=` in `XROUTER.CFG`)               |
| RHPv2 user    | leave blank if XRouter doesn't require RHPv2 auth          |
| RHPv2 password | matching credential, otherwise blank                       |

Neither page has a default bearer port field: set each neighbour's **Radio port** when you add it on the **Topology > Neighbours** tab. A neighbour without one uses bearer port `0` (change that with `DAPPS_DEFAULT_BEARER_PORT` before first start, or `POST /Config`). Bearer ports are 0-indexed against the order `PORT=N` blocks appear in `XROUTER.CFG` (so `PORT=1` -> 0, `PORT=2` -> 1, etc); DAPPS adds 1 internally to derive RHPv2's 1-indexed port name.

The wizard's **Detect packet node** button probes localhost:9000 and pre-selects RHPv2 automatically when XRouter is on the same host.

Saved settings hot-reload - the daemon binds the listener on the new callsign within a few seconds, no restart needed.

(The `DAPPS_CALLSIGN`, `DAPPS_NODE_BEARER`, `DAPPS_NODE_HOST`, `DAPPS_RHP_PORT`, `DAPPS_RHP_USER`, `DAPPS_RHP_PASS`, `DAPPS_DEFAULT_BEARER_PORT` env vars still work as **first-run seeds** for automated deployments - set them before the daemon's first start and they populate the equivalent rows. After first start, the persisted values win and env vars stop mattering.)

## Step 5: start DAPPS, watch for the listener bind

A successful DAPPS start logs:

```
RHP inbound: connecting to <xrouter-host>:9000
RHP inbound: listener bound to M0LTE-7 on handle <N>
```

If you see repeated reconnect attempts, RHPv2 isn't listening or isn't reachable from DAPPS's host - confirm with:

```bash
nc -vz <xrouter-host> 9000
```

If you see an authentication failure, double-check `DAPPS_RHP_USER` / `DAPPS_RHP_PASS` against your XRouter's RHPv2 credentials.

## Step 6: prove the inbound side

From any other node on the same air:

```
c <your-DAPPS-callsign>
```

You should land at the `DAPPSv1>` prompt. That's DAPPS having taken the inbound L2 dispatch from XRouter via RHPv2. Disconnect.

## Step 7: prove the outbound side

Add a manual neighbour on the dashboard's **Topology > Neighbours** tab:

| Field          | Value                                       |
|----------------|---------------------------------------------|
| Callsign       | another DAPPS node's callsign with SSID     |
| Radio port     | the XRouter port byte to use for the originate |
| UDP endpoint   | leave blank                                 |

Send a test message with the **Quick send** form on the dashboard's Overview. The forwarder service picks it up within a few seconds, asks XRouter to dial the remote callsign, and you'll see the session in XRouter's stats / monitor view.

## Multi-port setups

If your XRouter has multiple radio ports (`PORT=1`, `PORT=2`, etc.), every neighbour record can specify which port to use. The dashboard's add-neighbour form has the field; the discovery system records the port a peer was heard on automatically.

The byte numbering in DAPPS is 0-indexed against the order ports appear in `XROUTER.CFG`. `PORT=1` -> byte 0, `PORT=2` -> byte 1, and so on. DAPPS handles the +1 conversion to RHPv2's port-name shape internally, so the same `BearerPort` value works for both AGW (BPQ) and RHPv2 (XRouter) neighbours.

## Sharing a callsign between XRouter and DAPPS

The recommended pattern is to give DAPPS its own SSID under your callsign. XRouter's NODECALL / CONSOLECALL / CHATCALL / APPL blocks each consume one callsign+SSID; DAPPS consumes one more. Pick a free SSID for DAPPS (often `-7`, `-9`, or whatever else you have spare).

Same-callsign-same-SSID with another XRouter declaration is **not** supported - XRouter will refuse the bind because the callsign is already claimed.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `RHP inbound: connection lost; reconnecting` repeatedly | XRouter isn't listening on `DAPPS_RHP_PORT`, or you've got a firewall in the way. Confirm with `nc -vz <host> <port>`. Check `RHPPORT=` is set in `XROUTER.CFG`. |
| The log stops at `RHP inbound: connecting to`, with no `listener bound` line after it | You're on XRouter 505o. See [XRouter versions](#xrouter-versions). |
| Authentication failure in the log | `DAPPS_RHP_USER` / `DAPPS_RHP_PASS` don't match XRouter's RHPv2 credentials. |
| Inbound `c <DAPPS-callsign>` lands at the XRouter node prompt instead of `DAPPSv1>` | DAPPS isn't successfully bound - either it's not running, or the bind failed (callsign collision with NODECALL / CONSOLECALL / CHATCALL / APPL). Check DAPPS logs. |
| Inbound `c <DAPPS-callsign>` fails with no L2 response | The remote node doesn't have a route to your DAPPS callsign. NetROM/AX.25 routing is XRouter's job, not DAPPS's. |
| Outbound forwards never connect | DAPPS's neighbour has the wrong `bearer port` byte - check the port-numbering note above. |

For more general DAPPS troubleshooting, see the main [Troubleshooting](../troubleshooting.md) page.

## Why not AGW on XRouter?

Short version: XRouter's AGW emulator scopes the callsign claim per-TCP-connection. DAPPS opens a fresh TCP connection per outbound forward; the second connection's `X` register for the DAPPS callsign collides with the standing inbound registration owned by another connection, and XRouter silently drops the subsequent `C` frame. Symptoms: outbound forwards from a DAPPS-on-XR node never connect.

There's no AGW-only fix that doesn't involve a substantial refactor of DAPPS's outbound transport (multiplexing onto a shared inbound socket). RHPv2 doesn't have the per-connection claim - it uses handle-based addressing - so the same multiplexing is what RHPv2 ships natively. That's why DAPPS-on-XRouter goes via RHPv2.
