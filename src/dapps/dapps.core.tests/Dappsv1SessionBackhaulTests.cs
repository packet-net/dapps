using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for the AGW-stream backhaul implementation. The
/// end-to-end <c>DappsEndToEndTests</c> exercise this against real BPQ
/// over AXIP-UDP, but those need Docker. These drive the session
/// directly with a fake transport that hands back canned receiver bytes
/// - fast, hermetic, covers each rejection path explicitly. The canned
/// answers are all there from the start; the session reads a line at a
/// time and sends as soon as it can, so they land on what it sent.
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

    /// <summary>What the answering node says first: the prompt and its rules.</summary>
    private const string Hello = "DAPPSv1>\nexchange id=far001 hold=0 inline=256 z=1\n";

    [Fact]
    public async Task SendAsync_HappyPath_ReturnsOkAndWritesTheMessageAfterOurRules()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(Hello + "ack mid0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0001", "app@N0DEST", Salt: 1L, Ttl: 60, Payload: "hi"u8.ToArray()),
            new BackhaulRoute("N0DEST", BearerPort: 1),
            "N0SRC",
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        written.Should().StartWith("exchange id=");
        written.Should().Contain("msg mid0001 ");
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
        result.Deferred.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task SendAsync_ANodeBannerAndNoPrompt_FailsWhenTheWaitRunsOut_WithoutSendingOurRules()
    {
        // Something that isn't DAPPS answered (a node's welcome text):
        // unlike a crossed call, the link isn't silent.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("Welcome to BPQ Node PEWSEY\rType ? for help.\r"), endOfStream: false);
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance) { PromptWait = TimeSpan.FromMilliseconds(200) };

        var result = await sb.SendAsync(
            new BackhaulMessage("mid0002", "app@N0DEST", null, null, "x"u8.ToArray()),
            new BackhaulRoute("N0DEST"), "N0SRC", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
        Encoding.UTF8.GetString(transport.WriteCapture).Should().BeEmpty();
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
            Hello + "ack msg0001\nack msg0002\nack msg0003\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"), Msg("msg0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        transport.Connects.Should().Be(1);
        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted)).Should().Equal(
            ("msg0001", true), ("msg0002", true), ("msg0003", true));
        var written = Encoding.UTF8.GetString(transport.WriteCapture);
        written.Should().Contain("msg msg0001").And.Contain("msg msg0002").And.Contain("msg msg0003");
    }

    [Fact]
    public async Task SendBatchAsync_OneMessageFailing_DoesNotStopTheOthers()
    {
        // Answers are per message, so the session stays in step after one
        // is turned down: unlike the old one-at-a-time exchange, it goes on.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            Hello + "ack msg0001\nerror msg0002\nerror msg0002\nack msg0003\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"), Msg("msg0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted)).Should().Equal(("msg0001", true), ("msg0002", false), ("msg0003", true));
        batch.Outcomes[1].Result.Error.Should().Contain("offer rejected");
        System.Text.RegularExpressions.Regex.Count(Encoding.UTF8.GetString(transport.WriteCapture), "msg msg0002 ")
            .Should().Be(2, "one more go after the first error, then it fails for this session");
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
    public async Task SendBatchAsync_ThePeerHangsUpWithMessagesUnanswered_TheOldestFails_TheRestAreDeferred()
    {
        // A session that breaks off without a quit is a failure of the
        // link or the neighbour: one message fails, so the neighbour gets
        // a cooldown instead of an immediate redial; the others just wait.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(Hello + "ack msg0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"), Msg("msg0002"), Msg("msg0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted, o.Result.Deferred)).Should().Equal(
            ("msg0001", true, false), ("msg0002", false, false), ("msg0003", false, true));
    }

    [Fact]
    public async Task SendBatchAsync_APeerThatHangsUpBeforeItsRules_FailsTheFirstMessage()
    {
        // Nothing has been exchanged, so a peer that goes away is a failed
        // send, as it always was.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var batch = new ListBatch(Msg("msg0001"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Single().Result.Deferred.Should().BeFalse();
        batch.Outcomes.Single().Result.Accepted.Should().BeFalse();
        batch.Outcomes.Single().Result.Error.Should().Contain("hung up");
    }

    [Fact]
    public async Task SendBatchAsync_OnceTheBatchHasRunDry_ItIsNeverAskedAgain()
    {
        // The forwarder's run moves on once its batch is done; traffic
        // queued after that reaches the session by hand-off, not by the
        // session going back to a batch the run has finished with.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(Hello + "ack msg0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: new NullInbox());
        var batch = new ListBatch(Msg("msg0001")) { SecondWave = [Msg("late001")] };

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Select(o => o.Id).Should().Equal("msg0001");
        batch.SecondWave.Should().ContainSingle();
    }

    [Fact]
    public async Task SendBatchAsync_ASessionThatAnswersNothing_DoesNotHoldTheForwarderPastItsPatience()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new OneStreamTransport(ours), NullLoggerFactory.Instance)
        {
            BatchPatience = TimeSpan.FromMilliseconds(300),
        };
        var message = ExchangeTestKit.Message("slow to answer", "app@N0DEST");
        var batch = new RecordingBatch(message);

        var send = sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=60 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);

        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);
        batch.Outcomes.Should().BeEmpty("the answer hasn't come yet");

        // It still counts when it does.
        await peer.WriteLineAsync($"ack {message.Id}", ct);
        await batch.WaitForOutcomesAsync(1, ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_WaitsForTheAnswer_HoweverTheFirstHandOutIsTimed()
    {
        // The wait for answers starts when the first message is handed to
        // the session, and measures its patience from when that happened.
        // It once woke between the two, read "last progress" as the year 1
        // and moved on at once: the forwarder's run returned before the
        // message had even gone. A clock that's slow to answer holds that
        // gap open.
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new SingleConnectionTransport(new RetirableConnection(ours)), NullLoggerFactory.Instance)
        {
            TimeProvider = new SlowClock(TimeSpan.FromMilliseconds(20)),
        };
        var message = ExchangeTestKit.Message("waiting for its ack", "app@N0DEST");
        var batch = new RecordingBatch(message);

        var send = sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=0 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);
        await Task.Delay(300, ct);

        send.IsCompleted.Should().BeFalse("its message hasn't been answered, and 60 s haven't passed");
        await peer.WriteLineAsync($"ack {message.Id}", ct);
        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
    }

    /// <summary>The system clock, taking a while to answer.</summary>
    private sealed class SlowClock(TimeSpan lag) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            Thread.Sleep(lag);
            return base.GetUtcNow();
        }
    }

    [Fact]
    public async Task ASessionRetiredForANewerOne_Stops_DefersItsWork_AndDropsTheLinkWithoutADisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var connection = new RetirableConnection(ours);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new SingleConnectionTransport(connection), NullLoggerFactory.Instance);
        var message = ExchangeTestKit.Message("waiting for its ack", "app@N0DEST");
        var batch = new RecordingBatch(message);

        var send = sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=60 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);

        connection.Retire();
        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue("it goes on the newer session, not counted as a failure");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!connection.Abandoned && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        connection.Abandoned.Should().BeTrue();
        connection.Disposed.Should().BeFalse("a disconnect would take the link from the newer session");
        sb.OpenPeers.Should().BeEmpty();
    }

    [Fact]
    public async Task ARetirement_WithHandedOnWorkInFlight_DefersAllOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var connection = new RetirableConnection(ours);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new SingleConnectionTransport(connection), NullLoggerFactory.Instance);
        var route = new BackhaulRoute("N0DEST");
        var first = ExchangeTestKit.Message("the one that dialled", "app@N0DEST");
        var batch = new RecordingBatch(first);
        var send = sb.SendBatchAsync(route, "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=60 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);
        await peer.WriteLineAsync($"ack {first.Id}", ct);
        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var more = new RecordingBatch(
            ExchangeTestKit.Message("handed on, one", "app@N0DEST"),
            ExchangeTestKit.Message("handed on, two", "app@N0DEST"));
        sb.TryHandToOpenSession(route, more).Should().BeTrue("the session is still open, on its hold");
        await peer.ReadWithPayloadAsync(ct);
        await peer.ReadWithPayloadAsync(ct);
        connection.Retire();

        await more.WaitForOutcomesAsync(2, ct);
        more.Outcomes.Should().AllSatisfy(o => o.Result.Deferred.Should().BeTrue("they go on the newer session"));
        await WaitForAsync(() => connection.Abandoned, ct);
        connection.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task ARetirement_WhileQuitting_DropsTheLinkWithoutADisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var connection = new RetirableConnection(ours);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new SingleConnectionTransport(connection), NullLoggerFactory.Instance)
        {
            MinQuiet = TimeSpan.FromMilliseconds(200),
        };
        var message = ExchangeTestKit.Message("answered before the quit", "app@N0DEST");
        var batch = new RecordingBatch(message);
        var send = sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=0 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);
        await peer.WriteLineAsync($"ack {message.Id}", ct);
        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);
        (await peer.ReadLineAsync(ct)).Should().Be("quit");

        connection.Retire();

        await WaitForAsync(() => connection.Abandoned, ct);
        connection.Disposed.Should().BeFalse();
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARetirementRacingThePeerHangingUp_SettlesTheMessageOnce_AndClosesTheLinkOnce(bool retiredFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var connection = new RetirableConnection(ours);
        var peer = new LinePeer(theirs);
        var sb = new Dappsv1SessionBackhaul(new SingleConnectionTransport(connection), NullLoggerFactory.Instance);
        var message = ExchangeTestKit.Message("in flight", "app@N0DEST");
        var batch = new RecordingBatch(message);
        var send = sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=60 inline=256", ct);
        await peer.ReadLineAsync(ct);
        await peer.ReadWithPayloadAsync(ct);

        if (retiredFirst)
        {
            connection.Retire();
            peer.Close();
        }
        else
        {
            peer.Close();
            connection.Retire();
        }

        await send.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitForAsync(() => connection.Abandoned || connection.Disposed, ct);
        await Task.Delay(200, ct);
        batch.Outcomes.Should().ContainSingle();
        (connection.Abandoned && connection.Disposed).Should().BeFalse("the link is closed once");
        if (retiredFirst)
        {
            batch.Outcomes.Single().Result.Deferred.Should().BeTrue();
            connection.Abandoned.Should().BeTrue();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        condition().Should().BeTrue();
    }

    private sealed class RetirableConnection(Stream stream) : IDappsConnection
    {
        private readonly CancellationTokenSource retired = new();
        public Stream Stream => stream;
        public CancellationToken Retired => retired.Token;
        public bool Abandoned { get; private set; }
        public bool Disposed { get; private set; }
        public void Retire() => retired.Cancel();

        public ValueTask AbandonAsync()
        {
            Abandoned = true;
            stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleConnectionTransport(IDappsConnection connection) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken) =>
            Task.FromResult(connection);
    }

    [Fact]
    public async Task APeerWithoutTheDictionary_RefusesTheCompressedMessage_AndGetsItPlainOnTheSameSession()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""");
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            Hello + "error wps0001\nack wps0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: null,
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(Compress: true)));

        var result = await sb.SendAsync(
            new BackhaulMessage("wps0001", "app@N0DEST", Salt: 1L, Ttl: 60, Payload: payload),
            new BackhaulRoute("N0DEST"), "N0SRC", TestContext.Current.CancellationToken);

        result.Accepted.Should().BeTrue();
        transport.Connects.Should().Be(1);
        var written = Encoding.Latin1.GetString(transport.WriteCapture);
        written.IndexOf(" fmt=z1 ", StringComparison.Ordinal).Should().BeLessThan(
            written.IndexOf(" fmt=p ", StringComparison.Ordinal), "compressed first, then plain after the refusal");
        written.Should().EndWith(Encoding.UTF8.GetString(payload), "what went after the refusal is the readable payload");
    }

    [Fact]
    public async Task APeerThatCantDecodeTheCompressedPayload_GetsItPlainOnTheSameSession()
    {
        // e.g. a hop on a connect-script path that mangles binary bytes.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            Hello + "bad wps0001\nack wps0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: null,
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(Compress: true)));

        var result = await sb.SendAsync(WpsMsg("wps0001"), new BackhaulRoute("N0DEST"), "N0SRC", TestContext.Current.CancellationToken);

        result.Accepted.Should().BeTrue();
        var written = Encoding.Latin1.GetString(transport.WriteCapture);
        written.IndexOf(" fmt=z1 ", StringComparison.Ordinal).Should().BeLessThan(written.IndexOf(" fmt=p ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnceThePeerRefusesCompression_TheRestOfTheSessionGoesPlain()
    {
        // All three go compressed in the first write; once the first is
        // refused, whatever goes again goes plain.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            Hello + "error wps0001\nerror wps0002\nack wps0001\nack wps0002\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: null,
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(Compress: true)));
        var batch = new ListBatch(WpsMsg("wps0001"), WpsMsg("wps0002"), WpsMsg("wps0003"));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", batch, TestContext.Current.CancellationToken);

        batch.Outcomes.Where(o => o.Id != "wps0003").Should().AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
        var written = Encoding.Latin1.GetString(transport.WriteCapture);
        System.Text.RegularExpressions.Regex.Count(written, "msg wps0002 len=[0-9]+ fmt=z1").Should().Be(1);
        System.Text.RegularExpressions.Regex.Count(written, "msg wps0002 len=[0-9]+ fmt=p").Should().Be(1,
            "its second go is plain, like the rest of the session");
    }

    private static BackhaulMessage WpsMsg(string id) => new(id, "app@N0DEST", Salt: 1L, Ttl: 60, Payload: Encoding.UTF8.GetBytes(
        """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}"""));

    [Fact]
    public async Task AnOpenLinkWhoseReadFails_Closes_RatherThanSpinning()
    {
        // The session is established and held, then the link read fails
        // (the AGW socket to the node dropping, say). It must end, not loop.
        var transport = new FailingAfterScriptTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nexchange id=far001 hold=60 inline=256\nack msg0001\n"));
        var sb = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance, inbox: new NullInbox(),
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(HoldSeconds: 60)));

        await sb.SendBatchAsync(new BackhaulRoute("N0DEST"), "N0SRC", new ListBatch(Msg("msg0001")), TestContext.Current.CancellationToken);
        sb.OpenPeers.Should().Contain("N0DEST", "both ends hold, so the link stays up");
        transport.ReleaseFailure();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sb.OpenPeers.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        sb.OpenPeers.Should().BeEmpty();
        transport.Disposed.Should().BeTrue("the session hung up");
    }

    /// <summary>Replies from a script, then fails the next read once
    /// the test says so.</summary>
    private sealed class FailingAfterScriptTransport(byte[] script) : IDappsOutboundTransport
    {
        private readonly TaskCompletionSource fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public void ReleaseFailure() => fail.TrySetResult();

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken) =>
            Task.FromResult<IDappsConnection>(new Connection(this, new ScriptThenFail(script, fail.Task)));

        private sealed class Connection(FailingAfterScriptTransport owner, Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                owner.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class ScriptThenFail(byte[] script, Task failWhen) : Stream
        {
            private readonly MemoryStream reads = new(script);

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var n = await reads.ReadAsync(buffer, ct);
                if (n > 0) return n;
                await failWhen.WaitAsync(ct);
                throw new IOException("AGW session disconnected");
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
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

    /// <summary>Replies with canned bytes. At the end of them the link
    /// closes, or with <paramref name="endOfStream"/> false stays open
    /// and silent.</summary>
    private sealed class FakeOutboundTransport(byte[] cannedReceiverBytes, bool endOfStream = true) : IDappsOutboundTransport
    {
        public byte[] WriteCapture => _stream?.WriteCapture.ToArray() ?? [];
        public int Connects { get; private set; }

        private CapturingStream? _stream;

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Connects++;
            _stream = new CapturingStream(cannedReceiverBytes, endOfStream);
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

        private sealed class CapturingStream(byte[] preloaded, bool endOfStream) : Stream
        {
            private readonly MemoryStream _read = new(preloaded);
            public MemoryStream WriteCapture { get; } = new();

            public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var n = await _read.ReadAsync(buffer, ct);
                if (n > 0 || endOfStream) return n;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            }
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
