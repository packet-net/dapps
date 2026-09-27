using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan F3 - server-side <c>rev</c> handler and client-side
/// <c>PollAsync</c>. Tests exercise the new wire shape directly
/// (canned bytes, capturing fakes) and the full flow against the
/// real Database. Opportunistic-poll-on-push is exercised by
/// <see cref="Dappsv1SessionBackhaulOpportunisticPollTests"/>.
/// </summary>
public sealed class F3PollClientTests
{
    [Fact]
    public async Task PollAsync_ServerEmitsPromptImmediately_NoMessagesYielded()
    {
        // Empty drain - server has nothing for caller; emits the
        // "drained" prompt straight away. Caller sees no messages.
        var stream = new FakeDuplexStream("DAPPSv1>\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var got = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var p in client.PollAsync(requestedIds: null, CancellationToken.None))
        {
            got.Add(p);
        }

        got.Should().BeEmpty();
        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().Be("rev\n");
    }

    [Fact]
    public async Task PollAsync_SelectiveIds_EmitsRevWithIdList()
    {
        var stream = new FakeDuplexStream("DAPPSv1>\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await foreach (var _ in client.PollAsync(new[] { "abc1234", "def5678" }, CancellationToken.None)) { }

        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().Be("rev abc1234 def5678\n");
    }

    [Fact]
    public async Task PollAsync_ServerSendsOneMessage_YieldsItAndAcksHashed()
    {
        // Build a server reply that offers one message and emits its
        // payload after we accept. Hash must match the per-message
        // contract for the client to ack rather than NAK.
        var payload = "hello-poll"u8.ToArray();
        long salt = 12345;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, $"ihave {id} len={payload.Length} fmt=p dst=app@N0US s={salt}\n");
        AppendUtf8(serverBytes, $"data {id}\n");
        serverBytes.Write(payload);
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        var stream = new FakeDuplexStream(serverBytes.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var got = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var p in client.PollAsync(null, CancellationToken.None))
        {
            got.Add(p);
        }

        got.Should().ContainSingle();
        got[0].Id.Should().Be(id);
        got[0].Destination.Should().Be("app@N0US");
        got[0].Payload.Should().Equal(payload);

        // Wire on the way back: rev, send <id>, ack <id>.
        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Contain("rev\n");
        written.Should().Contain($"send {id}\n");
        written.Should().Contain($"ack {id}\n");
    }

    [Fact]
    public async Task PollAsync_HashMismatch_NaksAndSkipsMessage()
    {
        // Server claims id X but the bytes hash to something else.
        // Client must NAK with `bad <id>` and not yield the corrupt
        // payload. Server then sends prompt to end.
        var payload = "tampered"u8.ToArray();
        var lyingId = "bad1234";   // not the real hash
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, $"ihave {lyingId} len={payload.Length} fmt=p dst=app@N0US\n");
        AppendUtf8(serverBytes, $"data {lyingId}\n");
        serverBytes.Write(payload);
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        var stream = new FakeDuplexStream(serverBytes.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var got = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var p in client.PollAsync(null, CancellationToken.None)) got.Add(p);

        got.Should().BeEmpty("hash mismatch must not yield to the caller");
        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Contain($"bad {lyingId}");
    }

    [Fact]
    public async Task PollAsync_F2Fragment_PreservesMidAndFragHeaders()
    {
        // Plan F3 ↔ F2 interplay: a fragment is just a regular message
        // on the wire with mid= + frag=N/M. The poll loop must
        // surface those onto PolledMessage so the caller can route to
        // the reassembly buffer rather than direct delivery.
        var chunk = "frag3-payload"u8.ToArray();
        long salt = 999;
        var fragId = DappsMessage.ComputeHash(chunk, salt)[..7];
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes,
            $"ihave {fragId} len={chunk.Length} fmt=p dst=app@N0US s={salt} mid=master12 frag=3/5\n");
        AppendUtf8(serverBytes, $"data {fragId}\n");
        serverBytes.Write(chunk);
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        var stream = new FakeDuplexStream(serverBytes.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var got = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var p in client.PollAsync(null, CancellationToken.None)) got.Add(p);

