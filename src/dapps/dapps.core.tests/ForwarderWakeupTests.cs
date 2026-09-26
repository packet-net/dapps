using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public sealed class ForwarderWakeupTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AWakeAlreadyPending_IsSeenAtOnce()
    {
        var wakeup = new ForwarderWakeup();
        wakeup.Wake();

        (await wakeup.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public async Task NoWake_TimesOut()
    {
        (await new ForwarderWakeup().WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task SeveralWakes_CoalesceIntoOne()
    {
        var wakeup = new ForwarderWakeup();
        wakeup.Wake();
        wakeup.Wake();
        wakeup.Wake();

        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeFalse(
            "one run after a burst of wakes takes everything queued by then");
    }

    [Fact]
    public async Task ATimedOutWait_DoesNotSwallowTheNextWake()
    {
        var wakeup = new ForwarderWakeup();
        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeFalse();

        wakeup.Wake();

        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task TheLastSessionWithAPeerEnding_WakesTheForwarder()
    {
        var wakeup = new ForwarderWakeup();
        var registry = new PeerSessionRegistry(wakeup);
        var first = registry.Acquire("G5ALF-3", "inbound");
        var second = registry.Acquire("G5ALF-3", "outbound");

        first.Dispose();
        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeFalse(
            "another session with the peer is still open");

        second.Dispose();
        (await wakeup.WaitAsync(Short, TimeProvider.System, TestContext.Current.CancellationToken)).Should().BeTrue();
    }
}
