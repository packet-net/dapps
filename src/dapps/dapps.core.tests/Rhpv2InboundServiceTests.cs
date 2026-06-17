using System.Collections.Concurrent;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// Wire-level smoke for <see cref="Rhpv2InboundService"/> against
/// rhp2lib-net's <c>MockRhpServer</c>. Locks the startup-frame sequence
/// the listener produces (SOCKET → BIND → LISTEN), with the local
/// callsign passed through verbatim. End-to-end delivery (ACCEPT →
/// per-child stream → DAPPSv1 parsing) is covered by the
/// scripts/sim-mixed-bearer.sh end-to-end run, not here.
///
/// In the collection because the service now consults the
/// DerivedCallsignPending marker through the static
/// <c>DbInfo.OverridePath</c> seam; isolate from classes that mutate it.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class Rhpv2InboundServiceTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-rhp-inbound-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Startup_BindsLocalCallsign_AndListensPassive()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G0DPB-1",
            NodeBearer = "rhpv2",
            NodeHost = server.Endpoint.Address.ToString(),
            RhpPort = server.Endpoint.Port,
            // No auth - leave RhpUser/RhpPass empty.
        });

        var database = new Database(NullLogger<Database>.Instance, opts);
        var inbox = new NoopInbox();
        var metrics = new OperationalMetrics();
        var service = new Rhpv2InboundService(
            opts, NullLoggerFactory.Instance, NullLogger<Rhpv2InboundService>.Instance,
            database, inbox, metrics);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            var frames = WaitForFrames(server, count: 3, TimeSpan.FromSeconds(5));
            frames[0].Should().BeOfType<SocketMessage>();
            frames[1].Should().BeOfType<BindMessage>();
            frames[2].Should().BeOfType<ListenMessage>();

            var socket = (SocketMessage)frames[0];
            socket.Pfam.Should().Be(ProtocolFamily.Ax25);
            socket.Mode.Should().Be(SocketMode.Stream);

            var bind = (BindMessage)frames[1];
            bind.Local.Should().Be("G0DPB-1",
                "the inbound listener binds the daemon's own callsign so dispatch matches L2 SABM addressed to it");
            bind.Port.Should().BeNull(
                "port=null asks XRouter to listen across all configured ports rather than constraining to one");

            var listen = (ListenMessage)frames[2];
            ((OpenFlags)listen.Flags & OpenFlags.Active).Should().Be(0,
                "inbound is passive - active would issue a connect");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_WithRhpAuth_AuthenticatesBeforeSocket()
    {
        await using var server = new MockRhpServer
        {
            RequireAuth = true,
            Credentials = ("op", "pw"),
        };
        server.Start();

        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G0DPB-1",
            NodeBearer = "rhpv2",
            NodeHost = server.Endpoint.Address.ToString(),
            RhpPort = server.Endpoint.Port,
            RhpUser = "op",
            RhpPass = "pw",
        });

        var database = new Database(NullLogger<Database>.Instance, opts);
        var inbox = new NoopInbox();
        var metrics = new OperationalMetrics();
        var service = new Rhpv2InboundService(
            opts, NullLoggerFactory.Instance, NullLogger<Rhpv2InboundService>.Instance,
            database, inbox, metrics);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            var frames = WaitForFrames(server, count: 4, TimeSpan.FromSeconds(5));
            frames[0].Should().BeOfType<AuthMessage>(
                "AUTH must precede SOCKET when RhpUser is configured");
            frames[1].Should().BeOfType<SocketMessage>();
            frames[2].Should().BeOfType<BindMessage>();
            frames[3].Should().BeOfType<ListenMessage>();

            var auth = (AuthMessage)frames[0];
            auth.User.Should().Be("op");
            auth.Pass.Should().Be("pw");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class NoopInbox : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static List<RhpMessage> WaitForFrames(MockRhpServer server, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.ReceivedFrames.Count >= count) return server.ReceivedFrames.Take(count).ToList();
            Thread.Sleep(10);
        }
        throw new TimeoutException(
            $"Did not observe {count} frames within {timeout}; saw [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }
}

