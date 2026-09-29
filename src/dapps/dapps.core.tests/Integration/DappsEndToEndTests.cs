using System.Text;
using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The DAPPSv1 exchange end to end: two real DAPPS daemons, each on its
/// own real node, driven through their app API the way an app would, with
/// the nodes' own monitors as the record of what went over the air.
/// Nothing here pokes the forwarder by hand, so every test also relies on
/// it sending when a message is queued.
///
///     app -> DAPPS A -- node A -- node B -- DAPPS B -> app
///
/// These cases run on any pair of nodes (<see cref="IDappsNodePair"/>): BPQ
/// over AXIP (<see cref="DappsEndToEndTests"/>), pdn over AXUDP
/// (<see cref="PdnEndToEndTests"/>), and pdn with BPQ, each end calling
/// (<see cref="PdnBpqEndToEndTests"/>, <see cref="BpqPdnEndToEndTests"/>).
/// </summary>
public abstract class DappsExchangeTests(IDappsNodePair pair) : IAsyncLifetime
{
    protected static readonly TimeSpan Delivery = TimeSpan.FromSeconds(45);
    private readonly List<IAsyncDisposable> running = [];
    private IAirMonitor air = null!;

    public async ValueTask InitializeAsync()
    {
        air = await pair.StartAirMonitorAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState is { Result: TestResult.Failed }) WriteFailureRecord();
        foreach (var r in running) await r.DisposeAsync();
        await air.DisposeAsync();
        // Let the nodes release the registrations and any link state before
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

