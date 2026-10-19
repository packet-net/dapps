using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// #233 on real linbpq (the pinned image): DAPPS shows and uses BPQ's own
/// port numbers, its <c>PORTNUM</c>s, which <c>PORTS</c> lists and
/// <c>C 2 CALL</c> uses.
///
/// Two nodes, A and B, each with <c>PORTNUM=1</c> Telnet and two AXIP ports,
/// <c>PORTNUM=2</c> and <c>PORTNUM=3</c>, A's port 2 linked to B's port 2
/// and 3 to 3. B's AGW monitor shows each frame it hears with the
/// <c>PORTNUM</c> it came in on (" 3:Fm ..."), so it shows which of A's
/// ports a frame went out on.
///
/// BPQ's AGW sends by a port's position in bpq32.cfg and reports heard
/// frames by <c>PORTNUM</c> - 1 (M0LTE/linbpq#78), so the two agree only
/// with ports numbered 1..N in config order and none hidden, as here. A
/// hidden port makes BPQ count more ports than it lists: DAPPS warns, and
/// never sends on a port past the list.
/// </summary>
[Collection(Name)]
[Trait("Category", "Integration")]
public sealed class LinbpqPortNumberIntegrationTests : IAsyncLifetime
{
    public const string Name = "Linbpq port numbers";

    private const string NodeA = "G9BPA", NodeB = "G9BPB";
    private const string ApplA = "G9BPA-9", ApplB = "G9BPB-9";
    private const int AgwA = 18101, AgwB = 18102;
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(90);

