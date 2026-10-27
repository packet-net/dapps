using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static dapps.core.tests.ExchangeTestKit;

namespace dapps.core.tests;

/// <summary>
/// The answering side of a session: what a probe, a person or a simple
/// sender sees before any exchange, and the session registering for the
/// forwarder once the caller's <c>exchange</c> arrives.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class InboundConnectionHandlerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-handler-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbLearnedRoute>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
        }
        database = new Database(NullLogger<Database>.Instance, new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0US" }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BeforeAnExchange_HelpIsAnswered_AndAnUnknownCommandEndsTheSession()
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes("help\nbogus\nhelp\n"));
        var handler = new InboundConnectionHandler(stream, "N0THEM", NullLoggerFactory.Instance, database, new RecordingInbox(),
            settingsFor: (_, _) => Task.FromResult(new ExchangeSettings(HoldSeconds: 90, MaxBytes: 2048)));

        await handler.Handle(TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var lines = Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("DAPPSv1>");
        var rules = ExchangeRules.Parse(lines[1]);
        rules.HoldSeconds.Should().Be(90);
        rules.MaxBytes.Should().Be(2048);
        lines[2].Should().StartWith("This is DAPPS");
        lines[3].Should().Be("eh?");
        lines.Should().HaveCount(4, "nothing after the unknown command is read");
    }

    [Fact]
    public async Task TheCallersExchange_OpensTheSessionForTheForwarder_UntilItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var directory = new SessionDirectory();
        var handler = new InboundConnectionHandler(ours, "N0THEM", NullLoggerFactory.Instance, database, new RecordingInbox(), directory: directory);
        var run = handler.Handle(ct);
        var peer = new LinePeer(theirs);
        await peer.ReadLineAsync(ct);
        await peer.ReadLineAsync(ct);
        directory.TryHand("N0THEM", new RecordingBatch()).Should().BeFalse("no exchange yet: a probe's session takes no traffic");

        await peer.WriteLineAsync("exchange id=them01 hold=0 inline=256", ct);
        var message = Message("for you", "app@N0THEM");
        var deadline = DateTime.UtcNow + Patience;
        while (!directory.TryHand("N0THEM", new RecordingBatch(message)) && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        (await peer.ReadWithPayloadAsync(ct)).Line.Should().StartWith($"msg {message.Id} ");

        await peer.WriteLineAsync("quit", ct);
        (await peer.ReadLineAsync(ct)).Should().Be("bye");
        await run.WaitAsync(Patience, ct);
        directory.TryHand("N0THEM", new RecordingBatch()).Should().BeFalse("the session has gone");
    }

    [Theory]
    [InlineData("GB7LOX-6", "GB7LOX", "GB7LOX")]
    [InlineData("G6FCI-7", "G6FCI-8", "G6FCI-8")]
    [InlineData("GB7LOX-6", null, "GB7LOX-6")]
    [InlineData("GB7LOX-6", "G6FCI-8", "GB7LOX-6")]
    [InlineData("GB7LOX-6", "GB7LOX-16", "GB7LOX-6")]
    [InlineData("GB7LOX-6", "GB7LOX-foo", "GB7LOX-6")]
    [InlineData("GB7LOX-6", "GB7LOX-", "GB7LOX-6")]
    [InlineData("GB7LOX-6", "GB7LOX--6", "GB7LOX-6")]
    [InlineData("GB7LOX-6", "", "GB7LOX-6")]
    public async Task NodeDeclaration_IdentifiesOnlyAValidSameBasePeer_OnTheFirstExchange_AndDeliveryUsesThatIdentity(
        string source, string? node, string identity)
    {
        var ct = TestContext.Current.CancellationToken;
        var (ours, theirs) = await LoopbackPairAsync(ct);
        var directory = new SessionDirectory();
        var peers = new PeerSessionRegistry();
        using var lease = peers.Acquire(source, "inbound");
        using var logs = new CapturingLoggerFactory();
        var inbox = new RecordingInbox();
        var handler = new InboundConnectionHandler(ours, source, logs, database, inbox,
            directory: directory, nodeCallsign: "N0US", lease: lease);
        var run = handler.Handle(ct);
        var peer = new LinePeer(theirs);
        var unclaimed = identity == source ? source[..source.IndexOf('-')] : source;
        var laterNode = source.StartsWith("GB7LOX", StringComparison.Ordinal) ? "GB7LOX-8" : "G6FCI-9";
        var declaration = node is null ? "" : $" node={node}";
        try
        {
            (await peer.ReadLineAsync(ct)).Should().Be("DAPPSv1>");
            ExchangeRules.Parse(await peer.ReadLineAsync(ct)).Node.Should().Be("N0US");
            directory.CallerFor($"app@{identity}").Should().BeNull("no exchange has identified the peer yet");
            peers.IsActive(source, out _).Should().BeTrue();

            await peer.WriteLineAsync($"exchange id=them01 hold=0 inline=256{declaration}\nping identified", ct);
            (await peer.ReadLineAsync(ct)).Should().Be("pong identified");

            var session = directory.Find(identity);
            session.Should().NotBeNull();
            session!.Peer.Should().Be(identity);
            directory.CallerFor($"app@{identity}").Should().Be(identity);
            directory.CallerFor($"app@{unclaimed}").Should().BeNull();
            directory.TryHand(identity, new RecordingBatch()).Should().BeTrue();
            directory.TryHand(unclaimed, new RecordingBatch()).Should().BeFalse("no sibling stands in for the identified peer");
            lease.Peer.Should().Be(identity);
            peers.IsActive(identity, out var direction).Should().BeTrue();
            direction.Should().Be("inbound");
            peers.IsActive(unclaimed, out _).Should().BeFalse();
            if (identity == "GB7LOX")
            {
                directory.CallerFor("other@app@gb7lox-0").Should().Be(identity);
                directory.TryHand("gb7lox-0", new RecordingBatch()).Should().BeTrue("SSID zero is the bare callsign");
            }

            var first = Message("before restart", "app@N0US");
            await peer.SendMessageAsync(first, ct);
            (await peer.ReadLineAsync(ct)).Should().Be($"ack {first.Id}");
            inbox.Sources.Should().Equal(identity);

            await peer.WriteLineAsync($"exchange id=them01 hold=0 inline=256{declaration}\nping repeated", ct);
            (await peer.ReadLineAsync(ct)).Should().Be("pong repeated");
            await peer.WriteLineAsync($"exchange id=them02 hold=0 inline=256 node={laterNode}\nping restarted", ct);
            ExchangeRules.Parse(await peer.ReadLineAsync(ct)).Node.Should().Be("N0US");
            (await peer.ReadLineAsync(ct)).Should().Be("pong restarted");

            directory.Find(identity).Should().BeSameAs(session);
            session.Peer.Should().Be(identity, "a later exchange cannot change the first exchange's identity");
            lease.Peer.Should().Be(identity);
            directory.CallerFor($"app@{laterNode}").Should().BeNull();
            directory.TryHand(laterNode, new RecordingBatch()).Should().BeFalse();
            peers.IsActive(laterNode, out _).Should().BeFalse();
            var second = Message("after restart", "app@N0US");
            await peer.SendMessageAsync(second, ct);
            (await peer.ReadLineAsync(ct)).Should().Be($"ack {second.Id}");
            inbox.Sources.Should().Equal(identity, identity);
            if (source == "GB7LOX-6" && node == "G6FCI-8")
            {
                logs.Entries.Where(e => e.Message.Contains(source, StringComparison.OrdinalIgnoreCase)
                    && e.Message.Contains(node, StringComparison.OrdinalIgnoreCase)).Should().ContainSingle(
                    "the other-base declaration is ignored and logged only once, even when repeated");
            }

            await peer.WriteLineAsync("quit", ct);
            (await peer.ReadLineAsync(ct)).Should().Be("bye");
            await run.WaitAsync(Patience, ct);
        }
        finally
        {
            peer.Close();
            await run.WaitAsync(Patience, ct);
        }

        directory.Find(identity).Should().BeNull("teardown unregisters the identified peer");
        directory.Find(source).Should().BeNull();
        directory.CallerFor($"app@{identity}").Should().BeNull();
        directory.TryHand(identity, new RecordingBatch()).Should().BeFalse();
        lease.Dispose();
        peers.AllSessions().Should().BeEmpty("disposing the held lease releases its identified key");
    }
}
