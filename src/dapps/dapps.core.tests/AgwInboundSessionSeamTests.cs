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
    private const string Prompt = "DAPPSv1>\n";

    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-seam-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
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
        Encoding.UTF8.GetString(prompt.Payload).Should().Be(Prompt);
        prompt.Port.Should().Be(Port, "dapps writes on the port the session came in on");
        prompt.CallFrom.Should().Be(Local);
        prompt.CallTo.Should().Be(Remote);

        // BPQ's disconnect notification carries the port of the last
        // frame it received from us. After a keepalive that is 0.
        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, port: 0));
        await h.Logs.WaitForAsync("session closed", ct);
        h.Logs.Any("carried port 0 but the session was on port 1").Should().BeTrue(
            "the port fallback is logged so a field log shows which rule matched");

        // The pair is free again: the redial is a clean new session, not
        // a stale-entry rescue.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt);

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
            (await bpq.ReadTextAsync(ct)).Should().Be(Prompt, $"session {i} should be prompted when {because}");

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
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt);

        // No 'd' ever arrives for that session. BPQ refuses live
        // duplicates itself, so when it dispatches the peer's next
        // connect for the pair, our entry can only be stale.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt, "the new connect is legitimate and must not be rejected");
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

    [Fact]
    public async Task DataStampedWithTheWrongPort_ReachesTheOnlySessionForThatPair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt);

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
        new[] { first.Port, second.Port }.Should().BeEquivalentTo(new byte[] { 1, 2 });

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
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt);
        await bpq.WriteAsync(ct, FakeAgwSocket.Data(Remote, Local, Port, "quit\n"));
        (await bpq.ReadTextAsync(ct)).Should().Be("bye\n");
        var ours = await bpq.ReadUntilAsync('d', ct);
        ours.Port.Should().Be(Port, "dapps closed this one, so it tells BPQ");

        // BPQ answers an application's 'd' with a 'd' of its own
        // ("*** DISCONNECTED RETRYOUT With"), stamped with the port we
        // just used. The entry is already gone; nothing to do.
        await bpq.WriteAsync(ct, FakeAgwSocket.Disconnect(Remote, Local, Port));
        await h.Logs.WaitForAsync("'d' for unknown session", ct);

        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        (await bpq.ReadTextAsync(ct)).Should().Be(Prompt);
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

        // BPQ's reply to a 'G' is noise to the read loop.
        await bpq.WriteAsync(ct, new AgwFrame(0, 'G', 0, "", "", "2;Port1 Telnet;Port2 AXIP;"u8.ToArray()));
        await h.Logs.WaitForAsync("ignoring frame kind 'G'", ct);
        h.Logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenBpqDropsTheSocket_DappsRedialsAfterTheBackoff_OnTheClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var first = await h.StartAsync(ct);

        first.Close();
        await h.Logs.WaitForAsync("reconnecting in", ct);
        await clock.WaitForTimerAsync(AgwInboundService.ReconnectBackoff, ct);
        h.Host.Pending().Should().BeFalse("no redial before the backoff has elapsed");

        clock.Advance(AgwInboundService.ReconnectBackoff);
        using var second = await h.Host.AcceptAsync(ct);
        (await second.ExpectRegisterAsync(ct)).CallFrom.Should().Be(Local, "the new connection re-registers our callsign");
    }
}