        // The whole session: B's prompt and rules, the routes pull, A's
        // rules with the message in one frame, the ack, and once the link
        // has been quiet a while, A's quit and a clean hang-up.
        await HangUpShowsAsync(ct, TimeSpan.FromSeconds(45));
        Connects("A").Should().Be(1, Transcript());
        AirSent("B", "DAPPSv1>").Should().Be(1, Transcript());
        AirSent("B", "exchange id=").Should().Be(1, "B's rules go once, with its prompt\n" + Transcript());
        AirSent("A", "\nroutes").Should().Be(1, Transcript());
        AirSent("B", "route ").Should().BeGreaterThan(0, Transcript());
        AirSent("A", "exchange id=").Should().Be(1, Transcript());
        air.SentBy("A").Should().ContainSingle(f => f.Contains("exchange id=") && f.Contains($"msg {id} "),
            "the rules and the message share a frame\n" + Transcript());
        AirSent("B", $"ack {id}").Should().Be(1, Transcript());
        AirSent("A", "ihave ").Should().Be(0, "a message this small goes unasked\n" + Transcript());
        AirSent("A", "\nquit").Should().Be(1, Transcript());
        AirSent("B", "bye").Should().Be(1, Transcript());
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
        await HangUpShowsAsync(ct, TimeSpan.FromSeconds(45));
        Connects("A").Should().Be(1, "five queued messages share one session\n" + Transcript());
        Connects("B").Should().Be(0, Transcript());
    }

    [Fact]
    public async Task MailTheOtherNodeCantDeliver_ComesBackOnTheCallersSession()
    {
        // B has no route to A, so its mail for A waits in its queue until
        // A calls for its own reasons; then it goes on A's session.
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 0, bKnowsA: false);
        var reply = Encoding.UTF8.GetBytes("waiting for you to call");
        await b.SubmitAsync("chat", a.Callsign, reply, ct);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("hello"), ct);

        (await a.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(reply);
        (await b.WaitForInboundAsync("chat", 1, Delivery, ct)).Should().ContainSingle();
        AirSent("B", "msg ").Should().BeGreaterThan(0, Transcript());
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
        // AGW frames long, offered first because it's over B's inline limit.
        var noise = new byte[1500];
        new Random(7).NextBytes(noise);

        await a.SubmitAsync("wps", b.Callsign, post, ct);
        await a.SubmitAsync("mail", b.Callsign, mail, ct);
        await a.SubmitAsync("bin", b.Callsign, noise, ct);

        (await b.WaitForInboundAsync("wps", 1, Delivery, ct)).Single().Payload.Should().Equal(post);
        (await b.WaitForInboundAsync("mail", 1, Delivery, ct)).Single().Payload.Should().Equal(mail);
        (await b.WaitForInboundAsync("bin", 1, Delivery, ct)).Single().Payload.Should().Equal(noise);
        // Several messages share a frame now, and BPQ's monitor shows only
        // part of a long one, so the daemons' logs are the record here.
        System.Text.RegularExpressions.Regex.Count(a.Log, " as fmt=z1: ").Should().BeGreaterThanOrEqualTo(3,
            "the post and both parts of the mail go compressed\n" + a.Tail());
        var parts = (mail.Length + 4095) / 4096;
        b.Log.Should().Contain($"({parts} fragments, {mail.Length} bytes)", "the parts are put back together at B\n" + b.Tail());
        b.Log.Should().Contain("Accepting offer ", "the noise is too big to send unasked, so it's offered first\n" + b.Tail());
    }

    [Fact]
    public async Task AnOpenLink_CarriesTrafficBothWays_OnOneConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 60);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("first"), ct);
        await b.WaitForInboundAsync("chat", 1, Delivery, ct);

        // The reply goes the other way over the same link, the moment
        // it's queued: B sends it on A's session.
        await b.SubmitAsync("chat", a.Callsign, Encoding.UTF8.GetBytes("reply"), ct);
        (await a.WaitForInboundAsync("chat", 1, Delivery, ct)).Single().Payload.Should().Equal(Encoding.UTF8.GetBytes("reply"));

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("second"), ct);
        await b.WaitForInboundAsync("chat", 2, Delivery, ct);

        Connects("A").Should().Be(1, "all three went on the link A opened\n" + Transcript());
        Connects("B").Should().Be(0, Transcript());
        (HangUps("A") + HangUps("B")).Should().Be(0, "the link is still held\n" + Transcript());
    }

    [Fact]
    public async Task AQuietLink_IsClosed_AndTheNextMessageDialsAfresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await StartPairAsync(ct, tail: 5);

        await a.SubmitAsync("chat", b.Callsign, Encoding.UTF8.GetBytes("first"), ct);
        await b.WaitForInboundAsync("chat", 1, Delivery, ct);
        await AirShowsAsync("A", "quit", ct, TimeSpan.FromSeconds(30));
        await HangUpShowsAsync(ct, TimeSpan.FromSeconds(30));

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
        AirSent("A", "sid=conv1 sn=").Should().BeGreaterThan(0, Transcript());
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
        AirSent("A", "exchange ").Should().Be(0, "a probe only asks; it never starts an exchange\n" + Transcript());
    }

    /// <summary>Connects (SABM) from one DAPPS node to the other. Two BPQs
    /// also link up between their node callsigns for NET/ROM, which isn't
    /// DAPPS traffic and doesn't count.</summary>
    protected int Connects(string side) => air.CountSentBy(side, $"Fm {Call(side)} To {Call(Other(side))} <C C");

    protected int HangUps(string side) => air.CountSentBy(side, $"Fm {Call(side)} To {Call(Other(side))} <D C");

    private string Call(string side) => side == "A" ? pair.ApplCallA : pair.ApplCallB;

    private static string Other(string side) => side == "A" ? "B" : "A";

    protected int AirSent(string side, string contains) => air.CountSentBy(side, contains.Replace("\\n", "\n"));

    /// <summary>
    /// Whether B's node may be the one to hang up after quit and bye. Both
    /// ends let go then, and which node's DISC reaches the air first is
    /// down to the nodes: between two BPQs it is A's (the caller's), but a
    /// pdn node answering hangs up first. False unless a pdn node is in it.
    /// </summary>
    protected virtual bool EitherEndMayHangUp => false;

    /// <summary>Wait for A's node to hang up the link between the two DAPPS
    /// callsigns (or either node, where <see cref="EitherEndMayHangUp"/>).</summary>
    protected async Task HangUpShowsAsync(CancellationToken ct, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (HangUps("A") + (EitherEndMayHangUp ? HangUps("B") : 0) == 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"{(EitherEndMayHangUp ? "Neither end" : "A")} never hung up.\n{Transcript()}");
            }
            await Task.Delay(100, ct);
        }
    }

    protected async Task AirShowsAsync(string side, string contains, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!await air.WaitForAsync(side, contains, timeout ?? TimeSpan.FromSeconds(30), ct))
        {
            throw new TimeoutException($"Never heard '{contains}' from {side}.\n{Transcript()}");
        }
    }

    /// <summary>
    /// A failure's message carries only part of the story, so a failed test
    /// leaves the whole air transcript and both daemons' logs in
    /// scenario-reports/, which CI keeps.
    /// </summary>
    private void WriteFailureRecord()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var pairName = new string([.. pair.ChannelName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        var file = Path.Combine(dir, $"e2e-{pairName}-{TestContext.Current.TestMethod?.MethodName}.txt");
        File.WriteAllText(file, "--- air ---\n" + air.Transcript() + "\n\n"
            + string.Join("\n\n", running.OfType<DappsDaemon>().Select(d => $"--- {d.Name} ({d.Callsign}) log ---\n{d.Log}")));
    }

    protected string Transcript() => "--- air ---\n" + air.Transcript() + "\n" + string.Join("\n", running.OfType<DappsDaemon>().Select(d => d.Tail(40)));

    private async Task<(DappsDaemon A, DappsDaemon B)> StartPairAsync(
        CancellationToken ct, int tail = 0, bool compression = true, bool bKnowsA = true)
    {
        var a = await StartNodeAsync("a", pair.ApplCallA, pair.NodeA,
            [new(pair.ApplCallB, pair.NodeA.BearerPort)], tail, ct, compression);
        var b = await StartNodeAsync("b", pair.ApplCallB, pair.NodeB,
            bKnowsA ? [new(pair.ApplCallA, pair.NodeB.BearerPort)] : [], tail, ct, compression);
        return (a, b);
    }

    private protected async Task<DappsDaemon> StartNodeAsync(
        string name, string callsign, NodeAttachment node, DappsDaemon.Neighbour[] neighbours, int tail, CancellationToken ct,
        bool compression = true)
    {
        var settings = new Dictionary<string, string>
        {
            ["DAPPS_SESSION_TAIL_SECONDS"] = tail.ToString(),
            ["DAPPS_COMPRESSION_ENABLED"] = compression ? "true" : "false",
        };
        var daemon = await DappsDaemon.StartAsync(name, callsign, node, neighbours, settings, ct);
        running.Add(daemon);
        return daemon;
    }
}

