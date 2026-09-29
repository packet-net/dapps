# Connect via pdn (packet.net)

[pdn](https://github.com/packet-net/packet.net) is the packet.net node. DAPPS runs on it as an app: pdn installs it, starts and supervises it, gives it a callsign, and puts its dashboard on a tile in pdn's control panel. DAPPS talks to the node over [RHPv2](rhp.md).

## Run DAPPS as a pdn app

This is the easy way.

**1. Turn on pdn's RHPv2 server.** It's off by default. Add this to pdn's config (the control panel's **Config** screen, or `packetnet config export` / `import`):

```yaml
rhp:
  enabled: true
```

That listens on `127.0.0.1:9000`, which is all DAPPS needs when it runs on the node.

**2. Install and enable DAPPS** from the control panel's **Apps** screen. pdn gives it your node's callsign with a free SSID. To choose the callsign yourself, add it to pdn's `apps:` block:

```yaml
apps:
  - id: dapps
    enabled: true
    callsign: M0LTE-7
```

**3. Open DAPPS** from its tile, and add your neighbours on its dashboard.

## Run DAPPS on its own against pdn

If you installed DAPPS separately (say from the [apt repo](../install/index.md)), point it at pdn in the `/Setup` wizard or **Edit configuration**:

| Field | Value |
|---|---|
| Node bearer | **RHPv2** |
| Node host | pdn's host (`localhost` if it's the same machine) |
| RHPv2 port | `9000`, or whatever pdn's `rhp.port` says |
| Default bearer port | `0` for pdn's first port, `1` for its second, and so on |

If DAPPS is on another machine, pdn's `rhp.bind` has to be an address that machine can reach. RHPv2 has no encryption, so keep it on a network you trust. If you set `requireAuth: true`, give DAPPS a pdn username and password as its RHPv2 user and password.

## Name your first port `1` (for now)

DAPPS asks pdn for a port by number, but current pdn releases only accept a port's name (its `id`). Until that's fixed ([packet.net#841](https://github.com/packet-net/packet.net/issues/841)), name the port DAPPS uses `1`:

```yaml
ports:
  - id: "1"
    transport:
      kind: kiss-tcp
      host: 127.0.0.1
      port: 8001
```

## Radio port settings

As with BPQ ([Tune](../tune.md)), your radio port's timers matter more than anything in DAPPS. These are pdn's names for the settings we tested on a simulated 2 m FM channel:

| pdn setting | AFSK 1200 | QPSK 3600 | BPQ name |
|---|---|---|---|
| `ax25.t1Ms` | 7000 | 4000 | `FRACK` |
| `ax25.t2Ms` | 1000 | 1000 | `RESPTIME` (pdn's default is 3000, which holds every acknowledgement back 3 s) |
| `ax25.windowSize` | 4 | 7 | `MAXFRAME` |
| `ax25.n1` | 120 | 236 | `PACLEN` |
| `kiss.persistence`, `kiss.slotTime` | 64, 10 | 64, 10 | `PERSIST`, `SLOTTIME` |
| `kiss.txDelay` | As short as your radios allow (units of 10 ms) | | `TXDELAY` |

pdn always sends your TNC a TX tail, 0 unless you set `kiss.txTail`. A software modem such as Dire Wolf usually wants one: 10 (100 ms) is Dire Wolf's own default.

If the neighbour is a BPQ node, set `link: dial: v20` on that port, as pdn's own docs say: many BPQ builds ignore the newer connect pdn tries first.

## Good to know

- **When both nodes call each other at once**, pdn joins the two calls into one link. DAPPS can't see that over RHPv2, so each end waits 10 s for a greeting before carrying on. Nothing is lost; it just takes 10 s longer.
- **Linking pdn to a BPQ node over AXIP:** give BPQ one `MAP` line for pdn's address, for the DAPPS callsign. With two lines for the same address (the node's callsign as well), BPQ sends every frame twice and the link never comes up ([packet.net#842](https://github.com/packet-net/packet.net/issues/842)).
