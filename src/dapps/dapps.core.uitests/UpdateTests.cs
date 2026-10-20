using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Update / upgrade UI surface. The dashboard reads
/// <c>tables.update</c> from the <c>/Operational?full=true</c> snapshot
/// and renders four downstream surfaces from it: the topbar build chip
/// (<c>#ts-version</c>), the Overview hero update tile
/// (<c>#hero-update*</c>), the "needs attention" alert and the upgrade
/// banner when an update is available. DAPPS never installs an update
/// itself; these surfaces only say how to upgrade. Each combination of
/// <c>(isDevBuild, isAvailable, latest)</c> drives different markup, and
/// pinning that against a real release feed is awkward, so these tests stub
/// <c>/Operational</c> via Playwright route interception and assert
/// the rendered UI for each state.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class UpdateTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    private const string CurrentVersion = "0.33.2";
    private const string LatestVersion = "0.34.0";

    // The Build chip in the topbar is text-only (no .led child element,
    // unlike Node/MQTT). The layout JS still calls led() against it but
    // led() no-ops when there's nothing to colour, so these tests assert
    // on the visible text the operator actually reads.

    [Fact]
    public async Task Topbar_Build_Chip_Up_To_Date_Shows_Current_Version()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        await WaitChipAsync(page, expected: $"v{CurrentVersion}");
    }

    [Fact]
    public async Task Topbar_Build_Chip_Update_Available_Shows_Both_Versions()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitChipAsync(page, expected: $"v{CurrentVersion} -> v{LatestVersion}");
    }

    [Fact]
    public async Task Topbar_Build_Chip_Dev_Build_Drops_V_Prefix()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(Current: "dev-abcdef0", IsDevBuild: true, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        // Dev builds skip the v-prefix so the operator sees the raw
        // commit-shaped current value.
        await WaitChipAsync(page, expected: "dev-abcdef0");
    }

    [Fact]
    public async Task Hero_Update_Tile_Up_To_Date_Shows_Current_Pill()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "current");
        (await page.Locator("#hero-update-v").InnerTextAsync()).Trim()
            .Should().Be($"v{CurrentVersion}");
    }

    [Fact]
    public async Task Hero_Update_Tile_Available_Shows_Both_Versions()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");
        (await page.Locator("#hero-update-v").InnerTextAsync()).Trim()
            .Should().Be($"v{CurrentVersion} -> v{LatestVersion}");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Should().Contain("release page");
    }

    [Fact]
    public async Task Hero_Update_Tile_Dev_Build_Shows_Dev_Pill()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(Current: "dev-abcdef0", IsDevBuild: true, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "dev");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Trim()
            .Should().Be("dev build");
    }

    /// <summary>
    /// The "needs attention" panel computes its alerts from the same
    /// snapshot. An available release should appear there as an info-
    /// tone tile so an operator who isn't looking at the topbar still
    /// notices. A dev build or up-to-date state should NOT contribute
    /// an alert.
    /// </summary>
    [Fact]
    public async Task Update_Available_Adds_Needs_Attention_Alert()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        // The alerts panel renders one tile per alert; the "Update
        // available" alert is keyed by its title. CSS text-transform:
        // uppercase styles the .lbl rendering, so the alert title
        // text we read back from innerText is upper-cased.
        await page.WaitForFunctionAsync(
            $"() => Array.from(document.querySelectorAll('#alerts .lbl')).some(el => el.textContent.toLowerCase().includes('update available'))",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var alertText = (await page.Locator("#alerts").InnerTextAsync()).ToLowerInvariant();
        alertText.Should().Contain($"update available: v{LatestVersion.ToLowerInvariant()}");
    }

    [Fact]
    public async Task Update_Up_To_Date_Does_Not_Add_Update_Alert()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        // Up-to-date doesn't suppress every alert (a fresh node always
        // raises "no neighbours and no discovered peers"), so we can't
        // assert on the .empty placeholder. The narrower correct claim
        // is that the update-specific alert title is absent.
        await WaitTextAsync(page, "#hero-update-pill", "current");
        var alertText = (await page.Locator("#alerts").InnerTextAsync()).ToLowerInvariant();
        alertText.Should().NotContain("update available");
    }

    /// <summary>
    /// "Check now" link on the Overview hero update tile triggers
    /// POST /Update/check (the manual GitHub re-poll). The operator's
    /// expected entry-point for "is there a new version?" rather
    /// than navigating to Settings -> Updates.
    /// </summary>
    [Fact]
    public async Task Hero_Update_Check_Now_Posts_Update_Check()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: false));

        var stubBody = """
        {"current":"0.33.7","isDevBuild":false,"latest":null,"releaseUrl":null,"isAvailable":false,"fetchedAt":"2026-05-09T00:00:00Z"}
        """;
        await page.RouteAsync("**/Update/check", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = stubBody,
            });
        });

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "current");
        (await page.Locator("#hero-update-check").IsVisibleAsync())
            .Should().BeTrue("Check now link should be visible in the up-to-date state");

        var requestTask = page.WaitForRequestAsync(
            req => req.Url.EndsWith("/Update/check", StringComparison.Ordinal) && req.Method == "POST",
            new PageWaitForRequestOptions { Timeout = 5_000 });
        await page.ClickAsync("#hero-update-check");
        await requestTask;
    }

    /// <summary>
    /// The Settings panel exposes <c>UpdateCheckEnabled</c> as a
    /// checkbox. Toggle it off, save, reload, verify the persisted
    /// state - and restore so the rest of the suite isn't perturbed.
    /// </summary>
    private const string AptCommand = "sudo apt update && sudo apt install --only-upgrade dapps";

    [Fact]
    public async Task Apt_Install_Update_Available_Shows_Banner_With_Command()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, ReleaseUrl: $"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}",
            Install: "apt", UpgradeCommand: AptCommand));

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await page.Locator("#upgrade-title").InnerTextAsync()).Should()
            .Be($"DAPPS v{LatestVersion} is available (you have v{CurrentVersion}).");
        (await page.Locator("#upgrade-cmd").InnerTextAsync()).Should().Be(AptCommand);
        (await page.Locator("#upgrade-notes").GetAttributeAsync("href")).Should()
            .Be($"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}");

        await WaitTextAsync(page, "#hero-update-pill", "available");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Should().Contain("apt");
    }

    [Fact]
    public async Task Apt_Install_Banner_Shows_On_Every_Page()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, Install: "apt", UpgradeCommand: AptCommand));

        await page.GotoAsync(app.BaseUrl + "/Topology");
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
    }

    [Fact]
    public async Task Hand_Installed_Deb_Banner_Links_The_New_Deb()
    {
        const string url = $"https://github.com/packet-net/dapps/releases/download/v{LatestVersion}/dapps_{LatestVersion}_amd64.deb";
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, Install: "deb",
            UpgradeCommand: $"sudo apt install ./dapps_{LatestVersion}_amd64.deb", DownloadUrl: url));

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await page.Locator("#upgrade-what a").GetAttributeAsync("href")).Should().Be(url);
        (await page.Locator("#upgrade-cmd").InnerTextAsync()).Should().Be($"sudo apt install ./dapps_{LatestVersion}_amd64.deb");
    }

    [Fact]
    public async Task Other_Install_Banner_Points_At_The_Release_Page_And_Docker()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, ReleaseUrl: $"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}"));

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await page.Locator("#upgrade-what").InnerTextAsync()).Should().Contain("release page").And.Contain("Docker");
        (await page.Locator("#upgrade-cmd").IsVisibleAsync()).Should().BeFalse("there is no apt command for this install");
        (await page.Locator("#upgrade-notes").GetAttributeAsync("href")).Should()
            .Be($"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}");
    }

    [Fact]
    public async Task Up_To_Date_Hides_The_Upgrade_Banner()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "current");
        (await page.Locator("#upgrade-banner").IsVisibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Settings_UpdateCheckEnabled_Toggle_Persists()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        var box = page.Locator("input[name='UpdateCheckEnabled']");
        var originallyChecked = await box.IsCheckedAsync();
        try
        {
            // Flip it.
            if (originallyChecked) await box.UncheckAsync();
            else await box.CheckAsync();

            await page.ClickAsync("form#settings-form button[type='submit']");
            await page.WaitForSelectorAsync(".ok-banner",
                new PageWaitForSelectorOptions { Timeout = 5_000 });

            await page.ReloadAsync();
            (await page.Locator("input[name='UpdateCheckEnabled']").IsCheckedAsync())
                .Should().Be(!originallyChecked, "the toggled value persists across reload");
        }
        finally
        {
            // Restore so subsequent tests aren't perturbed.
            var current = page.Locator("input[name='UpdateCheckEnabled']");
            if (await current.IsCheckedAsync() != originallyChecked)
            {
                if (originallyChecked) await current.CheckAsync();
                else await current.UncheckAsync();
                await page.ClickAsync("form#settings-form button[type='submit']");
                await page.WaitForSelectorAsync(".ok-banner",
                    new PageWaitForSelectorOptions { Timeout = 5_000 });
            }
        }
    }

    // --- helpers ---

    private sealed record UpdateFields(
        string Current,
        bool IsDevBuild,
        bool IsAvailable,
        string? Latest = null,
        string? ReleaseUrl = null,
        DateTime? FetchedAt = null,
        string Install = "other",
        string? UpgradeCommand = null,
        string? DownloadUrl = null);

    /// <summary>
    /// Stub the layout's <c>/Operational?full=true</c> poll with a
    /// minimal valid snapshot whose only meaningful field is
    /// <c>tables.update</c>. The other counters are zeroed so the
    /// dashboard's other surfaces (queue summary, alerts, sparkline)
    /// don't pick up incidental state from a real upstream.
    /// </summary>
    private static Task StubOperationalAsync(IPage page, UpdateFields fields)
    {
        var json = BuildSnapJson(fields);
        return page.RouteAsync("**/Operational*", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = json,
            });
        });
    }

    /// <summary>Build the minimal-but-complete /Operational JSON for a
    /// given <see cref="UpdateFields"/> shape. Extracted so dynamic-
    /// per-call route handlers can rebuild the body on each tick.</summary>
    private static string BuildSnapJson(UpdateFields fields)
    {
        var snap = new
        {
            callsign = LoggedInWebAppFixture.Callsign,
            version = fields.Current,
            generatedAt = DateTime.UtcNow,
            uptimeSeconds = 0,
            status = "healthy",
            callsignConfigured = true,
            nodeReachable = true,
            mqttBrokerUp = true,
            lastForwardSuccessAt = (DateTime?)null,
            forwardAttempts = 0,
            forwardSuccess = 0,
            forwardFailure = 0,
            ttlExpiredDrops = 0,
            noRouteSkips = 0,
            agwReconnects = 0,
            agwLastReconnectAt = (DateTime?)null,
            inboundConnects = 0,
            hashMismatches = 0,
            probeAttempts = 0,
            probeSuccess = 0,
            probeFailure = 0,
            pollAttempts = 0,
            pollSuccess = 0,
            pollFailure = 0,
            routesLearned = 0,
            peersAgedOut = 0,
            budgetRefusals = 0,
            pendingOutboundCount = 0,
            undeliveredLocalCount = 0,
            totalMessagesCount = 0,
            neighbourCount = 0,
            discoveredPeerCount = 0,
            discoveryChannelCount = 0,
            airtimeConsumedSecondsLastHour = 0,
            airtimeBudgetSecondsPerHour = 0,
            recentEvents = Array.Empty<object>(),
            neighbourLinks = Array.Empty<object>(),
            tables = new
            {
                outbound = Array.Empty<object>(),
                localInbox = Array.Empty<object>(),
                dropped = Array.Empty<object>(),
                neighbours = Array.Empty<object>(),
                discoveredPeers = Array.Empty<object>(),
                discoveryChannels = Array.Empty<object>(),
                probedNodes = Array.Empty<object>(),
                polledNodes = Array.Empty<object>(),
                update = new
                {
                    current = fields.Current,
                    isDevBuild = fields.IsDevBuild,
                    latest = fields.Latest,
                    releaseUrl = fields.ReleaseUrl,
                    isAvailable = fields.IsAvailable,
                    fetchedAt = fields.FetchedAt,
                    install = fields.Install,
                    upgradeCommand = fields.UpgradeCommand,
                    downloadUrl = fields.DownloadUrl,
                },
            },
        };
        return JsonSerializer.Serialize(snap);
    }

    /// <summary>Wait for the topbar build chip's value text to land on
    /// the expected string. The chip starts as "-" and updates on the
    /// first /Operational poll, which our stub fulfils synchronously.</summary>
    private static Task WaitChipAsync(IPage page, string expected) =>
        page.WaitForFunctionAsync(
            $"() => document.getElementById('ts-version-v')?.textContent === {JsonSerializer.Serialize(expected)}",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

    /// <summary>Wait for an element's textContent to equal the expected
    /// string. Whitespace-trimming kept off so callers can match the
    /// exact rendered token.</summary>
    private static Task WaitTextAsync(IPage page, string selector, string expected) =>
        page.WaitForFunctionAsync(
            $"() => document.querySelector({JsonSerializer.Serialize(selector)})?.textContent.trim() === {JsonSerializer.Serialize(expected)}",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
}
