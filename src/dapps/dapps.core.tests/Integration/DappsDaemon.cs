using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using dapps.core.Models;
using SQLite;

namespace dapps.core.tests.Integration;

/// <summary>
/// A real DAPPS daemon in its own process, for end-to-end tests: the
/// dapps.core build output run with its own working directory (so its own
/// database), configured through the same <c>DAPPS_*</c> environment
/// variables a deployment uses, and driven through its app API the way an
/// app would. Its neighbours are written into the fresh database before it
/// starts, as the simulation scripts do, which keeps the dashboard's login
/// out of the tests.
/// </summary>
internal sealed class DappsDaemon : IAsyncDisposable
{
    private Process process;
    private readonly StringBuilder output = new();
    private readonly string directory;

    public string Name { get; }
    public string Callsign { get; }
    public HttpClient Http { get; }

    private DappsDaemon(string name, string callsign, string directory, Process process, HttpClient http)
    {
        Name = name;
        Callsign = callsign;
        this.directory = directory;
        this.process = process;
        Http = http;
    }

    /// <summary>A neighbour row to seed: its callsign and the AGW port that reaches it.</summary>
    public sealed record Neighbour(
        string Callsign, int BearerPort, bool? CompressionEnabled = null, int? SessionTailSeconds = null,
        dapps.client.ConnectScript? Script = null);

    public static async Task<DappsDaemon> StartAsync(
        string name, string callsign, string agwHost, int agwPort, int bearerPort,
        IEnumerable<Neighbour> neighbours, IReadOnlyDictionary<string, string>? settings, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dapps-e2e-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        using (var db = new SQLiteConnection(Path.Combine(directory, "data", "dapps.db")))
        {
            db.CreateTable<DbNeighbour>();
            foreach (var n in neighbours)
            {
                db.Insert(new DbNeighbour
                {
                    Callsign = n.Callsign, BearerPort = n.BearerPort,
                    CompressionEnabled = n.CompressionEnabled, SessionTailSeconds = n.SessionTailSeconds,
                    ConnectScriptJson = n.Script?.ToJson(),
                });
            }
        }

        var httpPort = FreeTcpPort();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var dll = LocateDaemon();
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(Path.GetDirectoryName(dll)!);
        var env = new Dictionary<string, string>
        {
            ["DAPPS_CALLSIGN"] = callsign,
            ["DAPPS_NODE_HOST"] = agwHost,
            ["DAPPS_AGW_PORT"] = agwPort.ToString(),
            ["DAPPS_DEFAULT_BEARER_PORT"] = bearerPort.ToString(),
            ["DAPPS_MQTT_PORT"] = FreeTcpPort().ToString(),
            ["DAPPS_UDP_LISTEN_PORT"] = "0",
            ["DAPPS_AUTH_REQUIRED"] = "false",
            ["DAPPS_UPDATE_CHECK_ENABLED"] = "false",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort}",
            ["DOTNET_ENVIRONMENT"] = "Production",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>()) env[key] = value;
        foreach (var (key, value) in env) start.Environment[key] = value;

