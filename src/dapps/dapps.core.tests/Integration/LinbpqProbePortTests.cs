using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using dapps.core.Controllers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// #246 on real LinBPQ, the counterpart of <see cref="XrouterProbePortTests"/>:
/// a station learned from a neighbour's peers list is probed on this node's
/// port, not on the port number the neighbour uses.
///
///     DAPPS A -AGW- BPQ X -AXIP- BPQ Y -AGW- DAPPS B
///                       \           \ port 3
///                        \-AXIP---- BPQ Z -AGW- DAPPS C
///
///     X: port 1 Telnet, port 2 AXIP (reaches B on Y and C on Z)
///     Y: port 1 Telnet, port 2 AXIP to X, port 3 AXIP to Z
///     Z: port 1 Telnet, port 2 AXIP (reaches B on Y's port 3, and A on X)
///
/// A has B as a neighbour; B has A (port 2) and C (port 3) as neighbours. A
/// probes B and asks for its peers, and B lists C with <c>port=2</c>, the AGW
/// port byte of its port 3, which X doesn't have. Before 0.47.1 A probed C on
/// that port byte. Now A probes C on its port to B, the AXIP port that also
/// reaches Z, and C answers.
///
/// Y and Z share X's network namespace, as in <see cref="ThreeInstanceLinbpqFixture"/>,
/// so the AXIP links are on 127.0.0.1 and X publishes every AGW port.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LinbpqProbePortTests : IAsyncLifetime
{
    private const string CallA = "N0AAA-9";
    private const string CallB = "N0BBB-9";
    private const string CallC = "N0CCC-9";

    private const int InsideAgwX = 18101, InsideAgwY = 18102, InsideAgwZ = 18103;
    private const int AxipX = 19101, AxipY = 19102, AxipYToZ = 19103, AxipZ = 19104;

    private IContainer? x;
    private IContainer? y;
    private IContainer? z;
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
        // Y and Z live in X's network namespace; stop them first.
        if (z is not null) await z.DisposeAsync();
        if (y is not null) await y.DisposeAsync();
        if (x is not null) await x.DisposeAsync();
    }

    [Fact]
    public async Task PeerLearnedFromANeighbour_IsProbedOnOurPortToThatNeighbour()
    {
        var ct = TestContext.Current.CancellationToken;
        x = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(ConfigX()), "/data/bpq32.cfg")
            .WithPortBinding(InsideAgwX, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwY, assignRandomHostPort: true)
            .WithPortBinding(InsideAgwZ, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwX))
            .Build();
        await x.StartAsync(ct);
        var xId = x.Id;
        y = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(ConfigY()), "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig!.NetworkMode = $"container:{xId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwY))
            .Build();
        await y.StartAsync(ct);
        z = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(ConfigZ()), "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig!.NetworkMode = $"container:{xId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwZ))
            .Build();
        await z.StartAsync(ct);
        // linbpq's AGW listener accepts at once; its AXIP sockets bind a moment later.
        await Task.Delay(2000, ct);
        var agwX = x.GetMappedPublicPort(InsideAgwX);
        var agwY = x.GetMappedPublicPort(InsideAgwY);
        var agwZ = x.GetMappedPublicPort(InsideAgwZ);

        // AGW port bytes are 0-indexed: 1 is BPQ's port 2, 2 is port 3.
        var a = await StartAsync("bpq246-a", CallA, agwX, 1, [new(CallB, 1)], ct);
        var b = await StartAsync("bpq246-b", CallB, agwY, 1, [new(CallA, 1), new(CallC, 2)], ct);
        var c = await StartAsync("bpq246-c", CallC, agwZ, 1, [new(CallB, 1)], ct);
        await a.SignInAsAdminAsync(ct);
        await b.SignInAsAdminAsync(ct);

        // B really does reach C on Y's port 3.
        var bToC = await ProbeAsync(b, CallC, ct);
        bToC.LastSuccessAt.Should().NotBeNull($"B should reach C on Y's port 3: {bToC.LastError}\n{b.Tail()}\n{c.Tail()}");

        // A reaches B over AXIP and learns of C from B's peers list.
        var probeB = await ProbeAsync(a, CallB, ct);
        probeB.LastSuccessAt.Should().NotBeNull($"A should reach B over AXIP: {probeB.LastError}\n{a.Tail()}");
        (await ProbesAsync(a, ct)).Single(p => p.Callsign == CallC).Source.Should().Be($"via:{CallB}");

        // The scheduled sweep probes the candidate, as it would overnight.
        // B opted out, so the sweep probes C alone: no second probe of B and
        // no wait between probes inside the one HTTP request.
        (await a.Http.PostAsJsonAsync($"Probes/{CallB}/optout", new { optOut = true }, ct)).EnsureSuccessStatusCode();
        var sweep = await a.Http.PostAsync("Probes/run", null, ct);
        sweep.EnsureSuccessStatusCode();
        var probedC = (await ProbesAsync(a, ct)).Single(p => p.Callsign == CallC);
        probedC.LastProbedAt.Should().NotBeNull();
        probedC.LastBearerPort.Should().Be(1, $"C is reached through B, on A's port to B, not B's port to C: {probedC.LastError}\n{a.Tail()}");
        probedC.LastSuccessAt.Should().NotBeNull($"C should answer A's probe: {probedC.LastError}\n{a.Tail()}\n{c.Tail()}");
    }

    private async Task<DappsDaemon> StartAsync(
        string name, string callsign, int agwPort, int bearerPort, DappsDaemon.Neighbour[] neighbours, CancellationToken ct)
    {
        var node = NodeAttachment.Agw("127.0.0.1", agwPort, bearerPort);
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

    /// <summary>X: A's application, and an AXIP port that reaches B on Y and
    /// C on Z.</summary>
    private static string ConfigX() => Config(
        "N0XXX", "XXX", InsideAgwX, telnetBase: 18111, [(CallA, "APPLA")],
        Axip(AxipX, [("N0YYY", AxipY), (CallB, AxipY), ("N0ZZZ", AxipZ), (CallC, AxipZ)]));

    /// <summary>Y: B's application, AXIP to X on port 2 and to Z on port 3.</summary>
    private static string ConfigY() => Config(
        "N0YYY", "YYY", InsideAgwY, telnetBase: 18112, [(CallB, "APPLB")],
        Axip(AxipY, [("N0XXX", AxipX), (CallA, AxipX)]) +
        Axip(AxipYToZ, [("N0ZZZ", AxipZ), (CallC, AxipZ)]));

    /// <summary>Z: C's application, and an AXIP port that reaches B on Y's
    /// port 3 and A on X.</summary>
    private static string ConfigZ() => Config(
        "N0ZZZ", "ZZZ", InsideAgwZ, telnetBase: 18113, [(CallC, "APPLC")],
        Axip(AxipZ, [("N0YYY", AxipYToZ), (CallB, AxipYToZ), ("N0XXX", AxipX), (CallA, AxipX)]));

    private static string Axip(int udpPort, IEnumerable<(string Call, int UdpPort)> maps)
    {
        var config = new StringBuilder()
            .Append("PORT\n ID=AXIP\n DRIVER=BPQAXIP\n QUALITY=200\n MINQUAL=1\n CONFIG\n")
            .Append($" UDP {udpPort}\n");
        foreach (var (call, port) in maps) config.Append($" MAP {call} 127.0.0.1 UDP {port} B\n");
        return config.Append("ENDPORT\n\n").ToString();
    }

    private static string Config(
        string nodeCall, string alias, int agwPort, int telnetBase, IReadOnlyList<(string Call, string Alias)> apps, string axipPorts)
    {
        var config = new StringBuilder()
            .Append("SIMPLE=1\n")
            .Append($"NODECALL={nodeCall}\n")
            .Append($"NODEALIAS={alias}\n")
            .Append("LOCATOR=NONE\n")
            .Append($"AGWPORT={agwPort}\n")
            .Append("AGWSESSIONS=10\n")
            .Append("AGWMASK=1\n")
            .Append($"APPLICATIONS={string.Join(',', apps.Select(app => app.Alias))}\n");
        for (var i = 0; i < apps.Count; i++)
        {
            config.Append($"APPL{i + 1}CALL={apps[i].Call}\n").Append($"APPL{i + 1}ALIAS={apps[i].Alias}\n");
        }
        config.Append(
            "\nPORT\n ID=Telnet\n DRIVER=Telnet\n CONFIG\n" +
            $" TCPPORT={telnetBase}\n HTTPPORT={telnetBase + 10}\n NETROMPORT={telnetBase + 20}\n" +
            $" FBBPORT={telnetBase + 30}\n APIPORT={telnetBase + 40}\n" +
            $" MAXSESSIONS=20\n USER=test,test,{nodeCall},,SYSOP\nENDPORT\n\n");
        return config.Append(axipPorts).ToString();
    }
}
