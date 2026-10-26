using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// A neighbour's mail goes on its call to us whatever SSID that call
/// arrived with (#417), without losing what the exact match already did:
/// the exact call+SSID still wins, a sibling of ours never stands in, our
/// own call to another SSID is another neighbour's, and the call ending
/// wakes the forwarder. The exact match winning at the forwarder is in
/// <see cref="OutboundMessageManagerTests"/>.
/// </summary>
public sealed class SessionHandoffAnySsidTests
{
    private const string Us = "N0US";

    private static ExchangeSession Session(string peer, bool dialled) =>
        new(new MemoryStream(), peer, dialled, new ExchangeSettings(), null, NullLoggerFactory.Instance);

    [Fact]
    public void TryHand_ACallFromAnotherSsid_TakesTheBatch()
    {
        var directory = new SessionDirectory();
        directory.Register("N0DEST-6", Session("N0DEST-6", dialled: false));

        directory.TryHand("N0DEST", new RecordingBatch(), Us).Should().BeTrue();
    }

    [Fact]
    public void TryHand_OurPollOfAnotherSsid_DoesNotTakeTheBatch()
    {
        var directory = new SessionDirectory();
        directory.Register("N0DEST-6", Session("N0DEST-6", dialled: true));

        directory.TryHand("N0DEST", new RecordingBatch(), Us).Should().BeFalse(
            "our poll of N0DEST-6 is another neighbour's session");
    }

    [Fact]
    public void TryHand_ACallFromASiblingOfOurs_NeverStandsIn()
    {
        var directory = new SessionDirectory();
        directory.Register("N0US-6", Session("N0US-6", dialled: false));

        directory.TryHand("N0US-2", new RecordingBatch(), Us).Should().BeFalse();
    }

    [Fact]
    public void IsStationActive_TheExactPeer_EitherDirection()
    {
        var registry = new PeerSessionRegistry();
        using (registry.Acquire("N0DEST", "outbound"))
        {
            registry.IsStationActive("N0DEST", Us, out var direction).Should().BeTrue();
            direction.Should().Be("outbound");
        }
        registry.IsStationActive("N0DEST", Us, out _).Should().BeFalse();
    }

    [Fact]
    public void IsStationActive_ACallFromAnotherSsid_CountsUntilItEnds()
    {
        var registry = new PeerSessionRegistry();
        var lease = registry.Acquire("N0DEST-6", "inbound");

        registry.IsStationActive("N0DEST", Us, out var direction).Should().BeTrue();
        direction.Should().Be("inbound");
        registry.IsActive("N0DEST", out _).Should().BeFalse("the exact check is unchanged");

        lease.Dispose();
        registry.IsStationActive("N0DEST", Us, out _).Should().BeFalse();
    }

    [Fact]
    public void IsStationActive_OurCallToAnotherSsid_DoesNotCount()
    {
        var registry = new PeerSessionRegistry();
        using var lease = registry.Acquire("N0DEST-6", "outbound");

        registry.IsStationActive("N0DEST", Us, out var direction).Should().BeFalse();
        direction.Should().BeNull();
    }

    [Fact]
    public void IsStationActive_ACallFromASiblingOfOurs_DoesNotCount()
    {
        var registry = new PeerSessionRegistry();
        using var lease = registry.Acquire("N0US-6", "inbound");

        registry.IsStationActive("N0US-2", Us, out _).Should().BeFalse();
    }

    [Fact]
    public void IsStationActive_ADifferentStation_DoesNotCount()
    {
        var registry = new PeerSessionRegistry();
        using var lease = registry.Acquire("N0OTHER-6", "inbound");

        registry.IsStationActive("N0DEST", Us, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheCallFromAnotherSsidEnding_WakesTheForwarder()
    {
        var wakeup = new ForwarderWakeup();
        var registry = new PeerSessionRegistry(wakeup);
        var lease = registry.Acquire("N0DEST-6", "inbound");
        await wakeup.WaitAsync(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);

        lease.Dispose();

        (await wakeup.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken))
            .Should().BeTrue("the mail held back for N0DEST can be dialled now");
    }
}
