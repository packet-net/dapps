using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="Rhpv2UiFrameSender"/>, which sends AGW discovery's beacons
/// over RHPv2 while the AGW connection only listens, against rhp2lib's
/// <c>MockRhpServer</c> playing an XRouter whose <c>PORT=</c> numbers are
/// 1, 3 and 4: a bind to any other number is "Invalid port", as the real
/// one answers. The frame goes on the port by the node's own number for it
/// (#370); matching that to the AGW port is <see cref="NodePortNumbers"/>'s
/// (<see cref="NodePortNumbersTests"/>).
/// </summary>
public sealed class Rhpv2UiFrameSenderTests
{
    private const string Local = "G5ALF-3";
    private static readonly int[] Ports = [1, 3, 4];

    [Fact]
    public async Task AFrame_GoesOnTheRhpPortByItsNumber_FromOurCallsign_AndEveryHandleIsClosed()
    {
        await using var server = Xrouter();
        var payload = Encoding.ASCII.GetBytes("DAPPS v1 callsign=G5ALF-3 hops=0 ttl=600 end=1");

        await Sender(server).SendUiAsync(4, Local, "DAPPS", payload, TestContext.Current.CancellationToken);

        var frames = server.ReceivedFrames.ToList();
        var sendTo = frames.OfType<SendToMessage>().Should().ContainSingle().Subject;
        (sendTo.Local, sendTo.Remote, sendTo.Port).Should().Be((Local, "DAPPS", "4"), "PORT=4 by its own number");
        RhpDataEncoding.FromWireString(sendTo.Data).Should().Equal(payload);
        frames.OfType<BindMessage>().Should().ContainSingle()
            .Which.Should().Match<BindMessage>(b => b.Handle == sendTo.Handle && b.Local == Local && b.Port == "4");
        frames.OfType<CloseMessage>().Select(c => c.Handle).Should().BeEquivalentTo(
            frames.OfType<BindMessage>().Select(b => b.Handle), "every socket opened is closed");
    }

