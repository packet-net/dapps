using System.Text.Json.Serialization;
using SQLite;

namespace dapps.core.Models;

[Table("systemoptions")]
public class DbSystemOption
{
    [PrimaryKey]
    public string Option { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Plan B7 - probe-scheduler strategy. See
/// <see cref="SystemOptions.ProbeStrategy"/>. Serialised as the enum
/// name (e.g. <c>"Overnight"</c>) so the Settings form can POST the
/// value the &lt;select&gt; control yields without a per-field int
/// translation; without this, a string body fails validation and any
/// /Config save (even unrelated fields like Callsign) returns 400.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeStrategy
{
    /// <summary>Pre-B7 behaviour - sweep every
    /// <see cref="SystemOptions.ProbeIntervalHours"/>.</summary>
    FixedInterval = 0,
    /// <summary>One sweep per local-time day, inside the configured
    /// overnight window.</summary>
    Overnight = 1,
    /// <summary>Sweep on the fixed cadence, but defer if the outbound
    /// forwarder ran inside <see cref="SystemOptions.ProbeQuietWindowSeconds"/>
    /// seconds ago.</summary>
    WhenQuiet = 2,
}

public class SystemOptions
{
    /// <summary>Hostname or IP of the local packet node (BPQ, XRouter, ...).</summary>
    public string NodeHost { get; set; } = "";

    /// <summary>TCP port the node's AGW listener is on (AGW convention: 8000).</summary>
    public int AgwPort { get; set; }

    /// <summary>
    /// Which bearer DAPPS uses to talk to the local packet node.
    /// <c>agw</c> (default) - AGW host protocol; works with BPQ, Direwolf
    /// AGW server, AGWPE, etc. <c>rhpv2</c> - Remote Host Protocol v2;
    /// works with XRouter today and BPQ when it gains RHPv2 support.
    /// Operators pick one per installation. Restart required.
    /// </summary>
    public string NodeBearer { get; set; } = "agw";

    /// <summary>
    /// TCP port for the RHPv2 bearer. Only consulted when
    /// <see cref="NodeBearer"/> is <c>rhpv2</c>. Default 9000 per the
    /// RHPv2 spec; XRouter uses this by default.
    /// </summary>
    public int RhpPort { get; set; } = 9000;

    /// <summary>
    /// Optional RHPv2 authentication credentials. Most loopback
    /// deployments don't require auth. Empty string = skip the
    /// AuthenticateAsync step on connect.
    /// </summary>
    public string RhpUser { get; set; } = "";
    public string RhpPass { get; set; } = "";

    /// <summary>
    /// Default bearer port (0-indexed) to use when originating a connection
    /// to a neighbour DAPPS instance via AGW. Individual neighbours can
    /// override this with DbNeighbour.BearerPort.
    /// </summary>
    public int DefaultBearerPort { get; set; }

    /// <summary>This DAPPS instance's local callsign + SSID, used as `callfrom` on outbound AGW connects.</summary>
    public string Callsign { get; set; } = "";

    /// <summary>TCP port the embedded MQTT broker listens on for app-interface clients.</summary>
    public int MqttPort { get; set; }

    /// <summary>
    /// UDP port the datagram-backhaul listener binds on. Default 0 =
    /// disabled. Plan A0.4: a stand-in datagram bearer, validating the
    /// backhaul seam against fragmentation / reassembly before MeshCore
    /// lands.
    /// </summary>
    public int UdpListenPort { get; set; }

    // ---- MeshCore bearer (Phase H1, #154) ----

    /// <summary>Enable the MeshCore Companion-radio bearer. When true, DAPPS
    /// opens <see cref="MeshCorePort"/>, configures the radio, and carries
    /// BackhaulMessages over a private channel. Restart required.</summary>
    public bool MeshCoreEnabled { get; set; }

    /// <summary>Serial port of the attached MeshCore Companion radio.</summary>
    public string MeshCorePort { get; set; } = "/dev/ttyUSB0";

    /// <summary>Region preset (localisation): <c>uk-narrow</c>, <c>uk-test</c>,
    /// <c>eu-legacy</c>. Sets frequency/BW/SF/CR and caps TX power.</summary>
    public string MeshCoreRegion { get; set; } = "uk-test";

    /// <summary>TX power in dBm (capped by the region's regulatory max).</summary>
    public int MeshCoreTxPowerDbm { get; set; } = 8;

    /// <summary>Local channel slot (0 = public; use 1+ for a private channel).</summary>
    public int MeshCoreChannelIndex { get; set; } = 1;

    /// <summary>Channel name (local label).</summary>
    public string MeshCoreChannelName { get; set; } = "dapps";

    /// <summary>Channel PSK: a 32-char hex string (16 bytes) used verbatim, or
    /// any other value treated as a passphrase and hashed to 16 bytes.</summary>
    public string MeshCoreChannelPsk { get; set; } = "dapps-default-channel";

    /// <summary>Node advert name set on the radio.</summary>
    public string MeshCoreNodeName { get; set; } = "DAPPS";

    /// <summary>Self-enforced airtime budget (seconds per trailing hour) — the
    /// good-citizen governor. Default 30 ≈ 0.83% duty.</summary>
    public double MeshCoreAirtimeBudgetSecondsPerHour { get; set; } = 30;

    /// <summary>Compress the backhaul payload (zstd + shared dictionary).</summary>
    public bool MeshCoreCompress { get; set; } = true;

    /// <summary>Adaptive congestion backoff (#157): refuse sends when channel
    /// occupancy is at/above this fraction (0..1). 0 disables.</summary>
    public double MeshCoreCongestionBackoffFraction { get; set; } = 0.5;

    /// <summary>Listen-before-talk guard in ms (#157). 0 disables.</summary>
    public int MeshCoreLbtGuardMs { get; set; } = 400;

    /// <summary>End-to-end reliability (#26): ACK received messages + resend our own
    /// unacked messages until acked or their lifetime expires.</summary>
    public bool MeshCoreReliableDelivery { get; set; } = true;

    /// <summary>
    /// When true, app-interface clients (MQTT and REST) must present a
    /// valid token; topic / endpoint scope is also enforced against the
    /// authenticated app. When false, the app interface is open to
    /// anyone reachable on those ports - fine for single-host loopback,
    /// not fine for shared nodes. Plan A4.
    /// </summary>
    public bool AuthRequired { get; set; }

    /// <summary>
    /// When true, dapps periodically polls the GitHub Releases API and
    /// surfaces "v0.X.Y available" in the dashboard. Outbound HTTPS
    /// only; no operator-identifying data leaks (User-Agent is just
    /// <c>dapps/&lt;version&gt;</c>). Set to false for nodes that are
    /// firewalled off the public internet, or to opt out of the
    /// notification entirely. Plan C5.1.
    /// </summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// Plan B6.1 - connected-mode probe-and-map. When true, the
    /// <c>ProbeSchedulerService</c> walks known peers (manual
    /// neighbours + AGW-bearer discovered peers, less opt-outs) on
    /// a slow cadence and records reachability in
    /// <see cref="dapps.core.Models.DbProbedNode"/>. Off by default -
    /// probing uses other operators' airtime, so opt-in.
    /// </summary>
    public bool ProbingEnabled { get; set; } = false;

    /// <summary>
    /// Interval between full sweeps when <see cref="ProbingEnabled"/>
    /// is true. Default 24 hours - once a day is enough to spot a
    /// neighbour going dark without saturating slow links.
    /// </summary>
    public int ProbeIntervalHours { get; set; } = 24;

    /// <summary>
    /// Plan F2 - payloads strictly larger than this byte count are
    /// fragmented into chunks at submit time. The originator splits
    /// a 50 KB payload into ⌈50 KB / threshold⌉ rows; the receiver
    /// reassembles. End-to-end (intermediate hops forward fragments
    /// as opaque single messages). Bearers do their own framing
    /// underneath, so this knob is about *resumability* - the unit of
    /// retransmission after a link drop or crash mid-transfer - not
    /// about MTU adaptation. 0 disables fragmentation entirely.
    /// </summary>
    public int FragmentThresholdBytes { get; set; } = 4096;

    /// <summary>
    /// Plan F2 - drop incomplete reassembly buffer rows older than
    /// this. Default 7 days because HF / mesh propagation gaps can
    /// cleanly close for multiple days mid-transmission and we'd
    /// rather hold the partial work than throw it away. Operators on
    /// always-on links can shorten this aggressively.
    /// </summary>
    public int FragmentReassemblyTimeoutSeconds { get; set; } = 7 * 24 * 3600;

    /// <summary>
    /// Route gossip: minimum hours between consecutive <c>routes</c>
    /// pulls from the same neighbour. The piggyback gate skips the
    /// gossip step on a session if the previous pull is younger than
    /// this. Default 6 hours; <c>0</c> disables gossip entirely
    /// (no <c>routes</c> command exchanged either way).
    ///
    /// <para>
    /// Pulls only happen on a session that's already opened for real
    /// work (a push, a probe, an opportunistic poll). The staleness
    /// floor bounds airtime cost without adding any scheduled
    /// transmission.
    /// </para>
    /// </summary>
    public int RouteGossipStalenessHours { get; set; } = 6;

    /// <summary>
    /// Plan F3 - opportunistic poll on every successful push. After
    /// <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/>
    /// finishes pushing a message, send <c>rev</c> on the same session
    /// to drain anything the remote has queued for us. Free in
    /// connection-time terms (the link is already up) and turns every
    /// outbound session into a bidirectional drain - the difference
    /// between "B has my mail until B can reach me" and "B has my mail
    /// until I push to B." Default true; disable for nodes that want
    /// to push without ever pulling.
    /// </summary>
    public bool OpportunisticPollEnabled { get; set; } = true;

    /// <summary>
    /// Plan F3b - scheduled poll. When true, the
    /// <c>PollSchedulerService</c> walks every AGW-reachable manual
    /// neighbour on a slow cadence and drains queued mail via
    /// <c>rev</c>. Off by default - opportunistic poll on every push
    /// covers the majority of cases for free; this is for nodes that
    /// don't push often (read-only consumers, scheduled HF stations)
    /// and would otherwise let mail rot at their forwarding partners.
    /// </summary>
    public bool ScheduledPollEnabled { get; set; } = false;

    /// <summary>
    /// Interval between full poll sweeps when
    /// <see cref="ScheduledPollEnabled"/> is true. Default 6 hours -
    /// shorter than the probe scheduler's daily cadence because
    /// polling drains real mail that an operator wants delivered, not
    /// just liveness checks.
    /// </summary>
    public int PollIntervalHours { get; set; } = 6;

    /// <summary>
    /// Plan B6.1 Phase 2b - when true, every inbound AGW DAPPS beacon
    /// seeds a node-prompt-probe candidate for the BASE callsign of the
    /// beacon's source (e.g. heard <c>M0LTE-9</c> → seed <c>M0LTE</c>).
    /// The probe scheduler then picks them up alongside regular probe
    /// targets, navigates the BPQ node prompt, and runs the standard
    /// peers exchange. Useful for expanding the discoverable graph
    /// beyond direct DAPPS beacons. Off by default - opt in.
    /// </summary>
    public bool AutoDiscoverViaNodeCall { get; set; } = false;

    /// <summary>
    /// Application command typed at the BPQ node prompt to enter the
    /// DAPPS APPLICATION slot. Default <c>DAPPS</c> matches the
    /// recommended convention. Operators with a different
    /// <c>APPLICATIONS=</c> name override here.
    /// </summary>
    public string NodePromptApplicationCommand { get; set; } = "DAPPS";

    /// <summary>
    /// Plan C3 PR-B - periodic MQTT heartbeat publish. When true, an
    /// <see cref="dapps.core.Services.OperationalSnapshot"/> is
    /// serialised and published as a retained message on
    /// <c>dapps/metrics/heartbeat</c> every
    /// <see cref="HeartbeatIntervalSeconds"/> seconds. Lets operator-
    /// side consumers (Home Assistant, simple MQTT subscribers) wire
    /// DAPPS into their dashboards without any new infra. Default on
    /// - the broker is already running for the app interface, so
    /// the publish is effectively free.
    /// </summary>
    public bool HeartbeatEnabled { get; set; } = true;

    /// <summary>Seconds between heartbeat publishes when
    /// <see cref="HeartbeatEnabled"/> is true. Default 60. Clamped
    /// to a minimum of 10 by the publisher to avoid pathological
    /// settings.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Plan B7 - single trailing-hour cap on airtime consumed by ALL
    /// discovery-class transmissions (beacons, solicit replies,
    /// probes). 0 = unlimited (default - preserves pre-B7 behaviour).
    /// Set to a positive value to enforce; the airtime accountant
    /// defers transmissions whose estimated cost would push the
    /// last-hour total past the budget. Operators on shared 1200-
    /// baud VHF or HF channels are the use case.
    /// </summary>
    public int DiscoveryAirtimeBudgetSecondsPerHour { get; set; } = 0;

    /// <summary>
    /// Plan B7 - strategy controlling when probe sweeps fire. Default
    /// <see cref="Models.ProbeStrategy.FixedInterval"/> matches the
    /// pre-B7 behaviour (sweep every <see cref="ProbeIntervalHours"/>).
    /// <see cref="Models.ProbeStrategy.Overnight"/> sweeps once per
    /// local-time day inside [<see cref="ProbeOvernightStartHour"/>,
    /// <see cref="ProbeOvernightEndHour"/>). <see cref="Models.ProbeStrategy.WhenQuiet"/>
    /// sweeps on the same fixed cadence but defers each tick if the
    /// outbound forwarder transmitted in the last
    /// <see cref="ProbeQuietWindowSeconds"/> seconds.
    /// </summary>
    public ProbeStrategy ProbeStrategy { get; set; } = ProbeStrategy.FixedInterval;

    /// <summary>Local-time hour (0–23) at which the Overnight strategy's
    /// nightly sweep window opens. Default 02:00 local.</summary>
    public int ProbeOvernightStartHour { get; set; } = 2;

    /// <summary>Local-time hour (0–23) at which the Overnight strategy's
    /// nightly sweep window closes. Default 06:00 local. If the end
    /// hour is less than the start hour the window straddles midnight
    /// (e.g. start=22, end=6 = 22:00 through 06:00 next morning).</summary>
    public int ProbeOvernightEndHour { get; set; } = 6;

    /// <summary>Seconds of quiet required before the WhenQuiet strategy
    /// will fire a sweep. Default 300 (5 minutes).</summary>
    public int ProbeQuietWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Which routing algorithm composition to use. Two stacks are
    /// shipped today; both wrap <see cref="dapps.core.Routing.StaticRoutingAlgorithm"/>
    /// so operator overrides always win.
    ///
    /// <list type="bullet">
    /// <item><c>passive-flood</c> (default) - AODV-flavoured passive
    ///   learning of next-hop routes from F1 src= observations,
    ///   with bounded-flood as cold-start fallback. Stores per-
    ///   destination next-hop only.</item>
    /// <item><c>meshcore</c> - DSR-style source routing with passive
    ///   discovery. Stores the full path in
    ///   <see cref="dapps.core.Models.DbDiscoveredPath"/> so
    ///   subsequent sends embed the route on the wire instead of
    ///   re-resolving at each hop. First send to an unknown
    ///   destination triggers a flood-discovery whose accumulated
    ///   TraversedHops give every transiting node a path back to
    ///   the originator.</item>
    /// </list>
    ///
    /// Algorithm choice is global per-node and applied at startup;
    /// changing requires a restart.
    /// </summary>
    public string RoutingAlgorithm { get; set; } = "passive-flood";

    /// <summary>
    /// Operator master TX kill-switch. When false, every bearer's
    /// chokepoint refuses to put bytes that produce on-air emissions
    /// on the wire - forwards, floods, beacons, probes, polls, all
    /// blocked at the AGW frame / RHP open / UDP send level. Inbound
    /// RX is unaffected. Disconnect frames and node-control admin
    /// frames stay enabled so the BPQ/XR session remains usable and
    /// in-flight sessions tear down cleanly. Toggle via the dashboard
    /// header; persists across restart. Default true.
    /// </summary>
    public bool TxEnabled { get; set; } = true;

    /// <summary>
    /// When true, every outbound transmission (beacon, solicit, probe,
    /// forward, poll, ack, heartbeat) is logged to the
    /// <see cref="DbTransmission"/> table for operator-side audit.
    /// Default true - the storage cost is small (a few hundred rows
    /// per day on a typical node) and the value is high for
    /// post-mortems and regulatory traceability. Disable only if
    /// you're storage-constrained on a tiny SD card.
    /// </summary>
    public bool TransmissionAuditEnabled { get; set; } = true;

    /// <summary>
    /// Days of <see cref="DbTransmission"/> rows the retention sweeper
    /// keeps. Older rows are deleted on the sweeper's tick. Default
    /// 90 days - enough for most "what happened last month" lookups
    /// without growing the database without bound. Set to 0 to disable
    /// automatic retention (keep everything; operator manages purges
    /// manually).
    /// </summary>
    public int TransmissionAuditRetentionDays { get; set; } = 90;

    /// <summary>
    /// When true, every transmission audit row is also published to
    /// MQTT topic <c>dapps/audit/tx</c> as a JSON document, so
    /// operators with an existing MQTT-based monitoring stack can
    /// scrape transmissions live. Off by default - opt in once you
    /// know you want it (it's a per-transmission publish, so it's
    /// noisier than the heartbeat).
    /// </summary>
    public bool TransmissionAuditMqttPublish { get; set; } = false;
}
