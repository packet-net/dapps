using System.Diagnostics;
using System.Text.RegularExpressions;
using dapps.core.Services;

namespace dapps.core.uitests;

/// <summary>
/// Boots the dapps.core app as a real subprocess for the UI test
/// session. WebApplicationFactory + TestServer don't speak HTTP to a
/// browser, and the two-host trick (TestServer for WAF + Kestrel for
/// the browser) trips on shared service-provider lifetime in WAF
/// 8.x - subprocess is the simpler, more honest model: same binary
/// the operator runs, same env-var path, same startup ordering.
///
/// Side-effects we tame:
/// - SQLite path → per-test temp file. Set both via
///   <see cref="DbInfo.OverridePath"/> (visible to in-process code if
///   anyone needs it) AND via <c>DAPPS_DB_PATH</c> isn't a knob
///   today, so we cd into a temp dir whose <c>data/</c> subdir is the
///   one DbInfo's default-path lookup picks up.
/// - Callsign is intentionally left unset - the daemon now starts in
///   setup-required mode on the placeholder, so JourneyTests drives
///   the /Setup wizard's bearer step end-to-end.
/// - MQTT → high port (28830) to avoid collision with a local dev
///   daemon on 1883.
/// - AGW → port 0 means the AGW client connects to an unbound port
///   and retries quietly forever. Harmless for UI tests.
/// - HTTP → ASPNETCORE_URLS=http://127.0.0.1:0 so Kestrel picks an
///   ephemeral port; we scrape it from the "Now listening on" line.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private static readonly Regex ListeningRegex =
        new(@"Now listening on:\s*(http://[^\s]+)", RegexOptions.Compiled);

    private readonly bool _authRequired;
    private Process? _process;
    private string? _baseUrl;
    private string? _workDir;

    // xunit requires a collection-fixture type (ICollectionFixture<WebAppFixture>
    // in UiCollection) to expose exactly one public constructor, so the
    // authRequired switch below is internal rather than a second public
    // overload or a default-parameter - either of those trips xunit's
    // "may only define a single public constructor" / "unresolved
    // constructor arguments" validation when it activates this as a
    // fixture, even for test classes that never go through that path.
    public WebAppFixture() : this(authRequired: false) { }

    /// <param name="authRequired">Seeds <c>DAPPS_AUTH_REQUIRED</c> for this
    /// instance's subprocess. Most UI tests talk to MQTT/REST without an
    /// app token, so the shared collection fixture uses the public
    /// parameterless constructor (false); a test that specifically wants
    /// the real new-install default - AuthRequired true - constructs its
    /// own instance via this internal overload instead.</param>
    internal WebAppFixture(bool authRequired)
    {
        _authRequired = authRequired;
    }

    public string BaseUrl => _baseUrl
        ?? throw new InvalidOperationException("Fixture not initialised - call InitializeAsync first.");

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"dapps-uitest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workDir, "data"));

        var dapps = LocateDappsBinary();

        _process = new Process
        {
            StartInfo =
            {
                FileName = dapps.fileName,
                Arguments = dapps.arguments,
                WorkingDirectory = _workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        var env = _process.StartInfo.Environment;
        // The child inherits this process's environment. A stray DAPPS_* or
        // PDN_* variable changes how the daemon starts (a PDN_APP_CALLSIGN
        // makes it bind that callsign verbatim), so drop every inherited one
        // before setting the fixture's own (#330).
        foreach (var key in env.Keys
                     .Where(k => k.StartsWith("DAPPS_", StringComparison.OrdinalIgnoreCase)
                              || k.StartsWith("PDN_", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            env.Remove(key);
        }
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        env["DAPPS_MQTT_PORT"] = PickEphemeralPort().ToString();
        env["DAPPS_AGW_PORT"] = "0";
        env["DAPPS_UDP_LISTEN_PORT"] = "0";
        env["DAPPS_HEARTBEAT_ENABLED"] = "false";
        env["DAPPS_PROBING_ENABLED"] = "false";
        env["DAPPS_UPDATE_CHECK_ENABLED"] = "false";
        // Fresh DB every run, which now defaults AuthRequired to true
        // (#316). Most UI tests talk to MQTT/REST without an app token,
        // so the default here pins the fixture to the old open-access
        // behaviour; a caller that specifically wants to exercise the
        // AuthRequired=true path (e.g. the Setup page's token hint)
        // passes authRequired: true.
        env["DAPPS_AUTH_REQUIRED"] = _authRequired ? "true" : "false";

        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var match = ListeningRegex.Match(e.Data);
            if (match.Success) listening.TrySetResult(match.Groups[1].Value);
        };
        _process.ErrorDataReceived += (_, _) => { /* keep stderr drained */ };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // 60s - first-time `dotnet run` does a build, which is slow.
        // Subsequent runs reuse the already-built binary and are fast.
        var timeout = Task.Delay(TimeSpan.FromSeconds(60));
        var done = await Task.WhenAny(listening.Task, timeout);
        if (done == timeout)
        {
            try { _process.Kill(true); } catch { /* ignore */ }
            throw new TimeoutException(
                "dapps.core didn't surface a 'Now listening on' line within 60s. " +
                "First-time builds can be slow - try `dotnet build` from src/dapps once before running UI tests.");
        }
        _baseUrl = await listening.Task;
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

    /// <summary>
    /// Resolves how to launch dapps.core. Prefers the already-built
    /// binary (faster and avoids `dotnet run`'s rebuild-on-every-test
    /// surprise) and falls back to <c>dotnet run</c> if no build
    /// output exists yet. Tries the same configuration as the test
    /// assembly first (CI runs Release; local dev usually Debug),
    /// then the other one as a fallback.
    /// </summary>
    private static (string fileName, string arguments) LocateDappsBinary()
    {
        var here = AppContext.BaseDirectory;
        // …/dapps.core.uitests/bin/<Cfg>/net10.0
        var primaryCfg = new DirectoryInfo(here).Parent?.Name ?? "Debug";
        var fallbackCfg = primaryCfg == "Debug" ? "Release" : "Debug";

        foreach (var cfg in new[] { primaryCfg, fallbackCfg })
        {
            var dll = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "dapps.core", "bin", cfg, "net10.0", "dapps.core.dll"));
            if (File.Exists(dll))
            {
                return ("dotnet", $"\"{dll}\"");
            }
        }

        var coreCsproj = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "dapps.core", "dapps.core.csproj"));
        return ("dotnet", $"run --no-launch-profile --project \"{coreCsproj}\"");
    }

    /// <summary>
    /// Pick a free TCP port the embedded MQTT broker can claim.
    /// Tiny race window (someone else binds it before dapps does)
    /// is acceptable - the host will exit-code 78 and the test will
    /// time out with a clear message.
    /// </summary>
    private static int PickEphemeralPort()
    {
        using var sock = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        sock.Start();
        var port = ((System.Net.IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return port;
    }
}
