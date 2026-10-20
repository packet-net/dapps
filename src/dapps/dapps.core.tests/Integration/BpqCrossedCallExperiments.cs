using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// What BPQ does when two AX.25 calls between the same pair of callsigns
/// cross, found by driving its AGW interface by hand on the simulated radio
/// channel, with no DAPPS in the way. These are experiments, not checks:
/// each writes what every AGW socket and the air saw to scenario-reports/,
/// and asserts nothing about BPQ. They only run when
/// DAPPS_BPQ_EXPERIMENTS is set. The findings are in
/// docs-internal/end-to-end-tests.md.
///
/// Each node has what a DAPPS daemon had before #272: a listener socket
/// registered for its application callsign (where inbound connects
/// arrive), and a fresh socket per outbound call. The OneSocket cases
/// instead give each node one socket for everything, as a daemon has
/// now, and repeat each case to count the outcomes.
/// </summary>
public abstract class BpqCrossedCallExperiments(NetSimTwoBpqFixture fixture) : IAsyncLifetime
{
    private static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_BPQ_EXPERIMENTS") is { Length: > 0 };

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> log = [];
    private readonly List<AgwProbe> sockets = [];
    private AirMonitor? air;

    public async ValueTask InitializeAsync()
    {
        if (!Wanted) return;
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Wanted) return;
        foreach (var s in sockets) await s.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        await Task.Delay(3000);
    }

    [Fact]
    public async Task AnOutboundCall_ForAPairAlreadyConnectedInbound_AfterTheListenerHasSpoken()
    {
        // As DAPPS does it: A calls B, B's listener gets the connect and
        // answers straight away (DAPPS sends its prompt), so A's node has
        // had an I-frame on the link. Then B calls A as well.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var listenerA = await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await WaitForAsync(() => listenerB.Saw('C'), ct);
        Note("B's listener has A's connect; it answers");
        await listenerB.SendAsync("B listener's prompt\r", ct, to: fixture.ApplCallA);
        await WaitForAsync(() => callerA.Saw('D'), ct);
        Note(callerA.Saw('D') ? "A's caller has the answer; now B calls A" : "A's caller never got the answer; B calls A anyway");
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        Note($"A caller saw 'd': {callerA.Saw('d')}; A listener saw 'C': {listenerA.Saw('C')}");

        // What a DAPPS caller would send when no prompt comes. (A line
        // starting "B" would be taken for BYE if this lands at A's node.)
        await SendAndWatchAsync(callerB, "exchange id=0badcafe hold=0", ct);
        await SendAndWatchAsync(listenerA, "A listener -> B", ct, to: fixture.ApplCallB);
        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(listenerB, "B listener -> A", ct, to: fixture.ApplCallA);
        await HangUpAndWatchAsync(callerB, ct);
        Write("inbound-then-outbound-active");
    }

    [Fact]
    public async Task AnOutboundCall_ForAPairAlreadyConnectedInbound()
    {
        // The #194 sighting: A calls B, B's listener gets the connect, then
        // B calls A as well, before anything has gone over the link.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await WaitForAsync(() => listenerB.Saw('C'), ct);
        Note("B's listener has A's connect; now B calls A");
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(listenerB, "B listener -> A", ct, to: fixture.ApplCallA);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerB, ct);
        Write("inbound-then-outbound");
    }

    [Fact]
    public async Task TwoCalls_MadeAtTheSameMoment()
    {
        // The crossed call: both nodes have traffic and dial together.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);

        await Task.WhenAll(callerA.CallAsync(fixture.ApplCallB, ct), callerB.CallAsync(fixture.ApplCallA, ct));
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerA, ct);
        Write("simultaneous");
    }

    [Fact]
    public async Task ASecondCall_JustAfterTheFirstIsOnAir()
    {
        // As in the WPS scenario at 1200 baud: B dials a moment after A,
        // before A's call has reached B's listener.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        await OpenAsync("A listener", fixture.AgwPortA, fixture.ApplCallA, ct);
        var listenerB = await OpenAsync("B listener", fixture.AgwPortB, fixture.ApplCallB, ct);
        var callerA = await OpenAsync("A caller", fixture.AgwPortA, fixture.ApplCallA, ct);
        var callerB = await OpenAsync("B caller", fixture.AgwPortB, fixture.ApplCallB, ct);

        await callerA.CallAsync(fixture.ApplCallB, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        if (listenerB.Saw('C')) Note("B's listener already had A's connect before B dialled");
        await callerB.CallAsync(fixture.ApplCallA, ct);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        await SendAndWatchAsync(callerA, "A caller -> B", ct);
        await SendAndWatchAsync(callerB, "B caller -> A", ct);
        await HangUpAndWatchAsync(callerA, ct);
        Write("just-after");
    }

    private async Task<AgwProbe> OpenAsync(string name, int agwPort, string callsign, CancellationToken ct)
    {
        var probe = await AgwProbe.OpenAsync(name, fixture.Host, agwPort, callsign, (byte)fixture.RadioPortIndex, Note, ct);
        sockets.Add(probe);
        return probe;
    }

    private async Task SendAndWatchAsync(AgwProbe from, string label, CancellationToken ct, string? to = null)
    {
        Note($"sending '{label}' on {from.Name}");
        await from.SendAsync(label + "\r", ct, to);
        await Task.Delay(TimeSpan.FromSeconds(6), ct);
    }

    private async Task HangUpAndWatchAsync(AgwProbe who, CancellationToken ct)
    {
        Note($"{who.Name} sends 'd'");
        await who.HangUpAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, ct);
    }

    private void Note(string text)
    {
        lock (log) log.Add($"{clock.Elapsed.TotalSeconds,7:F2}  {text}");
    }

    private void Write(string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# BPQ experiment: {name}, {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine("What each AGW socket received, and what the test did, in order:");
        sb.AppendLine();
        sb.AppendLine("```");
        lock (log) foreach (var line in log) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("What went over the air:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(air!.Transcript());
        sb.AppendLine("```");
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"bpq-{name}-{fixture.ChannelName.Split(' ')[0].ToLowerInvariant()}.md"), sb.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(sb.ToString());
    }

    // One-socket mode (#272 PR 0). Each node has one AGW socket that does
    // everything a DAPPS daemon will do over a single connection: one 'X'
    // for its application callsign, one plain 'm' so its own monitor is on,
    // inbound connects, outbound dials and data, all on the same socket. A
    // case is repeated several times and the report counts the outcomes.
    // Each run opens fresh sockets, and after it closes them the next run
    // waits until the air has gone quiet.

    private const int Repeats = 5;

    private bool Slow => fixture.ChannelName.StartsWith("AFSK", StringComparison.Ordinal);

    /// <summary>How long a dial gets to be confirmed or refused.</summary>
    private TimeSpan DialLimit => TimeSpan.FromSeconds(Slow ? 60 : 30);

    /// <summary>How long a line of data gets to arrive.</summary>
    private TimeSpan DataLimit => TimeSpan.FromSeconds(Slow ? 15 : 8);

    /// <summary>No new frame for this long counts as quiet: longer than FRACK, so a retry would show.</summary>
    private TimeSpan QuietFor => TimeSpan.FromSeconds(Slow ? 9 : 6);

    [Fact]
    public async Task OneSocket_PeerCallArrives_WhileOurDialIsPending()
    {
        // Case 1. B dials A; A dials B a moment later, before B's SABM has
        // reached BPQ-A, so A's 'C' is at BPQ-A first. First a few plain
        // B-calls-A runs (no dial from A) measure how long B's SABM takes
        // to reach A's monitor, and A then dials at fractions of the
        // shortest of those. What counts is where A's dial actually fell
        // against B's SABM arriving, which each run records.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        var arrivals = await MeasureArrivalsAsync(runs, Repeats, ct);
        var shortest = arrivals.Count > 0 ? arrivals.Min() : 400;
        foreach (var fraction in new[] { 0.3, 0.6, 0.9 })
        {
            var lead = TimeSpan.FromMilliseconds(shortest * fraction);
            for (var i = 1; i <= Repeats; i++)
            {
                runs.Add(await OneSocketRunAsync($"A dials {lead.TotalMilliseconds:F0} ms after B ({fraction:P0} of the shortest arrival)", i, async (a, b) =>
                {
                    await b.DialAsync(ct);
                    await Task.Delay(lead, ct);
                    await a.DialAsync(ct);
                    await WaitForDialsAsync([a, b], ct);
                }, ct));
            }
        }
        WriteOneSocket("one-socket-pending", runs);
    }

    [Fact]
    public async Task OneSocket_OurDial_AroundThePeersSabm()
    {
        // Case 1 again, aimed at the edge: BPQ-A refuses B's inbound
        // connect only if A's 'C' gets there after B's SABM is on its way
        // but before BPQ-A has set up A's own link. A dials at a random
        // moment from 600 ms before the median measured arrival of B's
        // SABM to 200 ms after it, and each run records where its dial
        // actually fell.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        var arrivals = await MeasureArrivalsAsync(runs, 3, ct);
        var middle = arrivals.Count > 0 ? (int)arrivals.Order().ElementAt(arrivals.Count / 2) : 1000;
        var (low, high) = (middle - 600, middle + 200);
        var random = Random.Shared;
        for (var i = 1; i <= Repeats * 4; i++)
        {
            var lead = TimeSpan.FromMilliseconds(random.Next(Math.Max(0, low), high));
            runs.Add(await OneSocketRunAsync("A dials at a random moment around B's SABM arriving", i, async (a, b) =>
            {
                Note($"A will dial {lead.TotalMilliseconds:F0} ms after B");
                await b.DialAsync(ct);
                await Task.Delay(lead, ct);
                await a.DialAsync(ct);
                await WaitForDialsAsync([a, b], ct);
            }, ct));
        }
        WriteOneSocket("one-socket-around-sabm", runs);
    }

    [Fact]
    public async Task OneSocket_UnansweredDial_ThenHangUpAndRedial()
    {
        // #397: A dials at the edge of B's SABM arriving. When either
        // node's dial gets nothing at all (no confirmation, no 'd', no
        // inbound call), the other sends its exchange on the call it has,
        // and the stuck one does what DAPPS does: a 'd' 15 s after its
        // dial, a fresh call 12 s later, and then a routes line, which
        // should reach the other node. Up to 60 runs, stopping after 3
        // stuck dials.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        var arrivals = await MeasureArrivalsAsync(runs, 3, ct);
        var middle = arrivals.Count > 0 ? (int)arrivals.Order().ElementAt(arrivals.Count / 2) : 1000;
        var (low, high) = (middle - 250, middle + 30);
        var random = Random.Shared;
        var unanswered = 0;
        for (var i = 1; i <= 60 && unanswered < 3; i++)
        {
            var lead = TimeSpan.FromMilliseconds(random.Next(Math.Max(0, low), high));
            runs.Add(await OneSocketRunAsync("A dials around B's SABM; a stuck dial hangs up and redials", i, async (a, b) =>
            {
                Note($"A will dial {lead.TotalMilliseconds:F0} ms after B");
                await b.DialAsync(ct);
                await Task.Delay(lead, ct);
                var dialled = DateTime.UtcNow;
                await a.DialAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
                static bool Stuck(NodeSocket n) => !n.Got('C', "") && !n.Got('d', "");
                var (x, y) = Stuck(a) ? (a, b) : Stuck(b) ? (b, a) : (null, null);
                if (x is null || y is null) return;
                unanswered++;
                Note($"{x.Name}'s dial unanswered, no inbound either; {y.Name} sends its exchange on what it has");
                await y.SendAsync("exchange id=test\r", ct);
                var wait = dialled + TimeSpan.FromSeconds(15) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                await x.HangUpAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(12), ct);
                Note($"{x.Name} redials");
                await x.DialAsync(ct);
                await WaitForAsync(() => x.CountText('C', "CONNECTED With") >= 1 || x.CountText('d', "") >= 3, TimeSpan.FromSeconds(30), ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                Note($"{y.Name} sends a prompt line");
                await y.SendAsync("DAPPSv1>\r", ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                Note($"{x.Name} sends routes");
                await x.SendAsync("routes\r", ct);
                await WaitForAsync(() => y.Got('D', "routes"), TimeSpan.FromSeconds(10), ct);
                Note($"{y.Name} {(y.Got('D', "routes") ? "got" : "never got")} {x.Name}'s routes line");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }, ct, exchangeData: false));
        }
        WriteOneSocket("one-socket-unanswered-redial", runs);
    }

    /// <summary>
    /// Plain B-calls-A runs on one-socket nodes (A does not dial), each a
    /// baseline run of its own, measuring how long B's SABM takes from B's
    /// 'C' to A's monitor.
    /// </summary>
    private async Task<List<double>> MeasureArrivalsAsync(List<OneSocketRun> runs, int count, CancellationToken ct)
    {
        var arrivals = new List<double>();
        for (var i = 1; i <= count; i++)
        {
            runs.Add(await OneSocketRunAsync("B calls A, A does not dial (baseline)", i, async (a, b) =>
            {
                var dialled = await b.DialAsync(ct);
                await WaitForAsync(() => a.Got('C', "CONNECTED"), DialLimit, ct);
                if (a.FirstMonitor(b.Call, a.Call, "<C C") is { } heard) arrivals.Add(heard - dialled);
            }, ct));
        }
        Note($"B's 'C' to A's monitor seeing B's SABM: {string.Join(", ", arrivals.Select(x => $"{x:F0}"))} ms");
        return arrivals;
    }

    [Fact]
    public async Task OneSocket_OurDial_JustAfterThePeersSabm()
    {
        // Between case 1 and case 2: B dials A, and A dials B a set time
        // after A's own monitor shows B's SABM, which is the earliest a
        // daemon on one socket could know about the peer's call. Short
        // delays land before BPQ-A has told A's socket about the inbound
        // connect (where AGWConnected's duplicate check could refuse it);
        // long ones on a live inbound session (case 2).
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        foreach (var ms in new[] { 0, 100, 300, 1000, 2000 })
        {
            for (var i = 1; i <= Repeats; i++)
            {
                runs.Add(await OneSocketRunAsync($"A dials {ms} ms after its monitor shows B's SABM", i, async (a, b) =>
                {
                    await b.DialAsync(ct);
                    await WaitForAsync(() => a.FirstMonitor(b.Call, a.Call, "<C C") is not null, DialLimit, ct);
                    await Task.Delay(ms, ct);
                    await a.DialAsync(ct);
                    await WaitForDialsAsync([a, b], ct);
                }, ct));
            }
        }
        WriteOneSocket("one-socket-after-sabm", runs);
    }

    [Fact]
    public async Task OneSocket_OurDial_OnALiveInboundSession()
    {
        // Case 2. B calls A and A's socket has the inbound connect; then A
        // dials B on the same socket, for the same key. Once with nothing
        // sent on the link yet, once after A has answered (as DAPPS does,
        // with its prompt) so BPQ-B has had an I-frame on the link.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        foreach (var answered in new[] { false, true })
        {
            for (var i = 1; i <= Repeats; i++)
            {
                runs.Add(await OneSocketRunAsync(answered ? "A answers B's call, then dials B" : "A has B's call, says nothing, dials B", i, async (a, b) =>
                {
                    await b.DialAsync(ct);
                    await WaitForAsync(() => a.Got('C', "CONNECTED To"), DialLimit, ct);
                    await WaitForAsync(() => b.Got('C', "CONNECTED With"), DialLimit, ct);
                    if (answered)
                    {
                        await a.SendAsync("A prompt\r", ct);
                        await WaitForAsync(() => b.Got('D', "A prompt"), DataLimit, ct);
                    }
                    Note("A dials B on the socket that has B's call");
                    await a.DialAsync(ct);
                    await WaitForDialsAsync([a], ct);
                }, ct));
            }
        }
        WriteOneSocket("one-socket-live-inbound", runs);
    }

    [Fact]
    public async Task OneSocket_BothDialAtOnce()
    {
        // Case 3. Both nodes dial each other at the same moment, each from
        // its one socket.
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        for (var i = 1; i <= Repeats * 2; i++)
        {
            runs.Add(await OneSocketRunAsync("A and B dial together", i, async (a, b) =>
            {
                await Task.WhenAll(a.DialAsync(ct), b.DialAsync(ct));
                await WaitForDialsAsync([a, b], ct);
            }, ct));
        }
        WriteOneSocket("one-socket-simultaneous", runs);
    }

    [Fact]
    public async Task OneSocket_UiWithoutX_AndTheCallingCallsign()
    {
        // Case 4. (a) An 'M' from a socket that never sent 'X', from A's
        // application callsign and from a callsign nobody registered: does
        // it go on air, and from whom? (b) A 'C' whose CallFrom is not the
        // socket's registered callsign: which callsign does the SABM carry,
        // and what keys do the AGW frames that come back carry?
        Assert.SkipUnless(Wanted, "Set DAPPS_BPQ_EXPERIMENTS to run the BPQ experiments.");
        var ct = TestContext.Current.CancellationToken;
        var runs = new List<OneSocketRun>();
        var other = fixture.CallsignA + "-7";
        for (var i = 1; i <= 2; i++)
        {
            runs.Add(await OneSocketRunAsync("'M' from a socket with no 'X'", i, async (a, b) =>
            {
                await using var bare = await NodeSocket.OpenAsync("A bare", fixture.Host, fixture.AgwPortA, fixture.ApplCallA, fixture.ApplCallB,
                    (byte)fixture.RadioPortIndex, Note, clock, register: false, monitor: false, ct);
                await bare.SendUiAsync(fixture.ApplCallA, "DAPPS", $"ui from {fixture.ApplCallA} {i}", ct);
                await bare.SendUiAsync(other, "DAPPS", $"ui from {other} {i}", ct);
                await WaitForAsync(() => b.FirstMonitor(other, "DAPPS", "<UI") is not null, DataLimit, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                Note($"B's monitor heard the UI from {fixture.ApplCallA}: {b.FirstMonitor(fixture.ApplCallA, "DAPPS", "<UI") is not null}; from {other}: {b.FirstMonitor(other, "DAPPS", "<UI") is not null}");
            }, ct, exchangeData: false));
        }
        for (var i = 1; i <= 2; i++)
        {
            runs.Add(await OneSocketRunAsync($"A's 'C' says CallFrom {other}", i, async (a, b) =>
            {
                await a.DialAsync(ct, callFrom: other);
                await WaitForAsync(() => a.Got('C', "") || a.Got('d', ""), DialLimit, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                Note($"B's monitor heard a SABM from {a.Call}: {b.FirstMonitor(a.Call, b.Call, "<C C") is not null}; from {other}: {b.FirstMonitor(other, b.Call, "<C C") is not null}");
                Note($"B's socket got an inbound connect: {b.Got('C', "CONNECTED To")}");
                await a.SendAsync($"A->B as {other} {i}\r", ct, callFrom: other);
                await WaitForAsync(() => b.Got('D', $"as {other}"), DataLimit, ct);
                await a.SendAsync($"A->B as {a.Call} {i}\r", ct);
                await WaitForAsync(() => b.Got('D', $"as {a.Call}"), DataLimit, ct);
                await b.SendAsync($"B->A {i}\r", ct);
                await WaitForAsync(() => a.Got('D', "B->A"), DataLimit, ct);
                await a.HangUpAsync(ct, callFrom: other);
                await Task.Delay(QuietFor, ct);
            }, ct, exchangeData: false));
        }
        WriteOneSocket("one-socket-callsigns", runs);
    }

    private sealed record OneSocketRun(string Setup, int Index, string Outcome, string Offsets, string Air, string Log, string Transcript);

    /// <summary>
    /// One run: fresh one-socket nodes A and B and a fresh air monitor, the
    /// case's own moves, then (unless told not to) a line of data each way,
    /// then both sockets closed and a wait for the air to go quiet.
    /// </summary>
    private async Task<OneSocketRun> OneSocketRunAsync(string setup, int index, Func<NodeSocket, NodeSocket, Task> act, CancellationToken ct, bool exchangeData = true)
    {
        int first;
        lock (log) first = log.Count;
        Note($"--- {setup}, run {index}");
        var runAir = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, ct);
        await using var channel = await fixture.StartChannelLogAsync(ct);
        var started = DateTime.UtcNow;
        var a = await NodeSocket.OpenAsync("A", fixture.Host, fixture.AgwPortA, fixture.ApplCallA, fixture.ApplCallB, (byte)fixture.RadioPortIndex, Note, clock, register: true, monitor: true, ct);
        var b = await NodeSocket.OpenAsync("B", fixture.Host, fixture.AgwPortB, fixture.ApplCallB, fixture.ApplCallA, (byte)fixture.RadioPortIndex, Note, clock, register: true, monitor: true, ct);
        string outcome, offsets, air;
        try
        {
            await act(a, b);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (exchangeData)
            {
                Note("A sends a line to B");
                await a.SendAsync($"A->B {index}\r", ct);
                await WaitForAsync(() => b.Got('D', "A->B"), DataLimit, ct);
                Note("B sends a line to A");
                await b.SendAsync($"B->A {index}\r", ct);
                await WaitForAsync(() => a.Got('D', "B->A"), DataLimit, ct);
            }
            outcome = Classify(a, b, runAir, exchangeData);
            offsets = Offsets(a, b, channel?.Between(started, DateTime.UtcNow) ?? []);
            air = AirCounts(runAir);
            Note($"outcome: {outcome}; {offsets}; air {air}");
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
            await WaitForQuietAsync(runAir, ct);
        }
        var transcript = runAir.Transcript();
        await runAir.DisposeAsync();
        string text;
        lock (log) text = string.Join('\n', log.Skip(first));
        return new OneSocketRun(setup, index, outcome, offsets, air, text, transcript);
    }

    /// <summary>Wait until every dial these nodes made has been confirmed, or refused, or the dial limit.</summary>
    private async Task WaitForDialsAsync(IEnumerable<NodeSocket> nodes, CancellationToken ct)
    {
        var list = nodes.ToList();
        await WaitForAsync(() => list.All(n => n.DialledAt is null || n.Got('C', "CONNECTED With") || n.Got('d', "")), DialLimit, ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
    }

    private async Task WaitForQuietAsync(AirMonitor runAir, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        var count = -1;
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var now = runAir.HeardBy("A").Count + runAir.HeardBy("B").Count;
            if (now != count) (count, since) = (now, DateTime.UtcNow);
            else if (DateTime.UtcNow - since >= QuietFor) return;
            await Task.Delay(200, ct);
        }
        Note("the air never went quiet");
    }

    /// <summary>
    /// What happened, short enough to count: what each socket got (our own
    /// call confirmed "out", an inbound connect "in", and how many 'd'),
    /// whether BPQ's "already connected" refusal showed anywhere, whether
    /// anything reached a node's command prompt, and each line of data.
    /// </summary>
    private static string Classify(NodeSocket a, NodeSocket b, AirMonitor runAir, bool data)
    {
        static string Side(NodeSocket s)
        {
            var parts = new List<string>();
            if (s.Got('C', "CONNECTED With")) parts.Add("out");
            if (s.Got('C', "CONNECTED To")) parts.Add("in");
            if (s.DialledAt is not null && !s.Got('C', "CONNECTED With")) parts.Add(s.Got('d', "") ? "dial failed" : "dial unanswered");
            if (s.Count('d') > 0) parts.Add($"{s.Count('d')}x d");
            return parts.Count > 0 ? string.Join(' ', parts) : "nothing";
        }
        var refused = a.Got('D', "already connected") || b.Got('D', "already connected") || runAir.Transcript().Contains("already connected", StringComparison.Ordinal);
        var prompt = a.Got('D', "Invalid command") || b.Got('D', "Invalid command") || runAir.Transcript().Contains("Invalid command", StringComparison.Ordinal);
        var s = $"A [{Side(a)}], B [{Side(b)}]";
        if (refused) s += ", refusal";
        if (prompt) s += ", node prompt";
        if (data) s += $"; A->B {(b.Got('D', "A->B") ? "ok" : "lost")}, B->A {(a.Got('D', "B->A") ? "ok" : "lost")}";
        return s;
    }

    /// <summary>
    /// Where each dial fell (negative: before) against the other node's
    /// first SABM showing on the dialler's monitor, and against the end of
    /// the other node's first transmission as net-sim saw it, which is
    /// when that frame reached the dialler's node to within about 10 ms
    /// (the monitor can lag by up to BPQ's 100 ms AGW poll, and more when
    /// BPQ is busy).
    /// </summary>
    private static string Offsets(NodeSocket a, NodeSocket b, IReadOnlyList<ChannelLog.Over> overs)
    {
        static string One(NodeSocket us, NodeSocket peer, IReadOnlyList<ChannelLog.Over> overs)
        {
            if (us.DialledAt is not { } dialled) return "";
            var monitor = us.FirstMonitor(peer.Call, us.Call, "<C C") is { } heard
                ? $"{us.Name}'s dial {dialled - heard:+0;-0} ms from {peer.Name}'s SABM at {us.Name}'s monitor"
                : $"{us.Name} dialled, {peer.Name}'s SABM never on {us.Name}'s monitor";
            var first = overs.FirstOrDefault(o => o.Side == peer.Name);
            return first is null || us.DialledUtc is not { } utc ? monitor : $"{monitor}, {(utc - first.End).TotalMilliseconds:+0;-0} ms from the end of {peer.Name}'s first transmission";
        }
        return string.Join("; ", new[] { One(a, b, overs), One(b, a, overs) }.Where(x => x.Length > 0)) is { Length: > 0 } s ? s : "no dial from A or B";
    }

    private static string AirCounts(AirMonitor runAir)
    {
        static string Side(IReadOnlyList<string> frames)
        {
            int Count(string what) => frames.Count(f => f.Contains(what, StringComparison.Ordinal));
            return $"SABM {Count("<C C")} UA {Count("<UA")} DM {Count("<DM")} DISC {Count("<D C")} I {Count("<I ")}";
        }
        return $"A sent: {Side(runAir.SentBy("A"))}; B sent: {Side(runAir.SentBy("B"))}";
    }

    private void WriteOneSocket(string name, List<OneSocketRun> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# BPQ experiment: {name}, {fixture.ChannelName}");
        sb.AppendLine();
        sb.AppendLine($"{fixture.RadioSettings}. Each node has one AGW socket ('X' once, 'm' once) for its listener and its calls.");
        sb.AppendLine();
        sb.AppendLine("## Outcomes");
        sb.AppendLine();
        sb.AppendLine("| Setup | Outcome | Runs |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in runs.GroupBy(r => (r.Setup, r.Outcome)))
            sb.AppendLine($"| {g.Key.Setup} | {g.Key.Outcome} | {g.Count()} of {runs.Count(r => r.Setup == g.Key.Setup)} |");
        sb.AppendLine();
        sb.AppendLine("| Setup | Run | Outcome | Timing | Frames heard |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in runs) sb.AppendLine($"| {r.Setup} | {r.Index} | {r.Outcome} | {r.Offsets} | {r.Air} |");
        sb.AppendLine();
        lock (log)
        {
            if (log.FirstOrDefault(l => l.Contains("B's 'C' to A's monitor", StringComparison.Ordinal)) is { } calibration)
                sb.AppendLine(calibration.Trim()).AppendLine();
        }
        foreach (var r in runs)
        {
            sb.AppendLine($"## {r.Setup}, run {r.Index}");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(r.Log);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(r.Transcript);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"bpq-{name}-{fixture.ChannelName.Split(' ')[0].ToLowerInvariant()}.md"), sb.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(sb.ToString());
    }

    /// <summary>
    /// A node's one AGW socket, as a single-connection daemon would hold
    /// it: registered once for the application callsign, monitor on. It
    /// logs every frame except monitor frames, which it keeps with the time
    /// they arrived (for when a peer's SABM showed).
    /// </summary>
    private sealed class NodeSocket : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly AgwFrameTransport agw;
        private readonly byte port;
        private readonly Action<string> note;
        private readonly Stopwatch clock;
        private readonly CancellationTokenSource stop = new();
        private readonly List<(char Kind, string Text)> frames = [];
        private readonly List<(double Ms, string Text)> monitored = [];

        private NodeSocket(string name, TcpClient tcp, string call, string peer, byte port, Action<string> note, Stopwatch clock)
        {
            Name = name;
            this.tcp = tcp;
            agw = new AgwFrameTransport(tcp.GetStream());
            Call = call;
            Peer = peer;
            this.port = port;
            this.note = note;
            this.clock = clock;
        }

        public string Name { get; }
        public string Call { get; }
        public string Peer { get; }

        /// <summary>When this socket sent its 'C', ms on the test's clock.</summary>
        public double? DialledAt { get; private set; }

        /// <summary>When this socket sent its 'C', UTC, to set against net-sim's channel log.</summary>
        public DateTime? DialledUtc { get; private set; }

        public static async Task<NodeSocket> OpenAsync(string name, string host, int agwPort, string call, string peer, byte port,
            Action<string> note, Stopwatch clock, bool register, bool monitor, CancellationToken ct)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, agwPort, ct);
            var s = new NodeSocket(name, tcp, call, peer, port, note, clock);
            _ = s.PumpAsync();
            if (register) await s.agw.WriteFrameAsync(new AgwFrame(0, 'X', 0, call, "", []), ct);
            if (monitor) await s.agw.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
            return s;
        }

        public async Task<double> DialAsync(CancellationToken ct, string? callFrom = null)
        {
            note($"{Name} sends 'C' {callFrom ?? Call}->{Peer}");
            await agw.WriteFrameAsync(new AgwFrame(port, 'C', 0xF0, callFrom ?? Call, Peer, []), ct);
            var at = clock.Elapsed.TotalMilliseconds;
            DialledAt ??= at;
            DialledUtc ??= DateTime.UtcNow;
            return at;
        }

        public async Task SendAsync(string text, CancellationToken ct, string? callFrom = null) =>
            await agw.WriteDataAsync(port, callFrom ?? Call, Peer, Encoding.ASCII.GetBytes(text), ct);

        public async Task HangUpAsync(CancellationToken ct, string? callFrom = null)
        {
            note($"{Name} sends 'd' {callFrom ?? Call}->{Peer}");
            await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, callFrom ?? Call, Peer, []), ct);
        }

        public async Task SendUiAsync(string from, string to, string text, CancellationToken ct)
        {
            note($"{Name} sends 'M' {from}->{to}: {text}");
            await agw.WriteFrameAsync(new AgwFrame(port, 'M', 0xF0, from, to, Encoding.ASCII.GetBytes(text)), ct);
        }

        public bool Got(char kind, string contains)
        {
            lock (frames) return frames.Any(f => f.Kind == kind && f.Text.Contains(contains, StringComparison.Ordinal));
        }

        public int CountText(char kind, string contains)
        {
            lock (frames) return frames.Count(f => f.Kind == kind && f.Text.Contains(contains, StringComparison.Ordinal));
        }

        public int Count(char kind)
        {
            lock (frames) return frames.Count(f => f.Kind == kind);
        }

        /// <summary>When this node's monitor first showed a frame from <paramref name="from"/> to <paramref name="to"/> containing <paramref name="contains"/>.</summary>
        public double? FirstMonitor(string from, string to, string contains)
        {
            var header = $"Fm {from} To {to} ";
            lock (monitored)
            {
                foreach (var (ms, text) in monitored)
                    if (text.Contains(header, StringComparison.Ordinal) && text.Contains(contains, StringComparison.Ordinal)) return ms;
            }
            return null;
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    var text = Encoding.Latin1.GetString(frame.Payload).Replace("\r", "|").Replace("\n", "|").Replace("\0", "");
                    if (frame.Kind is 'S' or 'U' or 'I' or 'T')
                    {
                        lock (monitored) monitored.Add((clock.Elapsed.TotalMilliseconds, text));
                        continue;
                    }
                    lock (frames) frames.Add((frame.Kind, text));
                    if (frame.Kind is 'G' or 'R') continue;
                    if (frame.Kind == 'X')
                    {
                        note($"{Name} got 'X' reply {(frame.Payload.Length > 0 ? frame.Payload[0] : -1)}");
                        continue;
                    }
                    note($"{Name} got '{frame.Kind}' port {frame.Port} {frame.CallFrom}->{frame.CallTo}: {text}");
                }
            }
            catch
            {
                if (!stop.IsCancellationRequested) note($"{Name}: socket closed");
            }
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One AGW socket, registered for a callsign, logging every frame it gets.</summary>
    private sealed class AgwProbe : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly AgwFrameTransport agw;
        private readonly byte port;
        private readonly string callsign;
        private readonly Action<string> note;
        private readonly CancellationTokenSource stop = new();
        private readonly List<char> kinds = [];
        private string? peer;

        private AgwProbe(string name, TcpClient tcp, byte port, string callsign, Action<string> note)
        {
            Name = name;
            this.tcp = tcp;
            agw = new AgwFrameTransport(tcp.GetStream());
            this.port = port;
            this.callsign = callsign;
            this.note = note;
        }

        public string Name { get; }

        public bool Saw(char kind)
        {
            lock (kinds) return kinds.Contains(kind);
        }

        public static async Task<AgwProbe> OpenAsync(string name, string host, int agwPort, string callsign, byte port, Action<string> note, CancellationToken ct)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, agwPort, ct);
            var probe = new AgwProbe(name, tcp, port, callsign, note);
            await probe.agw.WriteFrameAsync(new AgwFrame(0, 'X', 0, callsign, "", []), ct);
            _ = probe.PumpAsync();
            return probe;
        }

        public async Task CallAsync(string to, CancellationToken ct)
        {
            peer = to;
            note($"{Name} asks for a connect to {to}");
            await agw.WriteFrameAsync(new AgwFrame(port, 'C', 0xF0, callsign, to, []), ct);
        }

        public async Task SendAsync(string text, CancellationToken ct, string? to = null) =>
            await agw.WriteDataAsync(port, callsign, to ?? peer!, Encoding.ASCII.GetBytes(text), ct);

        public async Task HangUpAsync(CancellationToken ct) =>
            await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, callsign, peer!, []), ct);

        private async Task PumpAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    lock (kinds) kinds.Add(frame.Kind);
                    if (frame.Kind is 'G' or 'R') continue;
                    var text = Encoding.Latin1.GetString(frame.Payload).Replace("\r", "|").Replace("\n", "|").Replace("\0", "");
                    note($"{Name} got '{frame.Kind}' port {frame.Port} {frame.CallFrom}->{frame.CallTo}: {text}");
                }
            }
            catch
            {
                note($"{Name}: socket closed");
            }
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

[Collection("net-sim AFSK 1200")]
[Trait("Category", "Integration")]
public sealed class BpqCrossedCallExperimentsAfsk1200(NetSimAfsk1200Fixture fixture) : BpqCrossedCallExperiments(fixture);

[Collection("net-sim QPSK 3600")]
[Trait("Category", "Integration")]
public sealed class BpqCrossedCallExperimentsQpsk3600(NetSimQpsk3600Fixture fixture) : BpqCrossedCallExperiments(fixture);
