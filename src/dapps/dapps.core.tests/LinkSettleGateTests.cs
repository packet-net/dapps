using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="LinkSettleGate"/> on a fake clock: the whole point of the
/// gate is a wait, and none of these tests spend a millisecond in one.
/// </summary>
public sealed class LinkSettleGateTests
{
    private const string Key = "agw|G0LOCAL-1|G0REMA-1|0";
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task NoPriorRelease_DoesNotWait()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);

        var wait = gate.WaitAsync(Key, TestContext.Current.CancellationToken);

        wait.IsCompletedSuccessfully.Should().BeTrue("nothing has been released on this key, so there is nothing to settle");
        gate.PendingWait(Key).Should().Be(TimeSpan.Zero);
        await wait;
    }

    [Fact]
    public async Task AfterRelease_WaitsUntilTheFullDelayHasElapsed()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);

        var wait = gate.WaitAsync(Key, TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse("the link was released just now");

        clock.Advance(Delay - TimeSpan.FromMilliseconds(1));
        wait.IsCompleted.Should().BeFalse("one millisecond short of the delay is still inside it");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PendingWait_ShrinksAsTheClockAdvances()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);

        gate.PendingWait(Key).Should().Be(Delay);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        gate.PendingWait(Key).Should().Be(TimeSpan.FromMilliseconds(1500));
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        gate.PendingWait(Key).Should().Be(TimeSpan.Zero);
        clock.Advance(TimeSpan.FromHours(1));
        gate.PendingWait(Key).Should().Be(TimeSpan.Zero, "a long-settled key never reports a negative wait");
    }

    [Fact]
    public async Task ADifferentKey_IsNotPacedByThisOne()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);

        var other = gate.WaitAsync("agw|G0LOCAL-1|G0REMB-1|0", TestContext.Current.CancellationToken);

        other.IsCompletedSuccessfully.Should().BeTrue("only the same (bearer, local, remote, port) has a link to wait out");
        await other;
    }

    [Fact]
    public async Task ZeroDelay_DisablesTheGateEntirely()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, TimeSpan.Zero);
        gate.RecordRelease(Key);

        var wait = gate.WaitAsync(Key, TestContext.Current.CancellationToken);

        wait.IsCompletedSuccessfully.Should().BeTrue();
        gate.TrackedKeys.Should().Be(0, "with no delay there is nothing worth remembering");
        await wait;
    }

    [Fact]
    public async Task ASecondRelease_RestartsTheClockForThatKey()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);
        clock.Advance(TimeSpan.FromSeconds(1));
        gate.RecordRelease(Key);

        gate.PendingWait(Key).Should().Be(Delay, "the most recent release is the one the far end still has to absorb");
        var wait = gate.WaitAsync(Key, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        wait.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_AbortsTheWait()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitAsync(Key, cts.Token);
        cts.Cancel();

        await wait.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<OperationCanceledException>("shutdown must not be held up by a settle wait");
    }

    [Fact]
    public void SettledKeys_ArePrunedOnTheNextRelease()
    {
        var clock = new FakeTimeProvider();
        var gate = new LinkSettleGate(clock, Delay);
        gate.RecordRelease(Key);
        gate.RecordRelease("agw|G0LOCAL-1|G0REMB-1|0");
        gate.TrackedKeys.Should().Be(2);

        clock.Advance(Delay);
        gate.RecordRelease("agw|G0LOCAL-1|G0REMC-1|0");

        gate.TrackedKeys.Should().Be(1, "keys whose delay has fully elapsed are dropped, so a busy node's table stays bounded");
    }
}
