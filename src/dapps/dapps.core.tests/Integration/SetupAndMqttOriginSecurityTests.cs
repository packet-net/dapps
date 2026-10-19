using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The parts of #316 and #314 that are plain bugs: the /Setup bearer
/// step must need the admin cookie to view, an antiforgery token to
/// post, and must refuse once setup is done; and the MQTT WebSocket
/// at /mqtt must refuse a cross-site browser page while the broker
/// needs no credentials.
///
/// Boots dapps.core as a real subprocess (the same harness as
/// <see cref="GatewayPathBaseTests"/>) because the behaviour lives in
/// the middleware order, the antiforgery filter and the WebSocket
/// upgrade, none of which a unit test reaches.
/// </summary>
public sealed class SetupAndMqttOriginSecurityTests
{
    private const string Password = "hunter2hunter2";

    [Fact]
    public async Task BearerStep_WithoutTheAdminCookie_RedirectsToLogin_AndShowsNothing()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await SetPasswordAsync(admin);

        // A second browser with no cookie: the password is set but the
        // callsign is still the placeholder, so the wizard is on its
        // bearer step. That step must not render to a stranger.
        using var stranger = NewClient(app);
        var resp = await stranger.GetAsync("/Setup", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().StartWith("/Login");
    }

    [Fact]
    public async Task BearerPost_WithoutAntiforgeryToken_IsRefused()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await SetPasswordAsync(admin);

        var resp = await admin.PostAsync("/Setup?handler=Bearer", BearerForm("M0LTE-7", token: null), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CallsignAsync(admin)).Should().NotBe("M0LTE-7");
    }

    [Fact]
    public async Task BearerPost_WithToken_CompletesSetup()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await SetPasswordAsync(admin);

        var token = await TokenFromAsync(admin, "/Setup");
        var resp = await admin.PostAsync("/Setup?handler=Bearer", BearerForm("M0LTE-7", token), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Be("/");
        (await CallsignAsync(admin)).Should().Be("M0LTE-7");
    }

    [Fact]
    public async Task BearerPost_OnceSetupIsDone_ChangesNothing()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await CompleteSetupAsync(admin, "M0LTE-7");

        // Even with the cookie and a valid token, the wizard is a
        // one-shot: changing a set-up node is the Settings page's job.
        var token = await TokenFromAsync(admin, "/Tokens");
        var resp = await admin.PostAsync("/Setup?handler=Bearer", BearerForm("G0EVIL", token), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Be("/");
        (await CallsignAsync(admin)).Should().Be("M0LTE-7");
    }

    [Fact]
    public async Task MqttWebSocket_OpenMode_RefusesACrossSiteOrigin()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await CompleteSetupAsync(admin, "M0LTE-7");
        await SetAuthRequiredAsync(admin, false);

        var status = await UpgradeAsync(app, origin: "http://evil.example");

        status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MqttWebSocket_OpenMode_AcceptsSameOriginAndNoOrigin()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await CompleteSetupAsync(admin, "M0LTE-7");
        await SetAuthRequiredAsync(admin, false);

        var origin = new Uri(app.BaseUrl).GetLeftPart(UriPartial.Authority);
        (await UpgradeAsync(app, origin)).Should().Be(HttpStatusCode.SwitchingProtocols);
        (await UpgradeAsync(app, origin: null)).Should().Be(HttpStatusCode.SwitchingProtocols);
    }

    [Fact]
    public async Task MqttWebSocket_TokensRequired_AcceptsACrossSiteOrigin()
    {
        // With app tokens required, a page still has to present a valid
        // token in the MQTT CONNECT, so a browser app hosted anywhere
        // (or opened from disk, like the examples) keeps working.
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = NewClient(app);
        await CompleteSetupAsync(admin, "M0LTE-7");
        await SetAuthRequiredAsync(admin, true);

        (await UpgradeAsync(app, origin: "http://elsewhere.example")).Should().Be(HttpStatusCode.SwitchingProtocols);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient NewClient(GatewayPathBaseTests.DappsSubprocess app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(app.BaseUrl),
        };

    private static async Task SetPasswordAsync(HttpClient http)
    {
        var resp = await http.PostAsync("/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = Password,
            ["confirm"] = Password,
        }), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    private static async Task CompleteSetupAsync(HttpClient http, string callsign)
    {
        await SetPasswordAsync(http);
        var token = await TokenFromAsync(http, "/Setup");
        var resp = await http.PostAsync("/Setup?handler=Bearer", BearerForm(callsign, token), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        (await CallsignAsync(http)).Should().Be(callsign);
    }

    private static async Task<string> TokenFromAsync(HttpClient http, string path)
    {
        var resp = await http.GetAsync(path, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"GET {path} should render a form");
        var body = await resp.Content.ReadAsStringAsync(Ct);
        var token = Regex.Match(body, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty(because: $"GET {path} should carry an antiforgery token");
        return token;
    }

    private static FormUrlEncodedContent BearerForm(string callsign, string? token)
    {
        var fields = new Dictionary<string, string>
        {
            ["callsign"] = callsign,
            ["nodeHost"] = "localhost",
            ["nodeBearer"] = "agw",
            ["port"] = "8000",
            ["rhpUser"] = "",
            ["rhpPass"] = "",
        };
        if (token is not null) fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<string?> CallsignAsync(HttpClient http)
    {
        var resp = await http.GetAsync("/Config", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return (json?["callsign"] ?? json?["Callsign"])?.GetValue<string>();
    }

    private static async Task SetAuthRequiredAsync(HttpClient http, bool value)
    {
        var resp = await http.PostAsJsonAsync("/Config", new JsonObject { ["AuthRequired"] = value }, Ct);
        resp.IsSuccessStatusCode.Should().BeTrue(because: $"POST /Config AuthRequired={value} returned {(int)resp.StatusCode}");
    }

    /// <summary>Attempts the /mqtt WebSocket upgrade the way a browser
    /// would (sub-protocol <c>mqtt</c>, with or without an Origin) and
    /// returns the HTTP status the server answered with.</summary>
    private static async Task<HttpStatusCode> UpgradeAsync(GatewayPathBaseTests.DappsSubprocess app, string? origin)
    {
        var uri = new UriBuilder(app.BaseUrl) { Scheme = "ws", Path = "/mqtt" }.Uri;
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("mqtt");
        ws.Options.CollectHttpResponseDetails = true;
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        try
        {
            await ws.ConnectAsync(uri, Ct);
        }
        catch (WebSocketException)
        {
            return ws.HttpStatusCode;
        }
        var status = ws.HttpStatusCode;
        try { ws.Abort(); } catch { /* best-effort */ }
        return status;
    }
}
