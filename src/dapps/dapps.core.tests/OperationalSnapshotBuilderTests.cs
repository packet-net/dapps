using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using dapps.core.Updater;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan C3 PR-B - `OperationalSnapshotBuilder` is the single source of
/// truth feeding both <c>/Operational</c> and the periodic MQTT
/// heartbeat. These tests pin its composition so a future field
/// rename / removal can't silently drift the operator-visible
/// shape across the two surfaces.
///
/// The TCP probes (BPQ + MQTT) intentionally run against unbound
/// loopback ports in the test fixture - they'll fail fast and the
/// resulting snapshot's <c>Status</c> = "degraded" is the easiest
/// thing to assert against.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OperationalSnapshotBuilderTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-snap-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbMessage>();
        c.CreateTable<DbReceived>();
        c.CreateTable<DbNeighbour>();
        c.CreateTable<DbDiscoveredPeer>();
        c.CreateTable<DbDiscoveryChannel>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BuildAsync_ComposesCountersQueuesAirtimeAndStatus()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G7TST-9",
            NodeHost = "127.0.0.1",
            // Pick ports nothing will be listening on so the TCP probes
            // resolve to "unreachable" and Status flips to degraded.
            // Nothing in this test asserts the probes succeed.
            AgwPort = 1,
            MqttPort = 1,
            DiscoveryAirtimeBudgetSecondsPerHour = 60,
        });

        var metrics = new OperationalMetrics(clock);
        metrics.RecordForwardSuccess("aaa1234", "G7PEER", 100);
        metrics.RecordProbeOutcome("G7PEER", success: true, error: null);
        metrics.RecordPollOutcome("G7PEER", success: false, messagesDrained: 0, error: "no banner");
        metrics.RecordRouteLearned("G7DEST", "G7HOP-1");

        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        airtime.TryReserve(2.0, "beacon udp/x").Should().BeTrue();

        var db = new Database(NullLogger<Database>.Instance, opts);
        await db.UpsertNeighbour("G7PEER-9", bearerPort: 1);

        var builder = new OperationalSnapshotBuilder(
            opts, metrics, airtime, db,
            updateChecker: BuildUpdateChecker(),
            updaterFs: new FakeUpdaterFs(),
            timeProvider: clock);
        var snap = await builder.BuildAsync(TestContext.Current.CancellationToken);

        snap.Callsign.Should().Be("G7TST-9");
        snap.CallsignConfigured.Should().BeTrue();
        snap.Status.Should().Be("degraded", "BPQ + MQTT probes target unbound ports");
        snap.NodeReachable.Should().BeFalse();
        snap.MqttBrokerUp.Should().BeFalse();

        snap.ForwardSuccess.Should().Be(1);
        snap.ProbeAttempts.Should().Be(1);
        snap.ProbeSuccess.Should().Be(1);
        snap.PollAttempts.Should().Be(1);
        snap.PollFailure.Should().Be(1);
        snap.RoutesLearned.Should().Be(1);

        snap.NeighbourCount.Should().Be(1);
        snap.LastForwardSuccessAt.Should().NotBeNull();

        snap.AirtimeBudgetSecondsPerHour.Should().Be(60);
        snap.AirtimeConsumedSecondsLastHour.Should().BeApproximately(2.0, 0.001);

        snap.RecentEvents.Should().NotBeEmpty();
        snap.RecentEvents.Select(e => e.Kind).Should()
            .Contain(new[] { "forward.ok", "probe.ok", "poll.fail", "route.learned" });
    }

    [Fact]
    public async Task BuildAsync_PlaceholderCallsign_FlagsDegradedEvenIfDepsCouldBeUp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",  // placeholder
            NodeHost = "127.0.0.1",
            AgwPort = 1,
            MqttPort = 1,
        });
        var metrics = new OperationalMetrics(clock);
        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        var db = new Database(NullLogger<Database>.Instance, opts);

        var builder = new OperationalSnapshotBuilder(
            opts, metrics, airtime, db,
            updateChecker: BuildUpdateChecker(),
            updaterFs: new FakeUpdaterFs(),
            timeProvider: clock);
        var snap = await builder.BuildAsync(TestContext.Current.CancellationToken);

        snap.CallsignConfigured.Should().BeFalse();
        snap.Status.Should().Be("degraded");
    }

    [Fact]
    public async Task BuildFullAsync_PopulatesPerRowTables()
    {
        // Schema for the per-row tables BuildFullAsync queries.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbProbedNode>();
            c.CreateTable<DbPolledNode>();
        }

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G7TST-9",
            NodeHost = "127.0.0.1",
            AgwPort = 1,
            MqttPort = 1,
        });
        var metrics = new OperationalMetrics(clock);
        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        var db = new Database(NullLogger<Database>.Instance, opts, clock);

        await db.UpsertNeighbour("G7HOP-1", bearerPort: 0);
        await db.SubmitOutboundMessage("chat", "G7DEST", "hello"u8.ToArray(), ttlSeconds: 600);

        var builder = new OperationalSnapshotBuilder(
            opts, metrics, airtime, db,
            updateChecker: BuildUpdateChecker(),
            updaterFs: new FakeUpdaterFs(),
            timeProvider: clock);

        var lite = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var full = await builder.BuildFullAsync(TestContext.Current.CancellationToken);

        // Lite snapshot must NOT carry per-row tables - that's what
        // makes it suitable for the periodic heartbeat publish.
        lite.Tables.Should().BeNull();

        // Full snapshot's tables get populated.
        full.Tables.Should().NotBeNull();
        full.Tables!.Outbound.Should().NotBeEmpty("the test inserted one outbound message");
        full.Tables.Outbound[0].Destination.Should().Be("chat@G7DEST");
        full.Tables.Neighbours.Should().HaveCount(1);
        full.Tables.Neighbours[0].Callsign.Should().Be("G7HOP-1");
        full.Tables.Update.Should().NotBeNull();
        full.Tables.Update.Current.Should().NotBeNullOrEmpty();
        full.Tables.Update.RequestPending.Should().BeFalse();

        // Both shapes share the cheap fields verbatim.
        full.Callsign.Should().Be(lite.Callsign);
        full.NeighbourCount.Should().Be(lite.NeighbourCount).And.Be(1);
    }

    private static UpdateChecker BuildUpdateChecker()
        => new(new NoopHttpClientFactory(), new TestOptionsMonitor<SystemOptions>(new SystemOptions()),
               TimeProvider.System, NullLogger<UpdateChecker>.Instance);

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeUpdaterFs : IUpdaterFileSystem
    {
        private readonly Dictionary<string, string> store = new();
        public bool Exists(string path) => store.ContainsKey(path);
        public void SwapInPlace(string src, string dest, string previous) { }
        public void Restore(string previous, string dest) { }
        public void MarkExecutable(string path) { }
        public string? ReadAllText(string path) => store.TryGetValue(path, out var v) ? v : null;
        public void WriteAllText(string path, string contents) => store[path] = contents;
        public void Delete(string path) => store.Remove(path);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
