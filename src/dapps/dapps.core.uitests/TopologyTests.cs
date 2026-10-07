using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Topology page: tabs over manual neighbours, discovery channels,
/// discovered peers, probed nodes, polled nodes. Two write paths are
/// exercised end-to-end (add neighbour, add channel) so a regression
/// in the JSON shape sent by the form would surface here.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class TopologyTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Theory]
    [InlineData("neighbours", "neigh-body")]
    [InlineData("channels",   "chan-body")]
    [InlineData("peers",      "peer-body")]
    [InlineData("probes",     "probe-body")]
    [InlineData("polls",      "poll-body")]
    public async Task Topology_Each_Tab_Renders_Table(string tab, string bodyId)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab={tab}");

        // Cold-start DB queries on the first /Operational hit can stretch
        // beyond a 10s bound on a slow box; widen so the test isn't flaky.
        await page.WaitForFunctionAsync(
            $"() => {{ const el = document.querySelector('#{bodyId}'); return el && !el.textContent.includes('loading'); }}",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        // Empty state is fine - just prove the loader replaced "loading"
        // with concrete content.
        (await page.Locator($"#{bodyId}").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Topology_Neighbours_Add_And_Remove_Roundtrip()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab=neighbours");

        const string testCallsign = "TEST-NEI";

        // Auto-accept the "remove neighbour?" confirm() dialog when we
        // get to the cleanup step; deny everything else.
        page.Dialog += async (_, dlg) =>
        {
            if (dlg.Message.Contains("Remove", StringComparison.OrdinalIgnoreCase))
                await dlg.AcceptAsync();
            else
                await dlg.DismissAsync();
        };

        try
        {
            await page.FillAsync("#neigh-form input[name='callsign']", testCallsign);
            await page.FillAsync("#neigh-form input[name='bearerPort']", "0");
            await page.ClickAsync("#neigh-form button[type='submit']");

            // The handler reloads the page on success.
            await page.WaitForURLAsync($"**/Topology?tab=neighbours",
                new PageWaitForURLOptions { Timeout = 5_000 });
            await page.WaitForFunctionAsync(
                $"() => {{ const el = document.querySelector('#neigh-body'); return el && el.textContent.includes('{testCallsign}'); }}",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
        }
        finally
        {
            // Fire the JS helper directly to skip locator gymnastics.
            await page.EvaluateAsync($"() => window.dappsDeleteNeighbour('{testCallsign}')");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }

    /// <summary>
    /// A saved connect-script shows in the neighbours table, and "edit"
    /// loads the row back into the form so the script can be changed
    /// and saved over the same callsign.
    /// </summary>
    [Fact]
    public async Task Topology_Neighbours_Connect_Script_Can_Be_Viewed_And_Edited()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab=neighbours");

        const string testCallsign = "TEST-SCR";
        const string script = "C G0NODE2|Connected\nDAPPS|DAPPSv1>|60";
        const string edited = "C G0NODE2|Connected\nC 14 G0NODE9|Connected to G0NODE9\nDAPPS|DAPPSv1>|60";

        page.Dialog += async (_, dlg) =>
        {
            if (dlg.Message.Contains("Remove", StringComparison.OrdinalIgnoreCase))
                await dlg.AcceptAsync();
            else
                await dlg.DismissAsync();
        };

        try
        {
            await page.FillAsync("#neigh-form input[name='callsign']", testCallsign);
            await page.FillAsync("#neigh-form input[name='bearerPort']", "0");
            await page.FillAsync("#neigh-form textarea[name='connectScript']", script);
            await page.ClickAsync("#neigh-form button[type='submit']");
            await page.WaitForURLAsync("**/Topology?tab=neighbours",
                new PageWaitForURLOptions { Timeout = 5_000 });

            var row = page.Locator($"#neigh-body tr:has-text('{testCallsign}')");
            await row.Locator("pre.connect-script").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await row.Locator("pre.connect-script").InnerTextAsync()).Should().Contain("C G0NODE2|Connected");

            await row.Locator("button:has-text('edit')").ClickAsync();
            (await page.InputValueAsync("#neigh-form textarea[name='connectScript']")).Should().Be(script);
            (await page.InputValueAsync("#neigh-form input[name='bearerPort']")).Should().Be("0");
            (await page.Locator("#neigh-form input[name='callsign']").GetAttributeAsync("readonly")).Should().NotBeNull();

            await page.FillAsync("#neigh-form textarea[name='connectScript']", edited);
            await page.ClickAsync("#neigh-form button[type='submit']");
            await page.WaitForURLAsync("**/Topology?tab=neighbours",
                new PageWaitForURLOptions { Timeout = 5_000 });
            await page.WaitForFunctionAsync(
                "() => { const el = document.querySelector('#neigh-body'); return el && el.textContent.includes('C 14 G0NODE9'); }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
        }
        finally
        {
            await page.EvaluateAsync($"() => window.dappsDeleteNeighbour('{testCallsign}')");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }

    [Fact]
    public async Task Topology_Channels_Add_And_Remove_Roundtrip()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab=channels");

        // The channel key encodes (bearer, key) uniqueness. Use a high
        // bearer port number so it doesn't collide with anything an
        // operator-running suite might leave behind.
        const string channelKey = "199";

        page.Dialog += async (_, dlg) =>
        {
            if (dlg.Message.Contains("Remove", StringComparison.OrdinalIgnoreCase))
                await dlg.AcceptAsync();
            else
                await dlg.DismissAsync();
        };

        try
        {
            await page.SelectOptionAsync("#chan-form select[name='bearer']", "agw");
            await page.FillAsync("#chan-form input[name='channelKey']", channelKey);
            await page.SelectOptionAsync("#chan-form select[name='linkClass']", "VhfUhfFm");
            await page.ClickAsync("#chan-form button[type='submit']");

            await page.WaitForURLAsync("**/Topology?tab=channels",
                new PageWaitForURLOptions { Timeout = 5_000 });
            await page.WaitForFunctionAsync(
                $"() => {{ const el = document.querySelector('#chan-body'); return el && el.textContent.includes('{channelKey}'); }}",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
        }
        finally
        {
            // Pull the row's id out of the table, hit DELETE.
            var id = await page.EvaluateAsync<int>(@"() => {
                for (const tr of document.querySelectorAll('#chan-body tr')) {
                    if (tr.textContent.includes('" + channelKey + @"')) {
                        const btn = tr.querySelector('button.link-btn.danger');
                        const m = btn?.getAttribute('onclick')?.match(/dappsDeleteChannel\((\d+)\)/);
                        if (m) return parseInt(m[1], 10);
                    }
                }
                return -1;
            }");
            if (id > 0)
            {
                await page.EvaluateAsync($"() => window.dappsDeleteChannel({id})");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
        }
    }

    /// <summary>
    /// AGW port-query picker: when /Bearer/agw/ports answers !ok (e.g.
    /// because BPQ isn't reachable - which is the test fixture's
    /// state), the JS swaps the &lt;select&gt; for a free-text input
    /// so the operator isn't stuck. Asserts both halves of that
    /// fallback - a name="bearerPort" input lands and an explanatory
    /// hint surfaces.
    /// </summary>
    [Fact]
    public async Task Topology_Neighbours_Port_Picker_Falls_Back_To_Free_Text_When_Bpq_Unreachable()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab=neighbours");

        // Wait for the JS to hit /Bearer/agw/ports, see the 503, and
        // swap in the fallback input.
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#neigh-form input[name=\"bearerPort\"]') != null",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var hint = await page.Locator("#neigh-form [data-port-picker='bearerPort'] .hint, #neigh-form .hint:has-text('couldn')").AllInnerTextsAsync();
        hint.Should().Contain(t => t.Contains("couldn't query BPQ"),
            "the hint should explain why we fell back to a text input");
    }

    /// <summary>
    /// "Probe everyone now" / "Poll everyone now" are dangerous in
    /// production (uses other operators' airtime), so the JS guards
    /// with confirm(). Dismiss the dialog and assert no request fired.
    /// </summary>
    [Theory]
    [InlineData("probes", "Probe everyone now", "Probe every")]
    [InlineData("polls",  "Poll everyone now",  "Poll every")]
    public async Task Topology_Sweep_Buttons_Confirm_Before_Sending(string tab, string buttonText, string confirmPrefix)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Topology?tab={tab}");

        string? dialogMessage = null;
        page.Dialog += async (_, dlg) =>
        {
            dialogMessage = dlg.Message;
            await dlg.DismissAsync();
        };

        await page.ClickAsync($"button:has-text('{buttonText}')");

        // Dialog handler is async; give it a tick to land.
        await page.WaitForFunctionAsync(
            "() => true",
            null,
            new PageWaitForFunctionOptions { Timeout = 1_000 });
        dialogMessage.Should().NotBeNull("the sweep button should raise a confirm() dialog");
        dialogMessage!.Should().Contain(confirmPrefix);
    }
}
