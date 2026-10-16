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

    /// <summary>
    /// The revoke confirm used to build a JS string literal by hand
    /// (<c>onsubmit="return confirm('...@t.AppName...')"</c>): HTML-
    /// encoding an app name there doesn't help, because the browser
    /// HTML-decodes an attribute value before handing it to the JS
    /// engine, so a name containing a quote breaks out of the string.
    /// The fix reads the name from a data- attribute via .dataset
    /// instead, which is never re-parsed as code. "o'brien" is exactly
    /// the kind of name a real person has and the old code broke on.
    /// </summary>
    [Fact]
    public async Task Revoke_AppNameWithQuoteCharacter_DoesNotBreakTheConfirmDialog()
    {
        await using var ctx = await NewSetUpContextAsync();
        var page = await ctx.NewPageAsync();
        const string hostileApp = "o'brien";

        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        await page.FillAsync("input[name='App']", hostileApp);
        await page.ClickAsync("#mint-token-btn");
        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });

        // Back to the plain listing (no just-minted panel in the way).
        await page.GotoAsync($"{_app.BaseUrl}/Tokens");

        var dialogs = new List<(string Type, string Message)>();
        page.Dialog += async (_, dlg) =>
        {
            dialogs.Add((dlg.Type, dlg.Message));
            await dlg.AcceptAsync();
        };

        await page.ClickAsync("button:has-text('Revoke')");
        await page.WaitForSelectorAsync(".ok-banner", new PageWaitForSelectorOptions { Timeout = 5_000 });

        dialogs.Should().HaveCount(1,
            "a hostile app name must trigger exactly one confirm dialog - broken-out JS from the old inline onsubmit would show as an extra alert() or a page error instead");
        dialogs[0].Type.Should().Be("confirm", "it must be the intended confirm(), not some other dialog kind an injection could trigger");
        dialogs[0].Message.Should().Contain(hostileApp, "the confirm text should name the exact app being revoked, unbroken by the apostrophe");

        (await page.Locator(".ok-banner").InnerTextAsync()).Should().Contain("Revoked");
    }

    /// <summary>
    /// Minting for an app name that already has a token silently
    /// rotated it before this fix - the dashboard now asks for
    /// confirmation first, and declining must leave the existing token
    /// working.
    /// </summary>
    [Fact]
    public async Task MintingOverAnExistingApp_AsksForConfirmation_AndDecliningKeepsTheOldTokenWorking()
    {
        await using var ctx = await NewSetUpContextAsync();
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        await page.FillAsync("input[name='App']", "chat");
        await page.ClickAsync("#mint-token-btn");
        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });
        var firstToken = (await page.Locator("#minted-token").InnerTextAsync()).Trim();

        // Attempt to mint "chat" again and decline the confirmation.
        await page.GotoAsync($"{_app.BaseUrl}/Tokens");
        var dialogs = new List<(string Type, string Message)>();
        EventHandler<IDialog> dismissHandler = async (_, dlg) =>
        {
            dialogs.Add((dlg.Type, dlg.Message));
            await dlg.DismissAsync();
        };
        page.Dialog += dismissHandler;
        await page.FillAsync("input[name='App']", "chat");
        await page.ClickAsync("#mint-token-btn");
        // Give the dialog a moment to fire and be dismissed; the form
        // must not have submitted (still on /Tokens, no second mint).
        await page.WaitForTimeoutAsync(300);
        page.Dialog -= dismissHandler;

        dialogs.Should().HaveCount(1, "minting over an existing app must ask for confirmation exactly once");
        dialogs[0].Type.Should().Be("confirm");
        dialogs[0].Message.Should().Contain("chat");

        // The original token must still work - declining must not have
        // rotated it.
        var stillWorksResp = await ctx.APIRequest.PostAsync($"{_app.BaseUrl}/AppApi/outbound", new APIRequestContextOptions
        {
            DataObject = new { app = "chat", destCallsign = "G7XYZ", payload = Convert.ToBase64String("hi"u8.ToArray()) },
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["Authorization"] = $"Bearer {firstToken}",
            },
        });
        stillWorksResp.Ok.Should().BeTrue("declining the rotation confirmation must leave the existing token working");

        // Now accept the confirmation and verify it actually rotates.
        page.Dialog += async (_, dlg) => await dlg.AcceptAsync();
        await page.ClickAsync("#mint-token-btn");
        await page.WaitForSelectorAsync("#minted-token", new PageWaitForSelectorOptions { Timeout = 5_000 });
        var secondToken = (await page.Locator("#minted-token").InnerTextAsync()).Trim();
        secondToken.Should().NotBe(firstToken);

        var oldTokenNowFailsResp = await ctx.APIRequest.PostAsync($"{_app.BaseUrl}/AppApi/outbound", new APIRequestContextOptions
        {
            DataObject = new { app = "chat", destCallsign = "G7XYZ", payload = Convert.ToBase64String("hi"u8.ToArray()) },
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["Authorization"] = $"Bearer {firstToken}",
            },
        });
        oldTokenNowFailsResp.Ok.Should().BeFalse("accepting the rotation confirmation must invalidate the prior token");
    }
}
