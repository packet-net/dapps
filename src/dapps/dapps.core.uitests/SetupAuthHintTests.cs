using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// The Setup wizard's bearer step shows a token-required hint when
/// <c>AuthRequired</c> is true - which is now the default for a brand
/// new install (#316). Every other UI test uses <see cref="WebAppFixture"/>'s
/// public, parameterless constructor (<c>AuthRequired=false</c>), so
/// this is the one place that boots a daemon with the real new-install
/// default and checks the hint actually renders and links somewhere real.
///
/// Own <see cref="WebAppFixture"/> instance (own subprocess), like
/// <see cref="JourneyTests"/>, but reuses the collection's shared
/// <see cref="PlaywrightFixture"/> browser.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupAuthHintTests(PlaywrightFixture pw) : IAsyncLifetime
{
    private readonly WebAppFixture _app = new(authRequired: true);
    private const string AdminPassword = "ui-test-password-auth-hint";

    public async ValueTask InitializeAsync() => await _app.InitializeAsync();
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task SetupBearerStep_WithAuthRequired_ShowsTokenHint()
    {
        await using var ctx = await pw.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();

        await page.GotoAsync(_app.BaseUrl);
        page.Url.Should().EndWith("/Setup");
        await page.FillAsync("input[name='password']", AdminPassword);
        await page.FillAsync("input[name='confirm']", AdminPassword);
        await page.ClickAsync("button[type='submit']");

        // Bearer step: the hint only renders when Model.AuthRequired is
        // true, which it now is by default on this fresh database.
        await page.WaitForSelectorAsync("input[name='callsign']",
            new PageWaitForSelectorOptions { Timeout = 10_000 });

        var bodyText = await page.Locator("body").InnerTextAsync();
        bodyText.Should().Contain("token", "the bearer step should warn that apps need a token on this install");

        // Points at the dashboard's own App tokens page first, not an
        // unrunnable bare curl command.
        var tokensLink = page.Locator("a:has-text('App tokens')");
        (await tokensLink.CountAsync()).Should().Be(1, "the hint should link to the dashboard's App tokens page");
        var tokensHref = await tokensLink.GetAttributeAsync("href");
        tokensHref.Should().Contain("/Tokens");

        // The scripted recipe is still offered as the alternative.
        var scriptedLink = page.Locator("a:has-text('scripted recipe')");
        (await scriptedLink.CountAsync()).Should().Be(1, "the curl recipe should remain available as an alternative");
        var scriptedHref = await scriptedLink.GetAttributeAsync("href");
        scriptedHref.Should().Contain("#authentication");
    }
}
