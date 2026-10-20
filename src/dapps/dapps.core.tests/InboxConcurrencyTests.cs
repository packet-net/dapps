using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;

namespace dapps.core.tests;

/// <summary>
/// Two copies of one thing arriving at the same moment (#289, #293): a
/// message is reassembled and delivered once, and a flood copy whose
/// store failed is not remembered as seen.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class InboxConcurrencyTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private readonly CountingLogger inboxLog = new();

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-conc-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbFragment>();
            c.CreateTable<DbFloodSeen>();
            c.CreateTable<DbSystemOption>();
        }
        database = new Database(NullLogger<Database>.Instance, Opts());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LastTwoFragmentsAtOnce_DeliverTheMessageOnce()
    {
        var inbox = MakeInbox(new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance));
        const int rounds = 12;
        for (var r = 0; r < rounds; r++)
        {
            var master = $"m{r:0000}";
            var frags = Fragments(master, 3);
            await inbox.DeliverAsync(frags[0], "N0A", CancellationToken.None);
            var gate = new TaskCompletionSource();
            var both = new[]
            {
                Task.Run(async () => { await gate.Task; await inbox.DeliverAsync(frags[1], "N0A", CancellationToken.None); }),
                Task.Run(async () => { await gate.Task; await inbox.DeliverAsync(frags[2], "N0B", CancellationToken.None); }),
            };
            gate.SetResult();
            await Task.WhenAll(both);
        }

        inboxLog.Count("Reassembled master").Should().Be(rounds, "each message is delivered exactly once");
        (await database.GetRecentMessages(1000)).Should().HaveCount(rounds);
        (await database.GetAllFragments()).Should().BeEmpty();
    }

    [Fact]
    public async Task SameMessageSavedAtOnce_NeitherThrows()
    {
        for (var r = 0; r < 40; r++)
        {
            var id = $"s{r:0000}";
            var gate = new TaskCompletionSource();
            var saves = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                await database.SaveMessage(id, [1, 2, 3], null, "app@N0SELF", "N0A", "{}", 60);
            })).ToArray();
            gate.SetResult();
            await Task.WhenAll(saves);
        }
        (await database.GetRecentMessages(1000)).Should().HaveCount(40);
    }

    [Fact]
    public async Task FloodCopy_WhoseStoreFailed_IsTakenWhenResent()
    {
        var algorithm = new FailsOnceAlgorithm();
        var inbox = MakeInbox(algorithm);
        var flood = new BackhaulMessage(
            Id: "fl00001", Destination: "app@N0SELF", Salt: 5, Ttl: 600, Payload: [9, 9],
            FloodHopsRemaining: 3);

        var first = async () => await inbox.DeliverAsync(flood, "N0A", CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();

        await inbox.DeliverAsync(flood, "N0A", CancellationToken.None);

        (await database.GetRecentMessages(10)).Should().ContainSingle(m => m.Id == "fl00001");
    }

    [Fact]
    public async Task FloodCopy_ArrivingTwiceAtOnce_IsStoredOnce()
    {
        var algorithm = new CountingAlgorithm();
        var inbox = MakeInbox(algorithm);
        for (var r = 0; r < 30; r++)
        {
            var flood = new BackhaulMessage(
                Id: $"fc{r:00000}", Destination: "app@N0SELF", Salt: null, Ttl: 600, Payload: [1],
                FloodHopsRemaining: 3);
            var gate = new TaskCompletionSource();
            var both = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                await inbox.DeliverAsync(flood, "N0A", CancellationToken.None);
            })).ToArray();
            gate.SetResult();
            await Task.WhenAll(both);
        }
        algorithm.Observed.Should().Be(30);
    }

    private static List<BackhaulMessage> Fragments(string master, int total) =>
        Enumerable.Range(0, total).Select(i => new BackhaulMessage(
            Id: $"{master}f{i + 1}", Destination: "app@N0SELF", Salt: 1000 + i, Ttl: 600,
            Payload: new byte[10], MasterId: master, FragmentIndex: i + 1, FragmentTotal: total)).ToList();

    private static TestOptionsMonitor<SystemOptions> Opts() => new(new SystemOptions { Callsign = "N0SELF" });

    private DatabaseAndMqttInbox MakeInbox(IRoutingAlgorithm algorithm)
    {
        var options = Opts();
        var broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, options, database,
            new AppTokenStore(NullLogger<AppTokenStore>.Instance),
            new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build()));
        return new DatabaseAndMqttInbox(
            database, broker, new InboundEventBus(), options, algorithm,
            new DatabaseRoutingContext(database, options), TimeProvider.System, inboxLog);
    }

    private class CountingAlgorithm : IRoutingAlgorithm
    {
        private int observed;
        public int Observed => observed;
        protected virtual void OnObserve() => Interlocked.Increment(ref observed);
        public Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct) => throw new NotSupportedException();
        public Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct)
        {
            OnObserve();
            return Task.CompletedTask;
        }
        public Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveProbeOutcomeAsync(string askedPeerCallsign, IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> peers, IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(IRoutingContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailsOnceAlgorithm : CountingAlgorithm
    {
        private int calls;
        protected override void OnObserve()
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("store failed");
        }
    }

    private sealed class CountingLogger : ILogger<DatabaseAndMqttInbox>
    {
        private readonly List<string> lines = [];
        public int Count(string contains) { lock (lines) return lines.Count(l => l.Contains(contains)); }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(formatter(state, exception));
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
