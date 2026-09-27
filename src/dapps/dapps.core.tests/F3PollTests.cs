using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static dapps.core.tests.ExchangeTestKit;

namespace dapps.core.tests;

/// <summary>
/// Plan F3 - polling a neighbour for mail it holds for us. A poll is an
/// ordinary exchange session with nothing of our own to send
/// (<see cref="NodePoller"/>): we dial, state our rules, take whatever
/// the neighbour sends, and hang up once the link goes quiet. Mail also
/// comes back on every session we dial for our own traffic; that's in
/// <see cref="PersistentSessionTests"/> and <see cref="BatchedSessionTests"/>.
/// </summary>
public sealed class F3PollTests
{
    private const string Us = "N0US";
    private const string Them = "N0THEM-9";
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task APoll_TakesWhatTheNeighbourSends_SendsNothingOfItsOwn_AndHangsUpWhenQuiet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var inbox = new RecordingInbox();
        var poller = MakePoller(ours, inbox);
        var peer = new LinePeer(theirs);
        var first = Message("queued one", $"app@{Us}", 1);
        var second = Message(new string('p', 400), $"app@{Us}", 2);

        var poll = poller.PollAsync(Us, Them, 1, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=0 inline=256 z=1", ct);
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");
        await peer.SendMessageAsync(first, ct);
        await peer.WriteLineAsync(Line("ihave", second).TrimEnd('\n'), ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"ack {first.Id}");
        (await peer.ReadLineAsync(ct)).Should().Be($"send {second.Id}");
        await peer.WriteAsync([.. Encoding.UTF8.GetBytes($"data {second.Id}\n"), .. second.Payload], ct);
        (await peer.ReadLineAsync(ct)).Should().Be($"ack {second.Id}");
        (await peer.ReadLineAsync(ct)).Should().Be("quit", "nothing of ours to send, and the link has gone quiet");
        await peer.WriteLineAsync("bye", ct);

        var result = await poll.WaitAsync(Patience, ct);
        result.Success.Should().BeTrue(result.Error);
        result.MessagesDrained.Should().Be(2);
        inbox.Texts.Should().Equal("queued one", new string('p', 400));
        inbox.Sources.Should().AllBe(Them);
    }

    [Fact]
    public async Task APoll_OfANeighbourWithNothing_StillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var poller = MakePoller(ours, new RecordingInbox());
        var peer = new LinePeer(theirs);

        var poll = poller.PollAsync(Us, Them, 1, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=0 inline=256", ct);
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");
        (await peer.ReadLineAsync(ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", ct);

        var result = await poll.WaitAsync(Patience, ct);
        result.Success.Should().BeTrue(result.Error);
        result.MessagesDrained.Should().Be(0);
    }

    [Fact]
    public async Task APoll_ThatNeverReachesTheExchange_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var poller = MakePoller(new FakeDuplexStream("Welcome to the node\r"u8.ToArray()), new RecordingInbox());

        var result = await poller.PollAsync(Us, Them, 1, ct).WaitAsync(Patience, ct);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task RoutesArePulledBeforeTheExchange_WhenTheGossipGateSaysSo()
    {
        // Command and response while neither side sends traffic yet, so
        // the route lines can't interleave with messages.
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var gossip = new RecordingGossip();
        var poller = MakePoller(ours, new RecordingInbox(), gossip);
        var peer = new LinePeer(theirs);

        var poll = poller.PollAsync(Us, Them, 1, ct);
        await peer.WriteLineAsync("DAPPSv1>\nexchange id=far001 hold=0 inline=256", ct);
        (await peer.ReadLineAsync(ct)).Should().Be("routes");
        (await peer.TryReadLineAsync(Quiet, ct)).Should().BeNull("our rules wait for the answer");
        await peer.WriteLineAsync("route G0ONE hops=1\nroute G0TWO hops=2 ageSeconds=60\nend", ct);
        (await peer.ReadLineAsync(ct)).Should().StartWith("exchange ");
        (await peer.ReadLineAsync(ct)).Should().Be("quit");
        await peer.WriteLineAsync("bye", ct);

        (await poll.WaitAsync(Patience, ct)).Success.Should().BeTrue();
        gossip.Imported.Should().Equal("G0ONE", "G0TWO");
        gossip.Pulled.Should().Be(1);
    }

    private static NodePoller MakePoller(Stream stream, IBackhaulInbox inbox, IRouteGossipPort? gossip = null) =>
        new(new OneStreamTransport(stream), inbox, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodePoller>.Instance, gossip)
        {
            MinQuiet = Quiet,
        };

    private sealed class RecordingGossip : IRouteGossipPort
    {
        public List<string> Imported { get; } = [];
        public int Pulled { get; private set; }

        public Task<bool> ShouldPullAsync(string remoteCallsign, CancellationToken ct) => Task.FromResult(Pulled == 0);

        public Task ImportAsync(string advertiserCallsign, IReadOnlyList<DappsProtocolClient.GossipedRoute> routes, CancellationToken ct)
        {
            Imported.AddRange(routes.Select(r => r.DestinationBaseCallsign));
            return Task.CompletedTask;
        }

        public Task RecordPulledAsync(string remoteCallsign, CancellationToken ct)
        {
            Pulled++;
            return Task.CompletedTask;
        }
    }
}

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
