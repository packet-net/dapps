using System.Net;

namespace dapps.core.uitests;

/// <summary>
/// Boots dapps.core via the underlying <see cref="WebAppFixture"/>, then
/// drives the <c>/Setup</c> wizard's POST handlers over plain HTTP so
/// every UI test in the &quot;LoggedInUI&quot; collection starts with a
/// node that already has an admin password and a real callsign. That's
/// what subsequent <c>/Login</c> form-fills and authed-API calls assume.
///
/// We POST <c>/Setup?handler=Password</c> and <c>/Setup?handler=Bearer</c>
/// rather than calling <c>/Config/admin-password</c> + <c>/Config</c>
/// directly because those endpoints are gated by <c>AdminAuthMiddleware</c>
/// once the password is set; the Setup page is allow-listed during
/// first-run, and the password POST signs the cookie in for the bearer
/// step. <see cref="HttpClientHandler"/> with a <see cref="CookieContainer"/>
/// carries the cookie between the two posts.
/// </summary>
public sealed class LoggedInWebAppFixture : IAsyncLifetime
{
    public const string AdminPassword = "ui-test-password-1234";
    public const string Callsign = "M0LTE-7";

    private readonly WebAppFixture _app = new();

    public string BaseUrl => _app.BaseUrl;

    /// <summary>See <see cref="WebAppFixture.DatabasePath"/>.</summary>
    public string DatabasePath => _app.DatabasePath;

    public async ValueTask InitializeAsync()
    {
        await _app.InitializeAsync();

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        // Step 1: password. This also signs the cookie in.
        var pwResp = await http.PostAsync("/Setup?handler=Password",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["password"] = AdminPassword,
                ["confirm"] = AdminPassword,
            }));
        if (!pwResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"/Setup?handler=Password failed: {(int)pwResp.StatusCode} {await pwResp.Content.ReadAsStringAsync()}");
        }

        // Step 2: bearer. Cookie is in the container, so the bearer
        // handler's auth check passes.
        var bearerResp = await http.PostAsync("/Setup?handler=Bearer",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["callsign"] = Callsign,
                ["nodeHost"] = "localhost",
                ["nodeBearer"] = "agw",
                ["port"] = "8000",
                ["rhpUser"] = "",
                ["rhpPass"] = "",
            }));
        if (!bearerResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"/Setup?handler=Bearer failed: {(int)bearerResp.StatusCode} {await bearerResp.Content.ReadAsStringAsync()}");
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>
/// Shared fixtures for the post-setup UI tests. One dapps boot, one
/// chromium for the whole "LoggedInUI" collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LoggedInUiCollection
    : ICollectionFixture<LoggedInWebAppFixture>, ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "LoggedInUI";
}
