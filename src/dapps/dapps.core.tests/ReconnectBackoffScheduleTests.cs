using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="ReconnectBackoffSchedule"/> - the sliding-scale reconnect
/// delay shared by <see cref="AgwInboundService"/> and
/// <see cref="Rhpv2InboundService"/>. Pins the exact ramp (10s x3, 30s
/// x3, 1min x3, then 5min steady-state) so a future tweak can't
/// silently change the schedule an operator sees on the dashboard.
/// </summary>
public sealed class ReconnectBackoffScheduleTests
{
    [Fact]
    public void RecordFailure_FollowsTheDocumentedRamp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var schedule = new ReconnectBackoffSchedule(clock);

        var delays = Enumerable.Range(1, 10)
            .Select(_ => schedule.RecordFailure())
            .ToArray();

        delays.Should().Equal(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));

        schedule.FailureStreak.Should().Be(10);
    }

    [Fact]
    public void RecordFailure_SetsNextRetryAtUtc_RelativeToTheClock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var schedule = new ReconnectBackoffSchedule(clock);

        var delay = schedule.RecordFailure();

        delay.Should().Be(TimeSpan.FromSeconds(10));
        schedule.NextRetryAtUtc.Should().Be(DateTimeOffset.Parse("2026-05-01T12:00:10Z"));
    }

    [Fact]
    public void RecordSuccess_ResetsStreakAndClearsNextRetry()
    {
        var schedule = new ReconnectBackoffSchedule();
        schedule.RecordFailure();
        schedule.RecordFailure();

        schedule.RecordSuccess();

        schedule.FailureStreak.Should().Be(0);
        schedule.NextRetryAtUtc.Should().BeNull();
    }

    [Fact]
    public void RecordFailure_AfterSuccess_RestartsAtTheFastEndOfTheRamp()
    {
        var schedule = new ReconnectBackoffSchedule();
        for (var i = 0; i < 9; i++) schedule.RecordFailure(); // walk up to the 1min tier
        schedule.RecordSuccess();

        var delay = schedule.RecordFailure();

        delay.Should().Be(TimeSpan.FromSeconds(10), "a successful connect resets the ramp, not just pauses it");
        schedule.FailureStreak.Should().Be(1);
    }

    [Fact]
    public void ClearPendingWait_LeavesFailureStreakUntouched()
    {
        var schedule = new ReconnectBackoffSchedule();
        schedule.RecordFailure();
        schedule.RecordFailure();

        schedule.ClearPendingWait();

        schedule.NextRetryAtUtc.Should().BeNull();
        schedule.FailureStreak.Should().Be(2, "clearing the countdown display isn't the same as a successful connect");
    }
}

/// <summary>
/// <see cref="InboundReconnectController"/> - the cross-thread plumbing
/// that lets an operator's "retry now" click, or a /Config save, collapse
/// an in-progress backoff wait early.
/// </summary>
public sealed class InboundReconnectControllerTests
{
    private static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void TriggerRetry_WithNoWaitInFlight_ReturnsFalse()
    {
        var controller = new InboundReconnectController();

        controller.TriggerRetry().Should().BeFalse();
    }

    [Fact]
    public async Task TriggerRetry_CollapsesAnInFlightWait_AndClearsTheCountdown()
    {
        var controller = new InboundReconnectController();
        controller.RecordFailure(); // establishes a NextRetryAtUtc to clear
        var ct = TestContext.Current.CancellationToken;

        var wait = controller.WaitAsync(TimeSpan.FromMinutes(10), ct);
        await WaitUntilTrueAsync(() => TryTriggerRetry(controller), ct);

        await wait.WaitAsync(AssertionTimeout, ct);
        controller.NextRetryAtUtc.Should().BeNull("a manual retry clears the stale pre-click countdown");
    }

    [Fact]
    public async Task Interrupt_CollapsesAnInFlightWait()
    {
        var controller = new InboundReconnectController();
        var ct = TestContext.Current.CancellationToken;

        var wait = controller.WaitAsync(TimeSpan.FromMinutes(10), ct);
        await WaitUntilTrueAsync(() =>
        {
            controller.Interrupt();
            return wait.IsCompleted;
        }, ct);

        await wait.WaitAsync(AssertionTimeout, ct);
    }

    [Fact]
    public async Task WaitAsync_HonoursTheOuterCancellationToken()
    {
        var controller = new InboundReconnectController();
        using var cts = new CancellationTokenSource();

        var wait = controller.WaitAsync(TimeSpan.FromMinutes(10), cts.Token);
        await cts.CancelAsync();

        await wait.WaitAsync(AssertionTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>TriggerRetry only succeeds once <see cref="InboundReconnectController.WaitAsync"/>
    /// has actually assigned its CancellationTokenSource - poll briefly
    /// rather than assume a fixed delay is enough on a loaded CI box.</summary>
    private static bool TryTriggerRetry(InboundReconnectController controller) => controller.TriggerRetry();

    private static async Task WaitUntilTrueAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition did not become true in time.");
            }
            await Task.Delay(10, ct);
        }
    }
}
