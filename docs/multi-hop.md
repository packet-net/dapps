# Reaching a peer through intermediate nodes

DAPPS discovers other DAPPS nodes via beacons (RF-direct) and via the `peers` / `routes` commands (transitive among DAPPS-aware peers). Both mechanisms break down when the *only* path between two DAPPS nodes runs through one or more **non-DAPPS** packet nodes.

A typical example: A and C are DAPPS nodes, neither in RF range of the other; B is a regular BPQ packet node that hears both, but B doesn't speak DAPPS and isn't running NET/ROM either. A's beacons never reach C; C is invisible to A's discovery layer. The path *exists* - operator could manually `C B`, then `C C` from B's prompt, and reach C - but A's daemon doesn't know to try.

This page documents the two ways DAPPS bridges that gap: the **node-prompt probe** for the one-intermediate case, and the **connect-script** for arbitrary chains.

## Single intermediate: node-prompt probing

When the intermediate B is a BPQ-style packet node and C's BPQ has DAPPS registered as an APPLICATION, the existing node-prompt probe handles this. A connects to B's AGW slot, types `DAPPS` (or whatever `NodePromptApplicationCommand` is set to), and BPQ's APPLICATION dispatcher routes the connection through to C's DAPPS slot. The probe runs end-to-end on that path; on success, C is added as a known peer.

Turn this on with:

```
DAPPS_AUTO_DISCOVER_VIA_NODE_CALL=true
```

Probing must also be enabled (`DAPPS_PROBING_ENABLED=true`). Once both are on, every AGW DAPPS beacon A hears seeds a node-prompt-probe candidate for the source's base callsign. See [Discovery & routing](discovery-and-routing.md) for the full probe taxonomy.

This handles the one-intermediate case automatically. Where it breaks down: A has to *first hear a beacon from somewhere related to C* for the candidate to land in the probe pool. If C is two hops away through bare packet nodes that don't propagate UI frames, A may never hear that beacon.

## Multi-hop chains: connect-scripts

A connect-script automates the operator's manual `C node1 / C node2 / ... / C <DAPPS callsign>` sequence. It's a property of a **manually-added neighbour** row: when the daemon goes to forward to that neighbour, it connects to the row's callsign, then plays the script over that connection before falling into the DAPPSv1 prompt.

### Topology

```
A (DAPPS)  ←RF→  G0NODE2  ←RF→  G0NODE3  ←RF→  G0NODE4  ←RF→  C (DAPPS, callsign G0DPC-3)
```

A and C can't hear each other. G0NODE2/3/4 are bare packet nodes that don't speak DAPPS. The operator's manual chain is:

```
A: connect to G0NODE2 (AGW)
   "Connected to G0NODE2"
A types: C G0NODE3
   "Connected to G0NODE3"
A types: C G0NODE4
   "Connected to G0NODE4"
A types: C G0DPC-3
   "DAPPSv1>"
```

The last connect goes straight to C's DAPPS callsign. That callsign is always a real callsign on C's node (a BPQ application callsign, an XRouter registration, a pdn listen), so connecting to it lands on DAPPS, which answers with its `DAPPSv1>` prompt at once. Don't follow it with the application command: that would be typed into the DAPPS session.

That sequence becomes a connect-script.

### Configuring a connect-script

#### Example: a peer in radio range

If you can connect to the peer's node on one of your ports, you can connect to the peer's DAPPS callsign on that port too: it's on the same node and radio. No script is needed. For `MB7NPW-3`, a DAPPS node on the BPQ node `MB7NPW` that you reach on your XRouter's `PORT=2`:

- Callsign: `MB7NPW-3`
- Radio port: `1`

#### Example: a peer only reachable over NET/ROM

The application command is for one case: the last node reaches the peer's node over NET/ROM, and the peer's DAPPS callsign isn't in its node table (BPQ only advertises an application callsign given an `APPLQUAL`). Then connect to the peer's node, and type the command that node gives DAPPS:

- Callsign: `MB7NPW-3`
- Connect via: `GB7BDH` (your first node)
- Connect script:

```
C MB7NPW|Connected to MB7NPW
DAPPS|DAPPSv1>|60
```

#### The general case

In the dashboard's **Add / update neighbour** form (or `POST /Neighbours`):

