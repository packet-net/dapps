using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// The inbound AGW session seam, end to end against a scripted BPQ:
/// <see cref="AgwInboundService"/> talking real AGW frames over a real
/// (loopback) socket to a <see cref="FakeAgwSocket"/> that plays the
/// node, with every frame dapps sends recorded and every line it logs
/// captured.
///
/// AGW frames carry no session id. A session is the (local, remote,
/// port) triple, BPQ reuses it the instant a peer redials, and - the
/// part that bit in the field - BPQ stamps its disconnect notification
/// with the port of the last frame it *received from us*, not the
/// session's port (AGWAPI.c, SendDisMsgtoAppl). After a 'G' keepalive
/// that is port 0 whatever port the session was on. These tests pin
/// down each consequence:
///
///   - a 'd' whose port matches nothing still closes the session it
///     can only mean (resolved by callsign pair),
///   - a 'C' for a pair we still hold an entry for retires that entry
///     and the new session is fully usable, not merely prompted,
///   - a session dapps did not close never emits a 'd' of its own,
///     because BPQ would apply it to the pair's newer session,
///   - the genuinely ambiguous case (one pair up on two ports) is left
///     alone rather than guessed at,
///   - keepalive and reconnect cadences, driven by a fake clock.
///
/// Every wait here is on a frame, a log line or a fake-clock timer;
/// none is a sleep that hopes the service got there first.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AgwInboundSessionSeamTests : IAsyncLifetime
{
    private const string Remote = "M0AHN-3";
    private const string Local = "G5ALF-3";
    /// <summary>AGW port index 1 = BPQ port 2, the RF/AXIP port in the field logs.</summary>
    private const byte Port = 1;

    /// <summary>The node's own number for AGW port <see cref="Port"/>, by
    /// which sessions know their link (#233): BPQ's port 2.</summary>
    private const int Number = Port + 1;
    /// <summary>What a new session sends first: the prompt, and the
    /// node's rules straight after it, in one write.</summary>
    private const string Prompt = "DAPPSv1>\nexchange id=";

    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-seam-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RemoteDisconnect_StampedWithTheWrongPort_StillClosesTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var prompt = await bpq.ReadUntilAsync('D', ct);
        Encoding.UTF8.GetString(prompt.Payload).Should().StartWith(Prompt);
        prompt.Port.Should().Be(Port, "dapps writes on the port the session came in on");
        prompt.CallFrom.Should().Be(Local);
        prompt.CallTo.Should().Be(Remote);

        // BPQ's disconnect notification carries the port of the last
        // frame it received from us. After a keepalive that is 0.
        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await h.Logs.WaitForAsync("session closed", ct);
        // The port fallback is logged so a field log shows which rule
        // matched. The service writes that line just after "session
        // closed", on its own thread, so wait for it too: checking at once
        // raced the service on a slow runner (3 to 6 runs in 50 under load).
        await h.Logs.WaitForAsync("carried port 0 but the session was on port 1", ct);

        // The pair is free again: the redial is a clean new session, not
        // a stale-entry rescue.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        await bpq.DrainAsync(ct);
        h.Logs.Warnings.Should().BeEmpty("the 'd' was matched by callsign pair, so nothing ever went stale");
        bpq.Received.Where(f => f.Kind == 'd').Should().BeEmpty(
            "the peer closed the first session; a 'd' from dapps would tear down the second one");
    }

    [Theory]
    [InlineData(Port, "the 'd' carries the session's own port")]
    [InlineData((byte)0, "the 'd' carries the port of our last keepalive, as BPQ really sends it")]
    public async Task HangupThenImmediateRedial_OneHundredTimes_EverySessionIsPrompted(byte disconnectPort, string because)
    {
        const int reconnects = 100;
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        var connect = FakeAgwSocket.Connect(Remote, Local, Port);
        var hangup = FakeAgwSocket.Disconnect(Remote, Local, disconnectPort);

        await bpq.WriteAsync(ct, connect);
        for (var i = 0; i <= reconnects; i++)
        {
            (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt, $"session {i} should be prompted when {because}");

            // Peer hangs up and redials in the same TCP write, the way
            // BPQ's poll loop flushes both when a client redials at once.
            if (i < reconnects) await bpq.WriteAsync(ct, hangup, connect);
            else await bpq.WriteAsync(ct, hangup);
        }

        await bpq.DrainAsync(ct);
        bpq.Received.Where(f => f.Kind == 'd').Should().BeEmpty(
            "the peer closed every session first; a 'd' from dapps can only hit a newer session on the same pair");
        h.Logs.Warnings.Should().BeEmpty("every 'd' was matched, so no entry was ever stale");
    }

    [Fact]
    public async Task ConnectForAPairStillRegistered_RetiresTheStaleEntry_AndTheNewSessionIsFullyUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        // No 'd' ever arrives for that session. BPQ refuses live
        // duplicates itself, so when it dispatches the peer's next
        // connect for the pair, our entry can only be stale.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt, "the new connect is legitimate and must not be rejected");
        var warning = await h.Logs.WaitForAsync(e => e.Level == LogLevel.Warning, ct);
        warning.Message.Should().Contain("retiring the stale entry");

        // Live end to end, not just prompted: a command gets its reply.
        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "quit\n"));
        (await bpq.ReadTextAsync(ct)).Should().Be("bye\n");

        // dapps ended that session itself, so exactly one 'd' goes out,
        // for the live session. The retired one was marked remote-closed
        // and must stay silent.
        var d = await bpq.ReadUntilAsync('d', ct);
        d.Port.Should().Be(Port);
        d.CallFrom.Should().Be(Local);
        d.CallTo.Should().Be(Remote);
        await bpq.DrainAsync(ct);
        bpq.Received.Count(f => f.Kind == 'd').Should().Be(1);
    }

    // #178: the forwarder consults PeerSessionRegistry so it never dials
    // a peer that is already connected to us, which would reset the link.
    // These pin down what the inbound side puts into it, and when.

    [Fact]
    public async Task WhileAPeerHasASessionOpen_ItIsRegisteredAsBusy_UntilTheSessionEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        peers.IsActive(Remote, out _).Should().BeFalse();

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        peers.IsActive(Remote, out var direction).Should().BeTrue("the session is open from the moment BPQ hands it to us");
        direction.Should().Be("inbound");

        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, Port));
        await peers.WaitUntilIdleAsync(Remote, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    [Fact]
    public async Task ADeclaredNodeIdentity_RekeysTheActualInboundBearerLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect("GB7LOX-6", Local, Port));
        var prompt = await bpq.ReadTextAsync(ct);
        prompt.Should().Contain($"node={Local}");
        peers.IsActive("GB7LOX-6", out _).Should().BeTrue();
        peers.IsActive("GB7LOX", out _).Should().BeFalse();
        await bpq.WriteAsync(ct, FakeAgwSocket.Data("GB7LOX-6", Local, Port,
            "exchange id=lox01 inline=256 node=GB7LOX\nping identified\n"));
        (await bpq.ReadTextAsync(ct)).Should().Be("pong identified\n");

        peers.IsActive("GB7LOX-6", out _).Should().BeFalse();
        peers.TryAcquire("GB7LOX", "outbound", out var direction, Number).Should().BeNull();
        direction.Should().Be("inbound");
        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect("GB7LOX-6", Local, Port));
        await peers.WaitUntilIdleAsync("GB7LOX", ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeclarationCrossingOurDial_RetiresTheOlderLinkWithoutDisconnectingTheWinner(bool dialConnectsFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect("GB7LOX-6", Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        using var outbound = peers.TryAcquire("GB7LOX", "outbound", out _, Number)!;
        if (dialConnectsFirst) peers.Connected(outbound);

        await bpq.WriteAsync(ct, FakeAgwSocket.Data("GB7LOX-6", Local, Port,
            "exchange id=lox01 inline=256 node=GB7LOX\nping identified\n"));
        if (!dialConnectsFirst)
        {
            (await bpq.ReadTextAsync(ct)).Should().Be("pong identified\n");
            peers.Connected(outbound).Should().BeTrue();
        }
        await h.Logs.WaitForAsync("retired: a newer session", ct);
        await peers.WaitUntilIdleAsync("GB7LOX-6", ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        outbound.Retired.IsCancellationRequested.Should().BeFalse();
        peers.AllSessions().Should().ContainSingle(s => s.Peer == "GB7LOX" && s.Session.Direction == "outbound");
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd', "retiring the inbound must not disconnect the newer link");
    }

    [Fact]
    public async Task AfterAStaleEntryIsRetired_ThePeerIsStillBusyForTheLiveSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        await h.Logs.WaitForAsync("retiring the stale entry", ct);
        peers.IsActive(Remote, out _).Should().BeTrue("the live replacement holds a lease of its own");

        // dapps closes the live session itself; that releases the peer.
        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "quit\n"));
        (await bpq.ReadTextAsync(ct)).Should().Be("bye\n");
        await peers.WaitUntilIdleAsync(Remote, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    // One session per peer: our dial to a peer that has just called us
    // goes out on the one connection the peer's call came in on, and BPQ
    // confirms it over the link that is already up. The inbound session
    // is then retired: out of the table, no 'd' from it (BPQ would apply that
    // to the link, now the newer session's), and its handler ended. Until
    // our dial has connected it says nothing at all (#205): the peer's BPQ
    // keeps its call up and shares the link with ours only if nothing has
    // reached it on the link yet. A prompt there makes it reset the link.

    [Fact]
    public async Task OurDialConnectingForAPeer_RetiresItsInboundSession_WhichNeverSaidAWord()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        var dialling = peers.TryAcquire(Remote, "outbound", out _, linkPort: Number)!;   // on its way already

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await h.Logs.WaitForAsync("holding the prompt", ct);
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'D', "no prompt while our own call is on its way");

        peers.Connected(dialling);
        await h.Logs.WaitForAsync("retired", ct);

        // Data for the pair has nowhere to go now, and nothing answers it.
        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "help\n"));
        var sent = await bpq.DrainAsync(ct, TimeSpan.FromMilliseconds(500));
        sent.Should().NotContain(f => f.Kind == 'd', "a retired session never disconnects the link");
        sent.Should().NotContain(f => f.Kind == 'D', "a retired session sends nothing");
        bpq.Received.Should().NotContain(f => f.Kind == 'D', "the held session never prompted");
        peers.IsActive(Remote, out var direction).Should().BeTrue();
        dialling.Dispose();
    }

    [Fact]
    public async Task OurDialGivingUp_LetsTheHeldInboundSessionAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        var dialling = peers.TryAcquire(Remote, "outbound", out _, linkPort: Number)!;
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await h.Logs.WaitForAsync("holding the prompt", ct);

        dialling.Dispose();   // BPQ refused our connect, say

        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePeerHangingUpDuringTheHold_EndsIt_AndNothingIsSentWhenOurDialThenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        var dialling = peers.TryAcquire(Remote, "outbound", out _, linkPort: Number)!;
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await h.Logs.WaitForAsync("holding the prompt", ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, Port));
        await h.Logs.WaitForAsync("hung up while we held the prompt", ct);
        dialling.Dispose();

        var sent = await bpq.DrainAsync(ct, TimeSpan.FromMilliseconds(500));
        sent.Should().NotContain(f => f.Kind == 'D', "a prompt to a pair that has hung up would reach nothing, or a newer session");
        sent.Should().NotContain(f => f.Kind == 'd', "the peer closed it; a 'd' from us could only find a newer session");
        h.Logs.Any("Inbound session from").Should().BeFalse();
    }

    [Fact]
    public async Task OurDialNotConnectingInTime_TheHeldInboundSessionAnswersAnyway()
    {
        // By then the peer has given up waiting for a prompt; holding on
        // gains nothing.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, clock, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        using var dialling = peers.TryAcquire(Remote, "outbound", out _, linkPort: Number)!;
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await clock.WaitForTimerAsync(AgwInboundService.OwnCallHold, ct);
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'D');

        clock.Advance(AgwInboundService.OwnCallHold);

        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Any("hasn't connected after 10s; answering its call").Should().BeTrue();
    }

    [Fact]
    public async Task OurDialOnAnotherPort_DoesNotHoldThePrompt()
    {
        // Another port is another link: our SABM won't come down this one.
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        using var dialling = peers.TryAcquire(Remote, "outbound", out _, linkPort: Number + 1)!;

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));

        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Any("holding the prompt").Should().BeFalse();
    }

    [Fact]
    public async Task AnInboundConnect_RetiresAnOlderOutboundSessionWithThatPeer()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);
        using var ours = peers.Acquire(Remote, "outbound", linkPort: Number);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        ours.Retired.IsCancellationRequested.Should().BeTrue("the link is the inbound session's now");
    }

    [Fact]
    public async Task DataStampedWithTheWrongPort_ReachesTheOnlySessionForThatPair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, port: 0, "quit\n"));

        var bye = await bpq.ReadUntilAsync('D', ct);
        Encoding.UTF8.GetString(bye.Payload).Should().Be("bye\n");
        bye.Port.Should().Be(Port, "the reply goes out on the session's real port, whatever the inbound frame said");
    }

    [Fact]
    public async Task SamePairOnTwoPorts_APortlessDisconnectIsIgnored_AndBothSessionsStayUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct,
            FakeAgwSocket.Connect(Remote, Local, port: 1),
            FakeAgwSocket.Connect(Remote, Local, port: 2));
        var first = await bpq.ReadUntilAsync('D', ct);
        var second = await bpq.ReadUntilAsync('D', ct);
        // Both sessions are prompted; the two handlers race, so the order is not fixed.
        new[] { first.Port, second.Port }.Order().Should().Equal(new byte[] { 1, 2 });

        // Which session does a port-0 'd' mean? Genuinely unknowable, so
        // neither is touched.
        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await h.Logs.WaitForAsync("'d' for unknown session", ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, port: 1, "quit\n"));
        var bye1 = await bpq.ReadUntilAsync('D', ct);
        Encoding.UTF8.GetString(bye1.Payload).Should().Be("bye\n");
        bye1.Port.Should().Be(1);

        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, port: 2, "quit\n"));
        var bye2 = await bpq.ReadUntilAsync('D', ct);
        Encoding.UTF8.GetString(bye2.Payload).Should().Be("bye\n");
        bye2.Port.Should().Be(2);
    }

    [Fact]
    public async Task BpqEchoOfOurOwnDisconnect_IsIgnored_AndTheNextConnectIsClean()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "quit\n"));
        (await bpq.ReadTextAsync(ct)).Should().Be("bye\n");
        var ours = await bpq.ReadUntilAsync('d', ct);
        ours.Port.Should().Be(Port, "dapps closed this one, so it tells BPQ");

        // BPQ answers an application's 'd' with up to two of its own: "***
        // DISCONNECTED RETRYOUT With" at once, stamped with the port we
        // just used, and "*** DISCONNECTED From Station" once the link
        // has gone, stamped with whatever port we used last. The entry is
        // already gone; nothing to do.
        await bpq.WriteAsync(ct,
            new AgwFrame(Port, 'd', 0, Remote, Local, Encoding.ASCII.GetBytes($"*** DISCONNECTED RETRYOUT With {Remote}\r\0")),
            FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await h.Logs.WaitForCountAsync("the node's answer to our own 'd'", 2, ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Keepalive_GoesOutOncePerIntervalOfClockTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var bpq = await h.StartAsync(ct);

        clock.Advance(AgwInboundService.KeepaliveInterval - TimeSpan.FromSeconds(1));
        (await bpq.DrainAsync(ct, TimeSpan.FromMilliseconds(200))).Should().BeEmpty(
            "one second short of the interval nothing is due");

        clock.Advance(TimeSpan.FromSeconds(1));
        var g = await bpq.ReadFrameAsync(ct);
        g.Kind.Should().Be('G');
        g.Port.Should().Be(0, "and this is why BPQ's next 'd' to us will say port 0");
        await h.Logs.WaitForAsync("keepalive 'G' sent", ct);

        clock.Advance(AgwInboundService.KeepaliveInterval);
        (await bpq.ReadFrameAsync(ct)).Kind.Should().Be('G');

        // BPQ's reply to a 'G' is kept as the port list, and is nothing
        // to worry about.
        await bpq.WriteAsync(ct, new AgwFrame(0, 'G', 0, "", "", "2;Port1 Telnet;Port2 AXIP;"u8.ToArray()));
        await h.Logs.WaitForAsync("port list ('G') received", ct);
        h.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenBpqDropsTheSocket_DappsRedialsAfterTheFirstBackoffTier_OnTheClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var first = await h.StartAsync(ct);
        h.Service.TriggerManualRetry().Should().BeFalse("nothing to collapse while connected");

        first.Close();
        await h.Logs.WaitForAsync("(attempt 1,", ct);
        var firstTier = ReconnectBackoffSchedule.DelayForAttempt(1);
        await clock.WaitForTimerAsync(firstTier, ct);
        h.Host.Pending().Should().BeFalse("no redial before the backoff has elapsed");
        var counters = h.Metrics.Take();
        counters.AgwReconnectAttempt.Should().Be(1);
        counters.AgwReconnectNextAtUtc.Should().Be((clock.GetUtcNow() + firstTier).UtcDateTime,
            "the dashboard countdown is published from the same clock");

        clock.Advance(firstTier);
        using var second = await h.Host.AcceptAsync(ct);
        (await second.ExpectRegisterAsync(ct)).CallFrom.Should().Be(Local, "the new connection re-registers our callsign");

        // The node's answer to the 'X', not the bare TCP connect, is what
        // counts as a successful reconnect and resets the ramp.
        await second.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await h.Logs.WaitForCountAsync("'X' ack", 2, ct);
        h.Metrics.Take().AgwReconnectAttempt.Should().Be(0);
    }

    [Fact]
    public async Task RepeatedFailures_ClimbTheBackoffRamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var socket = await h.StartAsync(ct);

        // BPQ accepts the socket, sees our registration, and drops us
        // without a word, four times running.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            socket.Close();
            await h.Logs.WaitForAsync($"(attempt {attempt},", ct);
            var delay = ReconnectBackoffSchedule.DelayForAttempt(attempt);
            await clock.WaitForTimerAsync(delay, ct);
            h.Metrics.Take().AgwReconnectAttempt.Should().Be(attempt);

            clock.Advance(delay);
            socket = await h.Host.AcceptAsync(ct);
            await socket.ExpectRegisterAsync(ct);
        }

        ReconnectBackoffSchedule.DelayForAttempt(4).Should().Be(TimeSpan.FromSeconds(30),
            "the fourth consecutive failure moves to the second tier of the ramp");
        socket.Dispose();
    }

    [Fact]
    public async Task RetryNow_CollapsesTheBackoffWait_WithoutWaitingForTheClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var first = await h.StartAsync(ct);

        first.Close();
        await h.Logs.WaitForAsync("(attempt 1,", ct);
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);

        h.Service.TriggerManualRetry().Should().BeTrue("a backoff wait was in flight to collapse");
        h.Metrics.Take().AgwReconnectNextAtUtc.Should().BeNull("the countdown clears the moment the operator clicks");

        using var second = await h.Host.AcceptAsync(ct);
        await second.ExpectRegisterAsync(ct);
    }

    [Fact]
    public async Task AConfigSaveDuringTheBackoff_InterruptsTheWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var first = await h.StartAsync(ct);

        first.Close();
        await h.Logs.WaitForAsync("(attempt 1,", ct);
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);

        h.Options.Set(h.With(o => o.Callsign = "G5ALF-4"));

        using var second = await h.Host.AcceptAsync(ct);
        await second.ExpectRegisterAsync(ct);
    }

    [Fact]
    public async Task AConfigSaveWhileConnected_ReconnectsAfterTheShortNonFailureDelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        using var first = await h.StartAsync(ct);

        h.Options.Set(h.With(o => o.Callsign = "G5ALF-4"));

        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        h.Host.Pending().Should().BeFalse("a cancelled cycle is not a failure, but it still pauses briefly");
        h.Metrics.Take().AgwReconnectAttempt.Should().Be(0, "an options change never counts against the ramp");

        clock.Advance(AgwInboundService.NonFailureRetryDelay);
        using var second = await h.Host.AcceptAsync(ct);
        await second.ExpectRegisterAsync(ct);
    }

    /// <summary>Saves that change nothing the AGW connection is built
    /// from: the TX STOP and RESUME buttons, other settings, and the RHP
    /// bearer's own port.</summary>
    public static TheoryData<string> UnrelatedSaves => ["TxEnabled", "HeartbeatEnabled", "CompressionEnabled", "RhpPort", "nothing"];

    private static void ChangeUnrelated(SystemOptions o, string field)
    {
        switch (field)
        {
            case "TxEnabled": o.TxEnabled = !o.TxEnabled; break;
            case "HeartbeatEnabled": o.HeartbeatEnabled = !o.HeartbeatEnabled; break;
            case "CompressionEnabled": o.CompressionEnabled = !o.CompressionEnabled; break;
            case "RhpPort": o.RhpPort += 1; break;
            case "nothing": break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    [Theory]
    [MemberData(nameof(UnrelatedSaves))]
    public async Task ASaveThatChangesNothingTheConnectionUses_LeavesItAndItsLiveSessionsUp(string field)
    {
        // #273: any save used to cancel the cycle, closing the AGW socket,
        // so BPQ sent DISC on every live inbound link and nobody was
        // listening for the next 5 s.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        using var bpq = await h.StartAsync(ct);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        h.Options.Set(h.With(o => ChangeUnrelated(o, field)));

        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd', "the live session is not torn down");
        // The same socket still serves: a second caller is answered on it.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect("M0XYZ-1", Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Host.Pending().Should().BeFalse("the service never dialled again");
    }

    [Fact]
    public async Task AnUnrelatedSaveDuringTheBackoff_LeavesTheWaitAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var first = await h.StartAsync(ct);

        first.Close();
        await h.Logs.WaitForAsync("(attempt 1,", ct);
        var firstTier = ReconnectBackoffSchedule.DelayForAttempt(1);
        await clock.WaitForTimerAsync(firstTier, ct);

        h.Options.Set(h.With(o => o.TxEnabled = false));

        // A redial that does not happen cannot be awaited, and nothing the
        // service exposes says "a save was ignored", hence the short real-
        // time settle. A collapsed wait redials within milliseconds (this
        // test fails without the fix); a slow box can only make it pass
        // when it should fail, never the reverse.
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        h.Host.Pending().Should().BeFalse("retrying with the same settings sooner would not help");
        clock.Advance(firstTier);
        using var second = await h.Host.AcceptAsync(ct);
        await second.ExpectRegisterAsync(ct);
    }

    public static TheoryData<string> ConnectionSaves => ["Callsign", "NodeHost", "AgwPort", "NodeBearer"];

    [Theory]
    [MemberData(nameof(ConnectionSaves))]
    public async Task ASaveThatChangesWhatTheConnectionUses_StillRestartsIt(string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        using var bpq = await h.StartAsync(ct);

        h.Options.Set(h.With(o =>
        {
            switch (field)
            {
                case "Callsign": o.Callsign = "G5ALF-4"; break;
                case "NodeHost": o.NodeHost = "127.0.0.2"; break;
                case "AgwPort": o.AgwPort += 1; break;
                case "NodeBearer": o.NodeBearer = "rhpv2"; break;
                default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }));

        (await ReadUntilClosedAsync(bpq, ct)).Should().Match<Exception>(e => e is EndOfStreamException || e is IOException,
            "the service closed the connection built from the old settings");
        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        h.Metrics.Take().AgwReconnectAttempt.Should().Be(0, "an options change never counts against the ramp");
    }

    [Fact]
    public async Task AFrameWhoseHandlingFails_LeavesTheSocketAndTheOtherSessionsUp()
    {
        // #276: one consumer's exception used to end the read loop, and
        // with it the socket: BPQ then drops every inbound link at once.
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);

        // Handling this caller's connect throws part way through.
        h.Logs.ThrowWhen = e => e.Message.Contains("'C' from M0BAD-1", StringComparison.Ordinal);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect("M0BAD-1", Local, Port));
        await h.Logs.WaitForAsync("carrying on with the connection", ct);

        // The first session is untouched, and the socket still serves.
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd');
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect("M0XYZ-1", Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Host.Pending().Should().BeFalse("the service never had to dial again");
    }

    [Fact]
    public async Task AConnectWhoseSetupFails_IsCleanedUp_SoThePeerIsLetGoAndCanRedial()
    {
        // Failing after the session is in the table but before its handler
        // starts used to leave the half-made entry there and the peer's
        // lease held: the peer's link hung until it timed out, and its
        // redial was taken for a stale entry.
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);

        h.Logs.ThrowWhen = e => e.Message.Contains($"session {Local}<->{Remote} on port {Port} registered", StringComparison.Ordinal);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await h.Logs.WaitForAsync("carrying on with the connection", ct);

        var bye = await bpq.ReadUntilAsync('d', ct);
        bye.CallTo.Should().Be(Remote, "the peer is let go at once rather than left waiting for a prompt");
        bye.Port.Should().Be(Port);
        await peers.WaitUntilIdleAsync(Remote, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        h.Logs.ThrowWhen = null;
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Any("while a session was still registered").Should().BeFalse("the failed session left nothing behind in the table");
    }

    [Fact]
    public async Task AConnectWhoseCountFails_IsCleanedUpToo()
    {
        // Counting the connect is part of its setup: a failure there must
        // not leave the session in the table with nothing to end it.
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);

        h.Logs.ThrowWhen = e => e.Message.Contains("inbound.connect", StringComparison.Ordinal);
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        await h.Logs.WaitForAsync("carrying on with the connection", ct);

        var bye = await bpq.ReadUntilAsync('d', ct);
        bye.CallTo.Should().Be(Remote, "the peer is let go at once rather than left waiting for a prompt");
        await peers.WaitUntilIdleAsync(Remote, ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        h.Logs.ThrowWhen = null;
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().StartWith(Prompt);
        h.Logs.Any("while a session was still registered").Should().BeFalse("the failed session left nothing behind in the table");
    }

    [Fact]
    public async Task ARegisterReplyOtherThanSuccess_BacksOffAndSaysTheCallsignWasRefused()
    {
        // #278: the reply used to be ignored, leaving the listener up but
        // deaf: the node would never hand it a call.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        await h.Service.StartAsync(ct);
        using var bpq = await h.Host.AcceptAsync(ct);
        await bpq.ExpectRegisterAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x00));

        var warning = await h.Logs.WaitForAsync(e => e.Message.Contains("(attempt 1,", StringComparison.Ordinal), ct);
        warning.Exception!.Message.Should().Contain("callsign refused").And.Contain(Local);
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
        h.Metrics.Take().AgwReconnectAttempt.Should().Be(1, "a refused callsign is a failure to back off from");
        h.Logs.Any("'X' ack").Should().BeFalse();
    }

    [Fact]
    public async Task TheOpenLinkIsPublished_AndWithdrawnWhenItDrops()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        await h.Service.StartAsync(ct);
        using var bpq = await h.Host.AcceptAsync(ct);
        await bpq.ExpectRegisterAsync(ct);

        h.Service.State.Should().Be(AgwLinkState.Connecting);
        h.Service.TryGetLink().Should().BeNull("not open until the node answers the 'X'");
        var waiting = h.Service.GetLinkAsync(TimeSpan.FromSeconds(5), ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        var link = await waiting.WaitAsync(FakeAgwHost.FrameTimeout, ct);
        link.Should().NotBeNull("a caller waiting while the link connects gets it once it opens");
        link!.Callsign.Should().Be(Local);
        h.Service.TryGetLink().Should().BeSameAs(link);
        h.Service.State.Should().Be(AgwLinkState.Open);

        bpq.Close();
        await h.Logs.WaitForAsync("(attempt 1,", ct);
        link.Closed.IsCompleted.Should().BeTrue();
        h.Service.State.Should().Be(AgwLinkState.Backoff);
        h.Service.TryGetLink().Should().BeNull();
        (await h.Service.GetLinkAsync(TimeSpan.FromSeconds(5), ct)).Should().BeNull("nothing to wait for while backing off");
    }

    [Fact]
    public async Task ARetiredSession_IsTakenOutOfTheTableAtOnce_NotOnlyWhenItsHandlerEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var peers = new PeerSessionRegistry();
        await using var h = new AgwInboundServiceHarness(Local, peerSessions: peers);
        var bpq = await h.StartAsync(ct);

        // Hold the session's handler just as it starts, so only the
        // retirement itself can take the session out of the table.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Logs.OnLogged = e =>
        {
            if (!e.Message.StartsWith("Inbound session from", StringComparison.Ordinal)) return;
            held.Set();
            release.Wait(FakeAgwHost.FrameTimeout);
        };
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        held.Wait(FakeAgwHost.FrameTimeout, ct).Should().BeTrue();

        try
        {
            using var ours = peers.Acquire(Remote, "outbound", linkPort: Number);
            peers.Connected(ours);
            await h.Logs.WaitForAsync("retired", ct);

            await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "help\n"));
            await h.Logs.WaitForAsync("'D' for unknown session", ct);
        }
        finally
        {
            release.Set();
        }
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd', "a retired session never disconnects the link");
    }

    [Fact]
    public async Task ACallerWaitingForTheLink_IsLetGo_WhenTheOpenFails_AndTheStateIsIdleAfterAStop()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        await h.Service.StartAsync(ct);
        using var bpq = await h.Host.AcceptAsync(ct);
        await bpq.ExpectRegisterAsync(ct);
        var waiting = h.Service.GetLinkAsync(TimeSpan.FromMinutes(5), ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x00));

        (await waiting.WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().BeNull("the open failed, and no link is coming before the backoff ends");
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
        h.Service.State.Should().Be(AgwLinkState.Backoff);

        await h.Service.StopAsync(ct);
        h.Service.State.Should().Be(AgwLinkState.Idle, "stopped during the backoff, nothing is coming any more");
    }

    /// <summary>Reads until the service closes the socket and returns how
    /// the read ended; a timeout propagates and fails the test.</summary>
    private static async Task<Exception> ReadUntilClosedAsync(FakeAgwSocket socket, CancellationToken ct)
    {
        try
        {
            while (true) await socket.ReadFrameAsync(ct);
        }
        catch (Exception ex) when (ex is not TimeoutException and not OperationCanceledException)
        {
            return ex;
        }
    }
}
