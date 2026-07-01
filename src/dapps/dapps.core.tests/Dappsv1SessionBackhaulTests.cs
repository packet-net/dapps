using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for the AGW-stream backhaul implementation. The
/// end-to-end <c>TtlForwardingIntegrationTests</c> exercises this
/// against a real BPQ over AXIP-UDP, but those need Docker. These
/// drive the protocol state machine directly with a fake transport
/// that hands back canned receiver bytes - fast, hermetic, covers
/// each rejection path explicitly.
/// </summary>
public sealed class Dappsv1SessionBackhaulTests
{
    [Fact]
    public async Task CanHandle_NoUdpEndpoint_True()
    {
        var sb = MakeBackhaul([]);
        sb.CanHandle(new BackhaulRoute("N0DEST", BearerPort: 0)).Should().BeTrue();
    }

    [Fact]
    public async Task CanHandle_UdpEndpointSet_False()
    {
        var sb = MakeBackhaul([]);
        // AGW must yield to UDP when both bearer hints are set so the
        // multi-backhaul dispatcher in OutboundMessageManager picks UDP.
        sb.CanHandle(new BackhaulRoute("N0DEST", BearerPort: 0, UdpEndpoint: "127.0.0.1:1880"))
            .Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CanHandle_MeshCoreChannelSet_False()
    {
        var sb = MakeBackhaul([]);
        // AGW must NOT claim a route discovered only over MeshCore (#27). If the
        // MeshCore bearer is down (disabled / link failed / not yet started) it
        // declines the route; AGW claiming it would mis-route a LoRa-only peer over a
        // connected-mode session (spurious RF on a gateway node). Leave it Unreachable.
        sb.CanHandle(new BackhaulRoute("N0DEST", BearerPort: 0, MeshCoreChannel: "dapps"))
            .Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SendAsync_HappyPath_ReturnsOkAndWritesIhaveLine()
    {
        var transport = new FakeOutboundTransport(
            cannedReceiverBytes: Encoding.UTF8.GetBytes("DAPPSv1>\nsend mid0001\nack mid0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0001", "app@N0DEST", Salt: 1L, Ttl: 60, Payload: "hi"u8.ToArray()),
            new BackhaulRoute("N0DEST", BearerPort: 1),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        written.Should().Contain("ihave mid0001");
        written.Should().Contain("ttl=60");
        written.Should().Contain("dst=app@N0DEST");
    }

    [Fact]
    public async Task SendAsync_NoPromptFromRemote_ReturnsFail()
    {
        var transport = new FakeOutboundTransport(
            cannedReceiverBytes: "garbage and no prompt"u8.ToArray());
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0002", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task SendAsync_OfferRejected_ReturnsFail()
    {
        var transport = new FakeOutboundTransport(
            cannedReceiverBytes: Encoding.UTF8.GetBytes("DAPPSv1>\nerror mid0003\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0003", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("offer rejected");
    }

    [Fact]
    public async Task SendAsync_PayloadNAKed_ReturnsFail()
    {
        // Receiver accepts the offer but bad-frames the payload (hash mismatch).
        var transport = new FakeOutboundTransport(
            cannedReceiverBytes: Encoding.UTF8.GetBytes("DAPPSv1>\nsend mid0004\nbad mid0004\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0004", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("payload");
    }

    [Fact]
    public async Task SendAsync_TransportThrows_ReturnsFailWithMessage()
    {
        var transport = new ThrowingTransport(new InvalidOperationException("kaboom"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("midbomb", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("kaboom");
    }

    private static Dappsv1SessionBackhaul MakeBackhaul(byte[] cannedReceiverBytes)
        => new(new FakeOutboundTransport(cannedReceiverBytes), NullLoggerFactory.Instance);

    private sealed class FakeOutboundTransport(byte[] cannedReceiverBytes) : IDappsOutboundTransport
    {
        public byte[] WriteCapture => _stream?.WriteCapture.ToArray() ?? [];

        private CapturingStream? _stream;

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            _stream = new CapturingStream(cannedReceiverBytes);
            return Task.FromResult<IDappsConnection>(new FakeConnection(_stream));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync()
            {
                Stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class CapturingStream(byte[] preloaded) : Stream
        {
            private readonly MemoryStream _read = new(preloaded);
            public MemoryStream WriteCapture { get; } = new();

            public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _read.ReadAsync(buffer, offset, count, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _read.ReadAsync(buffer, ct);
            public override void Write(byte[] buffer, int offset, int count) => WriteCapture.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                WriteCapture.Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                WriteCapture.Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            public override void Flush() => WriteCapture.Flush();
            public override Task FlushAsync(CancellationToken ct) => WriteCapture.FlushAsync(ct);
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    private sealed class ThrowingTransport(Exception toThrow) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            => Task.FromException<IDappsConnection>(toThrow);
    }
}
