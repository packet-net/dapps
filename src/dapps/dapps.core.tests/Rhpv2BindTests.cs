using System.Text.Json.Nodes;
using AwesomeAssertions;
using dapps.core.Services;
using RhpV2.Client;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="Rhpv2Bind"/>: a bind answered the way XRouter 505o answers
/// it, with a reply that isn't JSON, still succeeds or fails as the node
/// said, rather than waiting forever; and a bind answered properly is
/// untouched.
/// </summary>
public sealed class Rhpv2BindTests
{
    [Theory]
    // 505o's reply, verbatim (packethacking/xrouter-container#2).
    [InlineData("""{"type": "bind", "id": 3, "handle": 1, "errCode": 0, Ok}""", 1, true, 0, "Ok")]
    // The same shape, refusing.
    [InlineData("""{"type": "bind", "id": 4, "handle": 2, "errCode": 9, Duplicate socket}""", 2, true, 9, "Duplicate socket")]
    // Another socket's bind is not this one's answer.
    [InlineData("""{"type": "bind", "id": 3, "handle": 1, "errCode": 0, Ok}""", 2, false, 0, "")]
    // Nor is anything but a bind.
    [InlineData("""{"type": "open", "id": 3, "handle": 1, "errCode": 0, Ok}""", 1, false, 0, "")]
    [InlineData("garbage", 1, false, 0, "")]
    public void AnUnreadableFrame_IsReadAsABindReply_OnlyWhenItIsOne(string frame, int handle, bool expected, int code, string text)
    {
        var message = new UnknownMessage("<parse-error>", new JsonObject { ["raw"] = frame });

        Rhpv2Bind.TryReadUnreadableReply(message, handle, out var gotCode, out var gotText).Should().Be(expected);
        gotCode.Should().Be(code);
        gotText.Should().Be(text);
    }

    [Fact]
    public void AMessageRhpClientCouldRead_IsNeverTakenForOne()
    {
        var reply = new BindReplyMessage { Id = 3, Handle = 1, ErrCode = 0, ErrText = "Ok" };

        Rhpv2Bind.TryReadUnreadableReply(reply, 1, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task On505o_ABindTheNodeAccepted_Succeeds_AndTheSocketCanListen()
    {
        using var cts = Within(TimeSpan.FromSeconds(10));
        await using var node = new UnreadableBindReplyRhpNode();
        await using var rhp = await RhpClient.ConnectAsync("127.0.0.1", node.Endpoint.Port, cts.Token);

        var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cts.Token);
        (await Rhpv2Bind.BindAsync(rhp, handle, "G0DPB-1", cts.Token)).Should().BeTrue("the reply wasn't JSON");
        await rhp.ListenAsync(handle, OpenFlags.Passive, cts.Token);

        node.Requests.Select(r => r.GetType())
            .Should().Equal(typeof(SocketMessage), typeof(BindMessage), typeof(ListenMessage));
    }

    [Fact]
    public async Task On505o_ABindTheNodeRefused_ThrowsItsError()
    {
        using var cts = Within(TimeSpan.FromSeconds(10));
        await using var node = new UnreadableBindReplyRhpNode(bindErrCode: 9, bindErrText: "Duplicate socket");
        await using var rhp = await RhpClient.ConnectAsync("127.0.0.1", node.Endpoint.Port, cts.Token);

        var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cts.Token);
        Func<Task> act = () => Rhpv2Bind.BindAsync(rhp, handle, "G0DPB-1", cts.Token);
        var refused = await act.Should().ThrowAsync<RhpServerException>();
        refused.Which.ErrorCode.Should().Be(RhpErrorCode.DuplicateSocket);
        refused.Which.ErrorText.Should().Be("Duplicate socket");
    }

    [Fact]
    public async Task ABindAnsweredProperly_IsJustABind()
    {
        using var cts = Within(TimeSpan.FromSeconds(10));
        await using var server = new MockRhpServer();
        server.Start();
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, cts.Token);

        var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cts.Token);
        (await Rhpv2Bind.BindAsync(rhp, handle, "G0DPB-1", cts.Token)).Should().BeFalse();

        var bind = server.ReceivedFrames.OfType<BindMessage>().Single();
        bind.Local.Should().Be("G0DPB-1");
        bind.Port.Should().BeNull("the listener takes all of the node's ports");
    }

    /// <summary>The test's own token, cut short: a bind that waits forever
    /// fails the test rather than hanging the run.</summary>
    private static CancellationTokenSource Within(TimeSpan limit)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }
}
