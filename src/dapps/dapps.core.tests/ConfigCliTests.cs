using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Updater;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// `dapps --show-config` (#334, #320): finds the database the way
/// reset-admin-password does, never creates one, and hides secrets.
/// Drives the internal overload with a fake working directory and fake
/// install folders so nothing depends on the machine running the tests.
/// </summary>
public sealed class ConfigCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dapps-cfgcli-{Guid.NewGuid():N}");
    private readonly string cwd;
    private readonly string installed;

    public ConfigCliTests()
    {
        cwd = Path.Combine(root, "cwd");
        installed = Path.Combine(root, "var-lib-dapps");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(installed);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void EmptyDb_WarnsAndExitsNonZero()
    {
        var db = MakeDb(cwd);
        var (exit, stdout, stderr) = Run([]);
        exit.Should().Be(1);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("has no settings").And.Contain(db).And.Contain("--db");
    }

    [Fact]
    public void SeededRows_PrintAsScreamingSnakeAssignments()
    {
        MakeDb(cwd, ("Callsign", "G0TST-9"), ("DiscoveryAirtimeBudgetSecondsPerHour", "120"), ("ProbeStrategy", "Overnight"));
        var (exit, stdout, _) = Run([]);
        exit.Should().Be(0);
        stdout.Should().Contain("DAPPS_CALLSIGN=G0TST-9");
        stdout.Should().Contain("DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR=120");
        stdout.Should().Contain("DAPPS_PROBE_STRATEGY=Overnight");
    }

    [Fact]
    public void NoDatabaseAnywhere_IsAnError_AndCreatesNothing()
    {
        var (exit, stdout, stderr) = Run([]);
        exit.Should().Be(1);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("No DAPPS database found").And.Contain("--db").And.Contain("sudo -u dapps");
        Directory.GetFiles(cwd, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void DatabaseInTheInstallFolder_IsFoundFromAnotherFolder()
    {
        MakeDb(installed, ("Callsign", "G0INS-7"));
        var (exit, stdout, _) = Run([]);
        exit.Should().Be(0);
        stdout.Should().Contain("DAPPS_CALLSIGN=G0INS-7");
        Directory.GetFiles(cwd, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void DatabasesInBothPlaces_AsksForDb()
    {
        MakeDb(cwd, ("Callsign", "G0AAA-1"));
        MakeDb(installed, ("Callsign", "G0BBB-1"));
        var (exit, _, stderr) = Run([]);
        exit.Should().Be(1);
        stderr.Should().Contain("more than one").And.Contain("--db");
    }

    [Fact]
    public void DbArgument_NamesTheFileOrTheFolder()
    {
        var db = MakeDb(installed, ("Callsign", "G0ARG-1"));
        Run(["--db", db]).stdout.Should().Contain("DAPPS_CALLSIGN=G0ARG-1");
        Run(["--db", installed]).stdout.Should().Contain("DAPPS_CALLSIGN=G0ARG-1");
        var (exit, _, stderr) = Run(["--db", Path.Combine(root, "nope.db")]);
        exit.Should().Be(1);
        stderr.Should().Contain("No DAPPS database at");
        File.Exists(Path.Combine(root, "nope.db")).Should().BeFalse();
    }

    [Fact]
    public void NotADappsDatabase_IsReported()
    {
        var path = Path.Combine(cwd, "dapps.db");
        using (var c = new SQLiteConnection(path)) c.Execute("create table other (x integer)");
        var (exit, _, stderr) = Run([]);
        exit.Should().Be(1);
        stderr.Should().Contain("isn't a DAPPS database");
    }

    [Fact]
    public void Secrets_AreHidden_OtherValuesAreNot()
    {
        MakeDb(cwd,
            ("Callsign", "G0TST-9"),
            ("AdminPasswordHash", "HASHVALUE"),
            ("AdminPasswordSalt", "SALTVALUE"),
            ("AdminPasswordSetAt", "2026-10-01T00:00:00Z"),
            ("RhpPass", "RHPSECRET"),
            ("MeshCoreChannelPsk", "PSKSECRET"),
            ("MeshCoreFloodScopeKey", "KEYSECRET"),
            ("MeshCoreNodeName", "DAPPS"));
        var (exit, stdout, _) = Run([]);
        exit.Should().Be(0);
        stdout.Should().NotContain("HASHVALUE").And.NotContain("SALTVALUE").And.NotContain("RHPSECRET")
            .And.NotContain("PSKSECRET").And.NotContain("KEYSECRET");
        stdout.Should().Contain("DAPPS_ADMIN_PASSWORD_HASH=(set, hidden)");
        stdout.Should().Contain("DAPPS_RHP_PASS=(set, hidden)");
        stdout.Should().Contain("DAPPS_ADMIN_PASSWORD_SET_AT=2026-10-01T00:00:00Z");
        stdout.Should().Contain("DAPPS_MESH_CORE_NODE_NAME=DAPPS");
    }

    private string MakeDb(string folder, params (string Option, string Value)[] rows)
    {
        var path = Path.Combine(folder, "dapps.db");
        using var c = new SQLiteConnection(path);
        c.CreateTable<DbSystemOption>();
        foreach (var (option, value) in rows) c.Insert(new DbSystemOption { Option = option, Value = value });
        return path;
    }

    private (int exit, string stdout, string stderr) Run(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ConfigCli.ShowConfig(args, cwd, [installed], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
