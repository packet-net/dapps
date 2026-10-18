using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="AgwUiDiscoveryBearer"/> on the daemon's one AGW
/// connection (#272): the <see cref="AgwInboundService"/>'s link, against
/// a <see cref="FakeAgwSocket"/> playing BPQ. Beacons go out on the link
/// from its callsign, are heard on the configured ports only, keep being
/// heard across reconnects, are not sent or held back while it is down,
/// and with another bearer the link only listens.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AgwUiDiscoveryTests
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";

    private static DiscoveryChannelInfo Channel(int id, byte port) =>
        new(id, "agw", port.ToString(), LinkClass.VhfUhfFm,
            BeaconIntervalSeconds: 1800, AdvertisedTtlSeconds: 5400, CostHint: 5);

    [Fact]
    public async Task Beacons_GoOutOnTheLink_FromItsCallsign_WithNoSocketRegistrationOrMonitorOfTheirOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 0), Channel(2, 1)], ct);

        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        var beacon = await bpq.ReadUntilAsync('M', ct);
        (beacon.Port, beacon.CallFrom, beacon.CallTo, beacon.Pid).Should().Be(((byte)1, Local, "DAPPS", (byte)0xF0));
        Encoding.ASCII.GetString(beacon.Payload).Should().StartWith("DAPPS v1");

        await bearer.SolicitAsync(new SolicitFrame(Local), "0", ct);
        var solicit = await bpq.ReadUntilAsync('M', ct);
        (solicit.Port, solicit.CallFrom).Should().Be(((byte)0, Local));
        Encoding.ASCII.GetString(solicit.Payload).Should().StartWith("DAPPS v1 solicit ");

        // #274: BPQ's 'm' toggles one flag per socket; the link sent the one.
        bpq.Received.Where(f => f.Kind != 'G').Select(f => f.Kind).Should().Equal(['X', 'm', 'M', 'M']);
        h.Host.Pending().Should().BeFalse("the bearer opens no socket of its own");
    }

    [Fact]
    public async Task OnlyUiFramesOnTheConfiguredPorts_AreHeard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1), Channel(2, 3)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        await bpq.WriteAsync(ct,
            Ui(2, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"),      // no channel on port 2
            Ui(1, Local, $"DAPPS v1 callsign={Local} hops=0 ttl=600"),          // our own
            new AgwFrame(1, 'S', 0, "G7XYZ-9", "DAPPS", Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")),
            new AgwFrame(1, 'T', 0xF0, "G7XYZ-9", "DAPPS", Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")),
            Monitored(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=1 ttl=600", junk: "@`\u0001x"),   // an older sender's
            Ui(3, "G0AXP", "DAPPS v1 solicit callsign=G0AXP"),
            Monitored(3, "M0END", "DAPPS v1 callsign=M0END hops=0 ttl=600 end=1", junk: "9 Z3"));

        var first = (ReceivedBeacon)await NextAsync(heard, ct);
        (first.Beacon.Callsign, first.ChannelKey, first.Beacon.Hops, first.Beacon.Ttl).Should().Be(("G7XYZ-9", "1", 1, 600));
        ((AgwBearerHint)first.Beacon.Bearer).BearerPort.Should().Be(1);
        var second = (ReceivedSolicit)await NextAsync(heard, ct);
        (second.Solicit.Callsign, second.ChannelKey).Should().Be(("G0AXP", "3"));
        var third = (ReceivedBeacon)await NextAsync(heard, ct);
        (third.Beacon.Callsign, third.Beacon.Ttl).Should().Be(("M0END", 600), "nothing else in between was a UI frame from a peer on a configured port");
    }

    [Fact]
    public async Task WhileTheLinkIsDown_NothingIsSent_AndTheSubscriptionHearsTheNextLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        bpq.Close();
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
        bearer.Connection.Should().BeNull();
        await FluentActions.Awaiting(() => bearer.AnnounceAsync(new BeaconFrame(Local, 0, 111, new AgwBearerHint(1)), "1", ct)
                .WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<AgwLinkDownException>();

        clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
        using var again = await AcceptRegisteredAsync(h.Host, ct);
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        bearer.Connection.Should().BeSameAs(link);

        await again.WriteAsync(ct, Ui(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"));
        ((ReceivedBeacon)await NextAsync(heard, ct)).Beacon.Callsign.Should().Be("G7XYZ-9");

        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 222, new AgwBearerHint(1)), "1", ct);
        var beacon = await again.ReadUntilAsync('M', ct);
        Encoding.ASCII.GetString(beacon.Payload).Should().Contain("ttl=222", "the beacon tried while the link was down was not held back");
        again.Received.Count(f => f.Kind == 'M').Should().Be(1);
    }

    [Fact]
    public async Task WithAnotherBearer_TheLinkOnlyListens_SendingAndHearingBeacons_AndTakingNoCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        var rhp = new RecordingUiSender();
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs, rhp);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        await h.Service.StartAsync(ct);
        using var bpq = await AcceptListenOnlyAsync(h.Host, ct);
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        link!.ListenOnly.Should().BeTrue();
        h.Service.State.Should().Be(AgwLinkState.Open);

        // XRouter drops an 'M' from a socket that registered no callsign:
        // the beacon goes through the bearer, from our callsign, and the
        // link sends none.
        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        var sent = rhp.Sent.Should().ContainSingle().Subject;
        (sent.Port, sent.From, sent.To).Should().Be(((byte)1, Local, "DAPPS"));
        Encoding.ASCII.GetString(sent.Payload).Should().StartWith($"DAPPS v1 callsign={Local}");
        await FluentActions.Awaiting(() => link.SendUiAsync(1, "DAPPS", [1], ct)).Should().ThrowAsync<InvalidOperationException>();
        await bpq.WriteAsync(ct, Ui(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"));
        ((ReceivedBeacon)await NextAsync(heard, ct)).Beacon.Callsign.Should().Be("G7XYZ-9");

        // A call reaching it all the same is let go, with no prompt.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, 1));
        var bye = await bpq.ReadUntilAsync('d', ct);
        (bye.CallFrom, bye.CallTo, bye.Port).Should().Be((Local, Remote, (byte)1));

        // Nor does it call out.
        (await FluentActions.Awaiting(() => new AgwOutboundTransport(h.Service, h.Logs).ConnectAsync(Local, Remote, 1, ct))
            .Should().ThrowAsync<AgwLinkDownException>()).Which.Message.Should().Contain("only listens");
        await FluentActions.Awaiting(() => link.DialAsync(1, Local, Remote, TimeSpan.FromMinutes(3), ct))
            .Should().ThrowAsync<InvalidOperationException>();

        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'C' || f.Kind == 'D' || f.Kind == 'X' || f.Kind == 'M');
        bpq.Received.Should().NotContain(f => f.Kind == 'X' || f.Kind == 'D' || f.Kind == 'M');
        h.Metrics.Take().AgwReconnects.Should().Be(0, "nothing was registered");
    }

    [Fact]
    public async Task AListenOnlyLinkWithNothingToSendThrough_SendsNoM()
    {
        // Falling back to an 'M' would look sent and go nowhere on XRouter.
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await h.Service.StartAsync(ct);
        using var bpq = await AcceptListenOnlyAsync(h.Host, ct);
        await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);

        (await FluentActions.Awaiting(() => bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct))
            .Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("nothing else sends UI frames");
        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'M');
    }

    [Fact]
    public async Task ABearerSwitch_ReopensTheLinkListenOnly_AndBackWithItsRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var rhp = new RecordingUiSender();
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs, rhp);
        await bearer.StartAsync([Channel(1, 1)], ct);
        using var full = await h.StartAsync(ct);

        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        clock.Advance(AgwInboundService.NonFailureRetryDelay);
        using var listening = await AcceptListenOnlyAsync(h.Host, ct);
        (await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct))!.ListenOnly.Should().BeTrue();
        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        rhp.Sent.Should().ContainSingle().Which.From.Should().Be(Local);
        (await listening.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'M');

        h.Options.Set(h.With(o => o.NodeBearer = "agw"));
        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        clock.Advance(AgwInboundService.NonFailureRetryDelay);
        using var registered = await AcceptRegisteredAsync(h.Host, ct);
        (await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct))!.ListenOnly.Should().BeFalse();

        // Registered again: the beacon goes on the link as an 'M'.
        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        (await registered.ReadUntilAsync('M', ct)).CallFrom.Should().Be(Local);
        rhp.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task AListenOnlyLink_ClosesOnceNoDiscoveryChannelWantsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await h.Service.StartAsync(ct);
        using var bpq = await AcceptListenOnlyAsync(h.Host, ct);
        await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);

        await bearer.DisposeAsync();
        await h.Logs.WaitForAsync("connection withdrawn (Idle)", ct);
        h.Service.State.Should().Be(AgwLinkState.Idle);
        h.Service.TryGetLink().Should().BeNull();
    }

    [Fact]
    public async Task ACallsignChange_SendsTheNextBeaconFromTheNewCallsign_AtOnce()
    {
        // #274: the bearer used to keep the callsign it was built with.
        // It now has none of its own: the link reconnects under the new
        // one, and the service announces at once on the new link.
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            await using var h = new AgwInboundServiceHarness("M0OLD-1");
            var service = await StartDiscoveryAsync(h, beaconIntervalSeconds: 1800, ct);
            try
            {
                using var first = await h.StartAsync(ct);
                (await first.ReadUntilAsync('M', ct)).CallFrom.Should().Be("M0OLD-1");

                h.Options.Set(h.With(o => o.Callsign = "M0NEW-1"));

                using var second = await h.Host.AcceptAsync(ct);
                (await second.ExpectRegisterAsync(ct)).CallFrom.Should().Be("M0NEW-1");
                await second.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
                var beacon = await second.ReadUntilAsync('M', ct);
                beacon.CallFrom.Should().Be("M0NEW-1", "beacons go out as the new callsign");
                Encoding.ASCII.GetString(beacon.Payload).Should().Contain("callsign=M0NEW-1");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task WhileTheLinkIsDown_NoBeaconIsTried_AndOneGoesWhenItIsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            var clock = new ObservableTimeProvider();
            await using var h = new AgwInboundServiceHarness(Local, clock);
            var service = await StartDiscoveryAsync(h, beaconIntervalSeconds: 5, ct);
            try
            {
                var bpq = await h.StartAsync(ct);
                await bpq.ReadUntilAsync('M', ct);
                bpq.Close();
                await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);

                // More than two beacon intervals with the link down.
                await Task.Delay(TimeSpan.FromSeconds(11), ct);
                h.Logs.Any("announce on agw/1 failed").Should().BeFalse("nothing is tried while the link is down");

                clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
                using var again = await AcceptRegisteredAsync(h.Host, ct);
                await again.ReadUntilAsync('M', ct);
                (await again.DrainAsync(ct, TimeSpan.FromSeconds(2))).Should().NotContain(f => f.Kind == 'M',
                    "one beacon on the new link, not one for each interval missed");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task AListenOnlyHostThatAcceptsAndCloses_BacksOffAlongTheRamp()
    {
        // With no 'X' to answer, a bare TCP connect used to count as a
        // working link and reset the backoff each time.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await h.Service.StartAsync(ct);

        (await AcceptListenOnlyAsync(h.Host, ct, answer: false)).Close();
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
        clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
        (await AcceptListenOnlyAsync(h.Host, ct, answer: false)).Close();
        await h.Logs.WaitForAsync("(attempt 2,", ct);
        h.Service.TryGetLink().Should().BeNull("no link was ever published");
    }

    [Fact]
    public async Task AChannelEditWithAnotherBearer_KeepsTheListenOnlyLinkUp()
    {
        // A channel edit restarts the bearer: the old one's subscription
        // goes a moment before the new one's comes, which used to close
        // the link, reopen it and beacon again.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        var old = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await old.StartAsync([Channel(1, 1)], ct);
        await h.Service.StartAsync(ct);
        using var bpq = await AcceptListenOnlyAsync(h.Host, ct);
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);

        await old.DisposeAsync();
        await using var edited = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await edited.StartAsync([Channel(1, 3)], ct);
        await using var heard = edited.ListenAsync(ct).GetAsyncEnumerator(ct);
        await clock.WaitForTimerAsync(AgwInboundService.ListenOnlyLinger, ct);
        clock.Advance(AgwInboundService.ListenOnlyLinger);

        await bpq.WriteAsync(ct, Ui(3, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600 end=1"));
        ((ReceivedBeacon)await NextAsync(heard, ct)).Beacon.Callsign.Should().Be("G7XYZ-9");
        h.Service.TryGetLink().Should().BeSameAs(link);
        h.Logs.Any("closing the listen-only connection").Should().BeFalse();
        h.Host.Pending().Should().BeFalse();
    }

    [Fact]
    public async Task AFlappingLink_DoesNotBeaconOnEachReconnect()
    {
        // Seen live: 7 beacons in 60 s on a 2-hour channel.
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            var clock = new ObservableTimeProvider();
            await using var h = new AgwInboundServiceHarness(Local, clock);
            var service = await StartDiscoveryAsync(h, beaconIntervalSeconds: 7200, ct);
            try
            {
                var bpq = await h.StartAsync(ct);
                await bpq.ReadUntilAsync('M', ct);
                for (var flap = 1; flap <= 3; flap++)
                {
                    // Only a timer started after the close is the backoff's.
                    // GetLinkAsync on this clock starts a wait of the same
                    // 10 s when it runs before the link is published; matched
                    // here instead, the advance came before the backoff's
                    // timer and the reconnect never came.
                    clock.ForgetCreatedTimers();
                    bpq.Close();
                    await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
                    clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
                    bpq = await AcceptRegisteredAsync(h.Host, ct);
                    await WaitForLinkAsync(h.Service, ct);
                    (await bpq.DrainAsync(ct, TimeSpan.FromSeconds(1.5))).Should().NotContain(f => f.Kind == 'M',
                        $"the channel beaconed moments ago (reconnect {flap})");
                }
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task ABeaconThatNeverWent_IsNotChargedAgainstTheBudget_AndIsLoggedAsOneLine()
    {
        // The link going in the moment between the service's check and
        // the send: the announce fails with AgwLinkDownException.
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            using var host = new FakeAgwHost();
            var logs = new CapturingLoggerFactory();
            var options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Local });
            var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, _ => { }, logs.CreateLogger("link"), ct: ct);
            using var node = await AcceptRegisteredAsync(host, ct);
            await using var link = await opening;
            var airtime = new AirtimeAccountant(options, TimeProvider.System, new Logger<AirtimeAccountant>(logs));
            var service = await StartDiscoveryAsync(options, logs, new FlickeringLinkSource(link), 1800, ct, airtime);
            try
            {
                var line = await logs.WaitForAsync("announce on agw/1 failed", ct);
                line.Exception.Should().BeNull("a link-down announce is one line, no stack trace");
                airtime.ConsumedSecondsLastHour.Should().Be(0);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABeaconTheSenderSaysWasNotSent_IsNotCharged_AndIsOneLine_ButOneThatMayHaveGoneIs(bool notSent)
    {
        // Through RHPv2 from a listen-only link: a refusal, a port that
        // cannot be matched or no node is known not to have gone. A
        // timeout once the sendto went may have, and stays charged.
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            await using var h = new AgwInboundServiceHarness(Local);
            h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
            var airtime = new AirtimeAccountant(h.Options, TimeProvider.System, new Logger<AirtimeAccountant>(h.Logs));
            var sender = new ThrowingUiSender(notSent
                ? new UiFrameNotSentException("RHP: not sending on AGW port 1: test")
                : new IOException("RHP: the UI frame may have gone"));
            var service = await StartDiscoveryAsync(h.Options, h.Logs, h.Service, 1800, ct, airtime, sender: sender);
            try
            {
                await h.Service.StartAsync(ct);
                using var bpq = await AcceptListenOnlyAsync(h.Host, ct);
                var line = await h.Logs.WaitForAsync("announce on agw/1 failed", ct);
                if (notSent)
                {
                    line.Exception.Should().BeNull("one line, no stack trace");
                    airtime.ConsumedSecondsLastHour.Should().Be(0);
                }
                else
                {
                    airtime.ConsumedSecondsLastHour.Should().BeGreaterThan(0);
                }
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task WhileTheLinkIsDown_TheServiceDoesNotSpin()
    {
        // An overdue beacon on a down bearer used to make the loop wake
        // every 50 ms; it now waits for its usual tick (at most 1 s).
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            var logs = new CapturingLoggerFactory();
            var options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Local });
            var down = new FlickeringLinkSource(null);
            var service = await StartDiscoveryAsync(options, logs, down, 1800, ct, reload: TimeSpan.FromSeconds(10));
            try
            {
                await logs.WaitForAsync("started bearer 'agw'", ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var before = down.Calls;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                (down.Calls - before).Should().BeLessThan(10, "about one look a second, not twenty");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    private static Task<DiscoveryService> StartDiscoveryAsync(AgwInboundServiceHarness h, int beaconIntervalSeconds, CancellationToken ct) =>
        StartDiscoveryAsync(h.Options, h.Logs, h.Service, beaconIntervalSeconds, ct);

    private static async Task<DiscoveryService> StartDiscoveryAsync(
        MutableOptionsMonitor<SystemOptions> options, CapturingLoggerFactory logs, IAgwLinkSource links, int beaconIntervalSeconds,
        CancellationToken ct, AirtimeAccountant? airtime = null, TimeSpan? reload = null, IUiFrameSender? sender = null)
    {
        var database = new Database(NullLogger<Database>.Instance, options);
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm, BeaconIntervalSeconds = beaconIntervalSeconds,
        });
        var service = new DiscoveryService(
            database, options, TimeProvider.System, logs, new Logger<DiscoveryService>(logs), airtime: airtime, agwLinks: links,
            listenOnlyUiSender: sender)
        {
            ChannelReloadInterval = reload ?? TimeSpan.FromMilliseconds(100),
        };
        await service.StartAsync(ct);
        return service;
    }

    /// <summary>Accepts the service's next connection, a listen-only one:
    /// the 'm', then a 'G' it waits for an answer to, with no 'X'.</summary>
    private static async Task<FakeAgwSocket> AcceptListenOnlyAsync(FakeAgwHost host, CancellationToken ct, bool answer = true)
    {
        var socket = await host.AcceptAsync(ct);
        (await socket.ReadFrameAsync(ct)).Kind.Should().Be('m', "a listen-only link registers no callsign");
        (await socket.ReadFrameAsync(ct)).Kind.Should().Be('G');
        if (answer) await socket.WriteAsync(ct, PortsReply);
        return socket;
    }

    /// <summary>BPQ's answer to a 'G': its port count and names.</summary>
    private static readonly AgwFrame PortsReply = new(0, 'G', 0, "", "", Encoding.ASCII.GetBytes("2;Port1 Telnet;Port2 AXIP;\0"));

    private static async Task WithDatabaseAsync(Func<Task> test)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-disc-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        try
        {
            using (var c = DbInfo.GetConnection())
            {
                c.CreateTable<DbDiscoveredPeer>();
                c.CreateTable<DbDiscoveryChannel>();
            }
            await test();
        }
        finally
        {
            DbInfo.OverridePath = null;
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    /// <summary>Accepts the service's next connection and plays BPQ up to
    /// its monitor 'm'.</summary>
    /// <summary>Waits in real time for the service to publish a link,
    /// starting no timer on the test's clock.</summary>
    private static async Task WaitForLinkAsync(AgwInboundService service, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + FakeAgwHost.FrameTimeout;
        while (service.TryGetLink() is null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the link was not published");
            await Task.Delay(10, ct);
        }
    }

    private static async Task<FakeAgwSocket> AcceptRegisteredAsync(FakeAgwHost host, CancellationToken ct)
    {
        var socket = await host.AcceptAsync(ct);
        await socket.ExpectRegisterAsync(ct);
        await socket.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await socket.ReadUntilAsync('m', ct);
        return socket;
    }

    private static async Task<ReceivedDiscoveryFrame> NextAsync(IAsyncEnumerator<ReceivedDiscoveryFrame> heard, CancellationToken ct)
    {
        (await heard.MoveNextAsync().AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().BeTrue();
        return heard.Current;
    }

    /// <summary>A UI frame's bare data, as the tests' node has always sent
    /// it. The header's CallTo is whatever BPQ last put there.</summary>
    private static AgwFrame Ui(byte port, string from, string text) =>
        new(port, 'U', 0xF0, from, "STALE", Encoding.ASCII.GetBytes(text));

    /// <summary>A UI frame as 64-bit LinBPQ's monitor hands it over: a
    /// header line numbering the port from 1, a CR, the data, 4 stale
    /// bytes (M0LTE/linbpq#75), a CR.</summary>
    private static AgwFrame Monitored(byte port, string from, string text, string junk) =>
        new(port, 'U', 0xF0, from, "STALE",
            Encoding.ASCII.GetBytes($" {port + 1}:Fm {from} To DAPPS <UI pid=F0 Len={text.Length + junk.Length}>[12:00:00]\r{text}{junk}\r\0"));

    /// <summary>A link source whose link is there on every other look:
    /// the service's check sees it, the send that follows does not.
    /// With no link, always down.</summary>
    private sealed class FlickeringLinkSource(AgwLink? link) : IAgwLinkSource
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public AgwLinkState State => AgwLinkState.Backoff;

        public AgwLink? TryGetLink() => Interlocked.Increment(ref calls) % 2 == 1 ? link : null;

        public Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(null);

        public Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(null);

        public IDisposable SubscribeUi(Action<AgwFrame> onUi) => new NoSubscription();

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>Stands in for the RHPv2 sender: records each UI frame.</summary>
    private sealed class RecordingUiSender : IUiFrameSender
    {
        public List<(byte Port, string From, string To, byte[] Payload)> Sent { get; } = [];

        public Task SendUiAsync(byte bearerPort, string from, string to, byte[] payload, AgwPortList? agwPorts, CancellationToken ct)
        {
            lock (Sent) Sent.Add((bearerPort, from, to, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingUiSender(Exception failure) : IUiFrameSender
    {
        public Task SendUiAsync(byte bearerPort, string from, string to, byte[] payload, AgwPortList? agwPorts, CancellationToken ct) =>
            Task.FromException(failure);
    }
}
