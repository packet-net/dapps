using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #389: the Drop button on the Messages page's local inbox posts to
/// <c>/Messages?handler=Drop</c>. It needs the admin cookie and an
/// antiforgery token, answers 404 for an id not in the inbox and 409
/// for one its app has acked, and a dropped message is gone from the app
/// API's inbox and listed as dropped by the operator.
///
/// Boots dapps.core as a real subprocess (as
/// <see cref="SetupAndMqttOriginSecurityTests"/> does), because the
/// behaviour lives in the auth middleware and the antiforgery filter.
/// </summary>
public sealed class InboxDropEndpointTests(InboxDropEndpointTests.Daemon daemon)
    : IClassFixture<InboxDropEndpointTests.Daemon>
{
    private const string Password = "hunter2hunter2";
    private const string Callsign = "M0LTE-7";

    [Fact]
    public async Task Drop_WithoutTheAdminCookie_RedirectsToLogin_AndKeepsTheMessage()
    {
        var id = await daemon.SubmitLocalAsync("nocookie");
        using var stranger = daemon.NewClient();

        var resp = await stranger.PostAsync("/Messages?handler=Drop", DropForm(id, token: null), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.AbsolutePath.Should().Be("/Login");
        (await daemon.InboxIdsAsync()).Should().Contain(id);
    }

    [Fact]
    public async Task Drop_WithoutAntiforgeryToken_IsRefused_AndKeepsTheMessage()
    {
        var id = await daemon.SubmitLocalAsync("notoken");

        var resp = await daemon.Admin.PostAsync("/Messages?handler=Drop", DropForm(id, token: null), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await daemon.InboxIdsAsync()).Should().Contain(id);
    }

    [Fact]
    public async Task Drop_UnknownId_IsNotFound()
    {
        var token = await daemon.TokenAsync();

        var resp = await daemon.Admin.PostAsync("/Messages?handler=Drop", DropForm("0000000", token), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Drop_AMessageTheAppAcked_IsAConflict()
    {
        var id = await daemon.SubmitLocalAsync("acked");
        (await daemon.Admin.PostAsync($"/AppApi/inbound/chat/{id}/ack", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var token = await daemon.TokenAsync();

        var resp = await daemon.Admin.PostAsync("/Messages?handler=Drop", DropForm(id, token), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await daemon.DroppedAsync()).Should().NotContain(d => d.Id == id);
    }

    [Fact]
    public async Task Drop_AnUnackedMessage_RemovesItFromTheAppsInbox_AndListsItAsDropped()
    {
        var id = await daemon.SubmitLocalAsync("drop-me");

        // The dashboard's time left agrees with the app API's ttl.
        var row = (await daemon.SnapshotInboxAsync()).Single(r => r["id"]!.GetValue<string>() == id);
        var left = row["ttlRemaining"]!.GetValue<int>();
        left.Should().BeInRange(3500, 3600);
        var apiTtl = (await daemon.AppInboxAsync()).Single(m => m["id"]!.GetValue<string>() == id)["ttl"]!.GetValue<int>();
        Math.Abs(apiTtl - left).Should().BeLessThanOrEqualTo(5);

        var token = await daemon.TokenAsync();
        var resp = await daemon.Admin.PostAsync("/Messages?handler=Drop", DropForm(id, token), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await daemon.InboxIdsAsync()).Should().NotContain(id);
        (await daemon.DroppedAsync()).Should().Contain(d => d.Id == id && d.Reason == "dropped by operator");

        // Again: gone, so 404.
        (await daemon.Admin.PostAsync("/Messages?handler=Drop", DropForm(id, token), Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FormUrlEncodedContent DropForm(string id, string? token)
    {
        var fields = new Dictionary<string, string> { ["id"] = id };
        if (token is not null) fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>One daemon for the class: set up, app tokens not
    /// required, with a logged-in admin client.</summary>
    public sealed class Daemon : IAsyncLifetime
    {
        private GatewayPathBaseTests.DappsSubprocess app = null!;

        public HttpClient Admin { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
            Admin = NewClient();
            var pw = await Admin.PostAsync("/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["password"] = Password,
                ["confirm"] = Password,
            }));
            pw.StatusCode.Should().Be(HttpStatusCode.Found);
            var setupToken = await TokenFromAsync("/Setup");
            var bearer = await Admin.PostAsync("/Setup?handler=Bearer", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = setupToken,
                ["callsign"] = Callsign,
                ["nodeHost"] = "localhost",
                ["nodeBearer"] = "agw",
                ["port"] = "8000",
                ["rhpUser"] = "",
                ["rhpPass"] = "",
            }));
            bearer.StatusCode.Should().Be(HttpStatusCode.Found);
            var auth = await Admin.PostAsJsonAsync("/Config", new JsonObject { ["AuthRequired"] = false });
            auth.IsSuccessStatusCode.Should().BeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            Admin?.Dispose();
            if (app is not null) await app.DisposeAsync();
        }

        public HttpClient NewClient() =>
            new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
            {
                BaseAddress = new Uri(app.BaseUrl),
            };

        public Task<string> TokenAsync() => TokenFromAsync("/Messages?tab=inbox");

        private async Task<string> TokenFromAsync(string path)
        {
            var resp = await Admin.GetAsync(path, Ct);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"GET {path} should render");
            var body = await resp.Content.ReadAsStringAsync(Ct);
            var token = Regex.Match(body, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
            token.Should().NotBeEmpty(because: $"GET {path} should carry an antiforgery token");
            return token;
        }

        /// <summary>An app here sends a message to an app on this same
        /// node: it lands in the local inbox (#288).</summary>
        public async Task<string> SubmitLocalAsync(string body)
        {
            var resp = await Admin.PostAsJsonAsync("/AppApi/outbound", new
            {
                App = "chat",
                DestCallsign = Callsign,
                Payload = System.Text.Encoding.UTF8.GetBytes(body + "-" + Guid.NewGuid().ToString("N")),
                Ttl = 3600,
            }, Ct);
            resp.IsSuccessStatusCode.Should().BeTrue(because: $"POST /AppApi/outbound returned {(int)resp.StatusCode}");
            var id = (await resp.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<string>();
            // Wait for the inbox to take it.
            for (var i = 0; i < 50 && !(await InboxIdsAsync()).Contains(id); i++) await Task.Delay(100, Ct);
            return id;
        }

        public async Task<List<JsonNode>> AppInboxAsync()
        {
            var arr = await Admin.GetFromJsonAsync<JsonArray>("/AppApi/inbound/chat", Ct);
            return arr!.Select(n => n!).ToList();
        }

        public async Task<List<string>> InboxIdsAsync()
            => (await AppInboxAsync()).Select(m => m["id"]!.GetValue<string>()).ToList();

        public async Task<List<JsonNode>> SnapshotInboxAsync()
        {
            var snap = await Admin.GetFromJsonAsync<JsonObject>("/Operational?full=true", Ct);
            return snap!["tables"]!["localInbox"]!.AsArray().Select(n => n!).ToList();
        }

        public async Task<List<(string Id, string Reason)>> DroppedAsync()
        {
            var snap = await Admin.GetFromJsonAsync<JsonObject>("/Operational?full=true", Ct);
            return snap!["tables"]!["dropped"]!.AsArray()
                .Select(n => (n!["id"]!.GetValue<string>(), n["reason"]!.GetValue<string>())).ToList();
        }
    }
}
