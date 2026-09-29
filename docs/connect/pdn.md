# Connect via pdn (packet.net)

[pdn](https://github.com/packet-net/packet.net) is the packet.net node. DAPPS runs on it as an app: pdn installs it, starts and supervises it, gives it a callsign, and puts its dashboard on a tile in pdn's control panel. DAPPS talks to the node over [RHPv2](rhp.md).

## Run DAPPS as a pdn app

**Check the version first.** pdn's built-in app catalogue installs DAPPS 0.34.3 (as of pdn node-v0.55.2). That's older than DAPPS 0.40, which brought the exchange between nodes, and older than the fix that keeps DAPPS from losing the other node's greeting on pdn (in the release after 0.42.0). Until the catalogue is updated, put a current DAPPS in place by hand (step 2 below), or [run DAPPS on its own](#run-dapps-on-its-own-against-pdn).

**1. Turn on pdn's RHPv2 server.** It's off by default. Add this to pdn's config (the control panel's **Config** screen, or `packetnet config export` / `import`):

```yaml
rhp:
  enabled: true
```

That listens on `127.0.0.1:9000`, which is all DAPPS needs when it runs on the node.

**2. Install DAPPS.** From the control panel's **Apps** screen you get the catalogue's 0.34.3. For a current release, put its pdn package in `/var/lib/packetnet/apps/dapps/` yourself (use `dapps-linux-x64` on a PC, `dapps-linux-arm` on 32-bit Raspberry Pi OS); this also replaces a catalogue install:

```sh
sudo -u packetnet mkdir -p /var/lib/packetnet/apps/dapps
cd /var/lib/packetnet/apps/dapps
sudo -u packetnet curl -fsSLO https://github.com/packet-net/dapps/releases/latest/download/pdn-app.yaml
sudo -u packetnet curl -fsSL -o dapps https://github.com/packet-net/dapps/releases/latest/download/dapps-linux-arm64
sudo chmod 755 dapps
```

Then enable it on the **Apps** screen. pdn gives it your node's callsign with a free SSID. To choose the callsign yourself, add it to pdn's `apps:` block:

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
| Default bearer port | `0`, for pdn's first port (see below) |

If DAPPS is on another machine, pdn's `rhp.bind` has to be an address that machine can reach. RHPv2 has no encryption, so keep it on a network you trust. If you set `requireAuth: true`, give DAPPS a pdn username and password as its RHPv2 user and password.

## Which port DAPPS uses

DAPPS asks pdn for a port by number, one more than its bearer port: bearer port `0` is pdn's first port, `1` its second, in the order your config lists them. pdn node-v0.56.0 and later take a port's number as well as its name, so the port can have any name.

Older pdn releases only take a port's name ([packet.net#841](https://github.com/packet-net/packet.net/issues/841)). On those, name the port DAPPS uses `1`, or `2` if you set DAPPS's bearer port to `1`, and so on:

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
- **Linking pdn to a BPQ node over AXIP:** one `MAP` line in BPQ for pdn's address, for the DAPPS callsign, is all DAPPS needs. With a second line for the same address (the node's callsign as well), BPQ sends every frame twice. pdn node-v0.56.0 and later cope with that; on older pdn the link never comes up ([packet.net#842](https://github.com/packet-net/packet.net/issues/842)).
