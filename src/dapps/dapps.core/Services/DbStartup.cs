using System.Text;
using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

public static class DbInfo
{
    /// <summary>Test override for the SQLite path. Production uses the
    /// default `data/dapps.db` (or `dapps.db` if no `data/` dir).</summary>
    public static string? OverridePath { get; set; }

    private static string GetPath()
    {
        if (!string.IsNullOrEmpty(OverridePath)) return OverridePath;
        if (Directory.Exists("data")) return "data/dapps.db";
        return "dapps.db";
    }

    public static SQLiteConnection GetConnection() => new(GetPath());

    public static SQLiteAsyncConnection GetAsyncConnection() => new(GetPath());
}

public static class DbStartup
{
    /// <summary>Sentinel callsign in the seeded defaults. The daemon
    /// starts with this in place but refuses to operate (won't bind
    /// the inbound bearer, won't forward outbound, /Health reports
    /// degraded with <c>setupRequired</c>) until the operator configures
    /// a real callsign via /Setup or /Config. Frames stamped with the
    /// placeholder never go on the air.</summary>
    public const string PlaceholderCallsign = "N0CALL";

    /// <summary>Mode switch (read from the environment at every start,
    /// never stored): when set to <c>true</c> the deployment - not the
    /// dashboard - owns every option whose <c>DAPPS_*</c> env var is
    /// set. See <see cref="IsEnvManagedMode"/>.</summary>
    public const string EnvManagedModeVar = "DAPPS_ENV_MANAGED";

    /// <summary>Option key holding a callsign that was derived from the
    /// host node (<see cref="NodeCallsignEnvVar"/>) but has not yet been
    /// confirmed free by a successful RHPv2 listen. While this row
    /// matches the stored callsign, <c>Rhpv2InboundService</c> may
    /// probe-walk to a free SSID if the node refuses the derived one
    /// (errCode 9 "Duplicate socket"); once a listen succeeds the row
    /// is cleared and the confirmed identity is stable on every later
    /// start. Not a seeded option - no env var, no dashboard field.</summary>
    public const string DerivedCallsignPendingKey = "DerivedCallsignPending";

    /// <summary>Option key holding the SSID used when deriving this
    /// instance's callsign from the host node's callsign (see
    /// <see cref="NodeCallsignEnvVar"/>). Seeded to <see cref="DefaultSsid"/>;
    /// env-overridable as <c>DAPPS_SSID</c> like every other seeded
    /// option. Only consulted at derivation time - a stored or
    /// env-supplied <c>Callsign</c> always wins.</summary>
    public const string SsidOptionKey = "Ssid";

    /// <summary>Proposed convention: DAPPS lives at SSID -7 of the host
    /// node's callsign (matches the M0LTE-7 acceptance-test identity).
    /// There is no packet-wide DAPPS SSID convention yet; this default
    /// is the proposal.</summary>
    public const string DefaultSsid = "7";

    /// <summary>Injected by a pdn host into supervised app processes:
    /// the node's own callsign text, e.g. <c>M9YYY</c> (may carry an
    /// SSID, which we strip before composing). Absent when DAPPS runs
    /// standalone alongside BPQ/XRouter.</summary>
    public const string NodeCallsignEnvVar = "PDN_NODE_CALLSIGN";

    /// <summary>Injected by a pdn host (node-owned-callsign contract):
    /// the exact callsign this app must bind on air, e.g.
    /// <c>M9YYY-7</c>. When set and non-empty the node is the callsign
    /// authority: DAPPS binds it verbatim with NO self-derivation and NO
    /// SSID probe. Absent/empty falls back to the legacy
    /// <see cref="NodeCallsignEnvVar"/> derivation / <c>DAPPS_CALLSIGN</c>
    /// path so an older node or a standalone install still works.</summary>
    public const string AppCallsignEnvVar = "PDN_APP_CALLSIGN";

