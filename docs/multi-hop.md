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

A connect-script automates the operator's manual `C node1 / C node2 / ... / DAPPS` sequence. It's a property of a **manually-added neighbour** row: when the daemon goes to forward to that neighbour, it plays the script over the AGW connection before falling into the DAPPSv1 prompt.

### Topology

```
A (DAPPS)  ←RF→  G0NODE2  ←RF→  G0NODE3  ←RF→  G0NODE4  ←RF→  C (DAPPS)
```

A and C can't hear each other. G0NODE2/3/4 are bare packet nodes that don't speak DAPPS. The operator's manual chain is:

```
A: connect to G0NODE2 (AGW)
   "Connected to G0NODE2"
A types: C G0NODE3
   "Connected to G0NODE3"
A types: C G0NODE4
   "Connected to G0NODE4"
A types: C C
   "Connected to C"
A types: DAPPS
   "DAPPSv1>"
```

That sequence becomes a connect-script.

### Configuring a connect-script

In the dashboard's **Add / update neighbour** form (or `POST /Neighbours`):

- **Callsign**: the far-end DAPPS node (`C` in the example).
- **Bearer port**: the AGW port to use for the *first* hop (G0NODE2).
- **Connect script**: one step per line, `SEND|EXPECT[|TIMEOUT_SECONDS]`:

```
C G0NODE2|Connected
C G0NODE3|Connected to G0NODE3
C G0NODE4|Connected to G0NODE4
C C|Connected to C
DAPPS|DAPPSv1>|60
```

Notes:

- Each `SEND` is transmitted with a `\r` line terminator (BPQ-style node prompts use CR, not LF).
- Each `EXPECT` is a substring match against the inbound bytes; case-sensitive. Pick something distinctive enough that earlier banner text won't accidentally match.
- `TIMEOUT_SECONDS` is per-step; default 30s. The final step that lands on `DAPPSv1>` may want longer because the application command takes a moment to dispatch on the far-end node.
- The first step, `C G0NODE2`, names the node the daemon dials over AGW. It has to be a bare `C <CALL>` (or `CONNECT <CALL>`) with no port number. The daemon dials that callsign itself instead of typing the line, and a successful AGW connect counts as the step passing, so its expect text is not checked. The rest of the script is played from G0NODE2's prompt.
- If the first step isn't a bare `C <CALL>`, for example `C 14 G0NODE3` with a port number, the daemon dials the neighbour's own callsign and plays the whole script over that connection.
- The script's last step **must** end on a substring containing `DAPPSv1>`; the protocol client takes over from there.

Lines beginning with `#` are comments. Blank lines are ignored.

### What happens on send

When the outbound forwarder picks a message destined for C:

1. Resolves the route to the C-neighbour row, which has the connect-script attached.
2. Opens an AGW connection to the *first hop* named by the script's first step (G0NODE2) via the configured bearer port.
3. Plays the rest of the script: send line, wait for substring, send line, wait, ... until `DAPPSv1>`.
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

The Neighbours panel shows a "Connect script" column with the step count for each neighbour. Re-submit the form with the same callsign to update the script; submit with the connect-script box empty to clear it (the row falls back to direct connection).

A failed script run logs each step's last 200 chars of received text via the daemon's normal logging channel, so the operator can see exactly which expect didn't match.

### When *not* to use a connect-script

If B is itself a DAPPS node, or if B runs NET/ROM and has C in its routes table, you don't need a script - DAPPS can either reach C via the existing single-step node-prompt probe or via NET/ROM transparent routing. Connect-scripts are specifically for chains of *bare* packet nodes where the operator would otherwise be typing the chain by hand.
