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
        TimeSpan? maxLength = null, TimeSpan? inactivity = null)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var wire = new WriteRecordingStream(ours);
        var session = new ExchangeSession(wire, Them, dialled: true, settings ?? new ExchangeSettings(), inbox ?? new RecordingInbox(), NullLoggerFactory.Instance)
        {
            PromptWait = Short,
            MinQuiet = Short,
            MaxLength = maxLength ?? TimeSpan.FromMinutes(30),
            InactivityTimeout = inactivity ?? TimeSpan.FromMinutes(3),
            Opened = opened,
        };
        return (session, new LinePeer(theirs), session.RunAsync(Ct), wire);
    }

    /// <summary>A session that answered: the peer plays the caller.</summary>
    private async Task<(ExchangeSession Session, LinePeer Peer, Task Run)> CalleeAsync(
        ExchangeSettings? settings = null, IBackhaulInbox? inbox = null, TimeSpan? inactivity = null)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: false, settings ?? new ExchangeSettings(), inbox ?? new RecordingInbox(), NullLoggerFactory.Instance)
        {
            MinQuiet = Short,
            InactivityTimeout = inactivity ?? TimeSpan.FromMinutes(3),
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
    public async Task ACallerWhoseCallCrossedThePeers_SendsItsRulesAtOnce()
    {
        // Both ends dialled: neither is answering, so no prompt is coming,
        // and there's no need to wait for one.
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var crossed = new TaskCompletionSource();
        var session = new ExchangeSession(ours, Them, dialled: true, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance)
        {
            PromptWait = TimeSpan.FromSeconds(30),
            CrossedCall = crossed.Task,
        };
        _ = session.RunAsync(Ct);
        var peer = new LinePeer(theirs);
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull();

        crossed.SetResult();

        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ");
    }

    [Fact]
    public async Task ACallerThatHearsExchangeTrafficBeforeAnyPrompt_TakesIt_AndSendsItsRulesAtOnce()
    {
        // BPQ moved a link that was mid-exchange onto our new session: the
        // peer carries on as before, and our rules (with a new tag) have it
        // send its own and anything unanswered again.
        var inbox = new RecordingInbox();
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: true, new ExchangeSettings(), inbox, NullLoggerFactory.Instance)
        {
            PromptWait = TimeSpan.FromSeconds(30),
        };
        _ = session.RunAsync(Ct);
        var peer = new LinePeer(theirs);
        var m = Message("in flight when the link moved", $"app@{Us}");

        await peer.WriteLineAsync("ack 1234567", Ct);
        await peer.SendMessageAsync(m, Ct);

        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ");
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {m.Id}");
        inbox.Texts.Should().Equal("in flight when the link moved");
    }

    [Fact]
    public async Task ACallerHearingSomethingElseBeforeAnyPrompt_DoesNotTakeItForExchangeTraffic()
    {
        // "no such command" from whatever answered isn't an answer to one
        // of ours: no message id. So our rules don't go early, and it still
        // fails for want of a prompt.
        var (session, peer, run, wire) = await CallerAsync();

        await peer.WriteLineAsync("no such command", Ct);
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        session.Failure.Should().Contain("no DAPPSv1> prompt");
        wire.Writes.Should().NotContain(w => w.StartsWith("exchange ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallerWhoseRulesGetNothingAtAllBack_HangsUpAfterThreePromptWaits()
    {
        // No prompt, so it sent its rules, as for a crossed call; but
        // nothing came back at all: the call landed on a link the far node
        // had reset, where nothing will ever answer.
        var (session, peer, run, _) = await CallerAsync();
        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ", "no prompt came, so it sent its rules anyway");

        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        session.Failure.Should().Contain("nothing from");
        session.Established.Should().BeFalse();
    }

    [Fact]
    public async Task ACrossedCaller_WhosePeersRulesComeWithinThreePromptWaits_CarriesOn()
    {
        var (session, peer, run, _) = await CallerAsync();
        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ");

        await Task.Delay(500, Ct);   // more than one prompt wait (300 ms), less than three
        await peer.WriteLineAsync(Rules(), Ct);

        while (!session.Established && !run.IsCompleted) await Task.Delay(20, Ct);
        session.Established.Should().BeTrue();
        run.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ACallerWhoseRulesGetOnlyANodesReply_GivesUpAfterThePromptWait()
    {
        // BPQ leaves a link it has reset at the node's command prompt: our
        // rules went, and the node answered them as a command.
        var (session, peer, run, _) = await CallerAsync();
        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ", "no prompt came, so it sent its rules anyway");

        await peer.WriteLineAsync("AAA:N0AAA} Invalid command - Enter ? for command list", Ct);

        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        session.Failure.Should().Contain("no exchange from");
        session.Established.Should().BeFalse();
    }

    [Fact]
    public async Task ACallerHearingDappsTrafficAmongTheOtherText_KeepsTheSession()
    {
        // The link moved mid-message: the tail of one arrives as text, then
        // the peer carries on in DAPPS. Its rules follow once it sees ours.
        var inbox = new RecordingInbox();
        var (session, peer, run, _) = await CallerAsync(inbox: inbox);
        (await peer.ReadLineAsync(Ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ");

        await peer.WriteLineAsync("the lazy dog", Ct);
        var m = Message("after the cut", $"app@{Us}");
        await peer.SendMessageAsync(m, Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {m.Id}");

        await Task.Delay(Short * 3, Ct);
        run.IsCompleted.Should().BeFalse("it's talking to a DAPPS node");
        await peer.WriteLineAsync(Rules(), Ct);
        await Task.Delay(Short, Ct);
        session.Established.Should().BeTrue();
    }

    [Fact]
    public async Task ACallerThatHeardABannerBeforeThePrompt_IsNotHurriedAfterItsRules()
    {
        // The banner came before the prompt, not in answer to our rules.
        var (session, peer, run, _) = await CallerAsync();
        await peer.WriteLineAsync("Welcome to the node", Ct);
        await peer.WriteLineAsync("DAPPSv1>", Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");

        await Task.Delay(Short * 3, Ct);
        run.IsCompleted.Should().BeFalse();
        await peer.WriteLineAsync(Rules(), Ct);
        await Task.Delay(Short, Ct);
        session.Established.Should().BeTrue();
    }

    [Fact]
    public async Task TheAnsweringNode_TakesAnswersBeforeTheCallersRules_WithoutHangingUp()
    {
        // As when BPQ moves a link that was mid-exchange onto a new inbound
        // session: the peer's answers to the old session arrive first.
        var inbox = new RecordingInbox();
        var (session, peer, run) = await CalleeAsync(inbox: inbox);

        await peer.WriteLineAsync("ack 1234567\nsend 7654321\nno 1111111 not wanted", Ct);
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("no eh?, and no hang-up");

        var m = Message("after the rules", $"app@{Us}");
        await peer.WriteLineAsync(Rules(), Ct);
        await peer.SendMessageAsync(m, Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {m.Id}");
        run.IsCompleted.Should().BeFalse();
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
    public async Task ASessionWeDialledThatBreaksOff_FailsItsOldestUnanswered_AndDefersTheRest()
    {
        // The link carried the exchange and then dropped before any answer
        // (a marginal link, or the peer failing on what we sent). Failing
        // one gives the neighbour a cooldown: deferring everything would
        // have the forwarder dial straight back into the same failure.
        var first = Message("first", $"app@{Them}", 1);
        var second = Message("second", $"app@{Them}", 2);
        var batch = new RecordingBatch(first, second);
        var (session, peer, run, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        peer.Close();
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted, o.Result.Deferred)).Should().Equal(
            (first.Id, false, false), (second.Id, false, true));
        batch.Outcomes[0].Result.Error.Should().Contain("broke off");
        session.Established.Should().BeTrue();
    }

    [Fact]
    public async Task ASessionTheyDialledThatBreaksOff_DefersEverything()
    {
        // The caller's side of it fails its own; ours just waits for the
        // next session, whoever makes it.
        var (session, peer, run) = await CalleeAsync();
        await peer.WriteLineAsync(Rules(), Ct);
        var m = Message("for you", $"app@{Them}");
        var batch = new RecordingBatch(m);
        while (!session.Established) await Task.Delay(10, Ct);
        session.TryTake(batch);
        await peer.ReadWithPayloadAsync(Ct);

        peer.Close();
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue();
    }

    [Fact]
    public async Task APeersQuit_WithOursUnanswered_DefersThem()
    {
        var m = Message("in flight", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, run, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        await peer.WriteLineAsync("quit", Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be("bye");
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue("a quit is a clean end, not a failure");
    }

    [Fact]
    public async Task WorkHandedToTheSessionButNotStarted_IsDeferredWhenItEnds()
    {
        // The window is full, so the second batch is still waiting when the
        // link goes: each of its messages still gets an outcome (a flood
        // copy has no other chance of one).
        var first = Enumerable.Range(1, ExchangeSession.DefaultWindow).Select(i => Message($"message {i}", $"app@{Them}", i)).ToArray();
        var waiting = Message("still waiting", $"app@{Them}", 99);
        var batch = new RecordingBatch(first);
        var later = new RecordingBatch(waiting);
        var (session, peer, run, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        for (var i = 0; i < first.Length; i++) await peer.ReadWithPayloadAsync(Ct);
        session.TryTake(later).Should().BeTrue();

        peer.Close();
        await run.WaitAsync(Patience, Ct);

        later.Outcomes.Single().Result.Deferred.Should().BeTrue();
        batch.Outcomes.Should().HaveCount(first.Length);
        session.TryTake(new RecordingBatch(waiting)).Should().BeFalse("an ended session takes no more work");
    }

    [Fact]
    public async Task AnswersArrivingAfterOurQuit_StillCount_AndPayloadsArriveThenAreReadPast()
    {
        var m = Message("in flight", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, run, _) = await CallerAsync(maxLength: TimeSpan.FromSeconds(1));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be("quit");

        // Sent before the peer saw our quit: a message of its own, whose
        // payload happens to hold a line that reads "bye", then the ack.
        var theirs = new BackhaulMessage("tricky1", $"app@{Us}", 5L, 600, Encoding.UTF8.GetBytes("x\nbye\n"));
        await peer.SendMessageAsync(theirs, Ct);
        await peer.WriteLineAsync($"ack {m.Id}\nbye", Ct);
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Accepted.Should().BeTrue("its ack came before the real bye");
    }

    [Fact]
    public async Task APeerRestartWithAPayloadWeAskedForOutstanding_ForgetsIt_SoTheSessionCanStillEndOnQuiet()
    {
        var (session, peer, run, _) = await CallerAsync();
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules("old001") + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        var offered = Message(new string('o', 400), $"app@{Us}");
        await peer.WriteLineAsync(Line("ihave", offered).TrimEnd('\n'), Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"send {offered.Id}");

        // The old session object won't send it now; the new one would
        // offer it again itself.
        await peer.WriteLineAsync(Rules("new002"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");

        (await peer.ReadLineAsync(Ct)).Should().Be("quit", "nothing is left waiting, so the quiet spell ends it");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task AMessageThatNeverGetsAnAnswer_FailsAfterTheAnswerTimeout_AndTheSessionEnds()
    {
        // e.g. the peer couldn't read its id and said `error ??`, and has
        // answered nothing since.
        var unanswerable = Message("never answered", $"app@{Them}", 1);
        var answered = Message("answered", $"app@{Them}", 2);
        var batch = new RecordingBatch(unanswerable, answered);
        var (session, peer, run, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60), inactivity: TimeSpan.FromSeconds(1));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);
        await peer.WriteLineAsync($"error ??\nack {answered.Id}", Ct);

        await batch.WaitForOutcomesAsync(2, Ct);
        await run.WaitAsync(Patience, Ct);

        var failed = batch.Outcomes.Single(o => o.Id == unanswerable.Id).Result;
        failed.Accepted.Should().BeFalse();
        failed.Deferred.Should().BeFalse();
        failed.Error.Should().Contain("no answer");
    }

    [Fact]
    public async Task ALinkThatMovesNeitherWay_Ends_FailingTheOldestAndDeferringTheRest()
    {
        // Ours went, and then nothing: no answers and no traffic of the
        // peer's own. At the edge of range BPQ can resend the same frames
        // for as long as the link stays up. At either end, a new link
        // starts afresh; one failure, one cooldown.
        var (session, peer, run) = await CalleeAsync(inactivity: TimeSpan.FromSeconds(1));
        await peer.WriteLineAsync(Rules(), Ct);
        var ours = Enumerable.Range(1, 3).Select(i => Message($"unanswered {i}", $"app@{Them}", i)).ToArray();
        var batch = new RecordingBatch(ours);
        session.TryTake(batch);
        foreach (var _ in ours) await peer.ReadWithPayloadAsync(Ct);

        await batch.WaitForOutcomesAsync(3, Ct);
        await run.WaitAsync(Patience, Ct);
        batch.Outcomes.Single(o => o.Id == ours[0].Id).Result.Error.Should().Contain("no answer");
        batch.Outcomes.Where(o => o.Id != ours[0].Id).Should().AllSatisfy(o => o.Result.Deferred.Should().BeTrue());
    }

    [Fact]
    public async Task APeerStillSendingItsOwnMail_KeepsTheSession_ThoughOursWaitLongerThanTheTimeout()
    {
        // Near the edge at 1200 baud the peer's answers queue behind its
        // own mail for minutes, while both ways are moving.
        var inbox = new RecordingInbox();
        var ours = Message("waiting behind the peer's mail", $"app@{Them}", 1);
        var batch = new RecordingBatch(ours);
        var (session, peer, run, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60), inbox, inactivity: TimeSpan.FromSeconds(1));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        for (var i = 0; i < 6; i++)
        {
            await peer.SendMessageAsync(Message($"theirs {i}", $"app@{Us}", 10 + i), Ct);
            await Task.Delay(400, Ct);
        }
        run.IsCompleted.Should().BeFalse("its mail kept arriving for 2.4 s, more than twice the timeout");

        await peer.WriteLineAsync($"ack {ours.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task APeerTricklingALongMessage_ThenAnsweringOurs_KeepsTheSession()
    {
        // One long message of the peer's takes longer than the timeout to
        // arrive; our answer comes after it.
        var inbox = new RecordingInbox();
        var ours = Message("answered after the long one", $"app@{Them}", 1);
        var batch = new RecordingBatch(ours);
        var (session, peer, run, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60), inbox, inactivity: TimeSpan.FromSeconds(1));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        var longer = Message(new string('l', 1200), $"app@{Us}", 2);
        var bytes = (byte[])[.. Encoding.UTF8.GetBytes(Line("msg", longer)), .. longer.Payload];
        for (var offset = 0; offset < bytes.Length; offset += 100)
        {
            await peer.WriteAsync(bytes[offset..Math.Min(bytes.Length, offset + 100)], Ct);
            await Task.Delay(200, Ct);   // 2.6 s in all
        }
        (await peer.ReadLineAsync(Ct)).Should().Be($"ack {longer.Id}");
        await Task.Delay(300, Ct);
        run.IsCompleted.Should().BeFalse();

        await peer.WriteLineAsync($"ack {ours.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
        inbox.Texts.Should().Equal(new string('l', 1200));
    }

    [Fact]
    public async Task OnASlowLink_MessagesStillBeingAnswered_DontTimeOut()
    {
        // A full window can take longer than the answer timeout to go on
        // air at 1200 baud. While answers keep coming, the link is working.
        var messages = Enumerable.Range(1, 3).Select(i => Message($"slow {i}", $"app@{Them}", i)).ToArray();
        var batch = new RecordingBatch(messages);
        var (session, peer, _, _) = await CallerAsync(new ExchangeSettings(HoldSeconds: 60), inactivity: TimeSpan.FromSeconds(2));
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        foreach (var _ in messages) await peer.ReadWithPayloadAsync(Ct);

        // Answered 1.2 s apart: the last comes 3.6 s after all three went.
        foreach (var m in messages)
        {
            await Task.Delay(1200, Ct);
            await peer.WriteLineAsync($"ack {m.Id}", Ct);
        }

        await batch.WaitForOutcomesAsync(3, Ct);
        batch.Outcomes.Should().AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
    }

    [Fact]
    public async Task TwoEnds_EachSendingMoreThanAWindowAtOnce_GetEverythingThrough()
    {
        var (a, b) = await LoopbackPairAsync(Ct);
        var inboxA = new RecordingInbox();
        var inboxB = new RecordingInbox();
        var fromA = Enumerable.Range(1, 20).Select(i => Message($"from A {i} " + new string('a', i * 20), $"app@{Them}", i)).ToArray();
        var fromB = Enumerable.Range(1, 20).Select(i => Message($"from B {i} " + new string('b', i * 20), $"app@{Us}", 100 + i)).ToArray();
        var batchA = new RecordingBatch(fromA);
        var batchB = new RecordingBatch(fromB);
        var caller = new ExchangeSession(a, Them, dialled: true, new ExchangeSettings(), inboxA, NullLoggerFactory.Instance) { MinQuiet = Short };
        var callee = new ExchangeSession(b, Us, dialled: false, new ExchangeSettings(), inboxB, NullLoggerFactory.Instance)
        {
            Opened = s => s.TryTake(batchB),
        };
        caller.TryTake(batchA);

        await Task.WhenAll(caller.RunAsync(Ct), callee.RunAsync(Ct)).WaitAsync(Patience, Ct);

        inboxB.Texts.Should().Equal(fromA.Select(m => Encoding.UTF8.GetString(m.Payload)), "in the order they were sent");
        inboxA.Texts.Should().Equal(fromB.Select(m => Encoding.UTF8.GetString(m.Payload)));
        batchA.Outcomes.Should().HaveCount(20).And.AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
        batchB.Outcomes.Should().HaveCount(20).And.AllSatisfy(o => o.Result.Accepted.Should().BeTrue());
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

    // ---- Out of step (#207) ----

    [Fact]
    public async Task APayloadThatDoesntHashToItsId_EndsTheSessionWithAQuit()
    {
        // At the edge of range BPQ can hand over a stale frame in place of
        // the one sent: nothing after it can be trusted, so the session
        // ends and the next link starts in step, rather than answering bad.
        var inbox = new RecordingInbox();
        var hashMismatches = new List<string>();
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var session = new ExchangeSession(ours, Them, dialled: false, new ExchangeSettings(), inbox, NullLoggerFactory.Instance)
        {
            HashMismatch = hashMismatches.Add,
        };
        var run = session.RunAsync(Ct);
        var peer = new LinePeer(theirs);
        await peer.ReadLineAsync(Ct);
        await peer.ReadLineAsync(Ct);
        var m = Message("hello", $"app@{Us}");
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteAsync([.. Encoding.UTF8.GetBytes(Line("msg", m)), .. "hellO"u8.ToArray()], Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
        inbox.Messages.Should().BeEmpty();
        hashMismatches.Should().Equal(m.Id);
    }

    [Fact]
    public async Task ACompressedPayloadThatDoesntDecode_EndsTheSessionWithAQuit()
    {
        var inbox = new RecordingInbox();
        var (session, peer, run) = await CalleeAsync(inbox: inbox);
        await peer.WriteLineAsync(Rules(), Ct);
        var payload = Encoding.UTF8.GetBytes(WpsPost);
        var id = DappsMessage.ComputeHash(payload, 1L)[..7];

        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"msg {id} len={payload.Length} fmt=z1 clen=12 s=1 dst=app@{Us}\n"), .. "not zstd at!"u8.ToArray()], Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
        inbox.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforeTheExchange_APayloadThatDoesntHashToItsId_IsStillAnsweredBad()
    {
        // A simple sender pushing one message the old way expects bad.
        var inbox = new RecordingInbox();
        var (session, peer, run) = await CalleeAsync(inbox: inbox);
        var m = Message("hello", $"app@{Us}");

        await peer.WriteLineAsync(Line("ihave", m), Ct);
        (await peer.ReadLineAsync(Ct)).Should().Be($"send {m.Id}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {m.Id}\n"), .. "hellO"u8.ToArray()], Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be($"bad {m.Id}");
        run.IsCompleted.Should().BeFalse();
        inbox.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task AGarbledLine_EndsASessionWeDialled_WithAQuit_AndFailsNothing()
    {
        // The line seen at 156.75 dB: the start of an ihave replaced by the
        // tail of a stale frame. The link was working, so what we had in
        // flight waits for the next session without a cooldown.
        var first = Message("first", $"app@{Them}", 1);
        var second = Message("second", $"app@{Them}", 2);
        var batch = new RecordingBatch(first, second);
        var (session, peer, run, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        await peer.WriteLineAsync($"ack {first.Id}", Ct);
        await peer.WriteLineAsync("0AAAe 3b1f348 len=3113 fmt=z1 clen=721 dst=soak@N0CALL s=1790533250012 ttl=3267 src=N0DEST", Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted, o.Result.Deferred)).Should().Equal(
            (first.Id, true, false), (second.Id, false, true));
        batch.Outcomes[1].Result.Error.Should().Contain("out of step");
    }

    [Theory]
    [InlineData("ack 48\u00b54464")]
    [InlineData("no \u0001")]
    [InlineData("send")]
    [InlineData("\u0012\u00b5/\u00fd binary")]
    [InlineData("SEND 1234567")]
    public async Task InExchangeMode_ALineThatIsntDapps_EndsTheSession(string line)
    {
        var (session, peer, run) = await CalleeAsync();
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteLineAsync(line, Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
    }

    [Theory]
    [InlineData("ihave")]
    [InlineData("ihave \u00b5b1f348 len=5 fmt=p dst=app@N0CALL")]
    [InlineData("msg 3b\u00011f348 len=5 fmt=p dst=app@N0CALL")]
    public async Task InExchangeMode_AnOfferOrMessageWithoutAMessageId_EndsTheSession(string line)
    {
        // Nothing we could answer would name it, so it would wait for ever.
        var (session, peer, run) = await CalleeAsync();
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteLineAsync(line, Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task AMessageWhosePayloadLengthCantBeRead_EndsTheSession()
    {
        var (session, peer, run) = await CalleeAsync();
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.WriteLineAsync("msg abc1234 fmt=p dst=app@N0CALL", Ct);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await run.WaitAsync(Patience, Ct);
        session.Established.Should().BeTrue("it ended because nothing after that line can be read in step");
    }

    [Fact]
    public async Task ALineCutOffByTheLinkGoing_IsTheLinkGoing_NotAGarbledLine()
    {
        // What a hang-up leaves of a line is only its start; a session we
        // dialled that breaks off still fails its oldest, as for any break.
        var m = Message("in flight", $"app@{Them}");
        var batch = new RecordingBatch(m);
        var (session, peer, run, _) = await CallerAsync();
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules() + "\n"), Ct);
        await peer.ReadLineAsync(Ct);
        await peer.ReadWithPayloadAsync(Ct);

        await peer.WriteAsync([0xb5, (byte)'s'], Ct);
        peer.Close();
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Error.Should().Contain("broke off");
    }

    [Fact]
    public async Task TwoSessions_APayloadDamagedOnTheWay_EndsTheSessionAtBothEnds_AndGoesAgainNextTime()
    {
        // Our own sender at the other end: it hears the quit, defers what
        // it had without a cooldown, and the next session delivers it.
        var m = Message("across a bad patch", $"app@{Us}");
        var batch = new RecordingBatch(m);
        var inbox = new RecordingInbox();

        var (a1, b1) = await LoopbackPairAsync(Ct);
        var sender = new ExchangeSession(new DamageFirstPayload(a1), Us, dialled: true, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance)
        {
            PromptWait = Short,
            MinQuiet = Short,
        };
        var receiver = new ExchangeSession(b1, Them, dialled: false, new ExchangeSettings(), inbox, NullLoggerFactory.Instance) { MinQuiet = Short };
        sender.TryTake(batch);
        await Task.WhenAll(sender.RunAsync(Ct), receiver.RunAsync(Ct)).WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Deferred.Should().BeTrue("the receiver ended the session with a quit");
        inbox.Messages.Should().BeEmpty();

        var again = new RecordingBatch(m);
        var (a2, b2) = await LoopbackPairAsync(Ct);
        var sender2 = new ExchangeSession(a2, Us, dialled: true, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance)
        {
            PromptWait = Short,
            MinQuiet = Short,
        };
        var receiver2 = new ExchangeSession(b2, Them, dialled: false, new ExchangeSettings(), inbox, NullLoggerFactory.Instance) { MinQuiet = Short };
        sender2.TryTake(again);
        await Task.WhenAll(sender2.RunAsync(Ct), receiver2.RunAsync(Ct)).WaitAsync(Patience, Ct);

        again.Outcomes.Single().Result.Accepted.Should().BeTrue();
        inbox.Texts.Should().Equal("across a bad patch");
    }

    /// <summary>Flips the last byte of the first write that carries a
    /// <c>msg</c>: its payload, as a stale frame in its place would.</summary>
    private sealed class DamageFirstPayload(Stream inner) : Stream
    {
        private bool damaged;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!damaged && Encoding.UTF8.GetString(buffer.Span).Contains("msg ", StringComparison.Ordinal))
            {
                damaged = true;
                var copy = buffer.ToArray();
                copy[^1] ^= 0x20;
                return inner.WriteAsync(copy, ct);
            }
            return inner.WriteAsync(buffer, ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AReceiverWhoseInboxFails_EndsTheSession_WithoutAnAck()
    {
        // Storing it failed, so it mustn't be acked: the sender keeps it,
        // and a session it dialled fails it and backs off.
        var (session, peer, run) = await CalleeAsync(inbox: new FailingInbox());
        await peer.WriteLineAsync(Rules(), Ct);

        await peer.SendMessageAsync(Message("poison", $"app@{Us}"), Ct);

        await run.WaitAsync(Patience, Ct);
        (await peer.TryReadLineAsync(Short, Ct)).Should().BeNull("no ack went");
    }

    private sealed class FailingInbox : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct) =>
            throw new InvalidOperationException("the database is unwell");
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
