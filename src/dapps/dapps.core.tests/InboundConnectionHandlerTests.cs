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
}