    private IContainer? a;
    private IContainer? b;
    private AirMonitor? air;
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        if (b is not null) await b.DisposeAsync();
        if (a is not null) await a.DisposeAsync();
    }

    [Fact]
    public async Task ThePortList_ShowsBpqsOwnNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        await StartNodesAsync(ct);
        var daemon = await StartDaemonAsync("bpq-pn-list", ApplA, AgwPortA, [], ct);

        var list = await daemon.Http.GetFromJsonAsync<JsonElement>("Bearer/agw/ports", ct);

        list.GetProperty("ports").EnumerateArray()
            .Select(p => (p.GetProperty("number").GetInt32(), p.GetProperty("description").GetString()))
            .Should().Equal((1, "Telnet"), (2, "AXIP two"), (3, "AXIP three"));
        list.GetProperty("warning").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ADatabaseFrom052_SendsOnTheSamePortsAfterTheUpgrade()
    {
        var ct = TestContext.Current.CancellationToken;
        await StartNodesAsync(ct);
        air = await AirMonitor.StartAsync([AirMonitor.Tap.Bpq("B", "127.0.0.1", AgwPortB)], ct);

        // B, a fresh install, hears on its port 2 and answers anything.
        var bDaemon = await StartDaemonAsync("bpq-up-b", ApplB, AgwPortB, [Channel("2")], ct);

        // 0.52's rows on A, counting from 0: neighbour B on byte 2 (BPQ's
        // port 3) and a channel on byte 1 (BPQ's port 2).
        var before = Environment.GetEnvironmentVariable("DAPPS_BEFORE_DAEMON_DLL");
        var aDaemon = await StartDaemonAsync("bpq-up-a", ApplA, AgwPortA, null, ct, seed: Seed052, daemonDll: before);

        if (!string.IsNullOrEmpty(before))
        {
            await ExpectPortsAsync(aDaemon, bDaemon, ct, phase: "0.52");
            air.Clear();
            await aDaemon.RestartAsync(ct, daemonDll: null);
            await aDaemon.LogInAsAdminAsync(ct);
        }

        await WaitForLogAsync(aDaemon, "Port numbers: converted to the node's own numbers", ct);
        await ExpectPortsAsync(aDaemon, bDaemon, ct, phase: "after the upgrade");

        var neighbours = await aDaemon.Http.GetFromJsonAsync<JsonElement>("Neighbours", ct);
        neighbours.EnumerateArray().Single().GetProperty("port").GetInt32().Should().Be(3, "byte 2 was BPQ's port 3");
        var channels = await aDaemon.Http.GetFromJsonAsync<JsonElement>("DiscoveryChannels", ct);
        channels.EnumerateArray().Single().GetProperty("channelKey").GetString().Should().Be("2");
        var config = await aDaemon.Http.GetFromJsonAsync<JsonElement>("Config", ct);
        config.GetProperty("defaultPort").GetInt32().Should().Be(1, "default byte 0 was BPQ's port 1");
    }

    [Fact]
    public async Task AHiddenPort_IsWarnedAbout_AndAPortPastTheListIsNeverUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        a = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config(NodeA, ApplA, AgwA, 1, hideTwo: true)), "/data/bpq32.cfg")
            .WithPortBinding(AgwA, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(AgwA))
            .Build();
        await a.StartAsync(ct);
        await Task.Delay(2000, ct);
        var daemon = await StartDaemonAsync("bpq-hidden", ApplA, AgwPortA, [], ct,
            seed: db => db.Insert(new DbNeighbour { Callsign = ApplB, BearerPort = 3 }));

        var list = await daemon.Http.GetFromJsonAsync<JsonElement>("Bearer/agw/ports", ct);
        list.GetProperty("warning").GetString().Should().Contain("counts 3 ports over AGW but lists 2").And.Contain("1 to N");
        list.GetProperty("ports").GetArrayLength().Should().Be(2);

        // Port 3 is past BPQ's list of 2: BPQ would connect a byte past its
        // visible ports on its first port. DAPPS calls nothing.
        await daemon.SubmitAsync("chat", ApplB, "past the list"u8.ToArray(), ct);
        await WaitForLogAsync(daemon, "the node has no port 3", ct);
        daemon.Log.Should().Contain("was not called");
        daemon.Log.Should().Contain("counts 3 ports over AGW but lists 2");
    }

    /// <summary>A beacons on its port 2 and B hears it there; a message to
    /// B goes on port 3.</summary>
    private async Task ExpectPortsAsync(DappsDaemon aDaemon, DappsDaemon bDaemon, CancellationToken ct, string phase)
    {
        // Heard since this phase began (the log may be the last phase's).
        var deadline = DateTime.UtcNow + Within;
        while (Heard(f => f.Contains($"Fm {ApplA} To DAPPS <UI", StringComparison.Ordinal)).Count == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{phase}: no beacon from A heard by B:\n{air!.Transcript()}\n{aDaemon.Tail()}");
            await Task.Delay(250, ct);
        }
        await WaitForLogAsync(bDaemon, $"heard {ApplA} via agw/2", ct);
        Heard(f => f.Contains($"Fm {ApplA} To DAPPS <UI", StringComparison.Ordinal))
            .Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.TrimStart().StartsWith("2:", StringComparison.Ordinal),
                $"{phase}: A beacons on BPQ's port 2:\n{air!.Transcript()}\n{aDaemon.Tail()}");

        var payload = Encoding.ASCII.GetBytes($"Hello B, {phase}");
        var already = (await bDaemon.InboundAsync("chat", ct)).Count;
        await aDaemon.SubmitAsync("chat", ApplB, payload, ct);
        (await bDaemon.WaitForInboundAsync("chat", already + 1, Within, ct)).Last().Payload.Should().Equal(payload);
        Heard(f => f.Contains($"Fm {ApplA} To {ApplB} <C C", StringComparison.Ordinal))
            .Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.TrimStart().StartsWith("3:", StringComparison.Ordinal),
                $"{phase}: A calls B on BPQ's port 3:\n{air!.Transcript()}\n{aDaemon.Tail()}");
    }

    private IReadOnlyList<string> Heard(Func<string, bool> which) => air!.HeardBy("B").Where(which).ToList();

    private static void Seed052(SQLite.SQLiteConnection db)
    {
        db.Insert(new DbNeighbour { Callsign = ApplB, BearerPort = 2 });
        db.CreateTable<DbDiscoveryChannel>();
        db.Insert(Channel("1"));
        db.CreateTable<DbSystemOption>();
        db.Insert(new DbSystemOption { Option = "Callsign", Value = ApplA });
        db.Insert(new DbSystemOption { Option = "NodeBearer", Value = "agw" });
        db.Insert(new DbSystemOption { Option = "DefaultBearerPort", Value = "0" });
    }

    private static DbDiscoveryChannel Channel(string key) => new()
    {
        Bearer = "agw", ChannelKey = key, LinkClass = LinkClass.VhfUhfFm,
        BeaconIntervalSeconds = 10, AdvertisedTtlSeconds = 600, CostHint = 1, Enabled = true,
    };

    private int AgwPortA => a!.GetMappedPublicPort(AgwA);
    private int AgwPortB => a!.GetMappedPublicPort(AgwB);

    private async Task<DappsDaemon> StartDaemonAsync(
        string name, string callsign, int agwPort, DbDiscoveryChannel[]? channels, CancellationToken ct,
        Action<SQLite.SQLiteConnection>? seed = null, string? daemonDll = null)
    {
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var daemon = await DappsDaemon.StartAsync(name, callsign, NodeAttachment.Agw("127.0.0.1", agwPort, 0), [], settings, ct,
            discoveryChannels: channels, seed: seed, daemonDll: daemonDll);
        running.Add(daemon);
        await daemon.SignInAsAdminAsync(ct);
        return daemon;
    }

    /// <summary>A and B, B in A's network namespace (as
    /// <see cref="TwoInstanceLinbpqFixture"/> does), A publishing both AGW
    /// ports.</summary>
    private async Task StartNodesAsync(CancellationToken ct)
    {
        a = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config(NodeA, ApplA, AgwA, 1, peer: (NodeB, ApplB))), "/data/bpq32.cfg")
            .WithPortBinding(AgwA, assignRandomHostPort: true)
            .WithPortBinding(AgwB, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(AgwA))
            .Build();
        await a.StartAsync(ct);
        var aId = a.Id;
        b = new ContainerBuilder(LinbpqIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config(NodeB, ApplB, AgwB, 2, peer: (NodeA, ApplA))), "/data/bpq32.cfg")
            .WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{aId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(AgwB))
            .Build();
        await b.StartAsync(ct);
        await Task.Delay(2000, ct);
    }

    /// <summary>
    /// PORTNUM 1 Telnet, then two AXIP ports, PORTNUM 2 and 3, each to the
    /// peer's port of the same number. <paramref name="side"/> picks the
    /// UDP and TCP ports, 1 for A and 2 for B. With <paramref name="hideTwo"/>,
    /// port 2 is hidden.
    /// </summary>
    private static string Config(string nodeCall, string applCall, int agwPort, int side,
        (string Node, string Appl)? peer = null, bool hideTwo = false)
    {
        var other = side == 1 ? 2 : 1;
        var config = new StringBuilder($"""
            SIMPLE=1
            NODECALL={nodeCall}
            NODEALIAS={nodeCall[2..]}
            LOCATOR=NONE
            NODESINTERVAL=1
            AGWPORT={agwPort}
            AGWSESSIONS=10
            AGWMASK=1
            APPLICATIONS=DAPPS
            APPL1CALL={applCall}
            APPL1ALIAS=DAPPS{side}

            PORT
             PORTNUM=1
             ID=Telnet
             DRIVER=Telnet
             CONFIG
             TCPPORT={18110 + side}
             HTTPPORT={18120 + side}
             NETROMPORT={18130 + side}
             FBBPORT={18140 + side}
             APIPORT={18150 + side}
             MAXSESSIONS=20
             USER=test,test,{nodeCall},,SYSOP
            ENDPORT

            """);
        foreach (var (num, name) in new[] { (2, "AXIP two"), (3, "AXIP three") })
        {
            config.Append($"""
                PORT
                 PORTNUM={num}
                 ID={name}
                 DRIVER=BPQAXIP
                 QUALITY=0
                {(hideTwo && num == 2 ? " HIDE=1\n" : "")} CONFIG
                 UDP {19100 + num * 10 + side}
                 BROADCAST DAPPS

                """);
            if (peer is { } p)
            {
                config.Append($" MAP {p.Node} 127.0.0.1 UDP {19100 + num * 10 + other} B\n");
                config.Append($" MAP {p.Appl} 127.0.0.1 UDP {19100 + num * 10 + other} B\n");
            }
            config.Append("ENDPORT\n\n");
        }
        return config.ToString();
    }

    private static async Task WaitForLogAsync(DappsDaemon daemon, string text, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Within;
        while (!daemon.Log.Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{daemon.Name} never logged \"{text}\" within {Within.TotalSeconds:F0}s\n{daemon.Tail()}");
            await Task.Delay(250, ct);
        }
    }
}

[CollectionDefinition(LinbpqPortNumberIntegrationTests.Name, DisableParallelization = true)]
public sealed class LinbpqPortNumberCollection;
