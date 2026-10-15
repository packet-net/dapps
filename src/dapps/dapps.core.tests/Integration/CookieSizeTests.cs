using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #248: browsers send a host's cookies to every port on that host, so
/// another web app on the node receives DAPPS's cookies too. XRouter's
/// dashboard rejects request header lines over about 512 bytes
/// (packethacking/xrouter-container#30), so a signed-in operator's
/// whole <c>Cookie:</c> line has to stay under that.
/// </summary>
public sealed class CookieSizeTests
{
    // XRouter's limit is about 512; keep real headroom below it so a
    // small cookie from some other app on the host doesn't tip it over.
    private const int CookieLineBudget = 450;

    [Fact]
    public async Task SignedIn_CookieHeaderLine_FitsUnderXrouterLimit()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        var cookies = new Dictionary<string, string>();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.BaseUrl) };
        var ct = TestContext.Current.CancellationToken;

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            using var req = new HttpRequestMessage(method, path) { Content = content };
            if (cookies.Count > 0)
            {
                req.Headers.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            var resp = await client.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var nameValue = sc.Split(';', 2)[0].Split('=', 2);
                    if (nameValue.Length != 2) continue;
                    if (nameValue[1].Length == 0) cookies.Remove(nameValue[0]);
                    else cookies[nameValue[0]] = nameValue[1];
                }
            }
            return resp;
        }

        // The setup page renders a form, so it issues the antiforgery
        // cookie; setting the password signs the operator in.
        (await SendAsync(HttpMethod.Get, "/Setup")).StatusCode.Should().Be(HttpStatusCode.OK);
        var signIn = await SendAsync(HttpMethod.Post, "/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = "hunter2hunter2",
            ["confirm"] = "hunter2hunter2",
        }));
        signIn.StatusCode.Should().Be(HttpStatusCode.Found);
        // Another form page while signed in, so both cookies are in play.
        (await SendAsync(HttpMethod.Get, "/Setup")).StatusCode.Should().Be(HttpStatusCode.OK);

        cookies.Keys.Should().BeEquivalentTo(["dapps.admin", "dapps.af"]);
        var line = "Cookie: " + string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        line.Length.Should().BeLessThanOrEqualTo(CookieLineBudget, because: $"the line was \"{line}\"");
    }

    [Fact]
    public async Task OldDefaultAntiforgeryCookie_IsDeleted()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.BaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Get, "/Setup");
        req.Headers.Add("Cookie", ".AspNetCore.Antiforgery.w8rLWGkCd-E=old");

        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.Headers.GetValues("Set-Cookie").Should().Contain(sc =>
            Regex.IsMatch(sc, @"^\.AspNetCore\.Antiforgery\.w8rLWGkCd-E=;.*expires=Thu, 01 Jan 1970", RegexOptions.IgnoreCase)
            && sc.Contains("path=/", StringComparison.OrdinalIgnoreCase));
    }
}