        got.Should().ContainSingle();
        got[0].MasterId.Should().Be("master12");
        got[0].FragmentIndex.Should().Be(3);
        got[0].FragmentTotal.Should().Be(5);
    }

    private static void AppendUtf8(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>
/// Server-side rev handler. Spins up a real Database, populates the
/// outbound queue, drives an InboundConnectionHandler with canned
/// "rev\n" + the client's downstream send/ack, asserts the wire
/// shape coming back.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class F3PollServerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-f3-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
        }
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
        });
        database = new Database(NullLogger<Database>.Instance, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetMessagesForCaller_ReturnsOnlyMatchingDestination()
    {
        // Three messages: one for caller's apps, one for elsewhere,
        // one already forwarded. Only the caller's un-forwarded
        // message should come back.
        await database.SaveMessage("aaa1111", "1"u8.ToArray(), null, "app@N0THEM", "src", "{}", null);
        await database.SaveMessage("bbb2222", "2"u8.ToArray(), null, "app@N0OTHER", "src", "{}", null);
        await database.SaveMessage("ccc3333", "3"u8.ToArray(), null, "app2@N0THEM-9", "src", "{}", null);

        var rows = await database.GetMessagesForCaller("N0THEM", Array.Empty<string>());
        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { "aaa1111", "ccc3333" });
    }

    [Fact]
    public async Task GetMessagesForCaller_SelectiveIds_NarrowsToSubset()
    {
        await database.SaveMessage("aaa1111", "1"u8.ToArray(), null, "app@N0THEM", "src", "{}", null);
        await database.SaveMessage("bbb2222", "2"u8.ToArray(), null, "app2@N0THEM-9", "src", "{}", null);
        await database.SaveMessage("ccc3333", "3"u8.ToArray(), null, "app3@N0THEM", "src", "{}", null);

        var rows = await database.GetMessagesForCaller("N0THEM", new[] { "aaa1111", "ccc3333" });
        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { "aaa1111", "ccc3333" });
    }

    [Fact]
    public async Task GetMessagesForCaller_ExcludesAlreadyForwarded()
    {
        // Mark a message as forwarded; should not appear in the rev
        // drain candidate list.
        await database.SaveMessage("aaa1111", "1"u8.ToArray(), null, "app@N0THEM", "src", "{}", null);
        await database.MarkMessageAsForwarded("aaa1111");

        var rows = await database.GetMessagesForCaller("N0THEM", Array.Empty<string>());
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RevHandler_NoQueuedMail_EmitsOnlyPromptBack()
    {
        // Caller types `rev`; we have nothing. Server should emit just
        // `DAPPSv1>` after the initial banner - no ihave lines, then
        // `bye` after `q`.
        var output = await DriveSession("rev\nq\n");
        output.Should().StartWith("DAPPSv1>\n");
        // No `ihave ` lines - nothing to drain.
        output.Should().NotContain("ihave ");
        // Drained prompt + the `q` terminating bye.
        output.Should().Contain("DAPPSv1>\n");
        output.Should().Contain("bye\n");
    }

    [Fact]
    public async Task RevHandler_OneQueuedMessage_EmitsIhaveDataMarksForwarded()
    {
        // Pre-load one queued message destined for the caller.
        var payload = "queued-mail"u8.ToArray();
        long salt = 7777;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];
        await database.SaveMessage(id, payload, salt, "app@N0THEM", "originator", "{}", ttl: 600,
            originatorCallsign: "N0OUTSIDE");

        // Drive the session: client sends `rev\n`, then `send <id>\n`,
        // then `ack <id>\n`, then `q\n` to quit.
        var output = await DriveSession($"rev\nsend {id}\nack {id}\nq\n");

        output.Should().Contain($"ihave {id}");
        output.Should().Contain("dst=app@N0THEM");
        output.Should().Contain($"data {id}");
        // Server payload bytes should appear right after `data <id>\n`.
        output.IndexOf($"data {id}\n").Should().BePositive();

        // Message marked as forwarded.
        (await database.GetMessagesForCaller("N0THEM", Array.Empty<string>())).Should().BeEmpty(
            "successful drain marks the message forwarded so it doesn't get re-offered");
    }

    /// <summary>Drive an inbound session with the given client-side
    /// command stream; return what the server wrote back.</summary>
    private async Task<string> DriveSession(string clientInput)
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes(clientInput));
        var inbox = new RecordingInbox();
        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: "N0THEM-9",
            NullLoggerFactory.Instance, database, inbox);
        await handler.Handle(CancellationToken.None);
        return Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        public List<BackhaulMessage> Delivered { get; } = new();
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Plan F3 opportunistic-poll-on-push end-to-end. Drives
/// <see cref="Dappsv1SessionBackhaul"/> against a fake transport whose
/// canned receiver bytes simulate the remote's full session: prompt,
/// send/ack of our pushed message, then a rev-drained reply, then
/// drained prompt. Asserts the inbox sees the polled message.
/// </summary>
public sealed class Dappsv1SessionBackhaulOpportunisticPollTests
{
    [Fact]
    public async Task SendAsync_OpportunisticPollEnabled_DrainsRemoteQueue()
    {
        // Construct the canned remote-side reply:
        // prompt → send/ack of our push → drained-prompt for opportunistic rev
        // with one ihave/data/ack exchange in between, then drained prompt.
        var pushedId = "push001";
        var polledPayload = "polled-mail"u8.ToArray();
        long polledSalt = 4242;
        var polledId = DappsMessage.ComputeHash(polledPayload, polledSalt)[..7];

        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        AppendUtf8(serverBytes, $"send {pushedId}\n");
        AppendUtf8(serverBytes, $"ack {pushedId}\n");
        // Opportunistic rev: server emits one message, then drained
        // prompt to signal we're done.
        AppendUtf8(serverBytes, $"ihave {polledId} len={polledPayload.Length} fmt=p dst=app@N0US s={polledSalt}\n");
        AppendUtf8(serverBytes, $"data {polledId}\n");
        serverBytes.Write(polledPayload);
        AppendUtf8(serverBytes, "DAPPSv1>\n");

        var transport = new CapturingTransport(serverBytes.ToArray());
        var inbox = new RecordingInbox();
        var sb = new Dappsv1SessionBackhaul(
            transport, NullLoggerFactory.Instance,
            opportunisticInbox: inbox,
            opportunisticEnabled: () => true);

        var result = await sb.SendAsync(
            new BackhaulMessage(pushedId, "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST", BearerPort: 0),
            "N0US",
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        inbox.Delivered.Should().ContainSingle();
        inbox.Delivered[0].Id.Should().Be(polledId);
        inbox.Delivered[0].Payload.Should().Equal(polledPayload);
        // Wire write should include the rev call.
        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        written.Should().Contain($"ihave {pushedId}");   // the original push
        written.Should().Contain("rev\n");                // the opportunistic poll
        written.Should().Contain($"ack {polledId}");      // the ack of the polled message
    }

    [Fact]
    public async Task SendAsync_OpportunisticPollDisabled_NoRevSent()
    {
        // Same shape, but opportunistic toggle is off. Server only
        // gets to the prompt + send/ack of the push; rev never fires.
        var pushedId = "push002";
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        AppendUtf8(serverBytes, $"send {pushedId}\n");
        AppendUtf8(serverBytes, $"ack {pushedId}\n");

        var transport = new CapturingTransport(serverBytes.ToArray());
        var inbox = new RecordingInbox();
        var sb = new Dappsv1SessionBackhaul(
            transport, NullLoggerFactory.Instance,
            opportunisticInbox: inbox,
            opportunisticEnabled: () => false);

        var result = await sb.SendAsync(
            new BackhaulMessage(pushedId, "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST", BearerPort: 0),
            "N0US",
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        inbox.Delivered.Should().BeEmpty();
        Encoding.UTF8.GetString(transport.WriteCapture).Should().NotContain("rev\n");
    }

    [Fact]
    public async Task SendAsync_NoOpportunisticInbox_NoRevSent()
    {
        // Default constructor (no inbox supplied) - opportunistic
        // poll is silently a no-op. This is the shape we use in
        // existing tests / harnesses that don't care about pull.
        var pushedId = "push003";
        var serverBytes = new MemoryStream();
        AppendUtf8(serverBytes, "DAPPSv1>\n");
        AppendUtf8(serverBytes, $"send {pushedId}\n");
        AppendUtf8(serverBytes, $"ack {pushedId}\n");

        var transport = new CapturingTransport(serverBytes.ToArray());
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage(pushedId, "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST", BearerPort: 0),
            "N0US",
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        Encoding.UTF8.GetString(transport.WriteCapture).Should().NotContain("rev\n");
    }

    private static void AppendUtf8(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes, 0, bytes.Length);
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        public List<BackhaulMessage> Delivered { get; } = new();
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingTransport(byte[] cannedReceiverBytes) : IDappsOutboundTransport
    {
        private CapturingStream? _stream;
        public byte[] WriteCapture => _stream?.WriteCapture.ToArray() ?? [];

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            _stream = new CapturingStream(cannedReceiverBytes);
            return Task.FromResult<IDappsConnection>(new FakeConnection(_stream));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
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
}

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
