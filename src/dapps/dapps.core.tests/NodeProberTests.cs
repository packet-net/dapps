using System.Text;
using AwesomeAssertions;
using dapps.client.Transport;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for <see cref="NodeProber"/>. Drives the prober with a
/// fake transport that hands back canned receiver bytes, the same
/// pattern as <c>Dappsv1SessionBackhaulTests</c>. Each path corresponds
/// to a distinct ProbeResult shape the dashboard cares about.
/// </summary>
public sealed class NodeProberTests
{
    [Fact]
    public async Task ProbeAsync_DappsPromptObserved_ReturnsSuccess()
    {
        // The prober reads up to the prompt then closes. Anything after
        // the prompt is ignored; the trailing newline triggers the prompt
        // recogniser. (See DappsProtocolClient.ReadInitialPromptAsync.)
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeEmpty();
        result.Callsign.Should().Be("N0THEM-9");
        result.BearerPort.Should().Be(1);
        result.At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProbeAsync_TheExchangeLineAfterThePrompt_IsIgnored()
    {
        // A DAPPS node sends its rules straight after the prompt. A probe
        // never starts an exchange, so it just reads past them.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nexchange id=ab12cd hold=120 inline=256 z=1\npeer N0NEXT-9 source=n port=1\nend\n"));
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, CancellationToken.None, fetchPeers: true);

        result.Success.Should().BeTrue();
        result.DiscoveredPeers.Should().ContainSingle().Which.Callsign.Should().Be("N0NEXT-9");
    }

    [Fact]
    public async Task ProbeAsync_NoPrompt_ReturnsFailureWithReason()
    {
        var transport = new FakeOutboundTransport("garbage no prompt"u8.ToArray());
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task ProbeAsync_TransportThrows_ReturnsFailureWithMessage()
    {
        var prober = MakeProber(new ThrowingTransport(new InvalidOperationException("AGW connect rejected")));

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("AGW connect rejected");
    }

    [Fact]
    public async Task ProbeAsync_TransportTimesOut_ReturnsTimeoutError()
    {
        // TimeoutException gets a leading "timeout: " prefix so dashboard
        // operators can distinguish a wedged peer from a flat-out reject.
        var prober = MakeProber(new ThrowingTransport(new TimeoutException("AGW: no frame from BPQ for 3 minutes")));

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("timeout: ");
        result.Error.Should().Contain("3 minutes");
    }

    [Fact]
    public async Task ProbeAsync_CancellationFromCaller_PropagatesAsTaskCanceled()
    {
        // Caller-driven cancellation is a shutdown signal; the scheduler
        // needs the exception to bubble so it can exit ExecuteAsync. Use
        // a transport that blocks indefinitely - the only way out is the
        // cancellation token.
        var prober = MakeProber(new BlockingTransport());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => prober.ProbeAsync("N0US", "N0THEM-9", 0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static NodeProber MakeProber(IDappsOutboundTransport transport)
        => new(transport, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance);

    private sealed class FakeOutboundTransport(byte[] cannedReceiverBytes) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            // FakeDuplexStream rather than a bare MemoryStream - the
            // prober may write back ("peers\n" on Phase 2 fetch-peers
            // probes), which would otherwise overwrite the canned read
            // buffer if read and write shared one stream.
            => Task.FromResult<IDappsConnection>(new FakeConnection(new FakeDuplexStream(cannedReceiverBytes)));

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ThrowingTransport(Exception toThrow) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            => Task.FromException<IDappsConnection>(toThrow);
    }

    private sealed class BlockingTransport : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            var tcs = new TaskCompletionSource<IDappsConnection>();
            stoppingToken.Register(() => tcs.TrySetCanceled(stoppingToken));
            return tcs.Task;
        }
    }
}
