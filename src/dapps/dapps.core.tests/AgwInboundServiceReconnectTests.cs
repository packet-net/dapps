using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Regression tests for the inbound AGW reconnect race: a peer hangs up
/// and reconnects straight away with the same callsign pair. AGW frames
/// carry no session id, so anything dapps does for the *old* session
/// after the new <c>'C'</c> has arrived (removing it from the session
/// table, or sending it a <c>'d'</c>) hits the *new* session, which then
/// never gets its <c>DAPPSv1&gt;</c> prompt.
///
/// A fake AGW server plays BPQ: it sends <c>'d'</c> and the next
/// <c>'C'</c> back-to-back in one TCP write, then checks that dapps
/// prompts the new session and never sends a <c>'d'</c> of its own for a
/// session the peer already closed.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AgwInboundServiceReconnectTests : IAsyncLifetime
{
    private const string Remote = "M0AHN-3";
    private const string Local = "G5ALF-3";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-reconnect-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ImmediateReconnectAfterRemoteHangup_GetsPrompt_AndDappsNeverSendsStaleDisconnect()
    {
        const int reconnects = 100;
        var ct = TestContext.Current.CancellationToken;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = Local,
            NodeHost = "127.0.0.1",
            AgwPort = ((IPEndPoint)listener.LocalEndpoint).Port,
        });
        var service = new AgwInboundService(
            options,
            new Database(NullLogger<Database>.Instance, options),
            new NoopInbox(),
            NullLoggerFactory.Instance,
            NullLogger<AgwInboundService>.Instance);

        await service.StartAsync(ct);
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(FrameTimeout, ct);
            var wire = tcp.GetStream();
            var framing = new AgwFrameTransport(wire);

            // 'X' registration for our callsign comes first.
            var register = await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct);
            register.Kind.Should().Be('X');

            var fromDapps = new List<AgwFrame>();
            var connect = new AgwFrame(0, 'C', 0, Remote, Local, []).ToBytes();
            var hangup = new AgwFrame(0, 'd', 0, Remote, Local, []).ToBytes();

            await wire.WriteAsync(connect, ct);
            for (var i = 0; i <= reconnects; i++)
            {
                var prompt = await NextDataFrame(framing, fromDapps, ct);
                prompt.Should().NotBeNull($"session {i} should be prompted even though the previous one just closed");
                Encoding.UTF8.GetString(prompt!.Payload).Should().Be("DAPPSv1>\n");

                // Peer hangs up and reconnects in the same TCP write, the way
                // BPQ can when a client redials immediately.
                await wire.WriteAsync(i < reconnects ? [.. hangup, .. connect] : hangup, ct);
            }

            // Let any straggling teardown from the final session land.
            await Task.Delay(300, ct);
            while (wire.DataAvailable)
                fromDapps.Add(await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct));

            fromDapps.Where(f => f.Kind == 'd').Should().BeEmpty(
                "the peer closed every session first; a 'd' from dapps can only hit a newer session on the same callsign pair");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectArrivesBeforeStaleDisconnect_NewSessionStillGetsPrompt()
    {
        const int reconnects = 100;
        var ct = TestContext.Current.CancellationToken;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = Local,
            NodeHost = "127.0.0.1",
            AgwPort = ((IPEndPoint)listener.LocalEndpoint).Port,
        });
        var service = new AgwInboundService(
            options,
            new Database(NullLogger<Database>.Instance, options),
            new NoopInbox(),
            NullLoggerFactory.Instance,
            NullLogger<AgwInboundService>.Instance);

        await service.StartAsync(ct);
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(FrameTimeout, ct);
            var wire = tcp.GetStream();
            var framing = new AgwFrameTransport(wire);

            var register = await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct);
            register.Kind.Should().Be('X');

            var fromDapps = new List<AgwFrame>();
            var connect = new AgwFrame(0, 'C', 0, Remote, Local, []).ToBytes();
            var hangup = new AgwFrame(0, 'd', 0, Remote, Local, []).ToBytes();

            await wire.WriteAsync(connect, ct);
            for (var i = 0; i <= reconnects; i++)
            {
                var prompt = await NextDataFrame(framing, fromDapps, ct);
                prompt.Should().NotBeNull(
                    $"session {i} should be prompted even though its 'C' arrived before the previous " +
                    "session's 'd' - BPQ itself refuses a genuine concurrent duplicate, so a 'C' for a " +
                    "still-registered key means the old session's teardown just hasn't reached us yet");
                Encoding.UTF8.GetString(prompt!.Payload).Should().Be("DAPPSv1>\n");

                // Reversed from the sibling test: the *new* connect for the
                // next session arrives on the wire before the 'd' that
                // retires the one we just prompted, the way BPQ's own AGW
                // socket write can reorder relative to its "session ended"
                // bookkeeping.
                await wire.WriteAsync(i < reconnects ? [.. connect, .. hangup] : hangup, ct);
            }

            await Task.Delay(300, ct);
            while (wire.DataAvailable)
                fromDapps.Add(await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct));

            fromDapps.Where(f => f.Kind == 'd').Should().BeEmpty(
                "every session was retired by a newer 'C' or closed by the peer, never by dapps");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    /// <summary>Reads frames until the next 'D' frame, recording every frame seen.
    /// Returns null if none arrives in time.</summary>
    private static async Task<AgwFrame?> NextDataFrame(
        AgwFrameTransport framing, List<AgwFrame> seen, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await framing.ReadFrameAsync(ct).WaitAsync(FrameTimeout, ct);
                seen.Add(frame);
                if (frame.Kind == 'D') return frame;
            }
        }
        catch (TimeoutException)
        {
            return null;
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
}
