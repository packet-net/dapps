using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// DbStartup.EnsureSchemaAndSeed is the system's startup config seam:
/// it creates schema, seeds defaults from env vars (Plan C2),
/// re-applies set env vars at every start ONLY under the opt-in
/// DAPPS_ENV_MANAGED=true mode (deployment-managed config - the pdn
/// supervised-app case), derives a callsign from a pdn host's
/// PDN_NODE_CALLSIGN, and warns on a placeholder callsign. These tests
/// drive each path against a fresh SQLite file - both env-var modes
/// have their contract pinned here.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DbStartupTests : IAsyncLifetime
{
    private string dbPath = null!;
    private readonly Dictionary<string, string?> env = [];

    private void SetEnv(string key, string? value) => env[key] = value;


    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-startup-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        // Startup reads this dictionary, not the process environment, so
        // nothing set here can leak into a daemon another test starts (#330).
        DbStartup.ReadEnvironment = k => env.GetValueOrDefault(k);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbStartup.ReadEnvironment = Environment.GetEnvironmentVariable;
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EnsureSchemaAndSeed_NoEnvVars_StartsWithPlaceholderCallsign()
    {
        // Drop-the-binary-and-go-to-the-dashboard install flow: the
        // daemon must start cleanly with no env vars and the seeded
        // placeholder callsign. Inbound bearer services and the
        // forwarder gate themselves on a real callsign at runtime;
        // /Health reports CallsignConfigured=false until /Setup or
        // /Config configures one.
        var act = () => DbStartup.EnsureSchemaAndSeed();
        act.Should().NotThrow();

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbSystemOption>("Callsign");
        row.Should().NotBeNull();
        row!.Value.Should().Be("N0CALL");
    }

    [Fact]
    public void EnsureSchemaAndSeed_FreshDatabase_AuthRequiredDefaultsToTrue()
    {
        // New installs require app tokens from the start (#316): a
        // brand new database, with nothing seeded yet, seeds
        // AuthRequired=true even though the entry in SeededOptions
        // carries the historical "false" fallback.
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("AuthRequired")!.Value.Should().Be("true");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingDatabaseMissingAuthRequiredRow_StaysFalse()
    {
        // An upgrade whose database already has rows (just not this
        // one, e.g. a very old config predating AuthRequired) must
        // never retroactively lock out a running app: it keeps the
        // historical effective value of false.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("AuthRequired")!.Value.Should().Be("false");
    }

    [Fact]
    public void EnsureSchemaAndSeed_FreshDatabase_ExplicitAuthRequiredEnvVarStillWins()
    {
        // An explicit DAPPS_AUTH_REQUIRED still overrides the new
        // fresh-install default, same as every other seeded option.
        SetEnv("DAPPS_AUTH_REQUIRED", "false");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("AuthRequired")!.Value.Should().Be("false");
    }

    [Fact]
    public void EnsureSchemaAndSeed_FreshDatabase_AuthRequiredRowNotTouchedOnLaterRestarts()
    {
        // The fresh-install default only applies while the table is
        // still empty - once seeded, later starts leave it alone like
        // every other option (no DAPPS_ENV_MANAGED opt-in).
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update systemoptions set value=? where option=?", "false", "AuthRequired");
        }

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("AuthRequired")!.Value.Should().Be("false",
            "a dashboard edit after the first-start default must survive restarts");
    }

    [Fact]
    public void EnsureSchemaAndSeed_CallsignFromEnvVar_SeedsAndStarts()
    {
        SetEnv("DAPPS_CALLSIGN", "G0TST");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbSystemOption>("Callsign");
        row.Should().NotBeNull();
        row!.Value.Should().Be("G0TST");
    }

    [Fact]
    public void EnsureSchemaAndSeed_AllEnvVars_SeedsEachOption()
    {
        SetEnv("DAPPS_CALLSIGN", "G0TST");
        SetEnv("DAPPS_NODE_HOST", "bpq.local");
        SetEnv("DAPPS_AGW_PORT", "8001");
        SetEnv("DAPPS_DEFAULT_PORT", "2");
        SetEnv("DAPPS_MQTT_PORT", "1884");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
        c.Find<DbSystemOption>("NodeHost")!.Value.Should().Be("bpq.local");
        c.Find<DbSystemOption>("AgwPort")!.Value.Should().Be("8001");
        c.Find<DbSystemOption>("DefaultPort")!.Value.Should().Be("2");
        c.Find<DbSystemOption>("MqttPort")!.Value.Should().Be("1884");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingRow_NotOverwrittenByEnv()
    {
        // THE standalone contract: without the DAPPS_ENV_MANAGED=true
        // opt-in, a set env var seeds first-start values only. The
        // shipped standalone flow (scripts/dapps.service +
        // EnvironmentFile=/etc/dapps.env) keeps DAPPS_* set permanently,
        // so re-applying here would revert every dashboard edit on
        // every restart for every documented standalone install.
        // Pre-seed a manually-configured callsign as if /Config POST had set it.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        SetEnv("DAPPS_CALLSIGN", "DIFFERENT-CALL");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "existing rows MUST NOT be overwritten by env vars on subsequent starts");
    }

    [Fact]
    public void EnsureSchemaAndSeed_EnvManagedFalse_SameAsUnset()
    {
        // An explicit =false is the standalone default, not a third mode.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        SetEnv("DAPPS_ENV_MANAGED", "false");
        SetEnv("DAPPS_CALLSIGN", "DIFFERENT-CALL");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3");
    }

    [Fact]
    public void EnsureSchemaAndSeed_EnvManagedMode_ExistingRow_AppliedAtEveryStart()
    {
        // Deployment-managed config (opt-in via DAPPS_ENV_MANAGED=true -
        // pdn-app.yaml sets it for supervised installs): a SET env var
        // wins over the stored row at every start, not just the first -
        // the host's app config is authoritative.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        SetEnv("DAPPS_ENV_MANAGED", "true");
        SetEnv("DAPPS_CALLSIGN", "G0TST-7");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-7",
            "under DAPPS_ENV_MANAGED=true a set env var is deployment-managed and re-applied over the stored row at every start");
    }

    [Fact]
    public void IsEnvManaged_RequiresTheOptInMode()
    {
        // The dashboard's "managed by environment" badges key off
        // IsEnvManaged / EnvManagedKeys; without the opt-in they must
        // stay dark even when DAPPS_* vars are set (standalone installs
        // routinely keep /etc/dapps.env applied).
        SetEnv("DAPPS_CALLSIGN", "G0TST-7");

        DbStartup.IsEnvManaged("Callsign").Should().BeFalse("no DAPPS_ENV_MANAGED opt-in");
        DbStartup.EnvManagedKeys().Should().BeEmpty();

        SetEnv("DAPPS_ENV_MANAGED", "true");

        DbStartup.IsEnvManaged("Callsign").Should().BeTrue();
        DbStartup.EnvManagedKeys().Should().Contain("Callsign");
        DbStartup.IsEnvManaged("NodeHost").Should().BeFalse("DAPPS_NODE_HOST is not set");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingRow_NoEnv_LeftAlone()
    {
        // The standalone flow: no DAPPS_* env set, so the stored
        // (dashboard-configured) value must survive every restart
        // byte-for-byte.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "unset env vars must never touch stored config");
    }

    [Fact]
    public void EnsureSchemaAndSeed_SeedOnceViaEnvThenUnset_DashboardEditSticks()
    {
        // A standalone operator who seeds once via env then unsets it
        // keeps dashboard control exactly as before this change.
        SetEnv("DAPPS_CALLSIGN", "G0TST");
        DbStartup.EnsureSchemaAndSeed();
        SetEnv("DAPPS_CALLSIGN", null);

        // Restart without the env var: seeded value sticks.
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
            // Dashboard edit (as /Config POST would persist it).
            c.Execute("update systemoptions set value=? where option=?", "M0LTE-3", "Callsign");
        }

        // Next restart: the edit survives.
        DbStartup.EnsureSchemaAndSeed();
        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnNodeCallsign_DerivesCallsignWithDefaultSsid()
    {
        // pdn-hosted fresh install: no DAPPS_CALLSIGN, host injects
        // PDN_NODE_CALLSIGN -> DAPPS takes up residence at SSID -7 of
        // the node callsign.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
        c.Find<DbSystemOption>("Ssid")!.Value.Should().Be("7", "the SSID knob is seeded alongside");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnNodeCallsignWithSsid_StripsItBeforeComposing()
    {
        SetEnv("PDN_NODE_CALLSIGN", "m9yyy-2");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7",
            "the node's own SSID is stripped (and the base upper-cased) before composing");
    }

    [Fact]
    public void EnsureSchemaAndSeed_DappsSsidEnv_OverridesDerivationSsid()
    {
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        SetEnv("DAPPS_SSID", "4");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-4");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExplicitDappsCallsign_WinsOverDerivation()
    {
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        SetEnv("DAPPS_CALLSIGN", "G0TST-1");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-1",
            "an explicit DAPPS_CALLSIGN always wins over derivation");
    }

    // ---- Node-owned-callsign contract (PDN_APP_CALLSIGN) ----
    //
    // A current pdn host names the exact callsign DAPPS must bind via
    // PDN_APP_CALLSIGN. When set and non-empty the node is the authority:
    // DAPPS binds it verbatim, with no self-derivation and no SSID probe.
    // Absent/empty falls back to the legacy derivation / DAPPS_CALLSIGN
    // path so an older node or a standalone install still works.

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsign_BoundVerbatim_NoPendingMarker()
    {
        // The host assigned the on-air identity: it is stored verbatim
        // and nothing is pending confirmation (so the listener never
        // probe-walks).
        SetEnv("PDN_APP_CALLSIGN", "M9YYY-7");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
        c.Find<DbSystemOption>(DbStartup.DerivedCallsignPendingKey).Should().BeNull(
            "a node-assigned callsign is pinned - it never probe-walks");
        DbStartup.ReadNodeAssignedCallsign().Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsign_WinsOverNodeDerivation()
    {
        // Both injected (transitional host): the explicit assignment
        // wins; DAPPS does NOT self-derive <node>-7 underneath it.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        SetEnv("PDN_APP_CALLSIGN", "M9YYY-9");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-9",
            "PDN_APP_CALLSIGN is the node's authoritative assignment - no self-derivation");
        DbStartup.ReadPendingDerivedCallsign().Should().BeNull();
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsign_OverridesStoredCallsign_AtEveryStart()
    {
        // The node owns the identity: it re-asserts PDN_APP_CALLSIGN over
        // whatever is stored at every start (even a previously-configured
        // real callsign), so the host stays authoritative.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        SetEnv("PDN_APP_CALLSIGN", "M9YYY-7");

        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
        }

        // Restart re-asserts it, and clears a marker too if one appeared.
        DbStartup.EnsureSchemaAndSeed();
        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsign_ClearsLeftoverPendingMarker()
    {
        // A node that previously derived (PDN_NODE_CALLSIGN, marker set)
        // is upgraded to inject PDN_APP_CALLSIGN: the next start adopts
        // the assigned identity and drops the stale pending marker so the
        // listener never walks.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();
        DbStartup.ReadPendingDerivedCallsign().Should().Be("M9YYY-7", "preconditon: derivation pending");

        SetEnv("PDN_APP_CALLSIGN", "M9YYY-9");
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-9");
        DbStartup.ReadPendingDerivedCallsign().Should().BeNull(
            "the node-assigned identity is pinned - the stale derivation marker is cleared");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsignEmpty_FallsBackToDerivation()
    {
        // Older node / contract not honoured: PDN_APP_CALLSIGN empty, so
        // the legacy PDN_NODE_CALLSIGN derivation still runs.
        SetEnv("PDN_APP_CALLSIGN", "");
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7",
            "an empty PDN_APP_CALLSIGN is not an assignment - fall back to derivation");
        DbStartup.ReadNodeAssignedCallsign().Should().BeNull("whitespace/empty is treated as unset");
    }

    [Fact]
    public void EnsureSchemaAndSeed_CallsignFromCrlfEnvFile_IsTrimmed()
    {
        SetEnv("DAPPS_CALLSIGN", "G0TST\r");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
    }

    [Fact]
    public void EnsureSchemaAndSeed_EnvManagedMode_CrlfCallsignIsTrimmedAtEveryStart()
    {
        SetEnv("DAPPS_CALLSIGN", "G0TST");
        DbStartup.EnsureSchemaAndSeed();

        SetEnv("DAPPS_ENV_MANAGED", "true");
        SetEnv("DAPPS_CALLSIGN", "G0TST-7\r");
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_InvalidCallsign_LogsAnError()
    {
        // PDN_APP_CALLSIGN is bound verbatim, so nothing else stops a
        // malformed one reaching the database (#359).
        SetEnv("PDN_APP_CALLSIGN", "G0TST-99");
        var logs = new CapturingLoggerFactory();

        DbStartup.EnsureSchemaAndSeed(logs.CreateLogger("DbStartup"));

        logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("'G0TST-99' is not valid"));
    }

    [Fact]
    public void EnsureSchemaAndSeed_InvalidCallsignWithCr_LogsItVisibly()
    {
        // Stored before the env value was trimmed; nothing trims it now.
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            var row = c.Find<DbSystemOption>("Callsign")!;
            row.Value = "G0TST\r";
            c.Update(row);
        }
        var logs = new CapturingLoggerFactory();

        DbStartup.EnsureSchemaAndSeed(logs.CreateLogger("DbStartup"));

        logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(@"'G0TST\x0D' is not valid"));
        logs.Entries.Should().NotContain(e => e.Message.Contains('\r'));
    }

    [Fact]
    public void EnsureSchemaAndSeed_ValidCallsign_LogsNoError()
    {
        SetEnv("DAPPS_CALLSIGN", "G0TST-7");
        var logs = new CapturingLoggerFactory();

        DbStartup.EnsureSchemaAndSeed(logs.CreateLogger("DbStartup"));

        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnAppCallsign_TrimsWhitespace()
    {
        SetEnv("PDN_APP_CALLSIGN", "  M9YYY-7  ");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_RealStoredCallsign_WinsOverDerivation()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "a real stored callsign always wins over derivation");
    }

    [Fact]
    public void EnsureSchemaAndSeed_StoredPlaceholder_PdnNodeCallsign_Derives()
    {
        // A pdn host whose DAPPS db predates a callsign config (or was
        // reset to the placeholder) picks up the derived identity on
        // the next start.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "N0CALL" });
        }
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_NoPdnEnv_PlaceholderUnchanged()
    {
        // Standalone install: no PDN_NODE_CALLSIGN, no DAPPS_CALLSIGN -
        // the placeholder stays and the daemon boots into the existing
        // setup-required flow (configure via /Setup // /Config).
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("N0CALL");
        c.Find<DbSystemOption>(DbStartup.DerivedCallsignPendingKey).Should().BeNull(
            "nothing was derived, so nothing is pending confirmation");
    }

    [Fact]
    public void Derivation_MarksCallsignAsPendingConfirmation()
    {
        // The derived identity is provisional until the RHPv2 listener
        // confirms the SSID is free on the node (errCode 9 probe-walk).
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>(DbStartup.DerivedCallsignPendingKey)!.Value.Should().Be("M9YYY-7");
        DbStartup.ReadPendingDerivedCallsign().Should().Be("M9YYY-7");
    }

    [Fact]
    public void PendingMarker_SurvivesRestart_WhileUnconfirmed()
    {
        // Daemon restarted before the listener ever confirmed (node was
        // down, say): the stored callsign is real now, so derivation
        // skips, but the pending marker must survive so the probe-walk
        // can still run on this start.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();

        DbStartup.EnsureSchemaAndSeed(); // restart, still unconfirmed

        DbStartup.ReadPendingDerivedCallsign().Should().Be("M9YYY-7");
    }

    [Fact]
    public void PendingMarker_ClearedByDashboardConfiguredCallsign()
    {
        // Derive, then the operator pins an identity via the dashboard
        // before the listener confirmed: the explicit identity must
        // never probe-walk, so the next start drops the stale marker.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update systemoptions set value=? where option=?", "G0TST-1", "Callsign");
        }

        DbStartup.EnsureSchemaAndSeed();

        DbStartup.ReadPendingDerivedCallsign().Should().BeNull(
            "a dashboard-configured callsign is explicit - it never walks");
        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-1");
    }

    [Fact]
    public void PendingMarker_ClearedByExplicitDappsCallsignEnv()
    {
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();

        SetEnv("DAPPS_CALLSIGN", "G0TST-1");
        DbStartup.EnsureSchemaAndSeed();

        DbStartup.ReadPendingDerivedCallsign().Should().BeNull(
            "an explicit DAPPS_CALLSIGN pins the identity - it never walks");
    }

    [Fact]
    public void ConfirmDerivedCallsign_PersistsWinnerAndClearsMarker()
    {
        // The probe-walk found -7 taken and -8 free: the winner becomes
        // the stored identity and nothing is pending any more, so every
        // later start binds M9YYY-8 directly (never walks again).
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();

        DbStartup.ConfirmDerivedCallsign("M9YYY-8");

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-8");
        DbStartup.ReadPendingDerivedCallsign().Should().BeNull();

        // And a restart leaves the confirmed identity alone.
        DbStartup.EnsureSchemaAndSeed();
        using var c2 = DbInfo.GetConnection();
        c2.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-8");
        DbStartup.ReadPendingDerivedCallsign().Should().BeNull();
    }

    [Fact]
    public void AbandonDerivedCallsign_RevertsToSetupRequired()
    {
        // Every candidate SSID taken on the node: back to the
        // placeholder (setup-required mode) so the bearer idles instead
        // of hammering the node; a restart re-derives and tries again.
        SetEnv("PDN_NODE_CALLSIGN", "M9YYY");
        DbStartup.EnsureSchemaAndSeed();

        DbStartup.AbandonDerivedCallsign();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("N0CALL");
        DbStartup.ReadPendingDerivedCallsign().Should().BeNull();

        // Restart: derivation fires again (placeholder + PDN env).
        DbStartup.EnsureSchemaAndSeed();
        using var c2 = DbInfo.GetConnection();
        c2.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
        DbStartup.ReadPendingDerivedCallsign().Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingPlaceholderCallsign_StartsWithWarning()
    {
        // Operator left N0CALL in the DB. Daemon starts (so the
        // dashboard becomes reachable for /Setup); inbound bearer +
        // forwarder gate themselves at runtime - frames stamped with
        // the placeholder never go on the air.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "N0CALL" });
        }

        var act = () => DbStartup.EnsureSchemaAndSeed();

        act.Should().NotThrow();
    }
    /// <summary>
    /// Upgrading from 0.46 (#242): learned routes and discovered paths were
    /// keyed by base callsign, and sqlite-net never changes an existing
    /// table's primary key, so those two runtime tables are dropped and
    /// relearned. Everything the operator configured stays exactly as it
    /// was, and routes as it did.
    /// </summary>
    [Fact]
    public async Task Upgrade_FromBaseCallsignRoutingTables_KeepsConfigAndRoutesAsBefore()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0US-7" });
            c.CreateTable<DbNeighbour>();
            c.Insert(new DbNeighbour { Callsign = "M0RLY-1", BearerPort = 1 });
            c.Insert(new DbNeighbour { Callsign = "M0ABC-9", BearerPort = 2 });
            c.CreateTable<DbRouteHint>();
            c.Insert(new DbRouteHint { Destination = "G6FCI", NextHop = "M0RLY-1" });
            c.Insert(new DbRouteHint { Destination = "*", NextHop = "M0ABC-9" });
            c.CreateTable<OldLearnedRoute>();
            c.Insert(new OldLearnedRoute { DestinationBaseCallsign = "G0OLD", NextHopCallsign = "M0RLY-1", Source = "traffic" });
            c.CreateTable<OldDiscoveredPath>();
            c.Insert(new OldDiscoveredPath { DestinationBaseCallsign = "G0OLD", IntermediatesCsv = "M0RLY-1" });
        }

        DbStartup.EnsureSchemaAndSeed();
        DbStartup.EnsureSchemaAndSeed();

        using (var c = DbInfo.GetConnection())
        {
            c.Table<DbRouteHint>().OrderBy(h => h.Destination).ToList().Select(h => (h.Destination, h.NextHop))
                .Should().Equal(("*", "M0ABC-9"), ("G6FCI", "M0RLY-1"));
            c.Table<DbNeighbour>().ToList().Select(n => n.Callsign).Should().BeEquivalentTo(["M0RLY-1", "M0ABC-9"]);
            c.Table<DbLearnedRoute>().Count().Should().Be(0, "learned routes are runtime state and relearn");
            c.Table<DbDiscoveredPath>().Count().Should().Be(0);
        }

        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "M0US-7" });
        var database = new Database(NullLogger<Database>.Instance, options);
        var context = new dapps.core.Routing.DatabaseRoutingContext(database, options);
        var algorithm = new dapps.core.Routing.FloodFallbackAlgorithm(
            new dapps.core.Routing.PassiveLearningAlgorithm(
                new dapps.core.Routing.StaticRoutingAlgorithm(NullLogger<dapps.core.Routing.StaticRoutingAlgorithm>.Instance),
                NullLogger<dapps.core.Routing.PassiveLearningAlgorithm>.Instance),
            NullLogger<dapps.core.Routing.FloodFallbackAlgorithm>.Instance);

        async Task<string?> NextHop(string destination) =>
            await algorithm.ResolveAsync(new DbMessage { Id = "0000001", Destination = destination, Payload = [1] },
                context, TestContext.Current.CancellationToken) is dapps.core.Routing.RouteDecision.NextHop next
                ? next.Route.Callsign : null;

        (await NextHop("chat@G6FCI-8")).Should().Be("M0RLY-1", "a bare hint still covers every SSID");
        (await NextHop("chat@G6FCI")).Should().Be("M0RLY-1");
        (await NextHop("chat@M0ABC-9")).Should().Be("M0ABC-9", "a neighbour is still reached directly");
        (await NextHop("chat@M0ABC-3")).Should().Be("M0ABC-9", "a neighbour still stands in for another SSID of its callsign");
        (await NextHop("chat@G0ELSE-1")).Should().Be("M0ABC-9", "the wildcard hint still catches the rest");

        // And the new tables take what is learned.
        await database.UpsertLearnedRouteAsync("G0NEW-2", "M0RLY-1", DateTime.UtcNow);
        (await database.GetLearnedRouteAsync("G0NEW-2"))!.NextHopCallsign.Should().Be("M0RLY-1");
    }

    /// <summary>
    /// Downgrading from 0.47.0 to 0.46 must still start: 0.46 opens
    /// learnedroutes and discoveredpaths with their old base-callsign key,
    /// and sqlite-net can't add a primary key column to a table 0.47.0 left
    /// in another shape. 0.47.0 keeps its routes under new table names, so
    /// the old ones are simply recreated empty.
    /// </summary>
    [Fact]
    public void Downgrade_AfterThisVersionHasRun_StillOpensTheOldTables()
    {
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbLearnedRoute { DestinationCallsign = "G6FCI-8", NextHopCallsign = "M0RLY-1" });
            c.Insert(new DbDiscoveredPath { DestinationCallsign = "G6FCI-8", IntermediatesCsv = "M0RLY-1" });
        }

        using var old = DbInfo.GetConnection();
        var act = () =>
        {
            old.CreateTable<OldLearnedRoute>();
            old.CreateTable<OldDiscoveredPath>();
            old.Insert(new OldLearnedRoute { DestinationBaseCallsign = "G0OLD", NextHopCallsign = "M0RLY-1" });
        };

        act.Should().NotThrow();
    }

    [Table("learnedroutes")]
    private sealed class OldLearnedRoute
    {
        [PrimaryKey] public string DestinationBaseCallsign { get; set; } = "";
        public string NextHopCallsign { get; set; } = "";
        public DateTime LastSeenAt { get; set; }
        public DateTime LastUsedAt { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string Source { get; set; } = "";
    }

    [Table("discoveredpaths")]
    private sealed class OldDiscoveredPath
    {
        [PrimaryKey] public string DestinationBaseCallsign { get; set; } = "";
        public string IntermediatesCsv { get; set; } = "";
        public DateTime LastSeenAt { get; set; }
        public DateTime LastUsedAt { get; set; }
        public int ConsecutiveFailures { get; set; }
    }
}
