using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// <see cref="PeerSessionRegistry"/> on its own: the bookkeeping the
/// #178 guard rests on. What the inbound bearers and the outbound
/// transport put into it, and what the forwarder does with it, are
/// covered alongside those classes.
/// </summary>
public sealed class PeerSessionRegistryTests
{
    [Fact]
    public void APeerIsActiveForExactlyTheLifeOfItsLease()
    {
        var registry = new PeerSessionRegistry();
        registry.IsActive("M0AHN-3", out _).Should().BeFalse();

        var lease = registry.Acquire("M0AHN-3", "inbound");
        registry.IsActive("M0AHN-3", out var direction).Should().BeTrue();
        direction.Should().Be("inbound");

        lease.Dispose();
        registry.IsActive("M0AHN-3", out _).Should().BeFalse();
    }

    [Fact]
    public void CallsignsMatchRegardlessOfCase_AndTheSsidIsPartOfTheIdentity()
    {
        var registry = new PeerSessionRegistry();
        using var lease = registry.Acquire("m0ahn-3", "inbound");
        registry.IsActive("M0AHN-3", out _).Should().BeTrue("the 'C' frame and the neighbour table may differ in case");
        registry.IsActive("M0AHN-2", out _).Should().BeFalse("AX.25 links are per call+SSID");
        registry.IsActive("M0AHN", out _).Should().BeFalse();
    }

    [Fact]
    public void OverlappingLeases_KeepThePeerActiveUntilTheLastOneGoes()
    {
        // AgwInboundService retires a stale entry for a pair while
        // registering its replacement; the peer must not read as idle
        // in between, or a forwarder tick in that gap would dial.
        var registry = new PeerSessionRegistry();
        var stale = registry.Acquire("M0AHN-3", "inbound");
        var fresh = registry.Acquire("M0AHN-3", "inbound");

        stale.Dispose();
        registry.IsActive("M0AHN-3", out _).Should().BeTrue();

        fresh.Dispose();
        registry.IsActive("M0AHN-3", out _).Should().BeFalse();
    }

    [Fact]
    public void DisposingALeaseTwice_DoesNotReleaseAnotherOne()
    {
        var registry = new PeerSessionRegistry();
        var first = registry.Acquire("M0AHN-3", "outbound");
        var second = registry.Acquire("M0AHN-3", "inbound");

        first.Dispose();
        first.Dispose();
        registry.IsActive("M0AHN-3", out var direction).Should().BeTrue();
        direction.Should().Be("inbound", "the surviving lease is the one reported");

        second.Dispose();
        registry.IsActive("M0AHN-3", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilIdle_CompletesWhenTheLastLeaseIsReleased_AndAtOnceForAnIdlePeer()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new PeerSessionRegistry();
        var lease = registry.Acquire("M0AHN-3", "inbound");

        var idle = registry.WaitUntilIdleAsync("M0AHN-3", ct);
        idle.IsCompleted.Should().BeFalse();

        lease.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(5), ct);

        await registry.WaitUntilIdleAsync("G5ALF-3", ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
