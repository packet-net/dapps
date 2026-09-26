using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="OutboundMessageManager.DoRun"/> with a fake backhaul
/// that captures the <see cref="BackhaulMessage"/> + <see cref="BackhaulRoute"/>
/// the manager hands off. Lets the test assert TTL decrement and routing
/// without standing up real BPQ or even a real DAPPSv1 session protocol -
/// the seam (Plan A0) makes those concerns the bearer's, not the manager's.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundMessageManagerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private FakeBackhaul backhaul = null!;
    private OutboundMessageManager manager = null!;
    private TestOptionsMonitor<SystemOptions> optionsMonitor = null!;
    private DatabaseRoutingContext routingContext = null!;
    private StaticRoutingAlgorithm routingAlgorithm = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-omm-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            // A neighbour entry alone is enough to route messages to
            // app@N0DEST (post-A2 resolver matches base callsigns).
            c.Insert(new DbNeighbour { Callsign = "N0DEST", BearerPort = 0 });
        }

        optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            DefaultBearerPort = 0,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        backhaul = new FakeBackhaul();
        routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        manager = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DoRun_MessageWithNoTtl_ForwardsWithNullTtlOnTheBackhaul()
    {
        InsertMessage(id: "noex001", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Ttl.Should().BeNull();
    }

    [Fact]
    public async Task DoRun_MessageWithTtl_ForwardsWithDecrementedTtl()
    {
        // 30s in queue; 60s ttl → backhaul should see ttl 25..30.
        InsertMessage(id: "fresh01", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-30));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Ttl.Should().BeInRange(25, 30);
    }

    [Fact]
    public async Task DoRun_ExpiredMessage_DropsWithoutCallingBackhaul()
    {
        InsertMessage(id: "expired1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-120));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        // Row should be deleted so it doesn't get retried.
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expired1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_ExactlyAtTtlBoundary_DropsAsExpired()
    {
        InsertMessage(id: "ontime1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-60));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("ontime1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_DestinationSsidDiffersFromNeighbourSsid_StillRoutesViaBaseCallsignMatch()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "ssid001",
                Payload = "x"u8.ToArray(),
                Salt = 1L,
                Destination = "app@N0DEST-7",
                SourceCallsign = "N0CALL",
                AdditionalProperties = "{}",
            });
        }

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        var sent = backhaul.Sent.Single();
        sent.Message.Destination.Should().Be("app@N0DEST-7");
        sent.Route.Callsign.Should().Be("N0DEST");
    }

    [Fact]
    public async Task DoRun_NoMatchingNeighbour_LeavesMessageUnforwarded()
    {
        InsertMessage(id: "noroute", ttl: null, createdAt: DateTime.UtcNow);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update messages set destination=? where id=?", "app@N0OTHER", "noroute");
        }

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        using var conn = DbInfo.GetConnection();
        var row = conn.Find<DbMessage>("noroute");
        row.Should().NotBeNull();
        row!.Forwarded.Should().BeFalse("no neighbour matched, message should sit in queue until ttl expires");
    }

    [Fact]
    public async Task DoRun_MixedExpiredAndFresh_DropsExpiredAndForwardsFresh()
    {
        InsertMessage(id: "expir02", ttl: 5, createdAt: DateTime.UtcNow.AddSeconds(-3600));
        InsertMessage(id: "fresh02", ttl: 600, createdAt: DateTime.UtcNow.AddSeconds(-1));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Id.Should().Be("fresh02");

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expir02").Should().BeNull();
        var fresh = c.Find<DbMessage>("fresh02");
        fresh.Should().NotBeNull();
        fresh!.Forwarded.Should().BeTrue();
    }

    [Fact]
    public async Task DoRun_BackhaulRejection_LeavesMessageUnforwardedForRetry()
    {
        InsertMessage(id: "reject1", ttl: null, createdAt: DateTime.UtcNow);
        backhaul.NextResult = BackhaulSendResult.Fail("simulated bearer error");

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("reject1")!.Forwarded.Should().BeFalse();
    }

    /// <summary>
    /// Regression test for a retry storm seen in the field: a destination
    /// that accepts the AX.25 connect but rejects at the application
    /// layer (e.g. BPQ's "No AGWPE Host Sessions available" instead of
    /// the DAPPSv1&gt; prompt) was re-dialled on every single 5s forwarder
    /// tick forever, since a failed send just leaves the message
    /// pending. <see cref="OutboundDestinationBackoff"/> should put the
    /// destination in cooldown after a failure so an immediate next tick
    /// (well within the schedule's fastest 10s tier) skips it instead.
    /// </summary>
    [Fact]
    public async Task DoRun_BackhaulRejection_SecondImmediateTick_SkipsRetryDuringCooldown()
    {
        InsertMessage(id: "reject2", ttl: null, createdAt: DateTime.UtcNow);
        backhaul.NextResult = BackhaulSendResult.Fail("simulated bearer error");

        await manager.DoRun(TestContext.Current.CancellationToken);
        backhaul.Sent.Should().ContainSingle("the first attempt always goes out");

        // Immediately tick again - real elapsed time here is milliseconds,
        // nowhere near the schedule's fastest (10s) tier.
        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle(
            "the destination is still in its post-failure cooldown, so this tick must not retry yet");
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("reject2")!.Forwarded.Should().BeFalse();
    }

    [Fact]
    public async Task DoRun_PassesNeighbourBearerPortToBackhaul()
    {
        // Update the seeded neighbour to a non-default bearer port.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update neighbours set BearerPort=? where callsign=?", 3, "N0DEST");
        }
        InsertMessage(id: "portmsg", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Single().Route.BearerPort.Should().Be(3);
    }

    // ── B4: cost-based resolver across DbDiscoveredPeer ─────────────

    [Fact]
    public async Task DoRun_NoManualNeighbour_PrefersCheapestFreshDiscoveredPeer()
    {
        // Same peer N0DEST heard on three RF / IP channels at the
        // LinkClassDefaults cost weights. With RF-first ordering,
        // VHF (1) wins over HF (5) wins over Internet (10). Internet
        // is intentionally last so the resolver doesn't pick a wired
        // bridge over a perfectly good RF link.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("delete from neighbours");
            var now = DateTime.UtcNow;
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "udp", "ip-bridge"),
                Callsign = "N0DEST", Bearer = "udp", ChannelKey = "ip-bridge",
                LinkClass = LinkClass.InternetIp,
                CostHint = LinkClassDefaults.CostHint(LinkClass.InternetIp),
                UdpEndpoint = "10.0.0.5:1881",
                TtlSeconds = 900, LastSeen = now,
            });
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "1"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "1",
                LinkClass = LinkClass.VhfUhfFm,
                CostHint = LinkClassDefaults.CostHint(LinkClass.VhfUhfFm),
                BearerPort = 1, TtlSeconds = 5400, LastSeen = now,
            });
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "3"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "3",
                LinkClass = LinkClass.Hf,
                CostHint = LinkClassDefaults.CostHint(LinkClass.Hf),
                BearerPort = 3, TtlSeconds = 86400, LastSeen = now,
            });
        }
        InsertMessage("rffirst", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        var route = backhaul.Sent.Single().Route;
        route.BearerPort.Should().Be(1,
            "VHF/UHF FM (RF, line-of-sight) is the project's preferred class - should beat HF and the IP bridge");
        route.UdpEndpoint.Should().BeNull("IP must not be picked when an RF route is fresh");
    }

    [Fact]
    public async Task DoRun_StaleRfPeer_FallsBackToFreshInternetBridge()
    {
        // Preferred RF channel has gone silent past its advertised TTL.
        // The resolver must fall back to the next-fresh option even
        // when that's an internet bridge - that's exactly why we keep
        // IP routes as a last resort. RF first when available, IP when
        // RF has dropped.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("delete from neighbours");
            var now = DateTime.UtcNow;
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "1"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "1",
                LinkClass = LinkClass.VhfUhfFm,
                CostHint = LinkClassDefaults.CostHint(LinkClass.VhfUhfFm),
                BearerPort = 1, TtlSeconds = 60,
                LastSeen = now.AddMinutes(-10), // ttl=60, last seen 10 min ago → stale
            });
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "udp", "ip-bridge"),
                Callsign = "N0DEST", Bearer = "udp", ChannelKey = "ip-bridge",
                LinkClass = LinkClass.InternetIp,
                CostHint = LinkClassDefaults.CostHint(LinkClass.InternetIp),
                UdpEndpoint = "10.0.0.5:1881",
                TtlSeconds = 900, LastSeen = now,
            });
        }
        InsertMessage("rfdropped", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        var route = backhaul.Sent.Single().Route;
        route.UdpEndpoint.Should().Be("10.0.0.5:1881",
            "RF route is stale - internet fallback is the right answer here");
    }

    [Fact]
    public async Task DoRun_ManualNeighbourWinsOverCheaperDiscoveredPeer()
    {
        // Operator sets a manual neighbour entry. Resolver MUST honour
        // that even if a cheaper discovered channel exists - it's an
        // explicit override.
        using (var c = DbInfo.GetConnection())
        {
            // Manual neighbour points at AGW bearer port 0 (the seeded one).
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "udp", "g1"),
                Callsign = "N0DEST", Bearer = "udp", ChannelKey = "g1",
                LinkClass = LinkClass.LanMulticast, CostHint = 1,
                UdpEndpoint = "10.0.0.5:1881",
                TtlSeconds = 600, LastSeen = DateTime.UtcNow,
            });
        }
        InsertMessage("manualwins", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        var route = backhaul.Sent.Single().Route;
        route.UdpEndpoint.Should().BeNull("the manual neighbour entry has no UdpEndpoint set");
        route.BearerPort.Should().Be(0, "the manual neighbour points at bearer port 0");
    }

    [Fact]
    public async Task DoRun_TiedCost_TieBreaksOnHops()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("delete from neighbours");
            var now = DateTime.UtcNow;
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "1"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "1",
                LinkClass = LinkClass.VhfUhfFm, CostHint = 5, Hops = 3,
                BearerPort = 1, TtlSeconds = 5400, LastSeen = now,
            });
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "2"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "2",
                LinkClass = LinkClass.VhfUhfFm, CostHint = 5, Hops = 0,
                BearerPort = 2, TtlSeconds = 5400, LastSeen = now,
            });
        }
        InsertMessage("tied", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Single().Route.BearerPort.Should().Be(2,
            "tied costs break on hop count - direct neighbour beats 3-hop relay");
    }

    // #178: the forwarder must not dial a peer that already has a session
    // open with us. AX.25 has one link per callsign pair; the SABM would
    // reset the live link at both ends and the caller would land on the
    // node prompt instead of DAPPSv1>.

    [Fact]
    public async Task DoRun_PeerHasASessionOpen_LeavesTheMessageQueuedAndDoesNotDial()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        var guarded = MakeManager(peers);
        InsertMessage(id: "busy001", ttl: null, createdAt: DateTime.UtcNow);

        using (peers.Acquire("N0DEST", "inbound"))
        {
            await guarded.DoRun(ct);
            backhaul.Sent.Should().BeEmpty("dialling into the open session would reset it");
            (await database.GetPendingOutboundMessages()).Should().ContainSingle(m => m.Id == "busy001",
                "a deferred message is still pending, not dropped or failed");
        }

        await guarded.DoRun(ct);
        backhaul.Sent.Should().ContainSingle(s => s.Message.Id == "busy001",
            "the first tick after the session ends dials as normal");
    }

    [Fact]
    public async Task DoRun_ADeferralIsNotAFailure_SoNoCooldownStarts()
    {
        var peers = new PeerSessionRegistry();
        var backoff = new OutboundDestinationBackoff();
        var guarded = MakeManager(peers, backoff);
        InsertMessage(id: "busy002", ttl: null, createdAt: DateTime.UtcNow);

        using (peers.Acquire("N0DEST", "outbound"))
        {
            await guarded.DoRun(TestContext.Current.CancellationToken);
        }

        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse(
            "a busy peer is not a failing one; a failure here would also feed the route-invalidation counters");
    }

    [Fact]
    public async Task DoRun_PeerHasASessionOpen_ADatagramRouteStillSends()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0UDP", UdpEndpoint = "127.0.0.1:4000" });
        }
        var peers = new PeerSessionRegistry();
        var guarded = MakeManager(peers);
        InsertMessage(id: "udp0001", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0UDP");

        using (peers.Acquire("N0UDP", "inbound"))
        {
            await guarded.DoRun(TestContext.Current.CancellationToken);
        }

        backhaul.Sent.Should().ContainSingle(s => s.Message.Id == "udp0001",
            "UDP is a datagram bearer with no AX.25 link to collide on");
    }

    [Fact]
    public async Task DoRun_FloodSkipsTheNeighbourWithASessionOpen_AndStillReachesTheOthers()
    {
        var peers = new PeerSessionRegistry();
        var flooding = new FloodingAlgorithm(
            new BackhaulRoute("N0BUSY", BearerPort: 0),
            new BackhaulRoute("N0FREE", BearerPort: 0));
        var guarded = new OutboundMessageManager(
            database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], flooding, routingContext,
            peerSessions: peers);
        InsertMessage(id: "flood01", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        using (peers.Acquire("N0BUSY", "inbound"))
        {
            await guarded.DoRun(TestContext.Current.CancellationToken);
        }

        backhaul.Sent.Select(s => s.Route.Callsign).Should().Equal("N0FREE");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty(
            "a flood is one-shot: the skipped copy is not retried, the same as a failed one");
    }

    [Fact]
    public async Task DoRun_BearerDeferredTheSend_MessageStaysQueued_AndNoCooldownStarts()
    {
        // #178 crossed connect: the bearer served the peer's session on
        // the link instead of pushing. Not a success, not a failure.
        var backoff = new OutboundDestinationBackoff();
        var guarded = MakeManager(new PeerSessionRegistry(), backoff);
        backhaul.NextResult = BackhaulSendResult.Defer("crossed connect with N0DEST: served its session instead");
        InsertMessage(id: "defer01", ttl: null, createdAt: DateTime.UtcNow);

        await guarded.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle("the bearer was asked to send");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(m => m.Id == "defer01");
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse("a deferral is not a failure");
    }

    // Batching: everything queued for one next hop goes out on one
    // session, not one session per message.

    [Fact]
    public async Task DoRun_SeveralMessagesForOneNeighbour_GoOutAsOneBatchInQueueOrder()
    {
        var batching = new BatchingFakeBackhaul();
        var m = MakeManager(batching);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "third01", ttl: null, createdAt: t0.AddSeconds(2));
        InsertMessage(id: "first01", ttl: null, createdAt: t0);
        InsertMessage(id: "secnd01", ttl: null, createdAt: t0.AddSeconds(1));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Should().ContainSingle();
        batching.Batches.Single().Route.Callsign.Should().Be("N0DEST");
        batching.Batches.Single().Ids.Should().Equal("first01", "secnd01", "third01");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_MessagesForTwoNeighbours_GetOneBatchEach()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0TWO", BearerPort = 0 });
        }
        var batching = new BatchingFakeBackhaul();
        var m = MakeManager(batching);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "dest001", ttl: null, createdAt: t0);
        InsertMessage(id: "two0001", ttl: null, createdAt: t0.AddSeconds(1), destination: "app@N0TWO");
        InsertMessage(id: "dest002", ttl: null, createdAt: t0.AddSeconds(2));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Select(b => (b.Route.Callsign, string.Join(",", b.Ids))).Should().Equal(
            ("N0DEST", "dest001,dest002"),
            ("N0TWO", "two0001"));
    }

    [Fact]
    public async Task DoRun_AMessageFailsPartWay_TheOnesBeforeStayForwardedAndTheRestStayQueued()
    {
        var backoff = new OutboundDestinationBackoff();
        var batching = new BatchingFakeBackhaul
        {
            ResultFor = msg => msg.Id == "fail002" ? BackhaulSendResult.Fail("link dropped") : BackhaulSendResult.Ok(),
        };
        var m = MakeManager(batching, backoff: backoff);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "okay001", ttl: null, createdAt: t0);
        InsertMessage(id: "fail002", ttl: null, createdAt: t0.AddSeconds(1));
        InsertMessage(id: "wait003", ttl: null, createdAt: t0.AddSeconds(2));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Single().Ids.Should().Equal(new[] { "okay001", "fail002" },
            "the batch stops at the failure; the third message is never handed out");
        (await database.GetPendingOutboundMessages()).Select(p => p.Id).Should().BeEquivalentTo("fail002", "wait003");
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue("a failure mid-batch is still a failure of the route");
    }

    [Fact]
    public async Task DoRun_MessageQueuedWhileTheBatchIsGoingOut_GoesOnTheSameBatch()
    {
        var batching = new BatchingFakeBackhaul();
        batching.OnSent = msg =>
        {
            if (msg.Id == "early01") InsertMessage(id: "late001", ttl: null, createdAt: DateTime.UtcNow);
        };
        var m = MakeManager(batching);
        InsertMessage(id: "early01", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Should().ContainSingle("a message queued mid-session must not cost a second session");
        batching.Batches.Single().Ids.Should().Equal("early01", "late001");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_MessageQueuedMidBatchForAnotherNeighbour_WaitsForTheNextRun()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0TWO", BearerPort = 0 });
        }
        var batching = new BatchingFakeBackhaul();
        batching.OnSent = msg =>
        {
            if (msg.Id == "dest001") InsertMessage(id: "two0001", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0TWO");
        };
        var m = MakeManager(batching);
        InsertMessage(id: "dest001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Should().ContainSingle();
        batching.Batches.Single().Ids.Should().Equal("dest001");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(p => p.Id == "two0001");

        await m.DoRun(TestContext.Current.CancellationToken);
        batching.Batches.Last().Route.Callsign.Should().Be("N0TWO");
        batching.Batches.Last().Ids.Should().Equal("two0001");
    }

    [Fact]
    public async Task DoRun_ASteadyStreamOfNewMessages_StopsBeingPickedUpAtTheLimit()
    {
        // One neighbour with traffic arriving as fast as it goes out must
        // not hold the forwarder (and every other neighbour) forever.
        var batching = new BatchingFakeBackhaul();
        var n = 0;
        batching.OnSent = _ => InsertMessage(id: $"strm{++n:D3}", ttl: null, createdAt: DateTime.UtcNow);
        var m = MakeManager(batching);
        InsertMessage(id: "start01", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Should().ContainSingle();
        batching.Batches.Single().Ids.Should().HaveCount(1 + OutboundMessageManager.MaxPickedUpPerBatch);
    }

    [Fact]
    public async Task DoRun_TtlIsWorkedOutWhenAMessageIsHandedOut_NotWhenTheRunStarted()
    {
        // The second message's ttl runs out while the first is being
        // sent. It must not go out with the ttl it had at the start.
        var batching = new BatchingFakeBackhaul { OnSent = _ => Thread.Sleep(1200) };
        var m = MakeManager(batching);
        InsertMessage(id: "slow001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));
        InsertMessage(id: "tight01", ttl: 2, createdAt: DateTime.UtcNow.AddMilliseconds(-500));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Single().Ids.Should().Equal("slow001");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(p => p.Id == "tight01",
            "an expired message isn't sent; the next run drops it");
    }

    [Fact]
    public async Task DoRun_NeighbourInCooldown_SkipsItsWholeBatch()
    {
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("N0DEST");
        var batching = new BatchingFakeBackhaul();
        var m = MakeManager(batching, backoff: backoff);
        InsertMessage(id: "cool001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));
        InsertMessage(id: "cool002", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-4));

        await m.DoRun(TestContext.Current.CancellationToken);

        batching.Batches.Should().BeEmpty();
        (await database.GetPendingOutboundMessages()).Should().HaveCount(2);
    }

    [Fact]
    public async Task DoRun_NeighbourHasASessionOpen_DefersItsWholeBatch()
    {
        var peers = new PeerSessionRegistry();
        var batching = new BatchingFakeBackhaul();
        var m = MakeManager(batching, peers: peers);
        InsertMessage(id: "busy101", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));
        InsertMessage(id: "busy102", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-4));

        using (peers.Acquire("N0DEST", "inbound"))
        {
            await m.DoRun(TestContext.Current.CancellationToken);
        }

        batching.Batches.Should().BeEmpty();
        (await database.GetPendingOutboundMessages()).Should().HaveCount(2);
    }

    [Fact]
    public async Task DoRun_AMessageHandedToAnOpenSessionTwice_IsSentOnce()
    {
        // A session held open sends in the background, so a second run
        // can hand it the same queued message again before the first
        // copy has gone. The claim at hand-out stops a second send.
        var holding = new HoldingFakeBackhaul();
        var m = MakeManager(holding);
        InsertMessage(id: "once001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);
        await m.DoRun(TestContext.Current.CancellationToken);
        holding.Batches.Should().HaveCount(2, "both runs found the message still queued and handed it over");

        var sent = new List<string>();
        foreach (var batch in holding.Batches)
        {
            while (await batch.NextAsync(TestContext.Current.CancellationToken) is { } message)
            {
                sent.Add(message.Id);
                await batch.CompleteAsync(message, BackhaulSendResult.Ok(), TimeSpan.Zero, TestContext.Current.CancellationToken);
            }
        }

        sent.Should().Equal("once001");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_ADatagramBearer_StillSendsMessageByMessage()
    {
        // FakeBackhaul doesn't override SendBatchAsync, so it gets the
        // interface's default: one SendAsync per message, same batch.
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "dgram01", ttl: null, createdAt: t0);
        InsertMessage(id: "dgram02", ttl: null, createdAt: t0.AddSeconds(1));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Select(s => s.Message.Id).Should().Equal("dgram01", "dgram02");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    private OutboundMessageManager MakeManager(PeerSessionRegistry peers, OutboundDestinationBackoff? backoff = null) =>
        new(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            destinationBackoff: backoff, peerSessions: peers);

    private OutboundMessageManager MakeManager(IDappsBackhaul bearer, OutboundDestinationBackoff? backoff = null, PeerSessionRegistry? peers = null) =>
        new(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            destinationBackoff: backoff, peerSessions: peers);

    /// <summary>Routes every message as a flood to a fixed set of neighbours.</summary>
    private sealed class FloodingAlgorithm(params BackhaulRoute[] routes) : IRoutingAlgorithm
    {
        public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct) =>
            Task.FromResult<RouteDecision>(new RouteDecision.FloodToNeighbours(routes, HopBudget: 3));
        public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private static void InsertMessage(string id, int? ttl, DateTime createdAt, string destination = "app@N0DEST")
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = id,
            Payload = Encoding.UTF8.GetBytes("payload-" + id),
            Salt = 1L,
            Destination = destination,
            SourceCallsign = "N0CALL",
            AdditionalProperties = "{}",
            Ttl = ttl,
            CreatedAt = createdAt,
        });
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>A bearer with a session held open: every batch is
    /// handed to it and kept, to be worked through by the test.</summary>
    private sealed class HoldingFakeBackhaul : IDappsBackhaul
    {
        public List<IBackhaulBatch> Batches { get; } = [];
        public bool CanHandle(BackhaulRoute route) => true;
        public bool TryHandToOpenSession(BackhaulRoute route, IBackhaulBatch batch)
        {
            Batches.Add(batch);
            return true;
        }
        public Task<BackhaulSendResult> SendAsync(BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct) =>
            throw new InvalidOperationException("everything goes to the held session");
    }

    /// <summary>
    /// A session bearer stand-in: overrides <see cref="IDappsBackhaul.SendBatchAsync"/>
    /// and records which messages each batch carried, in order.
    /// <see cref="OnSent"/> runs after each send, before its outcome is
    /// reported, which is where a test queues "late" traffic.
    /// </summary>
    private sealed class BatchingFakeBackhaul : IDappsBackhaul
    {
        public List<(BackhaulRoute Route, List<string> Ids)> Batches { get; } = [];
        public Func<BackhaulMessage, BackhaulSendResult> ResultFor { get; init; } = _ => BackhaulSendResult.Ok();
        public Action<BackhaulMessage>? OnSent { get; set; }

        public bool CanHandle(BackhaulRoute route) => true;

        public Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct) =>
            throw new InvalidOperationException("the forwarder should hand this bearer batches");

        public async Task SendBatchAsync(BackhaulRoute route, string localCallsign, IBackhaulBatch batch, CancellationToken ct)
        {
            var ids = new List<string>();
            Batches.Add((route, ids));
            while (await batch.NextAsync(ct) is { } message)
            {
                ids.Add(message.Id);
                var result = ResultFor(message);
                OnSent?.Invoke(message);
                await batch.CompleteAsync(message, result, TimeSpan.Zero, ct);
                if (!result.Accepted) return;
            }
        }
    }

    /// <summary>
    /// In-memory backhaul that captures every send for assertion. The
    /// shape of the capture is the seam payoff over the prior
    /// stream-mocking contraption - tests assert at the message level.
    /// </summary>
    private sealed class FakeBackhaul : IDappsBackhaul
    {
        public List<(BackhaulMessage Message, BackhaulRoute Route, string LocalCallsign)> Sent { get; } = [];
        public BackhaulSendResult NextResult { get; set; } = BackhaulSendResult.Ok();

        public bool CanHandle(BackhaulRoute route) => true;

        public Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
        {
            Sent.Add((message, route, localCallsign));
            return Task.FromResult(NextResult);
        }
    }
}
