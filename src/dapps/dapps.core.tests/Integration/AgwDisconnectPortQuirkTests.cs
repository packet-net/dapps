using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests.Integration;

/// <summary>
/// Pins down, against real linbpq, the AGW behaviour the inbound
/// session table has to cope with - and proves dapps copes.
///
/// BPQ builds its disconnect notification to an application in
/// <c>SendDisMsgtoAppl</c> (AGWAPI.c), which stamps the frame's port
/// field with <c>sockptr-&gt;AGWRXHeader.Port</c>: the port of the last
/// frame BPQ <em>received from the application</em> on that socket, not
/// the port of the session that ended. dapps sends a 'G' keepalive on
/// port 0 every 15s, so most of the time the 'd' for a session on port
/// N arrives saying port 0. A session table keyed on (local, remote,
/// port) that insists on an exact match never sees that session end;
/// its entry goes stale, and the peer's next connect for the pair is
/// mistaken for a duplicate. That was the field failure behind #171,
/// #175 and #176.
///
/// The first test is a characterisation of BPQ (raw AGW clients on
/// both sides, no dapps). The second runs the real
/// <see cref="AgwInboundService"/> on the receiving node with a fake
/// clock, forces a keepalive out between the session start and the
/// hangup, and checks the session is closed, the redial is prompted,
/// and the new session works.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class AgwDisconnectPortQuirkTests(TwoInstanceLinbpqFixture fixture)
{
    private byte SessionPort => (byte)fixture.AxipPortIndex;

    [Fact]
    public async Task Bpq_StampsItsDisconnectNotification_WithThePortOfOurLastFrame_NotTheSessions()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = ctSource.Token;

        using var tcpA = new TcpClient();
        await tcpA.ConnectAsync(fixture.Host, fixture.AgwPortA, ct);
        var sideA = new AgwFrameTransport(tcpA.GetStream());
        using var tcpB = new TcpClient();
        await tcpB.ConnectAsync(fixture.Host, fixture.AgwPortB, ct);
        var sideB = new AgwFrameTransport(tcpB.GetStream());

        await sideA.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallA, "", []), ct);
        await sideB.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallB, "", []), ct);
        (await sideA.ReadFrameAsync(ct)).Kind.Should().Be('X');
        (await sideB.ReadFrameAsync(ct)).Kind.Should().Be('X');

        // A dials B's APPL over the AXIP carrier port.
        await sideA.WriteFrameAsync(
            new AgwFrame(SessionPort, 'C', 0, fixture.ApplCallA, fixture.ApplCallB, []), ct);
        await ReadUntil(sideA, 'C', ct);
        var inbound = await ReadUntil(sideB, 'C', ct);
        inbound.Port.Should().Be(SessionPort, "the inbound 'C' is stamped with the session's port");
        inbound.CallFrom.Should().Be(fixture.ApplCallA);
        inbound.CallTo.Should().Be(fixture.ApplCallB);

        // B's application does what dapps does every 15s: a 'G' keepalive on port 0.
        await sideB.WriteFrameAsync(new AgwFrame(0, 'G', 0, "", "", []), ct);
        await ReadUntil(sideB, 'G', ct);

        // A hangs up.
        await sideA.WriteFrameAsync(
            new AgwFrame(SessionPort, 'd', 0, fixture.ApplCallA, fixture.ApplCallB, []), ct);
        var disconnect = await ReadUntil(sideB, 'd', ct);
        disconnect.CallFrom.Should().Be(fixture.ApplCallA);
        disconnect.CallTo.Should().Be(fixture.ApplCallB);
        disconnect.Port.Should().Be(0,
            "BPQ copies the port of the last frame it received from the application (our 'G'), not the " +
            "session's port {0}; this is why AgwInboundService resolves a 'd' by callsign pair when the port matches nothing",
            SessionPort);

        // Let both nodes finish tearing the link down before the next
        // test in this collection reuses the same callsign pair.
        await Task.Delay(2000, ct);
    }

    [Fact]
    public async Task DappsInbound_ClosesTheSession_AndPromptsTheRedial_WhenAKeepalivePrecededTheHangup()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = ctSource.Token;

        var dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-quirk-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
        }

        // dapps on B, with the keepalive under test control.
        var clock = new ObservableTimeProvider();
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = fixture.ApplCallB,
            NodeHost = fixture.Host,
            AgwPort = fixture.AgwPortB,
        });
        var logs = new CapturingLoggerFactory();
        var service = new AgwInboundService(
            options,
            new Database(NullLogger<Database>.Instance, options),
            new NullBackhaulInbox(),
            logs,
            new Logger<AgwInboundService>(logs),
            timeProvider: clock);
        await service.StartAsync(ct);
        try
        {
            await logs.WaitForAsync("'X' ack", ct);

            using var tcpA = new TcpClient();
            await tcpA.ConnectAsync(fixture.Host, fixture.AgwPortA, ct);
            var sideA = new AgwFrameTransport(tcpA.GetStream());
            await sideA.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallA, "", []), ct);
            (await sideA.ReadFrameAsync(ct)).Kind.Should().Be('X');

            await sideA.WriteFrameAsync(
                new AgwFrame(SessionPort, 'C', 0, fixture.ApplCallA, fixture.ApplCallB, []), ct);
            await ReadUntil(sideA, 'C', ct);
            var prompt = await ReadUntil(sideA, 'D', ct);
            Encoding.UTF8.GetString(prompt.Payload).Should().Be("DAPPSv1>\n");

            // dapps's keepalive goes out; BPQ-B's last frame from dapps is now a 'G' on port 0.
            clock.Advance(AgwInboundService.KeepaliveInterval);
            await logs.WaitForAsync("keepalive 'G' sent", ct);

            // A hangs up. BPQ-B's 'd' to dapps carries port 0.
            await sideA.WriteFrameAsync(
                new AgwFrame(SessionPort, 'd', 0, fixture.ApplCallA, fixture.ApplCallB, []), ct);
            await logs.WaitForAsync("session closed", ct);
            logs.Any($"carried port 0 but the session was on port {SessionPort}").Should().BeTrue(
                "the notification really did arrive with the keepalive's port, and the pair fallback matched it");
            await ReadUntil(sideA, 'd', ct);

            // BPQ-A keeps the old session's key until its poll loop has
            // processed the disconnect; the real forwarder waits this out
            // via the outbound settle delay, and so do we.
            await Task.Delay(BearerSwitchingOutboundTransport.DefaultLinkSettleDelay + TimeSpan.FromMilliseconds(500), ct);

            await sideA.WriteFrameAsync(
                new AgwFrame(SessionPort, 'C', 0, fixture.ApplCallA, fixture.ApplCallB, []), ct);
            await ReadUntil(sideA, 'C', ct);
            var prompt2 = await ReadUntil(sideA, 'D', ct);
            Encoding.UTF8.GetString(prompt2.Payload).Should().Be("DAPPSv1>\n",
                "the previous session was closed cleanly, so the redial is a fresh session");
            logs.Warnings.Should().BeEmpty("nothing went stale, so nothing had to be retired");

            await sideA.WriteFrameAsync(
                new AgwFrame(SessionPort, 'D', 0xF0, fixture.ApplCallA, fixture.ApplCallB, "quit\n"u8.ToArray()), ct);
            var bye = await ReadUntil(sideA, 'D', ct);
            Encoding.UTF8.GetString(bye.Payload).Should().Be("bye\n", "and it is a working session");
            await ReadUntil(sideA, 'd', ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            // Same settle as AgwInboundDeliveryTests: let BPQ release our
            // 'X' registration before the next test registers the same call.
            await Task.Delay(2000);
            DbInfo.OverridePath = null;
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    private static async Task<AgwFrame> ReadUntil(AgwFrameTransport transport, char kind, CancellationToken ct)
    {
        while (true)
        {
            var frame = await transport.ReadFrameAsync(ct);
            if (frame.Kind == kind) return frame;
        }
    }
}
