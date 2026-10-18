using System.Text;
using AwesomeAssertions;
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
/// <c>MockRhpServer</c>: the wire shape (a datagram socket bound to our
/// callsign on the 1-indexed port, one <c>sendto</c>, the handle closed),
/// a refusal surfacing as not sent, and nothing sent while TX is stopped.
/// </summary>
public sealed class Rhpv2UiFrameSenderTests
{
    private const string Local = "G5ALF-3";

    [Fact]
    public async Task AFrame_GoesAsADatagramFromOurCallsign_OnThePort_AndTheHandleIsClosed()
    {
        await using var server = new MockRhpServer();
        server.Start();
        var sender = Sender(server);
        var payload = Encoding.ASCII.GetBytes("DAPPS v1 callsign=G5ALF-3 hops=0 ttl=600 end=1");

        await sender.SendUiAsync(1, Local, "DAPPS", payload, TestContext.Current.CancellationToken);

        var frames = await FramesAsync(server, 4);
        var socket = frames[0].Should().BeOfType<SocketMessage>().Subject;
        (socket.Pfam, socket.Mode).Should().Be((ProtocolFamily.Ax25, SocketMode.Dgram));
        var bind = frames[1].Should().BeOfType<BindMessage>().Subject;
        (bind.Local, bind.Port).Should().Be((Local, "2"), "DAPPS's port 1 is RHPv2's port \"2\"");
        var sendTo = frames[2].Should().BeOfType<SendToMessage>().Subject;
        (sendTo.Handle, sendTo.Local, sendTo.Remote, sendTo.Port).Should().Be((bind.Handle, Local, "DAPPS", "2"));
        RhpDataEncoding.FromWireString(sendTo.Data).Should().Equal(payload);
        frames[3].Should().BeOfType<CloseMessage>().Which.Handle.Should().Be(bind.Handle);
    }

    [Fact]
    public async Task ARefusal_IsAnIOException_SayingNotSent()
    {
        await using var server = new MockRhpServer
        {
            Handler = m => m is BindMessage b
                ? new BindReplyMessage { Handle = b.Handle, ErrCode = RhpErrorCode.DuplicateSocket, ErrText = "Duplicate socket" }
                : null,
        };
        server.Start();

        var thrown = await FluentActions.Awaiting(() => Sender(server).SendUiAsync(0, Local, "DAPPS", [1, 2, 3], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<IOException>();
        thrown.Which.Message.Should().Contain("Duplicate socket").And.Contain("not sent");
        server.ReceivedFrames.Should().NotContain(f => f is SendToMessage);
    }

    [Fact]
    public async Task WithTxStopped_NothingGoesToTheNode()
    {
        await using var server = new MockRhpServer();
        server.Start();

        await FluentActions.Awaiting(() => Sender(server, new ClosedGate()).SendUiAsync(0, Local, "DAPPS", [1], TestContext.Current.CancellationToken))
            .Should().ThrowAsync<TxStoppedException>();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        server.ReceivedFrames.Should().BeEmpty();
    }

    private static Rhpv2UiFrameSender Sender(MockRhpServer server, IDappsTxGate? gate = null) =>
        new(new MutableOptionsMonitor<SystemOptions>(new SystemOptions
            {
                NodeBearer = "rhpv2", NodeHost = server.Endpoint.Address.ToString(), RhpPort = server.Endpoint.Port,
            }),
            NullLogger<Rhpv2UiFrameSender>.Instance, gate);

    private static async Task<List<RhpMessage>> FramesAsync(MockRhpServer server, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (server.ReceivedFrames.Count >= count) return server.ReceivedFrames.Take(count).ToList();
            await Task.Delay(10);
        }
        throw new TimeoutException($"saw only [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }

    private sealed class ClosedGate : IDappsTxGate
    {
        public bool TxAllowed => false;
        public string? BlockReason => "test";
    }
}
