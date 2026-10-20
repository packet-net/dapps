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
    public async Task Resume_WithoutToken_IsRefused_AndChangesNothing()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);
        (await admin.PostAsync("/TxControl/stop", content: null, Ct)).StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        (await TxLocalAllowedAsync(admin)).Should().BeFalse();

        var refused = await admin.PostAsync("/TxControl/resume", content: null, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TxLocalAllowedAsync(admin)).Should().BeFalse(because: "the refused resume must not have turned transmit back on");
    }

    [Fact]
    public async Task Resume_WithTheDashboardsToken_Works()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);
        await admin.PostAsync("/TxControl/stop", content: null, Ct);
        await admin.UseAntiforgeryTokenAsync(ct: Ct);

        var resp = await admin.PostAsync("/TxControl/resume", content: null, Ct);

        // 302: it sends the browser back to the page it came from.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
        (await TxLocalAllowedAsync(admin)).Should().BeTrue();
    }

    [Fact]
    public async Task Stop_IsNeverRefused_BodylessOrAsAFormWithNoToken()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var admin = await SignedInAsync(app);

        var bodyless = await admin.PostAsync("/TxControl/stop", content: null, Ct);
        bodyless.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
        (await TxLocalAllowedAsync(admin)).Should().BeFalse();

        // Resume (with a token), then stop again as a stale page would: a form post, no token.
        await admin.UseAntiforgeryTokenAsync(ct: Ct);
        (await admin.PostAsync("/TxControl/resume", content: null, Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
        admin.DefaultRequestHeaders.Remove(AntiforgeryTestExtensions.HeaderName);
        var form = await admin.PostAsync("/TxControl/stop", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        form.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
        (await TxLocalAllowedAsync(admin)).Should().BeFalse();
    }

    [Fact]
    public async Task TopbarResumeForm_WorksThroughTheGatewayPathBase()
    {
        const string prefix = "/apps/dapps";
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        // Behind the gateway the cookies are scoped to the prefix, which a
        // CookieContainer keyed on the stripped URL would never replay, so
        // the jar is kept by hand, as a browser would for the public URL.
        var jar = new Dictionary<string, string>();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(app.BaseUrl),
        };

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            using var req = new HttpRequestMessage(method, path) { Content = content };
            req.Headers.Add("X-Forwarded-Prefix", prefix);
            if (jar.Count > 0) req.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}")));
            var resp = await http.SendAsync(req, Ct);
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var c in cookies)
                {
                    var nv = c.Split(';', 2)[0].Split('=', 2);
                    if (nv.Length == 2) jar[nv[0]] = nv[1];
                }
            }
            return resp;
        }

        (await SendAsync(HttpMethod.Post, "/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = Password,
            ["confirm"] = Password,
        }))).StatusCode.Should().Be(HttpStatusCode.Found);
        (await SendAsync(HttpMethod.Post, "/TxControl/stop")).StatusCode.Should().Be(HttpStatusCode.Found);

        // The page now shows the banner's Resume form; post it as the browser would.
        var page = await (await SendAsync(HttpMethod.Get, "/Settings")).Content.ReadAsStringAsync(Ct);
        var formHtml = System.Text.RegularExpressions.Regex.Match(page,
            "<form method=\"post\" action=\"" + prefix + "/TxControl/resume\"[^>]*>(.*?)</form>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        formHtml.Success.Should().BeTrue(because: "the Resume form should render with the gateway prefix");
        var token = System.Text.RegularExpressions.Regex.Match(formHtml.Groups[1].Value,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty(because: "the Resume form must carry a form-field token");

        var resume = await SendAsync(HttpMethod.Post, "/TxControl/resume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token),
        }));
        resume.StatusCode.Should().Be(HttpStatusCode.Found);

        var status = await (await SendAsync(HttpMethod.Get, "/TxControl/status")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        status.GetProperty("localAllowed").GetBoolean().Should().BeTrue();
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
