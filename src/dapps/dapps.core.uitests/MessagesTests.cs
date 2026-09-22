using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Messages page: tabbed view over outbound queue / local inbox /
/// dropped / live arrivals (SSE). Empty-state coverage is enough for
/// the static tabs; the live tab needs the filter input flow to prove
/// the JS is wired up, since on a fresh node there are no events.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class MessagesTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    /// <summary>
    /// Asserts each tab swaps its &quot;loading&quot; placeholder for
    /// real content once the /Operational snapshot arrives - either
    /// the empty-state text we expect on a fresh node, or rows from
    /// earlier tests in the shared fixture (the Overview quick-send /
    /// Compose tests submit outbound messages, so the &quot;outbound&quot;
    /// queue may legitimately be non-empty by the time this runs).
    /// </summary>
    [Theory]
    [InlineData("outbound", "outbound-body", "queue is empty")]
    [InlineData("inbox",    "inbox-body",    "inbox is empty")]
    [InlineData("dropped",  "dropped-body",  "no recent drops")]
    public async Task Messages_Tab_Replaces_Loading_With_Snapshot_Content(string tab, string bodyId, string emptyStateText)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Messages?tab={tab}");

        await page.WaitForFunctionAsync(
            $"() => {{ const el = document.querySelector('#{bodyId}'); return el && !el.textContent.includes('loading'); }}",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        var body = await page.Locator($"#{bodyId}").InnerTextAsync();

        // Accept either the empty-state placeholder or actual rows -
        // the absence of the loading text is the JS-fired-correctly
        // assertion; row content is fixture-shared state.
        var rowCount = await page.Locator($"#{bodyId} tr").CountAsync();
        if (rowCount == 1 && body.Contains(emptyStateText))
        {
            // Fresh node: the only row is the empty-state placeholder.
            return;
        }
        rowCount.Should().BeGreaterThan(0,
            $"the {tab} tab should render at least one tr (empty-state row or real data) once the snapshot arrives");
        body.Should().NotContain("loading",
            "JS replaced the placeholder, so the loading sentinel must be gone");
    }

    [Fact]
    public async Task Messages_Live_Tab_Filter_Updates_Summary()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        // DOMContentLoaded - the SSE connection is long-lived; NetworkIdle
        // would never settle.
        await page.GotoAsync($"{app.BaseUrl}/Messages?tab=live",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        (await page.Locator("#filter-summary").InnerTextAsync())
            .Should().Be("showing all rows");

        await page.FillAsync("#filter-app", "mail");
        var filtered = await page.Locator("#filter-summary").InnerTextAsync();
        filtered.Should().NotBe("showing all rows",
            "typing into a filter input should switch the summary to the row-count form");

        // Clear button resets all three filters.
        await page.ClickAsync("#filter-clear");
        (await page.Locator("#filter-app").InputValueAsync()).Should().BeEmpty();
        (await page.Locator("#filter-summary").InnerTextAsync())
            .Should().Be("showing all rows");
    }

    [Fact]
    public async Task Messages_Live_Tab_Reports_SSE_State()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Messages?tab=live",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // The SSE pill starts as "connecting" and either flips to
        // "live" (Events.onopen fires) or "reconnecting" if the route
        // is missing. Either is acceptable here; "connecting" forever
        // is the failure mode (means the SSE source never even tried).
        await page.WaitForFunctionAsync(
            "() => { const e = document.getElementById('sse-state'); return e && e.textContent !== 'connecting'; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
    }

    [Fact]
    public async Task Messages_Inbound_Legacy_URL_Redirects_To_Live_Tab()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Inbound",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        page.Url.Should().Contain("/Messages",
            "/Inbound is kept as a redirect so old bookmarks still resolve");
        page.Url.Should().Contain("tab=live");
    }

    /// <summary>
    /// Clicking a message id on the outbound tab should expand an
    /// inline payload preview row showing that message's actual
    /// content, mirroring the live tab's existing click-to-expand
    /// behaviour. Sends a message via Compose (to a non-local
    /// destination so it lands in the outbound queue) and asserts the
    /// clicked row's payload preview contains that exact text.
    /// </summary>
    [Fact]
    public async Task Messages_Outbound_Id_Click_Shows_Payload()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Compose");

        var payloadText = $"clickable-id-test-{Guid.NewGuid():N}";
        await page.FillAsync("input#App", "uitest");
        await page.FillAsync("input#Destination", "TEST-1");
        await page.FillAsync("input#Payload", payloadText);
        await page.FillAsync("input#Ttl", "3600");
        await page.ClickAsync("form.panel button[type='submit']");
        await page.WaitForSelectorAsync(".ok-banner",
            new PageWaitForSelectorOptions { Timeout = 5_000 });

        var id = await page.Locator(".card:has(.label:has-text('Message id')) .value.mono code").InnerTextAsync();

        await page.GotoAsync($"{app.BaseUrl}/Messages?tab=outbound");
        var row = page.Locator($"tr[data-id='{id}']");
        await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await row.Locator(".msg-id-link").ClickAsync();

        var payloadPre = page.Locator($"tr.payload-row[data-id='{id}'] pre[data-mode='text']");
        await payloadPre.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        (await payloadPre.InnerTextAsync()).Should().Contain(payloadText);
    }
}