/// <summary>
/// The exchange between two DAPPS daemons on two BPQ nodes linked over AXIP,
/// with BPQ's AGW monitor as the record of what went over the air:
///
///     app -> DAPPS A -AGW- BPQ-A -AXIP- BPQ-B -AGW- DAPPS B -> app
///
/// Beside the shared cases, a bare AGW caller checks the server's replies
/// one at a time, and a connect script reaches a peer through BPQ's prompt.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class DappsEndToEndTests(TwoInstanceLinbpqFixture fixture) : DappsExchangeTests(fixture)
{
    [Fact]
    public async Task TheServer_AnswersEachCommandAsDocumented()
    {
        // A bare caller on node A, talking DAPPSv1 by hand to the real
        // daemon on B, over the air.
        var ct = TestContext.Current.CancellationToken;
        var b = await StartNodeAsync("b", fixture.ApplCallB, fixture.NodeB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], tail: 120, ct);
        var t = TimeSpan.FromSeconds(20);

        await using (var caller = await RawAgwCaller.ConnectAsync(
            fixture.Host, fixture.AgwPortA, fixture.ApplCallA, fixture.ApplCallB, fixture.AxipPortIndex, ct))
        {
            (await caller.ReadLineAsync(t, ct)).Should().Be("DAPPSv1>");
            var rules = dapps.client.Backhaul.ExchangeRules.Parse((await caller.ReadLineAsync(t, ct))!);
            rules.HoldSeconds.Should().Be(120);
            rules.Inline.Should().Be(256);
            rules.Dictionaries.Should().Contain(1);

            await caller.SendAsync("help\n", ct);
            (await caller.ReadLineAsync(t, ct)).Should().StartWith("This is DAPPS");

            await caller.SendAsync($"ihave oops len=nope dst=chat@{b.Callsign}\n", ct);
            (await caller.ReadLineAsync(t, ct)).Should().Be("error oops");

            // One message the simple way: offer, send, data, ack.
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

            // Our rules: from now on either side sends as it likes. B
            // already sent its own, so it doesn't answer with another.
            var again = "again"u8.ToArray();
            var againId = dapps.client.DappsMessage.ComputeHash(again, 7L)[..7];
            await caller.SendAsync([.. Encoding.ASCII.GetBytes($"exchange id=raw001 hold=30 inline=256\nmsg {againId} len=5 fmt=p s=7 dst=chat@{b.Callsign}\n"), .. again], ct);
            (await caller.ReadLineAsync(t, ct)).Should().Be($"ack {againId}");

            // Mail for us turns up at B while the link is open: B sends it
            // on this session, unasked, as it's small.
            var reply = "reply"u8.ToArray();
            await b.SubmitAsync("chat", fixture.ApplCallA, reply, ct);
            var line = await caller.ReadLineAsync(t, ct);
            line.Should().StartWith("msg ").And.Contain(" len=5 fmt=p ");
            (await caller.ReadBytesAsync(reply.Length, t, ct)).Should().Equal(reply);
            await caller.SendAsync($"ack {line!.Split(' ')[1]}\n", ct);

            await caller.SendAsync("quit\n", ct);
            (await caller.ReadLineAsync(t, ct)).Should().Be("bye");
            (await caller.ReadLineAsync(t, ct)).Should().BeNull("B hangs up after bye");
        }

        // Before an exchange, an unknown command is answered and ends the session.
        await Task.Delay(2000, ct);
        await using var second = await RawAgwCaller.ConnectAsync(
            fixture.Host, fixture.AgwPortA, fixture.ApplCallA, fixture.ApplCallB, fixture.AxipPortIndex, ct);
        (await second.ReadLineAsync(t, ct)).Should().Be("DAPPSv1>");
        (await second.ReadLineAsync(t, ct)).Should().StartWith("exchange ");
        await second.SendAsync("bogus\n", ct);
        (await second.ReadLineAsync(t, ct)).Should().Be("eh?");
        (await second.ReadLineAsync(t, ct)).Should().BeNull("an unknown command ends the session");
    }

    [Fact]
    public async Task AConnectScript_ReachesThePeerThroughItsNode()
    {
        // A's route to B goes via B's node: dial the node, then type B's
        // application command at its prompt, as for a peer beyond a node
        // that doesn't speak DAPPS.
        var ct = TestContext.Current.CancellationToken;
        var script = new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep("APPLB", "DAPPSv1>", 30)]);
        var b = await StartNodeAsync("b", fixture.ApplCallB, fixture.NodeB, [new(fixture.ApplCallA, fixture.AxipPortIndex)], tail: 0, ct);
        var a = await StartNodeAsync("a", fixture.ApplCallA, fixture.NodeA,
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
}
