using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// #431 on real LinBPQ: two DAPPS peers behind one node, reached through
/// it, each called from its own Dial as SSID of A's callsign, have their
/// sessions open at the same time. Without Dial as they share one link
/// and go one at a time (#419, #428), as before.
///
///     DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B   (B's prompt, APPLB)
///                                     -AXIP- BPQ-C -AGW- DAPPS C   (C 2 N0CCC-9 at B's prompt)
///
/// A's neighbours are both reached through N0BBB's node: B's DAPPS by its
/// application at that prompt, C's by a downlink from it. A holds its
/// sessions for 120 s, so a session that is still open when the other
/// peer's message arrives shows the two ran at once.
/// </summary>
[Collection("Linbpq three-instance integration")]
[Trait("Category", "Integration")]
public sealed class DialAsIntegrationTests(ThreeInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(90);
    private readonly List<IAsyncDisposable> running = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        await Task.Delay(3000);
    }

    private DappsDaemon.Neighbour[] ThroughB(string? dialAsForB, string? dialAsForC) =>
    [
        new(fixture.ApplCallB, fixture.AxipPortIndex, SessionTailSeconds: 120,
            Script: new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep("APPLB", "DAPPSv1>", 30)]),
            ConnectVia: fixture.CallsignB, DialAs: dialAsForB),
        new(fixture.ApplCallC, fixture.AxipPortIndex, SessionTailSeconds: 120,
            Script: new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep($"C 2 {fixture.ApplCallC}", "DAPPSv1>", 60)]),
            ConnectVia: fixture.CallsignB, DialAs: dialAsForC),
    ];

    [Fact]
    public async Task TwoPeersBehindOneNode_CalledFromTheirOwnDialAsSsids_HaveSessionsOpenAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartAsync("dialasA", fixture.ApplCallA, fixture.AgwPortA, ThroughB(fixture.DialAsA1, fixture.DialAsA2), sessionTail: 120, ct);
        var b = await StartAsync("dialasB", fixture.ApplCallB, fixture.AgwPortB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], sessionTail: 120, ct);
        var c = await StartAsync("dialasC", fixture.ApplCallC, fixture.AgwPortC, [new(fixture.ApplCallB, fixture.AxipPortIndex)], sessionTail: 120, ct);

        var forB = Encoding.UTF8.GetBytes("for B, from N0AAA-10");
        var forC = Encoding.UTF8.GetBytes("for C, from N0AAA-11");
        await a.SubmitAsync("dialas", b.Callsign, forB, ct);
        await a.SubmitAsync("dialas", c.Callsign, forC, ct);

        DappsDaemon.Inbound gotB, gotC;
        try
        {
            gotB = (await b.WaitForInboundAsync("dialas", 1, Delivery, ct)).Single();
            gotC = (await c.WaitForInboundAsync("dialas", 1, Delivery, ct)).Single();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\n" + Transcript(a, b, c), ex);
        }

        gotB.Payload.Should().Equal(forB);
        gotC.Payload.Should().Equal(forC);
        a.Log.Should().NotContain("Session with N0BBB-9 closed",
            "B's session is held for 120 s, so it was still open when C's mail went\n" + Transcript(a, b, c));
        a.Log.Should().NotContain("waiting for the link to N0BBB", "neither session waited for the other's link");
        a.Log.Should().NotContain("already has an outbound session open");

        b.Log.Should().Contain($"Inbound session from {fixture.DialAsA1}", "B's DAPPS sees the Dial as SSID on the link\n" + b.Tail());
        c.Log.Should().Contain("Inbound session from N0AAA-4", "BPQ's downlink from B inverts N0AAA-11's SSID\n" + c.Tail());
        gotB.ViaCallsign.Should().Be(a.Callsign, "the exchange's node= names A's own callsign");
        gotC.ViaCallsign.Should().Be(a.Callsign);
        gotB.SourceCallsign.Should().Be(a.Callsign);
        gotC.SourceCallsign.Should().Be(a.Callsign);
    }

    [Fact]
    public async Task WithoutDialAs_TwoPeersBehindOneNode_GoOneAtATime_AsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await StartAsync("serialA", fixture.ApplCallA, fixture.AgwPortA, ThroughB(null, null), sessionTail: 120, ct);
        var b = await StartAsync("serialB", fixture.ApplCallB, fixture.AgwPortB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], sessionTail: 120, ct);
        var c = await StartAsync("serialC", fixture.ApplCallC, fixture.AgwPortC, [new(fixture.ApplCallB, fixture.AxipPortIndex)], sessionTail: 120, ct);

        await a.SubmitAsync("dialas", b.Callsign, Encoding.UTF8.GetBytes("for B"), ct);
        await a.SubmitAsync("dialas", c.Callsign, Encoding.UTF8.GetBytes("for C"), ct);

        try
        {
            await b.WaitForInboundAsync("dialas", 1, Delivery, ct);
            await c.WaitForInboundAsync("dialas", 1, Delivery, ct);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\n" + Transcript(a, b, c), ex);
        }

        a.Log.Should().Contain("waiting for the link to N0BBB",
            "both are called from N0AAA-9, one link to N0BBB, so the second waits and the first gives way\n" + a.Tail(80));
        b.Log.Should().Contain($"Inbound session from {fixture.ApplCallA}");
    }

    private static string Transcript(params DappsDaemon[] daemons) => string.Join("\n", daemons.Select(d => d.Tail(60)));

    private async Task<DappsDaemon> StartAsync(
        string name, string callsign, int agwPort, DappsDaemon.Neighbour[] neighbours, int sessionTail, CancellationToken ct)
    {
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = sessionTail.ToString() };
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex, neighbours, settings, ct);
        running.Add(node);
        return node;
    }
}
