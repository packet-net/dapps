using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// DAPPS on XRouter the way docs/connect/xrouter.md sets it up, over RHPv2:
/// two real DAPPS daemons on one real XRouter, each with its own callsign,
/// and a message each way between them through the node's loopback port.
///
///     app -> DAPPS A -RHPv2- XRouter (port 1, loopback) -RHPv2- DAPPS B -> app
///
/// Each daemon binds and listens for its own callsign (the inbound side)
/// and opens a link to the other's when it has mail (the outbound side), so
/// the two messages cover both sides on both daemons.
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterRhpEndToEndTests(XrouterIntegrationFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(60);

    // Different base callsigns: DAPPS takes mail for its own base callsign,
    // whatever the SSID, as its own. And none of the node's (G9DUM).
    private const string CallA = "G9AAA-7";
    private const string CallB = "G9BBB-7";
    private readonly List<DappsDaemon> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in running) await daemon.DisposeAsync();
    }

    [Fact]
    public async Task AMessage_GoesEachWay_BetweenTwoDaemonsOnOneXrouter()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartAsync("xr-a", CallA, CallB, ct);
        var b = await StartAsync("xr-b", CallB, CallA, ct);

        var there = Encoding.UTF8.GetBytes("Hello B, this is A by way of XRouter");
        var thereId = await a.SubmitAsync("chat", b.Callsign, there, ct);
        var gotThere = (await WaitAsync(b, a, ct)).Single();
        gotThere.Id.Should().Be(thereId);
        gotThere.Payload.Should().Equal(there);
        gotThere.SourceCallsign.Should().Be(a.Callsign);

        var back = Encoding.UTF8.GetBytes("Hello A, B here, heard you fine");
        var backId = await b.SubmitAsync("chat", a.Callsign, back, ct);
        var gotBack = (await WaitAsync(a, b, ct)).Single();
        gotBack.Id.Should().Be(backId);
        gotBack.Payload.Should().Equal(back);
        gotBack.SourceCallsign.Should().Be(b.Callsign);
    }

    private async Task<DappsDaemon> StartAsync(string name, string callsign, string peer, CancellationToken ct)
    {
        var node = NodeAttachment.Rhp(fixture.Host, fixture.RhpPort, fixture.BearerPortIndex);
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var daemon = await DappsDaemon.StartAsync(
            name, callsign, node, [new(peer, fixture.BearerPortIndex)], settings, ct);
        running.Add(daemon);
        return daemon;
    }

    /// <summary>Wait for one chat message at <paramref name="to"/>; on a
    /// timeout, the sender's log goes in the failure too.</summary>
    private static async Task<IReadOnlyList<DappsDaemon.Inbound>> WaitAsync(DappsDaemon to, DappsDaemon from, CancellationToken ct)
    {
        try
        {
            return await to.WaitForInboundAsync("chat", 1, Delivery, ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}\n{from.Tail()}", e);
        }
    }
}
