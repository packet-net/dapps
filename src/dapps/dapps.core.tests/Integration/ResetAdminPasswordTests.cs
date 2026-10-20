using System.Diagnostics;
using System.Net;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #385: <c>dapps reset-admin-password</c> run on the node while the
/// daemon is up, against the daemon's own database: it sets the new
/// password, signs out the browser that was logged in, and doesn't
/// start a second daemon.
/// </summary>
public sealed class ResetAdminPasswordTests
{
    [Fact]
    public async Task ResetWhileRunning_SignsOutTheOldLogin_AndTheNewPasswordWorks()
    {
        await using var app = await GatewayPathBaseTests.DappsSubprocess.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var browser = new CookieBrowser(app.BaseUrl);

        (await browser.SendAsync(HttpMethod.Get, "/Setup", ct: ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.SendAsync(HttpMethod.Post, "/Setup?handler=Password", Form(("password", "old-password-1"), ("confirm", "old-password-1")), ct))
            .StatusCode.Should().Be(HttpStatusCode.Found);
        (await browser.SendAsync(HttpMethod.Get, "/Config", ct: ct)).StatusCode.Should().Be(HttpStatusCode.OK, "signed in by /Setup");
        var oldCookie = browser.Cookies["dapps.admin"];

        // The login cookie carries its issue time to the second; let a
        // second pass so the reset is unambiguously later.
        await Task.Delay(TimeSpan.FromSeconds(1.1), ct);

        var (exit, output) = await RunCliAsync(app.WorkDir, "new-password-2\n", ct);
        exit.Should().Be(0, output);
        output.Should().NotContain("Now listening", "the reset must not start the daemon");

        // The old login no longer gets in (an API call is told 401
        // rather than redirected to /Login), and its cookie is cleared.
        var stale = await browser.SendAsync(HttpMethod.Get, "/Config", ct: ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browser.Cookies.Should().NotContainKey("dapps.admin");

        // Replaying the old cookie by hand doesn't get in either.
        var replay = new CookieBrowser(app.BaseUrl);
        replay.Cookies["dapps.admin"] = oldCookie;
        (await replay.SendAsync(HttpMethod.Get, "/Config", ct: ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Old password refused, new one accepted.
        (await browser.SendAsync(HttpMethod.Post, "/Login", Form(("password", "old-password-1")), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a wrong password re-renders the form");
        (await browser.SendAsync(HttpMethod.Post, "/Login", Form(("password", "new-password-2")), ct))
            .StatusCode.Should().Be(HttpStatusCode.Found);
        (await browser.SendAsync(HttpMethod.Get, "/Config", ct: ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static async Task<(int Exit, string Output)> RunCliAsync(string workDir, string stdin, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo =
            {
                FileName = "dotnet",
                Arguments = $"\"{GatewayPathBaseTests.DappsSubprocess.LocateDappsDll()}\" reset-admin-password",
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        ChildEnvironment.Apply(process.StartInfo.Environment, new Dictionary<string, string>
        {
            ["DOTNET_ROLL_FORWARD"] = "LatestMajor",
        });
        process.Start();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>A browser's cookie jar, by hand, so the test can read
    /// and replay the admin cookie.</summary>
    private sealed class CookieBrowser(string baseUrl)
    {
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(baseUrl),
        };

        public Dictionary<string, string> Cookies { get; } = new();

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(method, path) { Content = content };
            if (Cookies.Count > 0)
            {
                req.Headers.Add("Cookie", string.Join("; ", Cookies.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            var resp = await client.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var nameValue = sc.Split(';', 2)[0].Split('=', 2);
                    if (nameValue.Length != 2) continue;
                    if (nameValue[1].Length == 0) Cookies.Remove(nameValue[0]);
                    else Cookies[nameValue[0]] = nameValue[1];
                }
            }
            return resp;
        }
    }
}
