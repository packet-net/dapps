using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;

namespace dapps.core.tests.Integration;

/// <summary>
/// AGW discovery on XRouter with RHPv2 as the bearer, as
/// docs/connect/xrouter.md sets it up: two daemons on one XRouter, each
/// listening for calls over RHPv2 and with an AGW discovery channel on the
/// loopback port, hear each other's beacons.
///
/// The AGW connection only listens then (Tom's decision for #272: an
/// RHPv2 node never registers its callsign on AGW), and XRouter drops an
/// 'M' from a socket that registered no callsign without a word. So the
/// beacons go out over RHPv2, as datagrams from the daemon's callsign,
/// and are heard on the AGW monitor.
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterAgwDiscoveryIntegrationTests(XrouterIntegrationFixture fixture)
{
    private static readonly TimeSpan HearWithin = TimeSpan.FromSeconds(60);

    // None of the node's callsign (G9DUM), nor the other XRouter tests'.
    private const string CallA = "G9DSA-7";
    private const string CallB = "G9DSB-7";

    [Fact]
    public async Task WithRhpv2AsTheBearer_BeaconsGoBothWays_AndNeitherDaemonRegistersOnAgw()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await StartAsync("xr-disc-a", CallA, ct);
        await using var b = await StartAsync("xr-disc-b", CallB, ct);

        // The channel is the node's own number for the port, PORT=1 (#233).
        var key = (fixture.BearerPortIndex + 1).ToString();
        await WaitForLogAsync(a, "so the AGW connection only hears them", b, ct);
        await WaitForLogAsync(b, $"heard {CallA} via agw/{key}", a, ct);
        await WaitForLogAsync(a, $"heard {CallB} via agw/{key}", b, ct);

        foreach (var d in new[] { a, b })
        {
            d.Log.Should().NotContain("announce on agw/");
            d.Log.Should().NotContain("bearer 'agw' failed to start");
        }

        // Calls to each still reach its RHPv2 listener: nothing took the
        // callsign on AGW (XRouter would have handed the calls there).
        var message = "Hello B, heard your beacon"u8.ToArray();
        await a.SubmitAsync("chat", CallB, message, ct);
        var got = (await b.WaitForInboundAsync("chat", 1, HearWithin, ct)).Single();
        got.Payload.Should().Equal(message);
    }

    private async Task<DappsDaemon> StartAsync(string name, string callsign, CancellationToken ct)
    {
        var node = NodeAttachment.Rhp(fixture.Host, fixture.RhpPort, fixture.BearerPortIndex);
        var peer = callsign == CallA ? CallB : CallA;
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_AGW_PORT"] = fixture.AgwPort.ToString(),
            ["DAPPS_SESSION_TAIL_SECONDS"] = "0",
        };
        return await DappsDaemon.StartAsync(name, callsign, node, [new(peer, fixture.BearerPortIndex)], settings, ct, discoveryChannels:
        [
            new DbDiscoveryChannel
            {
                Bearer = "agw", ChannelKey = (fixture.BearerPortIndex + 1).ToString(), LinkClass = LinkClass.VhfUhfFm,
                BeaconIntervalSeconds = 10, AdvertisedTtlSeconds = 600, CostHint = 1,
            },
        ]);
    }

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
