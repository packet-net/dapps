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

    [Fact]
    public async Task SendAsync_TransportSaysPeerSessionBusy_ReturnsDeferredNotFailed()
    {
        // #185: BearerSwitchingOutboundTransport's own last-moment check
        // can decline a dial the forwarder's earlier check already let
        // through. That must land here as a defer (message stays
        // queued, no cooldown, no route outcome recorded), the same as
        // any other #178 non-send - not as a failure.
        var transport = new ThrowingTransport(new PeerSessionBusyException("N0DEST", "inbound"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("middefer", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Deferred.Should().BeTrue("a busy peer is not a failure of the route");
        result.Error.Should().Contain("N0DEST").And.Contain("inbound");
    }

    [Fact]
    public async Task SendBatchAsync_ThreeMessages_OneConnectionAndEachOneAcked()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nsend msg0001\nack msg0001\nsend msg0002\nack msg0002\nsend msg0003\nack msg0003\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"), Msg("msg0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        transport.Connects.Should().Be(1);
        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted)).Should().Equal(
            ("msg0001", true), ("msg0002", true), ("msg0003", true));
        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        written.Should().Contain("ihave msg0001").And.Contain("ihave msg0002").And.Contain("ihave msg0003");
    }

    [Fact]
    public async Task SendBatchAsync_SecondOfferRefused_StopsThere_AndTheThirdIsNeverHandedOut()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nsend msg0001\nack msg0001\nerror msg0002\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"), Msg("msg0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted)).Should().Equal(("msg0001", true), ("msg0002", false));
        batch.Outcomes[1].Result.Error.Should().Contain("offer rejected");
        batch.Remaining.Should().Be(1, "the message after a refusal stays queued, untouched");
    }

    [Fact]
    public async Task SendBatchAsync_ConnectFails_TheFirstMessageCarriesTheFailure()
    {
        var sb = new Dappsv1SessionBackhaul(new ThrowingTransport(new InvalidOperationException("kaboom")), NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Should().ContainSingle();
        batch.Outcomes[0].Id.Should().Be("msg0001");
        batch.Outcomes[0].Result.Error.Should().Be("kaboom");
        batch.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_NothingQueued_DoesNotDial()
    {
        var transport = new FakeOutboundTransport([]);
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", new ListBatch(), TestContext.Current.CancellationToken);

        transport.Connects.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_PeerBusy_DefersTheFirstMessage_AndLeavesTheRestUntouched()
    {
        var sb = new Dappsv1SessionBackhaul(new ThrowingTransport(new PeerSessionBusyException("N0DEST", "inbound")), NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Should().ContainSingle();
        batch.Outcomes[0].Result.Deferred.Should().BeTrue();
        batch.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_RevGoesOnceAfterAllThePushes()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nsend msg0001\nack msg0001\nsend msg0002\nack msg0002\nDAPPSv1>\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, new NullInbox(), () => true);

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC",
            new ListBatch(Msg("msg0001"), Msg("msg0002")), TestContext.Current.CancellationToken);

        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        // Payloads carry no newline, so "rev" follows the last one on
        // the same line: count the command, not lines.
        System.Text.RegularExpressions.Regex.Count(written, "rev\n").Should().Be(1);
        written.IndexOf("rev\n", StringComparison.Ordinal).Should().BeGreaterThan(written.IndexOf("data msg0002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendBatchAsync_PeerHangsUpAfterTheFirstMessage_TheNextIsDeferredNotFailed()
    {
        // A peer that closes after an exchange ends the session; the
        // neighbour isn't failing, so no cooldown for the next message.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\nsend msg0001\nack msg0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted, o.Result.Deferred)).Should().Equal(
            ("msg0001", true, false), ("msg0002", false, true));
    }

    [Fact]
    public async Task SendBatchAsync_TheFirstMessageOnASilentPeer_StillFails()
    {
        // Nothing has been exchanged yet, so a peer that goes away is a
        // failed send, as it always was.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Single().Result.Deferred.Should().BeFalse();
        batch.Outcomes.Single().Result.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task SendBatchAsync_LinkEndsDuringRev_DoesNotAskForMore()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\nsend msg0001\nack msg0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, new NullInbox(), () => true);
        var batch = new ListBatch(Msg("msg0001")) { SecondWave = [Msg("late001")] };

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => o.Id).Should().Equal("msg0001");
        batch.SecondWave.Should().ContainSingle("a message queued meanwhile waits for the next session, not a dead link");
    }

    private static BackhaulMessage Msg(string id) => new(id, "app@N0DEST", Salt: 1L, Ttl: 60, Payload: "x"u8.ToArray());

    /// <summary>A fixed list of messages, recording each outcome.
    /// <see cref="SecondWave"/> stands in for traffic queued while the
    /// session was open: handed out once the first list has run dry and
    /// the backhaul asks again.</summary>
    private sealed class ListBatch(params BackhaulMessage[] messages) : IBackhaulBatch
    {
        private readonly Queue<BackhaulMessage> queue = new(messages);
        private bool ranDry;

        public List<(string Id, BackhaulSendResult Result)> Outcomes { get; } = [];
        public int Remaining => queue.Count;
        public List<BackhaulMessage> SecondWave { get; init; } = [];

        public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct)
        {
            if (queue.TryDequeue(out var next)) return ValueTask.FromResult<BackhaulMessage?>(next);
            if (ranDry && SecondWave.Count > 0)
            {
                next = SecondWave[0];
                SecondWave.RemoveAt(0);
                return ValueTask.FromResult<BackhaulMessage?>(next);
            }
            ranDry = true;
            return ValueTask.FromResult<BackhaulMessage?>(null);
        }

        public ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
        {
            Outcomes.Add((message.Id, result));
            return ValueTask.CompletedTask;
        }
    }

    private static Dappsv1SessionBackhaul MakeBackhaul(byte[] cannedReceiverBytes)
        => new(new FakeOutboundTransport(cannedReceiverBytes), NullLoggerFactory.Instance);

    private sealed class FakeOutboundTransport(byte[] cannedReceiverBytes) : IDappsOutboundTransport
    {
        public byte[] WriteCapture => _stream?.WriteCapture.ToArray() ?? [];
        public int Connects { get; private set; }

        private CapturingStream? _stream;

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Connects++;
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

    private sealed class NullInbox : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingTransport(Exception toThrow) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            => Task.FromException<IDappsConnection>(toThrow);
    }
}
