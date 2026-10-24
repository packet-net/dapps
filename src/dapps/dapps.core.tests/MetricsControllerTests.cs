using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// <c>/metrics</c> is scraped by tooling that is configured against
/// series names, so pin the names and the label sets that matter: the
/// node-link uptime, the per-neighbour link record, the open peer
/// sessions, and the message counters.
///
/// The packet-node and MQTT probes target unbound loopback ports and
/// fail fast; the resulting <c>dapps_healthy 0</c> is asserted, not
/// worked around.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MetricsControllerTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-metrics-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbMessage>();
        c.CreateTable<DbReceived>();
        c.CreateTable<DbNeighbour>();
        c.CreateTable<DbDiscoveredPeer>();
        c.CreateTable<DbDiscoveryChannel>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Get_ExposesLinkUptimeMessageCountersAndNeighbourSeries()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G7TST-9",
            NodeHost = "127.0.0.1",
            AgwPort = 1,
            MqttPort = 1,
            DiscoveryAirtimeBudgetSecondsPerHour = 60,
        });

        var metrics = new OperationalMetrics(clock);
        // The node link came up ten minutes ago and has stayed up; a
        // session with G7PEER opened eight minutes later.
        metrics.RecordAgwReconnect();
        metrics.RecordReconnectBackoff("agw", failureStreak: 0, nextRetryAtUtc: null);
        clock.Advance(TimeSpan.FromMinutes(8));
        var sessions = new PeerSessionRegistry(timeProvider: clock);
        using var lease = sessions.Acquire("G7PEER", "inbound", linkPort: 1);
        clock.Advance(TimeSpan.FromMinutes(2));
        metrics.RecordForwardSuccess("aaa1234", "G7PEER", 100);
        metrics.RecordForwardSuccess("bbb1234", "G7PEER", 50);
        metrics.RecordForwardFailure("ccc1234", "G7OTHER", 10, "busy");
        metrics.RecordProbeOutcome("G7PEER", success: true, error: null);
        metrics.RecordPollOutcome("G7PEER", success: false, messagesDrained: 0, error: "no banner");

        // Two inbound messages, counted on the bus the inbox publishes to.
        var bus = new InboundEventBus(metrics);
        bus.Publish(new InboundEvent(clock.GetUtcNow().UtcDateTime, "ddd1234", "G7PEER", "chat@G7TST-9", 30, 3600));
        bus.Publish(new InboundEvent(clock.GetUtcNow().UtcDateTime, "eee1234", "G7PEER", "chat@G7TST-9", 70, 3600));

        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        airtime.TryReserve(2.0, "beacon udp/x").Should().BeTrue();

        var db = new Database(NullLogger<Database>.Instance, opts);
        await db.UpsertNeighbour("G7PEER", bearerPort: 1);

        var controller = new MetricsController(
            opts, metrics, db, airtime, sessions, new TcpReachabilityProbe(clock), clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Get();

        result.ContentType.Should().Be("text/plain; version=0.0.4; charset=utf-8");
        var lines = result.Content!.Split('\n');

        lines.Should().Contain("dapps_build_info{version=\"" + AppVersion.Current() + "\",callsign=\"G7TST-9\",bearer=\"agw\"} 1");
        lines.Should().Contain("dapps_healthy 0", "the probes target unbound ports");
        lines.Should().Contain("dapps_callsign_configured 1");
        lines.Should().Contain("dapps_node_reachable{bearer=\"agw\"} 0");

        lines.Should().Contain("dapps_node_link_connects_total{bearer=\"agw\"} 1");
        lines.Should().Contain("dapps_node_link_up{bearer=\"agw\"} 1");
        lines.Should().Contain("dapps_node_link_uptime_seconds{bearer=\"agw\"} 600");
        lines.Should().Contain("dapps_node_link_reconnect_failures{bearer=\"agw\"} 0");
        lines.Should().NotContain(l => l.StartsWith("dapps_node_link_next_retry_timestamp_seconds"), "not in a reconnect wait");

        lines.Should().Contain("dapps_forward_attempts_total 3");
        lines.Should().Contain("dapps_forward_success_total 2");
        lines.Should().Contain("dapps_forward_failure_total 1");
        lines.Should().Contain("dapps_forward_bytes_total 150");
        lines.Should().Contain("dapps_inbound_messages_total 2");
        lines.Should().Contain("dapps_inbound_bytes_total 100");
        lines.Should().Contain("dapps_probes_total{result=\"success\"} 1");
        lines.Should().Contain("dapps_polls_total{result=\"failure\"} 1");

        lines.Should().Contain("dapps_neighbours 1");
        lines.Should().Contain("dapps_discovery_airtime_used_seconds 2");
        lines.Should().Contain("dapps_discovery_airtime_budget_seconds 60");

        lines.Should().Contain("dapps_neighbour_forward_success_total{neighbour=\"G7PEER\"} 2");
        lines.Should().Contain("dapps_neighbour_forward_bytes_total{neighbour=\"G7PEER\"} 150");
        lines.Should().Contain("dapps_neighbour_forward_failure_total{neighbour=\"G7OTHER\"} 1");
        lines.Should().Contain("dapps_neighbour_link_ok{neighbour=\"G7PEER\"} 1");
        lines.Should().Contain("dapps_neighbour_link_ok{neighbour=\"G7OTHER\"} 0");
        lines.Should().Contain(l => l.StartsWith("dapps_neighbour_last_success_timestamp_seconds{neighbour=\"G7PEER\"} "));
        lines.Should().NotContain(l => l.StartsWith("dapps_neighbour_last_success_timestamp_seconds{neighbour=\"G7OTHER\"}"), "G7OTHER never succeeded");

        lines.Should().Contain("dapps_peer_sessions_open{peer=\"G7PEER\",direction=\"inbound\"} 1");
        lines.Should().Contain("dapps_peer_session_duration_seconds{peer=\"G7PEER\",direction=\"inbound\"} 120");

        // Every family is announced before its samples.
        var idx = Array.FindIndex(lines, l => l.StartsWith("dapps_forward_success_total "));
        lines[idx - 1].Should().Be("# TYPE dapps_forward_success_total counter");
        lines[idx - 2].Should().StartWith("# HELP dapps_forward_success_total ");
    }

    [Fact]
    public async Task Get_WhileReconnecting_ReportsLinkDownAndNextRetry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G7TST-9",
            NodeBearer = "rhpv2",
            NodeHost = "127.0.0.1",
            RhpPort = 1,
            MqttPort = 1,
        });
        var metrics = new OperationalMetrics(clock);
        metrics.RecordAgwReconnect();
        var retryAt = clock.GetUtcNow().UtcDateTime.AddSeconds(30);
        metrics.RecordReconnectBackoff("rhpv2", failureStreak: 2, nextRetryAtUtc: retryAt);

        var controller = new MetricsController(
            opts, metrics,
            new Database(NullLogger<Database>.Instance, opts),
            new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance),
            new PeerSessionRegistry(timeProvider: clock),
            new TcpReachabilityProbe(clock), clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var lines = (await controller.Get()).Content!.Split('\n');

        lines.Should().Contain("dapps_node_link_up{bearer=\"rhpv2\"} 0");
        lines.Should().Contain("dapps_node_link_uptime_seconds{bearer=\"rhpv2\"} 0");
        lines.Should().Contain("dapps_node_link_reconnect_failures{bearer=\"rhpv2\"} 2");
        lines.Should().Contain("dapps_node_link_next_retry_timestamp_seconds{bearer=\"rhpv2\"} " + PrometheusText.UnixSeconds(retryAt).ToString(System.Globalization.CultureInfo.InvariantCulture));
        lines.Should().Contain("dapps_meshcore_enabled 0");
        lines.Should().NotContain(l => l.StartsWith("dapps_meshcore_link_state"), "MeshCore series are omitted while the bearer is off");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