        // Cookies for the admin session (see SignInAsAdminAsync); no
        // redirects followed, so an auth redirect shows up as one.
        var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        var daemon = new DappsDaemon(name, callsign, directory, new Process { StartInfo = start, EnableRaisingEvents = true }, http);
        try
        {
            await daemon.LaunchAsync(ct);
        }
        catch
        {
            await daemon.DisposeAsync();
            throw;
        }
        return daemon;
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await WaitUntilOnTheNodeAsync(ct);
    }

    /// <summary>
    /// Stop the daemon as a service stop would (SIGTERM) and start it
    /// again on the same database, as after an upgrade.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await StopAsync();
        Append($"--- restarted by the test at {DateTime.UtcNow:HH:mm:ss.fff} ---");
        var start = process.StartInfo;
        process.Dispose();
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        await LaunchAsync(ct);
    }

    /// <summary>Queue a message for <paramref name="destCallsign"/>'s <paramref name="app"/>.</summary>
    public async Task<string> SubmitAsync(string app, string destCallsign, byte[] payload, CancellationToken ct, int? ttl = 600, string? streamId = null)
    {
        var response = await Http.PostAsJsonAsync("AppApi/outbound",
            new { App = app, DestCallsign = destCallsign, Payload = payload, Ttl = ttl, StreamId = streamId }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{Name}: submit failed {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Sign in as the node's admin, for the dashboard-side API (probes,
    /// neighbours, config). "Auth off" only opens the app API. Does the
    /// first-run password step, which signs the operator straight in.
    /// </summary>
    public async Task SignInAsAdminAsync(CancellationToken ct)
    {
        var page = await Http.GetStringAsync("Setup", ct);
        var token = System.Text.RegularExpressions.Regex.Match(
            page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        if (token.Length == 0) throw new InvalidOperationException($"{Name}: no antiforgery token on /Setup");
        const string password = "e2e-test-password";
        var response = await Http.PostAsync("Setup?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = password,
            ["Confirm"] = password,
            ["__RequestVerificationToken"] = token,
        }), ct);
        if ((int)response.StatusCode is not (302 or 303 or 200))
        {
            throw new InvalidOperationException($"{Name}: setting the admin password failed with {(int)response.StatusCode}");
        }
    }

    public sealed record Inbound(string Id, string SourceCallsign, byte[] Payload, int? Ttl, string? OriginatorCallsign);

    public async Task<IReadOnlyList<Inbound>> InboundAsync(string app, CancellationToken ct) =>
        await Http.GetFromJsonAsync<List<Inbound>>($"AppApi/inbound/{app}", JsonOptions, ct) ?? [];

    /// <summary>Wait until <paramref name="app"/>'s inbox holds <paramref name="count"/> messages.</summary>
    public async Task<IReadOnlyList<Inbound>> WaitForInboundAsync(string app, int count, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<Inbound> got = [];
        while (DateTime.UtcNow < deadline)
        {
            got = await InboundAsync(app, ct);
            if (got.Count >= count) return got;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"{Name} had {got.Count} of {count} message(s) for '{app}' after {timeout.TotalSeconds:F0}s.\n{Tail()}");
    }

    /// <summary>The daemon's own log so far, for a failure message.</summary>
    public string Tail(int lines = 60)
    {
        lock (output)
        {
            var all = output.ToString().Split('\n');
            return $"--- {Name} ({Callsign}) log, last {lines} lines ---\n" + string.Join('\n', all.TakeLast(lines));
        }
    }

    public string Log { get { lock (output) return output.ToString(); } }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (output) output.AppendLine(line);
    }

    private async Task WaitUntilOnTheNodeAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"{Name} exited with {process.ExitCode} while starting.\n{Tail()}");
            try
            {
                var snapshot = await Http.GetFromJsonAsync<JsonElement>("Operational", ct);
                if (snapshot.TryGetProperty("nodeReachable", out var reachable) && reachable.GetBoolean())
                {
                    // Reachable is the AGW socket; give its listener
                    // registration a moment to land on the node.
                    await Task.Delay(1000, ct);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"{Name} didn't reach its node within 60s.\n{Tail()}");
    }

    private async Task StopAsync()
    {
        bool running;
        try { running = !process.HasExited; }
        catch (InvalidOperationException) { running = false; } // never started
        if (running)
        {
            // SIGTERM first, so the host stops cleanly: held links are
            // closed and the AGW registration released, as in service.
            if (!OperatingSystem.IsWindows()) _ = kill(process.Id, 15);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Http.Dispose();
        process.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// The dapps.core build that sits beside this test build:
    /// src/dapps/dapps.core/bin/&lt;Configuration&gt;/net10.0/dapps.core.dll.
    /// </summary>
    private static string LocateDaemon()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);           // .../dapps.core.tests/bin/<Config>/net10.0
        var framework = testBin.Name;
        var configuration = testBin.Parent!.Name;
        var src = testBin.Parent!.Parent!.Parent!.Parent!.FullName;           // .../src/dapps
        var dll = Path.Combine(src, "dapps.core", "bin", configuration, framework, "dapps.core.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"dapps.core isn't built at {dll}; build the solution first.");
        return dll;
    }
}
