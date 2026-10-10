using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Update / upgrade UI surface. The dashboard reads
/// <c>tables.update</c> from the <c>/Operational?full=true</c> snapshot
/// and renders four downstream surfaces from it: the topbar build chip
/// (<c>#ts-version</c>), the Overview hero update tile
/// (<c>#hero-update*</c>), the "needs attention" alert when an update
/// is available, and the "Apply update" link that POSTs to
/// <c>/Update/apply</c>. Each combination of <c>(isDevBuild,
/// isAvailable, latest)</c> drives different markup, and pinning that
/// behaviour against a real updater is awkward (needs a fake GitHub
/// Releases response and a dummy binary swap), so these tests stub
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
        (await page.Locator("#hero-update-action").IsVisibleAsync())
            .Should().BeFalse("there is nothing to apply when up to date");
    }

    [Fact]
    public async Task Hero_Update_Tile_Available_Surfaces_Apply_Link()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");
        (await page.Locator("#hero-update-v").InnerTextAsync()).Trim()
            .Should().Be($"v{CurrentVersion} -> v{LatestVersion}");
        (await page.Locator("#hero-update-action").IsVisibleAsync())
            .Should().BeTrue("the Apply update link is the affordance the operator clicks");
    }

    [Fact]
    public async Task Hero_Update_Tile_Dev_Build_Shows_Dev_Pill_And_No_Apply_Link()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(Current: "dev-abcdef0", IsDevBuild: true, IsAvailable: false));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "dev");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Trim()
            .Should().Be("dev builds do not auto-update");
        (await page.Locator("#hero-update-action").IsVisibleAsync())
            .Should().BeFalse("dev builds aren't applyable through this UI");
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
    /// Clicking "Apply update" raises a confirm() dialog and POSTs to
    /// <c>/Update/apply</c> only on accept. Stubs the POST so the test
    /// doesn't actually trigger the privileged updater - we just want
    /// to prove the JS handler made the right call.
    /// </summary>
    [Fact]
    public async Task Apply_Update_Click_Confirms_Then_Posts_Update_Apply()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        // Stub /Update/apply - 204 with no body, like the real handler.
        await page.RouteAsync("**/Update/apply", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
        });

        string? confirmMessage = null;
        page.Dialog += async (_, dlg) =>
        {
            confirmMessage = dlg.Message;
            await dlg.AcceptAsync();
        };

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");

        // POST /Update/apply lands when we accept the confirm.
        var requestTask = page.WaitForRequestAsync(
            req => req.Url.EndsWith("/Update/apply", StringComparison.Ordinal) && req.Method == "POST",
            new PageWaitForRequestOptions { Timeout = 5_000 });
        await page.ClickAsync("#hero-update-action");
        await requestTask;

        confirmMessage.Should().NotBeNull("the apply-update handler raises a confirm before doing anything");
        confirmMessage!.Should().Contain($"v{LatestVersion}");
    }

    /// <summary>
    /// Dismissing the confirm dialog should suppress the POST entirely.
    /// </summary>
    [Fact]
    public async Task Apply_Update_Click_Dismiss_Suppresses_Post()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        var postFired = false;
        await page.RouteAsync("**/Update/apply", async route =>
        {
            postFired = true;
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
        });

        page.Dialog += async (_, dlg) => await dlg.DismissAsync();

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");
        await page.ClickAsync("#hero-update-action");

        // Give the click handler a tick to do nothing.
        await page.WaitForTimeoutAsync(500);
        postFired.Should().BeFalse("dismissing the confirm dialog must NOT POST /Update/apply");
    }

    [Fact]
    public async Task Hero_Update_Tile_RequestPending_Shows_Queued_State()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion, RequestPending: true));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "queued");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Trim()
            .Should().Contain("60s",
                "operator should know the timer fires within a minute");
        (await page.Locator("#hero-update-action").IsVisibleAsync())
            .Should().BeFalse("the apply affordance hides while the request is in flight");
    }

    [Theory]
    [InlineData("Checking",    "checking")]
    [InlineData("Downloading", "downloading")]
    [InlineData("Swapping",    "swapping")]
    [InlineData("Restarting",  "restarting")]
    [InlineData("Verifying",   "verifying")]
    public async Task Hero_Update_Tile_InFlight_Phase_Shows_In_Meta(string phase, string metaSubstring)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
                Latest: LatestVersion, RequestPending: true,
                Phase: phase, FromVersion: CurrentVersion, ToVersion: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "applying");
        var meta = (await page.Locator("#hero-update-meta").InnerTextAsync()).ToLowerInvariant();
        meta.Should().Contain(metaSubstring, $"the {phase} phase should surface readable progress text");
    }

    [Fact]
    public async Task Hero_Update_Tile_Failed_Phase_Surfaces_Error_And_Retry()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
                Latest: LatestVersion, Phase: "Failed", LastRunError: "GitHub fetch failed"));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "failed");
        (await page.Locator("#hero-update-meta").InnerTextAsync())
            .Should().Contain("GitHub fetch failed");
        (await page.Locator("#hero-update-action").InnerTextAsync()).Trim()
            .Should().Be("Retry update");
    }

    /// <summary>
    /// Optimistic-UI assertion: clicking Apply flips the tile to a
    /// queued state *without* waiting for the next /Operational poll
    /// to land. The 5s gap before the snap refreshes is exactly the
    /// silent-window the operator complained about.
    /// </summary>
    [Fact]
    public async Task Apply_Update_Click_Optimistically_Repaints_Pill_To_Queued()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));
        // Stall /Update/apply long enough that the only thing repainting
        // the pill before the response is the optimistic-UI code path.
        await page.RouteAsync("**/Update/apply", async route =>
        {
            await Task.Delay(2000);
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
        });
        page.Dialog += async (_, dlg) => await dlg.AcceptAsync();

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");

        await page.ClickAsync("#hero-update-action");
        await WaitTextAsync(page, "#hero-update-pill", "queued");
        (await page.Locator("#hero-update-action").IsVisibleAsync())
            .Should().BeFalse("apply link hides as soon as the request is in flight");
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
        {"current":"0.33.7","isDevBuild":false,"latest":null,"releaseUrl":null,"isAvailable":false,"fetchedAt":"2026-05-09T00:00:00Z","requestPending":false,"lastRun":null}
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

    [Fact]
    public async Task Hero_Update_Check_Now_Hidden_While_Apply_In_Flight()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
                Latest: LatestVersion, RequestPending: true,
                Phase: "Downloading", FromVersion: CurrentVersion, ToVersion: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "applying");
        (await page.Locator("#hero-update-check").IsVisibleAsync())
            .Should().BeFalse("re-checking GitHub is pointless during a swap; hide the link");
    }

    /// <summary>
    /// After an apply lands successfully the operator's open page is
    /// serving stale assets (HTML + cshtml-emitted JS were rendered by
    /// the previous binary). The layout's pull loop tracks the
    /// previous phase across snaps and schedules a hard refresh when
    /// the phase transitions from any non-Success state to Success.
    /// </summary>
    [Fact]
    public async Task Apply_Update_Success_Transition_Schedules_Hard_Refresh()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        // First snap: Verifying. Subsequent snap: Success. The route
        // handler tracks call count so the second pull() returns the
        // transitioned state.
        var calls = 0;
        await page.RouteAsync("**/Operational*", async route =>
        {
            calls++;
            var phase = calls == 1 ? "Verifying" : "Success";
            var fields = new UpdateFields(
                Current: calls == 1 ? CurrentVersion : LatestVersion,
                IsDevBuild: false, IsAvailable: false,
                Latest: LatestVersion, RequestPending: calls == 1,
                Phase: phase, FromVersion: CurrentVersion, ToVersion: LatestVersion);
            var json = BuildSnapJson(fields);
            await route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = json,
            });
        });

        await page.GotoAsync(app.BaseUrl);
        // First pull lands -> phase=Verifying recorded.
        await page.WaitForFunctionAsync(
            "() => window.__dappsLastSeenPhase === 'Verifying'",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        // Force the next pull -> phase=Success transition triggers the
        // refresh-schedule flag.
        await page.EvaluateAsync("() => window.dappsRefreshSnap()");
        await page.WaitForFunctionAsync(
            "() => window.__dappsReloadScheduled === true",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
    }

    [Fact]
    public async Task Apply_Update_Success_On_First_Load_Does_Not_Auto_Refresh()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        // Stub the snap with phase=Success on the very first response.
        // This mimics an operator opening a tab on a node that updated
        // some time ago - we should NOT auto-refresh because they
        // didn't witness a transition. The guard keys on
        // __dappsLastSeenPhase being a non-Success value, which it
        // isn't on cold load.
        await StubOperationalAsync(page,
            new UpdateFields(LatestVersion, IsDevBuild: false, IsAvailable: false,
                Phase: "Success", FromVersion: CurrentVersion, ToVersion: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await page.WaitForFunctionAsync(
            "() => window.__dappsLastSeenPhase === 'Success'",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var scheduled = await page.EvaluateAsync<bool>(
            "() => window.__dappsReloadScheduled === true");
        scheduled.Should().BeFalse(
            "no auto-refresh on cold-load when phase is already Success");
    }

    [Fact]
    public async Task Topbar_Build_Chip_Mirrors_InFlight_Phase()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page,
            new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
                Latest: LatestVersion, RequestPending: true,
                Phase: "Downloading", FromVersion: CurrentVersion, ToVersion: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitChipAsync(page, expected: "downloading...");
    }

    /// <summary>
    /// The Settings panel exposes <c>UpdateCheckEnabled</c> as a
    /// checkbox. Toggle it off, save, reload, verify the persisted
    /// state - and restore so the rest of the suite isn't perturbed.
    /// </summary>
    private const string AptCommand = "sudo apt update && sudo apt install --only-upgrade dapps";

    [Fact]
    public async Task Apt_Install_Update_Available_Shows_Banner_With_Command_And_No_Apply()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, ReleaseUrl: $"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}",
            Install: "apt", CanApply: false, UpgradeCommand: AptCommand));

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await page.Locator("#upgrade-title").InnerTextAsync()).Should()
            .Be($"DAPPS v{LatestVersion} is available (you have v{CurrentVersion}).");
        (await page.Locator("#upgrade-cmd").InnerTextAsync()).Should().Be(AptCommand);
        (await page.Locator("#upgrade-notes").GetAttributeAsync("href")).Should()
            .Be($"https://github.com/packet-net/dapps/releases/tag/v{LatestVersion}");

        await WaitTextAsync(page, "#hero-update-pill", "available");
        (await page.Locator("#hero-update-action").IsVisibleAsync()).Should().BeFalse(
            "apt owns the upgrade on a .deb install; there is no self-updater to apply it");
        (await page.Locator("#hero-update-meta").InnerTextAsync()).Should().Contain("apt");
    }

    [Fact]
    public async Task Apt_Install_Banner_Shows_On_Every_Page()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true,
            Latest: LatestVersion, Install: "apt", CanApply: false, UpgradeCommand: AptCommand));

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
            Latest: LatestVersion, Install: "deb", CanApply: false,
            UpgradeCommand: $"sudo apt install ./dapps_{LatestVersion}_amd64.deb", DownloadUrl: url));

        await page.GotoAsync(app.BaseUrl);
        await page.Locator("#upgrade-banner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        (await page.Locator("#upgrade-what a").GetAttributeAsync("href")).Should().Be(url);
        (await page.Locator("#upgrade-cmd").InnerTextAsync()).Should().Be($"sudo apt install ./dapps_{LatestVersion}_amd64.deb");
    }

    [Fact]
    public async Task Upgrade_Banner_Hidden_When_Up_To_Date_Or_Self_Updating()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        // A one-liner install with an update: the Apply button, not the banner.
        await StubOperationalAsync(page, new UpdateFields(CurrentVersion, IsDevBuild: false, IsAvailable: true, Latest: LatestVersion));

        await page.GotoAsync(app.BaseUrl);
        await WaitTextAsync(page, "#hero-update-pill", "available");
        (await page.Locator("#upgrade-banner").IsVisibleAsync()).Should().BeFalse();
        (await page.Locator("#hero-update-action").IsVisibleAsync()).Should().BeTrue();
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
        bool RequestPending = false,
        string? Phase = null,
        string? FromVersion = null,
        string? ToVersion = null,
        DateTime? LastRunUpdatedAt = null,
        string? LastRunError = null,
        string Install = "other",
        bool CanApply = true,
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
    /// per-call route handlers (e.g. the apply-success transition test)
    /// can rebuild the body on each tick.</summary>
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
                    requestPending = fields.RequestPending,
                    install = fields.Install,
                    canApply = fields.CanApply,
                    upgradeCommand = fields.UpgradeCommand,
                    downloadUrl = fields.DownloadUrl,
                    lastRun = fields.Phase is null ? null : (object)new
                    {
                        phase = fields.Phase,
                        fromVersion = fields.FromVersion ?? fields.Current,
                        toVersion = fields.ToVersion,
                        startedAt = DateTime.UtcNow.AddMinutes(-1),
                        updatedAt = fields.LastRunUpdatedAt ?? DateTime.UtcNow,
                        error = fields.LastRunError,
                    },
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
