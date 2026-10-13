using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Two DAPPS nodes under one callsign, told apart by SSID (#239): G6FCI-7
/// and G6FCI-8, as the BPQ guide recommends. The SSID is part of a node's
/// identity, so mail G6FCI-7 sends to <c>chat@G6FCI-8</c> is outbound mail
/// for its neighbour, not mail for its own <c>chat</c> app. Before the fix
/// every "is this for us?" check dropped the SSID, so the message sat in
/// G6FCI-7's own inbox and never left the node.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class SameBaseCallsignTests : IAsyncLifetime
{
    private const string Us = "G6FCI-7";
    private const string Sibling = "G6FCI-8";

    private string dbPath = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-samebase-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbFragment>();
            c.CreateTable<DbSystemOption>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
        }
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Us });
        database = new Database(NullLogger<Database>.Instance, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MailForTheSibling_IsQueuedToGo()
    {
        var id = await database.SubmitOutboundMessage("chat", Sibling, "Hello World"u8.ToArray());

        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Equal(id);
        (await database.GetPendingOutboundMessagesQueuedSince(DateTime.MinValue)).Select(m => m.Id).Should().Equal(id);
        (await database.CountPendingOutbound()).Should().Be(1);
    }

    [Fact]
    public async Task MailForTheSibling_IsNotOursToDeliver()
    {
        await database.SubmitOutboundMessage("chat", Sibling, "Hello World"u8.ToArray());

        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().BeEmpty();
        (await database.CountUndeliveredLocal()).Should().Be(0);
    }

    [Theory]
    [InlineData("G6FCI-7")]
    [InlineData("g6fci-7")]
    public async Task MailForUs_StaysHome(string addressedTo)
    {
        var id = await database.SubmitOutboundMessage("chat", addressedTo, "for me"u8.ToArray());

        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        (await database.CountPendingOutbound()).Should().Be(0);
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Select(m => m.Id).Should().Equal(id);
        (await database.CountUndeliveredLocal()).Should().Be(1);
    }

    [Fact]
    public async Task ACallsignThatMerelyStartsWithOurs_IsNotUs()
    {
        // The old check was a substring match on "@G6FCI", which G6FCIX
        // satisfies too.
        var id = await database.SubmitOutboundMessage("chat", "G6FCIX-7", "x"u8.ToArray());

        (await database.GetPendingOutboundMessages()).Select(m => m.Id).Should().Equal(id);
        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Should().BeEmpty();
    }

    [Fact]
    public async Task NoSsid_IsSsidZero()
    {
        // AX.25 has no "no SSID": G6FCI is G6FCI-0.
        options.CurrentValue.Callsign = "G6FCI";
        var id = await database.SubmitOutboundMessage("chat", "G6FCI-0", "x"u8.ToArray());

        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Select(m => m.Id).Should().Equal(id);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task Dashboard_ShowsMailForTheSibling_InTheOutboundQueue()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var controller = new EventsController(
            new InboundEventBus(), database, options, new OperationalMetrics(clock),
            new UpdateChecker(new NoopHttpClientFactory(), options, TimeProvider.System, NullLogger<UpdateChecker>.Instance));
        var outId = await database.SubmitOutboundMessage("chat", Sibling, "out"u8.ToArray());
        var inId = await database.SubmitOutboundMessage("chat", Us, "in"u8.ToArray());

        var snapshot = await controller.GetQueueSnapshot();

        snapshot.Outbound.Select(r => r.Id).Should().Equal(outId);
        snapshot.LocalInbox.Select(r => r.Id).Should().Equal(inId);
        snapshot.PendingOutbound.Should().Be(1);
        snapshot.UndeliveredLocal.Should().Be(1);
    }

    [Theory]
    [InlineData("foo@G6FCI-7", "G6FCI-7", true)]
    [InlineData("foo@g6fci-7", "G6FCI-7", true)]
    [InlineData("foo@G6FCI-8", "G6FCI-7", false)]
    [InlineData("foo@G6FCI", "G6FCI-7", false)]
    [InlineData("foo@G6FCI-7", "G6FCI", false)]
    [InlineData("foo@G6FCI", "G6FCI-0", true)]
    [InlineData("foo@G6FCI-0", "G6FCI", true)]
    [InlineData("foo@G6FCIX-7", "G6FCI-7", false)]
    public void IsLocal_ComparesTheWholeCallsign(string destination, string local, bool expected)
    {
        DestinationParser.IsLocal(destination, local).Should().Be(expected);
    }

    [Fact]
    public async Task Routing_PicksTheNeighbourWithTheRightSsid()
    {
        await database.UpsertNeighbour("G6FCI-9", bearerPort: 1);
        await database.UpsertNeighbour(Sibling, bearerPort: 1);
        var algorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var context = new DatabaseRoutingContext(database, options);

        var decision = await algorithm.ResolveAsync(
            new DbMessage { Id = "0000001", Destination = $"chat@{Sibling}", Payload = "x"u8.ToArray() },
            context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be(Sibling);
    }

    [Theory]
    [InlineData("chat@G6FCI-9")]
    [InlineData("chat@G6FCI")]
    public async Task Routing_NeverFallsBackToASiblingOfOurs(string destination)
    {
        // G6FCI-8 stood in for G6FCI-9 would hand it straight back to us
        // under the same rule, and the mail would die as a repeat.
        await database.UpsertNeighbour(Sibling, bearerPort: 1);
        var algorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var context = new DatabaseRoutingContext(database, options);

        var decision = await algorithm.ResolveAsync(
            new DbMessage { Id = "0000002", Destination = destination, Payload = "x"u8.ToArray() },
            context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.Unreachable>();
    }

    [Fact]
    public async Task Routing_StillFallsBackToAnotherSsidOfSomeoneElse()
    {
        await database.UpsertNeighbour("M0ABC-1", bearerPort: 1);
        var algorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var context = new DatabaseRoutingContext(database, options);

        var decision = await algorithm.ResolveAsync(
            new DbMessage { Id = "0000003", Destination = "chat@M0ABC-7", Payload = "x"u8.ToArray() },
            context, TestContext.Current.CancellationToken);

        decision.Should().BeOfType<RouteDecision.NextHop>().Which.Route.Callsign.Should().Be("M0ABC-1");
    }

    [Fact]
    public void ASessionFromTheExactStation_IsPreferred_AndASiblingOfOursIsNeverUsed()
    {
        var sessions = new SessionDirectory();
        sessions.Register("G6FCI-9", Session("G6FCI-9"));
        sessions.Register(Sibling, Session(Sibling));

        sessions.CallerFor($"chat@{Sibling}", Us).Should().Be(Sibling);
        sessions.CallerFor("chat@G6FCI-5", Us).Should().BeNull();
    }

    [Fact]
    public async Task MailAnAppHereAlreadyTook_NeverGoesOut()
    {
        // Before 0.46.3 mail for G6FCI-8 counted as ours; an app here may
        // have acked it. It must not start moving after the upgrade.
        var id = await database.SubmitOutboundMessage("chat", Sibling, "old"u8.ToArray());
        await database.MarkLocallyDelivered(id);

        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        (await database.CountPendingOutbound()).Should().Be(0);
    }

    [Fact]
    public async Task TheSqlAndIsLocal_ReadTheCallsignAfterTheLastAt()
    {
        var id = await database.SubmitOutboundMessage("a@b", Us, "x"u8.ToArray());

        DestinationParser.IsLocal($"a@b@{Us}", Us).Should().BeTrue();
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
        (await database.GetUnacknowledgedLocalMessagesForApp("a@b")).Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task OurCallsignWrittenWithSsidZero_TakesMailForTheBareCall()
    {
        options.CurrentValue.Callsign = "G6FCI-0";
        var id = await database.SubmitOutboundMessage("chat", "G6FCI", "x"u8.ToArray());

        (await database.GetUnacknowledgedLocalMessagesForApp("chat")).Select(m => m.Id).Should().Equal(id);
        (await database.GetPendingOutboundMessages()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnderscoreInAnAppName_IsNotAWildcard()
    {
        await database.SubmitOutboundMessage("wpsXrepl", Us, "x"u8.ToArray());
        var id = await database.SubmitOutboundMessage("wps_repl", Us, "x"u8.ToArray());

        (await database.GetUnacknowledgedLocalMessagesForApp("wps_repl")).Select(m => m.Id).Should().Equal(id);
    }

    private static ExchangeSession Session(string peer)
        => new(new MemoryStream(), peer, dialled: false, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance);

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
