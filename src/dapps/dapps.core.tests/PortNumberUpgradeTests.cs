using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// The one-off conversion of ports an older DAPPS saved, counting from 0,
/// into the node's own port numbers (#233, #370), and what is held until
/// it is done. The rule throughout: an existing node calls, beacons and
/// hears on exactly the radio ports it used before, except a peer heard on
/// a gapped XRouter, which is now called on the port it was heard on.
/// Each test starts from a 0.53 database: the tables as they were then
/// (no PortPending column), and the options it would hold.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class PortNumberUpgradeTests : IAsyncLifetime
{
    private string dbPath = null!;
    private readonly Dictionary<string, string?> env = [];

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ports-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        DbStartup.ReadEnvironment = k => env.GetValueOrDefault(k);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbStartup.ReadEnvironment = Environment.GetEnvironmentVariable;
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    // ── At start-up ───────────────────────────────────────────────

    [Fact]
    public void AFreshInstall_IsMarkedDone_WithDefaultPortOne()
    {
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        Option(c, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Done);
        Option(c, "DefaultPort").Should().Be("1");
        Option(c, "DefaultBearerPort").Should().BeNull("a fresh install never had it");
    }

    [Fact]
    public void AnRhpv2Node_ItsNeighbourAndProbePorts_GoUpByOneAtStartUp_SoTheSameRadioPortsAreCalled()
    {
        // 0.53 dialled neighbour port k as RHPv2 port "k+1" (XRouter's
        // PORT=k+1, pdn's port k+1): G6FCI's PORT=19 was entered as 18.
        SeedOld(bearer: "rhpv2", defaultBearerPort: 2);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('G6FCI-7', 18)");
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AAA-7', 0)");
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0BBB-7', null)");
            c.Execute("insert into probednodes (Callsign, LastBearerPort, Source) values ('M0CCC', 4, 'via:G6FCI-7')");
        }

        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Neighbour(db, "G6FCI-7").Should().Match<DbNeighbour>(n => n.BearerPort == 19 && n.PortPending != true);
        Neighbour(db, "M0AAA-7").BearerPort.Should().Be(1);
        Neighbour(db, "M0BBB-7").BearerPort.Should().BeNull("it uses the default, which moves with it");
        db.Find<DbProbedNode>("M0CCC")!.LastBearerPort.Should().Be(5);
        Option(db, "DefaultPort").Should().Be("3");
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Done, "nothing waits for the AGW connection");
    }

    [Fact]
    public void AnRhpv2Node_ItsAgwDiscoveryChannelsAndHeardPeers_WaitForTheAgwConnection()
    {
        SeedOld(bearer: "rhpv2", defaultBearerPort: 0);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into discoverychannels (Bearer, ChannelKey, Enabled) values ('agw', '1', 1)");
            c.Execute("insert into discoverychannels (Bearer, ChannelKey, Enabled) values ('udp', '239.1.1.1:1', 1)");
            c.Execute("insert into discoveredpeers (PeerKey, Callsign, Bearer, ChannelKey, BearerPort) values ('M0DDD-7|agw|1', 'M0DDD-7', 'agw', '1', 1)");
        }

        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        var channels = db.Query<DbDiscoveryChannel>("select * from discoverychannels order by Bearer");
        channels.Should().SatisfyRespectively(
            agw => agw.Should().Match<DbDiscoveryChannel>(c => c.ChannelKey == "1" && c.PortPending == true),
            udp => udp.PortPending.Should().NotBe(true));
        db.Query<DbDiscoveredPeer>("select * from discoveredpeers").Single().PortPending.Should().BeTrue();
        Option(db, "DefaultPort").Should().Be("1");
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Pending);
    }

    [Fact]
    public void AnAgwNode_KeepsItsPortsAsTheyAre_PendingTheAgwConnection_WithTheDefaultHeld()
    {
        SeedOld(bearer: "agw", defaultBearerPort: 1);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AHN-7', 2)");
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0BBB-7', null)");
        }

        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Neighbour(db, "M0AHN-7").Should().Match<DbNeighbour>(n => n.BearerPort == 2 && n.PortPending == true);
        Neighbour(db, "M0BBB-7").PortPending.Should().NotBe(true, "it has no port of its own; the default is held instead");
        Option(db, "DefaultPort").Should().Be("0", "held until the node's numbering is known");
        Option(db, "DefaultBearerPort").Should().Be("1", "kept for the conversion");
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Pending);
    }

    [Fact]
    public void TheMarker_MakesItRunOnce()
    {
        SeedOld(bearer: "rhpv2", defaultBearerPort: 0);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('G6FCI-7', 18)");
        }

        DbStartup.EnsureSchemaAndSeed();
        DbStartup.EnsureSchemaAndSeed();
        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Neighbour(db, "G6FCI-7").BearerPort.Should().Be(19);
    }

    // ── The old env var ───────────────────────────────────────────

    [Theory]
    [InlineData("0", "1")]
    [InlineData("2", "3")]
    public void TheOldEnvVar_CountsFromZero(string legacy, string expected)
    {
        env["DAPPS_DEFAULT_BEARER_PORT"] = legacy;

        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Option(db, "DefaultPort").Should().Be(expected);
    }

    [Fact]
    public void TheNewEnvVar_WinsOverTheOld()
    {
        env["DAPPS_DEFAULT_BEARER_PORT"] = "0";
        env["DAPPS_DEFAULT_PORT"] = "7";

        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Option(db, "DefaultPort").Should().Be("7");
    }

    [Fact]
    public void InEnvManagedMode_TheOldEnvVar_IsReappliedAtEveryStart_CountingFromZero()
    {
        // pdn-app.yaml up to 0.53 sets DAPPS_DEFAULT_BEARER_PORT: "0",
        // re-applied at every start: pdn's first port, "1" over RHPv2.
        env["DAPPS_ENV_MANAGED"] = "true";
        env["DAPPS_NODE_BEARER"] = "rhpv2";
        env["DAPPS_DEFAULT_BEARER_PORT"] = "0";
        SeedOld(bearer: "rhpv2", defaultBearerPort: 0);

        DbStartup.EnsureSchemaAndSeed();
        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        Option(db, "DefaultPort").Should().Be("1");
        DbStartup.IsEnvManaged("DefaultPort").Should().BeTrue();
    }

    // ── When the AGW connection answers ───────────────────────────

    [Fact]
    public void OnBpq_EachAgwByte_BecomesOneHigher_SoTheSameBytesAreSent()
    {
        SeedOld(bearer: "agw", defaultBearerPort: 1);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AHN-7', 0)");
            c.Execute("insert into probednodes (Callsign, LastBearerPort, Source) values ('M0CCC', 2, 'via:M0AHN-7')");
            c.Execute("insert into discoverychannels (Bearer, ChannelKey, Enabled) values ('agw', '1', 1)");
            c.Execute("insert into discoveredpeers (PeerKey, Callsign, Bearer, ChannelKey, BearerPort) values ('M0DDD-7|agw|1', 'M0DDD-7', 'agw', '1', 1)");
        }
        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        var result = PortNumberUpgrade.ConvertPending(db, NodePortMap.Plain(3))!;

        result.Should().Be(new PortNumberUpgrade.Result(1, 1, 1, 1, "2", 0));
        var plain = NodePortMap.Plain(3);
        plain.ToAgwPort(Neighbour(db, "M0AHN-7").BearerPort!.Value).Should().Be((byte)0, "the byte 0.53 sent");
        Neighbour(db, "M0AHN-7").PortPending.Should().NotBe(true);
        db.Find<DbProbedNode>("M0CCC")!.LastBearerPort.Should().Be(3);
        db.Query<DbDiscoveryChannel>("select * from discoverychannels").Single().ChannelKey.Should().Be("2");
        var peer = db.Query<DbDiscoveredPeer>("select * from discoveredpeers").Single();
        (peer.PeerKey, peer.ChannelKey, peer.BearerPort, peer.PortPending).Should().Be(("M0DDD-7|agw|2", "2", 2, (bool?)null));
        Option(db, "DefaultPort").Should().Be("2");
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Done);

        PortNumberUpgrade.ConvertPending(db, plain).Should().BeNull("it runs once");
        Neighbour(db, "M0AHN-7").BearerPort.Should().Be(1);
    }

    [Fact]
    public void OnAGappedXrouter_EachAgwByte_BecomesThePortNumberAtThatPosition()
    {
        // PORT=2, 7 and 19: 0.53 beaconed and heard channel byte 1 on
        // PORT=7, and learned a peer heard on PORT=19 as byte 2 (which it
        // then called on "3", #370).
        SeedOld(bearer: "rhpv2", defaultBearerPort: 0);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('G6FCI-7', 18)");
            c.Execute("insert into discoverychannels (Bearer, ChannelKey, Enabled) values ('agw', '1', 1)");
            c.Execute("insert into discoveredpeers (PeerKey, Callsign, Bearer, ChannelKey, BearerPort) values ('M0DDD-7|agw|2', 'M0DDD-7', 'agw', '2', 2)");
        }
        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        PortNumberUpgrade.ConvertPending(db, NodePortMap.Xrouter([2, 7, 19]))!.Held.Should().Be(0);

        Neighbour(db, "G6FCI-7").BearerPort.Should().Be(19, "converted at start-up, not again");
        db.Query<DbDiscoveryChannel>("select * from discoverychannels").Single().ChannelKey.Should().Be("7");
        var peer = db.Query<DbDiscoveredPeer>("select * from discoveredpeers").Single();
        (peer.ChannelKey, peer.BearerPort).Should().Be(("19", 19), "called on the port it was heard on now");
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Done);
    }

    [Fact]
    public void AnAgwByteTheNodeNoLongerHas_StaysHeld()
    {
        SeedOld(bearer: "agw", defaultBearerPort: 0);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AAA-7', 0)");
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0ZZZ-7', 5)");
        }
        DbStartup.EnsureSchemaAndSeed();

        using var db = DbInfo.GetConnection();
        var result = PortNumberUpgrade.ConvertPending(db, NodePortMap.Xrouter([2, 7]))!;

        (result.Neighbours, result.Held, result.DefaultPort).Should().Be((1, 1, "2"));
        Neighbour(db, "M0AAA-7").BearerPort.Should().Be(2);
        Neighbour(db, "M0ZZZ-7").Should().Match<DbNeighbour>(n => n.BearerPort == 5 && n.PortPending == true);
        Option(db, PortNumberUpgrade.MarkerKey).Should().Be(PortNumberUpgrade.Pending);
    }

    [Fact]
    public async Task TheService_ConvertsOnTheFirstAnswer_AndReloadsTheOptions()
    {
        SeedOld(bearer: "agw", defaultBearerPort: 1);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AHN-7', 0)");
        }
        DbStartup.EnsureSchemaAndSeed();
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        store.CurrentValue.DefaultPort.Should().Be(0);
        var service = new PortNumberUpgradeService(new FixedPortMaps(NodePortMap.Plain()), store, NullLogger<PortNumberUpgradeService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        service.Pending.Should().BeTrue();
        service.HeldReason.Should().NotBeNull();

        await service.ConvertAsync(null!);

        service.Pending.Should().BeFalse();
        service.HeldReason.Should().BeNull();
        store.CurrentValue.DefaultPort.Should().Be(2);
        using var db = DbInfo.GetConnection();
        Neighbour(db, "M0AHN-7").BearerPort.Should().Be(1);
    }

    [Fact]
    public async Task TheService_WithAnXrouterItCantRead_HoldsEverything_AndSaysWhy()
    {
        SeedOld(bearer: "agw", defaultBearerPort: 1);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort) values ('M0AHN-7', 0)");
        }
        DbStartup.EnsureSchemaAndSeed();
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = new PortNumberUpgradeService(new FixedPortMaps(null), store, NullLogger<PortNumberUpgradeService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.ConvertAsync(null!);

        service.Pending.Should().BeTrue();
        service.HeldReason.Should().Contain("RHPPORT");
        using var db = DbInfo.GetConnection();
        Neighbour(db, "M0AHN-7").Should().Match<DbNeighbour>(n => n.BearerPort == 0 && n.PortPending == true);
    }

    // ── What is held meanwhile ────────────────────────────────────

    [Fact]
    public void AHeldNeighbour_OrOneOnAHeldDefault_IsNotTakenByTheNodeBackhaul()
    {
        var held = RouteBuilder.FromNeighbour(new DbNeighbour { Callsign = "M0AHN-7", BearerPort = 0, PortPending = true }, 1);
        var onDefault = RouteBuilder.FromNeighbour(new DbNeighbour { Callsign = "M0BBB-7" }, 0);
        var fine = RouteBuilder.FromNeighbour(new DbNeighbour { Callsign = "M0CCC-7", BearerPort = 2 }, 0);
        var udp = RouteBuilder.FromNeighbour(new DbNeighbour { Callsign = "M0DDD-7", UdpEndpoint = "h:1" }, 0);

        (held.PortHeld, onDefault.PortHeld, fine.PortHeld, udp.PortHeld).Should().Be((true, true, false, false));
        var backhaul = new Dappsv1SessionBackhaul(new NeverTransport(), NullLoggerFactory.Instance);
        backhaul.CanHandle(held).Should().BeFalse();
        backhaul.CanHandle(onDefault).Should().BeFalse();
        backhaul.CanHandle(fine).Should().BeTrue();
        RouteBuilder.PortOf(new DbNeighbour { BearerPort = 4, PortPending = true }, 1).Should().BeNull();
        RouteBuilder.PortOf(new DbNeighbour(), 0).Should().BeNull();
        RouteBuilder.PortOf(new DbNeighbour(), 3).Should().Be(3);
    }

    [Fact]
    public async Task ResavingAHeldNeighboursSamePort_KeepsItHeld_ButANewPortIsTheNodesNumber()
    {
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into neighbours (Callsign, BearerPort, PortPending) values ('M0AHN-7', 0, 1)");
        }
        var database = new Database(NullLogger<Database>.Instance, new MutableOptionsMonitor<SystemOptions>(new SystemOptions()));

        // A form loaded before the conversion, saved for another field.
        await database.UpsertNeighbour("M0AHN-7", 0, compressionEnabled: true);
        (await database.GetNeighbour("M0AHN-7"))!.PortPending.Should().BeTrue();

        await database.UpsertNeighbour("M0AHN-7", 3);
        (await database.GetNeighbour("M0AHN-7"))!.Should().Match<DbNeighbour>(n => n.BearerPort == 3 && n.PortPending != true);
    }

    [Fact]
    public async Task AHeldChannel_NeitherRunsNorSolicits_AndKeepsItsMeaningWhenResaved()
    {
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("insert into discoverychannels (Bearer, ChannelKey, Enabled, LinkClass, PortPending) values ('agw', '1', 1, 1, 1)");
        }
        var database = new Database(NullLogger<Database>.Instance, new MutableOptionsMonitor<SystemOptions>(new SystemOptions()));
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel { Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.Hf });
        (await database.GetDiscoveryChannels()).Single().Should().Match<DbDiscoveryChannel>(ch => ch.PortPending == true && ch.LinkClass == LinkClass.Hf);

        var options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G5ALF-3" });
        var discovery = new DiscoveryService(database, options, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<DiscoveryService>.Instance);
        var controller = new DiscoveryChannelsController(database, discovery);
        var id = (await database.GetDiscoveryChannels()).Single().Id;
        (await controller.Solicit(id, TestContext.Current.CancellationToken)).Should().BeOfType<ConflictObjectResult>();
        (await controller.List()).Single().PortPending.Should().BeTrue();
    }

    [Fact]
    public async Task AnAgwChannelKey_IsTheNodesPortNumber_FromOne()
    {
        DbStartup.EnsureSchemaAndSeed();
        var database = new Database(NullLogger<Database>.Instance, new MutableOptionsMonitor<SystemOptions>(new SystemOptions()));
        var options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G5ALF-3" });
        var controller = new DiscoveryChannelsController(database,
            new DiscoveryService(database, options, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<DiscoveryService>.Instance));

        (await controller.Upsert(new DiscoveryChannelModel(0, "agw", "0", "VhfUhfFm"))).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Upsert(new DiscoveryChannelModel(0, "agw", "x", "VhfUhfFm"))).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Upsert(new DiscoveryChannelModel(0, "agw", "19", "VhfUhfFm"))).Should().BeOfType<NoContentResult>();
        (await controller.Upsert(new DiscoveryChannelModel(0, "udp", "239.1.1.1:1", "VhfUhfFm"))).Should().BeOfType<NoContentResult>();
    }

    // ── The REST field ────────────────────────────────────────────

    [Fact]
    public async Task TheNeighboursPort_IsTheNodesNumber_AndTheOldBearerPortCountsFromZero()
    {
        DbStartup.EnsureSchemaAndSeed();
        var database = new Database(NullLogger<Database>.Instance, new MutableOptionsMonitor<SystemOptions>(new SystemOptions()));
        var controller = new NeighboursController(database);

        (await controller.Upsert(new NeighbourModel("G6FCI-7", Port: 19))).Should().BeOfType<NoContentResult>();
        (await controller.Upsert(new NeighbourModel("M0AHN-7", Port: null, BearerPort: 0))).Should().BeOfType<NoContentResult>();
        (await controller.Upsert(new NeighbourModel("M0BBB-7", Port: 4, BearerPort: 0))).Should().BeOfType<NoContentResult>();
        (await controller.Upsert(new NeighbourModel("M0CCC-7", Port: 0))).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Upsert(new NeighbourModel("M0DDD-7", Port: null, BearerPort: -1))).Should().BeOfType<BadRequestObjectResult>();

        var listed = (await controller.List()).ToDictionary(n => n.Callsign);
        listed["G6FCI-7"].Port.Should().Be(19);
        listed["M0AHN-7"].Port.Should().Be(1, "BearerPort 0 was the node's first port");
        listed["M0BBB-7"].Port.Should().Be(4, "Port wins");
        listed.Values.Should().OnlyContain(n => n.BearerPort == null, "the old name is taken, never sent");
        listed.Should().NotContainKey("M0CCC-7");
        System.Text.Json.JsonSerializer.Serialize(listed["G6FCI-7"], System.Text.Json.JsonSerializerOptions.Web)
            .Should().Contain("\"port\":19").And.NotContain("bearerPort");
    }

    private static void SeedOld(string bearer, int defaultBearerPort)
    {
        using var c = DbInfo.GetConnection();
        // The 0.53 shapes of the tables that hold ports: no PortPending.
        c.Execute("create table neighbours (Id integer primary key autoincrement not null, Callsign varchar, BearerPort integer)");
        c.Execute("create table probednodes (Callsign varchar primary key not null, LastBearerPort integer, Source varchar)");
        c.Execute("create table discoverychannels (Id integer primary key autoincrement not null, Bearer varchar, ChannelKey varchar, Enabled integer, LinkClass integer)");
        c.Execute("create table discoveredpeers (PeerKey varchar primary key not null, Callsign varchar, Bearer varchar, ChannelKey varchar, BearerPort integer)");
        c.CreateTable<DbSystemOption>();
        c.Insert(new DbSystemOption { Option = "Callsign", Value = "G5ALF-3" });
        c.Insert(new DbSystemOption { Option = "NodeBearer", Value = bearer });
        c.Insert(new DbSystemOption { Option = "DefaultBearerPort", Value = defaultBearerPort.ToString() });
    }

    private static string? Option(SQLiteConnection c, string key) => c.Find<DbSystemOption>(key)?.Value;

    private static DbNeighbour Neighbour(SQLiteConnection c, string callsign) =>
        c.Query<DbNeighbour>("select * from neighbours where Callsign = ?", callsign).Single();

    private sealed class NeverTransport : dapps.client.Transport.IDappsOutboundTransport
    {
        public Task<dapps.client.Transport.IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken) =>
            throw new InvalidOperationException("not called");
    }

}
