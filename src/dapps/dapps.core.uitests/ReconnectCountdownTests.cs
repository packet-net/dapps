using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// The Packet node tile's reconnect countdown and "Retry now" link,
/// driven by the <c>nodeReconnectAttempt</c> / <c>nodeReconnectAt</c>
/// fields of the <c>/Operational</c> snapshot. Getting the real daemon
/// into a mid-backoff state on demand would mean a fake node that
/// accepts and drops, so instead the snapshot the dashboard polls is
/// fetched for real and patched in flight, which keeps every other
/// field the page reads exactly as the daemon produces it.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class ReconnectCountdownTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task PacketNodeTile_ShowsCountdownAndRetryLink_WhileReconnecting()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubReconnectingAsync(page, attempt: 2, retryIn: TimeSpan.FromSeconds(30));

        await page.GotoAsync(app.BaseUrl);

        await WaitRetryRowAsync(page, visible: true);
        var meta = (await page.Locator("#hero-node-retry-meta").InnerTextAsync()).ToLowerInvariant();
        meta.Should().Contain("retrying in").And.Contain("attempt 2");
        (await page.Locator("#hero-node-retry-action").IsVisibleAsync())
            .Should().BeTrue("the operator can jump the queue from the tile");
    }

    [Fact]
    public async Task PacketNodeTile_HidesCountdown_WhileConnected()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubConnectedAsync(page);

        await page.GotoAsync(app.BaseUrl);

        await page.WaitForSelectorAsync("#hero-node-pill", new PageWaitForSelectorOptions { Timeout = 10_000 });
        await WaitRetryRowAsync(page, visible: false);
    }

    /// <summary>
    /// The fixture points the daemon at localhost:8000 with nothing
    /// listening, so the real, unpatched snapshot is the backoff
    /// plumbing working end to end: the AGW inbound service is refused,
    /// records the failure, and publishes the countdown.
    /// </summary>
    [Fact]
    public async Task RealSnapshot_WithNoNodeListening_ReportsABackoffInFlight()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var resp = await ctx.APIRequest.GetAsync($"{app.BaseUrl}/Operational");
            resp.Status.Should().Be(200);
            var snap = JsonNode.Parse(await resp.TextAsync())!.AsObject();
            var attempt = snap["nodeReconnectAttempt"]?.GetValue<int>() ?? 0;
            if (attempt >= 1)
            {
                snap["nodeReconnectAt"]?.GetValue<string>().Should().NotBeNullOrEmpty(
                    "a failed connect always schedules the next attempt");
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the daemon never reported a reconnect attempt against the absent node");
            }
            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task RetryNowLink_PostsToOperationalRetryNow()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await StubReconnectingAsync(page, attempt: 1, retryIn: TimeSpan.FromSeconds(10));
        // Registered after the snapshot stub so it wins for this path
        // (Playwright checks the most recently added route first).
        await page.RouteAsync("**/Operational/retry-now", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = "{\"triggered\":true}",
        }));

        await page.GotoAsync(app.BaseUrl);
        await WaitRetryRowAsync(page, visible: true);

        var posted = page.WaitForRequestAsync(
            req => req.Url.EndsWith("/Operational/retry-now", StringComparison.Ordinal) && req.Method == "POST",
            new PageWaitForRequestOptions { Timeout = 5_000 });
        await page.ClickAsync("#hero-node-retry-action");
        await posted;
    }

    /// <summary>Serves the daemon's own snapshot with the node marked
    /// unreachable and a backoff wait in flight.</summary>
    private static Task StubReconnectingAsync(IPage page, int attempt, TimeSpan retryIn) =>
        page.RouteAsync("**/Operational*", async route =>
        {
            var real = await route.FetchAsync();
            var snap = JsonNode.Parse(await real.TextAsync())!.AsObject();
            snap["nodeReachable"] = false;
            snap["nodeReconnectAttempt"] = attempt;
            snap["nodeReconnectAt"] = DateTime.UtcNow.Add(retryIn).ToString("O");
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = real,
                Body = snap.ToJsonString(),
            });
        });

    /// <summary>Serves the daemon's own snapshot as if the node were up
    /// and no backoff were in flight.</summary>
    private static Task StubConnectedAsync(IPage page) =>
        page.RouteAsync("**/Operational*", async route =>
        {
            var real = await route.FetchAsync();
            var snap = JsonNode.Parse(await real.TextAsync())!.AsObject();
            snap["nodeReachable"] = true;
            snap["nodeReconnectAttempt"] = 0;
            snap["nodeReconnectAt"] = null;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = real,
                Body = snap.ToJsonString(),
            });
        });

    private static Task WaitRetryRowAsync(IPage page, bool visible) =>
        page.WaitForFunctionAsync(
            $"() => (document.querySelector('#hero-node-retry')?.style.display === 'flex') === {(visible ? "true" : "false")}",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
}
