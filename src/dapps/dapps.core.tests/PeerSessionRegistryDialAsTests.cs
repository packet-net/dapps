using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// #431: a session from one of our Dial as callsigns is another AX.25 link
/// than one from our own callsign to the same station, so the registry
/// keeps it apart: neither blocks nor retires the other. Sessions from our
/// own callsign behave exactly as before (<see cref="PeerSessionRegistryTests"/>).
/// </summary>
public sealed class PeerSessionRegistryDialAsTests
{
    [Fact]
    public void TwoDialAsCallsigns_EachHoldTheirOwnLinkToOneNode()
    {
        var registry = new PeerSessionRegistry();
        using var first = registry.TryAcquire("M1BFP-1", "outbound", out _, linkPort: 2, local: "MB7NPW-4");
        using var second = registry.TryAcquire("M1BFP-1", "outbound", out _, linkPort: 2, local: "MB7NPW-5");

        first.Should().NotBeNull();
        second.Should().NotBeNull("another Dial as callsign is another callsign pair, so another link");
        registry.TryAcquire("M1BFP-1", "outbound", out var direction, linkPort: 2, local: "mb7npw-4")
            .Should().BeNull("the same pair is still one link");
        direction.Should().Be("outbound");
    }

    [Fact]
    public void OurOwnCallsignsLink_AndADialAsLink_DoNotBlockEachOther()
    {
        var registry = new PeerSessionRegistry();
        using var own = registry.TryAcquire("M1BFP-1", "outbound", out _, linkPort: 2);
        using var dialAs = registry.TryAcquire("M1BFP-1", "outbound", out _, linkPort: 2, local: "MB7NPW-4");

        own.Should().NotBeNull();
        dialAs.Should().NotBeNull();
        registry.IsActive("M1BFP-1", out _).Should().BeTrue();
        registry.IsActive("M1BFP-1", out _, local: "MB7NPW-4").Should().BeTrue();
        registry.IsActive("M1BFP-1", out _, local: "MB7NPW-5").Should().BeFalse();
        registry.TryAcquire("M1BFP-1", "outbound", out _).Should().BeNull("our own callsign's link is still taken");
    }

    [Fact]
    public void ConnectingOnOneLink_NeverRetiresTheOther()
    {
        var registry = new PeerSessionRegistry();
        using var inbound = registry.Acquire("GB7AGM-3", "inbound", linkPort: 2);
        using var dialAs = registry.TryAcquire("GB7AGM-3", "outbound", out _, linkPort: 2, local: "MB7NPW-4")!;

        registry.Connected(dialAs).Should().BeFalse("nothing else was on that link");
        inbound.Retired.IsCancellationRequested.Should().BeFalse("the peer's call to our own callsign is another link");
        registry.OwnCallsSettledAsync(inbound).IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void AnOutboundDialAsLease_IsListedUnderThePeer_AndGoesWhenReleased()
    {
        var registry = new PeerSessionRegistry();
        var lease = registry.TryAcquire("m1bfp-1", "outbound", out _, local: "mb7npw-4")!;

        lease.Peer.Should().Be("M1BFP-1");
        lease.Local.Should().Be("MB7NPW-4");
        registry.Sessions("M1BFP-1", "MB7NPW-4").Should().ContainSingle();
        registry.Sessions("M1BFP-1").Should().BeEmpty();
        registry.AllSessions().Should().ContainSingle(s => s.Peer == "M1BFP-1");

        lease.Dispose();
        registry.IsActive("M1BFP-1", out _, local: "MB7NPW-4").Should().BeFalse();
        registry.AllSessions().Should().BeEmpty();
    }
}
