using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="OutboundDestinationBackoff"/> - per-destination cooldown
/// for the outbound forwarder, so a destination that keeps rejecting at
/// the application layer (accepts the AX.25 connect but never gives the
/// DAPPSv1&gt; prompt) doesn't get re-dialled on every 5s forwarder tick.
/// </summary>
public sealed class OutboundDestinationBackoffTests
{
    [Fact]
    public void IsInCooldown_UnknownDestination_ReturnsFalse()
    {
        var backoff = new OutboundDestinationBackoff();

        backoff.IsInCooldown("N0DEST", out var nextRetryAtUtc).Should().BeFalse();
        nextRetryAtUtc.Should().BeNull();
    }

    [Fact]
    public void RecordFailure_PutsTheDestinationInCooldown()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var backoff = new OutboundDestinationBackoff(clock);

        var nextRetryAtUtc = backoff.RecordFailure("N0DEST");

        nextRetryAtUtc.Should().Be(DateTimeOffset.Parse("2026-05-01T00:00:10Z"));
        backoff.IsInCooldown("N0DEST", out var status).Should().BeTrue();
        status.Should().Be(nextRetryAtUtc);
    }

    [Fact]
    public void IsInCooldown_AfterTheDelayElapses_ReturnsFalse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var backoff = new OutboundDestinationBackoff(clock);
        backoff.RecordFailure("N0DEST");

        clock.Advance(TimeSpan.FromSeconds(11));

        backoff.IsInCooldown("N0DEST", out var nextRetryAtUtc).Should().BeFalse();
        nextRetryAtUtc.Should().NotBeNull("the schedule still exists - it's just no longer in the cooldown window");
    }

    [Fact]
    public void RecordFailure_Repeatedly_EscalatesJustForThatDestination()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var backoff = new OutboundDestinationBackoff(clock);
        var now = clock.GetUtcNow();

        backoff.RecordFailure("N0DEST").Should().Be(now + TimeSpan.FromSeconds(10));
        backoff.RecordFailure("N0DEST").Should().Be(now + TimeSpan.FromSeconds(10));
        backoff.RecordFailure("N0DEST").Should().Be(now + TimeSpan.FromSeconds(10));
        backoff.RecordFailure("N0DEST").Should().Be(now + TimeSpan.FromSeconds(30));

        // A different destination has never failed - unaffected.
        backoff.IsInCooldown("N0OTHER", out _).Should().BeFalse();
    }

    [Fact]
    public void RecordSuccess_ClearsAnExistingCooldown()
    {
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("N0DEST");
        backoff.IsInCooldown("N0DEST", out _).Should().BeTrue();

        backoff.RecordSuccess("N0DEST");

        backoff.IsInCooldown("N0DEST", out var nextRetryAtUtc).Should().BeFalse();
        nextRetryAtUtc.Should().BeNull();
    }

    [Fact]
    public void RecordSuccess_ForADestinationThatNeverFailed_IsANoop()
    {
        var backoff = new OutboundDestinationBackoff();

        var act = () => backoff.RecordSuccess("N0DEST");

        act.Should().NotThrow();
        backoff.IsInCooldown("N0DEST", out _).Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_AfterSuccess_RestartsAtTheFastEndOfTheRamp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var backoff = new OutboundDestinationBackoff(clock);
        backoff.RecordFailure("N0DEST");
        backoff.RecordFailure("N0DEST");
        backoff.RecordFailure("N0DEST");
        backoff.RecordFailure("N0DEST"); // now at the 30s tier
        backoff.RecordSuccess("N0DEST");

        var nextRetryAtUtc = backoff.RecordFailure("N0DEST");

        nextRetryAtUtc.Should().Be(clock.GetUtcNow() + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void DestinationMatching_IsCaseInsensitive()
    {
        var backoff = new OutboundDestinationBackoff();
        backoff.RecordFailure("n0dest-1");

        backoff.IsInCooldown("N0DEST-1", out _).Should().BeTrue("callsigns are conventionally upper-case but shouldn't require exact casing to match");
    }
}
