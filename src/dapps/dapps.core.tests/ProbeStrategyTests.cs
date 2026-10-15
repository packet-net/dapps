using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Plan B7 - probe-strategy dispatcher behaviour. The scheduler asks
/// <see cref="ProbeSchedulerService.ShouldRunSweep"/> on each tick;
/// these tests pin the three strategies' decisions in place so a
/// future refactor can't silently shift them.
/// </summary>
public sealed class ProbeStrategyTests
{
    [Fact]
    public void FixedInterval_AlwaysRunsImmediately()
    {
        var opts = new SystemOptions
        {
            Callsign = "N0TST",
            ProbeStrategy = ProbeStrategy.FixedInterval,
            ProbeIntervalHours = 24,
        };
        var (svc, _) = NewService(opts);

        svc.ShouldRunSweep(opts, lastSweepCompletedAt: null)
           .RunNow.Should().BeTrue();
        svc.ShouldRunSweep(opts, lastSweepCompletedAt: DateTime.UtcNow)
           .RunNow.Should().BeTrue("FixedInterval doesn't gate on last-sweep - that's the post-sweep sleep's job");
    }

    [Fact]
    public void Overnight_FiresOnceInsideWindow_ThenWaits()
    {
        // Fix the test clock to local 03:00 inside a 02:00-06:00 window.
        // Use UTC so the ConvertTimeFromUtc result depends only on our
        // injected TimeZoneInfo (Utc in the test).
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-15T03:00:00Z"));
        var opts = new SystemOptions
        {
            Callsign = "N0TST",
            ProbeStrategy = ProbeStrategy.Overnight,
            ProbeOvernightStartHour = 2,
            ProbeOvernightEndHour = 6,
        };
        var (svc, _) = NewService(opts, clock, TimeZoneInfo.Utc);

        // First check: in window, no prior sweep - fire.
        svc.ShouldRunSweep(opts, null).RunNow.Should().BeTrue();

        // Just-completed sweep blocks the next tick even though we're
        // still in the window.
        var justNow = clock.GetUtcNow().UtcDateTime;
        svc.ShouldRunSweep(opts, justNow).RunNow.Should().BeFalse("already swept this night");
    }

    [Fact]
    public void Overnight_OutsideWindow_DoesNotFire()
    {
        // 13:00 local - well outside 02:00-06:00.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-15T13:00:00Z"));
        var opts = new SystemOptions
        {
            Callsign = "N0TST",
            ProbeStrategy = ProbeStrategy.Overnight,
            ProbeOvernightStartHour = 2,
            ProbeOvernightEndHour = 6,
        };
        var (svc, _) = NewService(opts, clock, TimeZoneInfo.Utc);

        svc.ShouldRunSweep(opts, null).RunNow.Should().BeFalse();
    }

    [Fact]
    public void Overnight_StraddlesMidnight_WhenStartAfterEnd()
    {
        // Window 22:00-06:00. Test 23:00 (in) and 04:00 (in) and 12:00 (out).
        ProbeSchedulerService.IsInOvernightWindow(23, 22, 6).Should().BeTrue();
        ProbeSchedulerService.IsInOvernightWindow(4, 22, 6).Should().BeTrue();
        ProbeSchedulerService.IsInOvernightWindow(0, 22, 6).Should().BeTrue();
        ProbeSchedulerService.IsInOvernightWindow(12, 22, 6).Should().BeFalse();
        ProbeSchedulerService.IsInOvernightWindow(6, 22, 6).Should().BeFalse("end is exclusive");
    }

    [Fact]
    public void WhenQuiet_RecentForwarderActivity_Defers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-15T12:00:00Z"));
        var tracker = new OutboundActivityTracker(clock);
        tracker.RecordTransmission();           // forwarder just transmitted

        var opts = new SystemOptions
        {
            Callsign = "N0TST",
            ProbeStrategy = ProbeStrategy.WhenQuiet,
            ProbeQuietWindowSeconds = 300,
        };
        var (svc, _) = NewService(opts, clock, activityTracker: tracker);

        svc.ShouldRunSweep(opts, null).RunNow.Should().BeFalse("forwarder active in last 5 minutes");

        // After the quiet window, the strategy should fire.
        clock.Advance(TimeSpan.FromSeconds(301));
        svc.ShouldRunSweep(opts, null).RunNow.Should().BeTrue();
    }

    [Fact]
    public void WhenQuiet_NoForwarderHistory_FiresImmediately()
    {
        // No transmissions ever recorded → null IdleFor → quiet
        // ("the forwarder hasn't done anything yet, fine to probe").
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-15T12:00:00Z"));
        var tracker = new OutboundActivityTracker(clock);

        var opts = new SystemOptions
        {
            Callsign = "N0TST",
            ProbeStrategy = ProbeStrategy.WhenQuiet,
            ProbeQuietWindowSeconds = 300,
        };
        var (svc, _) = NewService(opts, clock, activityTracker: tracker);

        svc.ShouldRunSweep(opts, null).RunNow.Should().BeTrue();
    }

    private static (ProbeSchedulerService svc, FakeTimeProvider clock) NewService(
        SystemOptions opts,
        FakeTimeProvider? clock = null,
        TimeZoneInfo? tz = null,
        OutboundActivityTracker? activityTracker = null)
    {
        clock ??= new FakeTimeProvider(DateTimeOffset.Parse("2026-04-15T12:00:00Z"));
        var optsMon = new TestOptions(opts);
        var poller = new NodeProber(
            new NoopTransport(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<NodeProber>.Instance);
        var db = new Database(NullLogger<Database>.Instance, optsMon);
        var svc = new ProbeSchedulerService(
            poller, db, optsMon, clock,
            NullLogger<ProbeSchedulerService>.Instance,
            airtime: null,
            activityTracker: activityTracker)
        {
            LocalTimeZone = tz ?? TimeZoneInfo.Local,
        };
        return (svc, clock);
    }

    private sealed class TestOptions(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }

    /// <summary>NodeProber needs an IDappsOutboundTransport even though
    /// these tests don't exercise probing - supply a no-op.</summary>
    private sealed class NoopTransport : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
