# Sessions to several peers behind one node

When a DAPPS node reaches several peers through the same packet node, mail for them can go one peer at a time: connect, send, hold, disconnect, then the next. Whether the sessions can run at the same time depends on three things: whether the interface DAPPS uses can start a NET/ROM connection, whether it can tell the sessions apart, and whether the link underneath can carry more than one. AX.25 L2 allows only one link per pair of callsigns on a port, while NET/ROM carries many circuits over one node link. Since 0.57.0 a quiet session gives way after 10 s when other mail is waiting, which roughly halves the wait. Since 0.58.0 each such neighbour can have its own Dial as SSID, so their sessions run at once ([Tune](tune.md#several-peers-behind-one-node)). In a lab replica of MB7NPW's network ([#430](https://github.com/packet-net/dapps/issues/430)) that halved both the slowest 10% of messages and the time for a burst; typical messages already took 3 to 8 s.

This page records what was learned in [#425](https://github.com/packet-net/dapps/issues/425), so the facts don't have to be found again. Start with [Troubleshooting](troubleshooting.md#mail-for-neighbours-behind-one-node-goes-one-at-a-time) if you just want to know what your node is doing.

## The case that started it

MB7NPW (Kevin) reaches three DAPPS peers, GB7AGM, GB7LOX and EI7KX, through one node, CALNE. On 0.56 the mail for them went one peer at a time, each session sitting out its 120 s hold before the next could start. A burst for all three took about 7 minutes in simulation, nearly all of it waiting.

MB7NPW's own logs on 0.56.1 showed that every session to CALNE was already a NET/ROM circuit over one node link, and that DAPPS held mail back 391 times in three hours because it assumed an L2 link that would be reset. So the one-at-a-time behaviour there was DAPPS waiting on itself, not a limit of the node or of AX.25.

## Three questions decide it

| Question | Why it matters |
|---|---|
| Can the interface start a NET/ROM connection? | NET/ROM gives each circuit its own number, so many sessions share one node link, even from the same callsign. |
| Can the interface tell the sessions apart? | If two sessions look the same to the interface, their data gets mixed up or sent to the wrong one. |
| Can the link underneath carry two? | AX.25 L2 has one link per callsign pair per port. A second connect (SABM) on an open link resets it. |

How each node handles a second L2 connect for a pair already up:

- **LinBPQ** reuses the existing link and resets it. The first session gets no disconnect, and its replies arrive on the second one.
- **XRouter RHPv2** refuses it (error 9, "Duplicate socket"). XRouter AGW ignores it. The first session is unharmed.
- **pdn RHPv2** refuses it (error 18, "Already connected"). The first session is unharmed.

### How the interfaces reach NET/ROM

- **LinBPQ never lets an interface open a NET/ROM circuit directly.** Only the node's own connect command does. An AGW connect is turned into a `C <port> <call>` typed into the node, and the node picks NET/ROM if the call is in its NODES table, ignoring the port. So an AGW dial to a call in NODES is already NET/ROM; a dial to a call that isn't (a DAPPS application call, usually) is plain L2.
- **LinBPQ AGW tells sessions apart only by port, from-call and to-call.** AGW frames carry no session id, so this is a limit of the protocol, not a bug. Two sessions to CALNE from the same call are created but every send goes to the first. Also, LinBPQ AGW puts only the first callsign registered on a socket on air.
- **LinBPQ AGW connect to `SWITCH`** lands at the node's own prompt with nothing on air. Distinct keys `SWITCH-1`, `SWITCH-2` keep the sessions apart, and typing `C CALNE` in each gives separate NET/ROM circuits.
- **LinBPQ RHPv2 `open`** parses the remote callsign and port but never uses them: every open lands at the node prompt, with its own handle. Two sessions typing `C CALNE` get two independent circuits.
- **XRouter RHPv2 `open`** does connect to the remote, and `pfam: netrom` opens a NET/ROM circuit. XRouter AGW has no NET/ROM path: a connect to the node's own call goes out on air.
- **pdn RHPv2 `open`** with no port (0.68.0 on) routes over NET/ROM.

### Which paths can run in parallel

| Path from DAPPS to a peer behind CALNE | NET/ROM? | Sessions apart? | Link carries two? | Parallel? |
|---|---|---|---|---|
| LinBPQ AGW connect to CALNE | Yes, if CALNE is in NODES | **No** | Yes | No |
| LinBPQ AGW to `SWITCH-n`, then type `C CALNE` | Yes | Yes | Yes | **Yes** |
| LinBPQ RHPv2 open, then type `C CALNE` | Yes | Yes | Yes | **Yes** |
| LinBPQ RHPv2 open straight to CALNE | Remote is ignored | | | Not today |
| XRouter RHPv2 open, `pfam: netrom` | Yes | Yes | Yes | **Yes** |
| XRouter AGW | No | | | No |
| pdn RHPv2 open with no port | Yes | Yes | Yes | **Yes** |
| Any interface over L2 to CALNE | No | | One link per pair | Only if each session uses a different source callsign |

DAPPS doesn't speak LinBPQ's RHP (a websocket at `/rhp`, not TCP RHPv2), so on LinBPQ the AGW rows are the ones that matter.

### Which callsign the far peer sees

| Last hop to the peer | DAPPS call G9APP-3 arrives as |
|---|---|
| Direct L2, or a NET/ROM circuit that ends at the peer's node | G9APP-3 |
| L2 downlink from a LinBPQ or XRouter node | G9APP-12 (SSID flipped to 15 minus SSID) |
| L2 downlink from a pdn node | The pdn node's own call |

The flip is why GB7AGM calls MB7NPW as GB7AGM-12. Since 0.56.0 both ends send `node=` in their exchange ([#422](https://github.com/packet-net/dapps/pull/422)), so a flipped SSID is recognised as the same node. A pdn last hop changes the base callsign, so `node=` can't help, and two downlinks from one pdn node to the same peer collide.

## What the rigs measured

Both rigs are on [#426](https://github.com/packet-net/dapps/pull/426) (test code only).

**Rig A** ran four real nodes in containers (LOCAL, CALNE, PEER1, PEER2) over AX.25 on UDP with NET/ROM, as all LinBPQ, all XRouter, all pdn and a mixed chain. It established the facts above: parallel sessions work on all three node types when each session goes through the local node and NET/ROM; a second plain L2 session for a pair already up never works.

**Rig B** ran Kevin's layout in [net-sim](https://github.com/packet-net/net-sim) with real LinBPQ and real DAPPS daemons: 18 messages for three peers, either all at once (burst) or handed in over 8 minutes (spread). Median of several runs:

| Setup | Burst: last delivery | Spread: median wait |
|---|---|---|
| 0.56.0, 120 s hold, L2 to CALNE | 415 s | 244 s |
| 0.56.0, hold cut to 10 s | 196 s | 41 s |
| 0.56.0, NET/ROM to CALNE, still one at a time | 410 s | 186 s |
| Prototype: three sessions at once over NET/ROM (`SWITCH-n` keys) | 146 s | 15 s |
| 0.57.0 (quiet session gives way after 10 s) | 198 s | 29 s |

What it says:

- **Most of the delay was idle hold time.** The data itself took about 120 s of air in every setup. A short hold gets about half the gain; parallel sessions get roughly a further quarter, with the channel going from 30% to about 80% busy.
- **Parallel sessions must not redial a peer through the via while its old circuit is open.** On a LinBPQ CALNE both downlinks to the peer share one L2 link, so the second resets it and closing the first kills the second.
- **The 30 s "no answer to routes" limit is tight** on a channel shared three ways. It fired once on a healthy, busy channel.
- **Nothing was lost or duplicated** in any run.

Rig B modelled MB7NPW to CALNE as a busy AFSK 1200 channel (about 120 s of airtime for 18 messages), and CALNE to each peer as AXUDP. The real network differs both ways: the first hop is a much faster NPR link, but every session then crosses CALNE's shared 70 cm 4k8 channel (see the survey below). Use the times for comparing setups only.

A later Rig B run checked [#419](https://github.com/packet-net/dapps/pull/419) (a slow dial no longer holds up every other neighbour). In Kevin's layout it made no difference once 0.57.0's give-way was in; with a slow via and direct neighbours it kept the direct neighbours' burst at 13 to 15 s instead of about 77 s. It stays.

## LinBPQ node links drop on a busy channel

In about half of Rig B's NET/ROM runs the LinBPQ node link between MB7NPW and CALNE stalled or reset, losing every circuit on it. net-sim delivered every frame once and in order; the faults are in LinBPQ's L2 code and would happen on air between two LinBPQ nodes on default settings whenever the channel is busy both ways:

1. With poll answers arriving late, the sender resends a batch the far end already has.
2. On an AX.25 2.2 link the receiver keeps those repeats as future frames, sends SREJ for a frame never sent, then delivers the stale frames as new data.
3. A node acknowledged for frames it never sent goes silent on the link and later sends DISC.
4. A safety counter meant for a dead link counts every frame that brings no new ack, so one long burst from the other side drops a healthy link (any AX.25 version).

NET/ROM's own sequence numbers kept DAPPS's data intact: messages were delayed, never corrupted.

- **Workaround on real nodes:** turn AX.25 2.2 off on the RF node link, with `NOV2.2=1` on the neighbour's ROUTES line or `OnlyVer2point0=1`. In the rig that cut resets from 4 in 9 runs to 1 in 9.
- **Fixes:** items 2 to 4 are fixed in the M0LTE/linbpq fork ([M0LTE/linbpq#81](https://github.com/M0LTE/linbpq/pull/81)) and reported upstream ([M0LTE/linbpq#82](https://github.com/M0LTE/linbpq/issues/82)). Item 1 still wastes airtime.

The same receiver fault was seen on air against GB7RDG in September ([packethacking/ax25spec#40](https://github.com/packethacking/ax25spec/issues/40)).

## MB7NPW's network (surveyed 2026-10-31)

Surveyed on air by hopping GB7RDG -> GB7BSK -> GB7WIN -> MB7NPW and using read-only node commands only.

MB7NPW (alias PEWSEY) runs LinBPQ. Its ports:

| Port | What |
|---|---|
| 2 | 3K6 IL2Pc, local sysop access |
| 4 | 70.3125 MHz 4K8 IL2Pc (towards GB7BDH) |
| 7 | 430.200 MHz 9K6 IL2Pc |
| 8 | Telnet |
| 9 | "NPR3.0 Link to CALNE" |

- **CALNE** (M1BFP-1) runs XRouter on a Raspberry Pi. It is a direct neighbour on port 9, about 0.5 s round trip.
- **GB7AGM** (AGMNOD, XRouter on Linux, Clavering, Essex) and **GB7LOX** (LOXLEY) are in MB7NPW's NODES, but NET/ROM's best route to both is via GB7BDH on the slow 4 m link, with CALNE only as the alternative.
- **EI7KX** is not in MB7NPW's NODES at all.
- **The DAPPS application calls** (GB7AGM-3, GB7LOX-6, EI7KX-3) are not in NODES.

GB7AGM calls in as GB7AGM-12 (LinBPQ's 15 minus SSID flip).

CALNE was surveyed the same day, one hop further ([#433](https://github.com/packet-net/dapps/issues/433)). It has **no direct link to any of the three peers**. Its only neighbours are:

| CALNE port | Neighbour |
|---|---|
| 1 | MB7NPW, over NPR |
| 2 | GB7BDH (SWINDN, XRouter), 70 cm 4k8 NinoTNC, shared with user access |
| 5 | G8PZT (KIDDER), AXUDP over the internet |

From CALNE, the best route to GB7AGM is back via MB7NPW (3 hops), and to GB7LOX via GB7BDH on port 2 (3 hops). EI7KX isn't in CALNE's nodes either, so how the scripts reach it from CALNE is unknown; Kevin's actual scripts would settle it.

So going via CALNE avoids MB7NPW's own slow 4 m link to GB7BDH, but the path is not NPR end to end: every session still crosses a shared 4k8 radio channel beyond CALNE.

## What has shipped

- **0.56.1** ([#427](https://github.com/packet-net/dapps/pull/427)): at start-up DAPPS asks its own node whether each Connect via is in NODES, and logs what `LINKS` shows while mail waits on a busy link. Nothing goes on air. See [Troubleshooting](troubleshooting.md#mail-for-neighbours-behind-one-node-goes-one-at-a-time).
- **0.57.0** ([#428](https://github.com/packet-net/dapps/pull/428)): a quiet session we dialled ends after 10 s, instead of its full hold, when mail for another neighbour waits for the link it holds. See [Tune](tune.md#polling).
- **#419** kept, after measuring it with and without.

## Options for parallel sessions (not decided)

1. **Dial each neighbour from its own source SSID** ([#431](https://github.com/packet-net/dapps/issues/431)), each on its own AGW connection, plus long holds. Different source callsigns mean different L2 links, so this gives parallel, persistent sessions on any node type, over L2 or NET/ROM. `node=` already keeps the identity straight. This is the recommended option.
2. **DAPPS types the connect at its own node's prompt** (LinBPQ AGW `SWITCH-n`, or LinBPQ RHPv2). Proven in Rig B, but LinBPQ only, and it means parsing the node's text replies.
3. **LinBPQ RHPv2 `open` honours Remote and Port**, as XRouter's does ([#432](https://github.com/packet-net/dapps/issues/432)). Needs an upstream change.
4. **Config-only stopgap:** the peers dial in and hold their sessions open, so each arrives on its own link.

## Open follow-ups

- [#430](https://github.com/packet-net/dapps/issues/430): a lab replica of MB7NPW's network, with the real link speeds.
- [#431](https://github.com/packet-net/dapps/issues/431): dial a neighbour from its own source SSID (option 1).
- [#432](https://github.com/packet-net/dapps/issues/432): ask LinBPQ to honour Remote and Port on RHPv2 open.
- How the via-CALNE scripts reach EI7KX (not in CALNE's nodes); Kevin's scripts would settle it.
- [M0LTE/linbpq#82](https://github.com/M0LTE/linbpq/issues/82): the LinBPQ L2 faults, for G8BPQ.
- XRouter and pdn didn't exchange NODES in Rig A's mixed chain (XRouter counted every pdn broadcast as an L3 error). Not yet investigated.

## Evidence

Line numbers are LinBPQ source as of the DAPPS test pin (M0LTE/linbpq edcc798, 6.0.25.41); the fork matches upstream in these files.

- **One L2 link per callsign pair per port:** AX.25 2.3 draft, section 2.7 and section 6.
- **LinBPQ reuses and resets an existing link:** `Cmd.c` `FindLink` (about line 2538) and its use in the connect command (about 3120).
- **Only the node command opens NET/ROM:** `DoNetromConnect` is called only from `Cmd.c` 957, 2856, 2870. `Cmd.c` checks NODES before the downlink code, so a port given with a call in NODES is ignored.
- **LinBPQ AGW connect:** types `C <port> <call>` into the node, `AGWAPI.c` 1171 to 1236. Session lookup by (port, CallFrom, CallTo) only, 1506 to 1518; a duplicate is created but sends go to the first match, 1258 to 1265. Version reply 2003.999, 1148.
- **LinBPQ RHPv2 ignores Remote and Port:** `RHP.c` 428 and 821. Two node sessions typing `C CALNE` get independent circuits: `Cmd.c` 2440 to 2470, `L4Code.c` 1891 to 1935.
- **XRouter RHPv2 duplicate refusal:** error 9, handled in `Rhpv2OutboundTransport.cs` (about line 227).
- **LinBPQ L2 faults** (`L2Code.c`): repeats stored as future frames with no window check, 2586 and 2647; silent after a bad N(R) with `FRMRHACK`, 2978 to 2982, 2279, 2531, 3879; safety counter in `ACKMSG`, 3702 to 3707; a poll on every batch end, 3413 to 3416, and V(S) rewound on any F, 2491 to 2497. Reproduction: `Integration/Rig425/RigB/linbpq-repro/` on #426, and `tests/integration/helpers/l2_repro.py` on M0LTE/linbpq#81.
- **Rig results:** all on [#425](https://github.com/packet-net/dapps/issues/425): Rig A results, the option 1 plan, Rig B results, MB7NPW 0.56.1 logs, the SREJ investigation and the #419 comparison. Rig B per-run data in `results/*.jsonl` on the #426 branch.
