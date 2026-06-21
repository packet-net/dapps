using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// Pins the pdn app-gateway contract (packet.net docs/app-gateway.md):
/// when a request carries <c>X-Forwarded-Prefix: /apps/dapps</c> the
/// dashboard must render every URL surface - link hrefs, form
/// actions, JS fetch targets, and redirect <c>Location</c> headers -
/// prefixed with the mount point, because the gateway forwards the
/// path with the prefix stripped and does NOT rewrite the response.
/// Without the header (standalone direct access) the same surfaces
/// must be byte-identical to what they were before PathBase support
/// landed: root-relative, no prefix anywhere.
///
/// Boots dapps.core as a real subprocess (same pattern as
/// dapps.core.uitests' WebAppFixture - WebApplicationFactory can't
/// exercise the real Kestrel + middleware-order path) on a fresh temp
/// data dir, then walks the first-run journey: the fresh-install
/// redirect, the /Setup wizard's two steps (both POST redirects), and
/// the authenticated dashboard + messages pages.
/// </summary>
public sealed class GatewayPathBaseTests
{
    private const string Prefix = "/apps/dapps";

    [Fact]
    public async Task Standalone_NoForwardedPrefix_UrlSurfacesStayRootRelative()
    {
        await using var app = await DappsSubprocess.StartAsync();
        var surfaces = await WalkFirstRunJourneyAsync(app, forwardedPrefix: null, expectedPrefix: "");

        // Nothing anywhere in the rendered output may carry the mount
        // point when the header is absent - standalone output must be
        // unchanged.
        foreach (var (name, body) in surfaces)
        {
            body.Should().NotContain(Prefix, because: $"{name} must render unprefixed in standalone mode");
        }
    }

    [Fact]
    public async Task BehindGateway_ForwardedPrefix_UrlSurfacesCarryThePrefix()
    {
        await using var app = await DappsSubprocess.StartAsync();
        await WalkFirstRunJourneyAsync(app, forwardedPrefix: Prefix, expectedPrefix: Prefix);
    }

