using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Our side of a crossed or moved call on AGW: spotting the peer's own
/// call in the node's monitor, retiring the older session when our dial
/// connects, closing a retired connection without a disconnect, and never
/// sending a disconnect for a link the node has already told us is gone.
/// A <see cref="FakeAgwSocket"/> plays the local node.
/// </summary>
public sealed class AgwOutboundRetirementTests : IAsyncDisposable
{
    private StandaloneAgwLinkSource? links;

    private const string Local = "N0AAA-3";
    private const string Remote = "N0BBB-3";

    // What BPQ's monitor shows: the port is BPQ's own number, one more
    // than the AGW port, and a SABM prints as "<C C P>".

    [Theory]
    [InlineData(0, " 1:Fm N0BBB-3 To N0AAA-3 <C C P>[12:00:00]\r", true)]
    [InlineData(0, " 1:Fm n0bbb-3 To n0aaa-3 <C C P>[12:00:00]\r", true)]
    [InlineData(1, " 1:Fm N0BBB-3 To N0AAA-3 <C C P>[12:00:00]\r", false)]   // heard on another port
    [InlineData(0, " 1:Fm N0BBB-3 To N0AAA-3 Via DIGI-1* <C C P>[12:00:00]\r", false)]   // through a digipeater
    [InlineData(0, " 1:Fm N0BBB-3 To N0AAA-3 <?? C P>[12:00:00]\r", false)]   // a SABME
    [InlineData(0, " 1:Fm N0BBB-3 To N0AAA-3 <UA R F>[12:00:00]\r", false)]
    [InlineData(0, " 1:Fm N0AAA-3 To N0BBB-3 <C C P>[12:00:00]\r", false)]   // our own call
    [InlineData(0, " 1:Fm G0XYZ-3 To N0AAA-3 <C C P>[12:00:00]\r", false)]   // someone else calling us
    [InlineData(0, " 1:Fm XN0BBB-3 To N0AAA-3 <C C P>[12:00:00]\r", false)]
    public void TheWatch_CountsOnlyThePeersDirectSabmToUs_OnOurPort(byte calledOn, string monitored, bool expected)
    {
        var frame = new AgwFrame(0, 'U', 0, Remote, Local, Encoding.ASCII.GetBytes(monitored));

        AgwOutboundTransport.IsPeerCallingUs(frame, calledOn, Local, Remote).Should().Be(expected);
    }

