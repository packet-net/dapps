using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan B6.1 Phase 2 - server-side test for the new <c>peers</c>
/// command on the DAPPSv1 receiver. Spins up an
/// <see cref="InboundConnectionHandler"/> against a fake duplex stream
/// preloaded with <c>peers\nq\n</c> (the <c>q</c> closes the session
/// cleanly so <c>Handle</c> returns rather than blocking on the
/// inactivity timeout).
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class InboundConnectionHandlerPeersTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-peers-handler-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
        }

        database = new Database(NullLogger<Database>.Instance,
            new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0US" }));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PeersCommand_NoNeighboursNoPeers_EmitsOnlyEnd()
    {
        var bytes = await DriveSession("peers\nq\n");

        // Initial DAPPSv1> banner, then end\n, then bye\n (for the q).
        bytes.Should().Contain("DAPPSv1>");
        bytes.Should().Contain("end\n");
        bytes.Should().NotContain("peer ");
    }

    [Fact]
    public async Task PeersCommand_NeighbourAndAgwDiscoveredPeer_EmitsBoth()
    {
        await database.UpsertNeighbour("N0NEIGH-9", bearerPort: 1);
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BCN-7",
            Bearer = "agw",
            ChannelKey = "0",
            BearerPort = 0,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var output = await DriveSession("peers\nq\n");

        output.Should().Contain("peer N0NEIGH-9 source=n");
        output.Should().Contain("port=1");
        output.Should().Contain("peer N0BCN-7 source=d");
        // Neighbours emit before discovered peers - pin that ordering
        // so future shuffles in HandlePeers are noticed.
        output.IndexOf("peer N0NEIGH-9").Should().BeLessThan(output.IndexOf("peer N0BCN-7"));
        output.Should().Contain("end\n");
    }

    [Fact]
    public async Task PeersCommand_UdpNeighbour_ExcludedFromPeersList()
    {
        // UDP-only neighbours can't be reached via AGW from the asker's
        // side, so emitting them as peers would just cause failed probes.
        await database.UpsertNeighbour("N0UDP-9", bearerPort: null, udpEndpoint: "127.0.0.1:1880");
        await database.UpsertNeighbour("N0AGW-9", bearerPort: 1);

        var output = await DriveSession("peers\nq\n");

        output.Should().Contain("peer N0AGW-9");
        output.Should().NotContain("peer N0UDP-9");
    }

    [Fact]
    public async Task PeersCommand_UdpDiscoveredPeer_ExcludedFromPeersList()
    {
        // Same rule for discovered peers - UDP-bearer rows live in the
        // same table but aren't AGW-reachable for our asker.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0UDP-7",
            Bearer = "udp",
            ChannelKey = "239.0.0.1:54321",
            UdpEndpoint = "127.0.0.1:1880",
            LinkClass = LinkClass.LanMulticast,
            CostHint = 8,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var output = await DriveSession("peers\nq\n");

        output.Should().Contain("end\n");
        output.Should().NotContain("peer ");
    }

    [Fact]
    public async Task PeersCommand_NeighbourAndPeerForSameCallsign_NeighbourWinsOnce()
    {
        // Same callsign appears as both a manual neighbour and a
        // beacon-discovered peer. We emit it once, with source=n
        // (neighbour wins) - the asker doesn't need to dedupe.
        await database.UpsertNeighbour("N0BOTH-9", bearerPort: 5);
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BOTH-9",
            Bearer = "agw",
            ChannelKey = "0",
            BearerPort = 0,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var output = await DriveSession("peers\nq\n");

        var lines = output.Split('\n').Where(l => l.StartsWith("peer ")).ToList();
        lines.Should().ContainSingle();
        lines[0].Should().Contain("source=n");
        lines[0].Should().Contain("port=5");
    }

    [Fact]
    public async Task WhoAlias_AcceptedAsSynonymForPeers()
    {
        // 'who' is the verb a sysop already types at a node prompt;
        // accepting it makes ad-hoc operator inspection from a terminal
        // pleasanter without the asker needing to know the canonical
        // command name.
        await database.UpsertNeighbour("N0AAA-9", bearerPort: 1);

        var output = await DriveSession("who\nq\n");

        output.Should().Contain("peer N0AAA-9");
        output.Should().Contain("end\n");
    }

    /// <summary>Drive an inbound session: feed <paramref name="commandStream"/>
    /// to the handler as the client's input, run <see cref="InboundConnectionHandler.Handle"/>,
    /// and return the captured bytes the handler wrote back.</summary>
    private async Task<string> DriveSession(string commandStream)
    {
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes(commandStream));
        var inbox = new RecordingInbox();
        var handler = new InboundConnectionHandler(
            stream, sourceCallsign: "N0THEM-9",
            NullLoggerFactory.Instance, database, inbox);

        await handler.Handle(CancellationToken.None);

        return Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        public List<BackhaulMessage> Delivered { get; } = new();
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
