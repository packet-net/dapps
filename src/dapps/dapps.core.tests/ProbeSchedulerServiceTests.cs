using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests for <see cref="ProbeSchedulerService"/>'s pure helpers - target
/// enumeration, port resolution, result recording. The
/// <c>ExecuteAsync</c> loop itself isn't exercised here; that's
/// covered indirectly via the on-demand SweepAsync entry point and
/// directly via integration coverage when the fixture is available.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class ProbeSchedulerServiceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-probe-sched-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbProbedNode>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbDiscoveredPeer>();
        }

        database = new Database(NullLogger<Database>.Instance,
            MakeOptions(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 }));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EnumerateTargets_NoNeighboursOrPeers_ReturnsEmpty()
    {
        var sched = MakeScheduler(new RecordingTransport());

        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithBearerPort_UsesNeighbourPort()
    {
        await database.UpsertNeighbour("N0THEM-9", bearerPort: 3);

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProbeSchedulerService.ProbeTarget("N0THEM-9", 3));
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithUdpEndpoint_Skipped()
    {
        // UDP-routed neighbours can't be probed via AGW. Skipping them
        // here is the only signal the prober has - there's no AGW
        // alternative path to fall back to for a UDP-only neighbour.
        await database.UpsertNeighbour("N0UDP-9", bearerPort: null, udpEndpoint: "127.0.0.1:1880");

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithoutBearerPort_FallsBackToDefault()
    {
        await database.UpsertNeighbour("N0NULL-9", bearerPort: null);

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.BearerPort.Should().Be(7);
    }

    [Fact]
    public async Task EnumerateTargets_AgwDiscoveredPeer_Included()
    {
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BCN-9",
            Bearer = "agw",
            ChannelKey = "1",
            BearerPort = 1,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProbeSchedulerService.ProbeTarget("N0BCN-9", 1));
    }

    [Fact]
    public async Task EnumerateTargets_UdpDiscoveredPeer_Skipped()
    {
        // UDP-bearer beacons populate DbDiscoveredPeer too, but the
        // probe surface is AGW-only - those rows must not contribute.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0UDP-9",
            Bearer = "udp",
            ChannelKey = "239.0.0.1:54321",
            UdpEndpoint = "127.0.0.1:1880",
            LinkClass = LinkClass.LanMulticast,
            CostHint = 8,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourAndPeerForSameCallsign_NeighbourPortWins()
    {
        // A manual /Neighbours add represents the operator's explicit
        // route; if a passing beacon also names that callsign on a
        // different port, we trust the operator.
        await database.UpsertNeighbour("N0BOTH-9", bearerPort: 5);
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BOTH-9",
            Bearer = "agw",
            ChannelKey = "0",
            BearerPort = 0,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.BearerPort.Should().Be(5);
    }

    [Fact]
    public async Task EnumerateTargets_OptOutRow_Excluded()
    {
        await database.UpsertNeighbour("N0OUT-9", bearerPort: 1);
        await database.UpsertProbedNode(new DbProbedNode { Callsign = "N0OUT-9", OptOut = true });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepAsync_ProbesEveryTarget_RecordsResultPerCallsign()
    {
        await database.UpsertNeighbour("N0AAA-9", bearerPort: 1);
        await database.UpsertNeighbour("N0BBB-9", bearerPort: 2);
        await database.UpsertNeighbour("N0CCC-9", bearerPort: 3);

        var transport = new RecordingTransport(
            ("N0AAA-9", "DAPPSv1>\n"),
            ("N0BBB-9", "no prompt here"),
            ("N0CCC-9", "DAPPSv1>\n"));
        var sched = MakeScheduler(transport,
            // Tests must not actually wait 5–30 seconds between probes.
            minInterProbeDelay: TimeSpan.Zero,
            maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(
            new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 },
            CancellationToken.None);

        var rows = (await database.GetProbedNodes()).ToDictionary(r => r.Callsign);
        rows.Should().HaveCount(3);
        rows["N0AAA-9"].LastSuccessAt.Should().NotBeNull();
        rows["N0AAA-9"].SuccessCount.Should().Be(1);
        rows["N0AAA-9"].ConsecutiveFailures.Should().Be(0);
        rows["N0AAA-9"].LastBearerPort.Should().Be(1);
        rows["N0BBB-9"].LastSuccessAt.Should().BeNull();
        rows["N0BBB-9"].ConsecutiveFailures.Should().Be(1);
        rows["N0BBB-9"].LastError.Should().Contain("DAPPSv1>");
        rows["N0CCC-9"].LastSuccessAt.Should().NotBeNull();

        transport.Connects.Should().HaveCount(3);
        transport.Connects.Select(c => c.Remote).Should().BeEquivalentTo(["N0AAA-9", "N0BBB-9", "N0CCC-9"]);
    }

    [Fact]
    public async Task SweepAsync_PreservesOptOutAcrossUpdate()
    {
        // OptOut on a previously-probed row would normally exclude it
        // from the sweep, but if the operator later un-flips it the row
        // already exists with prior result data. The recorder must not
        // wipe OptOut when applying a result. (Belt-and-braces: the
        // sweep filter excludes OptOut rows up front, so this only
        // matters for an explicit on-demand run.)
        await database.UpsertNeighbour("N0OUT-9", bearerPort: 1);
        await database.UpsertProbedNode(new DbProbedNode { Callsign = "N0OUT-9", OptOut = true });

        var sched = MakeScheduler(new RecordingTransport(("N0OUT-9", "DAPPSv1>\n")),
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.ProbeAndRecordAsync("N0US", "N0OUT-9", 1, CancellationToken.None);

        var row = await database.GetProbedNode("N0OUT-9");
        row.Should().NotBeNull();
        row!.OptOut.Should().BeTrue();
        row.LastSuccessAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_RepeatedFailures_BumpConsecutiveFailures()
    {
        await database.UpsertNeighbour("N0DEAD-9", bearerPort: 1);
        var transport = new RecordingTransport(
            ("N0DEAD-9", "no prompt"),
            ("N0DEAD-9", "no prompt"));
        var sched = MakeScheduler(transport,
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 }, CancellationToken.None);
        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 }, CancellationToken.None);

        var row = await database.GetProbedNode("N0DEAD-9");
        row.Should().NotBeNull();
        row!.ConsecutiveFailures.Should().Be(2);
        row.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task EnumerateTargets_TransitiveCandidate_IncludedAsTarget()
    {
        // A B6.1 Phase 2 transitive candidate sits in DbProbedNode with
        // Source="via:..." and no neighbour / discovered-peer row. The
        // sweep must pick it up - otherwise transitively-learned
        // callsigns sit in the table forever waiting for an operator.
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0VIA-9",
            LastBearerPort = 3,
            Source = "via:N0SRC-9",
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProbeSchedulerService.ProbeTarget("N0VIA-9", 3));
    }

    [Fact]
    public async Task EnumerateTargets_TransitiveCandidate_FallsBackToDefaultPortWhenMissing()
    {
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0VIA-9",
            LastBearerPort = null,
            Source = "via:N0SRC-9",
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 });

        targets.Should().ContainSingle().Which.BearerPort.Should().Be(7);
    }

    [Fact]
    public async Task ProbeAndRecord_FetchPeersTrue_RecordsTransitiveCandidatesWithSource()
    {
        await database.UpsertNeighbour("N0SRC-9", bearerPort: 1);
        // The fake transport hands back a DAPPSv1> + a peers response
        // listing two callsigns; ProbeAndRecordAsync should both record
        // the probe success and persist the two peers as candidates.
        var sched = MakeScheduler(new RecordingTransport(("N0SRC-9",
            "DAPPSv1>\n" +
            "peer N0AAA-9 source=n port=2\n" +
            "peer N0BBB-9 source=d\n" +
            "end\n")));

        await sched.ProbeAndRecordAsync("N0US", "N0SRC-9", 1, CancellationToken.None);

        var rows = (await database.GetProbedNodes()).ToDictionary(r => r.Callsign);
        rows.Should().ContainKey("N0SRC-9");
        rows["N0SRC-9"].Source.Should().Be("neighbour");
        rows["N0SRC-9"].LastSuccessAt.Should().NotBeNull();

        rows.Should().ContainKey("N0AAA-9");
        rows["N0AAA-9"].Source.Should().Be("via:N0SRC-9");
        rows["N0AAA-9"].LastBearerPort.Should().Be(2);     // server-supplied port preferred
        rows["N0AAA-9"].LastProbedAt.Should().BeNull(); // candidate, not probed yet

        rows.Should().ContainKey("N0BBB-9");
        rows["N0BBB-9"].Source.Should().Be("via:N0SRC-9");
        rows["N0BBB-9"].LastBearerPort.Should().Be(1);     // fell back to source's port
    }

    [Fact]
    public async Task ProbeAndRecord_PeersIncludesOurselves_NotRecordedAsCandidate()
    {
        // The remote will list us in its peers (we just sent traffic to
        // them). Recording ourselves as a probe target is meaningless
        // and would generate a self-probe - explicitly filtered.
        await database.UpsertNeighbour("N0SRC-9", bearerPort: 1);
        var sched = MakeScheduler(new RecordingTransport(("N0SRC-9",
            "DAPPSv1>\n" +
            "peer N0US source=n\n" +
            "peer N0OTHER-9 source=n\n" +
            "end\n")));

        await sched.ProbeAndRecordAsync("N0US", "N0SRC-9", 1, CancellationToken.None);

        var rows = await database.GetProbedNodes();
        rows.Select(r => r.Callsign).Should().Contain("N0SRC-9");
        rows.Select(r => r.Callsign).Should().Contain("N0OTHER-9");
        rows.Select(r => r.Callsign).Should().NotContain("N0US");
    }

    [Fact]
    public async Task ProbeAndRecord_PeersIncludesExistingNeighbour_DoesNotClobberSource()
    {
        // If a remote tells us about a callsign we already track as a
        // direct neighbour, the existing row's Source must not get
        // rewritten as "via:...". We trust direct evidence over hearsay.
        await database.UpsertNeighbour("N0SRC-9", bearerPort: 1);
        await database.UpsertNeighbour("N0EXIST-9", bearerPort: 2);
        // Run a probe of N0EXIST-9 first to establish a "neighbour" Source.
        var sched = MakeScheduler(new RecordingTransport(
            ("N0EXIST-9", "DAPPSv1>\nend\n"),     // probe of N0EXIST -- empty peers
            ("N0SRC-9",
                "DAPPSv1>\n" +
                "peer N0EXIST-9 source=n port=2\n" +
                "end\n")));

        await sched.ProbeAndRecordAsync("N0US", "N0EXIST-9", 2, CancellationToken.None);
        await sched.ProbeAndRecordAsync("N0US", "N0SRC-9", 1, CancellationToken.None);

        var existRow = await database.GetProbedNode("N0EXIST-9");
        existRow.Should().NotBeNull();
        existRow!.Source.Should().Be("neighbour", "direct probe state must outrank hearsay");
    }

    [Fact]
    public async Task ProbeAndRecord_FetchPeersFalse_DoesNotRequestPeers()
    {
        // Test seam used by other unit tests that don't model a peers
        // response. Verifies that suppressing fetchPeers really avoids
        // the second exchange - otherwise the prober would hang on a
        // missing "end" line in the canned bytes.
        await database.UpsertNeighbour("N0SRC-9", bearerPort: 1);
        var transport = new RecordingTransport(("N0SRC-9", "DAPPSv1>\n"));
        var sched = MakeScheduler(transport);

        await sched.ProbeAndRecordAsync("N0US", "N0SRC-9", 1, CancellationToken.None, fetchPeers: false);

        var row = await database.GetProbedNode("N0SRC-9");
        row.Should().NotBeNull();
        row!.LastSuccessAt.Should().NotBeNull();
        // Only one connect, and the bytes the prober wrote don't
        // include "peers".
        transport.Connects.Should().ContainSingle();
    }

    [Fact]
    public async Task ProbeAndRecord_SuccessfulProbeWithPeers_FeedsRoutingAlgorithm()
    {
        // B6.1 follow-up: a successful probe whose `peers` response
        // carries transitive callsigns should hand them to the routing
        // algorithm via ObserveProbeOutcomeAsync, not just stash them
        // in DbProbedNode. Verifies the wiring in
        // ProbeAndRecordVerboseAsync; the algorithm's own filters and
        // learned-route effects are tested in PassiveLearningAlgorithmTests.
        await database.UpsertNeighbour("N0SRC-9", bearerPort: 1);
        var captured = new RecordingRoutingAlgorithm();
        var sched = MakeScheduler(
            new RecordingTransport(("N0SRC-9",
                "DAPPSv1>\n" +
                "peer N0AAA-9 source=n port=2\n" +
                "peer N0BBB-9 source=d\n" +
                "end\n")),
            routingAlgorithm: captured,
            routingContext: new StubRoutingContext());

        await sched.ProbeAndRecordAsync("N0US", "N0SRC-9", 1, CancellationToken.None);

        captured.Calls.Should().ContainSingle();
        var call = captured.Calls[0];
        call.AskedPeer.Should().Be("N0SRC-9");
        call.Peers.Select(p => p.Callsign).Should().BeEquivalentTo(new[] { "N0AAA-9", "N0BBB-9" });
    }

    [Fact]
    public async Task ProbeAndRecord_SuccessfulProbeNoPeers_DoesNotCallRoutingAlgorithm()
    {
        await database.UpsertNeighbour("N0EMPTY-9", bearerPort: 1);
        var captured = new RecordingRoutingAlgorithm();
        var sched = MakeScheduler(
            new RecordingTransport(("N0EMPTY-9", "DAPPSv1>\nend\n")),
            routingAlgorithm: captured,
            routingContext: new StubRoutingContext());

        await sched.ProbeAndRecordAsync("N0US", "N0EMPTY-9", 1, CancellationToken.None);

        captured.Calls.Should().BeEmpty(
            "no peers means nothing to teach the routing algorithm; the hook fires only when there's at least one transitive callsign");
    }

    [Fact]
    public async Task ProbeAndRecord_FailedProbe_DoesNotCallRoutingAlgorithm()
    {
        await database.UpsertNeighbour("N0DEAD-9", bearerPort: 1);
        var captured = new RecordingRoutingAlgorithm();
        var sched = MakeScheduler(
            new RecordingTransport(("N0DEAD-9", "no prompt")),
            routingAlgorithm: captured,
            routingContext: new StubRoutingContext());

        await sched.ProbeAndRecordAsync("N0US", "N0DEAD-9", 1, CancellationToken.None);

        captured.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepAsync_SuccessAfterFailure_ResetsCounter()
    {
        await database.UpsertNeighbour("N0FLAP-9", bearerPort: 1);
        var transport = new RecordingTransport(
            ("N0FLAP-9", "no prompt"),
            ("N0FLAP-9", "DAPPSv1>\n"));
        var sched = MakeScheduler(transport,
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 }, CancellationToken.None);
        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7 }, CancellationToken.None);

        var row = await database.GetProbedNode("N0FLAP-9");
        row.Should().NotBeNull();
        row!.ConsecutiveFailures.Should().Be(0);
        row.SuccessCount.Should().Be(1);
        row.LastError.Should().BeEmpty();
    }

    private ProbeSchedulerService MakeScheduler(
        IDappsOutboundTransport transport,
        TimeSpan? minInterProbeDelay = null,
        TimeSpan? maxInterProbeDelay = null,
        dapps.core.Routing.IRoutingAlgorithm? routingAlgorithm = null,
        dapps.core.Routing.IRoutingContext? routingContext = null)
    {
        var prober = new NodeProber(transport, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance);
        var opts = MakeOptions(new SystemOptions { Callsign = "N0US", DefaultBearerPort = 7, ProbingEnabled = true });
        return new ProbeSchedulerService(
            prober, database, opts, TimeProvider.System, NullLogger<ProbeSchedulerService>.Instance,
            routingAlgorithm: routingAlgorithm,
            routingContext: routingContext)
        {
            // Tests skip the 15-minute startup grace; ExecuteAsync isn't
            // exercised here, but if it were, this lets the loop fire
            // immediately.
            StartupDelay = TimeSpan.Zero,
            MinInterProbeDelay = minInterProbeDelay ?? TimeSpan.Zero,
            MaxInterProbeDelay = maxInterProbeDelay ?? TimeSpan.FromMilliseconds(1),
        };
    }

    private static IOptionsMonitor<T> MakeOptions<T>(T value) => new TestOptionsMonitor<T>(value);

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Test transport that hands back a different canned byte
    /// stream per <paramref name="cannedByCallsign"/> entry, in order.
    /// Records each connect attempt for assertions. Uses a duplex stream
    /// with separate read and write buffers - a single MemoryStream
    /// would let the prober's writes ("peers\n") overwrite the canned
    /// bytes it's about to read.</summary>
    private sealed class RecordingTransport(params (string Remote, string Reply)[] cannedByCallsign) : IDappsOutboundTransport
    {
        private readonly Queue<(string Remote, string Reply)> _queue = new(cannedByCallsign);
        public List<(string Remote, int Port)> Connects { get; } = new();

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Connects.Add((remoteCallsign, bearerPort));
            // Best-effort: prefer the next-in-queue entry that matches
            // the requested remote, falling back to "no prompt" so the
            // prober reports failure rather than wedging on EOF.
            string reply = "";
            if (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                reply = string.Equals(next.Remote, remoteCallsign, StringComparison.OrdinalIgnoreCase)
                    ? next.Reply
                    : $"no canned reply for {remoteCallsign}";
            }
            return Task.FromResult<IDappsConnection>(new FakeConnection(new FakeDuplexStream(Encoding.UTF8.GetBytes(reply))));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>Stub routing algorithm that records every
    /// <see cref="dapps.core.Routing.IRoutingAlgorithm.ObserveProbeOutcomeAsync"/>
    /// call for assertion. Other hooks are noops - the wiring tests
    /// don't exercise them.</summary>
    private sealed class RecordingRoutingAlgorithm : dapps.core.Routing.IRoutingAlgorithm
    {
        public List<(string AskedPeer, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> Peers)> Calls { get; } = new();

        public Task<dapps.core.Routing.RouteDecision> ResolveAsync(DbMessage message, dapps.core.Routing.IRoutingContext ctx, CancellationToken ct)
            => Task.FromResult<dapps.core.Routing.RouteDecision>(new dapps.core.Routing.RouteDecision.Unreachable());

        public Task ObserveInboundAsync(dapps.client.Backhaul.BackhaulMessage message, string linkSourceCallsign, dapps.core.Routing.IRoutingContext ctx, CancellationToken ct)
            => Task.CompletedTask;

        public Task ObserveForwardOutcomeAsync(DbMessage message, dapps.client.Backhaul.BackhaulRoute attemptedRoute, dapps.client.Backhaul.BackhaulSendResult result, dapps.core.Routing.IRoutingContext ctx, CancellationToken ct)
            => Task.CompletedTask;

        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<dapps.client.DappsProtocolClient.DiscoveredPeerInfo> peers, dapps.core.Routing.IRoutingContext ctx, CancellationToken ct)
        {
            Calls.Add((askedPeerCallsign, peers));
            return Task.CompletedTask;
        }

        public Task RunAsync(dapps.core.Routing.IRoutingContext ctx, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Minimal in-memory routing context that satisfies the
    /// <see cref="dapps.core.Routing.IRoutingContext"/> interface for
    /// the wiring tests. Every method that the
    /// <see cref="RecordingRoutingAlgorithm"/> doesn't touch is a noop.</summary>
    private sealed class StubRoutingContext : dapps.core.Routing.IRoutingContext
    {
        public string LocalCallsign => "N0US";
        public int DefaultBearerPort => 0;

        public Task<IReadOnlyList<DbNeighbour>> GetNeighboursAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DbNeighbour>>(Array.Empty<DbNeighbour>());

        public Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DbDiscoveredPeer>>(Array.Empty<DbDiscoveredPeer>());

        public Task<DbNeighbour?> ResolveRouteHintAsync(string destinationCallsign, CancellationToken ct) => Task.FromResult<DbNeighbour?>(null);
        public Task<DbNeighbour?> GetNeighbourByCallsignAsync(string callsign, CancellationToken ct) => Task.FromResult<DbNeighbour?>(null);
        public Task UpsertLearnedRouteAsync(string destinationCallsign, string nextHopCallsign, CancellationToken ct) => Task.CompletedTask;
        public Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationCallsign, CancellationToken ct) => Task.FromResult<DbLearnedRoute?>(null);
        public Task RecordLearnedRouteSuccessAsync(string destinationCallsign, CancellationToken ct) => Task.CompletedTask;
        public Task<int> RecordLearnedRouteFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct) => Task.FromResult(0);
        public Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign, CancellationToken ct) => Task.FromResult(false);
        public Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, CancellationToken ct) => Task.CompletedTask;
        public Task UpsertDiscoveredPathAsync(string destinationCallsign, IReadOnlyList<string> intermediates, CancellationToken ct) => Task.CompletedTask;
        public Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationCallsign, CancellationToken ct) => Task.FromResult<DbDiscoveredPath?>(null);
        public Task RecordDiscoveredPathSuccessAsync(string destinationCallsign, CancellationToken ct) => Task.CompletedTask;
        public Task<int> RecordDiscoveredPathFailureAsync(string destinationCallsign, int invalidationThreshold, CancellationToken ct) => Task.FromResult(0);
    }
}
