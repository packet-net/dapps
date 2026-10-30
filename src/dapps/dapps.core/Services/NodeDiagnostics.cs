using System.Collections.Concurrent;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Asks the local node about the links DAPPS uses, and says what it
/// answered in the log (#425): whether sessions through a Connect via node
/// can go by NET/ROM or only by L2, one at a time. Fixed behaviour, no
/// settings, nothing on air.
///
/// At start-up, once the node link is up, it types NODES at the node's own
/// prompt (<see cref="INodePrompt"/>) and logs one line per distinct
/// Connect via. When the forwarder holds mail back because a link is busy,
/// <see cref="LinkBusy"/> takes a LINKS snapshot and logs the lines about
/// that link, at most once per link every <see cref="SnapshotEvery"/>.
/// Both run in the background and never hold up the forwarder; a failure
/// is one log line and is otherwise ignored. Once the node turns out to
/// have no way to its prompt at all, it isn't asked again.
/// </summary>
public sealed class NodeDiagnostics(
    INodePrompt prompt,
    Func<CancellationToken, Task<IReadOnlyCollection<string>>> connectVias,
    IOptionsMonitor<SystemOptions> options,
    ILogger<NodeDiagnostics> logger,
    IAgwLinkSource? agwLinks = null,
    TimeProvider? clock = null) : BackgroundService
{
    /// <summary>At most one LINKS snapshot per link this often.</summary>
    public static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(10);

    /// <summary>Pause before the start-up check, for the node link to open.</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest the start-up check waits for the AGW link to open.</summary>
    internal TimeSpan LinkUpWait { get; init; } = TimeSpan.FromMinutes(2);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> viaNodeCalls = new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken stopping;

    // Set once the node has no way to its prompt at all: asking again
    // would only repeat the same line.
    private volatile bool cantAsk;

    /// <summary>LINKS snapshots started, for tests to wait on.</summary>
    internal ConcurrentQueue<Task> Snapshots { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        await Task.Yield();
        await StartupCheckAsync(stoppingToken);
    }

    private async Task StartupCheckAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, time, ct);
            var vias = await connectVias(ct);
            if (vias.Count == 0)
            {
                logger.LogInformation("Node check: no neighbour has a Connect via, so there is nothing to ask the node");
                return;
            }
            if (!await NodeLinkUpAsync(ct))
            {
                logger.LogInformation("Node check: can't ask the node about Connect via nodes: the AGW connection to it didn't open");
                return;
            }

            string reply;
            try
            {
                reply = await prompt.AskAsync("NODES", ct);
            }
            catch (NodePromptUnavailableException ex)
            {
                if (ex.ForGood) cantAsk = true;
                logger.LogInformation("Node check: can't ask the node about Connect via nodes: {0}; type NODES at the node's console to check", ex.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                // Saying each via isn't listed would mislead.
                logger.LogInformation("Node check: the node didn't answer NODES; type NODES at the node's console to check");
                return;
            }

            var nodes = NodeTableText.ParseNodes(reply);
            foreach (var via in vias)
            {
                if (NodeTableText.Find(nodes, via) is { } entry)
                {
                    viaNodeCalls[via] = entry.Callsign;
                    logger.LogInformation("{0} is in the node's NODES list as {1}; sessions through it can go by NET/ROM", via, entry);
                }
                else
                {
                    logger.LogInformation("{0} isn't in the node's NODES list; sessions through it go by L2, one at a time", via);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogInformation("Node check: asking the node for NODES failed: {0}", ex.Message);
        }
    }

    /// <summary>On AGW, waits for the link to open; on RHPv2 each question
    /// makes its own connection.</summary>
    private async Task<bool> NodeLinkUpAsync(CancellationToken ct)
    {
        if (agwLinks is null || string.Equals(options.CurrentValue.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase)) return true;
        var deadline = time.GetUtcNow() + LinkUpWait;
        while (agwLinks.TryGetLink() is null)
        {
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
        }
        return true;
    }

    /// <summary>
    /// The forwarder is holding mail for <paramref name="route"/> back
    /// because its link is busy: take a LINKS snapshot in the background
    /// and log the lines about that link, unless one was taken for it in
    /// the last <see cref="SnapshotEvery"/>. Returns at once.
    /// </summary>
    public void LinkBusy(BackhaulRoute route, string ourCallsign)
    {
        if (cantAsk) return;
        var dial = route.DialCallsign;
        var key = $"{dial}|{route.BearerPort}";
        var now = time.GetUtcNow();
        var due = true;
        lastSnapshot.AddOrUpdate(key, now, (_, last) =>
        {
            if (now - last < SnapshotEvery)
            {
                due = false;
                return last;
            }
            return now;
        });
        if (!due) return;
        // Only the ones still going are kept, so this doesn't grow.
        while (Snapshots.TryPeek(out var head) && head.IsCompleted) Snapshots.TryDequeue(out _);
        Snapshots.Enqueue(Task.Run(() => SnapshotAsync(dial, ourCallsign, stopping), CancellationToken.None));
    }

    private async Task SnapshotAsync(string dial, string ourCallsign, CancellationToken ct)
    {
        try
        {
            string reply;
            try
            {
                reply = await prompt.AskAsync("LINKS", ct);
            }
            catch (NodePromptUnavailableException ex)
            {
                if (ex.ForGood) cantAsk = true;
                logger.LogInformation("Node LINKS while the link to {0} is busy: can't ask the node: {1}", dial, ex.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                logger.LogInformation("Node LINKS while the link to {0} is busy: the node didn't answer LINKS", dial);
                return;
            }

            var calls = new List<string> { dial, ourCallsign };
            if (viaNodeCalls.TryGetValue(dial, out var nodeCall)) calls.Add(nodeCall);
            var lines = NodeTableText.LinesNaming(reply, calls);
            if (lines.Count == 0)
            {
                logger.LogInformation("Node LINKS while the link to {0} is busy: no line for {1}", dial, string.Join(" or ", calls.Distinct(StringComparer.OrdinalIgnoreCase)));
                return;
            }
            foreach (var line in lines)
            {
                logger.LogInformation("Node LINKS while the link to {0} is busy: {1}", dial, line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogInformation("Node LINKS while the link to {0} is busy: asking the node failed: {1}", dial, ex.Message);
        }
    }

    /// <summary>Each distinct Connect via among <paramref name="neighbours"/>,
    /// as the operator wrote it, leaving out a via that is the neighbour itself.</summary>
    public static IReadOnlyCollection<string> DistinctVias(IEnumerable<DbNeighbour> neighbours) =>
        neighbours
            .Where(n => !string.IsNullOrWhiteSpace(n.ConnectVia) && !string.Equals(n.ConnectVia!.Trim(), n.Callsign, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.ConnectVia!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
}
