using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;

namespace dapps.core.tests;

/// <summary>
/// Our own calls and the peers' calls on one <see cref="AgwLink"/> (#272):
/// telling a confirmation from an inbound connect, routing each frame to
/// the right one of several sessions with one peer, a crossed call, the
/// give-up when the peer's call kills ours without a word, what the link
/// ending does to both kinds, and BPQ's two answers to each of our 'd's.
/// A <see cref="FakeAgwSocket"/> plays the node, with BPQ's wording.
/// </summary>
public sealed class AgwLinkDemuxTests : IAsyncDisposable
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";
    private const string Other = "M0XYZ-1";
    private const string Third = "M0QQQ-2";
    private const byte Port = 1;
    private const byte OtherPort = 2;
    private static readonly TimeSpan Deadline = AgwOutboundTransport.ConnectConfirmDeadline;

    private readonly CapturingLoggerFactory logs = new();
    private readonly Channel<AgwLinkSession> inbound = Channel.CreateUnbounded<AgwLinkSession>();
    private readonly FakeAgwHost host = new();
    private AgwLink? link;
    private FakeAgwSocket? node;

    [Fact]
    public async Task AnInboundConnectAndOurConfirmation_AreToldApartByTheirText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        var dialling = link.DialAsync(Port, Local, Remote, Deadline, ct);
        var request = await node.ReadUntilAsync('C', ct);
        (request.Port, request.CallFrom, request.CallTo).Should().Be((Port, Local, Remote));

        // The peer's own call, under the very key we are dialling: on one
        // socket the header is the same as our confirmation's.
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var theirs = await NextInboundAsync(ct);
        theirs.Remote.Should().Be(Remote);
        theirs.Outbound.Should().BeFalse();
        dialling.IsCompleted.Should().BeFalse("\"CONNECTED To\" is the peer's call, not the answer to ours");

        await node.WriteAsync(ct, Confirm(Remote, Port));
        await using var ours = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        inbound.Reader.TryRead(out _).Should().BeFalse("\"CONNECTED With\" is the answer to our call, not another inbound one");

        await node.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "hello\n"));
        (await ReadTextAsync(ours.Stream, ct)).Should().Be("hello\n", "the link is our call's now");
    }

    [Fact]
    public async Task TwoSessionsWithOnePeer_OnDifferentPorts_EachGetTheirOwnFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        await using var ours = await DialAndConfirmAsync(link, node, Remote, Port, ct);
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, OtherPort));
        var theirs = await NextInboundAsync(ct);

        await node.WriteAsync(ct,
            FakeAgwSocket.Data(Remote, Local, OtherPort, "to theirs\n"),
            FakeAgwSocket.Data(Remote, Local, Port, "to ours\n"));
        (await ReadTextAsync(ours.Stream, ct)).Should().Be("to ours\n");
        (await ReadTextAsync(theirs.Stream, ct)).Should().Be("to theirs\n");

        // The pair alone names both: leave both alone rather than guess.
        await node.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await logs.WaitForAsync("'d' for unknown session", ct);
        ours.RemoteDisconnected.Should().BeFalse();
        theirs.Stream.RemoteClosed.IsCancellationRequested.Should().BeFalse();

        await node.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, OtherPort));
        await logs.WaitForAsync("session closed", ct);
        theirs.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue();
        ours.RemoteDisconnected.Should().BeFalse("the 'd' named the other port");

        await ours.Stream.WriteAsync("reply\n"u8.ToArray(), ct);
        var reply = await node.ReadUntilAsync('D', ct);
        (reply.Port, reply.CallFrom, reply.CallTo).Should().Be((Port, Local, Remote));
    }

    [Fact]
    public async Task ThePeersCall_ArrivingWhileOursIsOnItsWay_IsReplacedByOursOnceItConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        var dialling = link.DialAsync(Port, Local, Remote, Deadline, ct, logs.CreateLogger("dial"));
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var theirs = await NextInboundAsync(ct);
        await logs.WaitForAsync("call reached us while ours to it was on its way", ct);

        // BPQ sends our SABM down the link the peer's call made, and
        // confirms our call on it.
        await node.WriteAsync(ct, Confirm(Remote, Port), FakeAgwSocket.Data(Remote, Local, Port, "for ours\n"));
        await using var ours = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        (await ReadTextAsync(ours.Stream, ct)).Should().Be("for ours\n");

        // The replaced session sends nothing on the key: it would land in ours.
        await theirs.Stream.WriteAsync("stale\n"u8.ToArray(), ct);
        await theirs.Stream.DisposeAsync();
        (await node.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'D' || f.Kind == 'd');
    }

    [Fact]
    public async Task WhenThePeersSabmShows_AndNeitherCallConnects_TheDialGivesUpAfterFifteenSeconds()
    {
        // BPQ refuses the peer's call as a duplicate of ours when our 'C'
        // lands just before its SABM, and both calls die with no word to
        // our socket. Without the give-up the dial sat out its 3 minutes.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var (link, node) = await OpenAsync(ct, clock);

        var dialling = link.DialAsync(Port, Local, Remote, Deadline, ct, logs.CreateLogger("dial"));
        await node.ReadUntilAsync('C', ct);
        await clock.WaitForTimerAsync(Deadline, ct);
        await node.WriteAsync(ct, PeersSabm(Port));
        await logs.WaitForAsync("the calls crossed", ct);
        await clock.WaitForTimerAsync(AgwLink.CrossedDialGiveUp, ct);
        clock.Advance(AgwLink.CrossedDialGiveUp - TimeSpan.FromSeconds(1));
        dialling.IsCompleted.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        (await FluentActions.Awaiting(() => dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("crossed ours");
        var bye = await node.ReadUntilAsync('d', ct);
        (bye.Port, bye.CallFrom, bye.CallTo).Should().Be((Port, Local, Remote), "the node is told to drop our dead call");
    }

    [Fact]
    public async Task WhenThePeersCallReachesUs_TheDialDoesNotGiveUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var (link, node) = await OpenAsync(ct, clock);

        var dialling = link.DialAsync(Port, Local, Remote, Deadline, ct, logs.CreateLogger("dial"));
        await node.ReadUntilAsync('C', ct);
        await clock.WaitForTimerAsync(Deadline, ct);
        // The monitor first this time, so the give-up clock is running
        // when the peer's call arrives.
        await node.WriteAsync(ct, PeersSabm(Port));
        await logs.WaitForAsync("the calls crossed", ct);
        await clock.WaitForTimerAsync(AgwLink.CrossedDialGiveUp, ct);
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await NextInboundAsync(ct);
        clock.Advance(AgwLink.CrossedDialGiveUp + TimeSpan.FromSeconds(1));
        logs.Any("giving up our call").Should().BeFalse("the peer's call reached us, so the node did not refuse it");

        await node.WriteAsync(ct, Confirm(Remote, Port));
        await using var ours = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        ours.CrossedCall.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task NothingIsSentOnACall_UntilTheNodeConfirmsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        var dialling = link.DialAsync(Port, Local, Remote, Deadline, ct);
        await node.ReadUntilAsync('C', ct);
        // Anything the node says that is not the confirmation leaves the
        // call unconfirmed: what we sent now would reach our own node's
        // command prompt.
        await node.WriteAsync(ct,
            FakeAgwSocket.Data(Remote, Local, Port, "early\n"),
            new AgwFrame(Port, 'C', 0, Remote, Local, []));
        await NextInboundAsync(ct);
        (await node.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'D');
        dialling.IsCompleted.Should().BeFalse("only \"CONNECTED With\" confirms our call");
    }

    [Fact]
    public async Task WhenTheLinkDrops_AWaitingDialFails_AndEverySessionEndsWithNoDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        await using var ours = await DialAndConfirmAsync(link, node, Remote, Port, ct);
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Other, Local, Port));
        var theirs = await NextInboundAsync(ct);
        var waiting = link.DialAsync(OtherPort, Local, Remote, Deadline, ct);
        await node.ReadUntilAsync('C', ct);

        node.Close();
        await link.Closed.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        await FluentActions.Awaiting(() => waiting.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().ThrowAsync<IOException>();
        ours.RemoteDisconnected.Should().BeTrue();
        theirs.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue();
        (await ours.Stream.ReadAsync(new byte[16], ct)).Should().Be(0);
        await FluentActions.Awaiting(() => ours.Stream.WriteAsync("late\n"u8.ToArray(), ct).AsTask())
            .Should().ThrowAsync<IOException>("a closed link takes no writes");
        await FluentActions.Awaiting(() => link.DialAsync(Port, Local, Other, Deadline, ct))
            .Should().ThrowAsync<IOException>("a closed link makes no calls");
    }

    [Fact]
    public async Task BpqsTwoAnswersToOurDisconnect_DoNotEndTheNextCall()
    {
        // BPQ answers each 'd' with "RETRYOUT With" at once and "From
        // Station" when the link has gone, which on air can be after our
        // redial has gone out under the same key.
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        var first = await DialAndConfirmAsync(link, node, Remote, Port, ct);
        await first.DisposeAsync();
        (await node.ReadUntilAsync('d', ct)).CallTo.Should().Be(Remote);
        await node.WriteAsync(ct, Echo(Remote, Port));

        var redial = link.DialAsync(Port, Local, Remote, Deadline, ct);
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await logs.WaitForCountAsync("the node's answer to our own 'd'", 2, ct);
        redial.IsCompleted.Should().BeFalse("that 'd' was about the call we closed");

        await node.WriteAsync(ct, Confirm(Remote, Port));
        await using var second = await redial.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        await node.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await logs.WaitForAsync("session closed", ct);
        second.RemoteDisconnected.Should().BeTrue("a 'd' once the echoes are in is the real thing");
    }

    [Fact]
    public async Task AConfirmationNoDialIsWaitingFor_IsDisconnected_UnlessASessionHoldsThePair()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, node) = await OpenAsync(ct);

        await node.WriteAsync(ct, Confirm(Other, Port));
        var bye = await node.ReadUntilAsync('d', ct);
        (bye.CallFrom, bye.CallTo).Should().Be((Local, Other));

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await NextInboundAsync(ct);
        await node.WriteAsync(ct, Confirm(Remote, OtherPort));
        await logs.WaitForAsync("so no 'd'", ct);
        (await node.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd', "BPQ would apply it to the session that holds the pair");
    }

    [Fact]
    public async Task OneSocket_TakesThePeersCalls_AndNeverSplicesThemIntoOurs()
    {
        // #272: every dial used to open and register a socket of its own,
        // and BPQ hands an inbound call to the first socket registered for
        // the callsign, so a dial's socket could take a call meant for the
        // listener and read its data as its own. Now there is one socket,
        // registered once, and each call's frames reach only that call.
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        var transport = new BearerSwitchingOutboundTransport(
            h.Options, h.Logs, AlwaysOpenTxGate.Instance, TimeProvider.System, TimeSpan.Zero, peers,
            linkSettleSpread: TimeSpan.Zero, agwLinks: h.Service);

        var dialling = transport.ConnectAsync(Local, Remote, Port, ct);
        await bpq.ReadUntilAsync('C', ct);
        await bpq.WriteAsync(ct, Confirm(Remote, Port));
        await using var ours = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        // Another station calls while ours is up: the listener answers it.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Other, Local, Port));
        var prompt = await bpq.ReadUntilAsync('D', ct);
        (prompt.CallFrom, prompt.CallTo).Should().Be((Local, Other));
        Encoding.UTF8.GetString(prompt.Payload).Should().StartWith("DAPPSv1>");

        await bpq.WriteAsync(ct,
            FakeAgwSocket.Data(Other, Local, Port, "quit\n"),
            FakeAgwSocket.Data(Remote, Local, Port, "for our call\n"));
        (await ReadTextAsync(ours.Stream, ct)).Should().Be("for our call\n", "nothing of the other call's reaches ours");
        var bye = await bpq.ReadUntilAsync('D', ct);
        (bye.CallTo, Encoding.UTF8.GetString(bye.Payload)).Should().Be((Other, "bye\n"));

        // A second dial goes out on the same socket, with no registration of its own.
        await using (var second = await DialOverAsync(transport, bpq, Third, ct)) { }
        h.Host.Pending().Should().BeFalse("no dial opens a socket of its own");
        bpq.Received.Count(f => f.Kind == 'X').Should().Be(1, "the callsign is registered once, by the link");
    }

    [Fact]
    public async Task ADial_WhileTheLinkIsDown_FailsAtOnce_AndWhileItOpens_WaitsForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new ScriptedLinkSource { State = AgwLinkState.Backoff };
        var transport = new AgwOutboundTransport(source, logs);

        (await FluentActions.Awaiting(() => transport.ConnectAsync(Local, Remote, Port, ct))
            .Should().ThrowAsync<AgwLinkDownException>()).Which.Should().BeAssignableTo<IOException>();
        source.Waits.Should().Be(0, "a link backing off is not waited for");

        source.State = AgwLinkState.Idle;
        await FluentActions.Awaiting(() => transport.ConnectAsync(Local, Remote, Port, ct)).Should().ThrowAsync<AgwLinkDownException>();
        source.Waits.Should().Be(0);

        source.State = AgwLinkState.Connecting;
        await FluentActions.Awaiting(() => transport.ConnectAsync(Local, Remote, Port, ct)).Should().ThrowAsync<AgwLinkDownException>();
        source.Waited.Should().Be(AgwOutboundTransport.LinkOpenWait);

        var (link, node) = await OpenAsync(ct);
        source.Link = link;
        var dialling = transport.ConnectAsync(Local, Remote, Port, ct);
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, Confirm(Remote, Port));
        await using var connected = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        await FluentActions.Awaiting(() => transport.ConnectAsync("G5ALF-4", Remote, Port, ct))
            .Should().ThrowAsync<AgwLinkDownException>("BPQ would send the call from the link's own callsign");
    }

    public async ValueTask DisposeAsync()
    {
        if (link is not null) await link.DisposeAsync();
        node?.Dispose();
        host.Dispose();
    }

    private async Task<(AgwLink, FakeAgwSocket)> OpenAsync(CancellationToken ct, TimeProvider? clock = null)
    {
        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, s => inbound.Writer.TryWrite(s), logs.CreateLogger("link"),
            clock: clock, ct: ct);
        node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await node.ReadUntilAsync('m', ct);
        link = await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        return (link, node);
    }

    private static async Task<AgwDial> DialAndConfirmAsync(AgwLink link, FakeAgwSocket node, string remote, byte port, CancellationToken ct)
    {
        var dialling = link.DialAsync(port, Local, remote, Deadline, ct);
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, Confirm(remote, port));
        return await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    private static async Task<IDappsConnection> DialOverAsync(IDappsOutboundTransport transport, FakeAgwSocket node, string remote, CancellationToken ct)
    {
        var dialling = transport.ConnectAsync(Local, remote, Port, ct);
        var request = await node.ReadUntilAsync('C', ct);
        request.CallTo.Should().Be(remote);
        await node.WriteAsync(ct, Confirm(remote, Port));
        return await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    private async Task<AgwLinkSession> NextInboundAsync(CancellationToken ct) =>
        await inbound.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);

    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    /// <summary>BPQ's confirmation of our call: CallFrom the peer, CallTo us.</summary>
    private static AgwFrame Confirm(string remote, byte port) =>
        new(port, 'C', 0, remote, Local, Encoding.ASCII.GetBytes($"*** CONNECTED With Station {remote}\r\0"));

    /// <summary>BPQ's immediate answer to a 'd' of ours, on the port we used.</summary>
    private static AgwFrame Echo(string remote, byte port) =>
        new(port, 'd', 0, remote, Local, Encoding.ASCII.GetBytes($"*** DISCONNECTED RETRYOUT With {remote}\r\0"));

    /// <summary>The peer's SABM to us as BPQ's monitor shows it (BPQ
    /// numbers its ports from 1). The header's CallTo is stale.</summary>
    private static AgwFrame PeersSabm(byte port) =>
        new(port, 'S', 0, Remote, "STALE", Encoding.ASCII.GetBytes($" {port + 1}:Fm {Remote} To {Local} <C C P>[12:00:00]\r"));

    private sealed class ScriptedLinkSource : IAgwLinkSource
    {
        public AgwLinkState State { get; set; }
        public AgwLink? Link { get; set; }
        public int Waits { get; private set; }
        public TimeSpan Waited { get; private set; }

        public AgwLink? TryGetLink() => Link;

        public Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct)
        {
            Waits++;
            Waited = wait;
            return Task.FromResult(Link);
        }
    }
}
