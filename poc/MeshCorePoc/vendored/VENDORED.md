# Vendored from dapps.client

These three files are **copied verbatim** from `src/dapps/dapps.client/Backhaul/`
at repo commit `58a5db3` so the PoC can prove the *actual* DAPPS wire format
round-trips over a real MeshCore private channel, without taking a project
reference on `dapps.client` (which would pull in central package management,
RhpV2.Client, logging, etc. and stop this being a standalone PoC).

- `BackhaulMessage.cs`      ← dapps.client/Backhaul/BackhaulMessage.cs
- `BackhaulMessageCodec.cs` ← dapps.client/Backhaul/Datagram/BackhaulMessageCodec.cs  (codec v7)
- `Packetiser.cs`           ← dapps.client/Backhaul/Datagram/Packetiser.cs            (13-byte fragment header)

**When this graduates into DAPPS proper**, delete this folder and reference
`dapps.client` directly — the MeshCore bearer is meant to reuse these, not fork them.
