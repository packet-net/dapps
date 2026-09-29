using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.tests.Integration;

/// <summary>
/// A packet.net node (pdn) in a container, as the tests run it: its config
/// seeded from <c>/etc/packetnet/packetnet.yaml</c> on first boot (the image
/// imports it into its database), one port, the RHPv2 server on for the DAPPS
/// daemon, and the web panel's login off so the tests can read its frame feed
/// and port states. Telnet is off, and NET/ROM broadcasts and ID beacons are
/// off by default, so what goes on air is DAPPS's own traffic.
/// </summary>
internal static class PdnNode
{
    /// <summary>ghcr.io/packet-net/packet.net at node-v0.55.2, pinned so a new
    /// build can't change results unnoticed. Refresh: pull the image at the
    /// release tag and take its digest.</summary>
    public const string Image = "ghcr.io/packet-net/packet.net@sha256:e2a0024c49be6b289df3fce5b9f97daaf3b3dc8e4c16dfaca4281780a3fd08af";

    /// <summary>
    /// The node's seed config. <paramref name="portYaml"/> is one entry of
    /// <c>ports:</c>. RHPv2 binds 0.0.0.0, as it must in a container for
    /// the daemon outside to reach it; pdn's own default is loopback.
    /// </summary>
    /// <remarks>
    /// The one port's id is <see cref="PortId"/>, "1": DAPPS asks RHPv2 for
    /// a port by number, 1 for the first, as XRouter numbers them, and pdn
    /// since node-v0.36.2 only takes a port's id (packet.net#841). Naming the
    /// port "1" works either way. A workaround: remove it once packet.net#841
    /// is fixed.
    /// </remarks>
    public static string Config(string callsign, string alias, string portYaml, int httpPort, int rhpPort) => $"""
        schemaVersion: 2
        identity:
          callsign: {callsign}
          alias: {alias}
        ports:
        {Indent(portYaml, 2)}
        management:
          telnet:
            enabled: false
          http:
            bind: 0.0.0.0
            port: {httpPort}
          auth:
            enabled: false
        rhp:
          enabled: true
          bind: 0.0.0.0
          port: {rhpPort}
          requireAuth: false

        """;

    /// <summary>The id every test node's one port has; see <see cref="Config"/>.
    /// Workaround for packet.net#841: remove once it is fixed, and give the
    /// ports ordinary names.</summary>
    public const string PortId = "1";

    private static string Indent(string yaml, int spaces) =>
        string.Join('\n', yaml.TrimEnd().Split('\n').Select(l => new string(' ', spaces) + l));

    /// <summary>
    /// Start a node. With <paramref name="networkOf"/>, it shares that
    /// container's network namespace (so its peers are on 127.0.0.1, and
    /// that container publishes its ports); otherwise it publishes
    /// <paramref name="publish"/> itself.
    /// </summary>
    public static async Task<IContainer> StartAsync(string config, string? networkOf, IEnumerable<int> publish)
    {
        var builder = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/packetnet/packetnet.yaml");
        if (networkOf is not null) builder = builder.WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{networkOf}");
        foreach (var port in publish) builder = builder.WithPortBinding(port, assignRandomHostPort: true);
        var container = builder.Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>Wait until the node answers and (unless <paramref name="portsUp"/>
    /// is false) every port it has is up.</summary>
    public static async Task WaitUntilUpAsync(string host, int httpPort, string name, TimeSpan limit, bool portsUp = true)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{host}:{httpPort}/"), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + limit;
        var last = "no answer";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var ports = await http.GetFromJsonAsync<JsonElement>("api/v1/ports");
                var states = ports.EnumerateArray().Select(p => p.GetProperty("state").GetString()).ToList();
                if (!portsUp || (states.Count > 0 && states.All(s => s == "up"))) return;
                last = "ports " + string.Join(", ", states);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = e.Message;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"pdn node {name} wasn't up within {limit.TotalSeconds:F0}s ({last}).");
    }