    /// <summary>
    /// Drives the first-run journey, asserting each URL surface
    /// carries <paramref name="expectedPrefix"/>. Mimics the gateway:
    /// the request path is always the stripped one; only the header
    /// differs. Returns the captured bodies so the standalone test can
    /// additionally assert the prefix appears nowhere.
    /// </summary>
    private static async Task<List<(string Name, string Body)>> WalkFirstRunJourneyAsync(
        DappsSubprocess app, string? forwardedPrefix, string expectedPrefix)
    {
        var surfaces = new List<(string, string)>();
        // Cookies are managed by hand rather than via CookieContainer:
        // behind the gateway the auth cookie is issued with
        // Path=/apps/dapps (cookie auth scopes it to the request
        // PathBase), while the forwarded request path is the stripped
        // one - a CookieContainer keyed on the request URL would
        // never replay it. A real browser stores it against the
        // public prefixed URL and the gateway forwards the Cookie
        // header verbatim; this jar emulates that.
        var cookies = new Dictionary<string, string>();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.BaseUrl) };
        if (forwardedPrefix is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", forwardedPrefix);
        }
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
                    if (nameValue.Length == 2) cookies[nameValue[0]] = nameValue[1];
                }
            }
            return resp;
        }

        // 1. Fresh install, no admin password: AdminAuthMiddleware
        //    bounces everything to /Setup. The Location must stay
        //    inside the mount.
        var root = await SendAsync(HttpMethod.Get, "/");
        root.StatusCode.Should().Be(HttpStatusCode.Found);
        root.Headers.Location!.OriginalString.Should().Be($"{expectedPrefix}/Setup");

        // 2. The password step's form action (FormTagHelper +
        //    PathBase).
        var setup = await SendAsync(HttpMethod.Get, "/Setup");
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var setupBody = await setup.Content.ReadAsStringAsync(ct);
        setupBody.Should().Contain($"action=\"{expectedPrefix}/Setup?handler=Password\"");
        surfaces.Add(("GET /Setup (password step)", setupBody));

        // 3. POST the password - a LocalRedirect("~/Setup") whose
        //    Location must carry the prefix. Also signs us in.
        var pwdPost = await SendAsync(HttpMethod.Post, "/Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = "hunter2hunter2",
            ["confirm"] = "hunter2hunter2",
        }));
        pwdPost.StatusCode.Should().Be(HttpStatusCode.Found);
        pwdPost.Headers.Location!.OriginalString.Should().Be($"{expectedPrefix}/Setup");

        // 4. The bearer step renders the detect-bearer JS fetch target
        //    via Url.Content and its own form action.
        var bearerStep = await SendAsync(HttpMethod.Get, "/Setup");
        bearerStep.StatusCode.Should().Be(HttpStatusCode.OK);
        var bearerBody = await bearerStep.Content.ReadAsStringAsync(ct);
        bearerBody.Should().Contain($"action=\"{expectedPrefix}/Setup?handler=Bearer\"");
        bearerBody.Should().Contain($"\"{expectedPrefix}/Config/detect-bearer?host=\"");
        surfaces.Add(("GET /Setup (bearer step)", bearerBody));

        // 5. POST the bearer config - LocalRedirect("~/") to the
        //    dashboard.
        var bearerPost = await SendAsync(HttpMethod.Post, "/Setup?handler=Bearer", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["callsign"] = "M0LTE-7",
            ["nodeHost"] = "localhost",
            ["nodeBearer"] = "agw",
            ["port"] = "8000",
            ["rhpUser"] = "",
            ["rhpPass"] = "",
        }));
        bearerPost.StatusCode.Should().Be(HttpStatusCode.Found);
        bearerPost.Headers.Location!.OriginalString.Should().Be($"{expectedPrefix}/");

        // 6. The authenticated dashboard: layout nav hrefs (~/ via the
        //    UrlResolutionTagHelper), the Stop-TX form action, and the
        //    topbar's Url.Content-resolved fetch target.
        var dash = await SendAsync(HttpMethod.Get, "/");
        dash.StatusCode.Should().Be(HttpStatusCode.OK);
        var dashBody = await dash.Content.ReadAsStringAsync(ct);
        dashBody.Should().Contain($"href=\"{expectedPrefix}/Messages\"");
        dashBody.Should().Contain($"href=\"{expectedPrefix}/Settings\"");
        dashBody.Should().Contain($"action=\"{expectedPrefix}/TxControl/stop\"");
        dashBody.Should().Contain($"fetch('{expectedPrefix}/Operational?full=true'");
        surfaces.Add(("GET / (dashboard)", dashBody));

        // 7. Messages: tab links and the SSE EventSource target.
        var messages = await SendAsync(HttpMethod.Get, "/Messages");
        messages.StatusCode.Should().Be(HttpStatusCode.OK);
        var messagesBody = await messages.Content.ReadAsStringAsync(ct);
        messagesBody.Should().Contain($"href=\"{expectedPrefix}/Messages?tab=outbound\"");
        messagesBody.Should().Contain($"new EventSource('{expectedPrefix}/Events/inbound')");
        surfaces.Add(("GET /Messages", messagesBody));

        return surfaces;
    }

    /// <summary>
    /// Boots the already-built dapps.core.dll as a subprocess on an
    /// ephemeral port with a throwaway working directory (the SQLite
    /// db lands in its data/ subdir). Same launch model as
    /// dapps.core.uitests' WebAppFixture, minus the browser.
    /// </summary>
    private sealed class DappsSubprocess : IAsyncDisposable
    {
        private static readonly Regex ListeningRegex =
            new(@"Now listening on:\s*(http://[^\s]+)", RegexOptions.Compiled);

        private Process? _process;
        private string? _workDir;

        public string BaseUrl { get; private set; } = null!;

        public static async Task<DappsSubprocess> StartAsync()
        {
            var self = new DappsSubprocess
            {
                _workDir = Path.Combine(Path.GetTempPath(), $"dapps-pathbase-test-{Guid.NewGuid():N}"),
            };
            Directory.CreateDirectory(Path.Combine(self._workDir, "data"));

            self._process = new Process
            {
                StartInfo =
                {
                    FileName = "dotnet",
                    Arguments = $"\"{LocateDappsDll()}\"",
                    WorkingDirectory = self._workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            var env = self._process.StartInfo.EnvironmentVariables;

            // The child inherits THIS test process's environment, and
            // sibling tests (DbStartupTests, Rhpv2InboundServiceTests)
            // mutate DAPPS_*/PDN_* vars process-wide via
            // Environment.SetEnvironmentVariable. Those classes live in a
            // different xUnit collection, so they run in PARALLEL with us;
            // if e.g. PDN_NODE_CALLSIGN happens to be set at the instant we
            // spawn, DbStartup in the child derives a real callsign, the
            // fresh-install /Setup journey skips the bearer step (OnGet
            // sees a configured callsign and 302s to /), and the assertion
            // for 200 fails - an intermittent flake that depends purely on
            // parallel timing, not on anything this test does. Scrub every
            // inherited DAPPS_*/PDN_* key so the subprocess always boots a
            // pristine first-run state, then set only what we need below.
            foreach (var key in env.Keys.Cast<string>()
                         .Where(k => k.StartsWith("DAPPS_", StringComparison.OrdinalIgnoreCase)
                                  || k.StartsWith("PDN_", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                env.Remove(key);
            }

            // net8 binary on a possibly-newer host runtime; harmless
            // where net8 is the latest installed.
            env["DOTNET_ROLL_FORWARD"] = "LatestMajor";
            env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            env["DAPPS_MQTT_PORT"] = PickEphemeralPort().ToString();
            env["DAPPS_AGW_PORT"] = "0";
            env["DAPPS_UDP_LISTEN_PORT"] = "0";
            env["DAPPS_HEARTBEAT_ENABLED"] = "false";
            env["DAPPS_PROBING_ENABLED"] = "false";
            env["DAPPS_UPDATE_CHECK_ENABLED"] = "false";

            var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            self._process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var match = ListeningRegex.Match(e.Data);
                if (match.Success) listening.TrySetResult(match.Groups[1].Value);
            };
            self._process.ErrorDataReceived += (_, _) => { /* keep stderr drained */ };

            self._process.Start();
            self._process.BeginOutputReadLine();
            self._process.BeginErrorReadLine();

            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            var done = await Task.WhenAny(listening.Task, timeout);
            if (done == timeout)
            {
                try { self._process.Kill(true); } catch { /* ignore */ }
                throw new TimeoutException("dapps.core didn't surface a 'Now listening on' line within 60s.");
            }
            self.BaseUrl = await listening.Task;
            return self;
        }

        public ValueTask DisposeAsync()
        {
            if (_process is not null && !_process.HasExited)
            {
                try { _process.Kill(true); } catch { /* ignore */ }
                try { _process.WaitForExit(5_000); } catch { /* ignore */ }
            }
            _process?.Dispose();
            if (_workDir is not null && Directory.Exists(_workDir))
            {
                try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>Sibling-project build output, same configuration
        /// as the test assembly first (CI runs Release, local dev
        /// usually Debug), then the other as a fallback - mirrors
        /// uitests' LocateDappsBinary.</summary>
        private static string LocateDappsDll()
        {
            var here = AppContext.BaseDirectory; // …/dapps.core.tests/bin/<Cfg>/net8.0
            var primaryCfg = new DirectoryInfo(here).Parent?.Name ?? "Debug";
            var fallbackCfg = primaryCfg == "Debug" ? "Release" : "Debug";

            foreach (var cfg in new[] { primaryCfg, fallbackCfg })
            {
                var dll = Path.GetFullPath(Path.Combine(
                    here, "..", "..", "..", "..", "dapps.core", "bin", cfg, "net8.0", "dapps.core.dll"));
                if (File.Exists(dll)) return dll;
            }

            throw new FileNotFoundException(
                "dapps.core.dll not found in the sibling project's build output - build src/dapps/dapps.core first.");
        }

        private static int PickEphemeralPort()
        {
            using var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }
    }
}
