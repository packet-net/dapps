using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// End-to-end UDP datagram backhaul test on loopback. Sends a
/// <see cref="BackhaulMessage"/> through <see cref="UdpDatagramBackhaul"/>;
/// the matching <see cref="UdpDatagramListener"/> reassembles, decodes,
/// and hands it to a fake <see cref="IBackhaulInbox"/>. Validates Plan
/// A0.3 (DAPPS-owned fragmentation/reassembly) end-to-end on a
/// non-stream bearer.
///
/// Single-fragment and multi-fragment messages are both exercised; the
/// MTU is dialed down to 64 bytes for the multi-fragment case so a
/// 1 KB payload chunks across many datagrams.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class UdpBackhaulEndToEndTests
{
    [Fact]
    public async Task SmallMessage_RoundTripsAcrossLoopback()
    {
        var port = PickFreeUdpPort();
        var inbox = new RecordingInbox();

        var listenerCts = new CancellationTokenSource();
        using var listener = StartListener(port, inbox);
        await WaitForUdpListenerReady(port);

        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance);
        var input = new BackhaulMessage(
            Id: "smallm0",
            Destination: "myapp@N0DEST",
            Salt: 42L,
            Ttl: 600,
            Payload: "hello over UDP"u8.ToArray());

        var result = await sender.SendAsync(
            input,
            new BackhaulRoute("N0DEST", UdpEndpoint: $"127.0.0.1:{port}"),
            "N0SRC",
            TestContext.Current.CancellationToken);

        result.Accepted.Should().BeTrue();

        var (received, sourceCallsign) = await inbox.WaitForOne(TimeSpan.FromSeconds(5));
        received.Id.Should().Be(input.Id);
        received.Destination.Should().Be(input.Destination);
        received.Salt.Should().Be(input.Salt);
        received.Ttl.Should().Be(input.Ttl);
        received.Payload.Should().Equal(input.Payload);
        sourceCallsign.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LargeMessage_FragmentsAndReassembles()
    {
        var port = PickFreeUdpPort();
        var inbox = new RecordingInbox();

        using var listener = StartListener(port, inbox);
        await WaitForUdpListenerReady(port);

        // mtu=64 means each datagram carries at most 51 chunk bytes; a
        // 1 KB payload fragments across ~22 datagrams once the encoded
        // BackhaulMessage overhead is factored in.
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance, mtu: 64);
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        var input = new BackhaulMessage(
            Id: "largem1",
            Destination: "myapp@N0DEST",
            Salt: null,
            Ttl: null,
            Payload: payload);

        var result = await sender.SendAsync(
            input,
            new BackhaulRoute("N0DEST", UdpEndpoint: $"127.0.0.1:{port}"),
            "N0SRC",
            TestContext.Current.CancellationToken);

        result.Accepted.Should().BeTrue();

        var (received, _) = await inbox.WaitForOne(TimeSpan.FromSeconds(10));
        received.Id.Should().Be(input.Id);
        received.Payload.Should().Equal(payload, "every byte must arrive intact across N fragments");
    }

    [Fact]
    public async Task CanHandle_DependsOnUdpEndpointPresent()
    {
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance);

        sender.CanHandle(new BackhaulRoute("N0DEST", UdpEndpoint: "127.0.0.1:1880"))
            .Should().BeTrue();
        sender.CanHandle(new BackhaulRoute("N0DEST", BearerPort: 0))
            .Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SendAsync_InvalidEndpoint_FailsCleanly()
    {
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance);
        var input = new BackhaulMessage("aaaaaa1", "x@y", null, null, "x"u8.ToArray());

        var result = await sender.SendAsync(
            input,
            new BackhaulRoute("N0DEST", UdpEndpoint: "not-an-endpoint"),
            "N0SRC",
            TestContext.Current.CancellationToken);

        result.Accepted.Should().BeFalse();
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Listener_SharesItsSocketWhileBound_AndIgnoresDiscoveryFrames()
    {
        // #383: discovery sends from the listener's socket and advertises its port
        // only while the listener holds it. A beacon reaching the listener is not mail.
        var port = PickFreeUdpPort();
        var inbox = new RecordingInbox();
        var shared = new dapps.client.Discovery.UdpListenerSocket();

        var listener = StartListener(port, inbox, shared);
        await WaitForUdpListenerReady(port);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (shared.BoundPort is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        shared.BoundPort.Should().Be(port);

        using (var probe = new UdpClient())
        {
            await probe.SendAsync("DAPPS v1 callsign=M0X hops=0 ttl=60 udpport=1 end"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, port));
        }
        using var sender = new UdpDatagramBackhaul(NullLoggerFactory.Instance);
        var input = new BackhaulMessage("abc1234", "app@N0DEST", null, null, "after beacon"u8.ToArray());
        (await sender.SendAsync(input, new BackhaulRoute("N0DEST", UdpEndpoint: $"127.0.0.1:{port}"), "N0SRC",
            TestContext.Current.CancellationToken)).Accepted.Should().BeTrue();
        var (received, _) = await inbox.WaitForOne(TimeSpan.FromSeconds(5));
        received.Id.Should().Be("abc1234", "the beacon was dropped, not delivered");

        await listener.StopAsync(CancellationToken.None);
        listener.Dispose();
        shared.BoundPort.Should().BeNull("nothing listens once the listener has stopped");
    }

    private static UdpDatagramListener StartListener(int port, IBackhaulInbox inbox,
        dapps.client.Discovery.UdpListenerSocket? shared = null)
    {
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            UdpListenPort = port,
        });
        // The UDP listener takes a Database for its inbound IP→callsign
        // mapping (so passive learning can identify the link source).
        // These tests use unique temp DB paths per test so they don't
        // collide; an empty neighbours table just means the listener
        // falls back to the "UDP" sentinel - which is what these tests
        // expect anyway.
        var dbPath = Path.Combine(Path.GetTempPath(), $"dapps-udpe2e-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbSystemOption>();
        }
        var database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        var listener = new UdpDatagramListener(
            optionsMonitor, inbox, database, TimeProvider.System, NullLogger<UdpDatagramListener>.Instance, shared);
        // Fire-and-forget: BackgroundService.StartAsync returns once the
        // execute task is scheduled. The listener begins receiving on its
        // own task. Disposal cancels.
        listener.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return listener;
    }

    private static async Task WaitForUdpListenerReady(int port)
    {
        // Poll until something is bound to the port (UDP doesn't have a
        // listen-handshake the way TCP does; binding is the only signal).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                // We *succeeded* binding, which means the listener didn't
                // - try again.
            }
            catch (SocketException)
            {
                // Couldn't bind because something else is on this port -
                // that's the listener; we're ready.
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"UDP listener did not bind on :{port} within 3s");
    }

    private static int PickFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        private readonly TaskCompletionSource<(BackhaulMessage, string)> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            _tcs.TrySetResult((message, sourceCallsign));
            return Task.CompletedTask;
        }

        public Task<(BackhaulMessage Message, string SourceCallsign)> WaitForOne(TimeSpan timeout)
            => _tcs.Task.WaitAsync(timeout);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
