using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using dapps.core.Models;
using dapps.core.Services.Apt;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Polls for a newer release on a slow cadence and exposes
/// "running version" + "latest known release" so the dashboard can
/// surface "v0.X.Y available" without the operator chasing release
/// notes by hand. Plan C5.1.
///
/// Read-only: this service never *applies* updates. Triggered and
/// auto-applied updates (C5.2 / C5.3) belong in a separate companion
/// process; dapps stays unprivileged.
///
/// Failure handling: network is flaky in the wild, so a fetch failure
/// is logged at Debug and the cached snapshot stays put. We never
/// surface "I don't know what's latest" as an error in the UI - at
/// worst the dashboard says "checking…" until the first fetch lands.
///
/// Where it looks depends on how DAPPS was installed. A .deb install
/// (<see cref="InstallKind.AptRepo"/> or <see cref="InstallKind.Deb"/>)
/// reads packet-net's apt index, so the dashboard says a version is
/// ready exactly when apt can install it, and gives the command that
/// does; there is no Apply button, because apt owns the upgrade.
/// Anything else asks GitHub Releases and keeps the self-updater's
/// Apply button.
/// </summary>
public sealed class UpdateChecker(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateChecker> logger,
    UpdateInstall? install = null) : BackgroundService
{
    /// <summary>packet-net's apt index, which a .deb install checks.</summary>
    public const string AptPackagesUrl = "https://packet-net.github.io/apt/Packages";

    /// <summary>Where each version's release notes and .debs are.</summary>
    public const string ReleasesPage = "https://github.com/packet-net/dapps/releases";

    /// <summary>The command that upgrades DAPPS installed from the apt repository.</summary>
    public const string AptCommand = "sudo apt update && sudo apt install --only-upgrade " + AptPackages.DappsPackage;

    // The apt index is a static file on GitHub Pages, but every node
    // fetching ~100 kB hourly adds up for no gain: a release only lands
    // a few times a week. "Check now" covers the impatient.
    private static readonly TimeSpan AptPollInterval = TimeSpan.FromHours(6);

    private const string ReleasesUrl = "https://api.github.com/repos/packet-net/dapps/releases/latest";
    // 1 hour: GitHub's unauthenticated rate limit is 60 req/hour/IP, so
    // one poll/hour is well within budget. 6h was too long for "I just
    // shipped, do my nodes see it?" - the dashboard would tell operators
    // their freshly-pushed release didn't exist for hours. Operators
    // wanting instant feedback hit POST /Update/check or the dashboard
    // "Check now" button.
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly UpdateInstall _install = install ?? UpdateInstall.Detect();
    private readonly HashSet<string> _failuresLogged = [];
    private string? _announced;
    private LatestRelease? _latest;
    public LatestRelease? Latest => _latest;

    /// <summary>How this DAPPS was installed, which decides where it looks and how it upgrades.</summary>
    public InstallKind Install => _install.Kind;

    /// <summary>
    /// Whether the dashboard's Apply button (the self-updater) applies
    /// here: not on a .deb install, where apt owns /usr/lib/dapps.
    /// </summary>
    public bool CanApply => !_install.IsDeb;

    /// <summary><see cref="Install"/> as the status JSON carries it: "apt", "deb" or "other".</summary>
    public string InstallName => _install.Kind switch
    {
        InstallKind.AptRepo => "apt",
        InstallKind.Deb => "deb",
        _ => "other",
    };

    /// <summary>The command that upgrades this node, on a .deb install with a newer version ready; otherwise null.</summary>
    public string? UpgradeCommand => !_install.IsDeb || !UpdateAvailable ? null
        : _install.Kind == InstallKind.AptRepo ? AptCommand
        : $"sudo apt install ./{DebFile(_latest!.Tag)}";

    /// <summary>The newer .deb's download, on a .deb installed by hand (not from the apt repo); otherwise null.</summary>
    public string? DownloadUrl => _install.Kind != InstallKind.Deb || !UpdateAvailable ? null
        : $"{ReleasesPage}/download/v{_latest!.Tag}/{DebFile(_latest.Tag)}";

    private string DebFile(string version) => $"{AptPackages.DappsPackage}_{version}_{_install.Architecture}.deb";

    /// <summary>
    /// Version string baked into the running binary by MSBuild's
    /// <c>&lt;Version&gt;</c>. Dev-pushed binaries get an
    /// <c>InformationalVersion</c> override of the form <c>dev-&lt;sha&gt;</c>;
    /// the dashboard treats those as "running a dev build" and doesn't
    /// claim out-of-date.
    /// </summary>
    public string Current { get; } = ResolveCurrentVersion();

    public bool IsDevBuild =>
        Current.StartsWith("dev-", StringComparison.OrdinalIgnoreCase);

    public bool UpdateAvailable
    {
        get
        {
            if (IsDevBuild || _latest is null) return false;
            return _install.IsDeb
                ? DebianVersion.Compare(_latest.Tag, Current) > 0
                : CompareSemver(_latest.Tag, Current) > 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tiny startup delay so we don't slow first-render of the dashboard
        // and so the rest of the service surface (DB, MQTT, AGW reconnect)
        // gets a moment first.
        // A .deb install waits a random 20 s - 2 min, so nodes restarted
        // together by one "apt upgrade" wave don't all ask at once.
        var first = _install.IsDeb ? TimeSpan.FromSeconds(Random.Shared.Next(20, 121)) : StartupDelay;
        var every = _install.IsDeb ? AptPollInterval : PollInterval;
        try { await Task.Delay(first, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnce(stoppingToken);
            try { await Task.Delay(every, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Manually re-poll now, regardless of the cached cadence.
    /// Used by the dashboard's "Check now" button and the
    /// <c>POST /Update/check</c> endpoint when an operator wants
    /// instant confirmation that a freshly-shipped release is visible
    /// to this node - instead of waiting up to an hour for the next
    /// scheduled poll.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct) => PollOnce(ct);

    private async Task PollOnce(CancellationToken ct)
    {
        if (!options.CurrentValue.UpdateCheckEnabled)
        {
            logger.LogDebug("Update check disabled by config");
            return;
        }
        if (_install.IsDeb)
        {
            await PollAptIndex(ct);
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("github");
            // GitHub requires a User-Agent on API calls; using the
            // version makes us identifiable in their access logs without
            // leaking anything operator-specific.
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"dapps/{Current}");
            client.Timeout = TimeSpan.FromSeconds(15);

            var release = await client.GetFromJsonAsync<GithubRelease>(ReleasesUrl, ct);
            if (release is null || string.IsNullOrEmpty(release.tag_name))
            {
                logger.LogDebug("Update check: empty / unparseable response");
                return;
            }

            _latest = new LatestRelease(
                Tag: release.tag_name.TrimStart('v'),
                Name: release.name ?? release.tag_name,
                Url: release.html_url ?? "",
                PublishedAt: release.published_at,
                FetchedAt: timeProvider.GetUtcNow().UtcDateTime);
            logger.LogInformation(
                "Update check: latest is {0} (running {1}{2})",
                _latest.Tag, Current, UpdateAvailable ? " - UPDATE AVAILABLE" : "");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Update check failed");
        }
    }

    /// <summary>
    /// One look at the apt index: the newest <c>dapps</c> for this
    /// machine's architecture. A failure keeps the last finding and is
    /// logged once per kind until a look works again, not every time.
    /// </summary>
    private async Task PollAptIndex(CancellationToken ct)
    {
        if (_install.Architecture is null)
        {
            Failed("arch", "packet-net builds no .deb for this machine's architecture");
            return;
        }
        try
        {
            var client = httpClientFactory.CreateClient("apt-index");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Get, AptPackagesUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", $"dapps/{Current} (+https://github.com/packet-net/dapps)");
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Failed("status", $"packet-net.github.io answered {(int)response.StatusCode}");
                return;
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            var newest = AptPackages.Newest(AptPackages.Parse(text), AptPackages.DappsPackage, _install.Architecture);
            if (newest is null)
            {
                Failed("missing", $"the apt index has no {AptPackages.DappsPackage} for {_install.Architecture}");
                return;
            }
            _latest = new LatestRelease(
                Tag: newest.Version,
                Name: "v" + newest.Version,
                Url: $"{ReleasesPage}/tag/v{newest.Version}",
                PublishedAt: null,
                FetchedAt: timeProvider.GetUtcNow().UtcDateTime);
            _failuresLogged.Clear();
            if (UpdateAvailable && _announced != newest.Version)
            {
                _announced = newest.Version;
                logger.LogInformation(
                    "Update check: version {0} is in the apt repository (running {1}); upgrade with: {2}",
                    newest.Version, Current, UpgradeCommand);
            }
            else
            {
                logger.LogDebug("Update check: newest in the apt repository is {0} (running {1})", newest.Version, Current);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            Failed("timeout", "no answer from packet-net.github.io in 15 s");
        }
        catch (HttpRequestException ex)
        {
            Failed("network", ex.Message);
        }
        catch (FormatException)
        {
            Failed("parse", "its answer was not an apt package list");
        }
        catch (Exception ex)
        {
            Failed("error:" + ex.GetType().Name, ex.Message);
        }
    }

    private void Failed(string kind, string why)
    {
        if (_failuresLogged.Add(kind))
        {
            logger.LogInformation(
                "Update check: cannot check the apt repository for a newer version: {0}. " +
                "It tries again every {1} hours and says nothing more about this until it works",
                why, AptPollInterval.TotalHours);
        }
    }

    private static string ResolveCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip the trailing build-metadata that .NET appends for
            // some publish modes: "0.8.0+abcd1234" → "0.8.0".
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    /// <summary>Compares two semver-ish version strings (dotted decimals,
    /// optional pre-release suffix). Returns &gt; 0 if <paramref name="a"/>
    /// is newer, &lt; 0 if older, 0 if equivalent. Tolerant: a
    /// non-parseable component is treated as 0.</summary>
    public static int CompareSemver(string a, string b)
    {
        var ap = a.Split('-')[0].Split('.');
        var bp = b.Split('-')[0].Split('.');
        var n = Math.Max(ap.Length, bp.Length);
        for (var i = 0; i < n; i++)
        {
            var av = i < ap.Length && int.TryParse(ap[i], out var x) ? x : 0;
            var bv = i < bp.Length && int.TryParse(bp[i], out var y) ? y : 0;
            if (av != bv) return av - bv;
        }
        return 0;
    }

    public sealed record LatestRelease(
        string Tag,
        string Name,
        string Url,
        DateTime? PublishedAt,
        DateTime FetchedAt);

    // Wire shape for the GitHub Releases API. Lowercase fields match
    // the JSON; we parse just what we need.
    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? tag_name { get; set; }
        [JsonPropertyName("name")] public string? name { get; set; }
        [JsonPropertyName("html_url")] public string? html_url { get; set; }
        [JsonPropertyName("published_at")] public DateTime? published_at { get; set; }
    }
}
