using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Settings page round-trip. The form posts JSON to <c>/Config</c>; a
/// regression that breaks deserialisation here breaks every save - the
/// case that motivated this suite was a missing
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// on <c>ProbeStrategy</c>, which made every save (including unrelated
/// fields like Callsign) fail with a 400 because the body never bound.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class SettingsTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Settings_Form_Renders_All_Panel_Groups()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        // Each panel is one of the SystemOptions concept groups. Asserting
        // the count guards against silent panel removal during a refactor.
        var panelHeadings = await page.Locator("form#settings-form .panel h3").AllInnerTextsAsync();
        panelHeadings.Should().HaveCountGreaterThan(5,
            "the settings form is broken into Identity / App interface / Discovery / Probing / Polling / Heartbeat / Updates groups");

        // Spot-check key fields exist by name attribute.
        foreach (var name in new[] { "Callsign", "NodeHost", "ProbeStrategy", "MqttPort", "HeartbeatEnabled", "MeshCoreEnabled", "MeshCoreRegion" })
        {
            (await page.Locator($"[name='{name}']").CountAsync())
                .Should().Be(1, $"Settings form should expose the {name} input");
        }
    }

    /// <summary>
    /// Saves the form unchanged from its rendered values. This is the
    /// canary the original bug would have tripped: the form serialises
    /// <c>ProbeStrategy</c> as a string (e.g. <c>"FixedInterval"</c>)
    /// and posts that to <c>/Config</c>; without a JsonStringEnumConverter
    /// the body fails to bind and every save returns 400, including
    /// unrelated fields like Callsign.
    /// </summary>
    [Fact]
    public async Task Settings_Save_Roundtrip_Returns_Success_Banner()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        await page.ClickAsync("form#settings-form button[type='submit']");

        await page.WaitForSelectorAsync(".ok-banner",
            new PageWaitForSelectorOptions { Timeout = 5_000 });
        var banner = (await page.Locator(".ok-banner").InnerTextAsync()).Trim();
        banner.Should().Be("Settings saved.");
    }

    /// <summary>
    /// Cycle every <c>ProbeStrategy</c> option and save. If the enum
    /// converter regresses (string-shape vs int-shape), at least one of
    /// these will surface the 400 - the alert is captured via the
    /// dialog handler so the assertion fails with a concrete message.
    /// </summary>
    [Theory]
    [InlineData("FixedInterval")]
    [InlineData("Overnight")]
    [InlineData("WhenQuiet")]
    public async Task Settings_Save_Each_ProbeStrategy_Succeeds(string strategy)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        string? alertText = null;
        page.Dialog += async (_, dlg) => { alertText = dlg.Message; await dlg.DismissAsync(); };

        await page.GotoAsync($"{app.BaseUrl}/Settings");
        await page.SelectOptionAsync("select[name='ProbeStrategy']", strategy);
        await page.ClickAsync("form#settings-form button[type='submit']");

        await page.WaitForSelectorAsync(".ok-banner",
            new PageWaitForSelectorOptions { Timeout = 5_000 });
        alertText.Should().BeNull($"saving ProbeStrategy={strategy} should not raise an error alert; got: {alertText}");
    }

    [Fact]
    public async Task Settings_Save_Persists_Callsign_Change()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        // Read the current callsign so we can restore it after the test.
        var original = await page.InputValueAsync("input[name='Callsign']");
        const string overridden = "M0LTE-9";
        try
        {
            await page.FillAsync("input[name='Callsign']", overridden);
            await page.ClickAsync("form#settings-form button[type='submit']");
            await page.WaitForSelectorAsync(".ok-banner",
                new PageWaitForSelectorOptions { Timeout = 5_000 });

            await page.ReloadAsync();
            (await page.InputValueAsync("input[name='Callsign']")).Should().Be(overridden,
                "the saved callsign survives a page reload (i.e. it persisted, not just rendered the success banner)");
        }
        finally
        {
            await page.FillAsync("input[name='Callsign']", original);
            await page.ClickAsync("form#settings-form button[type='submit']");
            await page.WaitForSelectorAsync(".ok-banner",
                new PageWaitForSelectorOptions { Timeout = 5_000 });
        }
    }

    /// <summary>
    /// Bearer-conditional fields: switching between AGW and RHPv2 in
    /// the Node bearer dropdown hides the unselected bearer's settings
    /// (their other-bearer counterpart is irrelevant, and showing both
    /// at once led operators to "fix" inactive ports without realising
    /// they did nothing). The values themselves stay in the form so
    /// flipping back doesn't lose the previous bearer's config.
    /// </summary>
    [Fact]
    public async Task Settings_Bearer_Toggle_Hides_Inactive_Fields()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        // AGW selected on first paint (the fixture's setup picked agw).
        await page.SelectOptionAsync("select[name='NodeBearer']", "agw");
        (await page.Locator("input[name='AgwPort']").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator("input[name='RhpPort']").IsVisibleAsync()).Should().BeFalse();
        (await page.Locator("input[name='RhpUser']").IsVisibleAsync()).Should().BeFalse();
        (await page.Locator("input[name='RhpPass']").IsVisibleAsync()).Should().BeFalse();

        // Flip to RHPv2: AGW-specific fields hide; RHPv2 fields surface.
        await page.SelectOptionAsync("select[name='NodeBearer']", "rhpv2");
        (await page.Locator("input[name='AgwPort']").IsVisibleAsync()).Should().BeFalse();
        (await page.Locator("input[name='RhpPort']").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator("input[name='RhpUser']").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator("input[name='RhpPass']").IsVisibleAsync()).Should().BeTrue();

        // Restore so the rest of the suite isn't perturbed (the toggle
        // is visual-only - no save happens until Save is clicked - but
        // belt-and-braces).
        await page.SelectOptionAsync("select[name='NodeBearer']", "agw");
    }

    [Fact]
    public async Task Settings_Check_For_Updates_Button_Posts_Update_Check()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        // Stub /Update/check so the test isn't dependent on real GitHub
        // connectivity; assert the POST fires.
        var stubBody = """
        {"current":"0.33.5","isDevBuild":false,"latest":null,"releaseUrl":null,"isAvailable":false,"fetchedAt":"2026-05-09T00:00:00Z","requestPending":false,"lastRun":null}
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

        await page.GotoAsync($"{app.BaseUrl}/Settings");
        var requestTask = page.WaitForRequestAsync(
            req => req.Url.EndsWith("/Update/check", StringComparison.Ordinal) && req.Method == "POST",
            new PageWaitForRequestOptions { Timeout = 5_000 });

        await page.ClickAsync("#check-updates-btn");
        await requestTask;

        // Status text reflects the stubbed response.
        await page.WaitForFunctionAsync(
            "() => document.getElementById('check-updates-status').textContent.includes('up to date')",
            null,
            new PageWaitForFunctionOptions { Timeout = 5_000 });
    }

    [Fact]
    public async Task Settings_Rotate_Password_Form_Visible()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Settings");

        // The admin-password rotation form is a separate <form> below
        // the SystemOptions form; assert it's still rendered + has the
        // required password input.
        (await page.Locator("input[name='password'][type='password']").CountAsync())
            .Should().Be(1, "Settings page exposes the rotate-admin-password form");
    }
}
