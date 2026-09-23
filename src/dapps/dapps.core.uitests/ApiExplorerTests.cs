using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// The generated OpenAPI document at <c>/openapi/v1.json</c> and the
/// Scalar explorer at <c>/scalar</c>. Both came back with the move to
/// .NET 10 (the .NET 8 rollback had dropped them) and both sit behind
/// <c>AdminAuthMiddleware</c> like the rest of the dashboard, so these
/// run against the real booted daemon with a real admin cookie.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class ApiExplorerTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task OpenApiDocument_LoggedIn_DescribesTheRestSurface()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);

        var resp = await ctx.APIRequest.GetAsync($"{app.BaseUrl}/openapi/v1.json");

        resp.Status.Should().Be(200);
        using var doc = JsonDocument.Parse(await resp.TextAsync());
        doc.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().Contain(p => p.StartsWith("/AppApi", StringComparison.OrdinalIgnoreCase),
            "the app-facing REST surface is the main reason the document exists");
        paths.Should().Contain(p => p.StartsWith("/Operational", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scalar_LoggedIn_RendersTheExplorer()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        var response = await page.GotoAsync($"{app.BaseUrl}/scalar");

        response.Should().NotBeNull();
        response!.Status.Should().Be(200);
        page.Url.Should().Contain("/scalar", "a logged-in operator is not bounced to /Login");
        (await page.ContentAsync()).Should().ContainEquivalentOf("scalar",
            "the page boots the Scalar viewer against the generated document");
    }

    [Fact]
    public async Task OpenApiDocument_WithoutLogin_IsChallenged()
    {
        await using var ctx = await pw.Browser.NewContextAsync();

        var resp = await ctx.APIRequest.GetAsync($"{app.BaseUrl}/openapi/v1.json",
            new APIRequestContextOptions { MaxRedirects = 0 });

        resp.Status.Should().BeOneOf([302, 401],
            "the API description is admin-only, same posture as /Config; /AppApi itself stays token-gated");
    }
}
