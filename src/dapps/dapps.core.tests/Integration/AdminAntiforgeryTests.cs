using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #320: a browser holding the admin cookie must not change state with a
/// bare POST (no body, so no JSON content type to stop it). Such posts
/// want the antiforgery token the dashboard's script sends; JSON posts
/// (the documented scripting recipe) and the bearer-token app API don't.
/// </summary>
public sealed class AdminAntiforgeryTests
{
    private const string Password = "hunter2hunter2";

    [Fact]
    public async Task BareAdminPost_WithTheCookieButNoToken_IsRefused_AndChangesNothing()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);

        var refused = await admin.PostAsync("/TxControl/stop", content: null, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TxLocalAllowedAsync(admin)).Should().BeTrue(because: "the refused request must not have stopped transmit");
    }

    [Fact]
    public async Task AdminPost_WithTheDashboardsToken_Works()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);
        await admin.UseAntiforgeryTokenAsync(ct: Ct);

        var resp = await admin.PostAsync("/TxControl/stop", content: null, Ct);

        // 302: it sends the browser back to the page it came from.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
        (await TxLocalAllowedAsync(admin)).Should().BeFalse();
    }

    [Fact]
    public async Task AdminFormPost_WithoutToken_IsRefused()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);

        var resp = await admin.PostAsync("/Probes/run", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdminJsonPost_WithoutToken_StillWorks_ForScripts()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);

        var resp = await admin.PostAsJsonAsync("/AppTokens", new { app = "hello" }, Ct);

        resp.IsSuccessStatusCode.Should().BeTrue(because: $"got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task AppApi_DoesNotNeedAnAntiforgeryToken()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var anyone = NewClient(app);

        var resp = await anyone.PostAsJsonAsync("/AppApi/outbound",
            new { App = "chat", DestCallsign = "G0ABC-1", Payload = new byte[] { 1, 2, 3 }, Ttl = 600 }, Ct);

        // 401 while app tokens are required, accepted when not; never an
        // antiforgery refusal.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.Created);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient NewClient(GatewayPathBaseTests.DappsSubprocess app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(app.BaseUrl),
        };

    private static async Task<HttpClient> SignedInAsync(GatewayPathBaseTests.DappsSubprocess app)
    {
        var http = NewClient(app);
        var resp = await http.PostAsync("/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = Password,
            ["confirm"] = Password,
        }), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        return http;
    }

    private static async Task<bool> TxLocalAllowedAsync(HttpClient http)
    {
        var json = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/TxControl/status", Ct);
        return json.GetProperty("localAllowed").GetBoolean();
    }
}
