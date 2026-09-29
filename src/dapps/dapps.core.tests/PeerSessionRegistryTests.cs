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

    // One session per peer and port: when two end up open with the same
    // peer on the same AGW port, the link is the newest one's, and the
    // older ones are retired.

    [Fact]
    public void ANewlyConnectedSession_RetiresTheOlderOne_ButNotItself()
    {
        var registry = new PeerSessionRegistry();
        using var older = registry.Acquire("M0AHN-3", "outbound", linkPort: 0);
        using var newer = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);

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
        var dialling = registry.TryAcquire("M0AHN-3", "outbound", out _, linkPort: 0)!;
        using var inbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        dialling.Retired.IsCancellationRequested.Should().BeFalse("it isn't connected yet");

        registry.Connected(dialling).Should().BeTrue("it took the link from another session");

        inbound.Retired.IsCancellationRequested.Should().BeTrue();
        dialling.Retired.IsCancellationRequested.Should().BeFalse();
        dialling.Dispose();
    }

    [Fact]
    public void RetiringAPeer_LeavesOtherPeersAlone()
    {
        var registry = new PeerSessionRegistry();
        using var other = registry.Acquire("G5ALF-3", "inbound", linkPort: 0);
        using var older = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        using var newer = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);

        other.Retired.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void ASessionWithThePeerOnAnotherPort_IsAnotherLink_AndIsLeftAlone()
    {
        // A peer reachable over RF on one port and AXIP on another.
        var registry = new PeerSessionRegistry();
        using var rf = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        var axip = registry.TryAcquire("M0AHN-3", "outbound", out _, linkPort: 2);
        axip.Should().BeNull("dialling a peer with a session open is still held back, whatever the port");

        using var axipInbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 2);

        rf.Retired.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void ALeaseWithNoPort_NeverRetiresAnything_AndIsNeverRetired()
    {
        // RHPv2: not measured, so it takes no part.
        var registry = new PeerSessionRegistry();
        using var rhp = registry.Acquire("M0AHN-3", "inbound");
        using var agw = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        using var rhpAgain = registry.Acquire("M0AHN-3", "inbound");

        rhp.Retired.IsCancellationRequested.Should().BeFalse();
        rhp.Retired.CanBeCanceled.Should().BeFalse();
        agw.Retired.IsCancellationRequested.Should().BeFalse();
    }

    // #205: an inbound session that arrives while our own call to the
    // peer is on its way waits for that call before it says anything.

    [Fact]
    public void OwnCallsSettled_WithNoCallOfOursOnItsWay_IsAlreadyDone()
    {
        var registry = new PeerSessionRegistry();
        using var ours = registry.Acquire("M0AHN-3", "outbound", linkPort: 0);   // connected: not on its way
        using var inbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);

        registry.OwnCallsSettledAsync(inbound).IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task OwnCallsSettled_WaitsForOurCall_WhichRetiresTheSessionWhenItConnects()
    {
        var registry = new PeerSessionRegistry();
        var dialling = registry.TryAcquire("M0AHN-3", "outbound", out _, linkPort: 0)!;
        using var inbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        var settled = registry.OwnCallsSettledAsync(inbound);
        settled.IsCompleted.Should().BeFalse("our call hasn't connected yet");

        registry.Connected(dialling);

        await settled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        inbound.Retired.IsCancellationRequested.Should().BeTrue("the link is our call's now, and the held session never said a word on it");
        dialling.Dispose();
    }

    [Fact]
    public async Task OwnCallsSettled_CompletesWhenOurCallGivesUp_AndTheSessionCarriesOn()
    {
        var registry = new PeerSessionRegistry();
        var dialling = registry.TryAcquire("M0AHN-3", "outbound", out _, linkPort: 0)!;
        using var inbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        var settled = registry.OwnCallsSettledAsync(inbound);

        dialling.Dispose();

        await settled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        inbound.Retired.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void OwnCallsSettled_IgnoresCallsOnAnotherPort_AndSessionsWithNoPort()
    {
        var registry = new PeerSessionRegistry();
        using var dialling = registry.TryAcquire("M0AHN-3", "outbound", out _, linkPort: 2)!;
        using var inbound = registry.Acquire("M0AHN-3", "inbound", linkPort: 0);
        using var rhp = registry.Acquire("M0AHN-3", "inbound");

        registry.OwnCallsSettledAsync(inbound).IsCompleted.Should().BeTrue("a call on another port is another link");
        registry.OwnCallsSettledAsync(rhp).IsCompleted.Should().BeTrue("RHPv2 takes no part");
    }
}
