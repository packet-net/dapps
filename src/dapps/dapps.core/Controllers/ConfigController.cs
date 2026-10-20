using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using dapps.client;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class ConfigController(SystemOptionsStore options, AdminPasswordStore adminPassword) : ControllerBase
{
    [HttpGet]
    public ActionResult<SystemOptions> Get() => options.CurrentValue;

    /// <summary>
    /// Change settings. Only the settings in the body change; the rest
    /// keep their stored values. The Settings page doesn't send every
    /// setting (the TX stop, the routing algorithm, the audit and
    /// fragment settings aren't on it), and binding the body to a whole
    /// <see cref="SystemOptions"/> used to save those as their defaults,
    /// so saving the page turned a stopped transmitter back on. Names
    /// that aren't settings are ignored.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonObject? changes)
    {
        if (changes is null) return BadRequest("Send a JSON object of the settings to change.");
        SystemOptions merged;
        try { merged = Merge(options.CurrentValue, changes); }
        catch (JsonException ex) { return BadRequest(ex.Message); }
        // The node's callsign goes on the air as src= on every message
        // we send, so it must be one the offer line can carry (#313).
        // Checked only when it changes: the Settings page always sends
        // it, and an older stored value must not stop other settings
        // being saved.
        if (!string.Equals(merged.Callsign, options.CurrentValue.Callsign, StringComparison.Ordinal)
            && MessageFields.CallsignError(merged.Callsign) is { } callError)
        {
            return BadRequest($"Callsign: {callError}");
        }
        if (merged.DefaultPort != options.CurrentValue.DefaultPort && merged.DefaultPort < 1)
        {
            return BadRequest("DefaultPort: the node's port number, as the node shows it; ports count from 1.");
        }
        // Typed at other nodes' prompts over the air, so it must be one
        // word: a CR in it would run a second command there (#309). Like
        // the callsign, checked only when it changes, so an older stored
        // value doesn't stop other settings being saved; the prober
        // refuses to send that value instead.
        if (!string.Equals(merged.NodePromptApplicationCommand, options.CurrentValue.NodePromptApplicationCommand, StringComparison.Ordinal))
        {
            merged.NodePromptApplicationCommand = merged.NodePromptApplicationCommand.Trim();
            if (NodePromptText.ApplicationCommandError(merged.NodePromptApplicationCommand) is { } commandError)
            {
                return BadRequest($"NodePromptApplicationCommand: {commandError}");
            }
        }
        await options.SaveAsync(merged);
        return Ok();
    }

    /// <summary>
    /// <paramref name="current"/> with the settings named in
    /// <paramref name="changes"/> (matched case-insensitively) replaced.
    /// Throws <see cref="JsonException"/> for a value of the wrong type,
    /// or null for a setting that can't be null.
    /// </summary>
    internal static SystemOptions Merge(SystemOptions current, JsonObject changes)
    {
        var json = JsonSerializerOptions.Web;
        var merged = JsonSerializer.SerializeToNode(current, json)!.AsObject();
        var names = merged.Select(p => p.Key).ToList();
        changes = WithLegacyDefaultPort(changes);
        foreach (var (name, value) in changes)
        {
            var key = names.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null) continue;
            if (value is null && !Nullable(key))
            {
                throw new JsonException($"{name} can't be null.");
            }
            merged[key] = value?.DeepClone();
        }
        return merged.Deserialize<SystemOptions>(json)!;
    }

    /// <summary>
    /// The default port was <c>DefaultBearerPort</c> up to 0.52, counting
    /// the node's ports from 0. It is <c>DefaultPort</c> now, the node's
    /// own number (#233). An old caller's <c>DefaultBearerPort</c> is still
    /// taken, as counting from 0, unless <c>DefaultPort</c> is sent too.
    /// Deprecated.
    /// </summary>
    internal static JsonObject WithLegacyDefaultPort(JsonObject changes)
    {
        var legacy = changes.FirstOrDefault(p => string.Equals(p.Key, "DefaultBearerPort", StringComparison.OrdinalIgnoreCase));
        if (legacy.Key is null) return changes;
        var copy = changes.DeepClone().AsObject();
        copy.Remove(legacy.Key);
        if (copy.Any(p => string.Equals(p.Key, nameof(SystemOptions.DefaultPort), StringComparison.OrdinalIgnoreCase))) return copy;
        if (legacy.Value is null) throw new JsonException("DefaultBearerPort can't be null.");
        var counted = legacy.Value.Deserialize<int>();
        if (counted < 0) throw new JsonException("DefaultBearerPort counts from 0; it can't be negative.");
        copy[nameof(SystemOptions.DefaultPort)] = counted + 1;
        return copy;
    }

    private static bool Nullable(string jsonName)
    {
        var property = typeof(SystemOptions).GetProperty(jsonName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        return property is not null
            && new System.Reflection.NullabilityInfoContext().Create(property).WriteState == System.Reflection.NullabilityState.Nullable;
    }

    /// <summary>
    /// First-run / wizard helper: tell the caller which packet-node
    /// software is running so /Setup can default the bearer dropdown.
    ///
    /// Two signals, OR'd:
    /// <list type="number">
    /// <item><description>Process scan. Walks the local process list
    /// for <c>linbpq</c> / <c>bpq32</c> (-> AGW) and <c>xrlin</c> /
    /// <c>xrouter</c> (-> RHPv2). Authoritative when DAPPS shares a
    /// host with the packet node, which is the common deployment.
    /// Doesn't depend on the bearer's listener being up at this exact
    /// moment - operators commonly haven't enabled <c>AGWPORT</c> /
    /// <c>RHPPORT</c> yet on a fresh BPQ/XRouter install.</description></item>
    /// <item><description>TCP probe of the well-known ports (AGW 8000,
    /// RHPv2 9000) on the configured host. Catches setups where DAPPS
    /// runs on a different host from the node.</description></item>
    /// </list>
    /// Either signal alone counts the bearer as available.
    /// </summary>
    [HttpGet("detect-bearer")]
    public async Task<DetectBearerResponse> DetectBearer([FromQuery] string? host = null)
    {
        var nodeHost = string.IsNullOrWhiteSpace(host) ? options.CurrentValue.NodeHost : host.Trim();
        if (string.IsNullOrWhiteSpace(nodeHost)) nodeHost = "localhost";

        var (procAgw, procRhp, procNote) = ScanLocalProcesses();

        var agwTask = ProbeTcp(nodeHost, 8000, TimeSpan.FromMilliseconds(250));
        var rhpTask = ProbeTcp(nodeHost, 9000, TimeSpan.FromMilliseconds(250));
        await Task.WhenAll(agwTask, rhpTask);

        var agw = procAgw || agwTask.Result;
        var rhp = procRhp || rhpTask.Result;
        var suggested = (agw, rhp) switch
        {
            (true, false) => "agw",
            (false, true) => "rhpv2",
            (true, true) => "rhpv2",   // both detected - prefer RHPv2 (sidesteps XR's AGW per-conn-claim quirk)
            _ => "unknown",
        };

        var notes = new List<string>();
        if (procNote.Length > 0) notes.Add(procNote);
        if (agwTask.Result) notes.Add("AGW :8000 listening");
        if (rhpTask.Result) notes.Add("RHPv2 :9000 listening");

        return new DetectBearerResponse(nodeHost, agw, rhp, suggested, string.Join("; ", notes));
    }

    /// <summary>Walk the local process list and detect known packet-node
    /// processes. Returns (any-AGW-host, any-RHPv2-host, human-readable-note).
    /// Catches all exceptions per-process - permission denied / zombies /
    /// other transient races shouldn't take the whole detection down.</summary>
    private static (bool agw, bool rhpv2, string note) ScanLocalProcesses()
    {
        bool agw = false, rhpv2 = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    string name;
                    try { name = (p.ProcessName ?? "").ToLowerInvariant(); }
                    catch { continue; }

                    if (name == "linbpq" || name == "bpq32")
                    {
                        agw = true;
                        seen.Add(p.ProcessName!);
                    }
                    else if (name == "xrlin" || name == "xrouter")
                    {
                        rhpv2 = true;
                        seen.Add(p.ProcessName!);
                    }
                }
            }
        }
        catch
        {
            // Process.GetProcesses can fail on locked-down systems.
            // Fall back to port probes alone.
        }
        var note = seen.Count > 0 ? string.Join(", ", seen) + " running" : "";
        return (agw, rhpv2, note);
    }

    private static async Task<bool> ProbeTcp(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Change the dashboard admin password. Needs the current password
    /// as well as the new one, and the new one must be at least
    /// <see cref="AdminPasswordStore.MinLength"/> characters. There is
    /// no way to clear it (#316).
    ///
    /// Behind the admin cookie like the rest of <c>/Config</c>. A fresh
    /// node with no password yet sets its first one on <c>/Setup</c>,
    /// since the auth middleware sends every other request there; the
    /// current-password check is skipped only in that no-password case.
    /// A change signs out every other dashboard login.
    /// </summary>
    [HttpPost("admin-password")]
    public async Task<IActionResult> SetAdminPassword([FromBody] AdminPasswordRequest req)
    {
        if (await adminPassword.IsConfiguredAsync() && !await adminPassword.VerifyAsync(req.CurrentPassword))
        {
            return BadRequest("The current password is wrong.");
        }
        if (AdminPasswordStore.NewPasswordError(req.Password) is { } error)
        {
            return BadRequest(error);
        }
        await adminPassword.SetAsync(req.Password);
        // The change signs out every other browser (#385); sign this
        // one straight back in so the sysop who made it stays put.
        await AdminSession.SignInAsync(HttpContext);
        return NoContent();
    }
}

public sealed record AdminPasswordRequest(string? Password, string? CurrentPassword = null);

/// <summary>
/// Result of <see cref="ConfigController.DetectBearer"/>. <c>Suggested</c>
/// is what the /Setup wizard should default to: <c>"agw"</c> if only AGW
/// detected; <c>"rhpv2"</c> if only RHPv2 (or both) detected;
/// <c>"unknown"</c> if neither is reachable. <c>Notes</c> describes what
/// triggered the detection (e.g. <c>"linbpq running; AGW :8000 listening"</c>)
/// for surfacing to the operator.
/// </summary>
public sealed record DetectBearerResponse(string Host, bool Agw, bool Rhpv2, string Suggested, string Notes);