    /// <summary>
    /// Every option key EnsureSchemaAndSeed seeds, with its hardcoded
    /// fallback default. Single source of truth for seeding, for the
    /// per-start env application, and for the dashboard's
    /// "managed by environment" markers.
    /// </summary>
    private static readonly (string Key, string Default)[] SeededOptions =
    [
        ("NodeHost", "localhost"),
        ("AgwPort", "8000"),
        ("DefaultBearerPort", "0"),
        ("Callsign", PlaceholderCallsign),
        ("MqttPort", "1883"),
        ("UdpListenPort", "0"),
        ("MeshCoreEnabled", "false"),
        ("MeshCorePort", "/dev/ttyUSB0"),
        ("MeshCoreRegion", "uk-test"),
        ("MeshCoreTxPowerDbm", "8"),
        ("MeshCoreChannelIndex", "1"),
        ("MeshCoreChannelName", "dapps"),
        ("MeshCoreChannelPsk", "dapps-default-channel"),
        ("MeshCoreNodeName", "DAPPS"),
        ("MeshCoreAirtimeBudgetSecondsPerHour", "30"),
        ("MeshCoreCompress", "true"),
        ("MeshCoreCongestionBackoffFraction", "0.5"),
        ("MeshCoreLbtGuardMs", "400"),
        ("MeshCoreReliableDelivery", "true"),
        ("AuthRequired", "false"),
        ("UpdateCheckEnabled", "true"),
        ("RoutingAlgorithm", "passive-flood"),
        ("ProbingEnabled", "false"),
        ("ProbeIntervalHours", "24"),
        ("FragmentThresholdBytes", "4096"),
        ("FragmentReassemblyTimeoutSeconds", "604800"),
        ("RouteGossipStalenessHours", "6"),
        ("OpportunisticPollEnabled", "true"),
        ("ScheduledPollEnabled", "false"),
        ("PollIntervalHours", "6"),
        ("DiscoveryAirtimeBudgetSecondsPerHour", "0"),
        ("ProbeStrategy", nameof(Models.ProbeStrategy.FixedInterval)),
        ("ProbeOvernightStartHour", "2"),
        ("ProbeOvernightEndHour", "6"),
        ("ProbeQuietWindowSeconds", "300"),
        ("HeartbeatEnabled", "true"),
        ("HeartbeatIntervalSeconds", "60"),
        ("AutoDiscoverViaNodeCall", "false"),
        ("NodePromptApplicationCommand", "DAPPS"),
        ("NodeBearer", "agw"),
        ("RhpPort", "9000"),
        ("RhpUser", ""),
        ("RhpPass", ""),
        (SsidOptionKey, DefaultSsid),
    ];