/// <summary>
/// The derived-callsign SSID probe-walk (Rhpv2InboundService.
/// BindListenerAsync). pdn answers a listen on an already-claimed
/// callsign with errCode 9 "Duplicate socket" deterministically
/// (packet.net docs/rhp2-server.md deviation D5); while the
/// DerivedCallsignPending marker matches the callsign in use, a 9
/// walks the SSIDs for a free one and persists the winner. The mock
/// server plays the pdn role via a Handler that refuses listens on a
/// "taken" set, keyed off the handle→callsign map built from BINDs.
///
/// Uses the real <see cref="SystemOptionsStore"/> (not a fixed
/// options monitor) so the persist→Reload→OnChange path the walk
/// drives in production is the one under test. In the collection
/// because both the store and the marker go through the static
/// <c>DbInfo.OverridePath</c> seam.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class Rhpv2SsidProbeWalkTests : IAsyncLifetime
{
    private string dbPath = null!;
    private readonly Dictionary<string, string?> savedEnv = [];

    private static readonly string[] EnvKeys =
    [
        "DAPPS_CALLSIGN",
        "DAPPS_ENV_MANAGED",
        "PDN_NODE_CALLSIGN",
        "PDN_APP_CALLSIGN",
    ];

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ssid-walk-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        foreach (var k in EnvKeys)
        {
            savedEnv[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, null);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (k, v) in savedEnv)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WalkOn9_PicksNextFreeSsid_PersistsWinner_AndIsStableAcrossRestart()
    {
        // The node already has an M9YYY-7 (errCode 9 on listen); the
        // derivation is still pending confirmation, so the service
        // walks to -8, wins, and persists the winner as the identity.
        await using var server = new MockRhpServer { Handler = RefuseListenOn("M9YYY-7") };
        server.Start();
        SeedDb(server.Endpoint.Port, callsign: "M9YYY-7", pendingMarker: "M9YYY-7");
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");

        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = NewService(store);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            WaitUntil(() => ReadOption("Callsign") == "M9YYY-8", TimeSpan.FromSeconds(5),
                "the first free SSID must be persisted as the stored callsign");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        ReadOption(DbStartup.DerivedCallsignPendingKey).Should().BeNull(
            "a successful listen confirms the derived identity");
        store.CurrentValue.Callsign.Should().Be("M9YYY-8",
            "the walk reloads the store so every consumer sees the confirmed identity");
        var binds = server.ReceivedFrames.OfType<BindMessage>().Select(b => b.Local).ToList();
        binds.Take(2).Should().Equal("M9YYY-7", "M9YYY-8");

        // Restart: the persisted winner is the identity - binds -8
        // directly and never walks again, even though -7 is still taken.
        await using var server2 = new MockRhpServer { Handler = RefuseListenOn("M9YYY-7") };
        server2.Start();
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update systemoptions set value=? where option=?", server2.Endpoint.Port.ToString(), "RhpPort");
        }
        var store2 = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service2 = NewService(store2);
        await service2.StartAsync(ct);
        try
        {
            var frames = WaitForFrames(server2, count: 3, TimeSpan.FromSeconds(5));
            frames[0].Should().BeOfType<SocketMessage>();
            frames[1].Should().BeOfType<BindMessage>().Which.Local.Should().Be("M9YYY-8");
            frames[2].Should().BeOfType<ListenMessage>();
        }
        finally
        {
            await service2.StopAsync(CancellationToken.None);
        }
        ReadOption("Callsign").Should().Be("M9YYY-8");
    }

    [Fact]
    public async Task ExplicitCallsign_On9_LogsAndRetries_NeverWalks()
    {
        // No pending-derivation marker = the callsign is explicit
        // (dashboard / DAPPS_CALLSIGN / already confirmed). A duplicate
        // refusal must keep the existing retry/reconnect behaviour, not
        // probe other SSIDs.
        await using var server = new MockRhpServer { Handler = RefuseListenOn("G0DPB-1") };
        server.Start();
        SeedDb(server.Endpoint.Port, callsign: "G0DPB-1", pendingMarker: null);
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY"); // present but irrelevant: not a derivation

        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = NewService(store);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            // socket → bind → listen(9) → close, then park for the
            // reconnect backoff. Give it a beat to prove no walk starts.
            WaitForFrames(server, count: 4, TimeSpan.FromSeconds(5));
            await Task.Delay(300, ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        server.ReceivedFrames.OfType<BindMessage>().Should()
            .OnlyContain(b => b.Local == "G0DPB-1", "an explicitly configured callsign never walks");
        ReadOption("Callsign").Should().Be("G0DPB-1", "the stored identity is untouched");
    }

    [Fact]
    public async Task NodeAssignedCallsign_On9_NeverWalks_EvenWithPendingMarker()
    {
        // Node-owned-callsign contract: PDN_APP_CALLSIGN is set, so the
        // identity is bound verbatim and must NEVER probe-walk - even if a
        // stale derivation-pending marker that matches the callsign is
        // still in the DB. A duplicate refusal keeps the retry/reconnect
        // behaviour instead of walking SSIDs.
        await using var server = new MockRhpServer { Handler = RefuseListenOn("M9YYY-7") };
        server.Start();
        SeedDb(server.Endpoint.Port, callsign: "M9YYY-7", pendingMarker: "M9YYY-7");
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");
        Environment.SetEnvironmentVariable("PDN_APP_CALLSIGN", "M9YYY-7");

        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = NewService(store);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            WaitForFrames(server, count: 4, TimeSpan.FromSeconds(5)); // socket → bind → listen(9) → close
            await Task.Delay(300, ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        server.ReceivedFrames.OfType<BindMessage>().Should()
            .OnlyContain(b => b.Local == "M9YYY-7",
                "a node-assigned PDN_APP_CALLSIGN is bound verbatim and never walks");
        ReadOption("Callsign").Should().Be("M9YYY-7", "the node-assigned identity is untouched");
    }

    [Fact]
    public async Task NonDuplicateListenError_DoesNotWalk_EvenWhilePending()
    {
        // Only errCode 9 means "callsign taken". Any other refusal
        // (here 13 "No buffers") keeps the existing reconnect handling:
        // no probing, identity and pending marker untouched.
        await using var server = new MockRhpServer
        {
            Handler = msg => msg is ListenMessage l
                ? new ListenReplyMessage { Handle = l.Handle, ErrCode = RhpErrorCode.NoBuffers, ErrText = "No buffers" }
                : null,
        };
        server.Start();
        SeedDb(server.Endpoint.Port, callsign: "M9YYY-7", pendingMarker: "M9YYY-7");
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");

        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = NewService(store);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            WaitForFrames(server, count: 3, TimeSpan.FromSeconds(5)); // socket → bind → listen
            await Task.Delay(300, ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        server.ReceivedFrames.OfType<BindMessage>().Should()
            .OnlyContain(b => b.Local == "M9YYY-7", "non-9 errors must not trigger the SSID walk");
        ReadOption("Callsign").Should().Be("M9YYY-7");
        ReadOption(DbStartup.DerivedCallsignPendingKey).Should().Be("M9YYY-7",
            "the derivation stays pending so a later start can still confirm or walk");
    }

    [Fact]
    public async Task AllSsidsTaken_WalksInOrder_ThenRevertsToSetupRequired()
    {
        // Every listen answers 9. The walk must try start+1 … 15 then
        // 1 … start−1 - skipping 0 and the node's own SSID (-2 here) -
        // and then park in setup-required mode (placeholder callsign)
        // instead of spinning against the node.
        await using var server = new MockRhpServer { Handler = RefuseListenOn(call => true) };
        server.Start();
        SeedDb(server.Endpoint.Port, callsign: "M9YYY-7", pendingMarker: "M9YYY-7");
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY-2");

        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var service = NewService(store);
        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            WaitUntil(() => ReadOption("Callsign") == DbStartup.PlaceholderCallsign, TimeSpan.FromSeconds(10),
                "exhausting every candidate must revert to setup-required mode");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        ReadOption(DbStartup.DerivedCallsignPendingKey).Should().BeNull();
        store.CurrentValue.Callsign.Should().Be(DbStartup.PlaceholderCallsign,
            "the reload parks every consumer in setup-required mode");
        var binds = server.ReceivedFrames.OfType<BindMessage>().Select(b => b.Local).ToList();
        binds.Should().Equal(
            "M9YYY-7", "M9YYY-8", "M9YYY-9", "M9YYY-10", "M9YYY-11", "M9YYY-12", "M9YYY-13",
            "M9YYY-14", "M9YYY-15", "M9YYY-1", "M9YYY-3", "M9YYY-4", "M9YYY-5", "M9YYY-6");
    }

    [Fact]
    public void SsidProbeCandidates_WalksForwardThenWraps_SkippingZeroAndNodeSsid()
    {
        Rhpv2InboundService.SsidProbeCandidates("M9YYY-7", "M9YYY").Should().Equal(
            "M9YYY-7", "M9YYY-8", "M9YYY-9", "M9YYY-10", "M9YYY-11", "M9YYY-12", "M9YYY-13",
            "M9YYY-14", "M9YYY-15", "M9YYY-1", "M9YYY-2", "M9YYY-3", "M9YYY-4", "M9YYY-5", "M9YYY-6");

        Rhpv2InboundService.SsidProbeCandidates("M9YYY-7", "M9YYY-2").Should().NotContain("M9YYY-2",
            "the SSID the node itself uses is skipped");

        Rhpv2InboundService.SsidProbeCandidates("M9YYY-13", null).Should().Equal(
            "M9YYY-13", "M9YYY-14", "M9YYY-15", "M9YYY-1", "M9YYY-2", "M9YYY-3", "M9YYY-4",
            "M9YYY-5", "M9YYY-6", "M9YYY-7", "M9YYY-8", "M9YYY-9", "M9YYY-10", "M9YYY-11", "M9YYY-12");

        Rhpv2InboundService.SsidProbeCandidates("M9YYY-7", "M9YYY")
            .Should().NotContain(c => c.EndsWith("-0"), "SSID 0 is the node's bare callsign");
    }

    /// <summary>Mock-server handler playing the pdn role for one taken
    /// callsign: listens on it answer errCode 9 "Duplicate socket"
    /// (docs/rhp2-server.md D5); everything else gets the defaults.</summary>
    private static Func<RhpMessage, RhpMessage?> RefuseListenOn(string takenCallsign) =>
        RefuseListenOn(call => string.Equals(call, takenCallsign, StringComparison.OrdinalIgnoreCase));

    private static Func<RhpMessage, RhpMessage?> RefuseListenOn(Func<string, bool> isTaken)
    {
        var handleToCallsign = new ConcurrentDictionary<int, string>();
        return msg =>
        {
            switch (msg)
            {
                case BindMessage { Local: { } local } b:
                    handleToCallsign[b.Handle] = local;
                    return null; // default Ok reply
                case ListenMessage l when handleToCallsign.TryGetValue(l.Handle, out var call) && isTaken(call):
                    return new ListenReplyMessage
                    {
                        Handle = l.Handle,
                        ErrCode = RhpErrorCode.DuplicateSocket,
                        ErrText = "Duplicate socket",
                    };
                default:
                    return null;
            }
        };
    }

    private static Rhpv2InboundService NewService(SystemOptionsStore store)
    {
        var database = new Database(NullLogger<Database>.Instance, store);
        return new Rhpv2InboundService(
            store, NullLoggerFactory.Instance, NullLogger<Rhpv2InboundService>.Instance,
            database, new NoopInbox(), new OperationalMetrics());
    }

    private static void SeedDb(int rhpPort, string callsign, string? pendingMarker)
    {
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbSystemOption>();
        c.InsertOrReplace(new DbSystemOption { Option = "Callsign", Value = callsign });
        c.InsertOrReplace(new DbSystemOption { Option = "NodeBearer", Value = "rhpv2" });
        c.InsertOrReplace(new DbSystemOption { Option = "NodeHost", Value = "127.0.0.1" });
        c.InsertOrReplace(new DbSystemOption { Option = "RhpPort", Value = rhpPort.ToString() });
        if (pendingMarker is not null)
        {
            c.InsertOrReplace(new DbSystemOption
            {
                Option = DbStartup.DerivedCallsignPendingKey,
                Value = pendingMarker,
            });
        }
    }

    private static string? ReadOption(string key)
    {
        using var c = DbInfo.GetConnection();
        return c.Query<DbSystemOption>("select * from systemoptions where option = ? collate nocase;", key)
            .FirstOrDefault()?.Value;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException($"Condition not met within {timeout}: {because}");
    }

    private static List<RhpMessage> WaitForFrames(MockRhpServer server, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.ReceivedFrames.Count >= count) return server.ReceivedFrames.Take(count).ToList();
            Thread.Sleep(10);
        }
        throw new TimeoutException(
            $"Did not observe {count} frames within {timeout}; saw [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }

    private sealed class NoopInbox : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
            => Task.CompletedTask;
    }
}