    /// <summary>
    /// Send UI frames from <paramref name="fromCall"/> on one node until
    /// the other node hears one: the first can transmit and the second
    /// receive. The frames go out through the node's RHPv2 datagram socket.
    /// </summary>
    public static async Task WaitUntilHeardAsync(string host, int fromRhpPort, string fromCall, int toHttpPort, string what)
    {
        var limit = TimeSpan.FromMinutes(2);
        using var cts = new CancellationTokenSource(limit);
        await using var heard = await AirMonitor.StartAsync([AirMonitor.Tap.PdnNode("B", host, toHttpPort)], cts.Token);
        try
        {
            while (true)
            {
                try
                {
                    await using var rhp = await RhpClient.ConnectAsync(host, fromRhpPort, cts.Token);
                    var handle = await rhp.SocketAsync(RhpV2.Client.Protocol.ProtocolFamily.Ax25, SocketMode.Dgram, cts.Token);
                    await rhp.BindAsync(handle, fromCall, PortId, cts.Token);
                    while (true)
                    {
                        await rhp.SendToAsync(handle, "ready", port: PortId, local: fromCall, remote: "READY", ct: cts.Token);
                        if (await heard.WaitForAsync("A", $"Fm {fromCall} To READY", TimeSpan.FromSeconds(3), cts.Token)) return;
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or RhpProtocolException && !cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{what}: sent UI frames from {fromCall} for {limit.TotalMinutes:F0} minutes and the other pdn node never heard them.");
        }
    }
}

/// <summary>
/// A pdn port's AX.25 and KISS settings, as its <c>ax25:</c>, <c>kiss:</c>
/// and <c>link:</c> blocks; unset means pdn's own default. Times as pdn
/// takes them: T1 and T2 in ms, the KISS ones in 10 ms units.
/// </summary>
public sealed record PdnRadio(
    int? T1Ms = null, int? T2Ms = null, int? N2 = null, int? Window = null, int? N1 = null,
    int? TxDelay = null, int? Persistence = null, int? SlotTime = null, int? TxTail = null,
    bool AckMode = false, bool T1FromTxComplete = false, string? Dial = null)
{
    /// <summary>The port's tuning blocks, to follow its <c>transport:</c>.</summary>
    public string Yaml()
    {
        var sb = new StringBuilder();
        var ax25 = Lines(("t1Ms", T1Ms), ("t2Ms", T2Ms), ("n2", N2), ("windowSize", Window), ("n1", N1));
        if (ax25.Length > 0) sb.Append("ax25:\n").Append(ax25);
        var kiss = Lines(("txDelay", TxDelay), ("persistence", Persistence), ("slotTime", SlotTime), ("txTail", TxTail))
            + (AckMode ? "  ackMode: true\n" : "") + (T1FromTxComplete ? "  t1FromTxComplete: true\n" : "");
        if (kiss.Length > 0) sb.Append("kiss:\n").Append(kiss);
        if (Dial is not null) sb.Append($"link:\n  dial: {Dial}\n");
        return sb.ToString();
    }

    private static string Lines(params (string Key, int? Value)[] values) =>
        string.Concat(values.Where(v => v.Value is not null).Select(v => $"  {v.Key}: {v.Value}\n"));

    public override string ToString()
    {
        var parts = new List<string>();
        void Add(string name, object? value) { if (value is not null) parts.Add($"{name}={value}"); }
        Add("t1Ms", T1Ms); Add("t2Ms", T2Ms); Add("n2", N2); Add("windowSize", Window); Add("n1", N1);
        Add("txDelay", TxDelay); Add("persistence", Persistence); Add("slotTime", SlotTime); Add("txTail", TxTail);
        if (AckMode) parts.Add("ackMode");
        if (T1FromTxComplete) parts.Add("t1FromTxComplete");
        Add("dial", Dial);
        return parts.Count == 0 ? "pdn defaults" : string.Join(' ', parts);
    }

    /// <summary>
    /// This with overrides from <paramref name="spec"/>, e.g.
    /// <c>T1=10000,WINDOW=2,ACKMODE=1,T1FROMTX=1</c>, for trying other
    /// settings without a rebuild.
    /// </summary>
    public PdnRadio With(string? spec)
    {
        var radio = this;
        foreach (var pair in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            int V() => int.Parse(kv[1]);
            radio = kv[0].ToUpperInvariant() switch
            {
                "T1" => radio with { T1Ms = V() },
                "T2" => radio with { T2Ms = V() },
                "N2" => radio with { N2 = V() },
                "WINDOW" => radio with { Window = V() },
                "N1" => radio with { N1 = V() },
                "TXDELAY" => radio with { TxDelay = V() },
                "PERSIST" => radio with { Persistence = V() },
                "SLOTTIME" => radio with { SlotTime = V() },
                "TXTAIL" => radio with { TxTail = V() },
                "ACKMODE" => radio with { AckMode = V() != 0 },
                "T1FROMTX" => radio with { T1FromTxComplete = V() != 0 },
                "DIAL" => radio with { Dial = kv[1] },
                _ => throw new ArgumentException($"pdn radio settings: unknown setting {kv[0]}"),
            };
        }
        return radio;
    }
}

/// <summary>
/// Two pdn nodes linked over AXUDP, each with a DAPPS daemon attached over
/// RHPv2: the pdn counterpart of <see cref="TwoInstanceLinbpqFixture"/>.
///
///     app -> DAPPS A -RHPv2- pdn-A -AXUDP- pdn-B -RHPv2- DAPPS B -> app
///
/// B shares A's network namespace, so the two AXUDP ports face each other
/// on 127.0.0.1 and A publishes both nodes' web and RHPv2 ports. Each
/// node's frame feed is its monitor (<see cref="AirMonitor"/>). The ports
/// run pdn's own AX.25 defaults: AXUDP has no channel to share.
/// </summary>
public sealed class TwoPdnFixture : IDappsScenarioBed, IAsyncLifetime
{
    private const int InsideHttpA = 18301, InsideHttpB = 18302;
    private const int InsideRhpA = 18401, InsideRhpB = 18402;
    private const int AxudpA = 10093, AxudpB = 10094;

    private IContainer? a;
    private IContainer? b;

    public string Host => "127.0.0.1";
    public string CallsignA => "N0AAA";
    public string CallsignB => "N0BBB";
    public string ApplCallA => "N0AAA-3";
    public string ApplCallB => "N0BBB-3";
    public int HttpPortA { get; private set; }
    public int HttpPortB { get; private set; }
    public int RhpPortA { get; private set; }
    public int RhpPortB { get; private set; }

    /// <summary>The AXUDP port is each node's first, "1" to RHPv2 and 0 to DAPPS.</summary>
    public NodeAttachment NodeA => NodeAttachment.Rhp(Host, RhpPortA, 0);
    public NodeAttachment NodeB => NodeAttachment.Rhp(Host, RhpPortB, 0);

    public string ChannelName => "pdn, AXUDP";
    public string RadioSettings => "pdn defaults (AXUDP, no channel to share)";
    public string ReportTag => "pdn-axudp";

    public async ValueTask InitializeAsync()
    {
        await StartNodesAsync();
        await WaitUntilReadyAsync();
    }

    private async Task StartNodesAsync()
    {
        a = await PdnNode.StartAsync(
            PdnNode.Config(CallsignA, "AAA", AxudpPort(AxudpB, AxudpA), InsideHttpA, InsideRhpA),
            networkOf: null, publish: [InsideHttpA, InsideHttpB, InsideRhpA, InsideRhpB]);
        HttpPortA = a.GetMappedPublicPort(InsideHttpA);
        HttpPortB = a.GetMappedPublicPort(InsideHttpB);
        RhpPortA = a.GetMappedPublicPort(InsideRhpA);
        RhpPortB = a.GetMappedPublicPort(InsideRhpB);
        b = await PdnNode.StartAsync(
            PdnNode.Config(CallsignB, "BBB", AxudpPort(AxudpA, AxudpB), InsideHttpB, InsideRhpB),
            networkOf: a.Id, publish: []);
    }

    private static string AxudpPort(int remote, int local) => $"""
        - id: "{PdnNode.PortId}"
          transport:
            kind: axudp
            host: 127.0.0.1
            port: {remote}
            localPort: {local}
        """;

    public async Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct) =>
        await AirMonitor.StartAsync([AirMonitor.Tap.PdnNode("A", Host, HttpPortA), AirMonitor.Tap.PdnNode("B", Host, HttpPortB)], ct);

    public Task<ChannelLog?> StartChannelLogAsync(CancellationToken ct) => Task.FromResult<ChannelLog?>(null);

    /// <summary>
    /// Both nodes afresh, as after a reboot (B shares A's network, so both
    /// go), waiting only until their web ports answer, for the monitor.
    /// </summary>
    public async Task RestartNodesColdAsync()
    {
        await StopNodesAsync();
        await StartNodesAsync();
        await PdnNode.WaitUntilUpAsync(Host, HttpPortA, CallsignA, TimeSpan.FromMinutes(1), portsUp: false);
        await PdnNode.WaitUntilUpAsync(Host, HttpPortB, CallsignB, TimeSpan.FromMinutes(1), portsUp: false);
    }

    public async Task WaitUntilReadyAsync()
    {
        await PdnNode.WaitUntilUpAsync(Host, HttpPortA, CallsignA, TimeSpan.FromMinutes(1));
        await PdnNode.WaitUntilUpAsync(Host, HttpPortB, CallsignB, TimeSpan.FromMinutes(1));
        await PdnNode.WaitUntilHeardAsync(Host, RhpPortA, CallsignA + "-15", HttpPortB, ChannelName);
        await PdnNode.WaitUntilHeardAsync(Host, RhpPortB, CallsignB + "-15", HttpPortA, ChannelName);
    }

    private async Task StopNodesAsync()
    {
        // B lives in A's network namespace; stop it first.
        if (b is not null) await b.DisposeAsync();
        if (a is not null) await a.DisposeAsync();
        a = b = null;
    }

    public async ValueTask DisposeAsync() => await StopNodesAsync();
}

[CollectionDefinition("pdn two-instance", DisableParallelization = true)]
public class TwoPdnCollection : ICollectionFixture<TwoPdnFixture> { }
