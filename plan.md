# DAPPS roadmap

Living planning document. Aim is to get DAPPS into the hands of node operators with enough confidence and documentation that they can run it, and to give app developers a guide they can build against.

## Where we are now

The protocol is fully specified (`README.md`'s "On-air protocol" section, including F4 versioning policy). The implementation matches the spec; the on-air format is byte-validated against real BPQ in CI via `m0lte/linbpq` (Testcontainers-managed). Local apps talk to DAPPS via MQTT (durable, idempotent on `dapps-id`) or REST. TTL forwarding works across multi-hop topologies. F1 end-to-end source tracking, F2 multi-part messages, F3 `rev` polling (opportunistic + scheduled) all done. B5 passive-learning routing + B5.1 MeshCore-flavoured DSR alternative both shipped. B6.1 connected-mode probe-and-map with transitive `peers` discovery (probe results now feed B5's learned-route graph too), B6.2 HF-NVIS solicit-and-listen (on-demand + scheduled cadence), B7 single-counter discovery airtime budget with probe strategies, all live. **H3 RHPv2 bearer** for XRouter is done (DAPPS_NODE_BEARER=rhpv2; mainline BPQ RHP still awaiting upstream support). C1 single-file native binaries publish via CI on five platforms; C2 env-var seeding plus `dapps --show-config`; C3 `/Health`, `/Operational`, decision-events ring + structured journal mirror, MQTT heartbeat; C4 install/upgrade docs in the README; C5.1 update-availability banner + C5.2 triggered self-update via privileged `dapps-updater.service`. D1-D4 dashboard MVP. E1-E4 developer guide complete. Phase M (#81-#85) - MCP server at `/mcp` with 26 operator-facing tools. **Master TX kill-switch** - operator stop button + dev-time centralised URL signal, both enforced at the bearer chokepoints (AGW frame / RHP open / UDP send), removed before 1.0 (see "Open tasks" entry).

What's left is in the "Suggested ordering" near the bottom plus the open H bearer-integration phase. The biggest remaining items are external-blocked (H1/H2 MeshCore hardware; H3 mainline-BPQ RHP awaiting upstream support - the XRouter half of H3 has shipped), parked (C5.3 scheduled auto-update - banner + one-click + `trigger_update` MCP cover today's operator population; F5 signing - design parked, see the F5 section for the recommended shape when it gets pulled forward), or not-yet-prioritised (Phase G second-language reference impl, scratchpad phone messenger app).

## Tom's scratchpad of ideas

Active forward-looking work is tracked as GitHub issues now. The bullets below are the historical scratchpad; the open ones link to their tracking issue. Resolved bullets stay struck-through for context.

