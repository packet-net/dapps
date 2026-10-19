using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>
/// Works out how the node behind an AGW connection numbers its radio
/// ports (<see cref="NodePortMap"/>), once per connection (#233, #370).
///
/// The node's answer to a 'G' says which kind it is. XRouter's is
/// <c>3;Port1 with "id";Port2 with "id";Port3 with "id";</c>: its AGW
/// counts the ports from 0 in order of their <c>PORT=</c> numbers, lowest
/// first, and never says which numbers they are. So for XRouter the
/// numbers are read from its RHPv2 port: a datagram bind to 1, 2, 3, ...
/// (the node refuses one that is not a port, "Invalid port", and a bind
/// sends nothing), until as many ports are found as AGW lists. With ports
/// 2, 7 and 19, AGW port 2 is <c>PORT=19</c>. If that can't be read (no
/// RHPv2 port, wrong settings, a port list that doesn't match) the map is
/// unknown, and nothing is sent by number on that connection: never on a
/// guessed port.
///
/// Anything else (BPQ, Dire Wolf, AGWPE, or no answer to the 'G') counts
/// its ports 1, 2, 3 in AGW order: byte = number - 1. BPQ's AGW sends by
/// position in bpq32.cfg but reports frames heard by <c>PORTNUM</c>, so the
/// two agree only when the ports are numbered 1..N in config order; it
/// cannot be seen from here, except for hidden ports (the count in the
/// answer is larger than the ports it lists), which get a warning.
/// </summary>
public sealed partial class NodePortNumbers(
    IOptionsMonitor<SystemOptions> options,
    ILogger<NodePortNumbers> logger) : INodePortMaps
{
    /// <summary>The highest RHPv2 port number asked about.</summary>
    internal const int MaxPortNumber = 255;

    /// <summary>The longest reading XRouter's port numbers may take.</summary>
    internal TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(20);

    private const int InvalidPort = 10;
    private const int DuplicateSocket = 9;

    private readonly ConditionalWeakTable<AgwLink, Task<NodePortMap?>> maps = new();
    private readonly Lock gate = new();

    /// <summary>What is wrong with the node's port numbering, for the
    /// dashboard: hidden BPQ ports, or an XRouter whose numbers could not
    /// be read. Null when nothing is.</summary>
    public string? Warning { get; private set; }

    public NodePortMap? TryGet(AgwLink link)
    {
        lock (gate)
        {
            if (maps.TryGetValue(link, out var known))
            {
                return known.IsCompletedSuccessfully ? known.Result : null;
            }
        }
        var payload = link.LatestPorts?.Payload;
        if (payload is null || XrouterAgwPortCount(payload) is not null)
        {
            // Not known yet (no answer to a 'G' so far), or an XRouter
            // whose numbers must be read: work it out in the background.
            // Frames heard meanwhile can't be placed.
            _ = GetAsync(link, CancellationToken.None);
            return null;
        }
        return Plain(payload);
    }

    public Task<NodePortMap?> GetAsync(AgwLink link, CancellationToken ct)
    {
        Task<NodePortMap?> task;
        lock (gate)
        {
            if (!maps.TryGetValue(link, out task!))
            {
                task = BuildAsync(link);
                maps.Add(link, task);
            }
        }
        return task.WaitAsync(ct);
    }

    private async Task<NodePortMap?> BuildAsync(AgwLink link)
    {
        await Task.Yield();
        NodePortMap? map;
        try
        {
            map = await WorkOutAsync(link);
        }
        catch (Exception ex)
        {
            logger.LogWarning("AGW: could not work out the node's port numbers ({0})", ex.Message);
            map = null;
        }
        if (map is null)
        {
            // Not kept: the next caller tries again (XRouter's RHPv2 port
            // may only just be coming up).
            lock (gate) maps.Remove(link);
        }
        return map;
    }

    private async Task<NodePortMap?> WorkOutAsync(AgwLink link)
    {
        AgwPortList? ports = link.LatestPorts;
        if (ports is null)
        {
            try
            {
                ports = await link.QueryPortsAsync(CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // A host that doesn't answer a 'G' isn't XRouter.
                logger.LogDebug("AGW: the node did not answer a port query; counting its ports from 1");
                return NodePortMap.Plain();
            }
        }

        var xrouterCount = XrouterAgwPortCount(ports.Payload);
        if (xrouterCount is not { } count)
        {
            var map = Plain(ports.Payload);
            Warning = HiddenPortsWarning(ports.Payload);
            if (Warning is not null) logger.LogWarning("AGW: {0}", Warning);
            logger.LogInformation("AGW: the node's ports are numbered {0}", map);
            return map;
        }

        var numbers = await ReadXrouterNumbersAsync(link.Callsign, count);
        if (numbers.Count != count)
        {
            Warning = $"XRouter lists {count} port(s) over AGW, but its RHPv2 port gave [{string.Join(",", numbers)}], so DAPPS can't tell which PORT= number each AGW port is; nothing is sent on AGW-heard ports until it can (check RHPPORT in XROUTER.CFG and DAPPS's RHPv2 settings)";
            logger.LogWarning("AGW: {0}", Warning);
            return null;
        }
        Warning = null;
        var xrouter = NodePortMap.Xrouter(numbers);
        logger.LogInformation("AGW: the node's ports are numbered {0}", xrouter);
        return xrouter;
    }

    /// <summary>XRouter's <c>PORT=</c> numbers, lowest first, as many as
    /// <paramref name="enough"/>, read from its RHPv2 port.</summary>
    private async Task<List<int>> ReadXrouterNumbersAsync(string callsign, int enough)
    {
        var opts = options.CurrentValue;
        var host = string.IsNullOrEmpty(opts.NodeHost) ? "127.0.0.1" : opts.NodeHost;
        var port = opts.RhpPort > 0 ? opts.RhpPort : RhpClient.DefaultPort;
        using var timeout = new CancellationTokenSource(ScanTimeout);
        try
        {
            await using var rhp = await RhpClient.ConnectAsync(host, port, timeout.Token);
            if (!string.IsNullOrEmpty(opts.RhpUser))
            {
                await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", timeout.Token);
            }
            return await RhpPortNumbersAsync(rhp, callsign, enough, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or RhpServerException)
        {
            logger.LogWarning("AGW: could not read XRouter's port numbers from its RHPv2 port at {0}:{1} ({2})", host, port, ex.Message);
            return [];
        }
    }

    /// <summary>The RHPv2 port numbers that exist, lowest first, up to
    /// <paramref name="enough"/> of them.</summary>
    internal static async Task<List<int>> RhpPortNumbersAsync(RhpClient rhp, string callsign, int enough, CancellationToken ct)
    {
        var found = new List<int>();
        for (var n = 1; n <= MaxPortNumber && found.Count < enough; n++)
        {
            var probe = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Dgram, ct);
            try
            {
                await rhp.BindAsync(probe, local: callsign, port: n.ToString(CultureInfo.InvariantCulture), ct);
                found.Add(n);
            }
            catch (RhpServerException ex) when (ex.ErrorCode == InvalidPort)
            {
            }
            catch (RhpServerException ex) when (ex.ErrorCode == DuplicateSocket)
            {
                // A beacon holds the callsign on this port right now: it
                // is a port.
                found.Add(n);
            }
            finally
            {
                try { await rhp.CloseAsync(probe, ct); }
                catch (RhpServerException) { /* gone already */ }
            }
        }
        return found;
    }

    /// <summary>A plain host's map, bounded by the ports its answer to a
    /// 'G' lists, when it gave one.</summary>
    internal static NodePortMap Plain(byte[]? payload)
    {
        if (payload is null) return NodePortMap.Plain();
        var listed = AgwPortQuery.Parse(payload).Count;
        return NodePortMap.Plain(listed > 0 ? listed : null);
    }

    /// <summary>A warning when the answer to a 'G' counts more ports than
    /// it lists: BPQ leaves hidden ports out of the list but not the
    /// count, and then numbers the rest differently for calls than for
    /// frames it hears.</summary>
    internal static string? HiddenPortsWarning(byte[] payload)
    {
        var text = System.Text.Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
        var first = text.Split(';')[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return null;
        var listed = AgwPortQuery.Parse(payload).Count;
        return count > listed
            ? $"the node counts {count} ports over AGW but lists {listed}; BPQ hides ports that way, and then numbers calls and heard frames differently, so DAPPS may call a neighbour on the wrong port. Number the ports DAPPS uses 1 to N in bpq32.cfg order, with none hidden."
            : null;
    }

    /// <summary>The number of ports in XRouter's answer to a 'G'
    /// (<c>3;Port1 with "id";Port2 with "id";Port3 with "id";</c>), or
    /// null if it is not that shape: BPQ, Dire Wolf and others describe
    /// their ports in other words.</summary>
    internal static int? XrouterAgwPortCount(byte[]? payload)
    {
        if (payload is null) return null;
        var parts = AgwPortQuery.Parse(payload);
        if (parts.Count == 0) return null;
        var text = System.Text.Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
        var first = text.Split(';')[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != parts.Count) return null;
        for (var i = 0; i < parts.Count; i++)
        {
            var m = XrouterPortDescription().Match(parts[i].Description);
            if (!m.Success || m.Groups[1].Value != (i + 1).ToString(CultureInfo.InvariantCulture)) return null;
        }
        return count;
    }

    [GeneratedRegex("^Port([0-9]+) with \"[^\"]*\"$")]
    private static partial Regex XrouterPortDescription();
}
