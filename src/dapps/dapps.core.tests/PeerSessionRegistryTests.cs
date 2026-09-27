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

    // #185: TryAcquire is the authoritative, race-free version of
    // "check IsActive, then Acquire" - the two steps happen under the
    // same lock, so nothing can register a session for the peer in the
    // gap between them the way it could between two separate calls.

    [Fact]
    public void TryAcquire_PeerIsIdle_SucceedsAndTheLeaseMarksItBusy()
    {
        var registry = new PeerSessionRegistry();

        var lease = registry.TryAcquire("M0AHN-3", "outbound", out var openDirection);

        lease.Should().NotBeNull();
        openDirection.Should().BeNull();
        registry.IsActive("M0AHN-3", out var direction).Should().BeTrue();
        direction.Should().Be("outbound");

        lease!.Dispose();
        registry.IsActive("M0AHN-3", out _).Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_PeerAlreadyHasASession_FailsAndReportsWhichDirection()
    {
        var registry = new PeerSessionRegistry();
        using var existing = registry.Acquire("M0AHN-3", "inbound");

        var lease = registry.TryAcquire("M0AHN-3", "outbound", out var openDirection);

        lease.Should().BeNull("a second link on the same callsign pair would reset the first");
        openDirection.Should().Be("inbound");
        registry.IsActive("M0AHN-3", out var direction).Should().BeTrue("the failed attempt must not disturb the existing lease");
        direction.Should().Be("inbound");
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

    // One session per peer: when two end up open with the same peer, the
    // link is the newest one's, and the older ones are retired.

    [Fact]
    public void ANewlyConnectedSession_RetiresTheOlderOne_ButNotItself()
    {
        var registry = new PeerSessionRegistry();
        using var older = registry.Acquire("M0AHN-3", "outbound");
        using var newer = registry.Acquire("M0AHN-3", "inbound");

        older.Retired.IsCancellationRequested.Should().BeTrue();
        newer.Retired.IsCancellationRequested.Should().BeFalse();
        registry.IsActive("M0AHN-3", out _).Should().BeTrue("a retired session still holds the peer until it has gone");
    }

    [Fact]
    public void ADialStillConnecting_IsNotRetired_AndRetiresTheOthersOnceItConnects()
    {
        // The crossing at one node: our dial is on its way when the peer's
        // call arrives, then BPQ confirms ours and moves the link to it.
        var registry = new PeerSessionRegistry();
        var dialling = registry.TryAcquire("M0AHN-3", "outbound", out _)!;
        using var inbound = registry.Acquire("M0AHN-3", "inbound");
        dialling.Retired.IsCancellationRequested.Should().BeFalse("it isn't connected yet");

        registry.Connected(dialling);

        inbound.Retired.IsCancellationRequested.Should().BeTrue();
        dialling.Retired.IsCancellationRequested.Should().BeFalse();
        dialling.Dispose();
    }

    [Fact]
    public void RetiringAPeer_LeavesOtherPeersAlone()
    {
        var registry = new PeerSessionRegistry();
        using var other = registry.Acquire("G5ALF-3", "inbound");
        using var older = registry.Acquire("M0AHN-3", "inbound");
        using var newer = registry.Acquire("M0AHN-3", "inbound");

        other.Retired.IsCancellationRequested.Should().BeFalse();
    }
}
