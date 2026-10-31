using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using static dapps.core.tests.ExchangeTestKit;

namespace dapps.core.tests;

/// <summary>
/// #425, early give-way: a quiet session we dialled ends after the
/// protocol's minimum quiet spell, instead of the agreed hold, when other
/// mail is waiting for the link it holds. Everything runs on a fake clock
/// moved only by the test.
/// </summary>
public sealed class EarlyGiveWayTests : IDisposable
{
    private const string Us = "N0CALL";
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);
    private static readonly BackhaulRoute ToAaa = new("N0AAA", BearerPort: 1, ConnectVia: "N0VIA");
    private static readonly BackhaulRoute ToBbb = new("N0BBB", BearerPort: 1, ConnectVia: "N0VIA");

    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private CancellationToken Ct => lifetime.Token;

    private static string Rules(int hold) => $"exchange id=peer01 hold={hold} inline=256 z=1";

    /// <summary>A session that dialled, with a 120 s hold agreed at both
    /// ends, its 10 s minimum quiet spell, on <paramref name="clock"/>.</summary>
    private async Task<(ExchangeSession Session, LinePeer Peer, Task Run, WriteRecordingStream Wire)> CallerAsync(
        WatchedClock clock, CapturingLoggerFactory? logs = null, bool dialled = true)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var wire = new WriteRecordingStream(ours);
        var session = new ExchangeSession(wire, "N0AAA", dialled, new ExchangeSettings(HoldSeconds: 120), new RecordingInbox(),
            (Microsoft.Extensions.Logging.ILoggerFactory?)logs ?? NullLoggerFactory.Instance)
        {
            TimeProvider = clock,
            PromptWait = TenSeconds,
            MinQuiet = TenSeconds,
            OutOfStepHistory = new OutOfStepMemory(),
        };
        return (session, new LinePeer(theirs), session.RunAsync(Ct), wire);
    }

    /// <summary>The peer answers as the called node, and the caller's rules come back.</summary>
    private async Task EstablishAsync(LinePeer peer, WatchedClock clock)
    {
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(120) + "\n"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        await clock.WaitUntilPendingAsync(Ct);
    }

    private static bool SaidQuit(WriteRecordingStream wire) => wire.Writes.Any(w => w.Contains("quit\n", StringComparison.Ordinal));

    /// <summary>
    /// Moves the clock on and checks the session's one timer neither ran out
    /// nor was replaced: a timer that runs out does so inside
    /// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider.Advance"/>, so until the next move nothing can
    /// make the session say <c>quit</c>.
    /// </summary>
    private static void AdvanceWithoutTimersRunningOut(WatchedClock clock, TimeSpan by, string because)
    {
        var armed = clock.Armed;
        clock.Pending.Should().Be(1, "the session is waiting on its timer");
        clock.Advance(by);
        clock.Armed.Should().Be(armed, because);
        clock.Pending.Should().Be(1, because);
    }

    [Fact]
    public async Task AQuietSessionAskedToGiveWay_EndsAfterTenSecondsOfQuiet_NotTheHold()
    {
        var clock = new WatchedClock();
        var logs = new CapturingLoggerFactory();
        var (session, peer, run, wire) = await CallerAsync(clock, logs);
        await EstablishAsync(peer, clock);

        var armed = clock.Armed;
        session.RequestYield("mail for N0BBB is waiting for the link to N0VIA").Should().BeTrue();
        await clock.WaitForArmedAsync(armed + 1, Ct);   // back waiting, on the shorter spell

        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(9), "only 9 s of quiet so far");
        SaidQuit(wire).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        (await peer.ReadLineAsync(Ct)).Should().Be("quit", "10 s of quiet is the protocol's minimum, and other mail waits");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);

        logs.Entries.Select(e => e.Message).Should().Contain(
            "Ending the session with N0AAA after 10s of quiet, before its 120s hold: mail for N0BBB is waiting for the link to N0VIA");
    }

    [Fact]
    public async Task WithoutARequest_TheHoldIsUnchanged()
    {
        var clock = new WatchedClock();
        var (_, peer, run, wire) = await CallerAsync(clock);
        await EstablishAsync(peer, clock);

        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(119), "the agreed hold is 120 s");
        SaidQuit(wire).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task ASessionAlreadyQuietForTenSeconds_EndsAtOnceWhenAsked()
    {
        var clock = new WatchedClock();
        var (session, peer, run, _) = await CallerAsync(clock);
        await EstablishAsync(peer, clock);
        clock.Advance(TimeSpan.FromSeconds(30));

        session.RequestYield("mail for N0BBB is waiting").Should().BeTrue();

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task ASessionAskedToGiveWay_WithOursUnanswered_GoesOnUntilItIsQuiet()
    {
        var clock = new WatchedClock();
        var ours = Message("still going", "app@N0AAA");
        var batch = new RecordingBatch(ours);
        var (session, peer, run, wire) = await CallerAsync(clock);
        session.TryTake(batch);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(120) + "\n"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        await peer.ReadWithPayloadAsync(Ct);
        await clock.WaitUntilPendingAsync(Ct);

        var beforeNudge = clock.Armed;
        session.RequestYield("mail for N0BBB is waiting").Should().BeTrue();
        await clock.WaitForArmedAsync(beforeNudge + 1, Ct);
        await clock.WaitUntilPendingAsync(Ct);
        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(30), "ours is still unanswered: giving way never cuts a session short while it is busy");
        SaidQuit(wire).Should().BeFalse();

        var armed = clock.Armed;
        await peer.WriteLineAsync($"ack {ours.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        await clock.WaitForArmedAsync(armed + 1, Ct);
        await clock.WaitUntilPendingAsync(Ct);
        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(9), "the quiet spell runs from the ack");
        SaidQuit(wire).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task AFollowUpForThePeerDuringTheShortSpell_GoesOnTheSessionOnce_AndTheSpellRunsFromIt()
    {
        var clock = new WatchedClock();
        var (session, peer, run, wire) = await CallerAsync(clock);
        await EstablishAsync(peer, clock);
        var armed = clock.Armed;
        session.RequestYield("mail for N0BBB is waiting").Should().BeTrue();
        await clock.WaitForArmedAsync(armed + 1, Ct);
        await clock.WaitUntilPendingAsync(Ct);
        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(5), "only 5 s of quiet so far");

        var followUp = Message("follow-up", "app@N0AAA");
        var batch = new RecordingBatch(followUp);
        session.TryTake(batch).Should().BeTrue("a session giving way still takes mail for its own peer");
        (await peer.ReadWithPayloadAsync(Ct)).Line.Should().StartWith($"msg {followUp.Id}");
        await clock.WaitUntilPendingAsync(Ct);
        armed = clock.Armed;
        await peer.WriteLineAsync($"ack {followUp.Id}", Ct);
        await batch.WaitForOutcomesAsync(1, Ct);
        await clock.WaitForArmedAsync(armed + 1, Ct);
        await clock.WaitUntilPendingAsync(Ct);

        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(9), "the quiet spell runs from the ack, not from the request");
        SaidQuit(wire).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);

        batch.Outcomes.Single().Result.Accepted.Should().BeTrue();
        wire.Writes.Count(w => w.Contains($"msg {followUp.Id}", StringComparison.Ordinal)).Should().Be(1, "it went once");
    }

    [Fact]
    public async Task ARequestBeforeTheExchange_Sticks()
    {
        var clock = new WatchedClock();
        var (session, peer, run, _) = await CallerAsync(clock);
        session.RequestYield("mail for N0BBB is waiting").Should().BeTrue();
        await EstablishAsync(peer, clock);

        clock.Advance(TenSeconds);

        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await run.WaitAsync(Patience, Ct);
    }

    [Fact]
    public async Task TheAnsweringNode_IsNeverAskedToGiveWay_AndNeverSaysQuit()
    {
        var clock = new WatchedClock();
        var (session, peer, _, wire) = await CallerAsync(clock, dialled: false);
        (await peer.ReadLineAsync(Ct)).Should().Be("DAPPSv1>");
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        await peer.WriteLineAsync(Rules(120), Ct);
        await clock.WaitUntilPendingAsync(Ct);

        session.RequestYield("mail for N0BBB is waiting").Should().BeFalse("only the node that dialled ends a session");
        clock.Advance(TimeSpan.FromSeconds(60));
        await clock.WaitUntilPendingAsync(Ct);

        SaidQuit(wire).Should().BeFalse();
    }

    // ---- The backhaul: which sessions give way ----

    /// <summary>
    /// A backhaul whose first dial reaches <paramref name="ours"/>, an
    /// answering DAPPS node played by the test, and whose later dials are
    /// refused as the node's lease for N0VIA would refuse them.
    /// </summary>
    private static Dappsv1SessionBackhaul Backhaul(Stream ours, WatchedClock clock) =>
        new(new FirstThenBusyTransport(ours), NullLoggerFactory.Instance, inbox: null,
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(HoldSeconds: 120)))
        {
            TimeProvider = clock,
            PromptWait = TenSeconds,
            MinQuiet = TenSeconds,
        };

    /// <summary>Dials N0AAA through N0VIA and has its one message acked, leaving the session open and quiet.</summary>
    private async Task<(Dappsv1SessionBackhaul Backhaul, LinePeer Peer, WriteRecordingStream Wire)> OpenToAaaAsync(WatchedClock clock)
    {
        var (ours, theirs) = await LoopbackPairAsync(Ct);
        var wire = new WriteRecordingStream(ours);
        var sb = Backhaul(wire, clock);
        var peer = new LinePeer(theirs);
        var m = Message("for aaa", "app@N0AAA");
        var send = sb.SendBatchAsync(ToAaa, Us, new RecordingBatch(m), Ct);
        await peer.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n" + Rules(120) + "\n"), Ct);
        (await peer.ReadLineAsync(Ct)).Should().StartWith("exchange ");
        await peer.ReadWithPayloadAsync(Ct);
        await peer.WriteLineAsync($"ack {m.Id}", Ct);
        await send.WaitAsync(Patience, Ct);
        await clock.WaitUntilPendingAsync(Ct);
        sb.OpenPeers.Should().Equal(["N0AAA"]);
        return (sb, peer, wire);
    }

    [Fact]
    public async Task OnlySessionsWeDialledThroughTheSameNodeOnTheSamePort_ToAnotherNeighbour_GiveWay()
    {
        var clock = new WatchedClock();
        var (sb, peer, wire) = await OpenToAaaAsync(clock);

        sb.YieldSessionsHolding(ToAaa, "x").Should().Be(0, "mail for N0AAA itself goes on its session");
        sb.YieldSessionsHolding(ToBbb with { ConnectVia = "N0OTHER" }, "x").Should().Be(0, "another node is another link");
        sb.YieldSessionsHolding(new BackhaulRoute("N0BBB", BearerPort: 1), "x").Should().Be(0, "a direct neighbour is another link");
        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(60), "nothing waits for its link, so it holds");
        SaidQuit(wire).Should().BeFalse();

        sb.YieldSessionsHolding(ToBbb with { BearerPort = 2 }, "mail for N0BBB is waiting").Should().Be(1,
            "the node's lease on N0VIA is per callsign, so mail for any port through it waits on this session");

        (await peer.ReadLineAsync(Ct)).Should().Be("quit", "it had been quiet for over 10 s already");
        await peer.WriteLineAsync("bye", Ct);
        await WaitForAsync(() => sb.OpenPeers.Count == 0);
        sb.YieldSessionsHolding(ToBbb, "x").Should().Be(0, "an ended session is forgotten");
    }

    [Fact]
    public async Task ADialRefusedBecauseTheNodeIsBusy_AsksTheSessionHoldingItToGiveWay()
    {
        var clock = new WatchedClock();
        var (sb, peer, wire) = await OpenToAaaAsync(clock);
        var forBbb = new RecordingBatch(Message("for bbb", "app@N0BBB"));
        var armed = clock.Armed;

        await sb.SendBatchAsync(ToBbb, Us, forBbb, Ct);

        forBbb.Outcomes.Single().Result.Deferred.Should().BeTrue("nothing failed; it waits for the link");
        await clock.WaitForArmedAsync(armed + 1, Ct);   // the session took the request and waits again
        await clock.WaitUntilPendingAsync(Ct);
        AdvanceWithoutTimersRunningOut(clock, TimeSpan.FromSeconds(9), "only 9 s of quiet so far");
        SaidQuit(wire).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        (await peer.ReadLineAsync(Ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", Ct);
        await WaitForAsync(() => sb.OpenPeers.Count == 0);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FirstThenBusyTransport(Stream stream) : IDappsOutboundTransport
    {
        private int dials;

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken) =>
            Interlocked.Increment(ref dials) == 1
                ? Task.FromResult<IDappsConnection>(new Connection(stream))
                : throw new PeerSessionBusyException(remoteCallsign, "outbound");

        private sealed class Connection(Stream stream) : IDappsConnection
        {
            public Stream Stream => stream;
            public ValueTask DisposeAsync()
            {
                stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
