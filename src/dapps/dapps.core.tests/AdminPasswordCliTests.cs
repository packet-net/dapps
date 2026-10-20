using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// #385: <c>dapps reset-admin-password</c>. Finds the daemon's database
/// the way the daemon does, never makes a new one, applies the
/// dashboard's password rules, and waits out a daemon that is writing.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AdminPasswordCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dapps-reset-cli-{Guid.NewGuid():N}");
    private readonly string nodeDir;
    private readonly string dbPath;

    public AdminPasswordCliTests()
    {
        nodeDir = Path.Combine(root, "node");
        Directory.CreateDirectory(Path.Combine(nodeDir, "data"));
        dbPath = Path.Combine(nodeDir, "data", "dapps.db");
        CreateDappsDb(dbPath, "original-password");
    }

    public void Dispose()
    {
        DbInfo.OverridePath = null;
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PipedPassword_InTheDaemonsFolder_SetsIt()
    {
        var term = new FakeTerminal(interactive: false, "new-password-123\r");

        var exit = await Run(term, [], nodeDir);

        exit.Should().Be(0, term.Error.ToString());
        Verify("new-password-123").Should().BeTrue("the trailing CR from a Windows pipe is not part of it");
        Verify("original-password").Should().BeFalse();
        DbInfo.OverridePath.Should().BeNull("the command puts the test override back");
    }

    [Fact]
    public async Task Interactive_AsksTwice_AndSetsIt()
    {
        var term = new FakeTerminal(interactive: true, "new-password-123", "new-password-123");

        var exit = await Run(term, [], nodeDir);

        exit.Should().Be(0, term.Error.ToString());
        term.SecretPrompts.Should().Be(2);
        Verify("new-password-123").Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "new-password-123", "new-password-124")]
    [InlineData(true, "short12", "short12")]
    [InlineData(true, "", "")]
    [InlineData(false, "short12")]
    [InlineData(false, "")]
    [InlineData(false, null)]
    public async Task MismatchedShortOrEmpty_IsRefused_AndChangesNothing(bool interactive, string? first, string? second = null)
    {
        // A null first line is stdin at end of file.
        var term = new FakeTerminal(interactive, first, second);

        var exit = await Run(term, [], nodeDir);

        exit.Should().Be(1);
        term.Error.ToString().Should().Contain("Nothing changed");
        Verify("original-password").Should().BeTrue();
    }

    [Fact]
    public async Task DbArgument_TakesTheFolderOrTheFile()
    {
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        (await Run(new FakeTerminal(false, "via-folder-123"), ["--db", nodeDir], elsewhere)).Should().Be(0);
        Verify("via-folder-123").Should().BeTrue();

        (await Run(new FakeTerminal(false, "via-file-1234"), ["--db", dbPath], elsewhere)).Should().Be(0);
        Verify("via-file-1234").Should().BeTrue();
    }

    [Fact]
    public async Task NoDatabase_FailsWithoutCreatingOne()
    {
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        var term = new FakeTerminal(false, "new-password-123");

        var exit = await Run(term, [], empty, installed: [Path.Combine(root, "not-installed")]);

        exit.Should().Be(2);
        term.Error.ToString().Should().Contain("--db");
        Directory.EnumerateFileSystemEntries(empty).Should().BeEmpty("it must never make a new database");

        (await Run(new FakeTerminal(false, "new-password-123"), ["--db", Path.Combine(empty, "nope.db")], empty)).Should().Be(2);
        File.Exists(Path.Combine(empty, "nope.db")).Should().BeFalse();
    }

    [Fact]
    public async Task ElsewhereOnTheNode_FindsTheInstalledDatabase()
    {
        // Run from a home directory: falls back to the packaged
        // install's state folder (/var/lib/dapps in production).
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var installedDir = Path.Combine(root, "var-lib-dapps");
        Directory.CreateDirectory(installedDir);
        var installedDb = Path.Combine(installedDir, "dapps.db");
        CreateDappsDb(installedDb, "original-password");

        var exit = await Run(new FakeTerminal(false, "installed-123"), [], home, installed: [installedDir]);

        exit.Should().Be(0);
        Verify("installed-123", installedDb).Should().BeTrue();
    }

    [Fact]
    public async Task TwoInstalledDatabases_AsksWhichOne()
    {
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        CreateDappsDb(Path.Combine(a, "dapps.db"), "original-password");
        CreateDappsDb(Path.Combine(b, "dapps.db"), "original-password");
        var term = new FakeTerminal(false, "new-password-123");

        var exit = await Run(term, [], home, installed: [a, b]);

        exit.Should().Be(2);
        term.Error.ToString().Should().Contain("--db");
        Verify("original-password", Path.Combine(a, "dapps.db")).Should().BeTrue();
        Verify("original-password", Path.Combine(b, "dapps.db")).Should().BeTrue();
    }

    [Fact]
    public async Task NotADappsDatabase_IsLeftAlone()
    {
        var other = Path.Combine(root, "other.db");
        using (var c = new SQLiteConnection(other)) c.Execute("create table t (x int)");
        var term = new FakeTerminal(false, "new-password-123");

        var exit = await Run(term, ["--db", other], root);

        exit.Should().Be(2);
        using var check = new SQLiteConnection(other);
        check.ExecuteScalar<int>("select count(*) from sqlite_master where name='systemoptions'").Should().Be(0);
    }

    [Fact]
    public async Task WhileTheDaemonHoldsAWriteLock_WaitsAndThenSetsIt()
    {
        // The daemon writing at the same moment holds SQLite's write
        // lock; the reset waits for it instead of failing.
        var daemon = new SQLiteConnection(dbPath);
        daemon.Execute("begin immediate");
        daemon.Execute("update systemoptions set value='x' where option='Unrelated'");
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            daemon.Execute("commit");
            daemon.Dispose();
        });

        var term = new FakeTerminal(false, "new-password-123");
        var exit = await Run(term, [], nodeDir);
        await release;

        exit.Should().Be(0, term.Error.ToString());
        Verify("new-password-123").Should().BeTrue();
    }

    [Fact]
    public async Task UnknownArgument_PrintsUsage()
    {
        var term = new FakeTerminal(false, "new-password-123");

        (await Run(term, ["--bogus"], nodeDir)).Should().Be(1);

        term.Error.ToString().Should().Contain("Usage: dapps reset-admin-password");
        Verify("original-password").Should().BeTrue();
    }

    [Fact]
    public async Task Reset_SignsOutLoginsIssuedBeforeIt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        (await Run(new FakeTerminal(false, "new-password-123"), [], nodeDir)).Should().Be(0);

        DbInfo.OverridePath = dbPath;
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        (await store.IsSessionCurrentAsync(before)).Should().BeFalse();
        (await store.IsSessionCurrentAsync(DateTimeOffset.UtcNow)).Should().BeTrue();
    }

    private static Task<int> Run(FakeTerminal term, string[] args, string cwd, string[]? installed = null) =>
        AdminPasswordCli.RunAsync(args, term, cwd, installed ?? []);

    private static void CreateDappsDb(string path, string password)
    {
        using (var c = new SQLiteConnection(path))
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Unrelated", Value = "1" });
        }
        DbInfo.OverridePath = path;
        new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance).SetAsync(password).GetAwaiter().GetResult();
        DbInfo.OverridePath = null;
    }

    private bool Verify(string password, string? path = null)
    {
        DbInfo.OverridePath = path ?? dbPath;
        try
        {
            return new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance).VerifyAsync(password).GetAwaiter().GetResult();
        }
        finally
        {
            DbInfo.OverridePath = null;
        }
    }

    private sealed class FakeTerminal(bool interactive, params string?[] lines) : AdminPasswordCli.ITerminal
    {
        private readonly Queue<string?> input = new(lines);
        public bool IsInteractive => interactive;
        public TextWriter Out { get; } = new StringWriter();
        public TextWriter Error { get; } = new StringWriter();
        public int SecretPrompts { get; private set; }

        public string? ReadSecret(string prompt)
        {
            SecretPrompts++;
            return input.Count > 0 ? input.Dequeue() : null;
        }

        public string? ReadLine() => input.Count > 0 ? input.Dequeue() : null;
    }
}
