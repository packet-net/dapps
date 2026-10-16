using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// End-to-end coverage of the dashboard's <c>/Tokens</c> page (#316
/// follow-up): mint a token, use it against the real <c>/AppApi/*</c>
/// bearer-auth surface with <c>AuthRequired=true</c>, revoke it, and
/// confirm the plaintext never reappears once minted. Own
/// <see cref="WebAppFixture"/> instance booted with
/// <c>authRequired: true</c> - every other UI test pins the shared
/// fixture to <c>false</c>, so this is the one place exercising the
/// real new-install default end to end rather than just rendering.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppTokensPageTests(PlaywrightFixture pw) : IAsyncLifetime
{
    private readonly WebAppFixture _app = new(authRequired: true);
    private const string AdminPassword = "ui-test-password-app-tokens";
    private const string Callsign = "M0LTE-8";

    public async ValueTask InitializeAsync() => await _app.InitializeAsync();
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<IBrowserContext> NewSetUpContextAsync()
    {
        var ctx = await pw.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();

        await page.GotoAsync(_app.BaseUrl);
        page.Url.Should().EndWith("/Setup");
        await page.FillAsync("input[name='password']", AdminPassword);
        await page.FillAsync("input[name='confirm']", AdminPassword);
        await page.ClickAsync("button[type='submit']");

        await page.WaitForSelectorAsync("input[name='callsign']",
            new PageWaitForSelectorOptions { Timeout = 20_000 });
        await page.FillAsync("input[name='callsign']", Callsign);
        await page.ClickAsync("#bearerForm button[type='submit']");
        await page.WaitForURLAsync(url => !url.Contains("/Setup", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 20_000 });

        await page.CloseAsync();
        return ctx;
    }

    [Fact]
    public async Task MintUseAndRevoke_RoundTrips_AgainstTheRealBearerAuthSurface()
    {
        await using var ctx = await NewSetUpContextAsync();
        var page = await ctx.NewPageAsync();

        // 1. Mint a token for app "hello" on the Tokens page.
        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        await page.FillAsync("input[name='App']", "hello");
        await page.ClickAsync("#mint-token-btn");

        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });
        var token = (await page.Locator("#minted-token").InnerTextAsync()).Trim();
        token.Should().NotBeNullOrWhiteSpace("the page should show the plaintext token right after minting");

        // 2. Use it against the real AppApi bearer-auth surface -
        // AuthRequired is true on this fixture, so this only succeeds
        // if the token is genuinely valid for app "hello".
        var outboundResp = await ctx.APIRequest.PostAsync($"{_app.BaseUrl}/AppApi/outbound", new APIRequestContextOptions
        {
            DataObject = new { app = "hello", destCallsign = "G7XYZ", payload = Convert.ToBase64String("hi"u8.ToArray()) },
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["Authorization"] = $"Bearer {token}",
            },
        });
        outboundResp.Ok.Should().BeTrue($"a freshly minted token should authenticate against /AppApi/outbound; got {outboundResp.Status} {await outboundResp.TextAsync()}");

        // 3. Reload the Tokens page - the plaintext must never reappear,
        // only the app name and mint time.
        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        var bodyText = await page.Locator("body").InnerTextAsync();
        bodyText.Should().Contain("hello", "the app should be listed");
        bodyText.Should().NotContain(token, "the plaintext token must never be shown again after the minting response");

        // 4. Revoke it from the page.
        page.Dialog += async (_, dlg) => await dlg.AcceptAsync();
        await page.ClickAsync("button:has-text('Revoke')");
        await page.WaitForSelectorAsync(".ok-banner", new PageWaitForSelectorOptions { Timeout = 5_000 });
        (await page.Locator(".ok-banner").InnerTextAsync()).Should().Contain("Revoked");

        // 5. The same token must now be rejected.
        var afterRevokeResp = await ctx.APIRequest.PostAsync($"{_app.BaseUrl}/AppApi/outbound", new APIRequestContextOptions
        {
            DataObject = new { app = "hello", destCallsign = "G7XYZ", payload = Convert.ToBase64String("hi"u8.ToArray()) },
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["Authorization"] = $"Bearer {token}",
            },
        });
        afterRevokeResp.Ok.Should().BeFalse("a revoked token must stop authenticating immediately");
    }

    [Fact]
    public async Task MintedToken_NeverAppearsInTheListingAfterTheFirstRender()
    {
        await using var ctx = await NewSetUpContextAsync();
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        await page.FillAsync("input[name='App']", "secretcheck");
        await page.ClickAsync("#mint-token-btn");

        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });
        var token = (await page.Locator("#minted-token").InnerTextAsync()).Trim();

        // Mint a second app too, to make sure the listing table in
        // general doesn't leak either row's secret.
        await page.FillAsync("input[name='App']", "secretcheck2");
        await page.ClickAsync("#mint-token-btn");
        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });
        var token2 = (await page.Locator("#minted-token").InnerTextAsync()).Trim();

        // A fresh GET (no just-minted state) must list both apps by
        // name only - neither plaintext token anywhere on the page.
        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        var bodyText = await page.Locator("body").InnerTextAsync();
        bodyText.Should().Contain("secretcheck").And.Contain("secretcheck2");
        bodyText.Should().NotContain(token);
        bodyText.Should().NotContain(token2);
        (await page.Locator("#minted-token").CountAsync()).Should().Be(0,
            "a plain listing load should render no minted-token block at all");
    }
}
