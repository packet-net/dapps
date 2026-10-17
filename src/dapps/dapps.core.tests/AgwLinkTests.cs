using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using Microsoft.Extensions.Logging;

namespace dapps.core.tests;

/// <summary>
/// <see cref="AgwLink"/> on its own, against a scripted node: opening and
/// registering, routing each frame to the session it belongs to, and
/// what every session sees when the link ends. The inbound service on
/// top of it is covered by <see cref="AgwInboundSessionSeamTests"/>.
/// </summary>
public sealed class AgwLinkTests
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";
    private const string Other = "M0XYZ-1";
    private const byte Port = 1;

    private readonly CapturingLoggerFactory logs = new();
    private readonly Channel<AgwLinkSession> inbound = Channel.CreateUnbounded<AgwLinkSession>();

    [Fact]
    public async Task Open_RegistersTheCallsign_AndReturnsOnceTheNodeSaysYes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var registerSent = 0;

        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, Accept, logs.CreateLogger("test"),
            onRegisterSent: () => registerSent++, ct: ct);
        using var node = await host.AcceptAsync(ct);
        var x = await node.ExpectRegisterAsync(ct);
        x.CallFrom.Should().Be(Local);
        await node.DrainAsync(ct, TimeSpan.FromMilliseconds(200));
        opening.IsCompleted.Should().BeFalse("the link is not open until the node answers the 'X'");
        registerSent.Should().Be(1);

        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await using var link = await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        link.Callsign.Should().Be(Local);
        link.Closed.IsCompleted.Should().BeFalse();
        (await node.DrainAsync(ct)).Should().BeEmpty("one 'X' and nothing else: no second registration, no monitor toggle");
        node.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task Open_FailsAndClosesTheSocket_WhenTheNodeRefusesTheCallsign()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();

        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, Accept, logs.CreateLogger("test"), ct: ct);
        using var node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x00));

        var refused = await FluentActions.Awaiting(() => opening.WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<IOException>();
        refused.Which.Message.Should().Contain("callsign refused").And.Contain(Local);
        await FluentActions.Awaiting(() => node.ReadFrameAsync(ct))
            .Should().ThrowAsync<Exception>().Where(e => e is EndOfStreamException || e is IOException,
                "a refused link does not keep its socket");
    }

    [Fact]
    public async Task EachFrame_GoesToItsOwnSession_AndADisconnectOnPortZeroClosesOnlyThePairItNames()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port), FakeAgwSocket.Connect(Other, Local, Port));
        var first = await NextInboundAsync(ct);
        var second = await NextInboundAsync(ct);
        first.Remote.Should().Be(Remote);
        first.Local.Should().Be(Local);
        first.Port.Should().Be(Port);
        second.Remote.Should().Be(Other);

        await node.WriteAsync(ct,
            FakeAgwSocket.Data(Other, Local, Port, "to other\n"),
            FakeAgwSocket.Data(Remote, Local, Port, "to remote\n"));
        (await ReadTextAsync(first.Stream, ct)).Should().Be("to remote\n");
        (await ReadTextAsync(second.Stream, ct)).Should().Be("to other\n");

        // BPQ's 'd' carries the port of the last frame it had from us, not
        // the session's: the pair alone says which session it means.
        await node.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await logs.WaitForAsync("session closed", ct);
        first.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue();
        second.Stream.RemoteClosed.IsCancellationRequested.Should().BeFalse("the 'd' named the other pair");

        // The other session still sends and receives on the link.
        await second.Stream.WriteAsync("reply\n"u8.ToArray(), ct);
        var reply = await node.ReadUntilAsync('D', ct);
        (reply.Port, reply.CallFrom, reply.CallTo).Should().Be((Port, Local, Other));
        Encoding.UTF8.GetString(reply.Payload).Should().Be("reply\n");
    }

    [Fact]
    public async Task AConnectForAKeyStillHeld_RetiresTheOldSession_WhichSendsNoDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var stale = await NextInboundAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var fresh = await NextInboundAsync(ct);

        stale.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue("BPQ never hands over a live duplicate");
        logs.Any("retiring the stale entry").Should().BeTrue();
        await stale.Stream.DisposeAsync();

        await node.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "hello\n"));
        (await ReadTextAsync(fresh.Stream, ct)).Should().Be("hello\n");
        (await node.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd', "a 'd' from the old session would end the new one");
    }

    [Fact]
    public async Task ASilencedSession_SendsNothing_AndARetiredOneIsNoLongerRoutedTo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;
        using var retired = new CancellationTokenSource();

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var session = await NextInboundAsync(ct);
        session.SilenceWhen(retired.Token);

        await retired.CancelAsync();
        await session.Stream.WriteAsync("too late\n"u8.ToArray(), ct);
        await session.Stream.DisposeAsync();
        (await node.DrainAsync(ct)).Should().BeEmpty("once silenced a session writes no data and no 'd'");

        session.Retire();
        await node.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "for nobody\n"));
        await logs.WaitForAsync("'D' for unknown session", ct);
    }

    [Fact]
    public async Task AConsumerThatThrows_DoesNotEndTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var calls = 0;
        var (link, node) = await OpenAsync(host, ct, onInbound: s =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("consumer fault");
            Accept(s);
        });
        await using var _ = link;
        using var __ = node;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await logs.WaitForAsync("carrying on with the connection", ct);

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Other, Local, Port));
        (await NextInboundAsync(ct)).Remote.Should().Be(Other);
        link.Closed.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheNodeDropsTheSocket_TheLinkCloses_AndEverySessionIsClosedByTheFarEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port), FakeAgwSocket.Connect(Other, Local, Port));
        var sessions = new[] { await NextInboundAsync(ct), await NextInboundAsync(ct) };

        node.Close();
        await link.Closed.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        link.CloseReason.Should().Match<Exception>(e => e is EndOfStreamException || e is IOException);
        foreach (var s in sessions)
        {
            s.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue($"{s.Remote}'s link went with the socket");
            (await s.Stream.ReadAsync(new byte[16], ct)).Should().Be(0);
        }
    }

    [Fact]
    public async Task DisposingTheLink_ClosesItAndItsSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        using var __ = node;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var session = await NextInboundAsync(ct);

        await link.DisposeAsync();

        link.Closed.IsCompleted.Should().BeTrue();
        link.CloseReason.Should().BeAssignableTo<OperationCanceledException>();
        session.Stream.RemoteClosed.IsCancellationRequested.Should().BeTrue();
        await session.Stream.DisposeAsync();
        await FluentActions.Awaiting(async () => { while (true) await node.ReadFrameAsync(ct); })
            .Should().ThrowAsync<Exception>().Where(e => e is EndOfStreamException || e is IOException,
                "the socket closes, and the session, closed with it, sends no 'd'");
        node.Received.Should().NotContain(f => f.Kind == 'd');
    }

    private void Accept(AgwLinkSession session) => inbound.Writer.TryWrite(session);

    private async Task<AgwLinkSession> NextInboundAsync(CancellationToken ct) =>
        await inbound.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);

    private async Task<(AgwLink, FakeAgwSocket)> OpenAsync(
        FakeAgwHost host, CancellationToken ct, Action<AgwLinkSession>? onInbound = null)
    {
        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, onInbound ?? Accept, logs.CreateLogger("test"), ct: ct);
        var node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        return (await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct), node);
    }

    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }
}
