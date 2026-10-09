using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Rhp;
using Microsoft.Extensions.Logging.Abstractions;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for <see cref="Rhpv2OutboundTransport"/> against the
/// rhp2lib-net <c>MockRhpServer</c> (in-process JSON-over-TCP). Locks
/// in the wire-shape DAPPS produces: AX.25 stream socket, active open,
/// 1-indexed RHP port name from DAPPS's 0-indexed AGW port byte, and
/// the bytes-in / bytes-out path through <c>RhpClient</c>.
///
/// Why MockRhpServer rather than a real XRouter container: the real
/// xrouter coverage is in <c>scripts/sim-mixed-bearer.sh</c> end-to-end;
/// this suite locks the contract DAPPS expects from the bearer at the
/// frame level, runs in milliseconds, and would catch a regression in
/// the +1 port-name conversion or the auth-then-open ordering.
/// </summary>
public sealed class Rhpv2OutboundTransportTests
{
    [Fact]
    public async Task ConnectAsync_OpensActiveAx25StreamWith1IndexedPortName()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            localCallsign: "G0DPA-1",
            remoteCallsign: "G0DPB-1",
            bearerPort: 0,
            ct);

        var open = WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        open.Pfam.Should().Be(ProtocolFamily.Ax25);
        open.Mode.Should().Be(SocketMode.Stream);
        open.Local.Should().Be("G0DPA-1");
        open.Remote.Should().Be("G0DPB-1");
        ((OpenFlags)open.Flags & OpenFlags.Active).Should().Be(OpenFlags.Active,
            "outbound forwards must be active opens; passive would just listen");
        open.Port.Should().Be("1",
            "DAPPS's 0-indexed AGW port byte maps to RHPv2's 1-indexed port name (XRouter PORT=1)");
    }

    [Fact]
    public async Task ConnectAsync_HigherPortByteShifts_To2()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            localCallsign: "G0DPA-1",
            remoteCallsign: "G0DPB-1",
            bearerPort: 1,
            ct);

        var open = WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        open.Port.Should().Be("2",
            "AGW port byte 1 -> RHP port name 2");
    }

    [Fact]
    public async Task ConnectAsync_WithAuthUser_SendsAuthBeforeOpen()
    {
        await using var server = new MockRhpServer
        {
            RequireAuth = true,
            Credentials = ("alice", "s3cret"),
        };
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance,
            authUser: "alice", authPass: "s3cret");

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            localCallsign: "G0DPA-1",
            remoteCallsign: "G0DPB-1",
            bearerPort: 0,
            ct);

        var ordered = WaitForFrames(server, count: 2, TimeSpan.FromSeconds(2));
        ordered[0].Should().BeOfType<AuthMessage>(
            "the transport must AUTH before OPEN when credentials are configured");
        ordered[1].Should().BeOfType<OpenMessage>();

        var auth = (AuthMessage)ordered[0];
        auth.User.Should().Be("alice");
        auth.Pass.Should().Be("s3cret");
    }

    [Fact]
    public async Task ConnectAsync_WithoutAuthUser_DoesNotSendAuth()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            localCallsign: "G0DPA-1", remoteCallsign: "G0DPB-1",
            bearerPort: 0, ct);

        WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        server.ReceivedFrames.Should().NotContain(f => f is AuthMessage,
            "no AUTH should be sent when authUser is null");
    }

    [Fact]
    public async Task Stream_Write_ProducesSendOnHandle()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            "G0DPA-1", "G0DPB-1", 0, ct);

        var openReply = WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        // After ConnectAsync, drain the OpenMessage so the next Send
        // is what we assert against.
        var preSendFrames = server.ReceivedFrames.Count;

        await conn.Stream.WriteAsync("DAPPSv1>\n"u8.ToArray(), ct);
        await conn.Stream.FlushAsync(ct);

        var send = WaitForFrame<SendMessage>(server, TimeSpan.FromSeconds(2));
        // The data field is wire-encoded; we only need to assert it
        // round-trips through the lib's encoder back to our bytes.
        var decoded = RhpDataEncoding.FromWireString(send.Data);
        Encoding.UTF8.GetString(decoded).Should().Be("DAPPSv1>\n");
    }

    [Fact]
    public async Task Stream_Read_ReceivesBytesFromMatchingHandle()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            "G0DPA-1", "G0DPB-1", 0, ct);

        var open = WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        // The MockRhpServer assigns handles starting at 101; we don't
        // need to know the exact value, just that it matches the open
        // reply. Read the handle from the next-handle counter via a
        // STATUS round-trip would be overkill - instead, capture the
        // handle from the SEND side by writing one byte and watching
        // the SendMessage fly past.
        await conn.Stream.WriteAsync(new byte[] { 0x01 }, ct);
        var send = WaitForFrame<SendMessage>(server, TimeSpan.FromSeconds(2));
        var ourHandle = send.Handle;

        // Simulate the server pushing a server-initiated RECV for our
        // handle. The transport's per-handle filter should route this
        // to the stream.
        await server.BroadcastAsync(new RecvMessage
        {
            Handle = ourHandle,
            Data = RhpDataEncoding.ToWireString("hello\n"u8),
        }, ct);

        var buf = new byte[64];
        var n = await conn.Stream.ReadAsync(buf.AsMemory(), ct).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("hello\n");
    }

    // pdn answers an open only once the far end's UA is in, so a quick
    // peer's prompt, and even its hang-up, can reach DAPPS in the same read
    // as the open reply, or ahead of it. RhpClient raises those events as it
    // reads them, before the code after OpenAsync runs, so the transport has
    // to be listening from before the open. With the prompt ahead of the
    // reply the old transport lost it every time; with it behind, only when
    // the read loop beat OpenAsync's continuation, which is usual but not
    // certain.

    [Fact]
    public async Task Stream_Read_KeepsDataTheNodeSendsBeforeTheOpenReply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Prompt, Reply(open)], ct);
        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

        (await ReadTextAsync(conn.Stream, ct)).Should().Be("DAPPSv1>\n");
    }

    [Fact]
    public async Task Stream_Read_KeepsDataTheNodeSendsStraightBehindTheOpenReply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open), Prompt], ct);
        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

        (await ReadTextAsync(conn.Stream, ct)).Should().Be("DAPPSv1>\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_ReadsThePromptThenEnds_WhenTheNodeClosesAtOnce(bool beforeTheReply)
    {
        var ct = TestContext.Current.CancellationToken;
        var closed = new CloseMessage { Handle = OpenedHandle };
        await using var node = ScriptedNode.Start(
            open => beforeTheReply ? [Prompt, closed, Reply(open)] : [Reply(open), Prompt, closed], ct);
        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

        (await ReadTextAsync(conn.Stream, ct)).Should().Be("DAPPSv1>\n");
        (await ReadTextAsync(conn.Stream, ct)).Should().BeEmpty("the node closed the handle, so the stream ends");
    }

    // pdn (0.57.0 on) puts "crossed": true on the open reply when the peer
    // was calling us as we called it, or the link was already up: no prompt
    // is coming then. Without the key, as from XRouter, the session waits
    // for a prompt as before.

    [Fact]
    public async Task CrossedCall_Completes_WhenTheOpenReplySaysTheCallsCrossed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open, crossed: true)], ct);
        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

        conn.CrossedCall.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task CrossedCall_NeverCompletes_WhenTheOpenReplyHasNoCrossedKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open), Prompt], ct);
        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

        (await ReadTextAsync(conn.Stream, ct)).Should().Be("DAPPSv1>\n");
        conn.CrossedCall.IsCompleted.Should().BeFalse("the node didn't say, so a prompt may be coming");
    }

    // XRouter answers an active open at once and reports the link once the
    // far end's UA is in (or closes the handle when its retries run out).
    // A session started before then waits for a prompt that can't come yet,
    // and closing that handle mid-connect leaves a redial refused with
    // "Duplicate socket".

    [Fact]
    public async Task ConnectAsync_WaitsForTheNodeToSayTheLinkIsUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open)], ct,
            onStatus: q => [StatusReply(q), Status(StatusFlags.None)]);

        var connecting = node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);
        await Task.Delay(300, ct);
        connecting.IsCompleted.Should().BeFalse("the node hasn't said the link is up");

        await node.PushAsync([Status(StatusFlags.ConOk | StatusFlags.Connected), Prompt], ct);
        await using var conn = await connecting.WaitAsync(TimeSpan.FromSeconds(5), ct);
        (await ReadTextAsync(conn.Stream, ct)).Should().Be("DAPPSv1>\n");
    }

    [Fact]
    public async Task ConnectAsync_Fails_WhenTheNodeGivesUpConnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open)], ct,
            onStatus: q => [StatusReply(q), Status(StatusFlags.None)]);

        var connecting = node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);
        await Task.Delay(300, ct);
        await node.PushAsync([Status(StatusFlags.None), new CloseMessage { Handle = OpenedHandle }], ct);

        var act = () => connecting.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await act.Should().ThrowAsync<RhpLinkFailedException>().WithMessage("*G0DPB-1 didn't answer*");
    }

    [Fact]
    public async Task ConnectAsync_Fails_WhenTheLinkNeverComesUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open)], ct,
            onStatus: q => [StatusReply(q), Status(StatusFlags.None)], linkUpWait: TimeSpan.FromMilliseconds(500));

        var act = () => node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await act.Should().ThrowAsync<RhpLinkFailedException>();
        node.Closes.Should().Be(1, "the half-open handle is closed, not left with the node");
    }

    [Fact]
    public async Task ConnectAsync_TakesTheOpenAsTheLink_WhenTheNodeSaysItsConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = ScriptedNode.Start(open => [Reply(open)], ct,
            onStatus: q => [StatusReply(q), Status(StatusFlags.ConOk | StatusFlags.Connected)]);

        await using var conn = await node.Transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    private static StatusReplyMessage StatusReply(StatusMessage q) => new() { Id = q.Id, Handle = q.Handle, ErrText = "Ok" };

    private static StatusMessage Status(StatusFlags flags) => new() { Handle = OpenedHandle, Flags = (int)flags };

    private const int OpenedHandle = 101;

    private static RecvMessage Prompt => new() { Handle = OpenedHandle, Data = RhpDataEncoding.ToWireString("DAPPSv1>\n"u8) };

    private static OpenReplyMessage Reply(OpenMessage open, bool crossed = false) =>
        new() { Id = open.Id, Handle = OpenedHandle, ErrText = "Ok", Crossed = crossed ? true : null };

    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[64];
        var n = await stream.ReadAsync(buf.AsMemory(), ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>
    /// An RHPv2 node that answers an open with what the script says, all in
    /// one write so RhpClient reads the messages back to back, and answers a
    /// close. Unlike MockRhpServer it can put a push ahead of a reply.
    /// </summary>
    private sealed class ScriptedNode : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        private Task serving = Task.CompletedTask;
        private System.Net.Sockets.NetworkStream? client;
        private readonly SemaphoreSlim writing = new(1, 1);

        public int Closes { get; private set; }

        /// <summary>Send the client messages of the node's own accord.</summary>
        public async Task PushAsync(RhpMessage[] messages, CancellationToken ct)
        {
            while (client is null) await Task.Delay(10, ct);
            await WriteAsync(client, messages, ct);
        }

        private async Task WriteAsync(Stream s, RhpMessage[] messages, CancellationToken ct)
        {
            var write = new MemoryStream();
            foreach (var m in messages) RhpFraming.WriteFrame(write, RhpJson.Serialize(m));
            await writing.WaitAsync(ct);
            try { await s.WriteAsync(write.ToArray(), ct); }
            finally { writing.Release(); }
        }

        public Rhpv2OutboundTransport Transport { get; private set; } = null!;

        public static ScriptedNode Start(Func<OpenMessage, RhpMessage[]> onOpen, CancellationToken ct,
            Func<StatusMessage, RhpMessage[]>? onStatus = null, TimeSpan? linkUpWait = null)
        {
            var node = new ScriptedNode();
            node.listener.Start();
            node.Transport = new Rhpv2OutboundTransport(
                "127.0.0.1", ((System.Net.IPEndPoint)node.listener.LocalEndpoint).Port,
                NullLogger<Rhpv2OutboundTransport>.Instance)
            {
                LinkUpWait = linkUpWait ?? TimeSpan.FromMinutes(5),
            };
            node.serving = Task.Run(async () =>
            {
                using var tcp = await node.listener.AcceptTcpClientAsync(ct);
                var s = tcp.GetStream();
                node.client = s;
                while (await RhpFraming.ReadFrameAsync(s, ct) is { } frame)
                {
                    var messages = RhpJson.Deserialize(frame) switch
                    {
                        OpenMessage open => onOpen(open),
                        StatusMessage status when onStatus is not null => onStatus(status),
                        CloseMessage close when close.Id is not null => node.Closed(close),
                        _ => [],
                    };
                    await node.WriteAsync(s, messages, ct);
                }
            }, ct);
            return node;
        }

        private RhpMessage[] Closed(CloseMessage close)
        {
            Closes++;
            return [new CloseReplyMessage { Id = close.Id, Handle = close.Handle, ErrText = "Ok" }];
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try { await serving; } catch { /* the client went, or the test was cancelled */ }
        }
    }

    [Fact]
    public async Task Stream_IgnoresRecvForOtherHandles()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        await using var conn = await transport.ConnectAsync(
            "G0DPA-1", "G0DPB-1", 0, ct);

        WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        await conn.Stream.WriteAsync(new byte[] { 0x01 }, ct);
        var ourHandle = WaitForFrame<SendMessage>(server, TimeSpan.FromSeconds(2)).Handle;

        // Push RECV for an unrelated handle. Stream.ReadAsync must
        // not return early with these bytes.
        await server.BroadcastAsync(new RecvMessage
        {
            Handle = ourHandle + 999,
            Data = RhpDataEncoding.ToWireString("not-for-us"u8),
        }, ct);

        var buf = new byte[64];
        var read = conn.Stream.ReadAsync(buf.AsMemory(), ct).AsTask();
        var first = await Task.WhenAny(read, Task.Delay(250, ct));
        first.Should().NotBe((Task)read,
            "RECV for a foreign handle must not surface on this session's stream");

        // Now push for OUR handle and confirm Read fires.
        await server.BroadcastAsync(new RecvMessage
        {
            Handle = ourHandle,
            Data = RhpDataEncoding.ToWireString("for-us"u8),
        }, ct);
        var n = await read.WaitAsync(TimeSpan.FromSeconds(2), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("for-us");
    }

    [Fact]
    public async Task Dispose_ClosesHandle()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var transport = new Rhpv2OutboundTransport(
            server.Endpoint.Address.ToString(), server.Endpoint.Port,
            NullLogger<Rhpv2OutboundTransport>.Instance);

        var ct = TestContext.Current.CancellationToken;
        var conn = await transport.ConnectAsync(
            "G0DPA-1", "G0DPB-1", 0, ct);

        WaitForFrame<OpenMessage>(server, TimeSpan.FromSeconds(2));
        await conn.Stream.WriteAsync(new byte[] { 0x01 }, ct);
        var ourHandle = WaitForFrame<SendMessage>(server, TimeSpan.FromSeconds(2)).Handle;

        await conn.DisposeAsync();

        var close = WaitForFrame<CloseMessage>(server, TimeSpan.FromSeconds(2));
        close.Handle.Should().Be(ourHandle,
            "DisposeAsync must close the session handle so the node tears down the L2 link");
    }

    private static T WaitForFrame<T>(MockRhpServer server, TimeSpan timeout) where T : RhpMessage
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var f in server.ReceivedFrames)
            {
                if (f is T match) return match;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException(
            $"Did not observe a {typeof(T).Name} within {timeout}; saw [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }

    private static List<RhpMessage> WaitForFrames(MockRhpServer server, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.ReceivedFrames.Count >= count) return server.ReceivedFrames.Take(count).ToList();
            Thread.Sleep(10);
        }
        throw new TimeoutException(
            $"Did not observe {count} frames within {timeout}; saw [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }
}
