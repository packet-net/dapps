using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Opt-in message ordering: end-to-end coverage of the
/// <c>sid=</c>/<c>sn=</c>/<c>gt=</c> protocol additions and the
/// receiver-side reorder buffer + gap sweeper.
///
/// Wire-level tests round-trip the new fields through both the binary
/// codec and the IHave text validator. Inbox tests drive
/// <see cref="DatabaseAndMqttInbox.DeliverAsync"/> with synthetic
/// out-of-order deliveries against a real MQTT broker so we can
/// assert the actual delivery order observed by an app.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class StreamOrderingTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private FakeTimeProvider clock = null!;
    private Database database = null!;
    private MqttServer mqttServer = null!;
    private MqttBrokerService broker = null!;
    private DatabaseAndMqttInbox inbox = null!;

    public async ValueTask InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-stream-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbStreamSendState>();
            c.CreateTable<DbStreamRecvState>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
        }

        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0SELF",
            MqttPort = brokerPort,
            FragmentThresholdBytes = 0,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor, clock);
        var tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(brokerPort).Build());
        await mqttServer.StartAsync();
        broker = new MqttBrokerService(NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens, mqttServer);
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        inbox = new DatabaseAndMqttInbox(database, broker, new InboundEventBus(),
            optionsMonitor, routingAlgorithm, routingContext, clock,
            NullLogger<DatabaseAndMqttInbox>.Instance);

        await broker.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await broker.StopAsync(CancellationToken.None);
        await mqttServer.StopAsync();
        mqttServer.Dispose();
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }

    // ── Wire format: codec ─────────────────────────────────────────

    [Fact]
    public void Codec_RoundTrip_StreamFields_PreservesAllThree()
    {
        var input = new BackhaulMessage(
            Id: "stream01",
            Destination: "chat@N0DEST",
            Salt: 1L,
            Ttl: 600,
            Payload: "ordered-msg"u8.ToArray(),
            StreamId: "c1",
            StreamSeq: 42u,
            StreamGapTimeoutSeconds: 600u);

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input with { Id = "stream0" }));

        decoded.StreamId.Should().Be("c1");
        decoded.StreamSeq.Should().Be(42u);
        decoded.StreamGapTimeoutSeconds.Should().Be(600u);
    }

    [Fact]
    public void Codec_RoundTrip_StrictMode_Gt0Preserved()
    {
        var input = new BackhaulMessage(
            Id: "stream0",
            Destination: "chat@N0DEST",
            Salt: null,
            Ttl: null,
            Payload: "x"u8.ToArray(),
            StreamId: "s",
            StreamSeq: 1u,
            StreamGapTimeoutSeconds: 0u);

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input));

        decoded.StreamGapTimeoutSeconds.Should().Be(0u, "gt=0 is the strict marker and must round-trip distinctly from absent");
        decoded.StreamId.Should().Be("s");
    }

    [Fact]
    public void Codec_PartialStreamTrio_Throws()
    {
        var bad = new BackhaulMessage(
            Id: "stream0",
            Destination: "chat@N0DEST",
            Salt: null,
            Ttl: null,
            Payload: "x"u8.ToArray(),
            StreamId: "s",
            StreamSeq: 1u);
        var act = () => BackhaulMessageCodec.Encode(bad);
        act.Should().Throw<ArgumentException>().WithMessage("*stream*");
    }

    // ── Wire format: ihave parser ──────────────────────────────────

    [Fact]
    public void Validator_ParsesAllThreeStreamKeys()
    {
        var line = "ihave 1234567 len=5 fmt=p s=1 sid=chat sn=42 gt=600 dst=app@N0DEST";
        var result = IHaveValidator.Validate(line);
        result.IsValid.Should().BeTrue();
        result.Offer!.StreamId.Should().Be("chat");
        result.Offer.StreamSeq.Should().Be(42u);
        result.Offer.StreamGapTimeoutSeconds.Should().Be(600u);
    }

    [Fact]
    public void Validator_RejectsPartialStreamSet()
    {
        var line = "ihave 1234567 len=5 fmt=p s=1 sid=chat sn=42 dst=app@N0DEST";
        var result = IHaveValidator.Validate(line);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("sid=, sn=, gt=");
    }

    [Fact]
    public void Validator_RejectsNonNumericSn()
    {
        var line = "ihave 1234567 len=5 fmt=p s=1 sid=chat sn=foo gt=600 dst=app@N0DEST";
        var result = IHaveValidator.Validate(line);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("sn=");
    }

    [Fact]
    public void IHaveCommand_EmitsAllThreeWhenSet()
    {
        var cmd = new IHaveCommand
        {
            Message = new DappsMessage
            {
                Payload = "hello"u8.ToArray(),
                Destination = "chat@N0DEST",
                Salt = 1L,
            },
            StreamId = "c1",
            StreamSeq = 7u,
            StreamGapTimeoutSeconds = 0u,
        };
        var line = cmd.ToString();
        line.Should().Contain("sid=c1");
        line.Should().Contain("sn=7");
        line.Should().Contain("gt=0");
    }

    // ── Inbound: out-of-order delivery ─────────────────────────────

    [Fact]
    public async Task Inbox_OutOfOrderArrival_DeliversInOrderToMqtt()
    {
        // Three messages on the same (sender, stream) arrive sn=3, sn=2, sn=1.
        // The inbox must hold 3 and 2 until 1 lands, then drain all three
        // in 1, 2, 3 order onto the MQTT topic.
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var received = new List<(string Id, byte[] Payload, uint? Sn)>();
        var allThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            var snProp = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(p => p.Name == "dapps-stream-seq");
            uint? sn = snProp is not null && uint.TryParse(snProp.Value, out var s) ? s : null;
            var idProp = e.ApplicationMessage.UserProperties!.Single(p => p.Name == "dapps-id");
            received.Add((idProp.Value, e.ApplicationMessage.PayloadSegment.ToArray(), sn));
            if (received.Count == 3) allThree.TrySetResult();
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var bm3 = MakeStreamMessage("ord0003", "G0FROM", "c1", 3, 0, "msg-three");
        var bm2 = MakeStreamMessage("ord0002", "G0FROM", "c1", 2, 0, "msg-two");
        var bm1 = MakeStreamMessage("ord0001", "G0FROM", "c1", 1, 0, "msg-one");

        await inbox.DeliverAsync(bm3, "G0HOP", ct);
        await inbox.DeliverAsync(bm2, "G0HOP", ct);
        // Nothing on MQTT yet.
        await Task.Delay(150, ct);
        received.Should().BeEmpty("ordered messages park until the missing prior arrives");

        await inbox.DeliverAsync(bm1, "G0HOP", ct);
        await allThree.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        received.Select(r => r.Sn).Should().Equal(1u, 2u, 3u);
        received.Select(r => System.Text.Encoding.UTF8.GetString(r.Payload))
            .Should().Equal("msg-one", "msg-two", "msg-three");

        // Recv state cursor advanced past the run.
        var state = await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "c1");
        state!.NextExpectedSeq.Should().Be(4u);
        state.GapDeadline.Should().Be(DateTime.MinValue);

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task Inbox_StrictMode_NeverSkipsGap()
    {
        // gt=0: a missing prior stalls indefinitely. Even after a long
        // wall-clock advance, the sweeper must not skip.
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var receivedCount = 0;
        client.ApplicationMessageReceivedAsync += async e =>
        {
            Interlocked.Increment(ref receivedCount);
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var bm2 = MakeStreamMessage("strict02", "G0FROM", "s1", 2, 0, "two");
        await inbox.DeliverAsync(bm2, "G0HOP", ct);

        // Run the sweeper logic manually after a huge clock advance:
        // strict mode means GapDeadline stays MinValue, so nothing
        // qualifies as stale.
        clock.Advance(TimeSpan.FromDays(1));
        var stale = await database.GetStaleStreamGapsAsync(clock.GetUtcNow().UtcDateTime);
        stale.Should().BeEmpty("strict-mode (gt=0) parked rows never set GapDeadline; the sweeper has nothing to do");

        await Task.Delay(150, ct);
        receivedCount.Should().Be(0, "strict mode keeps the row parked until the missing prior arrives");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task Inbox_TimeoutMode_SweeperSkipsGapAndDrains()
    {
        // gt=600: sn=2 parks with deadline = arrival+600s. After 600s
        // elapse without sn=1 arriving, the sweeper advances the cursor
        // past the gap and delivers sn=2.
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var received = new List<uint?>();
        var firstDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            var snProp = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(p => p.Name == "dapps-stream-seq");
            uint? sn = snProp is not null && uint.TryParse(snProp.Value, out var s) ? s : null;
            received.Add(sn);
            firstDelivery.TrySetResult();
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var bm2 = MakeStreamMessage("to000002", "G0FROM", "t1", 2, 600, "after-gap");
        await inbox.DeliverAsync(bm2, "G0HOP", ct);

        // Cursor should be 1, message parked, deadline set 600s out.
        var state = await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "t1");
        state!.NextExpectedSeq.Should().Be(1u);
        state.GapDeadline.Should().NotBe(DateTime.MinValue);

        // Advance clock past deadline + manually invoke the sweeper.
        clock.Advance(TimeSpan.FromSeconds(601));
        var stale = await database.GetStaleStreamGapsAsync(clock.GetUtcNow().UtcDateTime);
        stale.Should().HaveCount(1);
        await inbox.SkipGapAsync(stale.Single(), ct);

        await firstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        received.Single().Should().Be(2u, "the sweeper skipped sn=1 and delivered sn=2");

        var after = await database.GetStreamRecvStateAsync("N0SELF", "G0FROM", "t1");
        after!.NextExpectedSeq.Should().Be(3u);
        after.GapDeadline.Should().Be(DateTime.MinValue);

        await client.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task Inbox_StaleSeqAfterCursor_DroppedAsStreamStale()
    {
        // After sn=1 delivers and the cursor advances to 2, a re-arrival
        // of sn=1 is stale and must be dropped (not re-delivered to MQTT).
        var ct = TestContext.Current.CancellationToken;
        var client = await ConnectClient();
        var deliveryCount = 0;
        client.ApplicationMessageReceivedAsync += async e =>
        {
            Interlocked.Increment(ref deliveryCount);
            await Task.CompletedTask;
        };
        await client.SubscribeAsync("dapps/in/chat",
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, ct);

        await inbox.DeliverAsync(MakeStreamMessage("stale001", "G0FROM", "x1", 1, 0, "first"), "G0HOP", ct);
        await Task.Delay(150, ct);
        deliveryCount.Should().Be(1);

        await inbox.DeliverAsync(MakeStreamMessage("stale002", "G0FROM", "x1", 1, 0, "duplicate-first"), "G0HOP", ct);
        await Task.Delay(150, ct);

        deliveryCount.Should().Be(1, "stale re-arrivals of an already-delivered seq must not double-deliver");
        var dropped = await database.GetRecentDroppedMessages();
        dropped.Should().Contain(d => d.Id == "stale002" && d.Reason == "stream-stale");

        await client.DisconnectAsync(cancellationToken: ct);
    }

    // ── Sender-side counter persistence ────────────────────────────

    [Fact]
    public async Task Sender_CounterPersistsAcrossSubmissions()
    {
        await database.SubmitOutboundMessage("chat", "G0DEST", "one"u8.ToArray(),
            streamId: "c1");
        await database.SubmitOutboundMessage("chat", "G0DEST", "two"u8.ToArray(),
            streamId: "c1");
        await database.SubmitOutboundMessage("chat", "G0DEST", "three"u8.ToArray(),
            streamId: "c1");

        var states = await database.GetStreamSendStatesAsync();
        states.Should().ContainSingle();
        states[0].NextSeq.Should().Be(4u, "after three submissions the next-to-mint seq is 4");

        var rows = (await database.GetRecentMessages(10)).OrderBy(r => r.StreamSeq).ToList();
        rows.Select(r => r.StreamSeq).Should().Equal(1u, 2u, 3u);
        rows.Should().AllSatisfy(r => r.StreamId.Should().Be("c1"));
        rows.Should().AllSatisfy(r => r.StreamGapTimeoutSeconds.Should().Be(0u));
    }

    [Fact]
    public async Task Sender_DifferentStreamIds_GetIndependentCounters()
    {
        await database.SubmitOutboundMessage("chat", "G0DEST", "a"u8.ToArray(), streamId: "c1");
        await database.SubmitOutboundMessage("chat", "G0DEST", "b"u8.ToArray(), streamId: "c2");
        await database.SubmitOutboundMessage("chat", "G0DEST", "c"u8.ToArray(), streamId: "c1");

        var rows = await database.GetRecentMessages(10);
        rows.Where(r => r.StreamId == "c1").Select(r => r.StreamSeq).OrderBy(s => s).Should().Equal(1u, 2u);
        rows.Where(r => r.StreamId == "c2").Select(r => r.StreamSeq).OrderBy(s => s).Should().Equal(1u);
    }

    [Fact]
    public async Task Sender_NoStreamId_NoCounterMutationOrStreamFields()
    {
        await database.SubmitOutboundMessage("chat", "G0DEST", "plain"u8.ToArray());

        var states = await database.GetStreamSendStatesAsync();
        states.Should().BeEmpty();

        var rows = await database.GetRecentMessages(10);
        rows.Single().StreamId.Should().BeNull();
        rows.Single().StreamSeq.Should().BeNull();
        rows.Single().StreamGapTimeoutSeconds.Should().BeNull();
    }

    // ── helpers ────────────────────────────────────────────────────

    private static BackhaulMessage MakeStreamMessage(
        string id, string originator, string sid, uint sn, uint gt, string body)
        => new(
            Id: id,
            Destination: "chat@N0SELF",
            Salt: 1L,
            Ttl: 600,
            Payload: System.Text.Encoding.UTF8.GetBytes(body),
            Originator: originator,
            StreamId: sid,
            StreamSeq: sn,
            StreamGapTimeoutSeconds: gt);

    private async Task<IMqttClient> ConnectClient()
    {
        var client = new MqttFactory().CreateMqttClient();
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true)
            .Build();
        await client.ConnectAsync(opts);
        return client;
    }

    private static int PickFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
