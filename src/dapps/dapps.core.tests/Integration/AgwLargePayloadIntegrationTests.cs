using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace dapps.core.tests.Integration;

/// <summary>
/// Payloads bigger than one AGW data frame, through real BPQ both ways.
/// BPQ silently drops an AGW 'D' frame carrying more than 256 bytes, so a
/// payload written in one go never arrived and the far end sat waiting for
/// it. Each size here crosses BPQ-A, AXIP and BPQ-B to DAPPS's real inbound
/// service, on one session; the far end then drains a large message back
/// to us with <c>rev</c>, which exercises the inbound side's writes.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class AgwLargePayloadIntegrationTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private RecordingInbox farInbox = null!;
    private AgwInboundService service = null!;

    public async ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-large-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
        }

        var receiverOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = fixture.ApplCallB,
            NodeHost = fixture.Host,
            AgwPort = fixture.AgwPortB,
        });
        database = new Database(NullLogger<Database>.Instance, receiverOptions);
        farInbox = new RecordingInbox();
        service = new AgwInboundService(
            receiverOptions, database, farInbox,
            NullLoggerFactory.Instance, NullLogger<AgwInboundService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        await service.StopAsync(CancellationToken.None);
        // As in AgwInboundDeliveryTests: let BPQ release the registration
        // before the next test in the collection registers the same call.
        await Task.Delay(2000);
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task PayloadsOfEverySize_CrossRealBpq_BothWays()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = ctSource.Token;

        // Mail the far end holds for us, drained back with rev on the same
        // session: its writes go through the inbound service's stream.
        var back = Payload(1000, 'r');
        var backId = DappsMessage.ComputeHash(back, 7L)[..7];
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = backId, Payload = back, Salt = 7L, Destination = $"app@{fixture.CallsignA}",
                SourceCallsign = fixture.ApplCallB, AdditionalProperties = "{}", CreatedAt = DateTime.UtcNow,
            });
        }

        // Around the old limit (256 including the `data` line) and well past it.
        var sizes = new[] { 100, 240, 250, 300, 1000, 3000 };
        var messages = sizes.Select((size, i) =>
        {
            var payload = Payload(size, (char)('a' + i));
            return new BackhaulMessage(DappsMessage.ComputeHash(payload, i)[..7], $"app@{fixture.CallsignB}", i, 600, payload);
        }).ToArray();

        var ourInbox = new RecordingInbox();
        var bearer = new Dappsv1SessionBackhaul(
            new AgwOutboundTransport(fixture.Host, fixture.AgwPortA, NullLoggerFactory.Instance),
            NullLoggerFactory.Instance, opportunisticInbox: ourInbox, opportunisticEnabled: () => true);
        var batch = new ListBatch(messages);

        await bearer.SendBatchAsync(new BackhaulRoute(fixture.ApplCallB, BearerPort: fixture.AxipPortIndex), fixture.ApplCallA, batch, ct);

        batch.Outcomes.Select(o => (o.Id, o.Result.Accepted)).Should().Equal(messages.Select(m => (m.Id, true)),
            $"every size should be acked; failures: {string.Join("; ", batch.Outcomes.Where(o => !o.Result.Accepted).Select(o => $"{o.Id}: {o.Result.Error}"))}");
        farInbox.Payloads.Should().HaveCount(sizes.Length);
        for (var i = 0; i < messages.Length; i++)
        {
            farInbox.Payloads[i].Should().Equal(messages[i].Payload, $"the {sizes[i]}-byte payload arrives intact");
        }
        ourInbox.Payloads.Should().ContainSingle().Which.Should().Equal(back, "the far end's 1000-byte reply comes back intact by rev");
    }

    private static byte[] Payload(int size, char seed) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, size).Select(i => (char)(seed + i % 7))))[..size];

    private sealed class ListBatch(params BackhaulMessage[] messages) : IBackhaulBatch
    {
        private readonly Queue<BackhaulMessage> queue = new(messages);
        public List<(string Id, BackhaulSendResult Result)> Outcomes { get; } = [];

        public ValueTask<BackhaulMessage?> NextAsync(CancellationToken ct) =>
            ValueTask.FromResult(queue.TryDequeue(out var next) ? next : null);

        public ValueTask CompleteAsync(BackhaulMessage message, BackhaulSendResult result, TimeSpan elapsed, CancellationToken ct)
        {
            Outcomes.Add((message.Id, result));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingInbox : IBackhaulInbox
    {
        private readonly List<byte[]> payloads = [];
        public IReadOnlyList<byte[]> Payloads { get { lock (payloads) return [.. payloads]; } }

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            lock (payloads) payloads.Add(message.Payload);
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
