using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The UDP datagram bearer between two real DAPPS daemons - no radio, no
/// AX.25 session, just the fire-and-forget UDP codec (Plan A0.4, the
/// MeshCore-style stand-in). Reuses <see cref="TwoInstanceLinbpqFixture"/>'s
/// two BPQs purely so each daemon has a node to be "reachable" on at
/// startup; the message itself never goes near AGW or AXIP, which the
/// test proves by checking BPQ's own monitor saw no session between the
/// two application callsigns at all.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class UdpBearerIntegrationTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> running = [];
    private AirMonitor air = null!;

    public async ValueTask InitializeAsync()
    {
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        await air.DisposeAsync();
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AMessage_GoesOverTheUdpBearer_NotOverAgw()
    {
        var ct = TestContext.Current.CancellationToken;
        var udpPortA = FreeUdpPort();
        var udpPortB = FreeUdpPort();

        var a = await StartNodeAsync("udpA", fixture.ApplCallA, fixture.AgwPortA, udpPortA,
            [new(fixture.ApplCallB, BearerPort: 0, UdpEndpoint: $"127.0.0.1:{udpPortB}")], ct);
        var b = await StartNodeAsync("udpB", fixture.ApplCallB, fixture.AgwPortB, udpPortB,
            [new(fixture.ApplCallA, BearerPort: 0, UdpEndpoint: $"127.0.0.1:{udpPortA}")], ct);

        var payload = "over UDP, not the air"u8.ToArray();
        var id = await a.SubmitAsync("udp", b.Callsign, payload, ct, ttl: 600);

        var got = (await b.WaitForInboundAsync("udp", 1, TimeSpan.FromSeconds(30), ct)).Single();
        got.Id.Should().Be(id);
        got.Payload.Should().Equal(payload);
        got.SourceCallsign.Should().Be(a.Callsign, "the UDP codec stamps the link-source callsign in band (codec v3+)");

        // The two BPQs are only up so each daemon has a node to be
        // "reachable" on; the real proof this went over UDP is that no
        // DAPPS session ever opened between the two application
        // callsigns on the air at all.
        air.SentBy("A").Should().NotContain(f => f.Contains($"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <", StringComparison.Ordinal),
            "the message must have gone by UDP, not a DAPPSv1 session over AGW/AXIP\n" + air.Transcript());
    }

    private static int FreeUdpPort()
    {
        using var l = new UdpClient(0);
        return ((IPEndPoint)l.Client.LocalEndPoint!).Port;
    }

    private async Task<DappsDaemon> StartNodeAsync(
        string name, string callsign, int agwPort, int udpListenPort, DappsDaemon.Neighbour[] neighbours, CancellationToken ct)
    {
        var settings = new Dictionary<string, string> { ["DAPPS_UDP_LISTEN_PORT"] = udpListenPort.ToString() };
        var node = await DappsDaemon.StartAsync(
            name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex, neighbours, settings, ct);
        running.Add(node);
        return node;
    }
}
