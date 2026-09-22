using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// The outbound link-settle delay, wired through
/// <see cref="BearerSwitchingOutboundTransport"/> and driven by a fake
/// clock. <see cref="LinkSettleGateTests"/> covers the arithmetic; these
/// prove the transport actually parks a redial behind it, releases it
/// when the clock says so, and starts the clock at the right moments -
/// when our disconnect frame went out, and when a connect attempt
/// failed part-way.
///
/// Field symptom this guards against: <c>OutboundMessageManager</c>
/// sends several queued messages to the same destination back-to-back
/// with no gap of its own. Our own disconnect frame only tells *our*
/// node to drop the link; that has to propagate to the remote node,
/// whose AGW poll loop keeps the old session's key reserved until it
/// notices, and a redial that lands first is refused there ("Callsign
/// is already connected", visible only in that node's log). Here the
/// forward just times out for no apparent reason.
///
/// A <see cref="FakeAgwSocket"/> plays the local node for one connect:
/// 'X' register reply, echo the 'C' back to confirm, then read the 'd'
/// on dispose. No real BPQ needed, and no real waiting either.
/// </summary>
public sealed class BearerSwitchingOutboundTransportSettleDelayTests
{
    private const string Local = "G0LOCAL-1";
    private const string RemoteA = "G0REMA-1";
    private const string RemoteB = "G0REMB-1";
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task SecondConnectToSameDestination_IsParkedUntilTheClockSaysTheLinkHasSettled()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, clock, SettleDelay);

        var node1 = PlayNodeForOneConnectAsync(host, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync(); // sends 'd' - the settle clock starts here
        await node1;

        var connect2 = transport.ConnectAsync(Local, RemoteA, 0, ct);
        await clock.WaitForTimerAsync(SettleDelay, ct);
        connect2.IsCompleted.Should().BeFalse("the redial is parked behind the settle delay");
        host.Pending().Should().BeFalse("nothing has been dialled yet");

        var node2 = PlayNodeForOneConnectAsync(host, ct);
        clock.Advance(SettleDelay);
        var conn2 = await connect2.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await conn2.DisposeAsync();
        await node2;
    }

    [Fact]
    public async Task ConnectToADifferentDestination_DoesNotWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, clock, SettleDelay);

        var node1 = PlayNodeForOneConnectAsync(host, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync();
        await node1;

        // The clock never moves. If the transport paced this, it would
        // hang here until the frame timeout.
        var node2 = PlayNodeForOneConnectAsync(host, ct);
        var conn2 = await transport.ConnectAsync(Local, RemoteB, 0, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await conn2.DisposeAsync();
        await node2;
    }

    [Fact]
    public async Task ZeroSettleDelay_DisablesTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, clock, TimeSpan.Zero);

        var node1 = PlayNodeForOneConnectAsync(host, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync();
        await node1;

        var node2 = PlayNodeForOneConnectAsync(host, ct);
        var conn2 = await transport.ConnectAsync(Local, RemoteA, 0, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await conn2.DisposeAsync();
        await node2;
    }

    [Fact]
    public async Task AConnectThatFails_PacesTheRetryToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, clock, SettleDelay);

        var node1 = PlayNodeForOneConnectAsync(host, ct, refuse: true);
        await transport.Invoking(t => t.ConnectAsync(Local, RemoteA, 0, ct))
            .Should().ThrowAsync<IOException>("the node refused the connect");
        await node1;

        var connect2 = transport.ConnectAsync(Local, RemoteA, 0, ct);
        await clock.WaitForTimerAsync(SettleDelay, ct);
        connect2.IsCompleted.Should().BeFalse(
            "a failed attempt may have left half-up link state at either node, so the retry is paced like a clean disconnect");

        var node2 = PlayNodeForOneConnectAsync(host, ct);
        clock.Advance(SettleDelay);
        var conn2 = await connect2.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await conn2.DisposeAsync();
        await node2;
    }

    private static BearerSwitchingOutboundTransport MakeTransport(FakeAgwHost host, TimeProvider clock, TimeSpan settleDelay)
    {
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            NodeBearer = "agw",
            NodeHost = "127.0.0.1",
            AgwPort = host.Port,
        });
        return new BearerSwitchingOutboundTransport(
            options, NullLoggerFactory.Instance, AlwaysOpenTxGate.Instance, clock, settleDelay);
    }

    /// <summary>Plays the local node for one outbound connect: ack the
    /// 'X' registration, then either echo the 'C' request back as its
    /// confirmation and read the 'd' sent on dispose, or refuse it with
    /// a 'd' the way BPQ reports a failed link.</summary>
    private static async Task PlayNodeForOneConnectAsync(FakeAgwHost host, CancellationToken ct, bool refuse = false)
    {
        using var socket = await host.AcceptAsync(ct);
        await socket.ExpectRegisterAsync(ct);
        await socket.WriteAsync(ct, new AgwFrame(0, 'X', 0, "", "", [1]));

        var connect = await socket.ReadUntilAsync('C', ct);
        if (refuse)
        {
            await socket.WriteAsync(ct, new AgwFrame(
                connect.Port, 'd', 0, connect.CallTo, connect.CallFrom,
                Encoding.ASCII.GetBytes($"*** DISCONNECTED RETRYOUT With {connect.CallTo}\r\0")));
            return;
        }

        await socket.WriteAsync(ct, connect); // echo back as confirmation
        await socket.ReadUntilAsync('d', ct); // the 'd' on dispose
    }
}