- ~~when looking at long distance routing what about looking into the routing implementation in Meshcore?~~ *(actioned - see B5.1; selectable alongside the default passive-flood stack)*
- what about using Meshcore as a transport? *(tracked as #137 alongside Phase H1+H2)*
- shipping a real end-user app - phone messenger or long-form mail app *(tracked as #135)*
- ~~RHP (v2?) support~~ *(done for XRouter via `RhpV2.Client` 0.2.2; mainline BPQ RHP still upstream-blocked)*
- ~~MCP server endpoint exposing some DAPPS surface to LLMs~~ *(done - see Phase M)*
- ~~**Global airtime budget for discovery.**~~ *(actioned - see B7 below)*
- ~~**Probe strategies, not bare intervals.**~~ *(actioned - see B7 below)*
- **Collaborative route gossip** - share route state with neighbours, HF NVIS / QO-100 broadcast bearers, fountain codes for the unacked shape. *(tracked as #136 alongside Phase B8 below)*
- **Headless / scripted provisioning API** - one-shot bootstrap endpoint so sim scripts, fleet provisioning, CI, and first-boot init don't have to navigate the operator-facing Razor wizard. *(tracked as #134)*

## Open tasks (issues filed)

- **#5** - Switch integration tests from raw `Process` to Testcontainers. *(done)* `LinbpqIntegrationFixture` and `TwoInstanceLinbpqFixture` now use `Testcontainers.NET`'s `ContainerBuilder`. The previous attempt was abandoned because Testcontainers' default port-readiness probe gave up before BPQ finished binding AGW; resolved by an explicit `Wait.ForUnixContainer().UntilPortIsAvailable(8000)` (60-s default timeout, plenty for BPQ's slow start) plus a 2-s post-bind grace matching the previous shell-out fixture. Bind-mounted bpq32.cfg replaced with `WithResourceMapping(bytes, "/data/bpq32.cfg")` - no host temp dir, no root-owned-file chown sidecar. Two-instance shared-netns uses `WithCreateParameterModifier(p =&gt; p.HostConfig.NetworkMode = "container:&lt;A's id&gt;")` since `WithNetworkMode` isn't surfaced directly. All 5 integration tests pass against real BPQ in 21 s.

- **No automatic forwarder loop** *(fixed)*. `OutboundMessageManager.DoRun` was only triggered by an explicit POST to `/Message/dorun`, so messages would queue and sit until an operator poked the API. Fixed with `OutboundForwarderService` - a `BackgroundService` that ticks `DoRun` every 5 seconds (after a 3-second startup grace). A `SemaphoreSlim` on `DoRun` makes concurrent triggers (auto-tick racing the manual `/Message/dorun`) safe - second-and-later concurrent calls return immediately rather than racing through the same pending list and double-sending. Manual `/Message/dorun` still works for "kick now" semantics. Opportunistic kicks on submit / inbound delivery would shave latency further but aren't strictly necessary now that the tick is automatic.

- **`UdpMulticastDiscoveryBearer` multi-channel-per-port leakage** *(fixed)*. When one process subscribed to multiple discovery channels that shared a UDP port, the per-channel `recv` sockets all bound `0.0.0.0:port` with `SO_REUSEADDR` and Linux's REUSEADDR + multicast filtering edge cases let packets leak across groups within the process. Fixed by binding each `recv` socket to the multicast group address itself (`endpoint.Address:endpoint.Port`) rather than `IPAddress.Any`, so the kernel applies the group filter at the socket level. Regression test: `UdpMulticastDiscoveryTests.TwoChannels_SamePortDifferentGroups_NoCrossChannelLeakage`.

- **RHPv2 bearer: drive XRouter via RHP instead of AGW** *(done)*. The `scripts/sim-mixed-bearer.sh` work surfaced an architectural limitation in DAPPS-on-XR via AGW: XRouter scopes AGW callsign authorisation per-TCP-connection, so DAPPS's "open a fresh TCP connection per outbound forward" pattern fails (XR refuses the duplicate X-frame from the second connection because `AgwInboundService`'s connection still owns the call, and silently drops the subsequent C-frame). Cleanest fix: skip AGW for XR entirely and use RHPv2 (Remote Host Protocol v2) as the second supported bearer alongside AGW. XRouter ships RHPv2 natively; mainline BPQ is expected to follow. Implementation: `Rhpv2OutboundTransport : IDappsOutboundTransport` and `Rhpv2InboundService : BackgroundService` against M0LTE/rhp2lib-net's `RhpV2.Client` (NuGet 0.2.2 - cleared the wire-format quirks the parking note hit, capital-C `errCode`/`errText`, `port` string-or-int converter, "Ok" textual handling). DI wiring picks AGW or RHPv2 services based on a single `DAPPS_NODE_BEARER` knob (default `agw`; XR operators set `rhpv2`); the bearer choice is implementation detail not exposed elsewhere. `scripts/sim-mixed-bearer.sh`'s XR-side daemons switch to RHPv2 in the same change; all five exercise paths green including the showpiece B->D 3-hop XR->BPQ->BPQ->XR mixed-bearer route. Tests: `Rhpv2OutboundTransportTests` and `Rhpv2InboundServiceTests` use the lib's `MockRhpServer` to lock the wire-shape contract (active open with +1 port-byte, AUTH-before-OPEN ordering, passive bind for inbound, per-handle RECV demux). Docs in `docs/connect/rhp.md`; `docs/connect/xrouter.md` now points at RHPv2 as the recommended XRouter bearer.

- **AGW outbound: multiplex onto the inbound connection** *(superseded by RHPv2 bearer)*. The sim could be made to work via AGW alone by extending `AgwInboundService` to handle outbound C-frames over its single registered connection (demuxing replies by `(callfrom, callto)` and routing to a `TaskCompletionSource`, with session payload through `MultiplexedAgwSessionStream`). Substantial refactor of dapps.client. Decision: skip - the RHPv2 bearer above achieves the same operator-visible outcome with a smaller blast radius (new code rather than modifying load-bearing existing code) and lands a roadmap item in the same stroke. Revisit only if RHPv2 turns out to be a dead end on the XR side and we still need DAPPS-on-XR via AGW.

- **Rename BPQ-flavoured internals to bearer-neutral names** *(done)*. Now that RHPv2 is a live bearer alongside AGW, "BPQ port" was a misnomer in the DAPPS code/UI/REST - the same field is used for both AGW (BPQ port byte) and RHPv2 (XRouter PORT=N, with a +1 transform internally). Mechanical rename across the codebase, docs, sim scripts, and the dashboard: `DbNeighbour.BpqPort` / `DbProbedNode.LastBpqPort` / `BackhaulRoute.BpqPort` / `IDappsOutboundTransport.bpqPortNumber` -> `BearerPort` / `LastBearerPort` / `BearerPort` / `bearerPort`; `SystemOptions.DefaultBpqPort` -> `DefaultBearerPort`; env var `DAPPS_DEFAULT_BPQ_PORT` -> `DAPPS_DEFAULT_BEARER_PORT`; REST/JSON `bpqPort` -> `bearerPort`; dashboard "BPQ port" labels -> "Bearer port"; vestigial `NodeType` config row removed. Health-card on the dashboard ("BPQ AGW") relabelled to "Packet node" with a bearer-aware probe (AGW port when bearer=agw, RHPv2 port when bearer=rhpv2), so an XR-side daemon no longer falsely reports "TCP probe failed - start linbpq". `BpqAgwReachable` Health/Operational JSON field -> `NodeReachable`. No backwards compatibility / migration: gb7rdg-node is the only on-air node and a clean rename was preferred over running two names in parallel.

- **Transmission audit log** *(done)*. New `transmissions` table with one row per outbound transmission: beacon, solicit, solicit-reply, probe, probe-nodeprompt, forward, forward-flood, poll, heartbeat. Each row carries kind / bearer / channel-key / target callsign / message id / bytes / duration / success / error tag, plus a free-form `Reason` field describing the "why" (`scheduled probe sweep`, `operator-triggered probe (MCP)`, `forwarder tick: route via M0LTE-1`, `flood to neighbour (hop budget 3)`, etc.). Recording lives behind a single `TransmissionAuditService.RecordAsync(...)` so every transmit site is one fire-and-forget call. Surfaces: `/Transmissions` REST endpoint with kind/target/only-failures filters, `list_transmissions` MCP tool, `/Transmissions` Razor page polling the REST every 10 s with the same filters as a live tail, and an opt-in MQTT publish to `dapps/audit/tx` for operators with an existing MQTT-shaped scrape pipeline. Retention sweep rides the existing TtlSweeperService minute tick; default 90 days, 0 = keep forever. Distinct from the in-memory decision-events ring (which is bounded and resets on restart) and the systemd journal mirror (which is text-shaped) - this one is persistent and queryable. 7 new tests in `TransmissionAuditServiceTests` (insert + disabled flag + kind/target/failures filters + retention sweep). 531/531.

- **Headless-Chromium UI test harness** *(done)*. New `dapps.core.uitests` xunit project that boots dapps.core as a real subprocess on an ephemeral Kestrel port, drives it with `Microsoft.Playwright`'s headless Chromium, and screenshots every operator-facing page into `tests/screenshots/` so future UI work has a baseline to eyeball without a manual `dotnet run`. WebApplicationFactory's TestServer doesn't speak HTTP to a real browser, and the two-host trick (TestServer-for-WAF + Kestrel-for-Playwright) tripped on shared service-provider lifetime in WAF 8.x - subprocess is the simpler, more honest model: same binary the operator runs, same env-var path, same startup ordering. Side-effects tamed via env vars: `DAPPS_CALLSIGN=N0TEST`, MQTT to a per-fixture ephemeral port (avoids collision with a local dev daemon on 1883), AGW + UDP listener disabled, heartbeat + probing off, update checker off. `PlaywrightFixture` calls `Microsoft.Playwright.Program.Main(["install","chromium"])` once per session - idempotent, no out-of-band setup. `SmokeTests` covers the empty-DB renders (/Setup, /Login bouncing to /Setup, /Health JSON); `JourneyTests` drives the full first-use flow (Setup → set password → land on dashboard → /Inbound with filter typed and summary text observed) - proves the harness can do interactive flows, not just static screenshots. Tagged into a single `[Collection("UI")]` so the browser is reused across tests in a session. CI gets a separate `ui-test` job that pre-builds dapps.core in Release, installs Chromium with `--with-deps`, runs the suite, and uploads screenshots as an artifact regardless of pass/fail. Caught a bug in my mental model first time out - there's no `/Config` Razor page; it's an API endpoint serving JSON, with the operator UI sitting as a `<details>` block on the dashboard.

- **Inject `TimeProvider` so time-dependent code is testable** *(done)*. `TimeProvider.System` is registered as a singleton; `Database`, `OperationalMetrics`, `OutboundForwarderService`, `TtlSweeperService`, `UpdateChecker`, `ProbeSchedulerService`, `DiscoveryService`, `NodeProber`, `UdpDatagramListener`, and `UpdaterOrchestrator` all take it via DI. Every `DateTime.UtcNow` in those classes routes through it; every `Task.Delay(span, ct)` uses the `Task.Delay(span, TimeProvider, ct)` overload; `TtlSweeperService`'s `PeriodicTimer` uses the new `(TimeSpan, TimeProvider)` constructor. The `OutboundForwarderService` test-only ctor is gone - replaced with init-only `TickInterval` / `StartupDelay` setters. Tests in `TimeProviderInjectionTests` exercise the wiring end-to-end with `FakeTimeProvider.Advance(...)` (e.g. saving a row, advancing the clock by 90 s, asserting `DeleteExpired` soft-deletes it). 430/430.

- **Master TX kill-switch - operator toggle + dev-time centralised** *(done)*. Two safety mechanisms composed through one seam. (1) An operator master TX-stop button rendered on every dashboard page: `SystemOptions.TxEnabled` (default true) persisted in the systemoptions table, flipped via POST `/TxControl/{stop,resume}`. When closed, a sticky red banner shows on every Razor page with the block reason and a Resume button. (2) A centralised URL kill-switch the project author controls during the development phase: `TxKillSwitchPoller : BackgroundService, ITxKillSwitchSignal` polls a hardcoded HTTPS URL (`https://compute.oarc.uk/storage/public/folders/4803/dapps-devtime-killswitch.json`) every 5 minutes and stops TX if the JSON says so. Wire shape `{"txAllowed":bool,"reason":string?,"appliesTo":["*"|"M0LTE-*"|"M0LTE-2"]}` with glob-ish callsign targeting so one URL can pause one site without affecting the fleet. **Deliberately not configurable**: URL, cadence, 30-minute staleness window, and fail-open behaviour are all `const`s on `TxKillSwitchPoller`; operators cannot disable the polling, repoint it, or relax the timings. The whole subsystem is removed (not made configurable per-fleet) before 1.0; `docs/dev-time-tx-kill-switch.md` documents this transparently and is linked from `getting-started.md`. **Bearer-level enforcement** is the architectural commitment - `IDappsTxGate` is consulted at the four chokepoints where DAPPS bytes meet on-air emissions: `AgwFrameTransport.WriteFrameAsync` (gates RF Kinds C/v/c/D/M/V/K, lets admin / disconnect through so the BPQ session stays usable), `Rhpv2OutboundTransport.ConnectAsync` (before active OpenAsync = AX.25 SABM) and its `writeOutgoing` lambda (I-frames), `Rhpv2InboundService` writeOutgoing (replies on accepted-inbound sessions), and `UdpDatagramBackhaul.SendAsync`. Discovery, probes, polls, forwards and floods all flow through these chokepoints, so no application-layer service had to be touched. **TLS hardening**: the named `tx-kill-switch` `HttpClient` pins a fresh `SocketsHttpHandler` with default `SslOptions` (system trust store + chain / hostname / expiry validation) so any future tweak to the default handler can't silently weaken kill-switch validation; `PollOnce` adds a runtime guard that refuses to fetch over plaintext if the URL ever drifts off `https://`. Belt-and-braces over .NET's already-validating default. **Audit trail**: both signals write `tx-control` rows symmetrically - `bearer="ui"` for the local toggle (every press, including no-op presses, so operator intent is logged), `bearer="remote"` for remote-driven flips (first-poll-says-BLOCK and ALLOW↔BLOCK transitions; steady-state polls and failed polls don't audit). Files: `dapps.client/Tx/IDappsTxGate.cs`, `dapps.core/Services/SystemOptionsBackedTxGate.cs`, `dapps.core/Services/TxKillSwitchPoller.cs`, `dapps.core/Controllers/TxControlController.cs`. Tests: 27 across `TxGateTests`, `SystemOptionsBackedTxGateTests`, `TxControlControllerTests`, `TxKillSwitchPollerTests`, `TxKillSwitchPollerAuditTests`. PRs #126 (bearer seam), #127 (UI toggle + banner), #128 (poller, originally configurable), #129 (URL hardcoded + transparent docs), #131 (5-min cadence + TLS hardening + softer wording), #132 (symmetric audit). 644/644.

**Goal:** DAPPS core talks in terms of forwarding durable DAPPS units to neighbours, not in terms of opening a stream and speaking one specific session protocol.

The current factoring around `IDappsOutboundTransport` + `Stream` was the right move to get AGW under an interface, but it is still too transport-shaped if DAPPS is going to support a datagram bearer such as MeshCore without contorting the rest of the system. This is high priority while the code is still in progress.

### A0.1. Introduce a DAPPS-owned backhaul interface *(done - PR #12)*

Outbound seam: `IDappsBackhaul.SendAsync(BackhaulMessage, BackhaulRoute, localCallsign)`. `OutboundMessageManager` no longer opens streams or speaks DAPPSv1 itself; it constructs a backhaul message and hands it off. Inbound seam: `IBackhaulInbox.DeliverAsync(BackhaulMessage, sourceCallsign)`. Bearer-specific receive code (today the `DAPPSv1>` session reader, future MeshCore receivers) calls into the inbox once a message is fully received and validated; the inbox owns DB persistence and conditional MQTT delivery. Types live in `dapps.client/Backhaul/` so other-bearer projects can take a dependency without dragging in the AGW stack.

### A0.2. Refactor the current BPQ/AGW path behind the seam *(done - PR #12)*

Outbound: `Dappsv1SessionBackhaul` wraps `IDappsOutboundTransport` + `DappsProtocolClient`. Inbound: `InboundConnectionHandler` retains DAPPSv1 session/parsing (offer↔data correlation, hash check, on-the-wire ack), but now hands the validated message to `IBackhaulInbox` instead of writing DB rows + MQTT-injecting directly.

### A0.3. Define stable DAPPS backhaul units *(done - PR #12)*

`BackhaulMessage(Id, Destination, Salt, Ttl, Payload, Headers?)` is the bearer-neutral unit. Carries everything DAPPS callers need to forward or deliver; nothing bearer-specific. Fragmentation/reassembly (Phase F2) will sit between this unit and the bearer adapter, not at this layer.

### A0.4. Datagram bearer as the forcing function *(UDP stand-in done)*

The seam needed a non-stream bearer to validate the architecture before any real bearer with a small MTU landed. Implemented a UDP datagram backhaul (`UdpDatagramBackhaul` + `UdpDatagramListener`) plus a bearer-agnostic `Packetiser` and `BackhaulMessageCodec` in `dapps.client/Backhaul/Datagram/`. End-to-end tests on loopback exercise both single-fragment and multi-fragment messages with an artificially low MTU (64 bytes), proving the seam supports a fire-and-forget datagram bearer with DAPPS-owned fragmentation.

`IDappsBackhaul.CanHandle(BackhaulRoute)` and per-route bearer hints (`BackhaulRoute.UdpEndpoint`, `DbNeighbour.UdpEndpoint`) let the OMM dispatch to the right bearer per neighbour without leaking bearer-specific code into queue/router logic.

Real radio-bearer integrations (MeshCore Companion, MeshCore KISS, RHP UI, …) live in **Phase H**. The packetiser and codec carry over; each bearer just adds a wire-emit / wire-ingest layer.

## Phase A - make forwarding actually forward

**Goal:** the existing transit-and-deliver path works correctly across two nodes for a local sysop running both BPQs.

### A1. TTL forwarder logic *(done - PR #9)*

Landed: `DbMessage.Ttl` + `CreatedAt` columns, residual-TTL decrement on forward, drop-and-delete on expiry, `TtlSweeperService` for background expiry of offers and messages, two-instance integration fixture with end-to-end coverage through real BPQ over AXIP-UDP. M0LTE/linbpq#41 (image's `mail chat` CMD blocking AGW dispatch) was diagnosed and fixed in the same arc, which closed #6.

### A2. Better neighbour table than "manual MAP entries" *(done)*

`DbNeighbour` is the canonical table; manual entries coexist with auto-discovered peers (Phase B). REST surface at `/Neighbours` (list / upsert / delete) plus a dashboard panel with add/remove form. `OutboundMessageManager.ResolveNeighbour` consults the routing seam (Phase B5/B5.1), which itself walks neighbour + discovered-peer + route-hint state. The originally-planned `dapps neighbours …` CLI was deferred - REST + dashboard cover the sysop ergonomics; if a real need surfaces it can be added as a side-door (à la `dapps --show-config`).

### A3. Sender-side inactivity timeout on AGW *(done)*

`DappsProtocolClient` wraps every per-byte read with a 3-minute inactivity timeout (matches receiver-side T3-default). On expiry the read surfaces a `TimeoutException` rather than blocking forever; the forwarder loop catches and moves on. `AgwOutboundTransport.ConnectAsync` got the same treatment on the connect-confirm wait. Outer `CancellationToken` (from shutdown) takes precedence over the inactivity timer.

### A8. Roll back runtime to .NET 8 LTS *(done - v0.2.0)*

The .NET 10 v0.1.0/v0.1.1 binaries failed to load on Raspberry Pi OS 11 (Bullseye, glibc 2.31): `/opt/dapps/dapps` requires `GLIBC_2.32`–`2.34` and `GLIBCXX_3.4.29`–`3.4.30`. Initial diagnosis (transitive native deps from build host) was wrong - self-contained `PublishSingleFile` produces byte-identical output regardless of build host. The actual cause: .NET 10 dropped Debian 11 from its supported Linux list; its apphost is built against Bookworm-baseline glibc 2.36.

The previous restart (PR #8 entry above) left a comment that .NET 8 was where they had stayed; this confirms why. Rolling back:

- `<TargetFramework>` flipped to `net8.0` in all four csprojs.
- `Microsoft.Extensions.Logging*` pinned to `8.0.x`.
- `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` dropped - those are .NET 9+ APIs. Dashboard remains; the `/openapi/v1.json` + `/scalar` API explorer routes are gone for now. Re-add when we eventually move back to a newer runtime.
- `System.IO.Pipelines` added explicitly to `dapps.client.csproj` (not transitively in scope under the Library SDK on net8 the way it is under the Web SDK).
- `MultiplexedAgwSessionStream.ReadAsync` refactored to extract Span-typed work into a sync helper - async methods can't hold ref-struct locals in C# 12 (the .NET 8 SDK's default).
- CI: `dotnet-version` 8.0.x, dropped the bullseye-container split (the original split was based on the wrong diagnosis; .NET 8's apphost targets glibc 2.23 so any modern build host's output is fine).

.NET 8 LTS support runs to Nov 2026, giving us roughly a year before the next runtime question. By then either Pi OS Bookworm is universal (.NET 10 viable) or we evaluate a different runtime story (NativeAOT, etc.).

267 tests pass on net8 unchanged.

### A9. Forward to .NET 10; AGW session identity hardened *(done)*

Runtime: .NET 8 leaves support in November 2026, so every project now targets `net10.0` (SDK 10.0.x in CI, `sdk:10.0` / `aspnet:10.0` Docker images, Microsoft.Extensions.* on the 10.0 train, FakeTimeProvider 10.9). What it costs: .NET 10 is built against Debian 12 (Bookworm) glibc and needs symbols Bullseye's 2.31 does not have (A8 measured 2.32 to 2.34), so Raspberry Pi OS 11 can no longer run DAPPS; 0.35.0 is the last release that does. The .deb's libc6 floor is derived from the shipped binaries, so apt on Bullseye refuses the package rather than installing something that dies in the loader. What it buys in this area: `System.Threading.Lock`, `ConcurrentDictionary.TryRemove(KeyValuePair)`, span-typed locals in async methods (the `MultiplexedAgwSessionStream` copy helper is gone), and the current `FakeTimeProvider`. The `/openapi` + Scalar explorer that A8 dropped could now come back; not done here.

AGW session identity (follow-up to #171, #175, #176): reading linbpq's `AGWAPI.c` settled what BPQ actually does. Its disconnect notification (`SendDisMsgtoAppl`) is stamped with the port of the last frame BPQ received *from the application*, not the session's port, so after DAPPS's 15 s `'G'` keepalive a session on port 1 ends with a `'d'` saying port 0. DAPPS keyed sessions on (local, remote, port) with an exact match, so that session never closed, its entry went stale, and the peer's next connect was mistaken for a duplicate. A characterisation test against the real linbpq container pins the BPQ behaviour down. Fixes, all in `AgwInboundService`: a `'d'` / `'D'` that matches no session exactly resolves by callsign pair when that pair identifies one session (the ambiguous two-port case is left alone); a `'C'` for a key still registered retires the stale entry rather than rejecting the connect (#175's idea, kept as the fallback), and the retired stream is marked remote-closed so its teardown cannot emit a `'d'` that BPQ would apply to the new session. Outbound: #176's settle delay is kept as `LinkSettleGate` (2 s per bearer/local/remote/port, also armed by a failed connect) on the injected `TimeProvider`. `AgwInboundService` takes a `TimeProvider` too (reconnect backoff, idle backoff, keepalive), so the seam's tests run on a fake clock: 31 tests in about two seconds covering the wrong-port `'d'`, 200 hangup-and-redial cycles, stale-entry retirement with a working new session, the two-port ambiguity, BPQ's echo of our own `'d'`, keepalive cadence and reconnect backoff, plus two Docker tests against real BPQ.

### A7. AGW for both directions *(done)*

Replaces the BPQ Apps Interface (HOST/CMDPORT TCP-bridge) inbound path with AGW dispatch. dapps now uses one BPQ surface - AGW - for both inbound and outbound, multiplexed over a single TCP connection.

Why: the BPQ HOST handler hard-codes the dial-out target as `127.0.0.1` (TelnetV6.c:2689), forcing dapps to either co-locate with BPQ or maintain an SSH reverse tunnel. AGW has no such constraint - dapps connects *out* to BPQ's `AGWPORT` from wherever it runs, so different hosts work with just a network route. Also avoids the Telnet driver's LF→CR rewriting in the app→user direction (a quirk that required dapps's protocol client to accept `\r` as a line terminator).

Implementation:
- `MultiplexedAgwSessionStream` - duplex Stream over one logical AGW session when many sessions share a single AGW socket. Reads pull from a `Pipe` fed by the dispatcher; writes serialise into 'D' frames via a callback. `SignalRemoteDisconnect` closes the read pipe when 'd' arrives.
- `AgwInboundService` (hosted) - connects to BPQ AGW, registers the dapps callsign with an 'X' frame, dispatches inbound 'C'/'D'/'d' frames to per-session streams, hands each new session to `InboundConnectionHandler`. Reconnects on socket loss.
- `InboundConnectionHandler` is now bearer-neutral: takes `Stream` + `sourceCallsign` instead of `TcpClient` + read-first-line. Keeps the protocol logic isolated.
- Removed: `BpqConnectionListener`, `InboundConnectionHandlerFactory`, `BpqInboundListenerPort` option (and the seed/round-trip in `DbStartup`/`Database`), `TwoInstanceAttachFixture`, `BpqAttachBridgeTests`, `InboundDeliveryViaBpqAttachTests`.

Operator config change: the inbound `APPLICATION` line drops the `C N HOST K TRANS S` CMD field (now empty) - BPQ no longer runs a node command on inbound, just dispatches the L2 'C' frame to the registered AGW client. README updated.

Tests: `MultiplexedAgwSessionStreamTests` (5 unit tests for the new stream), `AgwInboundDeliveryTests` (full real-DAPPS-receiver E2E reusing `TwoInstanceLinbpqFixture` - the AGW dispatch shape was already proven by `TwoInstanceAgwSmokeTests`, this adds dapps receiver behaviour on top). 267 tests pass.

Bonus side-effects: outbound and inbound become byte-for-byte symmetric on the wire. Operationally simpler - one socket to BPQ, one config knob (`AGWPORT` + `AGWMASK`), one failure mode.

### A6. BPQ APPLICATION+TCP-bridge inbound coverage *(superseded by A7)*

Issue #32 closed by PR #33 with a HOST-form bridge fixture and E2E tests. Subsequently superseded when we moved to AGW-for-both (A7) - the Apps Interface code and tests were removed entirely. Keeping this entry for historical record: the work surfaced two real bugs (`DappsProtocolClient` didn't tolerate the BPQ Telnet driver's LF→CR rewriting; `BpqConnectionListener` leaked its OS socket on shutdown), the first of which transferred to the AGW path as a no-op (AGW frames are binary-transparent, no rewrites to defend against - though the line-tolerant `ReadLineAsync` is harmless to keep).

### A5. Outbound TTL on the app interface *(done)*

Apps can now request a residual lifetime when submitting a message, and inbound delivery surfaces residual TTL so apps can discriminate near-expiry messages from fresh ones. REST `OutboundRequest` gains an optional `Ttl` (positive int seconds; 0/negative → 400). MQTT publish reads optional `dapps-ttl` user property; malformed values fall through to no-TTL rather than rejecting the publish (the broker has no way to NACK after the fact). On delivery, both surfaces report the *residual* TTL - initial TTL minus dwell time on this node - so an app polling `/AppApi/inbound/{app}` and an app subscribed to `dapps/in/<app>` see the same number. Closes the spec gap where the on-air protocol carried `ttl=` end-to-end but the app interface couldn't read or write it.

### A4. Per-app authentication on MQTT/REST *(done)*

Per-app credentials issued via `/AppTokens` (POST mints + returns plaintext once; DELETE revokes). Tokens hashed at rest with PBKDF2-HMAC-SHA256 + 16-byte salt. Verification is constant-time-equality on the derived bytes.

Off by default - `SystemOptions.AuthRequired` is false initially, so existing single-host-loopback deployments don't break on upgrade. Operators issue tokens, then flip the flag (via `/Config` or `DAPPS_AUTH_REQUIRED=true` on first run).

When enabled:
- **REST**: `BearerAuthMiddleware` on `/AppApi/*` requires `Authorization: Bearer <token>` and stamps the authenticated app onto `HttpContext.Items`. Controllers call `IsAuthorisedForApp(...)` and return 403 on path-app mismatch.
- **MQTT**: `MqttBrokerService.OnValidatingConnection` validates `username` (= app name) + `password` (= token plaintext); rejects with `BadUserNameOrPassword` on mismatch. `InterceptingPublish` and `InterceptingSubscription` enforce that a connected client only operates on its own `dapps/in/<app>`, `dapps/out/<app>/...`, `dapps/ack/<app>` topics.
- **Admin surfaces** (`/Config`, `/Neighbours`, `/AppTokens` itself) are deliberately not behind the bearer check - pairing them with bearer auth would be a chicken-and-egg on first use. Loopback-binding remains the recommendation in the README until proper admin auth lands.

Not PKI-grade by design (no TLS, OAuth, JWT, 2FA). For TLS, sysops front the REST API with a reverse proxy.

**Browser support (MQTT-over-WebSocket).** The embedded broker now runs alongside Kestrel via MQTTnet.AspNetCore - the same broker instance backs the TCP listener on `:MqttPort` AND a WebSocket endpoint at `/mqtt` on the dashboard's HTTP listener. Browser apps subscribe to `dapps/in/<app>` and publish to `dapps/out/<app>/<dest>` directly; no REST proxy needed for real-time delivery. CONNECT-time auth + topic-scope interceptors are shared between transports. WebSocket has no CORS preflight, so a browser app served from `file://`, a static server, or a different host can speak to a DAPPS daemon without operator-side CORS configuration.

## Phase B - peer discovery and routing evolution

**Goal:** DAPPS nodes find each other on a shared frequency without the sysop hand-coding neighbour tables, and route learning evolves toward a transport-agnostic automatic-routing model.

The on-air protocol already has a hand-wavey "discovery" section in the README. AGW exposes the primitives (`'M'` / `'V'` for UI send, `'m'` for monitor). The transport interface in PR #4 is shaped for it but doesn't expose UI yet.

**MeshCore appears in two unrelated lanes** - keep them separate when reading anything below:

1. *MeshCore as a bearer* - using a MeshCore radio to carry DAPPS traffic. That's an `IDappsBackhaul` implementation question and lives in **Phase H** (concrete bearer integrations).
2. *MeshCore as a source of ideas for routing* - flood-then-learn, link-quality-weighted next-hop, etc. - applied to whatever bearer DAPPS happens to be on. That's **B5** below.

B5 doesn't depend on H, and H doesn't gate B5. A future DAPPS deployment could ship one without the other and still be coherent.

### B1. Discovery seam *(done - channels are first-class)*

A bearer (AGW, UDP multicast, future MeshCore) can serve many *channels*: AGW bearer port 1 (VHF) and bearer port 3 (AXIP) share one AGW socket; future MeshCore radios are per-channel; UDP can hold multiple multicast groups. The seam:

```csharp
public interface IDiscoveryBearer : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct);
    Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct);
    IAsyncEnumerable<ReceivedBeacon> ListenAsync(CancellationToken ct);
}
```

`ReceivedBeacon = (BeaconFrame, ChannelKey)` so the daemon knows which channel a peer was heard on. Each channel has its own beacon cadence and advertised TTL - chattering every 5 minutes is fine on AXIP, antisocial on 1200 baud VHF, inappropriate on HF where propagation is part-time.

### B2. Channels-as-first-class + LinkClass *(done)*

`DbDiscoveryChannel` table - one row per channel - is the authoritative configuration. Fields:

- `Bearer` (`agw` / `udp` / future `meshcore`)
- `ChannelKey` (bearer-specific: bearer port for AGW, multicast endpoint for UDP, MeshCore radio+channel for that bearer)
- `LinkClass` enum: `InternetIp` / `LanMulticast` / `VhfUhfFm` / `Hf` / `MeshCore` / `Unknown`
- `BeaconIntervalSeconds`, `AdvertisedTtlSeconds`, `CostHint` - default per `LinkClass`, operator-overridable
- `Enabled`, `Notes`

`LinkClassDefaults` encodes the channel-nature knowledge: HF advertises a 24-hour TTL because propagation closes overnight and a peer that "went away" at sundown is back at sunup; MeshCore beacons every hour because the bearer floods anyway; LAN multicast is cheap and frequent; VHF/UHF FM is in between.

`/DiscoveryChannels` REST surface (parallel to `/Neighbours`): GET / POST upsert / DELETE. Dashboard shows channel list and discovered-peers tagged with channel + link class + cost.

### B3. Beacon protocol *(done)*

Wire form: `DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300`. KV style rather than positional so future fields slot in without breaking parsers. The `Bearer` field on `BeaconFrame` is stamped by the receiver - never carried on the wire (would let a misbehaving peer claim routes it doesn't have).

Distance-vector for v1: beacons advertise self only. A peer reachable on three channels gets three rows; the resolver picks by `CostHint`.

### B4. Routing decisions *(done)*

`OutboundMessageManager` now resolves a `BackhaulRoute` per pending message, in this precedence order:

1. **Manual `DbNeighbour`** with matching base callsign - explicit operator override.
2. **Fresh `DbDiscoveredPeer` rows** for that base callsign, freshness-filtered by `LastSeen + TtlSeconds`, sorted by `CostHint` then by hop count.
3. **`DbRouteHint`** next-hop fallback - explicit "I know X is reachable via Y" when there's no live discovery record.

Cost ordering is **RF-first** per the project's amateur-radio identity. Default `CostHint` per class:

| Class | Cost | Notes |
|---|---:|---|
| `VhfUhfFm` | 1 | RF, line-of-sight, ~always-on - the preferred channel |
| `MeshCore` | 3 | RF mesh, slow but in-spirit |
| `Hf` | 5 | RF continental, propagation-locked |
| `LanMulticast` | 8 | IP, scoped - testing ergonomics |
| `InternetIp` | 10 | IP, last-resort bridge between RF islands |

Internet routes exist to glue isolated RF islands together, not as a preferred path. The denormalised `LinkClass` + `CostHint` on each peer row means the resolver doesn't need to join `discoverychannels`. Tie on cost breaks on hop count.

`LinkClassDefaultsTests` pins this ordering so a casually-tweaked default doesn't quietly invert the project's identity.

### B5. Routing as a learned graph (flood-then-learn) *(done - PRs #51, #52, #53)*

Landed across three PRs: PR-A introduced the `IRoutingAlgorithm` seam (interface designed to fit MeshCore-style source routing later), PR-B added passive learning (every inbound message teaches reverse-direction routes via F1's `src=`), PR-C added bounded-flood fallback for cold-start. The decisions called out below were made consciously - see `docs/routing-prior-art.md` for the comparison.

Resolution precedence (today): static (manual / discovered / hint) → learned (from passive observation) → bounded flood (cold-start). All three layered as decorators over `IRoutingAlgorithm`.

The decisions made:

- **learned whole paths vs. next-hop hints** - next-hop. Source routing was rejected for primary AX.25 deployments (path bytes per data packet expensive on slow links); kept the seam open via `RouteDecision.SourceRoute` which can land later if MeshCore-bearer integration (Phase H1) needs it.
- **route freshness and expiry** - three failed forwards invalidate; success resets the counter; new observation with a different next-hop also resets (fresh path is fresh evidence of liveness).
- **direct-vs-flood promotion** - static and learned ALWAYS win when available; flood is the last-resort fallback. Learned routes from a successful flood path become available the moment a reply traverses back, so floods diminish as the network warms.
- **neighbour advertisements vs. route exchange** - neither. Routes are learned from data-message observations rather than dedicated control packets. Lower control-plane overhead at the cost of needing bidirectional traffic OR a flood to converge.
- **what belongs in DAPPS core vs. bearer-specific** - all of the above is DAPPS-core / bearer-neutral. The wire-format additions (`src=`, `LinkSourceCallsign`, `FloodHopsRemaining`) live in `BackhaulMessage` / codec v4 and apply uniformly across AGW + UDP + future bearers.

The result is closest to AODV in shape (RFC 3561) but with control-plane traffic stripped - passive learning replaces RREQ/RREP. NET/ROM and INP3 explicitly off the table per operator experience (see `docs/routing-prior-art.md`).

#### B5.1 - MeshCore-flavoured DSR alternative *(done)*

A second algorithm stack ships alongside the default - selectable via `SystemOptions.RoutingAlgorithm = "meshcore"`. Source routing with passive discovery: cold-start floods accumulate a `TraversedHops` list at each transit node; arriving floods give every node along the path a discovered-path entry back to the originator (stored in `DbDiscoveredPath` with the full intermediate-hop list, not just next-hop). Subsequent sends embed the path in `BackhaulMessage.SourceRoute`; each hop strips the head before re-encoding. Codec bumped to v5 with new `SourceRoute` and `TraversedHops` fields (plus the long-overdue strip of v1/v2/v3 backward compatibility - we're pre-shipping, the version mechanism stays so future format changes hard-fail cleanly). Algorithm choice is global per-node and applied at startup.

This isn't a replacement for the default - both stacks have legitimate trade-offs (path bytes per packet vs. per-flow next-hop lookup; AODV's mid-flow path repair vs. DSR's ability to load-balance across discovered alternatives). Picking is an operator decision.

### B6. Active discovery - DAPPS goes asking

B1-B4 are passive discovery (beacon, listen). B5 is the routing graph on top. B6 is the third mode: DAPPS proactively explores the network on a slow cadence, building topology that beacons can't see (peers behind a hop, peers on a propagation path that's open right now).

Two sub-mechanisms, distinct because they target different propagation realities:

#### B6.1 - Connected-mode probe-and-map *(done - Phase 1 + Phase 2 + Phase 2b primitive + auto-discovery wiring)*

Phase 1 of B6.1 ships a **direct-connect liveness probe** for every callsign DAPPS already knows about. Sources: manual `DbNeighbour` rows (skipping UDP-routed ones) and AGW-bearer `DbDiscoveredPeer` rows, deduped by callsign with neighbour port winning over peer port. The probe AGW-connects to the target callsign on the chosen port, looks for the `DAPPSv1>` banner (reusing `DappsProtocolClient.ReadInitialPromptAsync`), and disconnects. The result lands in a new `DbProbedNode` row keyed by callsign - `LastProbedAt`, `LastSuccessAt`, `LastError`, `ConsecutiveFailures`, `SuccessCount`, plus an operator `OptOut` flag.

A `ProbeSchedulerService` (`BackgroundService`) drives the cadence: 15-minute startup grace, then a sweep every `ProbeIntervalHours` (default 24h). Within a sweep, individual probes are spaced by 5–30s of random jitter so two nodes on the same cron offset don't dial the same BPQ simultaneously. Off by default - `SystemOptions.ProbingEnabled` opts in. REST surface at `/Probes`: list, run-sweep, run-one, set-opt-out, forget-row. On-demand probes bypass the opt-out filter so a sysop can still test a peer they've muted. Dashboard panel sits between "Discovered peers" and "Recently dropped" with a "Probe all now" button, per-row probe / forget actions, and an opt-out toggle. Settings panel grows two new fields.

Phase 2 adds a new **`peers` command on the DAPPSv1 protocol** (`who` aliased) and ties it to transitive discovery. Server side: receivers respond with one `peer <callsign> source=<n|d>[ port=<byte>]` line per known forward target - manual neighbours emit `source=n`; AGW-bearer discovered peers emit `source=d`; UDP-only entries are excluded (asker can't reach them on the same bearer); same-callsign duplicates are de-deduped with the neighbour winning. Response is terminated with `end`. Client side: `DappsProtocolClient.RequestPeersAsync` parses the response tolerantly (forward-compat - unknown lines between `peer …` and `end` are skipped, partial responses on EOF return what was received, out-of-range port hints are dropped). After every successful Phase-1 probe (`fetchPeers: true` by default), `NodeProber.ProbeAsync` issues `peers` and stashes the result in `ProbeResult.DiscoveredPeers`. The scheduler then upserts each previously-unknown callsign as a candidate `DbProbedNode` row with `Source = via:<callsign-of-asked-peer>` and `LastBearerPort = <peer-supplied port or our port to source>` - never overwriting an existing direct probe row. The next sweep picks those candidates up automatically (`EnumerateTargets` was extended to include `Source` starts with `via:`). Dashboard adds a `Source` column and pills `via:CALLSIGN` candidates as info-level so a sysop sees the difference between direct and hearsay rows. The current node's own callsign is filtered out (the remote always reports us as a peer, since we just talked to them - recording that is noise). Phase 2 added 19 tests across `DappsProtocolClientPeersTests`, `InboundConnectionHandlerPeersTests`, and additions to `ProbeSchedulerServiceTests` - full suite at 401/401.

**Phase 2b - node-prompt-then-`DAPPS` discovery primitive:** `NodeProber.ProbeViaNodeCallAsync` connects to a BPQ NODECALL (not a DAPPS APPLICATION callsign), reads the node banner using a generic "data-then-idle" heuristic (no banner-text pattern-matching - wait until the wire is silent for 500 ms after at least one byte arrived, treat that as "prompt waiting for input"), sends `{applicationCommand}\r` (default `DAPPS`), then runs the regular DAPPSv1 handshake + peers query. Banner-text-agnostic so it works against any BPQ-style prompt regardless of the operator's banner config. MCP tool `probe_via_nodecall(nodeCall, bearerPort, applicationCommand?)` for operator-triggered probing. Three new unit tests pin the idle-detection contract (empty stream → empty result; data + EOF → return data; data + delay + more data → return first batch only).

**Phase 2b auto-discovery wiring:** `SystemOptions.AutoDiscoverViaNodeCall` (off by default) and `NodePromptApplicationCommand` (default `DAPPS`). When the toggle is on, every inbound AGW DAPPS beacon causes `DiscoveryService.UpsertAsync` to derive the BASE callsign of the source (e.g. heard `M0LTE-9` → seed `M0LTE`) and insert a `DbProbedNode` row with `Source = node-prompt:<source-callsign>` and the bearer port hint from the beacon. The scheduler picks these up alongside regular probes; `ProbeAndRecordVerboseAsync` looks up the existing row first and dispatches to `ProbeViaNodeCallAsync` when the source flag matches, falling back to the standard DAPPS-callsign path otherwise. Self-callsign filter prevents seeding our own NODECALL. Existing probe rows are never clobbered - auto-seed only fires when no row exists. Both knobs surfaced via `update_config` MCP and the `/Config` form. UDP beacons are skipped (UDP isn't AGW-routable).
**Probe → learned-route wiring:** `IRoutingAlgorithm` grew an `ObserveProbeOutcomeAsync(askedPeer, peers, ctx)` hook. `ProbeSchedulerService.ProbeAndRecordVerboseAsync` calls it after every successful probe-with-peers, alongside the existing `DbProbedNode` candidate persistence. `PassiveLearningAlgorithm`'s implementation upserts a learned route `(peerBase → askedPeer)` per reported peer, using the same filters as `ObserveInboundAsync` (skip self, skip single-hop self-loop, require asked-peer to be a manual neighbour - resolver discards the row otherwise). Static / FloodFallback / MeshCoreLike implementations are noop / pass-through. 9 new tests covering the algorithm filters and the wiring (`PassiveLearningAlgorithmTests`, `ProbeSchedulerServiceTests`).

**Convention:** "DAPPS lives here" = an `APPLICATION` line whose alias is `DAPPS` (typing `DAPPS` at the node prompt connects to the local DAPPSv1 instance). Already what we recommend; documented in the install README. Phase 2b leans on this.

#### B6.2 - HF NVIS solicit-and-listen *(done - on-demand + scheduled cadence)*

Wire form: `DAPPS v1 solicit callsign=M0LTE-9` - same KV style as the beacon, longer fixed prefix so the parsers don't confuse them. `SolicitCodec.TryParse` strictly anchors on `"DAPPS v1 solicit "` and accepts a `callsign=` field plus arbitrary forward-compat KVs. Both bearers (`AgwUiDiscoveryBearer`, `UdpMulticastDiscoveryBearer`) gained a `SolicitAsync(SolicitFrame, channelKey, ct)` emit method (UI 'M' frame on AGW, multicast datagram on UDP) and try the solicit codec before the beacon codec on inbound, surfacing them as `ReceivedSolicit` on the same listen stream as `ReceivedBeacon`. Both inherit from a new `ReceivedDiscoveryFrame` base so the dispatcher can pattern-match.

`DiscoveryService` now responds to incoming solicits by emitting a beacon on the same channel after a uniform random delay in `[0, SolicitResponseMaxDelay]` (default 5 s). The random jitter avoids the "ten nodes hear the same solicit, all reply at once" channel-saturation case. Self-echo on the UDP loopback path is filtered out (we don't respond to our own solicit). Replies arrive at the asker as normal beacons → `DbDiscoveredPeer` upsert via the existing path; no new storage. REST surface: `POST /DiscoveryChannels/{id}/solicit` fires a one-shot solicit; `503` if the bearer isn't currently running, `400` if the channel is disabled. Dashboard: per-channel "solicit" button next to "remove".

Scheduled cadence landed alongside the airtime budget: per-channel `DbDiscoveryChannel.SolicitIntervalSeconds` (0 = disabled, default), gated through the same `AirtimeAccountant.TryReserve` as scheduled beacons and solicit replies. `DiscoveryService.EmitAndSweepAsync` keeps a per-channel `nextSolicit` map alongside `nextEmit` and fires `bearer.SolicitAsync` on cadence - independent of beacon cadence so a channel can beacon every 30 min and solicit every 4 h, or vice versa. Defer math mirrors beacons (quarter-interval retry on budget refusal). Operator surface: REST `DiscoveryChannelModel.SolicitIntervalSeconds`, dashboard "Solicit every" column, "Add discovery channel" form input. Sim coverage: `prove-scheduled-solicit` reconfigures B's channels with `SolicitIntervalSeconds=3`, restarts the node, wipes discoveredpeers, waits 12 s, and asserts both A and C re-populate without any operator-triggered solicit.

Implementation notes carried forward:
- **Frame format**: long magic `DAPPS v1 solicit ` distinguishes solicits from beacons at the codec layer. Bearers try solicit first; beacons fall through.
- **Receive window**: random reply delay 0..5 s by default. Operator-tuneable via `DiscoveryService.SolicitResponseMaxDelay`.
- **Storage**: replies are normal beacons - `DbDiscoveredPeer` upserts via the existing path. No solicit-specific persistence.

#### Sequencing

B6.1 wants B5 (the routing graph). B6.2 is mostly orthogonal - could land after B5 too but doesn't strictly need it. Both are bigger engineering investments than the polish items in C3/D-anything; B5 → B6.1 → B6.2 is the natural order if we tackle this whole sub-phase.

### B7. Airtime budget + probe strategies *(done)*

Two scratchpad items folded into one PR because they're the same shape: pick the right cadence given operator context, rather than burning a fixed interval regardless.

**Airtime budget.** `AirtimeAccountant` is a singleton with a single global rolling 60-min window. Beacons (per channel), solicit replies (per channel), and probes (each session) call `TryReserve(estimatedSeconds, reason)` before transmitting; if the trailing-hour total would exceed `SystemOptions.DiscoveryAirtimeBudgetSecondsPerHour`, the call returns false and the caller defers (beacons reschedule a quarter of the regular interval out; probe sweeps stop early and resume next sweep). Budget defaults to `0` (unlimited - preserves pre-B7 behaviour); operators on shared 1200-baud VHF or HF opt in. Estimates rather than measurements: `LinkClassDefaults.AirtimeSecondsEstimate(LinkClass, AirtimeKind)` returns coarse per-class numbers (e.g. VHF FM beacon ≈ 2 s, HF probe session ≈ 32 s). Off by an order of magnitude in either direction is fine - the budget is a cap on order-of-magnitude growth, not a precision regulator.

**Probe strategies.** `SystemOptions.ProbeStrategy` enum chooses the dispatcher: `FixedInterval` (default - pre-B7 cadence), `Overnight` (one sweep per local-time day inside `[ProbeOvernightStartHour, ProbeOvernightEndHour)` - handles straddling-midnight windows naturally; default 02:00–06:00 local), `WhenQuiet` (fixed cadence but defers each tick if `OutboundActivityTracker` saw a successful forward inside `ProbeQuietWindowSeconds` ago; default 5 minutes). The dispatcher is a pure `ShouldRunSweep(opts, lastSweepCompletedAt)` function: easy to unit-test, no side effects, no real time involved.

**Operator-triggered probes from the REST surface bypass the budget** - that's an explicit human action on a single callsign, and rate-limiting it would be hostile to the "I'm debugging a peer right now" path. The budget gates only the *automatic* sweeps.

13 new tests across `AirtimeAccountantTests` (7 - budget zero allows; within-budget allows; over-budget rejects; entries age out; runtime budget reduction enforced; negative-estimate clamp; consumed-seconds rolls forward) and `ProbeStrategyTests` (6 - FixedInterval immediate; Overnight inside-window/outside-window/already-swept-this-night; Overnight straddle-midnight window math; WhenQuiet recent-activity defers / no-history fires).

B7 follow-ups landed in a separate PR: per-channel airtime budgets via `DbDiscoveryChannel.AirtimeBudgetSecondsPerHour` (0 = use the global cap; reservations must fit under both ceilings when both are set, with a key on each entry so per-channel buckets sum independently), an airtime-meter pill on the Discovery channels dashboard heading showing trailing-hour consumption against the global cap (warns at ≥90%), per-channel "Channel budget" column in the channels table + corresponding "Add channel" form input, and a `dapps --show-config` CLI subcommand that walks the persisted systemoptions table and prints `DAPPS_SCREAMING_SNAKE=value` pairs without booting the host.

### B8. Collaborative route gossip *(sketch - tracked as #136)*

Today every node discovers the mesh independently. B6.1 Phase 2's `peers` query *is* a small step toward sharing - when I successfully probe you I ask "who do you know?" and seed your neighbours as `via:<you>` candidates I then probe myself, so node *existence* propagates one-hop-at-a-time across the probe graph. What's missing is **route-state sharing**: I tell you "I successfully reached X via Y at hop-count N, save yourself the discovery cost." That's the AODV → DSDV/RIP step we deliberately didn't take.

The trade-off is airtime. Passive-flood (B5 default) is "learn from what you see anyway, transmit nothing extra"; real route advertisement means periodic gossip that eats into the B7 discovery budget even when nothing changed. Cheapest middle ground: piggyback a small reachability TLV on the existing `peers` response (or beacon), so a single conversation that already had to happen carries route info as a free side-effect.

Several axes worth thinking about before this is more than a sketch:

- **When to gossip.** Tie to B7 strategies - a new `Overnight` route-gossip window (similar to the probe overnight window) lets nodes do the heavier "here's my full reachability table" exchange during local quiet hours. Ties to time-of-day NVIS propagation patterns too. The default could be: piggyback every conversation cheaply year-round, plus one fuller exchange overnight when airtime is cheap and link congestion is low.
- **Bearer choice.** Topology info is broadcast-shaped, not point-to-point. Two interesting non-AGW bearers: (1) **HF NVIS broadcast** - fits the existing B6.2 solicit infrastructure; one node in the cluster transmits its known-routes table on the beacon channel and every receiver in the NVIS footprint absorbs it without anyone having to open a session. Asymmetric reach is the catch - a node hears the broadcast but the broadcaster doesn't know who heard. (2) **QO-100 satellite** - geostationary, continental-Europe-and-Africa footprint, near-zero propagation variance, no overnight-vs-daytime question. Different licence/equipment story (most operators don't have the dish), so it'd be opt-in for the operators who do; one QO-100-equipped node could bridge route info between otherwise-isolated terrestrial clusters.
- **Fountain codes.** The Phase F4-deferred fountain-code work was rejected for messages because DAPPSv1 is acked point-to-point; broadcast topology gossip is exactly the unacked-broadcast shape fountain codes were built for. A single transmitter dribbling LT/Raptor symbols of "the current routing table" lets late-joining or briefly-dropped receivers reconstruct without a per-receiver retry storm. Worth revisiting the F4-deferred fountain code in this context - same library covers both.
- **Loop / staleness defence.** Distance-vector classics: split horizon (don't tell Y about routes I learned from Y), poisoned reverse, hop-count cap, periodic re-advertisement so stale info ages out. Sequence numbers per advertisement so a receiver can tell "newer info from this originator" vs "echo of something I've seen."
- **Trust.** Amateur regs require call-sign identification on every transmission so the *originator* is always known; whether we trust their reachability claim is a separate question. Cheapest defence is "treat advertised routes as candidates, not facts - re-probe before believing." Same shape as B6.1 Phase 2's `via:<callsign>` candidate handling.
- **Negative info.** "I lost the link to X" is just as load-bearing as "I gained it." Cheapest way to carry it is per-destination sequence numbers + periodic re-ad - if a destination disappears from the latest table, receivers age it out after N missed cycles. Avoids needing an explicit withdrawal message.

Wire shape (sketch only): extend `peers` with optional `route <callsign> hops=<n> via=<via-callsign> seq=<n>` lines, terminated as today by `end`. Forward-compat - older nodes ignore unknown line types. Per-destination row in a new `routedigest` table with `(Destination, ViaCallsign, Hops, AdvertisedBy, AdvertisedAt, SeqNo)` so the resolver can prefer learned-from-gossip routes when no direct probe row exists, treating them as second-class evidence (lower confidence than a probe-confirmed reach).

**Status:** sketch. Worth doing once B8's airtime cost is cheaper than the discovery cost it replaces - i.e., once we have enough nodes that the per-pair probe matrix is meaningfully wasteful. With the current operator population (author + a handful) it's premature.

## Phase C - deployable, runnable for sysops

**Goal:** a sysop downloads a single binary for their platform from a GitHub Release, drops it next to a `dapps.db`, and it Just Works.

### C1. Native single-file binary releases *(done)*

Native single-file binaries published as GitHub Release assets, not a Docker image. Operators run a binary; no .NET runtime install, no Docker install, no container plumbing. The Dockerfile in the repo stays as a secondary option for those who want it but isn't the primary distribution.

- Single workflow `.github/workflows/ci.yml` handles the full PR-to-release path. Triggered on PR (test only) and master push (test, then conditional release). Tag-push triggers are gone; the release decision is driven by `<Version>` in `src/dapps/Directory.Build.props` - bump it to release, leave it to land a normal commit.
- Master-push flow: build + test → query whether `v$(Version)` already exists as a GitHub Release → if not, fan out the matrix (`linux-x64`, `linux-arm64`, `linux-arm`, `win-x64`, `osx-arm64`) and `gh release create` with all five binaries attached. Same version twice = second push is a no-op, so re-merging or amending master won't overwrite a published release.
- `dotnet publish ... --self-contained -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true` so the SQLite native lib AND the rest of the bundled framework extract correctly on first run (the `IncludeAll` form was needed to fix a `System.Diagnostics.Process` load failure on linux-arm - see #66).
- Trimming left off - ASP.NET Core + DI + JSON have enough reflection that trimming tends to break startup in subtle ways. Binary is ~100MB; acceptable for the ergonomics win.

### C2. Config tooling *(env-var seeding done; CLI subcommand deferred)*

`DbStartup` now seeds missing `systemoptions` rows from `DAPPS_*` environment variables (`DAPPS_CALLSIGN`, `DAPPS_NODE_HOST`, `DAPPS_AGW_PORT`, `DAPPS_DEFAULT_BEARER_PORT`, `DAPPS_MQTT_PORT`, `DAPPS_NODE_TYPE`). Once a row exists, env vars stop mattering - no surprise overwrites of operator-set values on restart. After seed, the startup refuses to run with the placeholder `N0CALL` callsign and logs a clear error pointing the operator at `DAPPS_CALLSIGN` or `/Config`. The originally-listed `dapps configure` CLI subcommand is unnecessary given env-var seeding plus the existing `/Config` REST endpoint; deferred unless a real need surfaces.

### C3. Health, logs, observability *(done - PR-A + PR-B landed)*

**PR-A - `/Health` + decision-events ring + journal mirror + `/Operational/recent`.** New `/Health` endpoint returns lightweight liveness JSON (callsign, version, uptime, BPQ-reachable, MQTT-up, last-forward-at, pending-outbound) with HTTP 200 / 503 semantics so systemd watchdog units and external uptime monitors have a clean signal. Existing `OperationalMetrics` (which already had a 100-entry recent-events ring and the `/Events/health` dashboard endpoint) gains six new categories - `probe.ok`/`probe.fail`, `poll.ok`/`poll.fail`, `route.learned`, `peer.aged`, `budget.refused` - with corresponding counters in `Snapshot`, plus a `LastForwardSuccessAt` accessor used by `/Health`. Every event recorded into the ring is *also* emitted as a structured `ILogger.Information("event {Kind} {Summary}", …)` line so systemd journal captures it for retrospective greps: `journalctl -u dapps -g 'forward.fail.*abc1234'` finds a specific message's failure 10 days later, even though the in-memory ring only holds the last 100. New `/Operational/recent` REST endpoint surfaces the ring as JSON for external scrapers. Forward-event recorders gained the message id as a parameter so the journal line carries it (`event forward.ok abc1234 → G0CALL (123 B)`). `Database.AgeOutDiscoveredPeers` now returns the deleted rows so per-peer `peer.aged` events can be fired. `DatabaseRoutingContext` does a get-then-upsert diff for both learned-routes and discovered-paths so `route.learned` only fires on new-or-changed entries (refresh ticks would otherwise drown the journal in noise).

**PR-B - `/Operational` + MQTT heartbeat.** New `OperationalSnapshotBuilder` singleton composes a single `OperationalSnapshot` shape (callsign, version, uptime, BPQ-reachable, MQTT-up, all 17 counters from `OperationalMetrics.Snapshot`, queue / peer / channel / neighbour counts, trailing-hour airtime, last-20 events). Used by both `/Operational` (operator-facing JSON, open) and `HeartbeatPublisher` - a `BackgroundService` that publishes the same shape as a retained MQTT message on `dapps/metrics/heartbeat` every `SystemOptions.HeartbeatIntervalSeconds` seconds (default 60, clamped to ≥10). Retained = true so a Home Assistant or simple MQTT subscriber connecting at any time gets the latest state immediately, without waiting up to a full interval. Default on (`HeartbeatEnabled` = true) - the broker is already running for the app interface, so the publish is essentially free. `MqttBrokerService` gained `PublishRetainedAsync(topic, payload)` for the publish path. Dashboard `/Config` form gets the toggle + interval input. 502 tests green (500 → 502: two new snapshot-builder tests covering composition + placeholder-callsign degraded-status).

### C4. Install / upgrade docs *(done)*

README "Getting started" rewritten end-to-end for the native-binary distribution: prerequisites, the BPQ-side `bpq32.cfg` snippet (AGW + Apps Interface slot + APPLICATION line with `TRANS`), download/run instructions for each release artifact, env-var bootstrap, neighbour add/remove via REST, and a verification step that connects from a node prompt and sees the `DAPPSv1>` banner. Troubleshooting block covers the usual surprises (AGW config, callsign typos, BPQ port-byte indexing, `TRANS` flag missing). Backups + upgrade notes added.

### C5. Self-update

Manual `curl` + `install` + `systemctl restart` is workable for the operator running through the README, miserable for a deployed estate. Goal: keep the bar of "single static binary, drop it in `/opt`, run via systemd" but stop expecting the sysop to chase release notes by hand.

Three phases by complexity / risk; ship in order, evaluate before moving on.

#### C5.1 - Visibility *(done)*

`UpdateChecker` (a `BackgroundService`) polls `https://api.github.com/repos/packet-net/dapps/releases/latest` every 6 hours after a 15s startup delay, caches the result, and exposes `Current` / `Latest` / `IsAvailable` / `IsDevBuild`. `Current` is resolved from `AssemblyInformationalVersion`; `dev-<sha>` builds are flagged so the dashboard never claims a dev build is "out of date". Compared via a tolerant dotted-decimal semver comparator. `SystemOptions.UpdateCheckEnabled` (default true, seeded by `DbStartup`, editable in the dashboard's settings panel) is the opt-out for sysops who pin a version on purpose. `EventsController` exposes the snapshot at `GET /Events/version`. The dashboard's `#version-pill` in the header polls that endpoint on load + every 10 minutes and renders one of three states: dev build (info pill), up-to-date (muted pill, `vX.Y.Z`), update available (warn pill, `vX.Y.Z → vX.Y.W available`, links to the GitHub release). Cross-platform - pure polling + UI.

#### C5.2 - Triggered update (Linux / systemd) *(done)*

Sysop clicks "Apply update" on the dashboard (or POSTs `/Update/apply`); the unprivileged dapps writes `/var/lib/dapps/update-requested`. The privileged `dapps-updater.service` (paired with `dapps-updater.timer` firing every 60 s) sees the marker on its next tick and invokes `dapps --apply-update` - the same dapps binary in a side-door mode that runs the orchestration, then exits, without booting the host. dapps itself stays unprivileged.

The orchestration: poll GitHub Releases for the latest tag → download the asset for our RID into `/opt/dapps/dapps.new` → atomic-swap (`/opt/dapps/dapps` ↔ `/opt/dapps/dapps.previous`, then `dapps.new` → `dapps`) → `systemctl restart dapps.service` → watch `is-active` for the 60 s health window. Auto-rollback on any failure (non-zero restart, is-active false during the window, swap exception): restore `dapps.previous` and restart. Status is persisted as JSON to `/var/lib/dapps/update-status` at every phase transition; the dashboard polls `/Update/status` every 5 s during a run.

Code shape: `UpdaterOrchestrator` is a pure state machine over `IUpdaterFileSystem` / `IUpdaterDownloader` / `IUpdaterProcess`. 11 unit tests cover the happy path plus every rollback path (download fail, swap fail, restart fail, is-active flips false mid-window, no-marker no-op, already-on-latest, missing previous binary). Real implementations (`RealUpdaterFileSystem`, `RealUpdaterDownloader`, `RealUpdaterProcess`) handle the actual disk / HTTP / systemctl calls.

CLI side-doors that exit before the host boots - **a key part of the design** so they work even when dapps.db is incompatible / a port is wedged / the callsign is unset:
- `dapps --version` - print version, exit.
- `dapps --check-update` - poll GitHub Releases, print `current=… latest=… status=…`, exit.
- `dapps --apply-update` - privileged; marker-gated (`/var/lib/dapps/update-requested`). Runs the orchestrator. Exit 0 on success / no-update-needed / no-marker, 1 on rolled-back, 2 on outright failure.
- `dapps --rollback` - privileged; unconditional manual rescue. Restores `dapps.previous`, restarts. The "self-rescue" the operator runs from SSH if the dashboard isn't reachable.

Systemd units live in `scripts/dapps-updater.service` + `.timer`. README install recipe added under §6.1.

**Operator UX:** dashboard's `#upd-phase` pill flips through `checking` → `downloading` → `swapping` → `restarting` → `verifying` → `success` (or `rolled back` / `failed`). Apply button is gated on `(release available) AND (no in-progress run) AND (no pending request)`. Dev builds never auto-update.

**What's NOT in this PR (deferred):** scheduled auto-update (C5.3), Windows/macOS triggered-update path, channel pinning, signature verification.

#### C5.3 - Auto-update on a schedule *(parked - tracked as #140)*

Originally sketched as: opt-in `AutoUpdate=true` + `QuietHours` window, skip if traffic was forwarded in the last N minutes, per-major-version pinning so a 0→1 bump doesn't surprise people overnight.

Parked because the existing surfaces are already enough for the current operator population: the dashboard's update banner (C5.1) plus the one-click `/Update/apply` button (C5.2) plus the `trigger_update` MCP tool (Plan M) already give an operator - or an MCP-driven assistant - a "release is available, apply it" flow that takes seconds. A scheduled apply is a convenience for operators who don't watch the box at all, but those operators don't yet exist; today it's just author + a small handful of friendly nodes. Revisit when there's a real population whose nodes drift weeks behind because nobody clicks the button.

Sketch retained above so the design intent isn't lost when we come back.

#### Out of scope for now

- Binary signing / signature verification. GitHub Releases over HTTPS is the trust model today; signing is its own initiative (Sigstore? minisign? - separate decision).
- Channels (stable / beta / dev). YAGNI until we have releases that warrant the distinction.
- Cross-platform privileged-update (Windows service control / launchd). Linux/systemd first; Windows + macOS get C5.1 (visibility) and stay manual otherwise.

## Phase D - web management UI

**Goal:** sysops who want a GUI to inspect state, exercise the system, and verify config can do so without leaving a browser.

The existing app exposes ASP.NET Core + Scalar/Swagger for its REST surface - that's enough scaffolding to host a UI alongside.

### D1. Inspection / dashboard *(MVP done)*

Single Razor Pages dashboard at `/`. Covers: callsign, BPQ AGW reachability probe, MQTT/UDP listener status, auth-required flag, queue depths (total / pending-outbound / undelivered-local / neighbour count), neighbours table, recent-messages table (id, dst, src, bytes, status, ttl, age).

Deferred for follow-up: filter by app/callsign/status, click-for-payload-preview, dedicated routes view (DbRouteHint is mostly gone post-A2 anyway), logs-tail page (sysops can use stdout / journalctl). Filed as items here so they're not lost.

### D2. Exercising the system *(done)*

Three surfaces:
- **Send-test-message form on the dashboard** - POSTs into `Database.SubmitOutboundMessage` (same path an app would take via REST/MQTT). Useful for verifying a node-to-node link without writing an app first.
- **`/Inbound`** - Razor page that opens an `EventSource` against `/Events/inbound` and live-tails every message landed by the inbox layer (after persist + MQTT inject) into a streaming table. Newest first; capped at 500 visible rows so the DOM doesn't grow unbounded on a busy node. Status pill flips between connecting / live / reconnecting based on the EventSource state. App / source-callsign / destination filter inputs hide non-matching rows live (no server round-trip - the data's already in the DOM); clicking any row fetches `/Events/payload/<id>` and inline-expands a tabbed text/hex preview (capped at 4 KiB to keep the page small even on multi-MB messages, with a `truncated` flag and a `textValid` flag the UI uses to pick text-vs-hex on UTF-8 payloads). The payload endpoint's a deliberate separate fetch - it keeps the SSE event itself tiny so every browser tab subscribed to a busy node doesn't get hosed by payload bytes flowing through the stream.
- **`/IHave`** - Razor page with a compose form exposing every operator-relevant field (app, destination, payload, optional TTL). The OnPost handler calls `Database.SubmitOutboundMessage` directly - same as the dashboard's send-test, just with the on-air `ihave` line preview shown after submit so an operator can see exactly how the message will appear on the wire.

Both pages link from the dashboard header. AdminAuthMiddleware gates them behind the existing admin cookie.

### D3. Configuration UI *(done)*

`/Config` `<details>` block on the dashboard exposes every operator-tunable knob: callsign, packet-node host/port (AGW + RHPv2), MQTT port, UDP listener, default bearer port, auth-required, update-check, probing toggles + cadence + strategy + overnight window + quiet window, scheduled-poll toggle + interval, opportunistic poll, F2 fragment threshold + reassembly timeout, B7 discovery airtime budget, C3 heartbeat toggle + interval, B5 routing algorithm. Restart-required fields are flagged in the form blurb. Save POSTs the full SystemOptions row to `/Config`.

### D4. Auth on the UI *(done)*

Cookie-based admin password with a first-use `/Setup` flow. `AdminPasswordStore` hashes via PBKDF2-HMAC-SHA256 (16-byte salt, 100k iterations) - same scheme as `AppTokenStore`. AdminAuthMiddleware gates everything except `/AppApi` (which has its own bearer auth model), `/Setup`, `/Login`, `/Logout`, the static-asset paths, and `/Health` / `/Operational` / `/mcp` (which are designed for clients without admin cookies - watchdog units, scrapers, MCP).

### D5. Implementation notes

Likeliest-cheapest stack: server-rendered Razor or Blazor Server. Avoid SPA tooling unless someone wants to take that on. The existing Scalar OpenAPI page already gives free API exploration; build the UI as a sibling, not a replacement.

## Phase E - application developer guide

**Goal:** someone who's never seen DAPPS can read the guide, understand the model, and ship a working app in an afternoon.

### E1. Concepts page *(done)*

`docs/concepts.md` covers the eventual-delivery model, `app@callsign` destinations, content-addressed message ids and what idempotency looks like in practice, the TTL-as-residual-lifetime contract, and the "why DAPPS rather than raw AX.25 / packet mail" framing. Linked from the README's app-interface section.

### E2. Tutorial: hello-world application *(done)*

`docs/tutorial-hello-world.md` walks through `docs/examples/hello.py` - a Python app using paho-mqtt 2.x (MQTT 5) that subscribes to `dapps/in/hello`, replies with `hello, <name>!` on `dapps/out/hello/<sender>`, and acks via `dapps/ack/hello`. The tutorial covers reading the `dapps-id` / `dapps-source` / `dapps-ttl` user properties, idempotent reprocessing on redelivery, and the equivalent flow expressed as `curl` against the REST endpoints. Same tutorial in C# / Go / Node welcomed as community contributions once Python is the worked example.

### E3. Reference *(done)*

`docs/reference.md` is the full app-interface reference: every MQTT topic, every REST endpoint, every user property with type and semantics, the idempotency contract spelled out with code-shaped pseudocode, and a discussion of practical payload-size limits (bearer MTU, multi-part fragmentation status, id collision space). The README app-interface section links to it as the place to look once you know what `app@callsign` means.

### E4. Sample app gallery *(done)*

`docs/gallery.md` indexes three runnable examples that cover the major DAPPS-shaped patterns:
- `docs/examples/chat.py` - group chat (one-to-many fan-out, symmetric peers, no central registry).
- `docs/examples/sensor.py` - periodic publisher (no `on_message`, no acks, long TTL, supports `--once` for cron-style use).
- `docs/examples/pager.py` - two-way messenger (long-running listener + one-shot sender modes sharing the same app slot).

Combined with the existing `hello.py` from E2, the gallery covers request/reply, many-to-many, one-shot submit, periodic submit, and mixed-mode shapes. Mailing-list / forum-style apps were considered but cut as borderline DAPPS-shape (more about server architecture than DAPPS surface) - defer to early adopters who actually need that pattern.

## Phase G - second-language reference implementation *(tracked as #141)*

**Goal:** prove the spec is portable, not an accidental description of the C# implementation.

A second compatible implementation forces the spec to be precise. Promoted out of Phase E because it's a multi-week engineering effort (new package, new CI lane, cross-implementation interop tests), not a developer-guide doc.

Python is the obvious second language - large amateur-radio Python community, easy onboarding. Aim for:
- A minimal `dapps-py` that implements the on-air protocol (DAPPSv1 codec + parser) and the AGW transport.
- Talks to the same `m0lte/linbpq` Docker image we use in CI today.
- Cross-implementation interop test in CI: `dapps.core` (C#) on one side, `dapps-py` on the other, message goes through end-to-end.

The implementation does not need feature parity with `dapps.core` - it only needs to be *interop-correct*. Subset of MQTT/REST app interface (probably MQTT only), no dashboard, no auth, no persistence (in-memory queue is fine for an interop tester). The point is to surface ambiguities in the spec by forcing a second author to read it and implement it.

## Phase F - spec maturation

Items deferred from earlier discussions, mostly "we know it'll need to happen, just not yet":

### F1. End-to-end source tracking *(done - PR #47)*

`ihave` gained an optional `src=<callsign>` field carrying the originating callsign - distinct from the link source. Originators set it; relays preserve it verbatim across re-forwards. Receivers expose it as the `dapps-origin` MQTT user property and the REST `OriginatorCallsign` field. Pre-F1 senders that omit `src=` round-trip cleanly with originator unknown. `BackhaulMessageCodec` bumped to v2 with a `HasOriginator` flag bit; v1 messages still decode. The 6-node multi-hop simulator's five canned exercises validate it end-to-end across path lengths 1-4 and branching.

### F2. Multi-part messages *(done)*

Spec extension: `ihave` lines may carry `mid=<7hex>` (master id, the originator's grouping handle) and `frag=N/M` (1-based, M ≥ 2). Both fields together mean "this is one fragment of a multi-part logical message"; absence of both means "single-part" (backward compat). Pre-F2 receivers seeing the new headers ignore them per the spec's forward-compat rule - they'll deliver each fragment to the app as a separate message, which is the wrong outcome but not a corruption.

The win isn't MTU adaptation (bearers do their own framing - AGW/AX.25 paclen, A0.4's UDP packetiser, MeshCore link layers all handle MTU below DAPPS) but **resumability**: if a 50 KB transmission drops at fragment 3 of 5, the next forward attempt picks up at fragment 4 rather than restarting the 50 KB session. Same across crashes - already-received fragment rows persist; only missing ones retry.

End-to-end: originator fragments at submit time when payload > `SystemOptions.FragmentThresholdBytes` (default 4096). Each fragment is a complete `DbMessage` row with shared `MasterId` and distinct `FragmentIndex` (1..N). The forwarder picks them up individually and re-emits `mid=` + `frag=N/M` on the wire. Intermediate hops just forward fragments as opaque single messages (no buffering, no reassembly). Final destination's `DatabaseAndMqttInbox` detects the F2 headers, routes to a new `DbFragment` reassembly buffer, and on completion concatenates by `FragmentIndex`, persists the assembled bytes as one `DbMessage` row, and injects to MQTT exactly as a single-part arrival would have. Fragment rows are dropped on success.

Stale-fragment sweep: `TtlSweeperService` drops `DbFragment` rows older than `SystemOptions.FragmentReassemblyTimeoutSeconds` (default 7 days - long because HF / mesh propagation gaps legitimately last days, and we'd rather hold partial work on disk than throw it away).

Apps never see fragments - the MQTT/REST surface only emits the assembled message. The originator's `SubmitOutboundMessage` returns the master id rather than per-fragment ids; the dashboard's queue panel lists fragments individually so an operator can spot a lost-in-transit chunk.

20 new tests across `F2WireFormatTests` (10 - parse + reject paths for `mid=` / `frag=N/M`), `F2FragmentationTests` (8 - sender chunking, in-order reassembly, out-of-order reassembly, idempotent re-delivery, incomplete-buffer hold, stale sweep, transit-fragment passthrough), and `DappsProtocolClientTests` (+2 - wire-emit shape with F2 headers, partial-args reject). 455/455.

### F2.5. Fountain codes for broadcast / HF / multi-path *(tracked as #138)*

Future option, not in F2's scope. A fountain code (LT, Raptor) lets the originator emit an unbounded stream of random-combination packets; any recipient who collects K(1+ε) of them reconstructs the source. No back-channel retransmission protocol; the sender just keeps emitting and recipients tune in until they've collected enough.

Where this shines is the corner DAPPS is most under-served on today: HF broadcast and multi-path mesh delivery on lossy / asymmetric links. A bulletin-style "broadcast a digest, any of N listening nodes builds it up over a propagation pass" feature is the natural home - completely different transport profile from F2's acknowledged unicast resumability.

**Why not now**: DAPPSv1 is acknowledged point-to-point (`ihave → send → data → ack`); fountain codes' natural home is unacknowledged broadcast - the wrong primitive to retrofit. Decoder is 500+ lines we'd own forever; LT is tractable on a Pi, Raptor is faster but the patent situation is murky. We also don't yet have the broadcast shape (B6.2 HF solicit is on-demand request/reply; nothing today is multicast-mesh-shaped).

**Where it'd land**: alongside a real broadcast surface - e.g. "DAPPS bulletin" one-to-many, no-ack, fountain-coded over HF - as its own transport profile coexisting with F2's classic chunking. F2's `frag=N/M` doesn't preclude this; they're different shapes for different jobs.

### F3. `rev` polling *(F3a + F3b done)*

#### F3a - server rev + opportunistic poll on push *(done)*

Spec form: `rev\n` drains every queued message whose final destination is the caller, then re-emits `DAPPSv1>`. Selective form `rev <id1> <id2> …\n` narrows. Same `ihave / send / data / ack` exchange as a push; the caller becomes the receiver. Final-destination only - transit messages aren't drained (the caller's `rev` is for their own mail, not for them to act as a downstream relay).

Server: `InboundConnectionHandler` parses `Command.Rev`, calls `Database.GetMessagesForCaller(callerBase, requestedIds)`, then runs the sender state machine via `DappsProtocolClient.OfferMessageAsync` / `SendMessageAsync` over the existing inbound stream. Successful drains mark the row `Forwarded=1`. TTL-expired rows are skipped (sweeper picks them up later).

Client: `DappsProtocolClient.PollAsync` issues `rev` (or `rev <ids>`), reads `ihave` lines, accepts each, hash-validates the payload, ACKs (or NAKs), and yields a `PolledMessage` per accepted message via `IAsyncEnumerable`. Returns when the server emits `DAPPSv1>`.

Opportunistic poll is on by default - `Dappsv1SessionBackhaul.SendAsync` follows every successful push with `rev` over the same session and hands each polled message to `IBackhaulInbox.DeliverAsync` exactly as if it had arrived via push. `SystemOptions.OpportunisticPollEnabled` toggle (default true) - operators can flip via `/Config`. Free in connection-time terms; turns every outbound session into a bidirectional drain, which is the difference between "B has my mail until B can reach me" and "B has my mail until I push to B."

13 new tests across `F3PollClientTests` (5 - empty drain, selective ids, single-message round-trip, hash-mismatch NAK, F2-fragment header passthrough), `F3PollServerTests` (5 - the database query in isolation + the inbound handler with rev), `Dappsv1SessionBackhaulOpportunisticPollTests` (3 - opportunistic-on, opportunistic-off, no-inbox).

F2 ↔ F3 compose naturally: a multi-part message stuck mid-forward at B sits in B's queue as N fragment rows. When A polls B (or pushes to B and opportunistically polls), B drains them. A's inbox routes each fragment to the reassembly buffer, eventually reassembles. No special interop code needed beyond passing `mid=` / `frag=N/M` through `PolledMessage` (which it does).

#### F3b - scheduled poll + dashboard *(done)*

For nodes that don't push often (read-only consumers, scheduled HF stations) the opportunistic mode never fires. `PollSchedulerService` mirrors `ProbeSchedulerService`: walks AGW-reachable neighbours on a slow cadence, opens a session, drains via `rev`, disconnects. UDP-only neighbours are excluded by design - F3 is AGW-only.

`NodePoller` is the single-shot worker: connect → wait for `DAPPSv1>` → `PollAsync` → deliver each polled message to `IBackhaulInbox`. Reused by both the scheduler and the on-demand REST endpoint, so an operator-triggered "poll now" exercises identical code to the cadenced sweep.

Operator state is per-callsign in `DbPolledNode` (PK Callsign, LastPolledAt, LastSuccessAt, LastError, ConsecutiveFailures, MessagesDrained, OptOut). Failure counter resets on first success after a streak. OptOut is operator state and survives result updates.

`SystemOptions.ScheduledPollEnabled` (off by default), `PollIntervalHours` (default 6). REST under `/Polls`: list, sweep-all, poll-one, set opt-out, forget. Dashboard panel surfaces the table with action buttons.

8 new tests in `F3PollSchedulerTests` covering target enumeration (no neighbours / AGW only / UDP excluded / opt-out excluded) and `PollAndRecordAsync` shape (success-clean, success-with-message, fail-then-succeed counter reset, opt-out preservation across result update).

### F4. Protocol versioning policy *(done)*

Decision pinned in `README.md` "On-air protocol" → "Versioning":

- Forward-compatible additions (new optional `ihave` headers, new commands) stay on the current prompt - receivers ignore unknown headers and respond `?` to unrecognised commands. F1 (`src=`), F2 (`mid=`, `frag=`), B6.1 (`peers`), F3 (`rev`) all rode `DAPPSv1>` under this rule.
- Incompatible changes (removed required fields, changed semantics, restructured frame syntax) bump the prompt - `DAPPSv2>` etc. Clean version cuts beat patching `v1` and hoping every implementation interprets the patch identically.
- Newer implementations SHOULD downgrade on detection - a `v2` client SHOULD speak `v1` to a node that emits `DAPPSv1>` if it can. The prompt is the natural carrier (first thing on the wire, before any state has been built up).
- The UDP datagram codec (`BackhaulMessage` binary format) versions independently - its own version byte, hard-fail on mismatch. Currently at v6 after the F2 fragment-header fix in #71. Independent because the two formats serve different bearers and evolve on different schedules.

The "feature negotiation on the prompt" alternative was rejected: it lets implementations drift in subtle ways that look fine in pairwise testing and break in N-way deployments. A version literal forces the conversation.

Pre-shipping caveat - also in the README - applies until non-author operators are on the air: while there's nobody to coordinate with, breaking changes still get to skip the version-bump, because the compatibility tape buys nothing. Policy fully kicks in when the first independent operator picks DAPPS up.

### F5. Authenticated message origin (signing) *(design parked - tracked as #139)*

Messages signed by the source node so receivers can verify the origin chain hasn't been tampered with. Encryption is illegal under amateur regulation; signing is fine. Design discussion happened mid-Phase-M but the work is parked - pulling forward when there's a real abuse case to defend against, or when the OARC community has appetite for a key-registry side-project.

**Sketch of the recommended shape (so the next pickup doesn't start cold):**

- **Algorithm: Ed25519.** 32-byte pubkey, 64-byte sig, ~150 µs verify on a Pi-class device, deterministic (no per-sign entropy - important for embedded HF stations), patent-free, native in .NET 8. ECDSA needs fresh randomness per sign; RSA is too heavy; Schnorr aggregation is overkill for v1.
- **Wire format:** new optional `sig=`, `sigtime=` (Unix epoch s), `kid=` (short key-id for rotation) headers on the `ihave` line. Per F4 these stay on `DAPPSv1>` as additive forward-compatible fields; pre-F5 receivers ignore them.
- **Canonical pre-image** for the signature covers id-inputs (salt + payload - already what the message-id hashes) plus `dst`, `ttl`, `originator`, `sigtime`. Excludes optional `key=value` extension headers (those are operator-extensibility, not security-critical).
- **Replay defence:** `sigtime` in the canonical bytes; receiver enforces ±7 days. Salt-based id dedup already covers exact-byte replays - `sigtime` makes timing-tampered ones detectable.
- **Identity binding (the hard part):** layered. **Primary** - a community registry (OARC could host a signed JSON file in a GitHub repo at a stable URL, daemons fetch on a slow refresh, operators submit pubkeys via PR or web form). **Fallback** - periodic key beacons broadcasting our pubkey as a special discovery-class frame; receivers cache `(callsign -> pubkey)` TOFU-style. DNS-based binding (DKIM-style TXT records) was considered but rejected for v1 because most amateur sysops don't own DNS. Web-of-trust deferred indefinitely (bootstrapping problem).
- **Three-tier verification:** no `sig=` -> backward-compatible (per-channel `RequireSignature` opt-in for stricter modes); sig present + verifies -> stamp `dapps-sig-ok=true` MQTT user property + green tick on dashboard; sig present + fails -> drop, record `sig.fail` event with originator and reason.
- **Wire impact:** ~70 bytes per signed message. ~0.6 s extra at 1200 baud, ~2.3 s at 300-baud HF. Bearable; per-channel toggle for short-message chat.

Out of scope even when this is picked up: confidentiality (illegal anyway), forward secrecy (irrelevant without confidentiality), per-message ephemeral keys, hardware key tokens.

Pickup signal: a real-world need (abuse case, cross-implementation interop concern, or OARC-side appetite for the registry). No need to pull forward speculatively.

## Phase H - concrete bearer integrations

**Bearer-specific work, distinct from the routing decisions in Phase B.** These are about *what wire forms DAPPS speaks*; routing logic (how DAPPS picks where to send) is solved one layer up and is bearer-agnostic. Each item here is an `IDappsBackhaul` (and optionally `IDiscoveryBearer`) implementation slotting under the existing seams without changing core routing.

### H1. MeshCore Companion-over-USB *(tracked as #137 alongside H2)*

Use a MeshCore companion radio (e.g. Heltec / TTGO LoRa boards running the MeshCore companion firmware) as a bearer. The companion exposes a serial protocol over USB; DAPPS opens that serial port, sends companion datagrams carrying our `BackhaulMessage` payloads, and listens for inbound datagrams. Discovery beacon support if the companion firmware allows it; otherwise rely on Phase B5's flood-then-learn over this bearer instead of explicit beacons.

### H2. MeshCore KISS-over-USB *(tracked as #137 alongside H1)*

Same hardware as H1 but via the MeshCore KISS interface - raw frames rather than companion-level datagrams. Trades convenience for control. Same `BackhaulMessage` codec; same packetiser. Probably done after H1 once H1 has shaken out the wire-shape questions.

### H3. RHPv2 (Remote Host Protocol v2) bearer *(XRouter half done)*

XRouter half **done** (PR #107): `Rhpv2OutboundTransport` + `Rhpv2InboundService` against `RhpV2.Client` 0.2.2. `DAPPS_NODE_BEARER=rhpv2` flips the bearer selector. Required (not optional) for DAPPS-on-XRouter because XRouter scopes the AGW callsign claim per-TCP-connection, breaking DAPPS's per-outbound-fresh-connection pattern; RHPv2 multiplexes inbound and outbound over a single connection with handle-based addressing, sidestepping the collision. Mainline BPQ does not yet ship RHPv2; the bearer is in place for it whenever it does. Discovery over RHPv2 (UI-frame-equivalent) is still pending - DAPPS uses AGW for discovery beacons today and RHPv2 for sessions; needs follow-up if XRouter exposes a UI-frame surface on RHPv2.

### H4. Other bearers as they show up

Anything that can carry a bytestring with sane addressing fits under `IDappsBackhaul`. KISS-over-TCP, AX.25-over-Ethernet, custom radio-modem JSON-RPC, …. Listed here so they don't get muddled with Phase B routing work.

## Phase G - community + governance

The README already says "multiple compatible implementations is healthy." Once the developer guide exists and there's a Python reference implementation:

- Move the spec into a versioned document (in `docs/spec/v1.md`, with future `v2.md` etc.).
- Issue tracker labels for spec discussion vs. implementation issues.
- Release notes / changelog discipline so implementers know what's changed.

Probably also worth a public conversation about whether DAPPS sits under OARC (the original RFC venue) or stays as a personal project - has implications for spec governance and license expectations.

## Phase M - MCP server endpoint *(done)*

An operator-assistant interface: expose DAPPS state and supervised actions to MCP clients (Claude Desktop, Claude Code, Cursor) so an LLM can compose diagnostic narratives, propose topology fixes, and run controlled probes on the operator's behalf. Sysop catches up after a week away with "what happened?" instead of grep'ing journals.

In-process: the dapps daemon hosts the MCP server at `/mcp` via `ModelContextProtocol.AspNetCore`. Same Kestrel listener, same DI graph as the rest of the daemon, so tools have direct access to `OperationalSnapshotBuilder`, `Database`, `OperationalMetrics`, etc. without the round-trip through REST. AdminAuthMiddleware allowlists `/mcp` alongside `/Health` and `/Operational` - clients (Claude / Cursor) don't carry an admin cookie. An MCP-specific token model can come later.

Five PRs landed (#81 bootstrap, #82 reads, #83 actions, #84 diagnostics, #85 exploration), 26 tools total:
- **Bootstrap (#81)** - framework wired in-process; one tool (`get_operational_snapshot`) wrapping the existing snapshot builder.
- **PR-A (#82)** - full read-only toolset: `get_recent_events`, `get_system_options`, `list_neighbours`, `list_discovered_peers`, `list_discovery_channels`, `list_learned_routes`, `list_discovered_paths`, `list_route_hints`, `list_probed_nodes`, `list_polled_nodes`, `get_message`, `list_recent_messages`, `list_dropped_messages`.
- **PR-B (#83)** - action tools: `run_probe`, `run_probe_sweep`, `run_poll`, `run_poll_sweep`, `run_solicit`, `send_test_message`, plus a single `update_config(partial)` setter that takes a typed `ConfigUpdate` record (every field nullable; null = no change). Excludes Callsign / NodeHost / AgwPort / MqttPort / UdpListenPort intentionally - those need a daemon restart and a wrong value takes a node off-air.
- **PR-C (#84)** - composite diagnostic narratives, the actual point: `explain_why_message_failed(id)` traces a message across messages + dropped + recent events + route resolution; `diagnose_silent_neighbour(callsign)` walks every state surface for a callsign; `summarize_recent_activity` aggregates the events ring; `find_path_to(destination)` deterministic resolution walk; `propose_topology_changes` heard-but-not-configured + high-failure-streak suggestions.
- **PR-D (#85)** - supervised exploration: `explore_via_neighbour(callsign)` probes a neighbour with peers-fetch and annotates the response NEW vs KNOWN; `opinion_on_route(destination)` ranks candidates with confidence + recommended next-action string. The agent suggests, the operator runs the recommended action tool to actually act.

What's explicitly out: autonomous routing-participation (algorithm stays deterministic; agent suggests, operator decides), anything that mutates other operators' nodes, long-term memory baked into the server (let the client own conversation context).

Package versioning side-effect: `ModelContextProtocol.AspNetCore` 1.2.0 transitively pulls Microsoft.Extensions.Logging.Abstractions 10.0.5 and System.IO.Pipelines 10.0.5. Both target net8.0 explicitly - they load fine on the LTS runtime; the version number is just the .NET 10 release train. Bumped accordingly in `Directory.Packages.props`.

## Cross-cutting: licensing housekeeping

Resolved during the toolchain refresh: tests use **AwesomeAssertions** (MIT-licensed community fork of FluentAssertions, drop-in API-compatible). FluentAssertions 8.x's switch to a paid Xceed licence isn't a concern. No further action.

## Cross-cutting: local multi-hop simulator

A dev-machine testbed for routing + forwarding work, in the absence of an RF test network. Maps **multicast group ≈ RF broadcast domain**: each group is a population of nodes that hear each other, and a node subscribed to multiple groups stands in for one within RF range of multiple disjoint populations.

Minimum topology - three `dapps.core` instances on the same host (different ports + DB paths):

- **A** - discovery channel `udp` `239.0.0.1:54321`
- **B** - discovery channels `udp` `239.0.0.1:54321` *and* `udp` `239.0.0.2:54321` (the relay)
- **C** - discovery channel `udp` `239.0.0.2:54321`

A's beacons reach B but not C; C's beacons reach B but not A; B is heard by both. For A→C to work, B must forward - which is exactly the multi-hop behaviour we want to exercise. Discovery already uses multicast (Phase B); message forwarding stays unicast to the discovered peer, which is also how RF works (broadcast discovery, point-to-point forward).

Validates:
- **F1** end-to-end source tracking - receiver at C sees originator A, not link-source B.
- **B5** flood-then-learn over a non-trivial graph - paths form and decay as channels go up/down.
- General routing / forwarding / TTL behaviour without burning RF time.

Doesn't validate:
- RF-specific behaviour: half-duplex, contention, lossy paths, AX.25 connect/disconnect quirks. For loss/jitter realism, layer `tc netem` on `lo`. AGW-bearer behaviour still needs a real BPQ in the loop.

Implementation cost is low - the UDP datagram bearer (A0.4) and discovery channels are already in place. Mostly a matter of a `scripts/sim-multihop.sh` that spins up three configured instances, plus a short README section so a contributor can reproduce. Worth doing as the *first* concrete validation harness for F1 or B5, before either lands.

**Status:** landed in PR #47 alongside F1. Six-node mesh (one branching relay; path lengths 1–4; an off-spine route that never touches A or B), driven by five canned exercises that exercise longest path, reverse longest path, off-spine, fan-out, and concurrent cross-traffic. Verifies F1 end-to-end at every receiver. Two real bugs surfaced during the build: one filed under "Open tasks" above (multicast bearer multi-channel-per-port leakage - channels in the simulator now use distinct ports per group as a workaround); the other is the missing automatic-forwarder-loop, also filed there (the simulator has to drain by hand after every send).

## Suggested ordering

Roughly:

1. **A0.1–A0.3** (backhaul seam) - *done*. **A1** (TTL forwarding) - *done*. **A2** (neighbour-table cleanup) - *done*.
3. **C1 + C2 + C4** (docker image, config tooling, install docs) - gets the thing into one sysop's hands.
4. **A4** (per-app auth) - *done*.
5. **D1 + D2 + D3 + D4** (web UI: inspection / exercise / config / auth) - *MVP done*. SSE inbound feed + manual `ihave` terminal still pending under D2.
6. **B1–B4** (channels-first-class discovery + cost-based resolver) - *done*. **B5** (learned-graph routing inside DAPPS, bearer-agnostic) - *done*. **B6.1** all phases (1: direct-connect liveness probes; 2: `peers` command + transitive discovery; 2b: node-prompt discovery against non-DAPPS NODECALLs, with auto-discovery wiring behind `AutoDiscoverViaNodeCall`) - *done*. **B6.2** (HF NVIS solicit-and-listen) - *done* including scheduled cadence. **B7** (airtime budget + probe strategies) - *done*.
7. **E1–E4** (concepts + tutorial + reference + sample-app gallery) - *done*. Developer guide is complete; third-party app development is unlocked.
8. **H** (concrete bearer integrations - MeshCore Companion, MeshCore KISS, RHP, …) on its own track, doesn't gate the routing or developer-guide work.
9. **A3** - *done*. **C5.1** (update-availability banner) - *done*. **C5.2** (triggered self-update) - *done*. **C3** (health/logs/observability) - *done*. **D3, D4, F1–F4** all *done*. **C5.3** (scheduled auto-update) *parked* - banner + one-click apply + `trigger_update` MCP tool already cover the current operator population; revisit when real third-party operators are on-air and drifting.
10. **Phase M** (MCP server endpoint exposing operator tools to LLMs) - *done* (#81–#85). 26 tools across reads / actions / config / composite diagnostics / supervised exploration.
11. **Phase G** (second-language reference impl) once the spec has been exercised by enough first-party apps that the ambiguities are likely to surface.

Phases A and C can ship as a single "v0.1.0 - runnable" release. Phase D as "v0.2.0 - operable". Phase B + E as "v1.0.0 - networked + developable".

## Useful pointers for a fresh session

If picking this up cold:

- `README.md` - current spec + app interface docs.
- `src/dapps/dapps.core/` - main app (ASP.NET Core hosted services).
- `src/dapps/dapps.client/` - sender-side library (`AgwOutboundTransport`, `DappsProtocolClient`, `DappsMessage`).
- `src/dapps/dapps.core/Services/` - `MqttBrokerService`, `OutboundMessageManager`, `InboundConnectionHandler`, `IHaveValidator`, `Database`, etc.
- `src/dapps/dapps.core.tests/` - unit (xunit.v3, MTP runner) + integration (Docker `m0lte/linbpq`).
- `docs/meshcore-backhaul-routing.md` - design note on the backhaul seam, MeshCore Companion/KISS implications, and MeshCore-inspired routing/discovery questions.
- `src/dapps/Directory.Packages.props` - central package versions; update there, not in csproj.
- `~/src/linbpq/docs/protocols/` - apps-interface.md, rhp.md, bpqtoagw.md - the BPQ protocol surfaces we built against.
- `~/src/linbpq/tests/integration/` - the linbpq test suite, especially `test_two_instance_agw_tunnel.py` for the topology pattern that feeds issue #6.
- Open issue #6 (two-instance AGW dispatch under bridge networking) - known test-infra gap. #5 closed in #77 (Testcontainers migration).

Test runner today is `dotnet test` - MTP filter syntax is `-- --filter-trait "Category=Integration"` (not `--filter Category=...`, which is the old VSTest syntax).

The protocol is frozen at v1. The implementation now performs TTL forwarding end-to-end across hops; remaining spec features that are still words-on-a-page are the explicit deferrals in Phase F (multi-part, end-to-end source, signing, polling). The integration-test setup proves the wire format is correct against real BPQ.