    /// <summary>True when <see cref="EnvManagedModeVar"/> is set to
    /// <c>true</c> (or <c>1</c>): the deployment-managed mode, opted
    /// into by pdn-supervised installs via pdn-app.yaml. In this mode
    /// every set <c>DAPPS_*</c> env var is re-applied over the stored
    /// row at every start and the dashboard badges those fields
    /// read-only. Unset / any other value = the standalone default:
    /// env vars seed missing rows on first start only and the
    /// dashboard owns everything thereafter (the shipped
    /// scripts/dapps.service flow keeps /etc/dapps.env applied
    /// forever, so re-applying would revert dashboard edits there).</summary>
    public static bool IsEnvManagedMode
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(EnvManagedModeVar);
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }

    /// <summary>
    /// Create every table the daemon needs, seed the first-run
    /// systemoptions defaults (env-var overrides → hardcoded fallback),
    /// and - only when <see cref="IsEnvManagedMode"/> - re-apply any
    /// env-set values to existing rows (deployment-managed config).
    /// Safe to call multiple times - every step is idempotent.
    ///
    /// Called once from Program.cs *before* <c>builder.Build()</c> so
    /// the eager DI materialisation of hosted services (which transit
    /// IRoutingAlgorithm → IOptionsMonitor&lt;SystemOptions&gt;.CurrentValue
    /// → <see cref="SystemOptionsStore"/>'s constructor read) finds a
    /// seeded table rather than racing it.
    /// </summary>
    public static void EnsureSchemaAndSeed(ILogger? logger = null)
    {
        using var db = DbInfo.GetConnection();
        logger?.LogInformation($"DB: {db.DatabasePath}");

        db.CreateTable<DbOffer>();
        db.CreateTable<DbMessage>();
        db.CreateTable<DbSystemOption>();
        db.CreateTable<DbRouteHint>();
        db.CreateTable<DbNeighbour>();
        db.CreateTable<DbAppToken>();
        db.CreateTable<DbDiscoveredPeer>();
        db.CreateTable<DbDiscoveryChannel>();
        db.CreateTable<DbDroppedMessage>();
        db.CreateTable<DbLearnedRoute>();
        db.CreateTable<DbFloodSeen>();
        db.CreateTable<DbDiscoveredPath>();
        db.CreateTable<DbProbedNode>();
        db.CreateTable<DbFragment>();
        db.CreateTable<DbPolledNode>();
        db.CreateTable<DbTransmission>();
        db.CreateTable<DbStreamSendState>();
        db.CreateTable<DbStreamRecvState>();
        db.CreateTable<DbRouteGossipState>();

        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");

        // Seeded defaults. A set env var DAPPS_<KEY> always wins for a
        // row that doesn't exist yet (first start). What happens to an
        // EXISTING row depends on the DAPPS_ENV_MANAGED mode switch:
        //
        //   unset/false (the standalone default): env vars seed only;
        //     stored (dashboard-edited) values are never touched on
        //     later starts. The shipped standalone flow
        //     (scripts/dapps.service + EnvironmentFile=/etc/dapps.env)
        //     keeps DAPPS_* set permanently, so re-applying would
        //     silently revert every dashboard edit - hence opt-in.
        //
        //   true (the pdn supervised-app case; set in pdn-app.yaml):
        //     every set DAPPS_<KEY> is deployment-managed config and is
        //     re-applied over whatever the row holds at EVERY start,
        //     and the dashboard badges those fields read-only.
        foreach (var (key, defaultValue) in SeededOptions)
        {
            SeedOrApplyEnv(db, options, key, defaultValue, logger);
        }

        // Node-owned-callsign contract: when the pdn host names the
        // exact callsign to bind (PDN_APP_CALLSIGN), it is the authority -
        // applied verbatim over the stored Callsign row at EVERY start,
        // ahead of (and overriding) the legacy self-derivation. Falls
        // through to DeriveCallsignFromHostNodeIfUnset when absent/empty.
        ApplyNodeAssignedCallsignIfPresent(db, logger);

        DeriveCallsignFromHostNodeIfUnset(db, logger);

        ValidateRequiredConfig(db, logger);

        logger?.LogInformation("DB schema refreshed");
    }

    private static void SeedOrApplyEnv(SQLiteConnection db, List<DbSystemOption> options, string key, string defaultValue, ILogger? logger)
    {
        var envKey = EnvVarFor(key);
        var envValue = Environment.GetEnvironmentVariable(envKey);

        var existing = options.FirstOrDefault(o => string.Equals(o.Option, key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var value = string.IsNullOrEmpty(envValue) ? defaultValue : envValue;
            db.Insert(new DbSystemOption { Option = key, Value = value });

            if (!string.IsNullOrEmpty(envValue))
            {
                logger?.LogInformation("Seeded {0} from {1}", key, envKey);
            }
            return;
        }

        if (IsEnvManagedMode && !string.IsNullOrEmpty(envValue) && !string.Equals(existing.Value, envValue, StringComparison.Ordinal))
        {
            existing.Value = envValue;
            db.Update(existing);
            logger?.LogInformation(
                "SystemOption {Key} applied from environment ({EnvVar}) — this value is deployment-managed; " +
                "dashboard edits will be overridden while the variable remains set",
                key, envKey);
        }
    }

    /// <summary>The exact on-air callsign the pdn host assigned this app
    /// via <see cref="AppCallsignEnvVar"/>, trimmed; null when the var is
    /// unset/empty (legacy node or standalone). When non-null the node is
    /// the callsign authority and DAPPS binds this verbatim - no
    /// derivation, no SSID probe.</summary>
    public static string? ReadNodeAssignedCallsign()
    {
        var value = Environment.GetEnvironmentVariable(AppCallsignEnvVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Node-owned-callsign contract: if the pdn host set
    /// <see cref="AppCallsignEnvVar"/> to a non-empty callsign, write it
    /// to the stored <c>Callsign</c> row (every start - the node owns the
    /// identity) and clear any leftover derivation-pending marker so the
    /// RHPv2 listener binds it verbatim and never probe-walks. No-op when
    /// the var is absent/empty, leaving the legacy derivation /
    /// <c>DAPPS_CALLSIGN</c> path in charge.
    /// </summary>
    private static void ApplyNodeAssignedCallsignIfPresent(SQLiteConnection db, ILogger? logger)
    {
        var assigned = ReadNodeAssignedCallsign();
        if (assigned is null)
        {
            return;
        }

        var options = db.Query<DbSystemOption>("select * from systemoptions;");
        var callsignRow = options.FirstOrDefault(
            o => string.Equals(o.Option, "Callsign", StringComparison.OrdinalIgnoreCase));
        if (callsignRow is null)
        {
            db.Insert(new DbSystemOption { Option = "Callsign", Value = assigned });
        }
        else if (!string.Equals(callsignRow.Value, assigned, StringComparison.Ordinal))
        {
            callsignRow.Value = assigned;
            db.Update(callsignRow);
        }

        // The node pinned the identity; nothing is pending confirmation,
        // and the listener must never walk SSIDs off it.
        var markerRow = options.FirstOrDefault(
            o => string.Equals(o.Option, DerivedCallsignPendingKey, StringComparison.OrdinalIgnoreCase));
        ClearPendingMarker(db, markerRow);

        logger?.LogInformation(
            "Callsign {Assigned} assigned by the host node ({EnvVar}); binding it verbatim " +
            "(node-owned-callsign contract - no self-derivation, no SSID probe).",
            assigned, AppCallsignEnvVar);
    }

    /// <summary>
    /// "DAPPS resides at an SSID of the node callsign": when DAPPS runs
    /// supervised under a pdn node, the host injects
    /// <see cref="NodeCallsignEnvVar"/> and we derive
    /// <c>&lt;base-of-node-call&gt;-&lt;Ssid&gt;</c> as the callsign -
    /// but only while the stored callsign is absent or still the
    /// <see cref="PlaceholderCallsign"/> placeholder. An explicit
    /// <c>DAPPS_CALLSIGN</c> env var or a real stored callsign always
    /// wins over derivation. Standalone installs (no PDN_NODE_CALLSIGN)
    /// are untouched.
    /// </summary>
    private static void DeriveCallsignFromHostNodeIfUnset(SQLiteConnection db, ILogger? logger)
    {
        var options = db.Query<DbSystemOption>("select * from systemoptions;");
        var callsignRow = options.FirstOrDefault(
            o => string.Equals(o.Option, "Callsign", StringComparison.OrdinalIgnoreCase));
        var markerRow = options.FirstOrDefault(
            o => string.Equals(o.Option, DerivedCallsignPendingKey, StringComparison.OrdinalIgnoreCase));
        var stored = callsignRow?.Value ?? "";

        // The node-owned-callsign contract wins over everything: if the
        // host assigned PDN_APP_CALLSIGN it was already written above, so
        // never self-derive underneath it (this also keeps a pathological
        // PDN_APP_CALLSIGN=N0CALL from being re-derived).
        if (ReadNodeAssignedCallsign() is not null)
        {
            ClearPendingMarker(db, markerRow); // node pinned the identity - nothing pending
            return;
        }

        // An explicit DAPPS_CALLSIGN wins over derivation. (It was
        // already applied above; this guard also keeps a pathological
        // DAPPS_CALLSIGN=N0CALL from being re-derived underneath.)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarFor("Callsign"))))
        {
            ClearPendingMarker(db, markerRow); // identity explicitly pinned - nothing pending
            return;
        }

        var unset = string.IsNullOrWhiteSpace(stored)
            || string.Equals(stored, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        if (!unset)
        {
            // A real stored callsign always wins. Keep the pending
            // marker only while it still matches the stored value (=
            // a derivation from a previous start that the RHPv2
            // listener hasn't confirmed yet); a dashboard-configured
            // callsign clears it so an explicit identity never walks.
            if (markerRow is not null
                && !string.Equals(markerRow.Value, stored, StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingMarker(db, markerRow);
            }
            return;
        }

        var ssidRow = options.FirstOrDefault(
            o => string.Equals(o.Option, SsidOptionKey, StringComparison.OrdinalIgnoreCase));
        var ssid = string.IsNullOrWhiteSpace(ssidRow?.Value) ? DefaultSsid : ssidRow!.Value.Trim();
        var derived = DeriveCallsignFromHostNode(ssid);
        if (derived is null)
        {
            // Not pdn-hosted - standalone setup-required flow as before.
            // A leftover marker (e.g. the host stopped injecting
            // PDN_NODE_CALLSIGN) can't match the placeholder; drop it.
            ClearPendingMarker(db, markerRow);
            return;
        }

        if (callsignRow is null)
        {
            db.Insert(new DbSystemOption { Option = "Callsign", Value = derived });
        }
        else
        {
            callsignRow.Value = derived;
            db.Update(callsignRow);
        }

        // Mark the derivation as not-yet-confirmed: if the node refuses
        // the listen with errCode 9 (callsign already claimed there),
        // Rhpv2InboundService probe-walks the SSIDs for a free one and
        // persists the winner; the first successful listen clears this.
        if (markerRow is null)
        {
            db.Insert(new DbSystemOption { Option = DerivedCallsignPendingKey, Value = derived });
        }
        else if (!string.Equals(markerRow.Value, derived, StringComparison.Ordinal))
        {
            markerRow.Value = derived;
            db.Update(markerRow);
        }

        logger?.LogInformation(
            "Callsign {Derived} derived from the host node ({EnvVar}={NodeCall}, SSID {Ssid}). " +
            "If the node already has a {DerivedDup} the RHPv2 listener will probe for a free SSID and keep it. " +
            "Set DAPPS_CALLSIGN or configure a callsign via the dashboard to pin a different identity.",
            derived, NodeCallsignEnvVar, Environment.GetEnvironmentVariable(NodeCallsignEnvVar), ssid, derived);
    }

    private static void ClearPendingMarker(SQLiteConnection db, DbSystemOption? markerRow)
    {
        if (markerRow is not null)
        {
            db.Execute("delete from systemoptions where option = ?;", markerRow.Option);
        }
    }

    /// <summary>The derived callsign awaiting its first successful
    /// listen, or null when none is pending. See
    /// <see cref="DerivedCallsignPendingKey"/>.</summary>
    public static string? ReadPendingDerivedCallsign()
    {
        using var db = DbInfo.GetConnection();
        db.CreateTable<DbSystemOption>();
        var row = db.Query<DbSystemOption>(
                "select * from systemoptions where option = ? collate nocase;", DerivedCallsignPendingKey)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(row?.Value) ? null : row!.Value;
    }

    /// <summary>A listen succeeded on <paramref name="confirmed"/>:
    /// persist it as the stored callsign (it may differ from the
    /// original derivation after a probe-walk) and clear the pending
    /// marker so the identity is stable on every later start - a
    /// persisted, confirmed callsign never walks again.</summary>
    public static void ConfirmDerivedCallsign(string confirmed)
    {
        using var db = DbInfo.GetConnection();
        var callsignRow = db.Query<DbSystemOption>(
                "select * from systemoptions where option = ? collate nocase;", "Callsign")
            .FirstOrDefault();
        if (callsignRow is null)
        {
            db.Insert(new DbSystemOption { Option = "Callsign", Value = confirmed });
        }
        else if (!string.Equals(callsignRow.Value, confirmed, StringComparison.Ordinal))
        {
            callsignRow.Value = confirmed;
            db.Update(callsignRow);
        }
        db.Execute("delete from systemoptions where option = ? collate nocase;", DerivedCallsignPendingKey);
    }

    /// <summary>Every candidate SSID was taken on the node: drop back
    /// to the placeholder (setup-required mode - the bearer gates idle
    /// instead of hammering the node) and clear the pending marker.
    /// The operator pins an identity via the dashboard / DAPPS_CALLSIGN,
    /// or the next daemon restart re-derives and probes again.</summary>
    public static void AbandonDerivedCallsign()
    {
        using var db = DbInfo.GetConnection();
        db.Execute("update systemoptions set value = ? where option = ? collate nocase;",
            PlaceholderCallsign, "Callsign");
        db.Execute("delete from systemoptions where option = ? collate nocase;", DerivedCallsignPendingKey);
    }

    /// <summary>Compose the conventional pdn-hosted DAPPS callsign:
    /// base of the host node's callsign (any SSID stripped, upper-cased)
    /// + <paramref name="ssid"/>. Null when <see cref="NodeCallsignEnvVar"/>
    /// is not set or empty (standalone install).</summary>
    public static string? DeriveCallsignFromHostNode(string ssid)
    {
        var nodeCall = Environment.GetEnvironmentVariable(NodeCallsignEnvVar);
        if (string.IsNullOrWhiteSpace(nodeCall))
        {
            return null;
        }

        var baseCall = nodeCall.Trim().ToUpperInvariant().Split('-')[0].Trim();
        if (baseCall.Length == 0)
        {
            return null;
        }

        return $"{baseCall}-{ssid}";
    }

    /// <summary>Overload reading the stored Ssid option (fallback
    /// <see cref="DefaultSsid"/>). Used by /Setup to prefill the
    /// callsign field when the daemon is pdn-hosted and still in
    /// setup-required mode.</summary>
    public static string? DeriveCallsignFromHostNode()
    {
        using var db = DbInfo.GetConnection();
        db.CreateTable<DbSystemOption>();
        var ssidRow = db.Query<DbSystemOption>("select * from systemoptions;")
            .FirstOrDefault(o => string.Equals(o.Option, SsidOptionKey, StringComparison.OrdinalIgnoreCase));
        var ssid = string.IsNullOrWhiteSpace(ssidRow?.Value) ? DefaultSsid : ssidRow!.Value.Trim();
        return DeriveCallsignFromHostNode(ssid);
    }

    /// <summary>The env var that overrides the given seeded option key,
    /// e.g. <c>NodeHost</c> → <c>DAPPS_NODE_HOST</c>.</summary>
    public static string EnvVarFor(string key) => "DAPPS_" + ToScreamingSnake(key);

    /// <summary>True when the daemon runs in deployment-managed mode
    /// (<see cref="IsEnvManagedMode"/>) AND the given option key's
    /// <c>DAPPS_*</c> env var is currently set (non-empty) - i.e. the
    /// value is re-applied from the environment at every start. Always
    /// false in the standalone default mode, where a set env var only
    /// seeds first-start values and the dashboard stays in charge.</summary>
    public static bool IsEnvManaged(string key) =>
        IsEnvManagedMode && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarFor(key)));

    /// <summary>Seeded option keys that are deployment-managed right
    /// now (<see cref="IsEnvManaged"/>), for the dashboard's "managed
    /// by environment" field markers. Empty outside
    /// <see cref="IsEnvManagedMode"/>.</summary>
    public static IReadOnlyList<string> EnvManagedKeys() =>
        SeededOptions.Select(s => s.Key).Where(IsEnvManaged).ToArray();

    /// <summary>
    /// Warn if the callsign is the seeded placeholder. The daemon starts
    /// either way - inbound bearer services and the outbound forwarder
    /// gate themselves on a real callsign at runtime, and /Health reports
    /// <c>setupRequired</c> until the operator configures one via the
    /// dashboard's /Setup or /Config form. Letting the daemon start with
    /// the placeholder is what makes "drop the binary, run the systemd
    /// unit, configure in the browser" possible.
    /// </summary>
    private static void ValidateRequiredConfig(SQLiteConnection db, ILogger? logger)
    {
        var optionsTable = db.Table<DbSystemOption>().Table.TableName;
        var options = db.Query<DbSystemOption>($"select * from {optionsTable};");
        var callsignRow = options.SingleOrDefault(
            o => string.Equals(o.Option, "Callsign", StringComparison.OrdinalIgnoreCase));
        var callsign = callsignRow?.Value ?? "";

        if (string.IsNullOrWhiteSpace(callsign)
            || string.Equals(callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Callsign is not configured (currently '{0}'). The daemon is in setup-required mode: " +
                "open the dashboard's /Setup page to configure a callsign and bearer. Inbound bearer " +
                "services and the outbound forwarder will not bind/transmit until then.",
                callsign);
        }
    }

    /// <summary>
    /// Convert a PascalCase or camelCase identifier to SCREAMING_SNAKE_CASE
    /// for use as an environment-variable suffix. <c>NodeHost</c> →
    /// <c>NODE_HOST</c>; <c>DefaultBearerPort</c> → <c>DEFAULT_BEARER_PORT</c>.
    /// </summary>
    private static string ToScreamingSnake(string identifier)
    {
        var sb = new StringBuilder(identifier.Length + 4);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
