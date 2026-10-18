using System.Net.Sockets;
using dapps.client.Transport.Agw;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests.Integration;

[Collection("Linbpq integration")]
[Trait("Category", "Integration")]
public class AgwIntegrationTests(LinbpqIntegrationFixture fixture)
{
    /// <summary>
    /// Frame format end-to-end against real BPQ. If our 36-byte AGW header
    /// layout is wrong (offsets, LE DataLength, callsign field shape),
    /// BPQ won't reply to an `R` (version) request - so a passing version
    /// exchange is strong validation of the frame layer.
    /// </summary>
    [Fact]
    public async Task AgwVersionHandshake_AgainstRealBpq_Succeeds()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(fixture.Host, fixture.AgwPort, ctSource.Token);
        var framing = new AgwFrameTransport(tcp.GetStream());

        await framing.WriteFrameAsync(
            new AgwFrame(0, 'R', 0, "", "", []),
            ctSource.Token);

        var reply = await framing.ReadFrameAsync(ctSource.Token);

        reply.Kind.Should().Be('R');
        reply.Payload.Length.Should().Be(8);
        // AGWAPI.c hard-codes AGWVersion = {2003, 999} - locks the wire
        // format in (LE uint32 pair).
        var major = BitConverter.ToUInt32(reply.Payload, 0);
        var minor = BitConverter.ToUInt32(reply.Payload, 4);
        (major, minor).Should().Be(((uint)2003, (uint)999));
    }

    /// <summary>
    /// Connect-handshake error path. Asking BPQ to dial a callsign that has
    /// no route configured drives BPQ through its full AX.25 retry cycle and
    /// it eventually emits a 'd' (disconnect) frame back. Our transport
    /// surfaces that as IOException.
    ///
    /// This validates: the X-register-then-C-connect sequence is correct,
    /// our 'd' frame parser captures the disconnect message, AgwOutboundTransport
    /// doesn't hang on connect failure.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ToUnreachableCallsign_ThrowsWithDisconnectMessage()
    {
        // BPQ's AX.25 retry exhaust is ~38s; give the test some headroom.
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var links = new StandaloneAgwLinkSource(fixture.Host, fixture.AgwPort, fixture.LocalCallsign, NullLoggerFactory.Instance);
        var transport = new AgwOutboundTransport(links, NullLoggerFactory.Instance);

        var act = async () => await transport.ConnectAsync(
            localCallsign: fixture.LocalCallsign,
            remoteCallsign: fixture.UnreachableCallsign,
            bearerPort: fixture.BearerPortIndex,
            stoppingToken: ctSource.Token);

        var ex = await act.Should().ThrowAsync<IOException>();
        ex.Which.Message.Should().Contain(fixture.UnreachableCallsign);
    }
}
