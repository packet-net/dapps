using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests for the auto-forwarder loop:
///
/// 1. <see cref="OutboundMessageManager"/> serialises concurrent
///    <c>DoRun</c> calls - second-and-later concurrent invocations
///    return immediately rather than racing through the same pending
///    list. This is the failure mode that produced wasteful duplicate
///    sends when both the manual <c>/Message/dorun</c> trigger and
///    a future hosted ticker existed; the mutex prevents that.
///
/// 2. <see cref="OutboundForwarderService"/> actually ticks
///    <c>DoRun</c> on its hosted-service loop. With a fast tick
///    interval substituted (via the test-only knobs on the service)
///    we observe <see cref="OutboundMessageManager.RunCount"/>
///    incrementing within a fraction of a second.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundForwarderServiceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private OutboundMessageManager outbound = null!;
    private SlowBackhaul slowBackhaul = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-fwdsvc-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
        }

        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G0TEST-1" });
        database = new Database(NullLogger<Database>.Instance, options);
        slowBackhaul = new SlowBackhaul();
        var routingContext = new DatabaseRoutingContext(database, options);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        outbound = new OutboundMessageManager(
            database,
            new NullLoggerFactory(),
            options,
            new IDappsBackhaul[] { slowBackhaul },
            routingAlgorithm, routingContext);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DoRun_SkipsWhenAlreadyInFlight()
    {
        // Queue a message + neighbour so DoRun has work. The slow
        // backhaul blocks deterministically on a TCS until we tell it
        // to release - so the first DoRun is guaranteed to be holding
        // the mutex when the second concurrent call arrives, no
        // sleep-and-pray timing.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "G0DEST-1", UdpEndpoint = "127.0.0.1:65535" });
        }
        await database.SaveMessage("doruna1", "x"u8.ToArray(), salt: 1L,
            destination: "chat@G0DEST-1", sourceCallsign: "G0TEST-1",
            additionalProperties: "{}", ttl: 600);

        var first = outbound.DoRun(TestContext.Current.CancellationToken);

        // Wait until the backhaul confirms it's in the middle of a
        // Send - at that point the first DoRun definitely holds the
        // mutex.
        await slowBackhaul.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var second = outbound.DoRun(TestContext.Current.CancellationToken);
        // The second DoRun should observe the held mutex and return
        // (without waiting). It completes before the first does.
        await second.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        // Now release the first.
        slowBackhaul.Release.SetResult();
        await first;

        outbound.RunCount.Should().Be(1,
            "the second concurrent DoRun must skip rather than racing through the queue");
        slowBackhaul.SendCount.Should().Be(1,
            "if the second DoRun ran, the message would have been sent twice - that's exactly what the mutex prevents");
    }

    [Fact]
    public async Task ForwarderService_TicksDoRun_OnItsLoop()
    {
        // Use the test-only constructor with sub-second timings - the
        // production cadence (3s startup grace + 5s tick) makes this
        // test brittle on CI under load. With 50ms intervals and a 5s
        // deadline there's headroom for ~100 ticks before we declare
        // the service broken.
        var sp = new ServiceCollection()
            .AddSingleton(outbound)
            .BuildServiceProvider();
        var service = new OutboundForwarderService(
            sp,
            TimeProvider.System,
            NullLogger<OutboundForwarderService>.Instance)
        {
            TickInterval = TimeSpan.FromMilliseconds(50),
            StartupDelay = TimeSpan.FromMilliseconds(50),
        };
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (outbound.RunCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(cts.Token);

        outbound.RunCount.Should().BeGreaterThan(0,
            "the hosted service must invoke DoRun on its tick - that's its only job");
    }

    /// <summary>Backhaul that signals when each Send begins and waits
    /// for an explicit release before completing. Lets concurrent-
    /// DoRun tests synchronise on the lock-held state without timing
    /// guesses.</summary>
    [Fact]
    public async Task SavingAMessage_WakesTheForwarder_WithoutWaitingForTheFallback()
    {
        var wakeup = new ForwarderWakeup();
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G0TEST-1" });
        var wakingDatabase = new Database(NullLogger<Database>.Instance, options, forwarderWakeup: wakeup);
        var sp = new ServiceCollection().AddSingleton(outbound).AddSingleton(wakeup).BuildServiceProvider();
        var service = new OutboundForwarderService(sp, TimeProvider.System, NullLogger<OutboundForwarderService>.Instance)
        {
            TickInterval = TimeSpan.FromHours(1),
            StartupDelay = TimeSpan.Zero,
            WakeSettle = TimeSpan.FromMilliseconds(10),
        };
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitForAsync(() => outbound.RunCount >= 1);

        await wakingDatabase.SaveMessage("wake001", "x"u8.ToArray(), salt: 1L,
            destination: "chat@G0DEST-1", sourceCallsign: "G0TEST-1", additionalProperties: "{}", ttl: 600);

        await WaitForAsync(() => outbound.RunCount >= 2);
        await service.StopAsync(cts.Token);
        outbound.RunCount.Should().BeGreaterThanOrEqualTo(2, "the save woke the forwarder; the hour-long fallback never came round");
    }

    [Fact]
    public async Task ADestinationComingOutOfCooldown_WakesTheForwarder()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var backoff = new OutboundDestinationBackoff(time);
        backoff.RecordFailure("G0DEST-1");   // first failure: 10 s cooldown
        var sp = new ServiceCollection().AddSingleton(outbound).AddSingleton(new ForwarderWakeup()).AddSingleton(backoff)
            .BuildServiceProvider();
        var service = new OutboundForwarderService(sp, time, NullLogger<OutboundForwarderService>.Instance)
        {
            TickInterval = TimeSpan.FromHours(1),
            StartupDelay = TimeSpan.Zero,
        };
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitForAsync(() => outbound.RunCount >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);   // let the loop reach its wait

        time.Advance(TimeSpan.FromSeconds(10));

        await WaitForAsync(() => outbound.RunCount >= 2);
        await service.StopAsync(cts.Token);
        outbound.RunCount.Should().BeGreaterThanOrEqualTo(2, "the retry is due at 10 s, not at the hour-long fallback");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class SlowBackhaul : IDappsBackhaul
    {
        public int SendCount;
        public TaskCompletionSource SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(BackhaulRoute route) => route.UdpEndpoint is not null;

        public async Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message,
            BackhaulRoute route,
            string localCallsign,
            CancellationToken ct)
        {
            Interlocked.Increment(ref SendCount);
            SendStarted.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return BackhaulSendResult.Ok();
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