- **Callsign**: the far-end DAPPS node (`G0DPC-3` in the example). Routing, probes, polls and the dashboard know the neighbour by this.
- **Connect via**: the node DAPPS dials first (`G0NODE2` in the example). DAPPS makes this connect itself, over the bearer, before the script starts. Left blank, DAPPS dials the Callsign itself and plays the script at its prompt. Needs a connect script.
- **Radio port**: the port to dial the first node on. It's 0-indexed: on AGW it's BPQ's port byte; on RHPv2 (XRouter) `0` is `PORT=1` in `XROUTER.CFG`, `1` is `PORT=2`, and so on.
- **Connect script**: what you'd type once connected to that node, one step per line, `SEND|EXPECT[|TIMEOUT_SECONDS]`:

```
C G0NODE3|Connected to G0NODE3
C G0NODE4|Connected to G0NODE4
C G0DPC-3|DAPPSv1>|60
```

Notes:

- Each `SEND` is transmitted with a `\r` line terminator (BPQ-style node prompts use CR, not LF).
- Each `EXPECT` is a substring match against the inbound bytes; case-sensitive. Pick something distinctive enough that earlier banner text won't accidentally match.
- `TIMEOUT_SECONDS` is per-step; default 30s. The final step that lands on `DAPPSv1>` may want longer: it waits for a connect over the last link, which can take several tries on a busy or marginal channel.
- The first step is *not* "C G0NODE2" - DAPPS has already connected to it (Connect via). The script picks up at G0NODE2's prompt.
- The script never starts at your own node's prompt: steps such as `SWITCH` or `C 2 G0NODE2` typed at your own node won't be answered. Put the first node you'd connect to in Connect via, and its port in Radio port.
- If you leave Connect via blank and write the script the way you'd type it, starting with the connect to the first node (`C GB7BDH|Connected`), DAPPS takes that line off and stores `GB7BDH` as Connect via when you save. Only a bare `C <CALL>` or `CONNECT <CALL>` is taken: a line with a port number (`C 2 GB7BDH`) or digipeaters is typed at the prompt like any other.
- The script's last step **must** end on a substring containing `DAPPSv1>`; the protocol client takes over from there.

Lines beginning with `#` are comments. Blank lines are ignored.

### What happens on send

When the outbound forwarder picks a message destined for C:

1. Resolves the route to the neighbour row with the connect-script attached.
2. Connects to the row's Connect via (the first hop, G0NODE2; the Callsign if that's blank) on the configured radio port, over the node's bearer (AGW or RHPv2).
3. Plays the script: send line, wait for substring, send line, wait, ... until `DAPPSv1>`.
4. Falls into the regular exchange: route gossip pull (subject to staleness gate), then traffic both ways.
5. On success, all the usual things happen: messages acked, anything the far end has for us collected on the same session, audit log entry.

If any step times out (default 30s) or the stream closes, the script aborts and the forward fails like any other transport failure. The route's failure counter increments; after enough consecutive failures, the daemon falls back to whatever else is available.

### Bidirectional setup

Connect-scripts are one-sided: configuring A's script for C lets A push to C. For C to reach A, C also needs a connect-script (for the reverse chain) - configured by the operator at C, the same way.

What you get for free, once messages flow either way:

- **Reverse passive learning**. The first message A pushes to C carries `src=A`; C's passive-flood algorithm learns A as a route. C can now reply to A via the gossip-imported route, no separate operator config required.
- **Route gossip propagation**. A's next session with C piggybacks a `routes` pull (subject to the per-neighbour staleness gate, default 6h). C tells A about whatever destinations C can reach; A learns about peers behind C without needing to script every chain.

### Probes use the same script

Once a neighbour has a connect-script, both forwarder *and* probes (when probing is enabled) play it. A green probe indicates the chain is currently working end-to-end - same liveness signal as for direct neighbours, with no special handling required by the operator.

### Dashboard / inspection

The Neighbours panel's "Script" column shows a neighbour's script lines, with `via G0NODE2` above them when it has a Connect via. **edit** on a row loads it back into the form; save to update it, or empty the connect-script and Connect via boxes to clear them (the row falls back to direct connection).

A failed script run logs each step's last 200 chars of received text via the daemon's normal logging channel, so the operator can see exactly which expect didn't match.

### When *not* to use a connect-script

If B is itself a DAPPS node, or if B runs NET/ROM and has C in its routes table, you don't need a script - DAPPS can either reach C via the existing single-step node-prompt probe or via NET/ROM transparent routing. Connect-scripts are specifically for chains of *bare* packet nodes where the operator would otherwise be typing the chain by hand.
