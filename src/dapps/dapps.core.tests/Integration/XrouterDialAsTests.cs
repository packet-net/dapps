using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #431 on a real XRouter, over RHPv2 and over AGW: A calls two neighbours
/// at once, each from its own Dial as SSID, and each sees that SSID on the
/// link and A's own callsign in the exchange (node=). Over RHPv2 each call
/// is an open with the Dial as callsign as its local call; over AGW it goes
/// on a connection of its own registered as that callsign.
///
///     DAPPS A -+- XRouter (port 1, loopback) -+- DAPPS B
///              |                              +- DAPPS C
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterDialAsTests(XrouterIntegrationFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(60);
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
    }

    [Theory]
    [InlineData("rhpv2")]
    [InlineData("agw")]
    public async Task TwoNeighbours_CalledFromTheirOwnDialAsSsids_SeeThemAndKnowItsUs(string bearer)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartAsync($"xda-a-{bearer}", "G9AAA-7", bearer,
            [new("G9BBB-7", fixture.BearerPortIndex, DialAs: "G9AAA-11"), new("G9CCC-7", fixture.BearerPortIndex, DialAs: "G9AAA-12")], ct);
        var b = await StartAsync($"xda-b-{bearer}", "G9BBB-7", bearer, [new("G9AAA-7", fixture.BearerPortIndex)], ct);
        var c = await StartAsync($"xda-c-{bearer}", "G9CCC-7", bearer, [new("G9AAA-7", fixture.BearerPortIndex)], ct);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("to B from -11"), ct);
        await a.SubmitAsync("chat", c.Callsign, Encoding.UTF8.GetBytes("to C from -12"), ct);

        DappsDaemon.Inbound gotB, gotC;
        try
        {
            gotB = (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Single();
            gotC = (await c.WaitForInboundAsync("chat", 1, Delivery, ct)).Single();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}\n{a.Tail(80)}", ex);
        }

        b.Log.Should().Contain("Inbound session from G9AAA-11", b.Tail());
        c.Log.Should().Contain("Inbound session from G9AAA-12", c.Tail());
        gotB.ViaCallsign.Should().Be(a.Callsign, "node= names A's own callsign");
        gotC.ViaCallsign.Should().Be(a.Callsign);
        gotB.SourceCallsign.Should().Be(a.Callsign);
        gotC.SourceCallsign.Should().Be(a.Callsign);
        a.Log.Should().NotContain("Session with G9BBB-7 closed", "both sessions are held for 120 s, so they were open at once");
        a.Log.Should().NotContain("Session with G9CCC-7 closed");
    }

    private async Task<DappsDaemon> StartAsync(string name, string callsign, string bearer, DappsDaemon.Neighbour[] neighbours, CancellationToken ct)
    {
        var node = bearer == "rhpv2"
            ? NodeAttachment.Rhp(fixture.Host, fixture.RhpPort, fixture.BearerPortIndex)
            : NodeAttachment.Agw(fixture.Host, fixture.AgwPort, fixture.BearerPortIndex);
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_SESSION_TAIL_SECONDS"] = "120",
            // On AGW, DAPPS reads XRouter's port numbers over RHPv2.
            ["DAPPS_RHP_PORT"] = fixture.RhpPort.ToString(),
        };
        var daemon = await DappsDaemon.StartAsync(name, callsign, node, neighbours, settings, ct);
        running.Add(daemon);
        return daemon;
    }
}