    [Fact]
    public async Task ThePeersSabm_WhileOurCallIsOnItsWay_SaysTheCallsCrossed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        node.Received.Should().Contain(f => f.Kind == 'm', "the link turns the monitor on, for the dial to watch for the peer's call");
        await node.WriteAsync(ct, Monitored(" 1:Fm N0BBB-3 To N0AAA-3 <C C P>[12:00:00]\r"));
        await node.WriteAsync(ct, Confirm());
        await using var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        conn.CrossedCall.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task OurDialConnecting_RetiresTheOlderSession_AndSaysNoPromptIsComing()
    {
        // The peer's call reached our listener while ours was on its way;
        // our call went over that live link, so the link is ours now and
        // nothing at the far end is answering it.
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, peers);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        using var inbound = peers.Acquire(Remote, "inbound", linkPort: 0);
        await node.WriteAsync(ct, Confirm());
        await using var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        inbound.Retired.IsCancellationRequested.Should().BeTrue("the link is our new call's now");
        conn.Retired.IsCancellationRequested.Should().BeFalse();
        conn.CrossedCall.IsCompleted.Should().BeTrue("no prompt is coming over a link that was already up");
    }

    [Fact]
    public async Task ARetiredConnection_ClosesWithoutADisconnect_AndReleasesThePeer()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, peers);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        await node.WriteAsync(ct, Confirm());
        var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        var inbound = peers.Acquire(Remote, "inbound", linkPort: 0);
        conn.Retired.IsCancellationRequested.Should().BeTrue();
        await conn.DisposeAsync();

        (await node.DrainAsync(ct, TimeSpan.FromMilliseconds(300))).Should().NotContain(f => f.Kind == 'd',
            "BPQ would apply a disconnect to the newer session");
        inbound.Dispose();
        peers.IsActive(Remote, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ASessionWithThePeerOnAnotherPort_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, peers);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        await node.WriteAsync(ct, Confirm());
        await using var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        using var otherPort = peers.Acquire(Remote, "inbound", linkPort: 1);

        conn.Retired.IsCancellationRequested.Should().BeFalse("that's another link");
        otherPort.Retired.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task AfterTheNodeHasDisconnectedUs_ClosingSendsNoDisconnect()
    {
        // BPQ ends our call when the peer calls us again over a live link,
        // and applies a 'd' by callsign pair: sent now, it would find
        // whatever session the node has set up for the pair since.
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        await node.WriteAsync(ct, Confirm());
        var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        await node.WriteAsync(ct, new AgwFrame(0, 'd', 0, Remote, Local,
            Encoding.ASCII.GetBytes($"*** DISCONNECTED From Station {Remote}\r\0")));
        (await conn.Stream.ReadAsync(new byte[16], ct)).Should().Be(0, "the 'd' ends the stream");
        await conn.DisposeAsync();

        (await node.DrainAsync(ct, TimeSpan.FromMilliseconds(300))).Should().NotContain(f => f.Kind == 'd');
    }

    [Fact]
    public async Task ClosingALiveLink_StillSendsTheDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        await node.WriteAsync(ct, Confirm());
        var conn = await connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await conn.DisposeAsync();

        (await node.ReadUntilAsync('d', ct)).CallTo.Should().Be(Remote);
    }

    [Fact]
    public async Task MonitoredFrames_DoNotExtendTheConfirmDeadline()
    {
        // #278: the wait for the node's answer used to restart its 3
        // minute timeout on every frame read, and the monitor is on, so
        // on a busy channel a node that never answered held the dial for
        // good.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var logs = new CapturingLoggerFactory();
        using var host = new FakeAgwHost();
        var transport = MakeTransport(host, clock: clock, logs: logs);

        var connecting = transport.ConnectAsync(Local, Remote, 0, ct);
        using var node = await AcceptCallAsync(host, ct);
        await clock.WaitForTimerAsync(AgwOutboundTransport.ConnectConfirmDeadline, ct);

        clock.Advance(TimeSpan.FromMinutes(2));
        // A frame the link demonstrably reads: a watcher of our own hears it.
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = links!.TryGetLink()!.WatchMonitor(_ => heard.TrySetResult());
        await node.WriteAsync(ct, Monitored(" 1:Fm G0XYZ-3 To N0CCC-3 <I C S0 R0>[12:00:00]\r"));
        await heard.Task.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        clock.Advance(TimeSpan.FromMinutes(1));

        var act = () => connecting.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("did not answer the connect");
    }

    private static AgwFrame Confirm() =>
        new(0, 'C', 0, Remote, Local, Encoding.ASCII.GetBytes($"*** CONNECTED With Station {Remote}\r\0"));

    private static AgwFrame Monitored(string text) => new(0, 'U', 0, Remote, Local, Encoding.ASCII.GetBytes(text));

    /// <summary>Plays the node up to our connect request: ack the register
    /// of the link the dial opens, read the 'C'.</summary>
    private static async Task<FakeAgwSocket> AcceptCallAsync(FakeAgwHost host, CancellationToken ct)
    {
        var socket = await host.AcceptAsync(ct);
        await socket.ExpectRegisterAsync(ct);
        await socket.WriteAsync(ct, new AgwFrame(0, 'X', 0, "", "", [1]));
        await socket.ReadUntilAsync('C', ct);
        return socket;
    }

    public async ValueTask DisposeAsync()
    {
        if (links is not null) await links.DisposeAsync();
    }

    private BearerSwitchingOutboundTransport MakeTransport(
        FakeAgwHost host, PeerSessionRegistry? peers = null, TimeProvider? clock = null, CapturingLoggerFactory? logs = null)
    {
        var loggers = (ILoggerFactory?)logs ?? NullLoggerFactory.Instance;
        links = new StandaloneAgwLinkSource("127.0.0.1", host.Port, Local, loggers, clock: clock);
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            NodeBearer = "agw",
            NodeHost = "127.0.0.1",
            AgwPort = host.Port,
        });
        return new BearerSwitchingOutboundTransport(
            options, loggers, AlwaysOpenTxGate.Instance, clock ?? TimeProvider.System, TimeSpan.Zero, peers,
            linkSettleSpread: TimeSpan.Zero, agwLinks: links);
    }
}
