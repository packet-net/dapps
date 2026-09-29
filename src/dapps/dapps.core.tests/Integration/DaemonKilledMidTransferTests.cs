using AwesomeAssertions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The receiving daemon crashes (SIGKILL, not a clean stop) while a large
/// message is still arriving, then restarts on the same database. Every
/// message must still arrive exactly once - what the received-message
/// ledger (#195, <see cref="dapps.core.Models.DbReceived"/>) promises.
/// The soak (<see cref="NetSimSoakTests"/>) only ever restarts a daemon
/// cleanly (SIGTERM, via <see cref="DappsDaemon.RestartAsync"/>); this is
/// the one that proves an actual crash mid-transfer doesn't lose or
/// duplicate anything.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class DaemonKilledMidTransferTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private const string App = "big";
    private readonly List<IAsyncDisposable> running = [];
    private AirMonitor air = null!;

    public async ValueTask InitializeAsync()
    {
        air = await AirMonitor.StartAsync(fixture.Host, fixture.AgwPortA, fixture.AgwPortB, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in running) await r.DisposeAsync();
        await air.DisposeAsync();
        // Let BPQ release the AGW registrations before the next test in
        // the collection reuses the same callsigns.
        await Task.Delay(3000);
    }

    [Fact]
    public async Task BKilledMidTransfer_RestartsAndTheMessageStillArrivesExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new Dictionary<string, string> { ["DAPPS_SESSION_TAIL_SECONDS"] = "0" };
        var a = await StartNodeAsync("killA", fixture.ApplCallA, fixture.AgwPortA, fixture.ApplCallB, settings, ct);
        var b = await StartNodeAsync("killB", fixture.ApplCallB, fixture.AgwPortB, fixture.ApplCallA, settings, ct);

        // Big enough to split into many AGW frames (the old 256-byte
        // limit AgwLargePayloadIntegrationTests found) over several
        // fragmented, individually-offered parts (over the 4 KiB
        // FragmentThresholdBytes) - plenty of time to catch it mid-flight.
        var payload = new byte[80_000];
        new Random(19).NextBytes(payload);
        var id = await a.SubmitAsync(App, b.Callsign, payload, ct, ttl: 600);

        // Let the session actually start, then let a handful of data
        // frames go by before killing B - however fast or slow the
        // transfer turns out to be, this lands the kill inside it rather
        // than before it starts or long after it's done. If BPQ's
        // monitor text for an I-frame doesn't match (unlikely - matches
        // the header AirMonitor already parses elsewhere), the 10 s cap
        // still lands the kill during an 80 KB transfer.
        await AirShowsAsync("A", $"Fm {fixture.ApplCallA} To {fixture.ApplCallB} <C C", ct, TimeSpan.FromSeconds(30));
        var dataDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (air.CountSentBy("A", "<I") < 3 && DateTime.UtcNow < dataDeadline)
        {
            await Task.Delay(20, ct);
        }

        await b.KillAsync(ct);

        var got = await b.WaitForInboundAsync(App, 1, TimeSpan.FromSeconds(150), ct);
        got.Should().ContainSingle();
        got[0].Id.Should().Be(id);
        got[0].Payload.Should().Equal(payload, "the message must survive the crash byte for byte");

        // Wait for A to actually stop retrying (its own pending-outbound
        // count reaching 0, meaning it got the ack), then give a
        // duplicate every chance to turn up before declaring "exactly
        // once" - a fixed sleep here could be outlasted by a retry
        // backoff on a slow runner.
        await WaitForDrainAsync(a, ct, TimeSpan.FromSeconds(60));
        await Task.Delay(2000, ct);
        (await b.InboundAsync(App, ct)).Should().ContainSingle(
            "the received-message ledger must stop a retried copy being delivered twice\n" + Transcript(a, b));
    }

    /// <summary>Wait until <paramref name="sender"/> considers its queue
    /// drained (it got the ack), so "give a duplicate a chance to turn
    /// up" doesn't rely on a fixed sleep that a retry backoff could
    /// outlast.</summary>
    private static async Task WaitForDrainAsync(DappsDaemon sender, CancellationToken ct, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await sender.PendingOutboundAsync(ct) == 0) return;
            await Task.Delay(200, ct);
        }
    }

    private string Transcript(DappsDaemon a, DappsDaemon b) =>
        "--- air ---\n" + air.Transcript() + "\n" + a.Tail(40) + "\n" + b.Tail(40);

    private async Task AirShowsAsync(string side, string contains, CancellationToken ct, TimeSpan timeout)
    {
        if (!await air.WaitForAsync(side, contains, timeout, ct))
        {
            throw new TimeoutException($"Never heard '{contains}' from {side}.\n{air.Transcript()}");
        }
    }

    private async Task<DappsDaemon> StartNodeAsync(
        string name, string callsign, int agwPort, string neighbour,
        IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var node = await DappsDaemon.StartAsync(name, callsign, fixture.Host, agwPort, fixture.AxipPortIndex,
            [new(neighbour, fixture.AxipPortIndex)], settings, ct);
        running.Add(node);
        return node;
    }
}
