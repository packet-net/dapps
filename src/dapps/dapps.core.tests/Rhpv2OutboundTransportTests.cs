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

    [Fact]
    public async Task Stream_Read_KeepsDataTheNodeSendsStraightBehindTheOpenReply()
    {
        // pdn answers an open only once the far end's UA is in, so a quick
        // peer's prompt can follow the reply in the same read. RhpClient
        // raises that recv before the code after OpenAsync runs; the
        // transport must already be listening, or the prompt is lost.
        var ct = TestContext.Current.CancellationToken;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            var s = tcp.GetStream();
            while (await RhpFraming.ReadFrameAsync(s, ct) is { } frame)
            {
                var reply = new MemoryStream();
                switch (RhpJson.Deserialize(frame))
                {
                    case OpenMessage open:
                        RhpFraming.WriteFrame(reply, RhpJson.Serialize(new OpenReplyMessage { Id = open.Id, Handle = 101, ErrText = "Ok" }));
                        RhpFraming.WriteFrame(reply, RhpJson.Serialize(new RecvMessage { Handle = 101, Data = RhpDataEncoding.ToWireString("DAPPSv1>\n"u8) }));
                        break;
                    case CloseMessage close:
                        RhpFraming.WriteFrame(reply, RhpJson.Serialize(new CloseReplyMessage { Id = close.Id, Handle = close.Handle, ErrText = "Ok" }));
                        break;
                }
                await s.WriteAsync(reply.ToArray(), ct);
            }
        }, ct);

        try
        {
            var transport = new Rhpv2OutboundTransport(
                "127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
                NullLogger<Rhpv2OutboundTransport>.Instance);
            await using var conn = await transport.ConnectAsync("G0DPA-1", "G0DPB-1", 0, ct);

            var buf = new byte[64];
            var n = await conn.Stream.ReadAsync(buf.AsMemory(), ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            Encoding.UTF8.GetString(buf, 0, n).Should().Be("DAPPSv1>\n");
        }
        finally
        {
            listener.Stop();
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
