using AwesomeAssertions;
using dapps.client.Backhaul;
using Microsoft.Extensions.Logging.Abstractions;
using static dapps.core.tests.ExchangeTestKit;

namespace dapps.core.tests;

/// <summary>
/// #178, the crossed call: two neighbours dial each other within one
/// round trip, both links come up, and each node's BPQ attaches its link
/// to its own outbound session. Neither side is handed an inbound
/// connect, so neither sends <c>DAPPSv1&gt;</c>, and from each caller's
/// point of view the link is simply silent. After
/// <see cref="Dappsv1SessionBackhaul.PromptWait"/> each caller sends its
/// <c>exchange</c> anyway, and the two sessions carry on as if one had
/// answered: no tie-break, no role switch.
/// </summary>
public sealed class CrossedConnectTests
{
    private const string Lower = "G5ALF-3";
    private const string Higher = "M0AHN-3";
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task BothEndsDialAtOnce_EachSendsItsRulesAfterTheWait_AndBothMessagesArrive()
    {
        var ct = TestContext.Current.CancellationToken;
        var (atLower, atHigher) = await LoopbackPairAsync(ct);
        var lowerInbox = new RecordingInbox();
        var higherInbox = new RecordingInbox();
        var clock = new WatchedClock();
        var lower = new Dappsv1SessionBackhaul(new OneStreamTransport(atLower), NullLoggerFactory.Instance, lowerInbox) { TimeProvider = clock, PromptWait = Wait, MinQuiet = Wait };
        var higher = new Dappsv1SessionBackhaul(new OneStreamTransport(atHigher), NullLoggerFactory.Instance, higherInbox) { TimeProvider = clock, PromptWait = Wait, MinQuiet = Wait };
        var toHigher = Message("from the lower call", $"app@{Higher}", 1);
        var toLower = Message("from the higher call", $"app@{Lower}", 2);

        var sends = Task.WhenAll(
            lower.SendAsync(toHigher, new BackhaulRoute(Higher, BearerPort: 1), Lower, ct),
            higher.SendAsync(toLower, new BackhaulRoute(Lower, BearerPort: 1), Higher, ct));
        await clock.WaitForArmedAsync(2, ct);   // both callers waiting for a prompt
        clock.Advance(Wait);
        var results = await sends.WaitAsync(Patience, ct);

        results.Should().AllSatisfy(r => r.Accepted.Should().BeTrue());
        await higherInbox.WaitForAsync(1, ct);
        await lowerInbox.WaitForAsync(1, ct);
        higherInbox.Texts.Should().Equal("from the lower call");
        higherInbox.Sources.Should().Equal(Lower);
        lowerInbox.Texts.Should().Equal("from the higher call");
        lowerInbox.Sources.Should().Equal(Higher);
    }

    [Fact]
    public async Task ACallerThatHearsThePeersRulesWithoutAPrompt_SendsItsOwnAtOnce()
    {
        // The other caller's wait ran out first: its rules are the first
        // thing we hear, and there's no point waiting any longer.
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var backhaul = new Dappsv1SessionBackhaul(new OneStreamTransport(ours), NullLoggerFactory.Instance, new RecordingInbox())
        {
            PromptWait = TimeSpan.FromSeconds(30),
            MinQuiet = Wait,
        };
        var peer = new LinePeer(theirs);
        var message = Message("hello", $"app@{Lower}");

        var send = backhaul.SendAsync(message, new BackhaulRoute(Lower, BearerPort: 1), Higher, ct);
        await peer.WriteLineAsync("exchange id=cafe01 hold=0 inline=256 z=1", ct);

        (await peer.ReadLineAsync(ct, TimeSpan.FromSeconds(5))).Should().StartWith("exchange ");
        (await peer.ReadWithPayloadAsync(ct)).Line.Should().StartWith($"msg {message.Id} ");
        await peer.WriteLineAsync($"ack {message.Id}", ct);
        (await send.WaitAsync(Patience, ct)).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task APromptThatComesAfterTheWait_StillLeadsToTheExchange()
    {
        // A slow answering node, not a crossed call after all: our rules
        // have gone, its prompt and rules follow, and the session goes on.
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var clock = new WatchedClock();
        var backhaul = new Dappsv1SessionBackhaul(new OneStreamTransport(ours), NullLoggerFactory.Instance, new RecordingInbox())
        {
            TimeProvider = clock,
            PromptWait = Wait,
            MinQuiet = Wait,
        };
        var peer = new LinePeer(theirs);
        var message = Message("hello", $"app@{Lower}");

        var send = backhaul.SendAsync(message, new BackhaulRoute(Lower, BearerPort: 1), Higher, ct);
        await clock.WaitForArmedAsync(1, ct);

        clock.Advance(Wait - TimeSpan.FromMilliseconds(1));
        (await peer.TryReadLineAsync(Wait / 2, ct)).Should().BeNull("nothing is sent before the wait is over");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");
        (await peer.TryReadLineAsync(Wait, ct)).Should().BeNull("no contents before it has our peer's rules");

        await peer.WriteLineAsync("DAPPSv1>\nexchange id=slow01 hold=0 inline=256 z=1", ct);
        (await peer.ReadWithPayloadAsync(ct)).Line.Should().StartWith($"msg {message.Id} ");
        await peer.WriteLineAsync($"ack {message.Id}", ct);
        (await send.WaitAsync(Patience, ct)).Accepted.Should().BeTrue();
    }
}
