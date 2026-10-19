using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// #233 and #370 on real XRouter (the pinned 505p), with gapped port numbers
/// written out of order: <c>PORT=19</c>, <c>PORT=2</c>, <c>PORT=7</c>, each a
/// loopback, so a frame sent on a port comes back in on the same port and
/// two daemons on the one node hear each other there.
///
/// XRouter's AGW counts the ports from 0 in order of their numbers, so its
/// monitor shows <c>PORT=2</c> as <c>0:</c>, <c>PORT=7</c> as <c>1:</c> and
/// <c>PORT=19</c> as <c>2:</c> (probed on 505p, PR #369 and #370); RHPv2
/// takes the numbers themselves. An <see cref="AirMonitor"/> on the node's
/// AGW port records what went on air, and on which port.
///
/// Proves: a neighbour on 19 is called on <c>PORT=19</c>; a channel on 7
/// beacons and hears on <c>PORT=7</c>; a peer heard on 7 is learned as 7
/// and called on <c>PORT=7</c> (on 0.52 it would have been called on "8",
/// which doesn't exist); and a 0.52 database calls and beacons on the same
/// ports after the upgrade as 0.52 did. With
/// <c>DAPPS_BEFORE_DAEMON_DLL</c> pointing at a 0.52 build, the last test
/// also runs that build first, on the same database, and compares.
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterPortNumberIntegrationTests : IAsyncLifetime
{
    private const string CallA = "G9PNA-7";
    private const string CallB = "G9PNB-7";
    private const int InsideAgw = 8000, InsideRhp = 9000;

    /// <summary>The monitor's port label for each <c>PORT=</c> number.</summary>
    private static readonly Dictionary<int, string> Monitor = new() { [2] = "0:Fm", [7] = "1:Fm", [19] = "2:Fm" };

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(90);

    private IContainer? node;
    private AirMonitor? air;
    private readonly List<DappsDaemon> running = [];

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        node = new ContainerBuilder(XrouterIntegrationFixture.Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config()), "/data/XROUTER.CFG")
            .WithResourceMapping(Encoding.UTF8.GetBytes("0.0.0.0/0 1\n"), "/data/ACCESS.SYS")
            .WithPortBinding(InsideAgw, assignRandomHostPort: true)
            .WithPortBinding(InsideRhp, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideRhp))
            .Build();
        await node.StartAsync(ct);
        await Task.Delay(2000, ct);
        air = await AirMonitor.StartAsync([AirMonitor.Tap.Bpq("X", "127.0.0.1", AgwPort)], ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        if (node is not null) await node.DisposeAsync();
    }

    private int AgwPort => node!.GetMappedPublicPort(InsideAgw);
    private int RhpPort => node!.GetMappedPublicPort(InsideRhp);

    [Fact]
    public async Task TheNodesOwnPortNumbers_AreUsedForCallsBeaconsAndLearnedPeers_GapsAndAll()
    {
        var ct = TestContext.Current.CancellationToken;
        // A knows B on PORT=19; B knows nobody and learns A from its beacon
        // on PORT=7. Fresh installs: ports are the node's numbers.
        var a = await StartAsync("xr-pn-a", CallA, [Neighbour(CallB, 19)], channel: "7", ct);
        var b = await StartAsync("xr-pn-b", CallB, [], channel: "7", ct);

        // Beacons go out and are heard on PORT=7, nowhere else.
        await WaitForLogAsync(b, $"heard {CallA} via agw/7", ct);
        await WaitForLogAsync(a, $"heard {CallB} via agw/7", ct);
        var beacons = Air(f => f.Contains("To DAPPS <UI", StringComparison.Ordinal));
        beacons.Should().NotBeEmpty($"beacons went on air:\n{air!.Transcript()}");
        beacons.Should().OnlyContain(f => f.Contains(Monitor[7], StringComparison.Ordinal), $"beacons go on PORT=7:\n{air!.Transcript()}");

        var learned = await PeersAsync(b, ct);
        learned.Should().ContainSingle(p => p.Callsign == CallA)
            .Which.Should().Match<Peer>(p => p.BearerPort == 7 && p.ChannelKey == "7", "the port it was heard on, as XRouter numbers it");

        // B calls A on PORT=7, where it heard it: 0.52 called "8" (#370).
        var toA = "Hello A, heard your beacon"u8.ToArray();
        await b.SubmitAsync("chat", CallA, toA, ct);
        (await a.WaitForInboundAsync("chat", 1, Within, ct)).Single().Payload.Should().Equal(toA);
        Sabms(CallB, CallA).Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.Contains(Monitor[7], StringComparison.Ordinal),
            $"B calls A on PORT=7:\n{air!.Transcript()}\n{b.Tail()}");
        b.Log.Should().NotContain("Invalid port");

        // B's session ends first: A's mail would otherwise ride on it.
        await WaitForAirAsync(f => f.Contains($"Fm {CallB} To {CallA} <DISC", StringComparison.Ordinal)
            || f.Contains($"Fm {CallA} To {CallB} <DISC", StringComparison.Ordinal), ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // A calls its neighbour B on PORT=19, by that number.
        var toB = "Hello B, on nineteen"u8.ToArray();
        await a.SubmitAsync("chat", CallB, toB, ct);
        (await b.WaitForInboundAsync("chat", 1, Within, ct)).Single().Payload.Should().Equal(toB);
        Sabms(CallA, CallB).Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.Contains(Monitor[19], StringComparison.Ordinal),
            $"A calls B on PORT=19:\n{air!.Transcript()}\n{a.Tail()}");
        a.Log.Should().Contain("the node's ports are numbered XRouter ports [2,7,19]");
    }

    [Fact]
    public async Task ADatabaseFrom052_CallsAndBeaconsOnTheSamePortsAfterTheUpgrade()
    {
        var ct = TestContext.Current.CancellationToken;
        // 0.52 rows, counting from 0: neighbour B saved as 18 (dialled as
        // RHPv2 "19", PORT=19), the default 0 ("1", which doesn't exist
        // here), and an AGW channel on byte 1, which 0.52 beaconed ('M' on
        // AGW byte 1) and heard on XRouter's second-lowest port, PORT=7.
        var b = await StartAsync("xr-up-b", CallB, [], channel: "7", ct);
        var before = Environment.GetEnvironmentVariable("DAPPS_BEFORE_DAEMON_DLL");
        var a = await StartAsync("xr-up-a", CallA, [], channel: null, ct, seed: Seed052, daemonDll: before);

        if (!string.IsNullOrEmpty(before))
        {
            // The release before, on that database: where it calls and beacons.
            await ExpectPortsAsync(a, b, ct, phase: "0.52");
            air!.Clear();
            await a.RestartAsync(ct, daemonDll: null);
            await a.LogInAsAdminAsync(ct);
        }

        // This build, on the same database: the same ports.
        await WaitForLogAsync(a, "Port numbers: converted to the node's own numbers", ct);
        await ExpectPortsAsync(a, b, ct, phase: "after the upgrade");

        var neighbours = await a.Http.GetFromJsonAsync<JsonElement>("Neighbours", ct);
        neighbours.EnumerateArray().Single().GetProperty("port").GetInt32().Should().Be(19, "18, counting from 0, was PORT=19");
        var channels = await a.Http.GetFromJsonAsync<JsonElement>("DiscoveryChannels", ct);
        channels.EnumerateArray().Single().GetProperty("channelKey").GetString().Should().Be("7");
    }

    /// <summary>A beacons on PORT=7 and B hears it there; a message to B
    /// goes on PORT=19.</summary>
    private async Task ExpectPortsAsync(DappsDaemon a, DappsDaemon b, CancellationToken ct, string phase)
    {
        // On the air since this phase began (the log may be the last phase's).
        await WaitForAirAsync(f => f.Contains($"Fm {CallA} To DAPPS <UI", StringComparison.Ordinal), ct);
        await WaitForLogAsync(b, $"heard {CallA} via agw/7", ct);
        Air(f => f.Contains($"Fm {CallA} To DAPPS <UI", StringComparison.Ordinal))
            .Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.Contains(Monitor[7], StringComparison.Ordinal),
                $"{phase}: A beacons on PORT=7:\n{air!.Transcript()}\n{a.Tail()}");

        var payload = Encoding.ASCII.GetBytes($"Hello B, {phase}");
        var already = (await b.InboundAsync("chat", ct)).Count;
        await a.SubmitAsync("chat", CallB, payload, ct);
        (await b.WaitForInboundAsync("chat", already + 1, Within, ct)).Last().Payload.Should().Equal(payload);
        Sabms(CallA, CallB).Should().NotBeEmpty($"it went on air:\n{air!.Transcript()}").And.OnlyContain(f => f.Contains(Monitor[19], StringComparison.Ordinal),
            $"{phase}: A calls B on PORT=19:\n{air!.Transcript()}\n{a.Tail()}");
    }

    private static void Seed052(SQLite.SQLiteConnection db)
    {
        db.Insert(new DbNeighbour { Callsign = CallB, BearerPort = 18 });
        db.CreateTable<DbDiscoveryChannel>();
        db.Insert(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm,
            BeaconIntervalSeconds = 10, AdvertisedTtlSeconds = 600, CostHint = 1, Enabled = true,
        });
        db.CreateTable<DbSystemOption>();
        db.Insert(new DbSystemOption { Option = "Callsign", Value = CallA });
        db.Insert(new DbSystemOption { Option = "NodeBearer", Value = "rhpv2" });
        db.Insert(new DbSystemOption { Option = "DefaultBearerPort", Value = "0" });
    }

    private async Task<DappsDaemon> StartAsync(
        string name, string callsign, DbNeighbour[] neighbours, string? channel, CancellationToken ct,
        Action<SQLite.SQLiteConnection>? seed = null, string? daemonDll = null)
    {
        // The fixtures count ports from 0 (DappsDaemon seeds them one
        // higher); these tests seed the node's own numbers themselves.
        var nodeAt = NodeAttachment.Rhp("127.0.0.1", RhpPort, bearerPort: 0);
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_AGW_PORT"] = AgwPort.ToString(),
            ["DAPPS_SESSION_TAIL_SECONDS"] = "0",
        };
        DbDiscoveryChannel[]? channels = channel is null ? null :
        [
            new DbDiscoveryChannel
            {
                Bearer = "agw", ChannelKey = channel, LinkClass = LinkClass.VhfUhfFm,
                BeaconIntervalSeconds = 10, AdvertisedTtlSeconds = 600, CostHint = 1,
            },
        ];
        void Seed(SQLite.SQLiteConnection db)
        {
            foreach (var n in neighbours) db.Insert(n);
            seed?.Invoke(db);
        }
        var daemon = await DappsDaemon.StartAsync(name, callsign, nodeAt, [], settings, ct,
            discoveryChannels: channels, seed: Seed, daemonDll: daemonDll);
        running.Add(daemon);
        await daemon.SignInAsAdminAsync(ct);
        return daemon;
    }

    private static DbNeighbour Neighbour(string callsign, int port) => new() { Callsign = callsign, BearerPort = port };

    private IReadOnlyList<string> Air(Func<string, bool> which) => air!.HeardBy("X").Where(which).ToList();

    /// <summary>Connect requests from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private IReadOnlyList<string> Sabms(string from, string to) =>
        Air(f => f.Contains($"Fm {from} To {to} <SABM", StringComparison.Ordinal)
            || f.Contains($"Fm {from} To {to} <C C", StringComparison.Ordinal));

    private sealed record Peer(string Callsign, string ChannelKey, int? BearerPort);

    private static async Task<IReadOnlyList<Peer>> PeersAsync(DappsDaemon daemon, CancellationToken ct)
    {
        var snapshot = await daemon.Http.GetFromJsonAsync<JsonElement>("Operational?full=true", ct);
        return [.. snapshot.GetProperty("tables").GetProperty("discoveredPeers").EnumerateArray().Select(p => new Peer(
            p.GetProperty("callsign").GetString()!,
            p.GetProperty("channelKey").GetString()!,
            p.GetProperty("bearerPort").ValueKind == JsonValueKind.Null ? null : p.GetProperty("bearerPort").GetInt32()))];
    }

    private async Task WaitForAirAsync(Func<string, bool> which, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Within;
        while (Air(which).Count == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"not heard on air within {Within.TotalSeconds:F0}s:\n{air!.Transcript()}");
            await Task.Delay(250, ct);
        }
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

    /// <summary>Three loopback ports, numbered 19, 2 and 7 and written in
    /// that order.</summary>
    private static string Config()
    {
        var config = new StringBuilder()
            .Append("DNS=8.8.8.8\n")
            .Append("NODECALL=G9PNX-1\n")
            .Append("NODEALIAS=PNX\n")
            .Append("LOCATOR=IO91PM\n")
            .Append("CONSOLECALL=G9PNX\n")
            .Append($"AGWPORT={InsideAgw}\n")
            .Append($"RHPPORT={InsideRhp}\n");
        for (var i = 1; i <= 3; i++)
        {
            config.Append($"INTERFACE={i}\n\tTYPE=LOOPBACK\n\tPROTOCOL=KISS\n\tMTU=256\nENDINTERFACE\n");
        }
        foreach (var (number, iface) in new[] { (19, 3), (2, 1), (7, 2) })
        {
            config.Append($"PORT={number}\n\tID=\"Port {number}\"\n\tINTERFACENUM={iface}\nENDPORT\n");
        }
        return config.ToString();
    }
}
