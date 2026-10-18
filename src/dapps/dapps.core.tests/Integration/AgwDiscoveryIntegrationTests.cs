using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;

namespace dapps.core.tests.Integration;

/// <summary>
/// AGW discovery on real BPQ, end to end: two daemons, each with an AGW
/// discovery channel on the AXIP port, beacon to each other over the
/// daemon's one AGW connection (#272) and each hears the other's beacon
/// through BPQ's monitor. The fixture's AXIP port broadcasts to the
/// "DAPPS" destination the beacons are sent to.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class AgwDiscoveryIntegrationTests(TwoInstanceLinbpqFixture fixture)
{
    private static readonly TimeSpan HearWithin = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TwoDaemons_HearEachOthersBeacons_OverTheirOneAgwConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await StartAsync("disc-a", fixture.ApplCallA, fixture.NodeA, ct);
        await using var b = await StartAsync("disc-b", fixture.ApplCallB, fixture.NodeB, ct);

        var key = fixture.AxipPortIndex.ToString();
        await WaitForLogAsync(a, $"heard {fixture.ApplCallB} via agw/{key}", b, ct);
        await WaitForLogAsync(b, $"heard {fixture.ApplCallA} via agw/{key}", a, ct);

        foreach (var d in new[] { a, b })
        {
            d.Log.Should().NotContain("AGW UI discovery read failed");
            d.Log.Should().NotContain("bearer 'agw' failed to start");
        }
    }

    [Fact]
    public async Task WithRhpv2AsTheBearer_TheAgwLinkOnlyListens_AndBeaconsStillGoBothWays()
    {
        // Tom's decision for #272: AGW discovery with another bearer runs
        // the AGW connection unregistered. BPQ sends an 'M' from the
        // header's CallFrom and shows the monitor to any socket, so the
        // beacons work without the 'X'. A's RHPv2 port here leads nowhere:
        // only its AGW connection matters.
        var ct = TestContext.Current.CancellationToken;
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_NODE_BEARER"] = "rhpv2",
            ["DAPPS_RHP_PORT"] = "1",
        };
        await using var a = await StartAsync("listen-a", fixture.ApplCallA, fixture.NodeA, ct, settings);
        await using var b = await StartAsync("disc-b", fixture.ApplCallB, fixture.NodeB, ct);

        var key = fixture.AxipPortIndex.ToString();
        await WaitForLogAsync(a, "only sends and hears discovery beacons", b, ct);
        await WaitForLogAsync(b, $"heard {fixture.ApplCallA} via agw/{key}", a, ct);
        await WaitForLogAsync(a, $"heard {fixture.ApplCallB} via agw/{key}", b, ct);
    }

    private static Task<DappsDaemon> StartAsync(
        string name, string callsign, NodeAttachment node, CancellationToken ct, IReadOnlyDictionary<string, string>? settings = null) =>
        DappsDaemon.StartAsync(name, callsign, node, [], settings, ct, waitForNode: settings is null, discoveryChannels:
        [
            new DbDiscoveryChannel
            {
                Bearer = "agw", ChannelKey = node.BearerPort.ToString(), LinkClass = LinkClass.VhfUhfFm,
                BeaconIntervalSeconds = 10, AdvertisedTtlSeconds = 600, CostHint = 1,
            },
        ]);

    private static async Task WaitForLogAsync(DappsDaemon daemon, string text, DappsDaemon other, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HearWithin;
        while (DateTime.UtcNow < deadline)
        {
            if (daemon.Log.Contains(text, StringComparison.Ordinal)) return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"{daemon.Name} never logged \"{text}\" within {HearWithin.TotalSeconds:F0}s.\n{daemon.Tail(80)}\n{other.Tail(40)}");
    }
}
