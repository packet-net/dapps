using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The DAPPSv1 protocol end to end: two real DAPPS daemons, each on its
/// own real BPQ, linked over AXIP, driven through their app API the way an
/// app would, with BPQ's monitor as the record of what went over the air.
/// Nothing here pokes the forwarder by hand, so every test also relies on
/// it sending when a message is queued.
///
///     app -> DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B -> app
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class DappsEndToEndTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(45);
    private readonly List<IAsyncDisposable> running = [];
    private AirMonitor air = null!;

    public async ValueTask InitializeAsync()
    {
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        await air.DisposeAsync();
        // Let BPQ release the AGW registrations and any link state before
        // the next test in the collection uses the same callsigns.
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AMessage_GoesFromOneAppToTheOther_WithTheWholeExchangeOnAir()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0);

        var payload = Encoding.UTF8.GetBytes("GM all, anyone on the WPS channel this morning?");
        var id = await a.SubmitAsync("chat", b.Callsign, payload, ct, ttl: 600);

        var got = (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Single();
        got.Id.Should().Be(id);
        got.Payload.Should().Equal(payload);
        got.SourceCallsign.Should().Be(a.Callsign);
        got.OriginatorCallsign.Should().Be(a.Callsign, "src= carries the originator end to end");
        got.Ttl.Should().BeInRange(500, 600, "the ttl counts down from what the app asked for");

        // The rest of the session: routes pulled, the peer polled with
        // rev, then a clean hang-up.
        await AirShowsAsync("A", $"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <D C", ct);
        Connects("A").Should().Be(1, Transcript());
        AirSent("A", $"ihave {id} ").Should().Be(1, Transcript());
        AirSent("B", $"send {id}").Should().Be(1, Transcript());
        AirSent("A", $"data {id}").Should().Be(1, Transcript());
        AirSent("B", $"ack {id}").Should().Be(1, Transcript());
        AirSent("A", "\nroutes").Should().BeGreaterThan(0, Transcript());
        AirSent("B", "route ").Should().BeGreaterThan(0, Transcript());
        AirSent("A", "\nrev").Should().BeGreaterThan(0, Transcript());
    }

    [Fact]
    public async Task ABurstOfMessages_GoesOnOneConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0);

        for (var i = 1; i <= 5; i++)
        {
            await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes($"post {i}"), ct);
        }

        var inbox = await b.WaitForInboundAsync("chat", 5, Delivery, ct);
        inbox.Select(m => Encoding.UTF8.GetString(m.Payload)).Should().BeEquivalentTo(
            Enumerable.Range(1, 5).Select(i => $"post {i}"));
        await AirShowsAsync("A", $"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <D C", ct);
        Connects("A").Should().Be(1, "five queued messages share one session\n" + Transcript());
        Connects("B").Should().Be(0, Transcript());
    }

    [Fact]
    public async Task MailTheOtherNodeCantDeliver_ComesBackByRev()
    {
        // B has no route to A, so its mail for A waits in its queue until
        // A connects for its own reasons and asks with rev.
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0, bKnowsA: false);
        var reply = Encoding.UTF8.GetBytes("waiting for you to call");
        await b.SubmitAsync("chat", a.Callsign, reply, ct);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("hello"), ct);

        (await a.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(reply);
        (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Should().ContainSingle();
        AirSent("A", "\nrev").Should().BeGreaterThan(0, Transcript());
        Connects("B").Should().Be(0, "B never dialled; its mail went back on A's session\n" + Transcript());
    }

    [Fact]
    public async Task Payloads_Compressed_Split_OrBinary_AllArriveIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0);

        // A WPS replication post: small JSON, goes zstd with the dictionary.
        var post = Encoding.UTF8.GetBytes(
            """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""");
        // Mail-sized text: over the 4 KiB fragment threshold, so it goes
        // in two parts, each compressed, reassembled at B.
        var mail = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 120).Select(i =>
            $"Line {i}: the weekly packet net is on Wednesday at 20:00 local, all welcome. 73\n")));
        // Noise: doesn't compress, so it goes plain and binary, several
        // AGW frames long.
        var noise = new byte[1500];
        new Random(7).NextBytes(noise);

        await a.SubmitAsync("wps", b.Callsign, post, ct);
        await a.SubmitAsync("mail", b.Callsign, mail, ct);
        await a.SubmitAsync("bin", b.Callsign, noise, ct);

        (await b.WaitForInboundAsync("wps", 1, Delivery, ct)).Single().Payload.Should().Equal(post);
        (await b.WaitForInboundAsync("mail", 1, Delivery, ct)).Single().Payload.Should().Equal(mail);
        (await b.WaitForInboundAsync("bin", 1, Delivery, ct)).Single().Payload.Should().Equal(noise);
        AirSent("A", "fmt=z1").Should().BeGreaterThanOrEqualTo(3, "the post and both parts of the mail go compressed\n" + Transcript());
        var parts = (mail.Length + 4095) / 4096;
        for (var part = 1; part <= parts; part++)
        {
            AirSent("A", $"frag={part}/{parts}").Should().Be(1, Transcript());
        }
    }

    [Fact]
    public async Task AHeldLink_CarriesTrafficBothWays_OnOneConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 60);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("first"), ct);
        await b.WaitForInboundAsync("chat", 1, Delivery, ct);
        await AirShowsAsync("B", "tail 60", ct);

        // The reply goes the other way over the same link: B says pending,
        // A collects it with rev.
        await b.SubmitAsync("chat", a.Callsign, Encoding.UTF8.GetBytes("reply"), ct);
        (await a.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(Encoding.UTF8.GetBytes("reply"));

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("second"), ct);
        await b.WaitForInboundAsync("chat", 2, Delivery, ct);

        Connects("A").Should().Be(1, "all three went on the link A opened\n" + Transcript());
        Connects("B").Should().Be(0, Transcript());
        AirSent("B", "pending").Should().BeGreaterThan(0, Transcript());
        HangUps("A").Should().Be(0, "the link is still held\n" + Transcript());
    }

    [Fact]
    public async Task AQuietHeldLink_IsClosed_AndTheNextMessageDialsAfresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 5);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("first"), ct);
        await b.WaitForInboundAsync("chat", 1, Delivery, ct);
        await AirShowsAsync("A", "quit", ct, TimeSpan.FromSeconds(30));
        await AirShowsAsync("A", $"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <D C", ct, TimeSpan.FromSeconds(30));

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("second"), ct);
        await b.WaitForInboundAsync("chat", 2, Delivery, ct);
        Connects("A").Should().Be(2, Transcript());
    }

    [Fact]
    public async Task AnOrderedStream_ArrivesComplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0);

        for (var i = 1; i <= 3; i++)
        {
            await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes($"part {i}"), ct, streamId: "conv1");
        }

        var inbox = await b.WaitForInboundAsync("chat", 3, Delivery, ct);
        inbox.Select(m => Encoding.UTF8.GetString(m.Payload)).Should().BeEquivalentTo(["part 1", "part 2", "part 3"]);
        AirSent("A", "sid=conv1 sn=").Should().Be(3, Transcript());
    }

    [Fact]
    public async Task AProbe_AsksThePeerWhoItKnows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0);

        await a.SignInAsAdminAsync(ct);
        var response = await a.Http.PostAsync($"Probes/run/{b.Callsign}", null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        response.IsSuccessStatusCode.Should().BeTrue(body + "\n" + a.Tail());
        if (!await air.WaitForAsync("B", $"peer {a.Callsign}", TimeSpan.FromSeconds(20), ct))
        {
            throw new TimeoutException($"Never heard B's peer list. Probe said: {body}\n{Transcript()}");
        }
        AirSent("A", "\npeers").Should().Be(1, body + "\n" + Transcript());
        AirSent("B", $"peer {a.Callsign}").Should().Be(1, "B lists A as a neighbour\n" + Transcript());
    }

    [Fact]
    public async Task TheServer_AnswersEachCommandAsDocumented()
    {
        // A bare caller on node A, talking DAPPSv1 by hand to the real
        // daemon on B, over the air.
        var ct = TestContext.Current.CancellationToken;
        var b = await StartNodeAsync("b", fixture.ApplCallB, fixture.AgwPortB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], tail: 120, ct);
        await using var caller = await RawAgwCaller.ConnectAsync(
            fixture.Host, fixture.AgwPortA, fixture.ApplCallA, fixture.ApplCallB, fixture.AxipPortIndex, ct);
        var t = TimeSpan.FromSeconds(20);

        (await caller.ReadLineAsync(t, ct)).Should().Be("DAPPSv1>");

        await caller.SendAsync("help\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().StartWith("This is DAPPS");

        await caller.SendAsync($"ihave oops len=nope dst=chat@{b.Callsign}\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("error oops");

        var hello = "hello"u8.ToArray();
        var helloId = dapps.client.DappsMessage.ComputeHash(hello, 5L)[..7];
        await caller.SendAsync($"ihave {helloId} len=5 fmt=p s=5 dst=chat@{b.Callsign}\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be($"send {helloId}");
        await caller.SendAsync([.. Encoding.ASCII.GetBytes($"data {helloId}\n"), .. hello], ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be($"ack {helloId}");
        (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(hello);

        var world = "world"u8.ToArray();
        var worldId = dapps.client.DappsMessage.ComputeHash(world, 6L)[..7];
        await caller.SendAsync($"ihave {worldId} len=5 fmt=p s=6 dst=chat@{b.Callsign}\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be($"send {worldId}");
        await caller.SendAsync([.. Encoding.ASCII.GetBytes($"data {worldId}\n"), .. "wurld"u8.ToArray()], ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be($"bad {worldId}", "the payload doesn't hash to its id");

        await caller.SendAsync("peers\n", ct);
        (await ReadUntilEndAsync(caller, ct)).Should().Contain($"peer {fixture.ApplCallA} source=n port={fixture.AxipPortIndex}");

        await caller.SendAsync("routes\n", ct);
        (await ReadUntilEndAsync(caller, ct)).Should().Contain($"route {fixture.CallsignA} hops=1");

        await caller.SendAsync("tail 30\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("tail 30", "the lower of our 30 and B's 120");

        await caller.SendAsync("rev\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("DAPPSv1>", "nothing queued for us yet");

        // Mail for us turns up at B while we're connected and idle: B can't
        // dial us over our open session, so it says pending.
        var reply = "reply"u8.ToArray();
        await b.SubmitAsync("chat", fixture.ApplCallA, reply, ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("pending");
        await caller.SendAsync("rev\n", ct);
        var offer = await caller.ReadLineAsync(t, ct);
        offer.Should().StartWith("ihave ");
        var replyId = offer!.Split(' ')[1];
        await caller.SendAsync($"send {replyId}\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be($"data {replyId}");
        (await caller.ReadBytesAsync(reply.Length, t, ct)).Should().Equal(reply);
        await caller.SendAsync($"ack {replyId}\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("DAPPSv1>");

        await caller.SendAsync("bogus\n", ct);
        (await caller.ReadLineAsync(t, ct)).Should().Be("eh?");
        (await caller.ReadLineAsync(t, ct)).Should().BeNull("an unknown command ends the session");
    }

    [Fact]
    public async Task AConnectScript_ReachesThePeerThroughItsNode()
    {
        // A's route to B goes via B's node: dial the node, then type B's
        // application command at its prompt, as for a peer beyond a node
        // that doesn't speak DAPPS.
        var ct = TestContext.Current.CancellationToken;
        var script = new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep("APPLB", "DAPPSv1>", 30)]);
        var b = await StartNodeAsync("b", fixture.ApplCallB, fixture.AgwPortB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], tail: 0, ct);
        var a = await StartNodeAsync("a", fixture.ApplCallA, fixture.AgwPortA,
            [new(fixture.CallsignB, fixture.AxipPortIndex, Script: script)], tail: 0, ct);

        var payload = "via the node"u8.ToArray();
        await a.SubmitAsync("chat", b.Callsign, payload, ct);

        (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(payload);
        // However BPQ routes the connect to B's node (directly, or over the
        // NET/ROM link the two nodes build between themselves), A has to
        // type B's application command there and get B's prompt back.
        AirSent("A", "APPLB").Should().Be(1, Transcript());
        AirSent("B", "Connected to APPLB").Should().Be(1, Transcript());
        Connects("A").Should().Be(0, "A never dialled B directly\n" + Transcript());
    }

    private static async Task<List<string>> ReadUntilEndAsync(RawAgwCaller caller, CancellationToken ct)
    {
        var lines = new List<string>();
        while (await caller.ReadLineAsync(TimeSpan.FromSeconds(20), ct) is { } line && line != "end") lines.Add(line);
        return lines;
    }

    /// <summary>Connects (SABM) from one DAPPS node to the other. The two
    /// BPQs also link up between their node callsigns for NET/ROM, which
    /// isn't DAPPS traffic and doesn't count.</summary>
    private int Connects(string side) => air.CountSentBy(side, $"Fm {Call(side)} To {Call(Other(side))} <C C");

    private int HangUps(string side) => air.CountSentBy(side, $"Fm {Call(side)} To {Call(Other(side))} <D C");

    private string Call(string side) => side == "A" ? fixture.ApplCallA : fixture.ApplCallB;

    private static string Other(string side) => side == "A" ? "B" : "A";

    private int AirSent(string side, string contains) => air.CountSentBy(side, contains.Replace("\\n", "\n"));

    private async Task AirShowsAsync(string side, string contains, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!await air.WaitForAsync(side, contains, timeout ?? TimeSpan.FromSeconds(30), ct))
        {
            throw new TimeoutException($"Never heard '{contains}' from {side}.\n{Transcript()}");
        }
    }

    private string Transcript() => "--- air ---\n" + air.Transcript() + "\n" + string.Join("\n", running.OfType<DappsDaemon>().Select(d => d.Tail(40)));

    private async Task<(DappsDaemon A, DappsDaemon B)> StartPairAsync(
        CancellationToken ct, int tail = 0, bool compression = true, bool bKnowsA = true)
    {
        var a = await StartNodeAsync("a", fixture.ApplCallA, fixture.AgwPortA,
            [new(fixture.ApplCallB, fixture.AxipPortIndex)], tail, ct, compression);
        var b = await StartNodeAsync("b", fixture.ApplCallB, fixture.AgwPortB,
            bKnowsA ? [new(fixture.ApplCallA, fixture.AxipPortIndex)] : [], tail, ct, compression);
        return (a, b);
    }

    private async Task<DappsDaemon> StartNodeAsync(
        string name, string callsign, int agwPort, DappsDaemon.Neighbour[] neighbours, int tail, CancellationToken ct,
        bool compression = true)
    {
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_SESSION_TAIL_SECONDS"] = tail.ToString(),
            ["DAPPS_COMPRESSION_ENABLED"] = compression ? "true" : "false",
            ["DAPPS_OPPORTUNISTIC_POLL_ENABLED"] = "true",
        };
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex, neighbours, settings, ct);
        running.Add(node);
        return node;
    }
}
