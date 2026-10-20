using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>#320: /Health and /Operational are open, so the node probe is
/// remembered for a few seconds instead of connecting on every request.</summary>
public sealed class TcpReachabilityProbeTests
{
    [Fact]
    public async Task RepeatedCalls_WithinTheLifetime_ConnectOnlyOnce()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        var probe = new TcpReachabilityProbe(clock);

        for (var i = 0; i < 20; i++)
        {
            (await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2))).Should().BeTrue();
        }
        probe.Attempts.Should().Be(1);

        clock.Advance(TcpReachabilityProbe.DefaultLifetime + TimeSpan.FromSeconds(1));
        (await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2))).Should().BeTrue();
        probe.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task FailuresAreRemembered_AndRetriedAfterTheLifetime()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        var probe = new TcpReachabilityProbe(clock);

        for (var i = 0; i < 5; i++)
        {
            (await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2))).Should().BeFalse();
        }
        probe.Attempts.Should().Be(1);

        // The node comes up; the next probe after the lifetime sees it.
        var again = new TcpListener(IPAddress.Loopback, port);
        again.Start();
        try
        {
            clock.Advance(TcpReachabilityProbe.DefaultLifetime + TimeSpan.FromSeconds(1));
            (await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2))).Should().BeTrue();
        }
        finally { again.Stop(); }
    }

    [Fact]
    public async Task NoHostOrPort_IsFalseWithoutConnecting()
    {
        var probe = new TcpReachabilityProbe();
        (await probe.IsReachableAsync("", 9000, TimeSpan.FromSeconds(1))).Should().BeFalse();
        (await probe.IsReachableAsync("127.0.0.1", 0, TimeSpan.FromSeconds(1))).Should().BeFalse();
        probe.Attempts.Should().Be(0);
    }
}
