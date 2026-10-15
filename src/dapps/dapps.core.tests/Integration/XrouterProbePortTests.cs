using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// #246 on real XRouter: a station learned from a neighbour's peers list is
/// probed on this node's port, not on the port number the neighbour uses.
///
///     DAPPS A -RHPv2- XRouter X -AXUDP- XRouter Y -RHPv2- DAPPS B, DAPPS C
///                     port 1 only       port 1: AXUDP to X
///                                       port 2: loopback, where B reaches C
///
/// A has B as a neighbour; B has A (port 1) and C (port 2) as neighbours. A
/// probes B and asks for its peers, and B lists C with <c>port=1</c>: its own
/// port 2, a port X doesn't have. Before 0.47.1 A probed C on that port and
/// XRouter X refused every probe with RHP error 10 ("No such port" from the
/// pinned 505c, "Invalid port" from the 505p in G6FCI's four-node chain).
/// Now A probes C on its port to B, the frame crosses the AXUDP link, and C
/// (listening on every port of Y) answers.
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterProbePortTests : IAsyncLifetime
{
    private const string CallA = "G9AAA-7";
    private const string CallB = "G9BBB-7";
    private const string CallC = "G9CCC-7";

    // Y shares X's network namespace, so each node needs its own ports
    // inside it, and X publishes both nodes' RHPv2 ports.
    private const int InsideRhpX = 9000, InsideRhpY = 9001;
    private const int InsideAgwX = 8000, InsideAgwY = 8001;
    private const int AxudpX = 10093, AxudpY = 10094;

    private IContainer? x;
    private IContainer? y;
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
        // Y lives in X's network namespace; stop it first.
        if (y is not null) await y.DisposeAsync();
        if (x is not null) await x.DisposeAsync();
    }

    [Fact]
    public async Task PeerLearnedFromANeighbour_IsProbedOnOurPortToThatNeighbour()
    {
        var ct = TestContext.Current.CancellationToken;
        x = new ContainerBuilder(XrouterIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config("G9XXX-1", "XRX", InsideAgwX, InsideRhpX, AxudpX, AxudpY, loopback: false)), "/data/XROUTER.CFG")
            .WithResourceMapping(Encoding.UTF8.GetBytes(AccessSys), "/data/ACCESS.SYS")
            .WithPortBinding(InsideRhpX, assignRandomHostPort: true)
            .WithPortBinding(InsideRhpY, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideRhpX))
            .Build();
        await x.StartAsync(ct);
        var xId = x.Id;
        y = new ContainerBuilder(XrouterIntegrationFixture.Image)
            .WithCreateParameterModifier(p => p.HostConfig!.NetworkMode = $"container:{xId}")
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config("G9YYY-1", "XRY", InsideAgwY, InsideRhpY, AxudpY, AxudpX, loopback: true)), "/data/XROUTER.CFG")
            .WithResourceMapping(Encoding.UTF8.GetBytes(AccessSys), "/data/ACCESS.SYS")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideRhpY))
            .Build();
        await y.StartAsync(ct);
        await Task.Delay(2000, ct);
        var rhpX = x.GetMappedPublicPort(InsideRhpX);
        var rhpY = x.GetMappedPublicPort(InsideRhpY);

        // DAPPS port bytes are 0-indexed: 0 is XRouter's PORT=1, 1 is PORT=2.
        var a = await StartAsync("xr246-a", CallA, rhpX, 0, [new(CallB, 0)], ct);
        var b = await StartAsync("xr246-b", CallB, rhpY, 0, [new(CallA, 0), new(CallC, 1)], ct);
        var c = await StartAsync("xr246-c", CallC, rhpY, 1, [new(CallB, 1)], ct);

        await a.SignInAsAdminAsync(ct);

        // A reaches B over AXUDP and learns of C from B's peers list.
        var probeB = await ProbeAsync(a, CallB, ct);
        probeB.LastSuccessAt.Should().NotBeNull($"A should reach B over AXUDP: {probeB.LastError}\n{a.Tail()}");
        (await ProbesAsync(a, ct)).Single(p => p.Callsign == CallC).Source.Should().Be($"via:{CallB}");

        // The scheduled sweep probes the candidate, as it would overnight.
        // B opted out, so the sweep probes C alone: no second probe of B and
        // no wait between probes inside the one HTTP request.
        (await a.Http.PostAsJsonAsync($"Probes/{CallB}/optout", new { optOut = true }, ct)).EnsureSuccessStatusCode();
        var sweep = await a.Http.PostAsync("Probes/run", null, ct);
        sweep.EnsureSuccessStatusCode();
        var probedC = (await ProbesAsync(a, ct)).Single(p => p.Callsign == CallC);
        probedC.LastProbedAt.Should().NotBeNull();
        probedC.LastError.Should().NotContain("RHP error 10", "X has no port 2: that is B's port to C");
        probedC.LastBearerPort.Should().Be(0, "C is reached through B, on A's port to B");
        probedC.LastSuccessAt.Should().NotBeNull($"C should answer A's probe through Y: {probedC.LastError}\n{a.Tail()}\n{c.Tail()}");
    }

    private async Task<DappsDaemon> StartAsync(
        string name, string callsign, int rhpPort, int bearerPort, DappsDaemon.Neighbour[] neighbours, CancellationToken ct)
    {
        var node = NodeAttachment.Rhp("127.0.0.1", rhpPort, bearerPort);
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var daemon = await DappsDaemon.StartAsync(name, callsign, node, neighbours, settings, ct);
        running.Add(daemon);
        return daemon;
    }

    private static async Task<ProbedNodeModel> ProbeAsync(DappsDaemon from, string callsign, CancellationToken ct)
    {
        var response = await from.Http.PostAsync($"Probes/run/{callsign}", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProbedNodeModel>(ct))!;
    }

    private static async Task<IReadOnlyList<ProbedNodeModel>> ProbesAsync(DappsDaemon on, CancellationToken ct) =>
        (await on.Http.GetFromJsonAsync<List<ProbedNodeModel>>("Probes", ct))!;

    /// <summary>Any callsign, no password, from anywhere, as in
    /// <see cref="XrouterIntegrationFixture"/>.</summary>
    private const string AccessSys = "0.0.0.0/0 1\n";

    /// <summary>
    /// PORT=1 is an AXUDP link to the other node on 127.0.0.1 (the shared
    /// namespace), so it runs on the Linux stack; AXUDP needs an IPADDRESS
    /// even so. Y adds PORT=2, a loopback.
    /// </summary>
    private static string Config(string nodeCall, string alias, int agwPort, int rhpPort, int udpLocal, int udpRemote, bool loopback)
    {
        var config = new StringBuilder()
            .Append("DNS=8.8.8.8\n")
            .Append($"NODECALL={nodeCall}\n")
            .Append($"NODEALIAS={alias}\n")
            .Append("LOCATOR=IO91PM\n")
            .Append($"CONSOLECALL={nodeCall[..5]}\n")
            .Append($"IPADDRESS=44.0.0.{udpLocal - 10092}\n")
            .Append($"AGWPORT={agwPort}\n")
            .Append($"RHPPORT={rhpPort}\n")
            .Append("INTERFACE=1\n\tTYPE=AXUDP\n\tMTU=256\nENDINTERFACE\n");
        if (loopback) config.Append("INTERFACE=2\n\tTYPE=LOOPBACK\n\tPROTOCOL=KISS\n\tMTU=256\nENDINTERFACE\n");
        config.Append(
            "PORT=1\n" +
            "\tID=\"AXUDP to the other node\"\n" +
            "\tINTERFACENUM=1\n" +
            "\tIPLINK=127.0.0.1\n" +
            $"\tUDPLOCAL={udpLocal}\n" +
            $"\tUDPREMOTE={udpRemote}\n" +
            "\tFRACK=2000\n" +
            "\tRESPTIME=200\n" +
            "ENDPORT\n");
        if (loopback) config.Append("PORT=2\n\tID=\"Loopback\"\n\tINTERFACENUM=2\nENDPORT\n");
        return config.ToString();
    }
}
