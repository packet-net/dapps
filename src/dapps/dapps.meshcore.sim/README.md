# dapps.meshcore.sim — simulated multi-hop MeshCore fabric

An in-process model of a **multi-hop MeshCore mesh** that backs the **real** bearer
classes (`MeshCoreCompanionBackhaul` + `MeshCoreInbound` + transport + reliability) via
the `IMeshCoreLink` seam. It exists to test the things two directly-adjacent bench
radios **can't** reach — multi-hop flood propagation, dedup across paths, flood-scope
containment, reliability over several hops, and passive-discovery convergence — with
**no RF**, deterministically, in CI.

## Why

The bench has two Companion radios that hear each other in one hop. The MeshCore design
centre is the opposite: sparse DAPPS nodes with long strings of relays between them. The
firmware is a fixed, source-verified dependency (64-hop cap, 160-entry no-expiry dedup
ring, transport-code scope drop), so what actually needs scale-testing is **our** logic
running on top of that behaviour. This fabric reproduces the verified firmware behaviour
and runs the unchanged bearer stack over arbitrary topologies.

## Pieces

- **`MeshFabric`** — nodes (`Leaf` = a DAPPS Companion; `Relay` = a Repeater/Room-Server)
  connected by optionally-lossy undirected edges. `Flood` propagates a datagram hop by
  hop: every node that hears it delivers to its app (leaves only), dedups by packet id,
  and re-floods only if it's an in-scope relay within the hop cap. The bearer's per-frame
  rolling nonce means distinct messages never collide (both flood) while one flood
  arriving by two paths is a single id (deduped) — the exact property under test.
- **`SimulatedMeshCoreLink : IMeshCoreLink`** — `SendDataAsync` hands the payload to the
  fabric; received datagrams queue for `DrainAsync`; `MessageWaiting`/`PacketHeard` fire
  as the real link's do. Drop-in for `MeshCoreLink`.
- **`MeshDappsNode`** — a full DAPPS node (the real backhaul + inbound + reliability +
  discovery) on a fabric leaf, with a recording inbox. `SendAsync` originates traffic;
  `Delivered` / `DistinctSeqs` / `DiscoveredPeers` expose what it received/learned.

## Example

```csharp
var f = new MeshFabric();
f.AddRelay("R1"); f.AddRelay("R2"); f.ConnectChain(["R1", "R2"]);
var a = new MeshDappsNode(f, "GB7A-1");
var b = new MeshDappsNode(f, "GB7B-1");
f.Connect("GB7A-1", "R1");           // A ── R1 ── R2 ── B  (two relay hops)
f.Connect("GB7B-1", "R2");

using var cts = new CancellationTokenSource();
var loops = Task.WhenAll(a.RunAsync(cts.Token), b.RunAsync(cts.Token));
await a.SendAsync("GB7B-1", seq: 0, "hello over the mesh", cts.Token);
// ... await delivery, then assert b.Delivered / b.DiscoveredPeers ...
cts.Cancel();
```

See `MeshFabricTests` (fast, deterministic flood/dedup/scope + transport-over-multi-hop)
and `MeshFabricScenarioTests` (the full bearer stack over a relay backbone) in
`dapps.core.tests`.

## Scope / limits

- Models the **channel**, not the serial link's liveness — the simulated link is always
  healthy (watchdog/recovery is out of scope).
- No-loss runs are fully deterministic and fast; **loss-recovery** exercises reliability
  resends on a multi-second cadence, so drive those from a longer harness run rather than
  a CI unit test.
- Propagation is instantaneous (no per-hop latency model yet) — correctness (delivery,
  dedup, containment) is faithful; fine-grained timing/contention is not.