    [Fact]
    public async Task APortTheNodeDoesNotHave_IsNotSent()
    {
        await using var server = Xrouter();
        var thrown = await FluentActions.Awaiting(() => Sender(server).SendUiAsync(2, Local, "DAPPS", [1], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<UiFrameNotSentException>();
        thrown.Which.Message.Should().Contain("Invalid port");
        server.ReceivedFrames.Should().NotContain(f => f is SendToMessage);
    }

    [Fact]
    public async Task PortZero_NothingGoesToTheNode()
    {
        await using var server = Xrouter();
        await FluentActions.Awaiting(() => Sender(server).SendUiAsync(0, Local, "DAPPS", [1], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<UiFrameNotSentException>();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        server.ReceivedFrames.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusal_IsNotSent()
    {
        await using var server = Xrouter();
        server.Handler = Ports1Then(m => m is SendToMessage s
            ? new SendToReplyMessage { Handle = s.Handle, ErrCode = RhpErrorCode.DuplicateSocket, ErrText = "Duplicate socket" }
            : null);

        var thrown = await FluentActions.Awaiting(() => Sender(server).SendUiAsync(1, Local, "DAPPS", [1, 2, 3], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<UiFrameNotSentException>();
        thrown.Which.Message.Should().Contain("Duplicate socket").And.Contain("not sent");
    }

    [Fact]
    public async Task NoNode_IsNotSent()
    {
        var unused = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        unused.Start();
        var port = ((System.Net.IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var sender = new Rhpv2UiFrameSender(Options("127.0.0.1", port), NullLogger<Rhpv2UiFrameSender>.Instance);

        await FluentActions.Awaiting(() => sender.SendUiAsync(1, Local, "DAPPS", [1], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<UiFrameNotSentException>();
    }

    [Fact]
    public async Task WithTxStopped_NothingGoesToTheNode()
    {
        await using var server = Xrouter();
        await FluentActions.Awaiting(() => Sender(server, new ClosedGate()).SendUiAsync(1, Local, "DAPPS", [1], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<TxStoppedException>();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        server.ReceivedFrames.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoFramesAtOnce_GoOneAfterTheOther()
    {
        // XRouter refuses a second datagram bind of one callsign on a port.
        await using var server = Xrouter();
        var inSendTo = 0;
        var most = 0;
        server.Handler = Ports1Then(m =>
        {
            if (m is not SendToMessage) return null;
            var now = Interlocked.Increment(ref inSendTo);
            lock (server) most = Math.Max(most, now);
            Thread.Sleep(300);
            Interlocked.Decrement(ref inSendTo);
            return null;
        });
        var sender = Sender(server);
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            sender.SendUiAsync(1, Local, "DAPPS", [1], ct),
            sender.SendUiAsync(3, Local, "DAPPS", [2], ct));

        server.ReceivedFrames.OfType<SendToMessage>().Should().HaveCount(2);
        most.Should().Be(1);
    }

    [Fact]
    public async Task AFrameThatFailed_DoesNotHoldUpTheNext()
    {
        await using var server = Xrouter();
        var refuse = true;
        server.Handler = Ports1Then(m => m is SendToMessage s && refuse
            ? new SendToReplyMessage { Handle = s.Handle, ErrCode = RhpErrorCode.DuplicateSocket, ErrText = "Duplicate socket" }
            : null);
        var sender = Sender(server, timeout: TimeSpan.FromSeconds(2));
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => sender.SendUiAsync(1, Local, "DAPPS", [1], ct)).Should().ThrowAsync<UiFrameNotSentException>();
        refuse = false;
        await sender.SendUiAsync(3, Local, "DAPPS", [2], ct).WaitAsync(TimeSpan.FromSeconds(1.5), ct);
    }

    [Fact]
    public async Task ANodeThatNeverAnswersTheSendto_IsGivenUpOnInTime_AndMayHaveSent()
    {
        await using var server = Xrouter();
        // A push, not a reply: the sendto is never answered.
        server.Handler = Ports1Then(m => m is SendToMessage s ? new StatusMessage { Handle = s.Handle, Flags = 0, Seqno = 99 } : null);
        var sender = Sender(server, timeout: TimeSpan.FromSeconds(2));
        var ct = TestContext.Current.CancellationToken;

        var thrown = await FluentActions.Awaiting(() => sender.SendUiAsync(1, Local, "DAPPS", [1], ct).WaitAsync(TimeSpan.FromSeconds(5), ct))
            .Should().ThrowAsync<IOException>();
        thrown.Which.Should().NotBeOfType<UiFrameNotSentException>("the sendto had gone, so the frame may be on air ({0})", thrown.Which.Message);
        thrown.Which.Message.Should().Contain("may have gone");
    }

    [Fact]
    public async Task ANodeThatNeverAnswersBeforeTheSendto_IsGivenUpOnInTime_AndNotSent()
    {
        await using var server = Xrouter();
        server.Handler = m => m is SocketMessage ? new StatusMessage { Handle = 0, Flags = 0, Seqno = 99 } : null;
        var sender = Sender(server, timeout: TimeSpan.FromSeconds(2));
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => sender.SendUiAsync(1, Local, "DAPPS", [1], ct).WaitAsync(TimeSpan.FromSeconds(5), ct))
            .Should().ThrowAsync<UiFrameNotSentException>();
    }

    /// <summary>A mock XRouter with these <c>PORT=</c> numbers.</summary>
    internal static MockRhpServer Xrouter(params int[] ports)
    {
        var server = new MockRhpServer { Handler = PortsOnly(ports.Length == 0 ? Ports : ports) };
        server.Start();
        return server;
    }

    private static Func<RhpMessage, RhpMessage?> PortsOnly(int[] ports) => m =>
        m is BindMessage b && !(int.TryParse(b.Port, out var n) && ports.Contains(n))
            ? new BindReplyMessage { Handle = b.Handle, ErrCode = 10, ErrText = "Invalid port" }
            : null;

    /// <summary>The default ports, then <paramref name="then"/>.</summary>
    private static Func<RhpMessage, RhpMessage?> Ports1Then(Func<RhpMessage, RhpMessage?> then) =>
        m => PortsOnly(Ports)(m) ?? then(m);

    private static Rhpv2UiFrameSender Sender(MockRhpServer server, IDappsTxGate? gate = null, TimeSpan? timeout = null) =>
        new(Options(server.Endpoint.Address.ToString(), server.Endpoint.Port), NullLogger<Rhpv2UiFrameSender>.Instance, gate)
        {
            SendTimeout = timeout ?? Rhpv2UiFrameSender.DefaultSendTimeout,
        };

    internal static MutableOptionsMonitor<SystemOptions> Options(string host, int port) =>
        new(new SystemOptions { NodeBearer = "rhpv2", NodeHost = host, RhpPort = port });

    private sealed class ClosedGate : IDappsTxGate
    {
        public bool TxAllowed => false;
        public string? BlockReason => "test";
    }
}
