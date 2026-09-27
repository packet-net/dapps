using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using Microsoft.Extensions.Logging.Abstractions;
using static dapps.core.tests.ExchangeTestKit;

namespace dapps.core.tests;

/// <summary>
/// The exchange session (docs/implement.md, "Sessions between DAPPS
/// nodes") against a peer driven line by line over a loopback socket:
/// the handshake, the receiver's rules, the window, ending, and a peer
/// whose session object changes under the link.
/// </summary>
public sealed class ExchangeSessionTests : IDisposable
{
    private const string Us = "N0CALL";
    private const string Them = "N0DEST";
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private CancellationToken Ct => lifetime.Token;

    private static string Rules(string tag = "peer01", int hold = 60, int inline = 256, int? max = null, string z = "1") =>
        $"exchange id={tag} hold={hold} inline={inline}" + (max is { } m ? $" max={m}" : "") + (z.Length > 0 ? $" z={z}" : "");

    /// <summary>A session that dialled: the peer plays the answering node.</summary>
    private async Task<(ExchangeSession Session, LinePeer Peer, Task Run, WriteRecordingStream Wire)> CallerAsync(
        ExchangeSettings? settings = null, IBackhaulInbox? inbox = null, Action<ExchangeSession>? opened = null,
        TimeSpan? maxLength = null)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var wire = new WriteRecordingStream(ours);
        var session = new ExchangeSession(wire, Them, dialled: true, settings ?? new ExchangeSettings(), inbox ?? new RecordingInbox(), NullLoggerFactory.Instance)
        {
            PromptWait = Short,
            MinQuiet = Short,
            MaxLength = maxLength ?? TimeSpan.FromMinutes(30),
            Opened = opened,
        };
        return (session, new LinePeer(theirs), session.RunAsync(Ct), wire);
    }

    /// <summary>A session that answered: the peer plays the caller.</summary>
    private async Task<(ExchangeSession Session, LinePeer Peer, Task Run)> CalleeAsync(
        ExchangeSettings? settings = null, IBackhaulInbox? inbox = null)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: false, settings ?? new ExchangeSettings(), inbox ?? new RecordingInbox(), NullLoggerFactory.Instance)
        {
            MinQuiet = Short,
        };
        var peer = new LinePeer(theirs);
        var run = session.RunAsync(Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        return (session, peer, run);
    }

    // ---- Session start ----

    [Fact]
    public async Task TheAnsweringNode_SendsThePrompt_AndItsRulesStraightAfter()
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: false,
            new ExchangeSettings(HoldSeconds: 120, MaxBytes: 4096), new RecordingInbox(), NullLoggerFactory.Instance);
        _ = session.RunAsync(Ct);
        var peer = new LinePeer(theirs);

        (await peer.ReadLineAsync(Ct)).Should().Be("DAPPSv1>");
        var rules = ExchangeRules.Parse(await peer.ReadLineAsync(Ct));

        rules.Tag.Should().NotBeEmpty().And.Be(session.OwnRules.Tag);
        rules.HoldSeconds.Should().Be(120);
        rules.Inline.Should().Be(ExchangeSettings.DefaultInline);
        rules.MaxBytes.Should().Be(4096);
        rules.Dictionaries.Should().Contain(dapps.client.Compression.PayloadCompression.CurrentDictionaryVersion);
    }

    [Fact]
    public async Task ACaller_SendsItsRulesAndFirstMessagesInOneWrite_OnceItHasThePeersRules()
    {
        var m1 = Message("one", $"app@{Them}", 1);
        var m2 = Message("two", $"app@{Them}", 2);
        var batch = new RecordingBatch(m1, m2);
        var (session, peer, _, wire) = await CallerAsync();
        session.TryTake(batch);

        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);

        (await peer.ReadLineAsync(Ct)).Should().StartWith($"exchange id={session.OwnRules.Tag} ");
        var (line1, payload1) = await peer.ReadWithPayloadAsync(Ct);
        var (line2, payload2) = await peer.ReadWithPayloadAsync(Ct);
        line1.Should().StartWith($"msg {m1.Id} ");
        payload1.Should().Equal(m1.Payload);
        line2.Should().StartWith($"msg {m2.Id} ");
        payload2.Should().Equal(m2.Payload);
        wire.Writes.Should().ContainSingle(w => w.StartsWith("exchange ") && w.Contains($"msg {m1.Id} ") && w.Contains($"msg {m2.Id} "),
            "the rules and the messages share a write, and so a frame");

        await peer.WriteLineAsync($"ack {m1.Id}\nack {m2.Id}", Ct);
        await batch.WaitForOutcomesAsync(2, Ct);
        batch.Outcomes.Should().AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
    }

    [Fact]
    public async Task ACaller_SendsNoContents_BeforeThePeersRulesArrive()
    {
        var m = Message("hello", $"app@{Them}");
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(new RecordingBatch(m));

        // A prompt on its own: our rules go, but nothing else until we
        // know what the peer takes.
        await peer.WriteLineAsync("DAPPSv1>", Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull();

        await peer.WriteLineAsync(Rules(), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"msg {m.Id} ");
    }

    [Fact]
    public async Task TheAnsweringNode_DoesNotAnswerTheCallersExchange_WithAnotherOne()
    {
        var inbox = new RecordingInbox();
        var (session, peer, _) = await CalleeAsync(inbox: inbox);
        var m = Message("hello", $"app@{Us}");

        await peer.WriteAsync([.. Encoding.UTF8.GetBytes(Rules() + "\n" + Line("msg", m)), .. m.Payload], Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {m.Id}", "its own rules went once, after the prompt");
        inbox.Texts.Should().Equal("hello");
        session.Established.Should().BeTrue();
    }

    [Fact]
    public async Task ACrossedCall_BothCallersSendTheirRulesAfterTheWait_AndTrafficGoesBothWays()
    {
        // Both ends dialled, so both are callers and neither hears a prompt.
        var (a, b) = await LoopbackPairAsync(Ct);
        var inboxA = new RecordingInbox();
        var inboxB = new RecordingInbox();
        var fromA = Message("from A", $"app@{Them}");
        var fromB = Message("from B", $"app@{Us}", 2);
        var batchA = new RecordingBatch(fromA);
        var batchB = new RecordingBatch(fromB);
        var sessionA = new ExchangeSession(a, Them, dialled: true, new ExchangeSettings(), inboxA, NullLoggerFactory.Instance) { PromptWait = Short, MinQuiet = Short };
        var sessionB = new ExchangeSession(b, Us, dialled: true, new ExchangeSettings(), inboxB, NullLoggerFactory.Instance) { PromptWait = Short, MinQuiet = Short };
        sessionA.TryTake(batchA);
        sessionB.TryTake(batchB);

        var runs = Task.WhenAll(sessionA.RunAsync(Ct), sessionB.RunAsync(Ct));
        await runs.WaitAsync(Patience, Ct);

        inboxA.Texts.Should().Equal("from B");
        inboxB.Texts.Should().Equal("from A");
        batchA.Outcomes.Single().Result.Accepted.Should().BeTrue();
        batchB.Outcomes.Single().Result.Accepted.Should().BeTrue();
        sessionA.Established.Should().BeTrue();
        sessionB.Established.Should().BeTrue();
    }

    [Fact]
    public async Task APeerOnANewSessionObject_GetsOurRulesAndUnansweredMessagesAgain_ButARepeatedTagGetsNothing()
    {
        var m = Message("hello", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules("old001") + "\n"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"exchange id={session.OwnRules.Tag} ");
        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {m.Id} ");

        // The link moved to a new session object at the peer's node: it
        // introduces itself with a new tag and hasn't seen our message.
        await peer.WriteLineAsync(Rules("new002"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"exchange id={session.OwnRules.Tag} ", "the same tag as before: we're the same session");
        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {m.Id} ");

        // The same object sending its rules again is not a restart.
        await peer.WriteLineAsync(Rules("new002"), Ct);
        await peer.WriteLineAsync(Rules("old001"), Ct);
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("no ping-pong: known tags change nothing");

        await peer.WriteLineAsync($"ack {m.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Should().ContainSingle().Which.Result.Accepted.Should().BeTrue();
    }

    // ---- The receiver's rules ----

    [Fact]
    public async Task APayloadOverThePeersInlineLimit_IsOfferedFirst_AndSentWhenAskedFor()
    {
        var small = Message("tiny", $"app@{Them}", 1);
        var large = Message(new string('x', 40), $"app@{Them}", 2);
        var batch = new RecordingBatch(small, large);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(inline: 16) + "\n"), Ct);
        await peer.ReadLineAsync(Ct);   // their exchange

        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {small.Id} ");
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"ihave {large.Id} len=40 ");

        await peer.WriteLineAsync($"send {large.Id}", Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"data {large.Id}");
        (await peer.ReadBytesAsync(40, Ct)).Should().Equal(large.Payload);
        await peer.WriteLineAsync($"ack {small.Id}\nack {large.Id}", Ct);

        await batch.WaitForOutcomesAsync(2, Ct);
        batch.Outcomes.Should().AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
    }

    [Fact]
    public async Task AnOfferThePeerAlreadyHas_IsAcked_AndItsPayloadNeverGoes()
    {
        var m = Message(new string('y', 300), $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, _, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"ihave {m.Id} ");

        await peer.WriteLineAsync($"ack {m.Id}", Ct);

        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("the payload wasn't sent");
    }

    [Fact]
    public async Task ACompressedOfferThePeerAlreadyHas_IsAcked_AndItsPayloadNeverGoes()
    {
        var post = new BackhaulMessage("wps0009", $"app@{Them}", 9L, 600, Encoding.UTF8.GetBytes(WpsPost));
        var batch = new RecordingBatch(post);
        var (session, peer, _, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60, Compress: true));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(inline: 0) + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"ihave {post.Id} ").And.Contain(" fmt=z1 ");

        await peer.WriteLineAsync($"ack {post.Id}", Ct);

        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("the payload wasn't sent");
    }

    [Fact]
    public async Task AMessageOverThePeersMax_IsRefused_WithoutGoingOnAir()
    {
        var big = Message(new string('z', 20), $"app@{Them}", 1);
        var ok = Message("fine", $"app@{Them}", 2);
        var batch = new RecordingBatch(big, ok);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(max: 10) + "\n"), Ct);
        await peer.ReadLineAsync(Ct);

        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {ok.Id} ", "only the one within its max goes");
        await peer.WriteLineAsync($"ack {ok.Id}", Ct);

        await batch.WaitForOutcomesAsync(2, Ct);
        var refused = batch.Outcomes.Single(o => o.Id == big.Id).Result;
        refused.Refused.Should().BeTrue();
        refused.Error.Should().Contain("10 bytes");
        batch.Outcomes.Single(o => o.Id == ok.Id).Result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task APeersNo_IsARefusal()
    {
        var m = Message("not wanted", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        await peer.WriteLineAsync($"no {m.Id} not for this app", Ct);

        await batch.WaitForOutcomesAsync(1, Ct);
        var result = batch.Outcomes.Single().Result;
        result.Refused.Should().BeTrue();
        result.Accepted.Should().BeFalse();
        result.Error.Should().Contain("not for this app");
    }

    [Fact]
    public async Task OurMax_RefusesBiggerOffersAndMessages_AndTheSessionCarriesOn()
    {
        var inbox = new RecordingInbox();
        var (session, peer, _) = await CalleeAsync(new ExchangeSettings(MaxBytes: 10), inbox);
        await peer.WriteLineAsync(Rules(), Ct);
        var offered = Message(new string('a', 20), $"app@{Us}", 1);
        var sent = Message(new string('b', 20), $"app@{Us}", 2);
        var small = Message("small", $"app@{Us}", 3);

        await peer.WriteLineAsync(Line("ihave", offered).TrimEnd('\n'), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"no {offered.Id} ");
        await peer.SendMessageAsync(sent, Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"no {sent.Id} ", "its payload is read past and it's dropped");
        await peer.SendMessageAsync(small, Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {small.Id}");

        inbox.Texts.Should().Equal("small");
    }

    [Fact]
    public async Task Compression_OnlyWhenThePeerHoldsTheDictionary()
    {
        var post = new BackhaulMessage("wps0001", $"app@{Them}", 1L, 600, Encoding.UTF8.GetBytes(WpsPost));
        var withZ = new RecordingBatch(post);
        var (s1, p1, _, _) = await CallerAsync(new ExchangeSettings(Compress: true));
        s1.TryTake(withZ);
        await p1.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(z: "1") + "\n"), Ct);
        await p1.ReadLineAsync(Ct);
        (await p1.ReadLineAsync(Ct)).Should().Contain(" fmt=z1 clen=");

        var plainOnly = new RecordingBatch(post with { Id = "wps0002" });
        var (s2, p2, _, _) = await CallerAsync(new ExchangeSettings(Compress: true));
        s2.TryTake(plainOnly);
        await p2.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(z: "") + "\n"), Ct);
        await p2.ReadLineAsync(Ct);
        (await p2.ReadLineAsync(Ct)).Should().Contain(" fmt=p ", "a peer without z= takes plain only");
    }

    [Fact]
    public async Task ACompressedMessageItCantDecode_GoesOncePlain_AndTheRestOfTheSessionTooThenBadAgainFails()
    {
        var first = new BackhaulMessage(DappsMessage.ComputeHash(Encoding.UTF8.GetBytes(WpsPost), 1L)[..7], $"app@{Them}", 1L, 600, Encoding.UTF8.GetBytes(WpsPost));
        var secondPayload = Encoding.UTF8.GetBytes(WpsPost.Replace("tonight", "this evening"));
        var second = new BackhaulMessage(DappsMessage.ComputeHash(secondPayload, 2L)[..7], $"app@{Them}", 2L, 600, secondPayload);
        var batch = new RecordingBatch(first);
        var (session, peer, _, _) = await CallerAsync(new ExchangeSettings(Compress: true));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);

        var compressedLine = await peer.ReadLineAsync(Ct);
        compressedLine.Should().Contain(" fmt=z1 ");
        IHaveValidator.TryGetWireLength(compressedLine, out _, out var clen);
        await peer.ReadBytesAsync(clen, Ct);
        await peer.WriteLineAsync($"bad {first.Id}", Ct);

        var (plainLine, plainPayload) = await peer.ReadWithPayloadAsync(Ct);
        plainLine.Should().StartWith($"msg {first.Id} ").And.Contain(" fmt=p ");
        plainPayload.Should().Equal(first.Payload);
        await peer.WriteLineAsync($"bad {first.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Error.Should().Contain("payload rejected");

        session.TryTake(new RecordingBatch(second));
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"msg {second.Id} ").And.Contain(" fmt=p ", "the rest of the session goes plain");
    }

    [Fact]
    public async Task AnErroredOffer_GoesOnceMorePlain_ThenFails()
    {
        var m = Message(new string('q', 300), $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);

        (await peer.ReadLineAsync(Ct)).Should().StartWith($"ihave {m.Id} ");
        await peer.WriteLineAsync($"error {m.Id}", Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith($"ihave {m.Id} ");
        await peer.WriteLineAsync($"error {m.Id}", Ct);

        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Error.Should().Contain("offer rejected");
    }

    // ---- The window ----

    [Fact]
    public async Task AtMostEightOfOursAreUnansweredAtOnce()
    {
        var messages = Enumerable.Range(1, 10).Select(i => Message($"message {i}", $"app@{Them}", i)).ToArray();
        var batch = new RecordingBatch(messages);
        var (session, peer, _, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);

        for (var i = 0; i < ExchangeSession.DefaultWindow; i++)
        {
            (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {messages[i].Id} ");
        }
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("the window is full");

        await peer.WriteLineAsync($"ack {messages[0].Id}", Ct);
        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {messages[8].Id} ");
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("one answer frees one place");
    }

    // ---- Ending ----

    [Fact]
    public async Task TheCaller_EndsAQuietSession_AfterTheLowerOfTheTwoHolds()
    {
        var (session, peer, run, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 1));
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(hold: 60) + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9)).And.BeLessThan(TimeSpan.FromSeconds(5));
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task TheAnsweringNode_NeverSaysQuit()
    {
        var (session, peer, run) = await CalleeAsync(new ExchangeSettings(HoldSeconds: 0));
        await peer.WriteLineAsync(Rules(hold: 0), Ct);

        (await peer.TryReadLineAsync(TimeSpan.FromSeconds(1), Ct)).Should().BeNull("only the node that dialled ends the session");
        await peer.WriteLineAsync("quit", Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be("bye");
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task TheCaller_EndsABusySession_AtItsLongestLength_AndWhatWasUnansweredStaysQueued()
    {
        var m = Message("in flight", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, run, _) = await CallerAsync(maxLength: TimeSpan.FromSeconds(1));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue();
        session.TryTake(new RecordingBatch()).Should().BeFalse("an ended session takes no more work");
    }

    [Fact]
    public async Task TheLinkEndingWithOursUnanswered_DefersThem()
    {
        var m = Message("in flight", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: true, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance);
        session.TryTake(batch);
        var run = session.RunAsync(Ct);
        var peer = new LinePeer(theirs);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        theirs.Close();
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue();
        session.Established.Should().BeTrue();
    }

    // ---- Receiving ----

    [Fact]
    public async Task OffersAndMessages_AreAnsweredInOrder_AndDelivered()
    {
        var inbox = new RecordingInbox();
        var (session, peer, _) = await CalleeAsync(inbox: inbox);
        var held = Message("had it", $"app@{Us}", 1);
        inbox.AlreadyHeld.Add(held.Id);
        var offered = Message(new string('o', 300), $"app@{Us}", 2);
        var inline = Message("inline", $"app@{Us}", 3);

        await peer.WriteLineAsync(Rules(), Ct);
        await peer.WriteAsync(Encoding.UTF8.GetBytes(Line("ihave", held) + Line("ihave", offered)), Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {held.Id}", "we have it already");
        (await peer.ReadLineAsync(Ct)).Should().Be($"send {offered.Id}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {offered.Id}\n"), .. offered.Payload,
            .. Encoding.UTF8.GetBytes(Line("msg", inline)), .. inline.Payload], Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {offered.Id}");
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {inline.Id}");

        inbox.Texts.Should().Equal(new string('o', 300), "inline");
        inbox.Sources.Should().AllBe(Them);
    }

    [Fact]
    public async Task APayloadThatDoesntHashToItsId_IsAnsweredBad()
    {
        var inbox = new RecordingInbox();
        var (session, peer, _) = await CalleeAsync(inbox: inbox);
        var m = Message("hello", $"app@{Us}");
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteAsync([.. Encoding.UTF8.GetBytes(Line("msg", m)), .. "hellO"u8.ToArray()], Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be($"bad {m.Id}");
        inbox.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task AMessageWhosePayloadLengthCantBeRead_EndsTheSession()
    {
        var (session, peer, run) = await CalleeAsync();
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteLineAsync("msg abc1234 fmt=p dst=app@N0CALL", Ct);

        await run.WaitAsync(Patience, Ct);
        session.Established.Should().BeTrue("it ended because nothing after that line can be read in step");
    }

    [Fact]
    public async Task InExchangeMode_APromptAndUnknownLinesAreIgnored()
    {
        var inbox = new RecordingInbox();
        var (session, peer, _) = await CalleeAsync(inbox: inbox);
        var m = Message("still here", $"app@{Us}");
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteLineAsync("DAPPSv1>", Ct);
        await peer.WriteLineAsync("something new", Ct);
        await peer.SendMessageAsync(m, Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {m.Id}");
    }

    [Fact]
    public async Task OpenedIsCalledOnce_WhenBothRulesHaveCrossed()
    {
        var opened = 0;
        var (session, peer, _, _) = await CallerAsync(opened: _ => Interlocked.Increment(ref opened));
        await peer.WriteLineAsync("DAPPSv1>", Ct);
        await peer.ReadLineAsync(Ct);
        opened.Should().Be(0);

        await peer.WriteLineAsync(Rules(), Ct);
        await peer.WriteLineAsync(Rules("other9"), Ct);
        await peer.ReadLineAsync(Ct);   // our rules again, for the new tag

        opened.Should().Be(1);
    }
}
