using System.Text;
using dapps.client;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

public class DappsProtocolClientTests
{
    [Fact]
    public async Task SendMessageAsync_WritesTheDataLineAndPayloadInOneWrite()
    {
        // One write is one AGW data frame, so the line and a small
        // payload travel in one I-frame instead of two.
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("ack abc1234\n"));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.SendMessageAsync("abc1234", "hello"u8.ToArray(), TestContext.Current.CancellationToken)).Should().BeTrue();

        stream.Writes.Should().ContainSingle();
        Encoding.UTF8.GetString(stream.Writes.Single()).Should().Be("data abc1234\nhello");
    }

    [Fact]
    public async Task PollAsync_AFormatItCantRead_IsDeclined_AndThePlainRetryIsTaken()
    {
        var payload = "hello"u8.ToArray();
        var id = DappsMessage.ComputeHash(payload, 1L)[..7];
        var stream = new FakeDuplexStream([
            .. Encoding.UTF8.GetBytes($"ihave {id} len=5 fmt=z9 clen=3 dst=app@N0CALL s=1\n"),
            .. Encoding.UTF8.GetBytes($"ihave {id} len=5 fmt=p dst=app@N0CALL s=1\n"),
            .. Encoding.UTF8.GetBytes($"data {id}\n"), .. payload,
            .. Encoding.UTF8.GetBytes("DAPPSv1>\n"),
        ]);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var polled = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var m in client.PollAsync(null, TestContext.Current.CancellationToken)) polled.Add(m);

        polled.Should().ContainSingle().Which.Payload.Should().Equal(payload);
        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().Be($"rev\nno {id}\nsend {id}\nack {id}\n");
    }

    [Fact]
    public async Task PollAsync_ACompressedPayload_IsDecodedBeforeTheHashCheck()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""");
        var id = DappsMessage.ComputeHash(payload, 1L)[..7];
        var wire = dapps.client.Compression.PayloadCompression.TryCompress(payload)!.Value;
        var stream = new FakeDuplexStream([
            .. Encoding.UTF8.GetBytes($"ihave {id} len={payload.Length} fmt={wire.Format} clen={wire.Bytes.Length} dst=app@N0CALL s=1\n"),
            .. Encoding.UTF8.GetBytes($"data {id}\n"), .. wire.Bytes,
            .. Encoding.UTF8.GetBytes("DAPPSv1>\n"),
        ]);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var polled = new List<DappsProtocolClient.PolledMessage>();
        await foreach (var m in client.PollAsync(null, TestContext.Current.CancellationToken)) polled.Add(m);

        polled.Should().ContainSingle().Which.Payload.Should().Equal(payload);
        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().EndWith($"ack {id}\n");
    }

    [Fact]
    public async Task APendingNotice_InPlaceOfAReply_IsSkippedAndRemembered()
    {
        // A peer holding the session sends `pending` when it's idle, so it
        // can cross with our offer and arrive before the reply.
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("pending\nsend abc1234\n"));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.OfferMessageAsync("abc1234", 1L, DappsMessage.MessageFormat.Plain, "app@N0DEST", 5,
            TestContext.Current.CancellationToken)).Should().BeTrue();
        client.PeerHasPending.Should().BeTrue();
    }

    [Theory]
    [InlineData("tail 30\n", 30)]
    [InlineData("tail 0\n", 0)]
    [InlineData("pending\ntail 45\n", 45)]
    [InlineData("eh?\n", null)]
    public async Task RequestTailAsync_ReadsWhatThePeerAgreed(string reply, int? expected)
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes(reply));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.RequestTailAsync(120, TestContext.Current.CancellationToken)).Should().Be(expected);
        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().Be("tail 120\n");
    }

    [Fact]
    public async Task ReadInitialPromptAsync_ReturnsTrueWhenPromptArrives()
    {
        var canned = Encoding.UTF8.GetBytes("DAPPSv1>\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.ReadInitialPromptAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadInitialPromptAsync_TolersaesLeadingNoise()
    {
        // Some nodes emit banner text before the DAPPS prompt
        var canned = Encoding.UTF8.GetBytes("*** Connected to DAPPS\rDAPPSv1>\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.ReadInitialPromptAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadInitialPromptAsync_ReturnsFalseOnEofBeforePrompt()
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("hello"));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        (await client.ReadInitialPromptAsync(CancellationToken.None)).Should().BeFalse();
    }

    // #178: a crossed connect looks like total silence after the link
    // came up. The caller needs to tell that apart from a peer that said
    // something other than the prompt (a node banner, say), which is a
    // different problem with a different answer.

    [Fact]
    public async Task ReadInitialPromptAsync_NothingAtAllWithinTheSilenceBudget_IsSilent()
    {
        var client = new DappsProtocolClient(new NeverReadyStream(), NullLoggerFactory.Instance);
        var outcome = await client.ReadInitialPromptAsync(TestContext.Current.CancellationToken, silenceBudget: TimeSpan.FromMilliseconds(100));
        outcome.Should().Be(DappsProtocolClient.PromptOutcome.Silent);
    }

    [Fact]
    public async Task ReadInitialPromptAsync_ANodeBannerInsteadOfThePrompt_IsNotSeen()
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("Welcome to BPQ Node PEWSEY in IO91BH.\rType ? for help.\r"));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);
        var outcome = await client.ReadInitialPromptAsync(TestContext.Current.CancellationToken, silenceBudget: TimeSpan.FromMilliseconds(100));
        outcome.Should().Be(DappsProtocolClient.PromptOutcome.NotSeen);
    }

    [Fact]
    public async Task ReadInitialPromptAsync_ThePrompt_IsSeen()
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);
        var outcome = await client.ReadInitialPromptAsync(TestContext.Current.CancellationToken, silenceBudget: TimeSpan.FromMilliseconds(100));
        outcome.Should().Be(DappsProtocolClient.PromptOutcome.Seen);
    }

    /// <summary>A socket whose peer never sends anything: every read waits until cancelled.</summary>
    private sealed class NeverReadyStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task OfferMessageAsync_WritesIhaveLineAndAcceptsSendReply()
    {
        var canned = Encoding.UTF8.GetBytes("send abcdeff\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var ok = await client.OfferMessageAsync(
            id: "abcdeff",
            salt: 12345678L,
            format: DappsMessage.MessageFormat.Plain,
            destination: "appname@gb7aaa-4",
            length: 11,
            ct: CancellationToken.None);

        ok.Should().BeTrue();

        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Be("ihave abcdeff len=11 fmt=p dst=appname@gb7aaa-4 s=12345678\n");
    }

    [Fact]
    public async Task OfferMessageAsync_OmitsSaltWhenNotProvided()
    {
        var canned = Encoding.UTF8.GetBytes("send abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await client.OfferMessageAsync(
            id: "abc",
            salt: null,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 5,
            ct: CancellationToken.None);

        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Be("ihave abc len=5 fmt=p dst=x@y\n");
    }

    [Fact]
    public async Task OfferMessageAsync_IncludesTtlWhenProvided()
    {
        var canned = Encoding.UTF8.GetBytes("send abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await client.OfferMessageAsync(
            id: "abc",
            salt: 42L,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 5,
            ct: CancellationToken.None,
            ttl: 600);

        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Be("ihave abc len=5 fmt=p dst=x@y s=42 ttl=600\n");
    }

    [Fact]
    public async Task OfferMessageAsync_F2Fragment_EmitsMidAndFragHeaders()
    {
        // Plan F2 - when the caller passes masterId + fragmentIndex +
        // fragmentTotal, the wire form gets `mid=…` and `frag=N/M`
        // appended after src= (or after ttl= when src is absent).
        var canned = Encoding.UTF8.GetBytes("send abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await client.OfferMessageAsync(
            id: "abc",
            salt: 42L,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 100,
            ct: CancellationToken.None,
            masterId: "def5678",
            fragmentIndex: 2,
            fragmentTotal: 5);

        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Be("ihave abc len=100 fmt=p dst=x@y s=42 mid=def5678 frag=2/5\n");
    }

    [Fact]
    public async Task OfferMessageAsync_PartialFragmentParams_Throws()
    {
        // mid set without fragment index/total → reject before hitting
        // the wire. The receiver-side validator would also reject,
        // but catching it here keeps malformed lines off the link.
        var stream = new FakeDuplexStream([]);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var act = async () => await client.OfferMessageAsync(
            id: "abc",
            salt: null,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 5,
            ct: CancellationToken.None,
            masterId: "def5678" /* no fragmentIndex/Total */);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*all be set together*");
    }

    [Fact]
    public async Task OfferMessageAsync_ReturnsFalseWhenReplyIsntSend()
    {
        var canned = Encoding.UTF8.GetBytes("error abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var ok = await client.OfferMessageAsync(
            id: "abc",
            salt: null,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 5,
            ct: CancellationToken.None);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task OfferMessageAsync_DeflateFormat_NotImplementedYet()
    {
        var stream = new FakeDuplexStream([]);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var act = async () => await client.OfferMessageAsync(
            id: "abc",
            salt: null,
            format: DappsMessage.MessageFormat.Deflate,
            destination: "x@y",
            length: 5,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task SendMessageAsync_WritesDataAndPayloadThenAccepts()
    {
        var canned = Encoding.UTF8.GetBytes("ack abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var payload = Encoding.UTF8.GetBytes("Hello world");
        var ok = await client.SendMessageAsync("abc", payload, CancellationToken.None);

        ok.Should().BeTrue();

        var written = stream.WriteCapture.ToArray();
        var expectedPrefix = Encoding.UTF8.GetBytes("data abc\n");
        written.AsSpan(0, expectedPrefix.Length).ToArray().Should().Equal(expectedPrefix);
        written.AsSpan(expectedPrefix.Length).ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsFalseOnBad()
    {
        var canned = Encoding.UTF8.GetBytes("bad abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var ok = await client.SendMessageAsync("abc", [1, 2, 3], CancellationToken.None);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsFalseOnUnexpectedReply()
    {
        var canned = Encoding.UTF8.GetBytes("garbage\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var ok = await client.SendMessageAsync("abc", [1, 2, 3], CancellationToken.None);

        ok.Should().BeFalse();
    }
}
