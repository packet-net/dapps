using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace dapps.core.tests;

/// <summary>
/// Regression tests for the outbound link-settle delay: <see cref="BearerSwitchingOutboundTransport"/>
/// paces successive connects to the same (bearer, local, remote, port)
/// so a burst of queued messages to one destination doesn't redial
/// faster than the remote node can release the previous link.
///
/// Field symptom this guards against: <c>OutboundMessageManager</c>
/// sends several queued messages to the same destination back-to-back
/// with no gap of its own. Our own disconnect frame only tells *our*
/// node to drop the link; that has to propagate to the remote node
/// before it will accept a fresh connect for the same callsign pair.
/// A too-fast redial gets rejected on the remote node's side (that
/// node's own log shows e.g. "already connected on socket N" - never
/// visible to us), so the forward just times out for no apparent
/// reason here.
///
/// A fake AGW host plays the local node (BPQ/XRouter) for the
/// duration of one connect: 'X' register reply, echo the 'C' connect
/// request back to confirm, then read the 'd' disconnect frame on
/// dispose. No real BPQ needed.
/// </summary>
public sealed class BearerSwitchingOutboundTransportSettleDelayTests
{
    private const string Local = "G0LOCAL-1";
    private const string RemoteA = "G0REMA-1";
    private const string RemoteB = "G0REMB-1";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SecondConnectToSameDestination_WaitsOutTheSettleDelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var settleDelay = TimeSpan.FromMilliseconds(600);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var transport = MakeTransport(listener, settleDelay);

        var host1 = FakeAgwEchoOnceAsync(listener, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync(); // sends 'd' - must happen before awaiting the host's read of it
        await host1;

        var sw = Stopwatch.StartNew();
        var host2 = FakeAgwEchoOnceAsync(listener, ct);
        var conn2 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        sw.Stop();
        await conn2.DisposeAsync();
        await host2;

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(
            settleDelay - TimeSpan.FromMilliseconds(50),
            "the redial should wait out the settle delay measured from when the previous " +
            "connection's disconnect frame actually went out");
    }

    [Fact]
    public async Task ConnectToADifferentDestination_DoesNotWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var settleDelay = TimeSpan.FromSeconds(5);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var transport = MakeTransport(listener, settleDelay);

        var host1 = FakeAgwEchoOnceAsync(listener, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync();
        await host1;

        var sw = Stopwatch.StartNew();
        var host2 = FakeAgwEchoOnceAsync(listener, ct);
        var conn2 = await transport.ConnectAsync(Local, RemoteB, 0, ct);
        sw.Stop();
        await conn2.DisposeAsync();
        await host2;

        sw.Elapsed.Should().BeLessThan(
            settleDelay,
            "a different destination has no stale link to wait out - only entries for the same key gate a redial");
    }

    [Fact]
    public async Task ZeroSettleDelay_DisablesTheGate()
    {
        var ct = TestContext.Current.CancellationToken;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var transport = MakeTransport(listener, TimeSpan.Zero);

        var host1 = FakeAgwEchoOnceAsync(listener, ct);
        var conn1 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        await conn1.DisposeAsync();
        await host1;

        var sw = Stopwatch.StartNew();
        var host2 = FakeAgwEchoOnceAsync(listener, ct);
        var conn2 = await transport.ConnectAsync(Local, RemoteA, 0, ct);
        sw.Stop();
        await conn2.DisposeAsync();
        await host2;

        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1), "a zero delay must not insert any wait at all");
    }

    private static BearerSwitchingOutboundTransport MakeTransport(TcpListener listener, TimeSpan settleDelay)
    {
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            NodeBearer = "agw",
            NodeHost = "127.0.0.1",
            AgwPort = ((IPEndPoint)listener.LocalEndpoint).Port,
        });
        return new BearerSwitchingOutboundTransport(
            options, NullLoggerFactory.Instance, AlwaysOpenTxGate.Instance, TimeProvider.System, settleDelay);
    }

    /// <summary>Accepts one TCP connection and plays just enough of the
    /// AGW host role for <see cref="AgwOutboundTransport"/> to succeed:
    /// ack the 'X' registration, echo the 'C' connect request back as
    /// its own confirmation, then read (and discard) the 'd' disconnect
    /// frame sent on dispose.</summary>
    private static async Task FakeAgwEchoOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using var tcp = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(FrameTimeout, ct);
        var framing = new AgwFrameTransport(tcp.GetStream());

        await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct); // 'X' register
        await framing.WriteFrameAsync(new AgwFrame(0, 'X', 0, "", "", [1]), ct);

        var connect = await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct); // 'C' request
        await framing.WriteFrameAsync(connect, ct); // echo back as confirmation

        await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct); // 'd' on dispose
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
