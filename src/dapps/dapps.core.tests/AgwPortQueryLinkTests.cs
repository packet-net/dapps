using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.core.Services;
using Microsoft.Extensions.Logging;

namespace dapps.core.tests;

/// <summary>
/// The dashboard's port list (<see cref="AgwPortQuery"/>) on the daemon's
/// one AGW connection (#272): answered from the keepalive's last 'G' when
/// that is fresh, else from a 'G' of its own, within 3 s, with nothing
/// else on the link disturbed; and, with no link up, on a short-lived
/// socket of its own that never registers a callsign.
/// </summary>
public sealed class AgwPortQueryLinkTests : IAsyncDisposable
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";
    private const string Other = "M0XYZ-1";
    private const byte Port = 1;

    private static readonly AgwFrame TwoPorts = PortsReply("2;Port1 VHF;Port2 AXIP;\0");

    private readonly CapturingLoggerFactory logs = new();
    private readonly Channel<AgwLinkSession> inbound = Channel.CreateUnbounded<AgwLinkSession>();
    private readonly FakeAgwHost host = new();
    private AgwLink? link;
    private FakeAgwSocket? node;

    [Fact]
    public async Task WhileTheLinkIsUp_TheQueryGoesOnIt_AndOpensNoSocketOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        var query = new AgwPortQuery(h.Service, new Logger<AgwPortQuery>(h.Logs));

        var asking = query.QueryAsync("127.0.0.1", h.Host.Port, ct);
        (await bpq.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await bpq.WriteAsync(ct, TwoPorts);
        var ports = await asking.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        ports.Select(p => p.Description).Should().Equal("Port1 VHF", "Port2 AXIP");
        h.Host.Pending().Should().BeFalse("the query used the daemon's connection, not one of its own");
        bpq.Received.Count(f => f.Kind == 'X').Should().Be(1, "the link's own registration, and no other");
    }

    [Fact]
    public async Task AFreshPortList_IsTheAnswer_AndAStaleOneIsAskedForAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var (link, node) = await OpenAsync(ct, clock);

        var first = link.QueryPortsAsync(ct);
        (await node.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await node.WriteAsync(ct, TwoPorts);
        Text((await first.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Payload).Should().StartWith("2;Port1 VHF");

        // Moments later: the node is not asked again.
        clock.Advance(AgwLink.PortListFreshFor - TimeSpan.FromSeconds(16));
        var second = await link.QueryPortsAsync(ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Text(second.Payload).Should().StartWith("2;Port1 VHF");
        (await node.DrainAsync(ct)).Should().BeEmpty("a list that fresh is the answer");

        // Older than PortListFreshFor, and the keepalive's 'G's unanswered:
        // the query asks for itself.
        clock.Advance(TimeSpan.FromSeconds(16));
        (await node.DrainAsync(ct)).Should().OnlyContain(f => f.Kind == 'G', "the keepalive's, left unanswered");
        var third = link.QueryPortsAsync(ct);
        (await node.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await node.WriteAsync(ct, PortsReply("1;Port1 HF;\0"));
        Text((await third.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Payload).Should().StartWith("1;Port1 HF");
    }

    [Fact]
    public async Task TheKeepalivesAnswer_IsTheAnswer_WithoutASecondG()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var (link, node) = await OpenAsync(ct, clock);

        clock.Advance(AgwLink.KeepaliveInterval);
        (await node.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await node.WriteAsync(ct, TwoPorts);
        await logs.WaitForAsync("port list ('G') received", ct);

        var ports = await link.QueryPortsAsync(ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Text(ports.Payload).Should().StartWith("2;Port1 VHF");
        (await node.DrainAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANodeThatDoesNotAnswer_TimesOutAfterThreeSeconds_AndTheLinkStaysUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var bpq = await h.StartAsync(ct);
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        var query = new AgwPortQuery(h.Service, new Logger<AgwPortQuery>(h.Logs));

        var asking = query.QueryAsync("127.0.0.1", h.Host.Port, ct);
        (await bpq.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await clock.WaitForTimerAsync(AgwLink.PortQueryTimeout, ct);
        asking.IsCompleted.Should().BeFalse();

        clock.Advance(AgwLink.PortQueryTimeout);
        var timedOut = await FluentActions.Awaiting(() => asking.WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<TimeoutException>();
        timedOut.Which.Message.Should().Be($"AGW port query timed out after 3s on 127.0.0.1:{h.Host.Port}");
        link!.Closed.IsCompleted.Should().BeFalse("a port list that never came is no reason to drop every session");

        // The next query asks again.
        var again = query.QueryAsync("127.0.0.1", h.Host.Port, ct);
        (await bpq.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await bpq.WriteAsync(ct, TwoPorts);
        (await again.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task WhileSessionsAndDialsAreLive_TheQuerySendsOnlyItsG_AndDisturbsNeither()
    {
        var ct = TestContext.Current.CancellationToken;
        var (link, node) = await OpenAsync(ct);

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var theirs = await inbound.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        var dialling = link.DialAsync(Port, Local, Other, AgwOutboundTransport.ConnectConfirmDeadline, ct);
        await node.ReadUntilAsync('C', ct);
        var sentBefore = node.Received.Count;

        var asking = link.QueryPortsAsync(ct);
        (await node.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await node.WriteAsync(ct,
            FakeAgwSocket.Data(Remote, Local, Port, "before\n"),
            TwoPorts,
            new AgwFrame(Port, 'C', 0, Other, Local, Encoding.ASCII.GetBytes($"*** CONNECTED With Station {Other}\r\0")),
            FakeAgwSocket.Data(Remote, Local, Port, "after\n"));

        Text((await asking.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Payload).Should().StartWith("2;Port1 VHF");
        await using var ours = await dialling.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        (await ReadAllAsync(theirs.Stream, "before\nafter\n".Length, ct)).Should().Be("before\nafter\n");
        theirs.Stream.RemoteClosed.IsCancellationRequested.Should().BeFalse();
        ours.RemoteDisconnected.Should().BeFalse();

        await node.DrainAsync(ct);
        node.Received.Skip(sentBefore).Select(f => f.Kind).Should().Equal(['G'],
            "the query sends its 'G' and nothing else: no 'X', no 'm', no 'd'");
    }

    [Fact]
    public async Task WithNoLinkUp_TheQueryOpensASocketOfItsOwn_ThatSendsOnlyTheG()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = new AgwPortQuery(new NoLinkSource(), new Logger<AgwPortQuery>(logs));

        var asking = query.QueryAsync("127.0.0.1", host.Port, ct);
        using var own = await host.AcceptAsync(ct);
        (await own.ReadFrameAsync(ct)).Kind.Should().Be('G', "never an 'X': that would take calls meant for the daemon");
        await own.WriteAsync(ct, TwoPorts);

        (await asking.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Select(p => p.Index).Should().Equal(0, 1);
        await FluentActions.Awaiting(() => own.ReadFrameAsync(ct))
            .Should().ThrowAsync<Exception>().Where(e => e is EndOfStreamException || e is IOException,
                "the socket is closed once the answer is in");
        own.Received.Select(f => f.Kind).Should().Equal(['G']);
    }

    [Fact]
    public async Task WithTheLinkUpToAnotherNode_TheQueryAsksTheNodeItWasGiven()
    {
        // A settings save to another node, before the link has moved.
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        var query = new AgwPortQuery(h.Service, new Logger<AgwPortQuery>(h.Logs));

        var asking = query.QueryAsync("127.0.0.1", host.Port, ct);
        using var own = await host.AcceptAsync(ct);
        (await own.ReadFrameAsync(ct)).Kind.Should().Be('G');
        await own.WriteAsync(ct, TwoPorts);

        (await asking.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().HaveCount(2);
        own.Received.Select(f => f.Kind).Should().Equal(['G']);
        (await bpq.DrainAsync(ct)).Should().BeEmpty("the link goes to another node");
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

    private static AgwFrame PortsReply(string text) => new(0, 'G', 0, "", "", Encoding.ASCII.GetBytes(text));

    private static string Text(byte[] payload) => Encoding.ASCII.GetString(payload);

    private static async Task<string> ReadAllAsync(Stream stream, int length, CancellationToken ct)
    {
        var got = new StringBuilder();
        var buffer = new byte[256];
        while (got.Length < length)
        {
            var n = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
            if (n == 0) break;
            got.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        return got.ToString();
    }

    /// <summary>The daemon's link is not up: the setup wizard before
    /// set-up, or the node down.</summary>
    private sealed class NoLinkSource : IAgwLinkSource
    {
        public AgwLinkState State => AgwLinkState.Backoff;
        public AgwLink? TryGetLink() => null;
        public Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(null);
        public Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(null);
        public IDisposable SubscribeUi(Action<AgwFrame> onUi) => throw new NotSupportedException();
    }
}
