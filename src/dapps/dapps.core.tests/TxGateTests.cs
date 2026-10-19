using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.client.Transport.Agw;
using dapps.client.Transport.Rhp;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Coverage of the four gate hook points wired in PR 1:
///   - <see cref="AgwFrameTransport.WriteFrameAsync"/> (Kind whitelist)
///   - <see cref="UdpDatagramBackhaul.SendAsync"/> (failed result)
///   - <see cref="Rhpv2OutboundTransport.ConnectAsync"/> (throws)
///   - <see cref="AgwFrameTransport"/> default (always-open)
///
/// The Rhpv2InboundService writeOutgoing path is exercised indirectly:
/// the lambda we install there is a small inline check whose shape
/// matches the outbound test.
/// </summary>
public class TxGateTests
{
    private sealed class StubGate : IDappsTxGate
    {
        public bool TxAllowed { get; set; } = true;
        public string? BlockReason { get; set; }
    }

    [Theory]
    [InlineData('C')] // connect
    [InlineData('v')] // connect via
    [InlineData('c')] // connect non-std PID
    [InlineData('D')] // data
    [InlineData('M')] // UNPROTO
    [InlineData('V')] // UNPROTO via
    [InlineData('K')] // raw
    public async Task AgwFrameTransport_BlocksRfEmittingKinds_WhenGateClosed(char kind)
    {
        var ms = new MemoryStream();
        var gate = new StubGate { TxAllowed = false, BlockReason = "test" };
        var transport = new AgwFrameTransport(ms, gate);

        var frame = new AgwFrame(0, kind, 0xF0, "M0LTE-1", "M0LTE-2", []);

        var act = async () => await transport.WriteFrameAsync(frame, CancellationToken.None);
        await act.Should().ThrowAsync<TxStoppedException>();

        ms.Length.Should().Be(0, "no bytes should reach the wire when blocked");
    }

    [Theory]
    [InlineData('X')] // register callsign
    [InlineData('x')] // unregister
    [InlineData('P')] // login
    [InlineData('G')] // ask port info
    [InlineData('g')] // ask port caps
    [InlineData('R')] // version
    [InlineData('m')] // monitor on/off
    [InlineData('k')] // raw mode toggle
    [InlineData('y')] // frames outstanding
    [InlineData('d')] // disconnect (allowed: blocking it leaks sessions)
    public async Task AgwFrameTransport_AllowsAdminAndDisconnectKinds_EvenWhenGateClosed(char kind)
    {
        var ms = new MemoryStream();
        var gate = new StubGate { TxAllowed = false, BlockReason = "test" };
        var transport = new AgwFrameTransport(ms, gate);

        var frame = new AgwFrame(0, kind, 0, "M0LTE-1", "", []);

        await transport.WriteFrameAsync(frame, CancellationToken.None);
        ms.Length.Should().Be(AgwFrame.HeaderLength,
            "non-RF kinds must reach BPQ even with TX gated, otherwise the host link breaks");
    }

    [Fact]
    public async Task AgwFrameTransport_DefaultGate_IsAlwaysOpen()
    {
        var ms = new MemoryStream();
        var transport = new AgwFrameTransport(ms);
        var frame = new AgwFrame(0, 'D', 0xF0, "M0LTE-1", "M0LTE-2", [1, 2, 3]);

        await transport.WriteFrameAsync(frame, CancellationToken.None);
        ms.Length.Should().Be(AgwFrame.HeaderLength + 3);
    }

    [Fact]
    public async Task AgwFrameTransport_GateOpen_StillWrites()
    {
        var ms = new MemoryStream();
        var gate = new StubGate { TxAllowed = true };
        var transport = new AgwFrameTransport(ms, gate);
        var frame = new AgwFrame(0, 'D', 0xF0, "M0LTE-1", "M0LTE-2", [1, 2, 3]);

        await transport.WriteFrameAsync(frame, CancellationToken.None);
        ms.Length.Should().Be(AgwFrame.HeaderLength + 3);
    }

    [Fact]
    public async Task UdpDatagramBackhaul_GateClosed_ReturnsFailNoWire()
    {
        // Bind a probe socket and confirm no datagram arrives within
        // a short wait when the gate is closed - the contract is
        // "fail-closed silently", not "throw".
        var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probePort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        var gate = new StubGate { TxAllowed = false, BlockReason = "operator pressed STOP" };
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance, txGate: gate);

        var route = new BackhaulRoute("M0LTE-2", UdpEndpoint: $"127.0.0.1:{probePort}");
        var msg = new BackhaulMessage(
            Id: "txstop1",
            Destination: "myapp@M0LTE-2",
            Salt: 42L,
            Ttl: 600,
            Payload: "x"u8.ToArray());

        var result = await sender.SendAsync(msg, route, "M0LTE-1", CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("tx-stopped");
        result.Error.Should().Contain("operator pressed STOP");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var receive = async () => await probe.ReceiveAsync(cts.Token);
        await receive.Should().ThrowAsync<OperationCanceledException>(
            "no UDP datagram should reach the wire when the gate is closed");
    }

    [Fact]
    public async Task UdpDatagramBackhaul_GateOpen_SendsDatagram()
    {
        var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probePort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        var gate = new StubGate { TxAllowed = true };
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance, txGate: gate);

        var msg = new BackhaulMessage(
            Id: "txopen2",
            Destination: "myapp@M0LTE-2",
            Salt: 42L,
            Ttl: 600,
            Payload: "ok"u8.ToArray());

        var result = await sender.SendAsync(
            msg,
            new BackhaulRoute("M0LTE-2", UdpEndpoint: $"127.0.0.1:{probePort}"),
            "M0LTE-1",
            CancellationToken.None);

        result.Accepted.Should().BeTrue("send should succeed when gate is open; error was: " + result.Error);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = await probe.ReceiveAsync(cts.Token);
        received.Buffer.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Rhpv2OutboundTransport_GateClosed_ThrowsBeforeOpen()
    {
        // Listener that accepts the TCP connect but never speaks RHP -
        // proves the gate trips before any RHP active-open is attempted.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();

        var gate = new StubGate { TxAllowed = false, BlockReason = "kill-switch active" };
        var transport = new Rhpv2OutboundTransport(
            "127.0.0.1", port,
            NullLogger<Rhpv2OutboundTransport>.Instance,
            txGate: gate);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var act = async () => await transport.ConnectAsync("M0LTE-1", "M0LTE-2", 1, cts.Token);
            (await act.Should().ThrowAsync<TxStoppedException>())
                .WithMessage("*kill-switch active*");
        }
        finally
        {
            listener.Stop();
            try { (await acceptTask).Dispose(); } catch { }
        }
    }
}
