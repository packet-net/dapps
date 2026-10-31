using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            // A neighbour entry alone is enough to route messages to
            // app@N0DEST (post-A2 resolver matches base callsigns).
            c.Insert(new DbNeighbour { Callsign = "N0DEST", BearerPort = 1 });
        }

        optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            DefaultPort = 1,
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
    public async Task DoRun_ARowThatThrows_DoesNotStopTheOthersGoing()
    {
        // #284: an exception for one message used to escape the loop
        // before any batch was sent, so nothing went, on every run.
        // A trigger makes dropping this one expired row fail.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("create trigger poison before insert on dropped_messages when new.Id = 'poison1' begin select raise(abort, 'poisoned'); end");
        }
        InsertMessage(id: "poison1", ttl: 5, createdAt: DateTime.UtcNow.AddSeconds(-3600));
        InsertMessage(id: "fresh03", ttl: 600, createdAt: DateTime.UtcNow.AddSeconds(-1));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Id.Should().Be("fresh03");
        using var check = DbInfo.GetConnection();
        check.Find<DbMessage>("poison1").Should().NotBeNull("the failed drop was rolled back, not half done");
        check.Find<DbDroppedMessage>("poison1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_AnExpiredRowAlsoInTheDroppedTable_IsDroppedAndTheRestGo()
    {
        // #284: a crash between the copy and the delete, or an old drop
        // of the same id, leaves the id in dropped_messages already.
        InsertMessage(id: "both003", ttl: 5, createdAt: DateTime.UtcNow.AddSeconds(-3600));
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbDroppedMessage
            {
                Id = "both003", Payload = "old"u8.ToArray(), Destination = "app@N0DEST",
                SourceCallsign = "N0CALL", AdditionalProperties = "{}",
                CreatedAt = DateTime.UtcNow.AddDays(-1), DroppedAt = DateTime.UtcNow.AddDays(-1), Reason = "refused: old",
            });
        }
        InsertMessage(id: "fresh04", ttl: 600, createdAt: DateTime.UtcNow.AddSeconds(-1));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Id.Should().Be("fresh04");
        using var check = DbInfo.GetConnection();
        check.Find<DbMessage>("both003").Should().BeNull();
        check.Find<DbDroppedMessage>("both003")!.Reason.Should().Be("ttl-expired");
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
    public async Task DoRun_APeerHeardBeforeAnUpgrade_IsNotRoutedTo_UntilItsPortIsConverted()
    {
        // #370: its port is still an AGW byte counted from 0 until the
        // node's AGW connection answers; routed now, it could go out on
        // another radio port. The cheaper channel is the held one.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("delete from neighbours");
            var now = DateTime.UtcNow;
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "1"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "1",
                LinkClass = LinkClass.VhfUhfFm, CostHint = 1,
                BearerPort = 1, PortPending = true, TtlSeconds = 5400, LastSeen = now,
            });
            c.Insert(new DbDiscoveredPeer
            {
                PeerKey = DbDiscoveredPeer.MakeKey("N0DEST", "agw", "3"),
                Callsign = "N0DEST", Bearer = "agw", ChannelKey = "3",
                LinkClass = LinkClass.Hf, CostHint = 5,
                BearerPort = 3, TtlSeconds = 86400, LastSeen = now,
            });
        }
        InsertMessage("heldpeer", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle().Which.Route.BearerPort.Should().Be(3, "the held row is skipped");
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
        route.BearerPort.Should().Be(1, "the manual neighbour points at port 1");
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

    [Fact]
    public async Task DoRun_AFailureWithAMinimumCooldown_KeepsTheNeighbourInCooldownAtLeastThatLong()
    {
        // #204: the bearer knows the peer's own call has just taken the
        // link and needs time to give up; the ramp's first step is 10 s.
        var backoff = new OutboundDestinationBackoff(spread: 0);
        var guarded = MakeManager(new PeerSessionRegistry(), backoff);
        backhaul.NextResult = BackhaulSendResult.Fail("N0DEST hung up before the exchange", TimeSpan.FromSeconds(50));
        InsertMessage(id: "cutoff1", ttl: null, createdAt: DateTime.UtcNow);

        await guarded.DoRun(TestContext.Current.CancellationToken);

        backoff.IsInCooldown("N0DEST", out var until).Should().BeTrue();
        until.Should().BeOnOrAfter(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45), "the bearer's minimum, not the ramp's 10 s");
    }

    [Fact]
    public async Task DoRun_AFloodCopyFailingWithAMinimumCooldown_KeepsThatNeighbourInCooldownAtLeastThatLong()
    {
        var backoff = new OutboundDestinationBackoff(spread: 0);
        var flooding = new FloodingAlgorithm(new BackhaulRoute("N0NEAR", BearerPort: 0));
        var m = new OutboundMessageManager(
            database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], flooding, routingContext,
            destinationBackoff: backoff);
        backhaul.NextResult = BackhaulSendResult.Fail("N0NEAR hung up before the exchange", TimeSpan.FromSeconds(50));
        InsertMessage(id: "flood02", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(TestContext.Current.CancellationToken);

        backoff.IsInCooldown("N0NEAR", out var until).Should().BeTrue();
        until.Should().BeOnOrAfter(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45));
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
    public async Task DoRun_AConnectScriptWhoseLinkDrops_FailsTheMessage_AndAfterTheCooldownTheNextRunSendsIt()
    {
        // #263: the link failing mid-script with an IOException used to
        // leave the message claimed, so no later run would send it.
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backoff = new OutboundDestinationBackoff(clock, spread: 0);
        var transport = new FirstDialFailsMidScriptTransport(new IOException("socket closed"), "drop001");
        var m = ScriptedManager(transport, backoff);
        InsertMessage(id: "drop001", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(ct);

        transport.Connects.Should().Be(1);
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue("a link that drops mid-script is a failed dial");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(x => x.Id == "drop001");

        await m.DoRun(ct);
        transport.Connects.Should().Be(1, "nothing redials before the cooldown is out");

        clock.Advance(TimeSpan.FromHours(1));
        await m.DoRun(ct);

        transport.Connects.Should().Be(2);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty("the next run sent it");
    }

    [Fact]
    public async Task DoRun_TheKillSwitchClosesMidScript_DefersTheMessage_WithNoCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var backoff = new OutboundDestinationBackoff();
        var transport = new FirstDialFailsMidScriptTransport(new dapps.client.Tx.TxStoppedException("operator stop"), "stop001");
        var m = ScriptedManager(transport, backoff);
        InsertMessage(id: "stop001", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(ct);

        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse("our own refusal to transmit says nothing about the route");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(x => x.Id == "stop001");

        await m.DoRun(ct);

        transport.Connects.Should().Be(2, "the message is no longer held in flight");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_ABatchThatThrows_DoesNotStopTheRunForTheNextNeighbour_AndItsMessageIsSentLater()
    {
        // #263: an exception from one neighbour's batch used to escape the
        // run, so the neighbours after it waited, and the message it had
        // taken stayed claimed until a restart.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0TWO", BearerPort = 0 });
        }
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backoff = new OutboundDestinationBackoff(clock, spread: 0);
        var bearer = new ThrowingOnceBackhaul("N0DEST");
        var m = MakeManager(bearer, backoff);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "dest001", ttl: null, createdAt: t0);
        InsertMessage(id: "two0001", ttl: null, createdAt: t0.AddSeconds(1), destination: "app@N0TWO");

        await m.DoRun(ct);

        bearer.Sent.Should().Equal("two0001");
        (await database.GetPendingOutboundMessages()).Select(p => p.Id).Should().Equal("dest001");
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue("what the bearer took fails, as a failed dial would");

        clock.Advance(TimeSpan.FromHours(1));
        await m.DoRun(ct);

        bearer.Sent.Should().Equal("two0001", "dest001");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    private OutboundMessageManager ScriptedManager(IDappsOutboundTransport transport, OutboundDestinationBackoff backoff)
    {
        var script = new ConnectScript([new ConnectScriptStep("C GB7XYZ", "Connected"), new ConnectScriptStep("DAPPS", "DAPPSv1>")]);
        var routing = new SwitchableRouting(new BackhaulRoute("N0DEST", BearerPort: 0, ConnectScript: script));
        var bearer = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        return new(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routing, routingContext,
            destinationBackoff: backoff);
    }

    /// <summary>The first dial's link fails the script's first write with
    /// <paramref name="failure"/>; every later dial gets through the
    /// script and has <paramref name="id"/> acked.</summary>
    private sealed class FirstDialFailsMidScriptTransport(Exception failure, string id) : IDappsOutboundTransport
    {
        public int Connects { get; private set; }

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Connects++;
            Stream stream = Connects == 1
                ? new ScriptedStream([], failure)
                : new ScriptedStream(Encoding.UTF8.GetBytes(
                    $"Connected to GB7XYZ\nDAPPSv1>\nexchange id=far001 hold=0 inline=256\nack {id}\n"), null);
            return Task.FromResult<IDappsConnection>(new Connection(stream));
        }

        private sealed class Connection(Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>Reads the canned bytes then ends; writes are dropped,
        /// or throw <paramref name="writeFailure"/>.</summary>
        private sealed class ScriptedStream(byte[] reads, Exception? writeFailure) : Stream
        {
            private readonly MemoryStream canned = new(reads);
            private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var n = await canned.ReadAsync(buffer, ct);
                if (n > 0 || writeFailure is null) return n;
                await closed.Task.WaitAsync(ct);
                return 0;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
                writeFailure is null ? ValueTask.CompletedTask : throw writeFailure;
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (writeFailure is not null) throw writeFailure;
            }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
            protected override void Dispose(bool disposing)
            {
                closed.TrySetResult();
                base.Dispose(disposing);
            }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>For <paramref name="throwFor"/>, takes the first message
    /// and throws without reporting on it, once; every other batch is
    /// sent and accepted.</summary>
    private sealed class ThrowingOnceBackhaul(string throwFor) : IDappsBackhaul
    {
        private bool thrown;
        public List<string> Sent { get; } = [];

        public bool CanHandle(BackhaulRoute route) => true;

        public Task<BackhaulSendResult> SendAsync(BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct) =>
            throw new InvalidOperationException("the forwarder should hand this bearer batches");

        public async Task SendBatchAsync(BackhaulRoute route, string localCallsign, IBackhaulBatch batch, CancellationToken ct)
        {
            if (!thrown && route.Callsign == throwFor)
            {
                thrown = true;
                await batch.NextAsync(ct);
                throw new IOException("the node went away");
            }
            while (await batch.NextAsync(ct) is { } message)
            {
                Sent.Add(message.Id);
                await batch.CompleteAsync(message, BackhaulSendResult.Ok(), TimeSpan.Zero, ct);
            }
        }
    }

    [Fact]
    public async Task DoRun_AFloodCopyWhoseSendThrows_DoesNotStopTheCopiesToTheOthers_AndTheMessageIsMarkedForwarded()
    {
        // #263: one neighbour's bearer throwing used to skip the neighbours
        // after it and leave the message to flood again on every run.
        var backoff = new OutboundDestinationBackoff();
        var flooding = new FloodingAlgorithm(new BackhaulRoute("N0BAD", BearerPort: 0), new BackhaulRoute("N0GOOD", BearerPort: 0));
        var bearer = new ThrowsForOneBackhaul("N0BAD");
        var m = new OutboundMessageManager(
            database, NullLoggerFactory.Instance, optionsMonitor, [bearer], flooding, routingContext,
            destinationBackoff: backoff);
        InsertMessage(id: "flood03", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Sent.Should().Equal("N0GOOD");
        backoff.IsInCooldown("N0BAD", out _).Should().BeTrue("a copy whose send throws is a failed send");
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("flood03")!.Forwarded.Should().BeTrue("a flood is one-shot, as before");
    }

    /// <summary>SendAsync throws for one neighbour and accepts for the rest.</summary>
    private sealed class ThrowsForOneBackhaul(string throwFor) : IDappsBackhaul
    {
        public List<string> Sent { get; } = [];
        public bool CanHandle(BackhaulRoute route) => true;
        public Task<BackhaulSendResult> SendAsync(BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
        {
            if (route.Callsign == throwFor) throw new IOException("the node went away");
            Sent.Add(route.Callsign);
            return Task.FromResult(BackhaulSendResult.Ok());
        }
    }

    private sealed class RecordingRouting : IRoutingAlgorithm
    {
        public List<(string Id, bool Accepted)> Observed { get; } = [];
        public bool ThrowOnceOnFail { get; set; }
        public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct) =>
            Task.FromResult<RouteDecision>(new RouteDecision.NextHop(new BackhaulRoute("N0DEST", BearerPort: 0)));
        public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct)
        {
            Observed.Add((message.Id, result.Accepted));
            if (ThrowOnceOnFail && !result.Accepted) { ThrowOnceOnFail = false; throw SQLiteException.New(SQLite3.Result.Busy, "database is locked"); }
            return Task.CompletedTask;
        }
        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>First batch: runs the script (which ends by throwing). Later batches send everything.</summary>
    private sealed class ScriptedOnceBackhaul(Func<IBackhaulBatch, CancellationToken, Task> script) : IDappsBackhaul
    {
        private bool done;
        public List<string> Sent { get; } = [];
        public bool CanHandle(BackhaulRoute route) => true;
        public Task<BackhaulSendResult> SendAsync(BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct) =>
            throw new InvalidOperationException();
        public async Task SendBatchAsync(BackhaulRoute route, string localCallsign, IBackhaulBatch batch, CancellationToken ct)
        {
            if (!done) { done = true; await script(batch, ct); return; }
            while (await batch.NextAsync(ct) is { } m)
            {
                Sent.Add(m.Id);
                await batch.CompleteAsync(m, BackhaulSendResult.Ok(), TimeSpan.Zero, ct);
            }
        }
    }

    private (OutboundMessageManager, RecordingRouting, OutboundDestinationBackoff, FakeTimeProvider) MakeWithRecordingRouting(IDappsBackhaul b)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backoff = new OutboundDestinationBackoff(clock, spread: 0);
        var routing = new RecordingRouting();
        return (new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [b], routing, routingContext, destinationBackoff: backoff), routing, backoff, clock);
    }

    [Fact]
    public async Task DoRun_ABearerThatReportsThenThrows_GivesTheMessageExactlyOneOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            var a = (await batch.NextAsync(c))!;
            await batch.CompleteAsync(a, BackhaulSendResult.Ok(), TimeSpan.Zero, c);
            throw new IOException("after the ack");
        });
        var (m, routing, backoff, _) = MakeWithRecordingRouting(b);
        InsertMessage(id: "okthr01", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(ct);

        routing.Observed.Should().Equal(("okthr01", true));
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse();
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_ABearerThatFailsOneThenTakesAnotherAndThrows_GivesEachExactlyOneOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            var a = (await batch.NextAsync(c))!;
            await batch.CompleteAsync(a, BackhaulSendResult.Fail("nope"), TimeSpan.Zero, c);
            await batch.NextAsync(c);
            throw new IOException("boom");
        });
        var (m, routing, _, _) = MakeWithRecordingRouting(b);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "ff00001", ttl: null, createdAt: t0);
        InsertMessage(id: "ff00002", ttl: null, createdAt: t0.AddSeconds(1));

        await m.DoRun(ct);

        routing.Observed.Should().Equal(("ff00001", false), ("ff00002", false));
    }

    [Fact]
    public async Task DoRun_ABearerThatTakesSeveralAndThrows_FailsTheOldest_DefersTheRest_AndAllGoAfterTheCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            await batch.NextAsync(c); await batch.NextAsync(c); await batch.NextAsync(c);
            throw new IOException("boom");
        });
        var (m, routing, backoff, clock) = MakeWithRecordingRouting(b);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "th00001", ttl: null, createdAt: t0);
        InsertMessage(id: "th00002", ttl: null, createdAt: t0.AddSeconds(1));
        InsertMessage(id: "th00003", ttl: null, createdAt: t0.AddSeconds(2));

        await m.DoRun(ct);

        routing.Observed.Should().Equal(("th00001", false));
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue();
        (await database.GetPendingOutboundMessages()).Should().HaveCount(3);

        clock.Advance(TimeSpan.FromHours(1));
        await m.DoRun(ct);
        b.Sent.Should().Equal("th00001", "th00002", "th00003");
    }

    [Fact]
    public async Task DoRun_ABearerThatTakesSeveralAndHitsTheKillSwitch_DefersThemAll_WithNoCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            await batch.NextAsync(c); await batch.NextAsync(c);
            throw new dapps.client.Tx.TxStoppedException("stop");
        });
        var (m, routing, backoff, _) = MakeWithRecordingRouting(b);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "tx00001", ttl: null, createdAt: t0);
        InsertMessage(id: "tx00002", ttl: null, createdAt: t0.AddSeconds(1));

        await m.DoRun(ct);
        routing.Observed.Should().BeEmpty();
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse();

        await m.DoRun(ct);
        b.Sent.Should().Equal("tx00001", "tx00002");
    }

    [Fact]
    public async Task DoRun_CancelledMidBatch_RecordsNoOutcome()
    {
        using var cts = new CancellationTokenSource();
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            await batch.NextAsync(c);
            await cts.CancelAsync();
            c.ThrowIfCancellationRequested();
        });
        var (m, routing, backoff, _) = MakeWithRecordingRouting(b);
        InsertMessage(id: "cc00001", ttl: null, createdAt: DateTime.UtcNow);

        var act = () => m.DoRun(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        routing.Observed.Should().BeEmpty();
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DoRun_ABearerThatThrows_AndRecordingTheFirstOutcomeFails_DoesNotStrandTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new ScriptedOnceBackhaul(async (batch, c) =>
        {
            await batch.NextAsync(c); await batch.NextAsync(c); await batch.NextAsync(c);
            throw new IOException("boom");
        });
        var (m, routing, _, clock) = MakeWithRecordingRouting(b);
        routing.ThrowOnceOnFail = true;
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        InsertMessage(id: "db00001", ttl: null, createdAt: t0);
        InsertMessage(id: "db00002", ttl: null, createdAt: t0.AddSeconds(1));
        InsertMessage(id: "db00003", ttl: null, createdAt: t0.AddSeconds(2));

        await m.DoRun(ct);
        clock.Advance(TimeSpan.FromHours(1));
        await m.DoRun(ct);

        b.Sent.Should().Equal("db00001", "db00002", "db00003");
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
    public async Task DoRun_AMessageWaitingInAnOpenSession_IsNotHandedToItAgain()
    {
        // A session works through what it's handed as its window allows.
        // Handing it the same queued message on every run meant, when the
        // session ended, one deferral per copy.
        var holding = new HoldingFakeBackhaul();
        var m = MakeManager(holding);
        InsertMessage(id: "once001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);
        await m.DoRun(TestContext.Current.CancellationToken);
        await m.DoRun(TestContext.Current.CancellationToken);

        holding.Batches.Should().ContainSingle("the session still has it");
        var batch = holding.Batches.Single();
        var message = await batch.NextAsync(TestContext.Current.CancellationToken);
        await batch.CompleteAsync(message!, BackhaulSendResult.Ok(), TimeSpan.Zero, TestContext.Current.CancellationToken);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_AMessageHandedToAnOpenSessionTwice_IsSentOnce()
    {
        // If a session never gets to a message it was handed, a later run
        // hands it over again; the claim at hand-out still stops a second
        // send if both copies are taken.
        var holding = new HoldingFakeBackhaul();
        var m = MakeManager(holding);
        m.HandOffPatience = TimeSpan.Zero;
        InsertMessage(id: "twice01", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);
        await m.DoRun(TestContext.Current.CancellationToken);
        holding.Batches.Should().HaveCount(2, "the first hand-off ran out of patience");

        var sent = new List<string>();
        foreach (var batch in holding.Batches)
        {
            while (await batch.NextAsync(TestContext.Current.CancellationToken) is { } message)
            {
                sent.Add(message.Id);
                await batch.CompleteAsync(message, BackhaulSendResult.Ok(), TimeSpan.Zero, TestContext.Current.CancellationToken);
            }
        }

        sent.Should().Equal("twice01");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_AHandedMessageThatDidntGo_IsHandedOutAgainNextRun()
    {
        // Whatever becomes of a handed-out message, its claim ends with
        // its outcome, or it could never be sent again.
        var holding = new HoldingFakeBackhaul();
        var m = MakeManager(holding);
        InsertMessage(id: "again01", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5));

        await m.DoRun(TestContext.Current.CancellationToken);
        var first = holding.Batches.Single();
        var message = await first.NextAsync(TestContext.Current.CancellationToken);
        await first.CompleteAsync(message!, BackhaulSendResult.Defer("the session ended"), TimeSpan.Zero, TestContext.Current.CancellationToken);

        await m.DoRun(TestContext.Current.CancellationToken);
        (await holding.Batches[1].NextAsync(TestContext.Current.CancellationToken))!.Id.Should().Be("again01");
    }

    [Fact]
    public async Task DoRun_AFloodToANeighbourWeHoldALinkTo_GoesOnThatLink()
    {
        // With links held open for minutes, skipping a busy neighbour
        // would lose most floods.
        var holding = new HoldingFakeBackhaul();
        var flooding = new FloodingAlgorithm(new BackhaulRoute("N0HELD", BearerPort: 0));
        var m = new OutboundMessageManager(
            database, NullLoggerFactory.Instance, optionsMonitor, [holding], flooding, routingContext);
        InsertMessage(id: "flood02", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(TestContext.Current.CancellationToken);

        var copy = await holding.Batches.Single().NextAsync(TestContext.Current.CancellationToken);
        copy!.Id.Should().Be("flood02");
        copy.FloodHopsRemaining.Should().Be(3);
        await holding.Batches.Single().CompleteAsync(copy, BackhaulSendResult.Ok(), TimeSpan.Zero, TestContext.Current.CancellationToken);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty("a flood is marked forwarded once its copies are away");
    }

    [Fact]
    public async Task DoRun_AnOpenSession_TakesTheBatch_EvenInACooldown()
    {
        // A live link isn't a neighbour that's failing to answer.
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("N0DEST");
        var holding = new HoldingFakeBackhaul();
        var m = MakeManager(holding, backoff);
        InsertMessage(id: "cool001", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(TestContext.Current.CancellationToken);

        (await holding.Batches.Single().NextAsync(TestContext.Current.CancellationToken))!.Id.Should().Be("cool001");
    }

    [Fact]
    public async Task DoRun_TheSessionItHasOpenWithUs_TakesTheBatch_EvenInACooldown()
    {
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("N0DEST");
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire("N0DEST", "inbound");
        var directory = new SessionDirectory();
        var peer = await EstablishedInboundSessionAsync("N0DEST", directory);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            destinationBackoff: backoff, peerSessions: peers, openSessions: directory);
        InsertMessage(id: "cool002", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(TestContext.Current.CancellationToken);

        (await peer.ReadLineAsync(TestContext.Current.CancellationToken)).Should().StartWith("msg cool002 ");
        backhaul.Sent.Should().BeEmpty("nothing was dialled");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DoRun_DeclaredInboundNode_TakesItsMailAndAck_WithoutDialling(bool hasNeighbour)
    {
        var ct = TestContext.Current.CancellationToken;
        if (hasNeighbour) await database.UpsertNeighbour("GB7LOX", bearerPort: 1);
        InsertMessage(id: "decl001", ttl: null, createdAt: DateTime.UtcNow, destination: "app@GB7LOX");
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire("GB7LOX-6", "inbound");
        var directory = new SessionDirectory();
        await using var inbound = await EstablishedInboundHandlerAsync("GB7LOX-6", "GB7LOX", directory, lease);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            peerSessions: peers, openSessions: directory);

        lease.Peer.Should().Be("GB7LOX");
        peers.IsActive("GB7LOX", out _).Should().BeTrue();
        peers.IsActive("GB7LOX-6", out _).Should().BeFalse();
        directory.CallerFor("app@GB7LOX", "N0CALL").Should().Be("GB7LOX");
        directory.CallerFor("app@GB7LOX-6", "N0CALL").Should().BeNull();

        await m.DoRun(ct);

        var sent = await inbound.Peer.ReadWithPayloadAsync(ct);
        sent.Line.Should().StartWith("msg decl001 ").And.Contain("app@GB7LOX");
        sent.Payload.Should().Equal(Encoding.UTF8.GetBytes("payload-decl001"));
        await inbound.Peer.WriteLineAsync("ack decl001\nping acked", ct);
        (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong acked");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty("the inbound session's acknowledgement completes the queued message");
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("decl001")!.Forwarded.Should().BeTrue();
        backhaul.Sent.Should().BeEmpty("the caller's declared node has a live session, not a reason to dial");
    }

    [Theory]
    [InlineData("G6FCI-8")]
    [InlineData("G6FCI-7")]
    public async Task DoRun_InboundSiblingDeclaringEight_TakesOnlyEightMail_AndSevenIsDialled(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        await database.UpsertNeighbour("G6FCI-7", bearerPort: 1);
        await database.UpsertNeighbour("G6FCI-8", bearerPort: 1);
        InsertMessage(id: "sibl007", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-1), destination: "app@G6FCI-7");
        InsertMessage(id: "sibl008", ttl: null, createdAt: DateTime.UtcNow, destination: "app@G6FCI-8");
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire(source, "inbound");
        var directory = new SessionDirectory();
        await using var inbound = await EstablishedInboundHandlerAsync(source, "G6FCI-8", directory, lease);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            peerSessions: peers, openSessions: directory);

        lease.Peer.Should().Be("G6FCI-8");
        peers.IsActive("G6FCI-7", out _).Should().BeFalse("rekeying must release the transport identity before the forwarder runs");
        peers.IsActive("G6FCI-8", out _).Should().BeTrue();
        directory.CallerFor("app@G6FCI-7", "N0CALL").Should().BeNull();
        directory.CallerFor("app@G6FCI-8", "N0CALL").Should().Be("G6FCI-8");

        await m.DoRun(ct);

        var sent = await inbound.Peer.ReadWithPayloadAsync(ct);
        sent.Line.Should().StartWith("msg sibl008 ").And.Contain("app@G6FCI-8");
        sent.Payload.Should().Equal(Encoding.UTF8.GetBytes("payload-sibl008"));
        await inbound.Peer.WriteLineAsync("ack sibl008\nping acked\nping nohand", ct);
        (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong acked");
        (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong nohand", "seven's mail must never follow eight's onto this session");
        var dialled = backhaul.Sent.Should().ContainSingle().Which;
        dialled.Message.Id.Should().Be("sibl007");
        dialled.Message.Destination.Should().Be("app@G6FCI-7");
        dialled.Route.Callsign.Should().Be("G6FCI-7");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("G6FCI-8")]
    public async Task DoRun_InboundSourceWithoutASameBaseDeclaration_DoesNotTakeBaseMail_AndTheBaseIsDialled(string? node)
    {
        var ct = TestContext.Current.CancellationToken;
        await database.UpsertNeighbour("GB7LOX", bearerPort: 1);
        InsertMessage(id: "legacy1", ttl: null, createdAt: DateTime.UtcNow, destination: "app@GB7LOX");
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire("GB7LOX-6", "inbound");
        var directory = new SessionDirectory();
        await using var inbound = await EstablishedInboundHandlerAsync("GB7LOX-6", node, directory, lease);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            peerSessions: peers, openSessions: directory);

        lease.Peer.Should().Be("GB7LOX-6");
        peers.IsActive("GB7LOX-6", out _).Should().BeTrue();
        peers.IsActive("GB7LOX", out _).Should().BeFalse();
        directory.CallerFor("app@GB7LOX-6", "N0CALL").Should().Be("GB7LOX-6");
        directory.CallerFor("app@GB7LOX", "N0CALL").Should().Be(node is null ? "GB7LOX-6" : null);
        directory.CallerFor("app@G6FCI-8", "N0CALL").Should().BeNull();

        await m.DoRun(ct);

        await inbound.Peer.WriteLineAsync("ping nohand01\nping nohand02", ct);
        (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong nohand01");
        (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong nohand02");
        var dialled = backhaul.Sent.Should().ContainSingle().Which;
        dialled.Message.Id.Should().Be("legacy1");
        dialled.Route.Callsign.Should().Be("GB7LOX");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "app@N0FAR", true)]
    [InlineData(null, "app@N0FAR-7", true)]
    [InlineData("N0FAR-16", "app@N0FAR", true)]
    [InlineData("N0FAR-8", "app@N0FAR-7", false)]
    [InlineData("G6FCI-8", "app@N0FAR", false)]
    public async Task DoRun_WithNoRoute_OnlyALegacyCallerCanStandInForAnotherSsid(string? node, string destination, bool legacyFallback)
    {
        var ct = TestContext.Current.CancellationToken;
        InsertMessage(id: "ssid004", ttl: null, createdAt: DateTime.UtcNow, destination: destination);
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire("N0FAR-2", "inbound");
        var directory = new SessionDirectory();
        await using var inbound = await EstablishedInboundHandlerAsync("N0FAR-2", node, directory, lease);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            peerSessions: peers, openSessions: directory);

        directory.CallerFor(destination, "N0CALL").Should().Be(legacyFallback ? "N0FAR-2" : null);
        directory.TryHand(DestinationParser.StationOf(destination), new RecordingBatch()).Should().BeFalse("neighbour hand-off remains exact");
        peers.IsActive(DestinationParser.StationOf(destination), out _).Should().BeFalse("the dial guard remains exact");

        await m.DoRun(ct);

        if (legacyFallback)
        {
            var sent = await inbound.Peer.ReadWithPayloadAsync(ct);
            sent.Line.Should().StartWith("msg ssid004 ").And.Contain(destination);
            await inbound.Peer.WriteLineAsync("ack ssid004\nping acked", ct);
            (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong acked");
            (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        }
        else
        {
            await inbound.Peer.WriteLineAsync("ping nohand01\nping nohand02", ct);
            (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong nohand01");
            (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong nohand02");
            (await database.GetPendingOutboundMessages()).Should().ContainSingle(m => m.Id == "ssid004");
        }
        backhaul.Sent.Should().BeEmpty("there is still no route to dial");
    }

    [Fact]
    public async Task DoRun_MailForANodeThatHasCalledUs_GoesOnItsSession_EvenWithNoRoute()
    {
        // What `rev` used to collect: we have no route to N0FAR, but it's
        // on the line.
        var directory = new SessionDirectory();
        var peer = await EstablishedInboundSessionAsync("N0FAR-2", directory);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            openSessions: directory);
        InsertMessage(id: "caller1", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(TestContext.Current.CancellationToken);

        (await peer.ReadLineAsync(TestContext.Current.CancellationToken)).Should().StartWith("msg caller1 ");
        backhaul.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_AMessageTheOfferLineWontCarry_IsDroppedWithTheReason_AndTheNeighbourIsNotBackedOff()
    {
        // #313: a stored destination with a space in it (from before the
        // app interface checked) can never go on a header line. That is
        // the message's fault, not the link's: it is dropped with the
        // reason, the neighbour is not put in a cooldown, and good mail
        // on the same session goes as normal.
        var ct = TestContext.Current.CancellationToken;
        var backoff = new OutboundDestinationBackoff();
        var directory = new SessionDirectory();
        var peer = await EstablishedInboundSessionAsync("N0FAR-2", directory);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            destinationBackoff: backoff, openSessions: directory);
        InsertMessage(id: "badline", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-1), destination: "bad app@N0FAR");
        InsertMessage(id: "good001", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(ct);

        (await peer.ReadLineAsync(ct)).Should().StartWith("msg good001 ", "the bad one never reaches the wire");
        var deadline = DateTime.UtcNow + ExchangeTestKit.Patience;
        while (DateTime.UtcNow < deadline
               && (await database.GetPendingOutboundMessages()).Any(r => r.Id == "badline"))
        {
            await m.DoRun(ct);
            await Task.Delay(50, ct);
        }

        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().NotContain("badline");
        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbDroppedMessage>("badline")!.Reason.Should().Contain("whitespace");
        }
        backoff.IsInCooldown("N0FAR-2", out _).Should().BeFalse("the link is fine");
        backoff.IsInCooldown("N0FAR", out _).Should().BeFalse("the link is fine");
        backhaul.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_MailForANodeWhoseSessionHasJustEnded_IsNotDialledForWithoutARoute()
    {
        // The session still shows in the directory when the run looks, but
        // has ended by the time the batch gets there. With no route to that
        // node there's nothing to dial: the mail waits.
        var ct = TestContext.Current.CancellationToken;
        var directory = new SessionDirectory();
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var session = new dapps.client.Backhaul.ExchangeSession(ours, "N0FAR-2", dialled: false, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance);
        var run = session.RunAsync(ct);
        new LinePeer(theirs).Close();
        await run.WaitAsync(ExchangeTestKit.Patience, ct);
        directory.Register("N0FAR-2", session);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], routingAlgorithm, routingContext,
            openSessions: directory);
        InsertMessage(id: "gone001", ttl: null, createdAt: DateTime.UtcNow, destination: "app@N0FAR");

        await m.DoRun(ct);

        backhaul.Sent.Should().BeEmpty("nothing is dialled on a made-up route");
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal("gone001");
    }

    [Fact]
    public async Task DoRun_ARefusalIsForThatMessage_NotForAnotherWithTheSameId()
    {
        // The refused one is deleted; a different message whose 7-character
        // id happens to match goes as normal.
        var bearer = new BatchingFakeBackhaul
        {
            ResultFor = msg => msg.Salt == 1L ? BackhaulSendResult.Refuse("not that one") : BackhaulSendResult.Ok(),
        };
        var m = MakeManager(bearer);
        InsertMessage(id: "same001", ttl: null, createdAt: DateTime.UtcNow);
        await m.DoRun(TestContext.Current.CancellationToken);
        using (var c = DbInfo.GetConnection()) c.Delete<DbMessage>("same001");

        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "same001", Payload = Encoding.UTF8.GetBytes("a different one"), Salt = 2L, Destination = "app@N0DEST",
                SourceCallsign = "N0CALL", AdditionalProperties = "{}", CreatedAt = DateTime.UtcNow,
            });
        }
        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Should().HaveCount(2);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty("the second was sent and acked, not dropped as refused");
    }

    private async Task<InboundHandlerSession> EstablishedInboundHandlerAsync(
        string sourceCallsign, string? node, SessionDirectory directory, PeerSessionLease lease)
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var handler = new InboundConnectionHandler(ours, sourceCallsign, NullLoggerFactory.Instance, database, new RecordingInbox(),
            directory: directory, nodeCallsign: "N0CALL", lease: lease);
        var inbound = new InboundHandlerSession(new LinePeer(theirs), handler.Handle(ct));
        try
        {
            (await inbound.Peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
            ExchangeRules.Parse(await inbound.Peer.ReadLineAsync(ct)).Node.Should().Be("N0CALL");
            var declaration = node is null ? "" : $" node={node}";
            await inbound.Peer.WriteLineAsync($"exchange id=dest01 hold=0 inline=256{declaration}\nping ready", ct);
            (await inbound.Peer.ReadLineAsync(ct)).Should().Be("pong ready");
            return inbound;
        }
        catch
        {
            await inbound.DisposeAsync();
            throw;
        }
    }

    private sealed class InboundHandlerSession(LinePeer peer, Task run) : IAsyncDisposable
    {
        public LinePeer Peer { get; } = peer;

        public async ValueTask DisposeAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            try
            {
                if (!run.IsCompleted)
                {
                    await Peer.WriteLineAsync("quit", ct);
                    (await Peer.ReadLineAsync(ct)).Should().Be("bye");
                }
            }
            finally
            {
                Peer.Close();
                await run.WaitAsync(ExchangeTestKit.Patience, ct);
            }
        }
    }

    /// <summary>A session <paramref name="peerCall"/> dialled, established
    /// and registered in <paramref name="directory"/>; returns the peer's
    /// end of the link, past the handshake.</summary>
    private static async Task<LinePeer> EstablishedInboundSessionAsync(string peerCall, SessionDirectory directory)
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var session = new dapps.client.Backhaul.ExchangeSession(ours, peerCall, dialled: false, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance)
        {
            Opened = s => directory.Register(peerCall, s),
        };
        _ = session.RunAsync(ct);
        var peer = new LinePeer(theirs);
        await peer.ReadLineAsync(ct);
        await peer.ReadLineAsync(ct);
        await peer.WriteLineAsync("exchange id=dest01 hold=0 inline=256", ct);
        while (!session.Established) await Task.Delay(10, ct);
        return peer;
    }

    [Fact]
    public async Task DoRun_ARefusal_IsNotAFailure_AndWithNoOtherRouteTheMessageIsDroppedWithTheReason()
    {
        var backoff = new OutboundDestinationBackoff();
        var bearer = new BatchingFakeBackhaul { ResultFor = _ => BackhaulSendResult.Refuse("N0DEST refused refu001: larger than 10 bytes") };
        var m = MakeManager(bearer, backoff);
        InsertMessage(id: "refu001", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(TestContext.Current.CancellationToken);
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse("the neighbour answered; it just won't take this one");
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["refu001"], "it waits for the next run to look for another route");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Should().ContainSingle("it isn't offered to the same neighbour again");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        using var c = DbInfo.GetConnection();
        c.Find<DbDroppedMessage>("refu001")!.Reason.Should().Contain("larger than 10 bytes");
    }

    [Fact]
    public async Task DoRun_ARefusedMessage_GoesAnotherWay_WhenThereIsOne()
    {
        var bearer = new BatchingFakeBackhaul
        {
            ResultFor = msg => BackhaulSendResult.Refuse("not here"),
        };
        var routing = new SwitchableRouting(new BackhaulRoute("N0DEST", BearerPort: 0));
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routing, routingContext);
        InsertMessage(id: "refu002", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(TestContext.Current.CancellationToken);
        routing.Route = new BackhaulRoute("N0OTHER", BearerPort: 0);
        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Select(b => (b.Route.Callsign, b.Ids.Single())).Should().Equal(("N0DEST", "refu002"), ("N0OTHER", "refu002"));
    }

    /// <summary>Routes every message to one next hop, which the test can change.</summary>
    private sealed class SwitchableRouting(BackhaulRoute route) : IRoutingAlgorithm
    {
        public BackhaulRoute Route { get; set; } = route;
        public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct) =>
            Task.FromResult<RouteDecision>(new RouteDecision.NextHop(Route));
        public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
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

    // pdn refuses a dial with "Already connected" (its RHPv2 error 18) when
    // something at the node already holds the link to the peer: the peer's
    // own call to us, when the calls crossed, or a console or another app.
    // Only the first is a crossing; the second is a failed dial.

    [Fact]
    public async Task DoRun_AlreadyConnected_AndThePeersCallReachesUs_IsACrossing_SoNoCooldownStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        var backoff = new OutboundDestinationBackoff();
        await using var node = new AlreadyConnectedRhpNode();
        // The peer's call reaches our listener a moment after the refusal.
        var accepted = new TaskCompletionSource<PeerSessionLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.OnOpen = open => _ = Task.Run(async () =>
        {
            await Task.Delay(200, ct);
            accepted.SetResult(peers.Acquire("N0DEST", "inbound"));
        }, ct);
        var m = MakeManager(RhpBackhaul(node, peers, TimeSpan.FromSeconds(5)), backoff, peers);
        InsertMessage(id: "cross18", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(ct);

        node.Opens.Should().Be(1);
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(x => x.Id == "cross18",
            "it waits for the peer's session, which takes it");
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse("the calls crossed: nothing failed");
        (await accepted.Task).Dispose();
    }

    [Fact]
    public async Task DoRun_AlreadyConnected_AndNoSessionReachesUs_IsAFailedDial_WithACooldown_AndNoRedialBeforeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        var backoff = new OutboundDestinationBackoff();
        await using var node = new AlreadyConnectedRhpNode();
        var m = MakeManager(RhpBackhaul(node, peers, TimeSpan.FromMilliseconds(300)), backoff, peers);
        InsertMessage(id: "held18", ttl: null, createdAt: DateTime.UtcNow);

        await m.DoRun(ct);

        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue("a console or another app holds the link, so the dial failed");
        (await database.GetPendingOutboundMessages()).Should().ContainSingle(x => x.Id == "held18");

        await m.DoRun(ct);

        node.Opens.Should().Be(1, "nothing redials the peer before its cooldown is out");
    }

    /// <summary>The real session backhaul, dialling over RHPv2 through the
    /// real outbound transport to <paramref name="node"/>.</summary>
    private static Dappsv1SessionBackhaul RhpBackhaul(AlreadyConnectedRhpNode node, PeerSessionRegistry peers, TimeSpan peersCallWait)
    {
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            NodeBearer = "rhpv2",
            NodeHost = "127.0.0.1",
            RhpPort = node.Port,
        });
        var transport = new BearerSwitchingOutboundTransport(
            options, NullLoggerFactory.Instance, dapps.client.Tx.AlwaysOpenTxGate.Instance, TimeProvider.System,
            linkSettleDelay: TimeSpan.Zero, peerSessions: peers, linkSettleSpread: TimeSpan.Zero, peersCallWait: peersCallWait);
        return new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
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

    [Fact]
    public async Task DoRun_Transit_IsNotHandedBackToTheLinkSourcesOpenSession()
    {
        // Transit mail from N0FAR-2 for app@N0FAR-7, and N0FAR-2 has its
        // session open with us. It would stand in for N0FAR-7 and take the
        // message straight back (#297).
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection()) c.CreateTable<DbLearnedRoute>();
        var stack = new FloodFallbackAlgorithm(
            new PassiveLearningAlgorithm(routingAlgorithm, NullLogger<PassiveLearningAlgorithm>.Instance),
            NullLogger<FloodFallbackAlgorithm>.Instance);
        var directory = new SessionDirectory();
        var peer = await EstablishedInboundSessionAsync("N0FAR-2", directory);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], stack, routingContext,
            openSessions: directory);
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbMessage { Id = "back001", Payload = "x"u8.ToArray(), Salt = 1L, Destination = "app@N0FAR-7",
                SourceCallsign = "N0FAR-2", OriginatorCallsign = "G0ORG-1", AdditionalProperties = "{}", CreatedAt = DateTime.UtcNow });

        await m.DoRun(ct);

        string? line = null;
        try { line = await peer.ReadLineAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct); } catch (TimeoutException) { }
        (line ?? "").Should().NotStartWith("msg back001 ", "split horizon: never back to the link source");
    }

    [Fact]
    public async Task DoRun_Transit_WithNoRoute_WaitsQueued_AndGoesOnceARouteTurnsUp()
    {
        // #295: a relay doesn't flood what it didn't originate. It keeps it,
        // and sends it on a later run when a route appears.
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection()) c.CreateTable<DbLearnedRoute>();
        var stack = new FloodFallbackAlgorithm(
            new PassiveLearningAlgorithm(routingAlgorithm, NullLogger<PassiveLearningAlgorithm>.Instance),
            NullLogger<FloodFallbackAlgorithm>.Instance);
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul], stack, routingContext);
        using (var c = DbInfo.GetConnection())
            c.Insert(new DbMessage { Id = "wait001", Payload = "x"u8.ToArray(), Salt = 1L, Destination = "app@N0FAR-7", Ttl = 600,
                SourceCallsign = "N0DEST", OriginatorCallsign = "G0ORG-1", AdditionalProperties = "{}", CreatedAt = DateTime.UtcNow });

        await m.DoRun(ct);
        await m.DoRun(ct);

        backhaul.Sent.Should().BeEmpty("no route, and not ours to flood");
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Contain("wait001");

        await database.UpsertNeighbour("N0FAR-7", bearerPort: 0);
        await m.DoRun(ct);

        backhaul.Sent.Should().ContainSingle().Which.Route.Callsign.Should().Be("N0FAR-7");
    }

    [Fact]
    public async Task DoRun_ADialThatHangs_DoesNotHoldUpTheOtherNeighbours_AndFinishesInTheBackground()
    {
        // Seen at MB7NPW: the oldest mail was for a neighbour whose first
        // hop took 3 minutes to fail, and everything else waited behind it.
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0TWO", BearerPort = 1 });
        var bearer = new GatedBackhaul(holdFor: "N0DEST");
        var wakeup = new ForwarderWakeup();
        var clock = new FakeTimeProvider();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "slow001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10));
        InsertMessage(id: "fast001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0TWO");

        await RunPastPatienceAsync(m, bearer, clock, ct);

        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["slow001"],
            "N0TWO's mail goes while N0DEST's dial is still going");

        bearer.Gate.SetResult(BackhaulSendResult.Ok());
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue(
            "the forwarder is woken when the send it left behind ends");
        await WaitUntilAsync(async () => (await database.GetPendingOutboundMessages()).Count == 0, ct);
    }

    [Fact]
    public async Task DoRun_WhileASendIsStillGoing_NeighboursReachedThroughTheSameNodeWait()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = MakeManager(bearer);
        m.TimeProvider = clock;
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await RunPastPatienceAsync(m, bearer, clock, ct);
        await m.DoRun(ct);

        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA"],
            "N0VIA is still being dialled for N0AAA: neither a second call for it nor one for N0BBB through it");

        bearer.Gate.SetResult(BackhaulSendResult.Ok());
        // Once the send has ended and let go of the link, a run takes
        // N0BBB's mail through it.
        await WaitUntilAsync(async () =>
        {
            await m.DoRun(ct);
            return (await database.GetPendingOutboundMessages()).Count == 0;
        }, ct);
        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA", "N0BBB"]);
    }

    [Fact]
    public async Task DoRun_MailHeldBackForABusyLink_TakesOneLinksSnapshotOfIt_WithoutWaitingForTheNode()
    {
        // #425: what the node says about the link the mail is waiting for.
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var prompt = new AnsweringPrompt(_ => { Interlocked.Increment(ref asked); return answer.Task; });
        var logs = new CapturingLoggerFactory();
        var diagnostics = new NodeDiagnostics(prompt, _ => Task.FromResult<IReadOnlyCollection<string>>([]), optionsMonitor,
            new Microsoft.Extensions.Logging.Logger<NodeDiagnostics>(logs));
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            nodeDiagnostics: diagnostics) { TimeProvider = clock };
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await RunPastPatienceAsync(m, bearer, clock, ct);
        await m.DoRun(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await m.DoRun(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        asked.Should().Be(1, "the node is asked once per link in ten minutes, and the runs don't wait for its answer");
        answer.SetResult("LOC:G9LOC} Links\rN0VIA     N0CALL     S=5 P=1 T=2 V=2 Q=0\r");
        await Task.WhenAll(diagnostics.Snapshots).WaitAsync(TimeSpan.FromSeconds(5), ct);
        logs.Entries.Select(e => e.Message).Should().Contain("Node LINKS while the link to N0VIA is busy: N0VIA N0CALL S=5 P=1 T=2 V=2 Q=0");
        bearer.Gate.SetResult(BackhaulSendResult.Ok());
    }

    private sealed class AnsweringPrompt(Func<string, Task<string>> answer) : INodePrompt
    {
        public Task<string> AskAsync(string command, CancellationToken ct) => answer(command);
    }

    [Fact]
    public async Task DoRun_TheNodeANeighbourIsReachedThroughNeverAnswers_TheOthersReachedThroughItWaitOutTheCooldownToo()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var backoff = new OutboundDestinationBackoff();
        var bearer = new BatchingFakeBackhaul
        {
            ResultFor = m => m.Destination == "app@N0AAA"
                ? BackhaulSendResult.Fail("AGW connect to N0VIA failed") with { DialFailed = true }
                : BackhaulSendResult.Ok(),
        };
        var m = MakeManager(bearer, backoff: backoff);
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA"], "N0BBB would only dial the same dead N0VIA");
        backoff.IsInCooldown("N0VIA", out _).Should().BeTrue();
        (await database.GetPendingOutboundMessages()).Should().HaveCount(2);
    }

    [Fact]
    public async Task DoRun_AConnectScriptFailingPastTheNodeItGoesThrough_DoesNotHoldUpTheOthersReachedThroughIt()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var backoff = new OutboundDestinationBackoff();
        var bearer = new BatchingFakeBackhaul
        {
            ResultFor = m => m.Destination == "app@N0AAA"
                ? BackhaulSendResult.Fail("connect-script failed for N0AAA: step 2/3 timed out")
                : BackhaulSendResult.Ok(),
        };
        var m = MakeManager(bearer, backoff: backoff);
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA", "N0BBB"], "N0VIA itself answered");
        backoff.IsInCooldown("N0VIA", out _).Should().BeFalse();
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["aaa0001"]);
    }

    /// <summary>
    /// Runs the forwarder until the held send has outlasted its patience
    /// and been left in the background. The patience wait is on
    /// <paramref name="clock"/> and starts before the bearer does, so one
    /// step of the clock once the bearer is holding ends exactly that
    /// wait, and no other send is ever left behind by accident.
    /// </summary>
    private static async Task RunPastPatienceAsync(OutboundMessageManager m, GatedBackhaul bearer, FakeTimeProvider clock, CancellationToken ct)
    {
        var run = m.DoRun(ct);
        await bearer.Holding.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        clock.Advance(m.SendPatience);
        await run.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task DoRun_ASendLeftInTheBackgroundFails_ItsMessageStaysQueued_InCooldown_AndCanGoAgainLater()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var backoff = new OutboundDestinationBackoff();
        var wakeup = new ForwarderWakeup();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            destinationBackoff: backoff, wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");

        await RunPastPatienceAsync(m, bearer, clock, ct);
        bearer.Gate.SetResult(BackhaulSendResult.Fail("AGW connect to N0VIA timed out") with { DialFailed = true });
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue();

        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["aaa0001"], "a failed send loses nothing");
        backoff.IsInCooldown("N0AAA", out _).Should().BeTrue();
        backoff.IsInCooldown("N0VIA", out _).Should().BeTrue("the node it's reached through never answered");

        await m.DoRun(ct);
        bearer.Batches.Should().ContainSingle("both are in cooldown");

        // Once the cooldowns are over, the message goes again: the link
        // and the message were both let go when the send ended.
        backoff.RecordSuccess("N0AAA");
        backoff.RecordSuccess("N0VIA");
        await m.DoRun(ct);
        bearer.Batches.Select(b => string.Join(",", b.Ids)).Should().Equal(["aaa0001", "aaa0001"]);
    }

    [Fact]
    public async Task DoRun_ASendLeftInTheBackgroundThrows_ItsMessageStaysQueued_AndTheLinkIsLetGo()
    {
        var ct = TestContext.Current.CancellationToken;
        var bearer = new GatedBackhaul(holdFor: "N0DEST");
        var clock = new FakeTimeProvider();
        var backoff = new OutboundDestinationBackoff();
        var wakeup = new ForwarderWakeup();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            destinationBackoff: backoff, wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "slow001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10));

        await RunPastPatienceAsync(m, bearer, clock, ct);
        bearer.Gate.SetException(new IOException("AGW socket closed"));
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue();

        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["slow001"]);
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue("the oldest unanswered message fails, as it did before");

        backoff.RecordSuccess("N0DEST");
        await m.DoRun(ct);
        bearer.Batches.Should().HaveCount(2, "nothing was left claimed");
    }

    [Fact]
    public async Task DoRun_ASendLeftInTheBackground_DoesNotPickUpMailQueuedSince_WhichGoesOnceOnALaterRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var bearer = new GatedBackhaul(holdFor: "N0DEST");
        var clock = new FakeTimeProvider();
        var wakeup = new ForwarderWakeup();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "old0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10));

        await RunPastPatienceAsync(m, bearer, clock, ct);
        InsertMessage(id: "new0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(1));
        await m.DoRun(ct);
        bearer.Batches.Should().ContainSingle("the link is still busy with the send left in the background");

        bearer.Gate.SetResult(BackhaulSendResult.Ok());
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue();
        await m.DoRun(ct);

        bearer.Batches.Select(b => string.Join(",", b.Ids)).Should().Equal(["old0001", "new0001"],
            "the batch left in the background only sends what it had; the new one goes once, on its own run");
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task DoRun_WhileASendIsStillGoing_OnlyTheSameNodeOnTheSamePortWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 2, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0VIA", BearerPort = 1 });
        }
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = MakeManager(bearer);
        m.TimeProvider = clock;
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-8), destination: "app@N0BBB");
        InsertMessage(id: "via0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-6), destination: "app@N0VIA");

        await RunPastPatienceAsync(m, bearer, clock, ct);

        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA", "N0BBB"],
            "N0VIA on port 2 is another link; N0VIA itself on port 1 is the link still being dialled");
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().BeEquivalentTo(["aaa0001", "via0001"]);
        bearer.Gate.SetResult(BackhaulSendResult.Ok());
    }

    [Fact]
    public async Task DoRun_OnShutdown_ASendLeftInTheBackgroundLetsGoOfItsLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var wakeup = new ForwarderWakeup();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], routingAlgorithm, routingContext,
            wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await RunPastPatienceAsync(m, bearer, clock, stopping.Token);
        await stopping.CancelAsync();
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue();

        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(1), destination: "app@N0BBB");
        await m.DoRun(ct);
        bearer.Batches.Select(b => b.Route.Callsign).Should().Equal(["N0AAA", "N0BBB"]);
    }

    [Fact]
    public async Task DoRun_AnAcceptedMessageThroughANode_ClearsThatNodesFailureStreak()
    {
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
        var clock = new FakeTimeProvider();
        var backoff = new OutboundDestinationBackoff(clock, spread: 0);
        var first = backoff.RecordFailure("N0VIA") - clock.GetUtcNow();
        // Up the ramp, past its first tier.
        for (var i = 0; i < 5; i++) backoff.RecordFailure("N0VIA");
        clock.Advance(TimeSpan.FromDays(1));
        var bearer = new BatchingFakeBackhaul();
        var m = MakeManager(bearer, backoff: backoff);
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Should().ContainSingle();
        (backoff.RecordFailure("N0VIA") - clock.GetUtcNow()).Should().Be(first, "the streak started again");
    }

    [Fact]
    public async Task DoRun_AFloodCopyForALinkStillBusyInTheBackground_GoesWhenTheLinkIsFree()
    {
        // On master the run waited for the send to end before flooding,
        // so the copy went. Sent now, it would be turned away and lost.
        var ct = TestContext.Current.CancellationToken;
        var busyRoute = new BackhaulRoute("N0AAA", BearerPort: 1);
        var otherRoute = new BackhaulRoute("N0BBB", BearerPort: 1);
        var algorithm = new ByDestinationAlgorithm(m => m.Destination == "app@N0AAA"
            ? new RouteDecision.NextHop(busyRoute)
            : new RouteDecision.FloodToNeighbours([busyRoute, otherRoute], HopBudget: 3));
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var wakeup = new ForwarderWakeup();
        var m = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [bearer], algorithm, routingContext,
            wakeup: wakeup) { TimeProvider = clock };
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "fld0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0FAR");

        await RunPastPatienceAsync(m, bearer, clock, ct);

        bearer.FloodCopies.Should().Equal([("fld0001", "N0BBB")], "N0AAA's link is still busy");
        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal(["aaa0001"], "the flood is done with, as floods are");

        // A run meanwhile leaves the link alone, flood copy and all.
        await m.DoRun(ct);
        bearer.FloodCopies.Should().ContainSingle();

        bearer.Gate.SetResult(BackhaulSendResult.Ok());
        await WaitUntilAsync(() => Task.FromResult(bearer.FloodCopies.Count == 2), ct);
        bearer.FloodCopies.Should().Equal([("fld0001", "N0BBB"), ("fld0001", "N0AAA")]);
        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task DoRun_MailHeldBackForALinkStillBusyInTheBackground_AsksTheSessionsHoldingItToGiveWay()
    {
        // #425: N0BBB's mail waits for N0VIA, which N0AAA's session holds.
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = MakeManager(bearer);
        m.TimeProvider = clock;
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await RunPastPatienceAsync(m, bearer, clock, ct);

        bearer.YieldsFor.Should().Equal(["N0BBB"]);
        bearer.Gate.SetResult(BackhaulSendResult.Ok());
    }

    [Fact]
    public async Task DoRun_MailHeldBackForABusyLink_ThatIsInCooldownAnyway_AsksNothingToGiveWay()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("N0BBB");
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = MakeManager(bearer, backoff: backoff);
        m.TimeProvider = clock;
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");

        await RunPastPatienceAsync(m, bearer, clock, ct);

        bearer.YieldsFor.Should().BeEmpty("N0BBB's mail couldn't go yet anyway");
        bearer.Gate.SetResult(BackhaulSendResult.Ok());
    }

    [Fact]
    public async Task DoRun_MailHeldBackForABusyLink_WhoseNodeIsInCooldown_AsksNothingToGiveWay()
    {
        // N0VIA went into cooldown after N0AAA's send through it was left in
        // the background: N0BBB's mail couldn't be dialled through it anyway.
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        var backoff = new OutboundDestinationBackoff();
        var bearer = new GatedBackhaul(holdFor: "N0AAA");
        var clock = new FakeTimeProvider();
        var m = MakeManager(bearer, backoff: backoff);
        m.TimeProvider = clock;
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");
        await RunPastPatienceAsync(m, bearer, clock, ct);

        backoff.RecordFailure("N0VIA");
        InsertMessage(id: "bbb0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0BBB");
        await m.DoRun(ct);

        bearer.YieldsFor.Should().BeEmpty("N0BBB is reached through N0VIA, which is in cooldown");
        bearer.Gate.SetResult(BackhaulSendResult.Ok());
    }

    [Fact]
    public async Task DoRun_MailForANodeWhoseLinkASessionThroughItHolds_AsksThatSessionToGiveWay()
    {
        // #425: the session to N0AAA through N0VIA holds N0VIA's lease, so
        // mail for N0VIA itself is held back by it.
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0VIA", BearerPort = 1 });
        var peers = new PeerSessionRegistry();
        var bearer = new GatedBackhaul(holdFor: "nobody");
        var m = MakeManager(bearer, peers: peers);
        InsertMessage(id: "via0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: "app@N0VIA");

        using (peers.Acquire("N0VIA", "outbound"))
        {
            await m.DoRun(TestContext.Current.CancellationToken);
        }

        bearer.Batches.Should().BeEmpty();
        bearer.YieldsFor.Should().Equal(["N0VIA"]);
    }

    [Theory]
    [InlineData("N0AAA", "N0VIA", "N0AAA")]   // a session with the neighbour itself is opening: its mail goes there
    [InlineData("N0VIA", null, "N0VIA")]      // the node's own call to us is opening
    public async Task DoRun_MailWaitingForASessionWithItsOwnNeighbour_AsksNothingElseToGiveWay(string neighbour, string? via, string leaseOn)
    {
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = neighbour, BearerPort = 1, ConnectVia = via });
        var peers = new PeerSessionRegistry();
        var bearer = new GatedBackhaul(holdFor: "nobody");
        var m = MakeManager(bearer, peers: peers);
        InsertMessage(id: "wait001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-5), destination: $"app@{neighbour}");

        using (peers.Acquire(leaseOn, "inbound"))
        {
            await m.DoRun(TestContext.Current.CancellationToken);
        }

        bearer.Batches.Should().BeEmpty();
        bearer.YieldsFor.Should().BeEmpty("the mail waits for a session with its own neighbour, not for one of ours through the node");
    }

    [Fact]
    public async Task DoRun_NoOtherMailWaiting_AsksNothingToGiveWay()
    {
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
        var bearer = new GatedBackhaul(holdFor: "nobody");
        var m = MakeManager(bearer);
        InsertMessage(id: "aaa0001", ttl: null, createdAt: DateTime.UtcNow.AddSeconds(-10), destination: "app@N0AAA");

        await m.DoRun(TestContext.Current.CancellationToken);

        bearer.Batches.Should().ContainSingle();
        bearer.YieldsFor.Should().BeEmpty();
    }

    /// <summary>
    /// #425 end to end in one process: the forwarder, the real session
    /// backhaul, and a node that allows one session through N0VIA at a
    /// time (its lease, as <see cref="BearerSwitchingOutboundTransport"/>
    /// takes it), on a fake clock. Each call through N0VIA reaches a DAPPS
    /// node that answers with a 120 s hold.
    /// </summary>
    private sealed class GiveWayRig : IAsyncDisposable
    {
        public readonly WatchedClock Clock = new();
        public readonly ForwarderWakeup Wakeup = new();
        public readonly PeerSessionRegistry Peers;
        public readonly RecordingInbox FarInbox = new();
        public readonly ViaNodeTransport Transport;
        public readonly Dappsv1SessionBackhaul Backhaul;
        public readonly OutboundMessageManager Manager;
        private readonly CancellationTokenSource lifetime;

        public GiveWayRig(OutboundMessageManagerTests t, CancellationToken ct)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Peers = new PeerSessionRegistry(Wakeup, Clock);
            Transport = new ViaNodeTransport(Peers, FarInbox, Clock, lifetime.Token);
            Backhaul = new Dappsv1SessionBackhaul(Transport, NullLoggerFactory.Instance, inbox: null,
                settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(HoldSeconds: 120)), sessionOpened: Wakeup.Wake)
            {
                TimeProvider = Clock,
                PromptWait = TimeSpan.FromSeconds(10),
                MinQuiet = TimeSpan.FromSeconds(10),
            };
            Manager = new OutboundMessageManager(t.database, NullLoggerFactory.Instance, t.optionsMonitor, [Backhaul], t.routingAlgorithm,
                t.routingContext, peerSessions: Peers, wakeup: Wakeup) { TimeProvider = Clock };
        }

        public Task RunAsync() => Manager.DoRun(lifetime.Token);

        /// <summary>Consumes any wake already pending.</summary>
        public async Task DrainWakesAsync()
        {
            while (await Wakeup.WaitAsync(TimeSpan.Zero, TimeProvider.System, lifetime.Token)) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            lifetime.Dispose();
        }
    }

    /// <summary>One session through each dialled node at a time, as the
    /// node's lease allows; each call reaches a DAPPS node answering on a
    /// loopback pair.</summary>
    private sealed class ViaNodeTransport(PeerSessionRegistry peers, RecordingInbox farInbox, TimeProvider clock, CancellationToken lifetime)
        : IDappsOutboundTransport
    {
        private int connects;
        public int Connects => Volatile.Read(ref connects);

        public async Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            var lease = peers.TryAcquire(remoteCallsign, "outbound", out var openDirection, linkPort: bearerPort)
                ?? throw new PeerSessionBusyException(remoteCallsign, openDirection!);
            Interlocked.Increment(ref connects);
            peers.Connected(lease);
            var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(lifetime);
            var far = new ExchangeSession(theirs, localCallsign, dialled: false, new ExchangeSettings(HoldSeconds: 120), farInbox,
                NullLoggerFactory.Instance) { TimeProvider = clock, OutOfStepHistory = new OutOfStepMemory() };
            _ = far.RunAsync(lifetime);
            return new Connection(ours, lease);
        }

        private sealed class Connection(Stream stream, PeerSessionLease lease) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                stream.Dispose();
                lease.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task TwoNeighboursThroughOneNode_TheSecondGoesTenSecondsAfterTheFirstGoesQuiet_NotAfterTheHold()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
            c.Insert(new DbNeighbour { Callsign = "N0BBB", BearerPort = 1, ConnectVia = "N0VIA" });
        }
        await using var rig = new GiveWayRig(this, ct);
        InsertHashedMessage("for aaa", DateTime.UtcNow.AddSeconds(-10), "app@N0AAA");
        var bbb = InsertHashedMessage("for bbb", DateTime.UtcNow.AddSeconds(-5), "app@N0BBB");

        await rig.RunAsync();

        (await database.GetPendingOutboundMessages()).Select(r => r.Id).Should().Equal([bbb],
            "N0AAA's session holds N0VIA, so N0BBB's mail waits");
        rig.Backhaul.OpenPeers.Should().Equal(["N0AAA"]);
        await rig.DrainWakesAsync();

        rig.Clock.Advance(TimeSpan.FromSeconds(10));

        (await rig.Wakeup.WaitAsync(ExchangeTestKit.Patience, TimeProvider.System, ct)).Should().BeTrue(
            "N0AAA's session ended after 10 s of quiet, and its lease going woke the forwarder");
        await WaitUntilAsync(async () =>
        {
            await rig.RunAsync();
            return (await database.GetPendingOutboundMessages()).Count == 0;
        }, ct);
        rig.Transport.Connects.Should().Be(2);
        rig.FarInbox.Messages.Select(x => x.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task OneNeighbour_TheHoldStillCarriesFollowUpMail_OnTheSameSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var c = DbInfo.GetConnection()) c.Insert(new DbNeighbour { Callsign = "N0AAA", BearerPort = 1, ConnectVia = "N0VIA" });
        await using var rig = new GiveWayRig(this, ct);
        InsertHashedMessage("for aaa", DateTime.UtcNow.AddSeconds(-10), "app@N0AAA");

        await rig.RunAsync();
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.Clock.WaitUntilPendingAsync(ct);
        rig.Backhaul.OpenPeers.Should().Equal(["N0AAA"], "nothing else waits for N0VIA, so the 120 s hold stands");

        InsertHashedMessage("more for aaa", DateTime.UtcNow, "app@N0AAA");
        await rig.RunAsync();
        await rig.FarInbox.WaitForAsync(2, ct);

        rig.FarInbox.Messages.Should().HaveCount(2);
        rig.Transport.Connects.Should().Be(1, "the follow-up rode the session already open");
    }

    /// <summary>Routes each message by a rule.</summary>
    private sealed class ByDestinationAlgorithm(Func<DbMessage, RouteDecision> decide) : IRoutingAlgorithm
    {
        public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct) => Task.FromResult(decide(message));
        public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met within 5s");
            await Task.Delay(20, ct);
        }
    }

    /// <summary>
    /// A session bearer whose send for one neighbour doesn't finish until
    /// the test opens <see cref="Gate"/>, as a dial to a dead link doesn't.
    /// Every other neighbour's messages are accepted at once.
    /// </summary>
    private sealed class GatedBackhaul(string holdFor) : IDappsBackhaul
    {
        private readonly List<(BackhaulRoute Route, List<string> Ids)> batches = [];
        public TaskCompletionSource<BackhaulSendResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once a send is waiting on <see cref="Gate"/>.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(BackhaulRoute Route, List<string> Ids)> Batches
        {
            get { lock (batches) return [.. batches]; }
        }

        public bool CanHandle(BackhaulRoute route) => true;

        private readonly List<string> yieldsFor = [];

        /// <summary>Whose waiting mail asked the sessions holding its link to give way (#425).</summary>
        public IReadOnlyList<string> YieldsFor
        {
            get { lock (yieldsFor) return [.. yieldsFor]; }
        }

        public int YieldSessionsHolding(BackhaulRoute waiting, string reason)
        {
            lock (yieldsFor) yieldsFor.Add(waiting.Callsign);
            return 0;
        }

        private readonly List<(string Id, string Callsign)> floodCopies = [];

        /// <summary>Flood copies sent, one message at a time.</summary>
        public IReadOnlyList<(string Id, string Callsign)> FloodCopies
        {
            get { lock (floodCopies) return [.. floodCopies]; }
        }

        public Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
        {
            lock (floodCopies) floodCopies.Add((message.Id, route.Callsign));
            return Task.FromResult(BackhaulSendResult.Ok());
        }

        public async Task SendBatchAsync(BackhaulRoute route, string localCallsign, IBackhaulBatch batch, CancellationToken ct)
        {
            var ids = new List<string>();
            lock (batches) batches.Add((route, ids));
            while (await batch.NextAsync(ct) is { } message)
            {
                ids.Add(message.Id);
                BackhaulSendResult result;
                if (string.Equals(route.Callsign, holdFor, StringComparison.OrdinalIgnoreCase))
                {
                    Holding.TrySetResult();
                    result = await Gate.Task.WaitAsync(ct);
                }
                else result = BackhaulSendResult.Ok();
                await batch.CompleteAsync(message, result, TimeSpan.Zero, ct);
                if (!result.Accepted) return;
            }
        }
    }

    /// <summary>A message whose id is the hash of its payload, as a real
    /// DAPPS session at the far end checks. Returns the id.</summary>
    private static string InsertHashedMessage(string text, DateTime createdAt, string destination)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var id = DappsMessage.ComputeHash(payload, 1L)[..7];
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = id, Payload = payload, Salt = 1L, Destination = destination, SourceCallsign = "N0CALL",
            AdditionalProperties = "{}", CreatedAt = createdAt,
        });
        return id;
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
