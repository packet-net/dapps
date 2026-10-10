using System.Net;
using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using dapps.core.Services.Apt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// The update check on a .deb install: packet-net's apt index read, the
/// newest dapps for this architecture found and compared as apt would,
/// the right command for how it was installed, silence when it cannot
/// check, and the one-time turn-on for nodes the old package seeded off.
/// No network: the repository is a fake handler.
/// </summary>
public class AptUpdateCheckTests
{
    /// <summary>packet-net's real index as it was on 6 October 2026, with dapps 0.43.0 for amd64, arm64 and armhf.</summary>
    private static string RealIndex() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Apt", "Packages-2026-10-06"));

    private static string Stanza(string package, string version, string arch) => $"""
        Package: {package}
        Version: {version}
        Architecture: {arch}
        Filename: pool/{package}_{version}_{arch}.deb
        Description: something
         a continuation line: Version: 99.0
        """ + "\n\n";

    [Theory]
    [InlineData("0.46.0", "0.45.2", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("0.45.2", "0.45.2", 0)]
    [InlineData("1.0~rc1", "1.0", -1)]
    [InlineData("1.0", "1.0+b1", -1)]
    [InlineData("0.46.0-1", "0.46.0", 1)]
    public void Compare_OrdersVersionsAsDpkgDoes(string a, string b, int sign)
    {
        Math.Sign(DebianVersion.Compare(a, b)).Should().Be(sign);
        Math.Sign(DebianVersion.Compare(b, a)).Should().Be(-sign);
    }

    [Fact]
    public void Parse_TheRealIndex_FindsDappsForEachArchitecture()
    {
        var packages = AptPackages.Parse(RealIndex());
        foreach (var arch in new[] { "amd64", "arm64", "armhf" })
        {
            var newest = AptPackages.Newest(packages, AptPackages.DappsPackage, arch);
            newest.Should().NotBeNull();
            newest!.Version.Should().Be("0.43.0");
            newest.Filename.Should().Be($"pool/dapps_0.43.0_{arch}.deb");
        }
        AptPackages.Newest(packages, AptPackages.DappsPackage, "i386").Should().BeNull();
    }

    [Fact]
    public void Newest_IsPerArchitecture_AndByDebianOrder_NotByOrderInTheFile()
    {
        var index = Stanza("dapps", "0.10.0", "amd64")
            + Stanza("dapps", "0.9.0", "amd64")
            + Stanza("dapps", "0.7.0", "arm64")
            + Stanza("pdn-mailcast-receiver", "0.11.0", "arm64");
        var packages = AptPackages.Parse(index.Replace("\n", "\r\n"));
        AptPackages.Newest(packages, "dapps", "amd64")!.Version.Should().Be("0.10.0");
        AptPackages.Newest(packages, "dapps", "arm64")!.Version.Should().Be("0.7.0");
        AptPackages.Newest(packages, "dapps", "armhf").Should().BeNull();
    }

    [Fact]
    public void Parse_SomethingElse_Throws()
    {
        Assert.Throws<FormatException>(() => AptPackages.Parse("<html><body>Not Found</body></html>"));
    }

    [Theory]
    // What docs/install/linux.md tells you to create, in /etc/apt/sources.list.d/packet-net.list.
    [InlineData("deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./\n", true)]
    [InlineData("# deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./\n", false)]
    [InlineData("Types: deb\nURIs: https://packet-net.github.io/apt\nSuites: ./\n", true)]
    [InlineData("Types: deb\nURIs: https://packet-net.github.io/apt\nSuites: ./\nEnabled: no\n", false)]
    public void NamesRepo_FindsALiveEntryForPacketNetsRepo(string file, bool expected) =>
        AptSources.NamesRepo(["deb http://deb.debian.org/debian trixie main\n", file]).Should().Be(expected);

    [Theory]
    [InlineData("/usr/lib/dapps/", true, InstallKind.AptRepo)]
    [InlineData("/usr/lib/dapps", false, InstallKind.Deb)]
    [InlineData("/opt/dapps/", true, InstallKind.Other)]
    [InlineData("/usr/lib/dapps-old/", true, InstallKind.Other)]
    [InlineData("/app/", false, InstallKind.Other)]
    public void Classify_ByWhereItRuns_ThenByAptSources(string baseDirectory, bool fromAptRepo, InstallKind expected) =>
        UpdateInstall.Classify(baseDirectory, () => fromAptRepo).Should().Be(expected);

    [Fact]
    public async Task Newer_FromTheAptRepo_GivesTheAptCommand_AndNoApply()
    {
        var (checker, handler) = Checker(InstallKind.AptRepo, current => Stanza("dapps", "999.0.0", "amd64") + Stanza("dapps", current, "amd64"));
        await checker.RefreshAsync(CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Should().Be(UpdateChecker.AptPackagesUrl);
        checker.UpdateAvailable.Should().BeTrue();
        checker.Latest!.Tag.Should().Be("999.0.0");
        checker.Latest.Url.Should().Be("https://github.com/packet-net/dapps/releases/tag/v999.0.0");
        checker.CanApply.Should().BeFalse();
        checker.InstallName.Should().Be("apt");
        checker.UpgradeCommand.Should().Be("sudo apt update && sudo apt install --only-upgrade dapps");
        checker.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task Newer_OnAHandInstalledDeb_GivesTheDownloadAndDpkgCommand()
    {
        var (checker, _) = Checker(InstallKind.Deb, _ => Stanza("dapps", "999.0.0", "arm64"), arch: "arm64");
        await checker.RefreshAsync(CancellationToken.None);

        checker.UpgradeCommand.Should().Be("sudo apt install ./dapps_999.0.0_arm64.deb");
        checker.DownloadUrl.Should().Be("https://github.com/packet-net/dapps/releases/download/v999.0.0/dapps_999.0.0_arm64.deb");
        checker.InstallName.Should().Be("deb");
    }

    [Fact]
    public async Task SameVersion_IsNotAnUpdate_AndGivesNoCommand()
    {
        var (checker, _) = Checker(InstallKind.AptRepo, current => Stanza("dapps", current, "amd64"));
        await checker.RefreshAsync(CancellationToken.None);

        checker.Latest!.Tag.Should().Be(checker.Current);
        checker.UpdateAvailable.Should().BeFalse();
        checker.UpgradeCommand.Should().BeNull();
        checker.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task OnlyOtherArchitectures_Newer_IsNotAnUpdate()
    {
        var (checker, _) = Checker(InstallKind.AptRepo, current => Stanza("dapps", "999.0.0", "arm64") + Stanza("dapps", current, "amd64"));
        await checker.RefreshAsync(CancellationToken.None);
        checker.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task AnErrorPage_LeavesTheLastFinding_AndDoesNotThrow()
    {
        var body = (string current) => Stanza("dapps", "999.0.0", "amd64");
        var (checker, handler) = Checker(InstallKind.AptRepo, current => body(current));
        await checker.RefreshAsync(CancellationToken.None);
        checker.UpdateAvailable.Should().BeTrue();

        handler.Status = HttpStatusCode.NotFound;
        await checker.RefreshAsync(CancellationToken.None);
        handler.Status = HttpStatusCode.OK;
        body = _ => "<html>oops</html>";
        await checker.RefreshAsync(CancellationToken.None);

        checker.Latest!.Tag.Should().Be("999.0.0");
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnArchitecturePacketNetDoesNotBuild_IsNeverChecked()
    {
        var (checker, handler) = Checker(InstallKind.AptRepo, _ => Stanza("dapps", "999.0.0", "amd64"), arch: null);
        await checker.RefreshAsync(CancellationToken.None);
        handler.Requests.Should().BeEmpty();
        checker.Latest.Should().BeNull();
    }

    [Fact]
    public async Task Disabled_IsNeverChecked()
    {
        var (checker, handler) = Checker(InstallKind.AptRepo, _ => Stanza("dapps", "999.0.0", "amd64"), enabled: false);
        await checker.RefreshAsync(CancellationToken.None);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void OtherInstalls_KeepTheApplyButton()
    {
        var (checker, _) = Checker(InstallKind.Other, _ => "");
        checker.CanApply.Should().BeTrue();
        checker.InstallName.Should().Be("other");
        checker.UpgradeCommand.Should().BeNull();
    }

    // --- The one-time turn-on for nodes the old .deb seeded off ---

    [Fact]
    public void Deb_StoredOffBeforeThisStart_IsTurnedOn_Once()
    {
        using var db = Db(("UpdateCheckEnabled", "false"));
        var before = Rows(db);

        DbStartup.TurnOnUpdateCheckForDeb(db, before, isDeb: true, logger: null);
        Value(db, "UpdateCheckEnabled").Should().Be("true");
        Value(db, DbStartup.DebUpdateCheckDefaultedKey).Should().Be("true");

        // The operator turns it off again: a later start leaves it off.
        Set(db, "UpdateCheckEnabled", "false");
        DbStartup.TurnOnUpdateCheckForDeb(db, Rows(db), isDeb: true, logger: null);
        Value(db, "UpdateCheckEnabled").Should().Be("false");
    }

    [Fact]
    public void Deb_FreshInstallSeededOff_StaysOff()
    {
        // Seeded this start (an operator's DAPPS_UPDATE_CHECK_ENABLED=false
        // on first install): not the old package default, so left alone.
        using var db = Db();
        var before = Rows(db);
        Set(db, "UpdateCheckEnabled", "false");

        DbStartup.TurnOnUpdateCheckForDeb(db, before, isDeb: true, logger: null);
        Value(db, "UpdateCheckEnabled").Should().Be("false");
        Value(db, DbStartup.DebUpdateCheckDefaultedKey).Should().Be("true");
    }

    [Fact]
    public void NotADeb_IsLeftAlone()
    {
        using var db = Db(("UpdateCheckEnabled", "false"));
        DbStartup.TurnOnUpdateCheckForDeb(db, Rows(db), isDeb: false, logger: null);
        Value(db, "UpdateCheckEnabled").Should().Be("false");
        Value(db, DbStartup.DebUpdateCheckDefaultedKey).Should().BeNull();
    }

    private static SQLiteConnection Db(params (string Option, string Value)[] rows)
    {
        var db = new SQLiteConnection(":memory:");
        db.CreateTable<DbSystemOption>();
        foreach (var (option, value) in rows)
        {
            db.Insert(new DbSystemOption { Option = option, Value = value });
        }
        return db;
    }

    private static List<DbSystemOption> Rows(SQLiteConnection db) => db.Query<DbSystemOption>("select * from systemoptions;");

    private static string? Value(SQLiteConnection db, string option) =>
        Rows(db).FirstOrDefault(o => o.Option == option)?.Value;

    private static void Set(SQLiteConnection db, string option, string value) =>
        db.InsertOrReplace(new DbSystemOption { Option = option, Value = value });

    private static (UpdateChecker, FakeRepo) Checker(InstallKind kind, Func<string, string> index, string? arch = "amd64", bool enabled = true)
    {
        UpdateChecker? checker = null;
        var handler = new FakeRepo(() => index(checker!.Current));
        checker = new UpdateChecker(
            new Factory(handler),
            new Options(new SystemOptions { UpdateCheckEnabled = enabled }),
            TimeProvider.System,
            NullLogger<UpdateChecker>.Instance,
            new UpdateInstall(kind, arch));
        return (checker, handler);
    }

    private sealed class FakeRepo(Func<string> body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body()) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Options(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }
}
