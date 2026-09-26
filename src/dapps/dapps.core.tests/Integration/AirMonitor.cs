using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// What went over the air between the two test nodes, from BPQ's own
/// monitor. A node's AGW monitor ('m') reports the frames that node
/// receives, not the ones it sends, so this listens on both: frames BPQ-B
/// heard were sent by A's side, and the other way round. BPQ reports each
/// monitored frame twice; consecutive repeats are folded into one.
///
/// Lines look like BPQ's monitor output, e.g.
/// <c>2:Fm N0AAA-9 To N0BBB-9 &lt;SABM P&gt;[20:33:53]</c>, with an
/// I-frame's text following its header.
/// </summary>
internal sealed class AirMonitor : IAsyncDisposable
{
    private readonly List<(string HeardBy, string Text)> frames = [];
    private readonly List<TcpClient> clients = [];
    private readonly CancellationTokenSource stop = new();

    public static async Task<AirMonitor> StartAsync(string host, int agwPortA, int agwPortB, CancellationToken ct)
    {
        var monitor = new AirMonitor();
        await monitor.ListenAsync("A", host, agwPortA, ct);
        await monitor.ListenAsync("B", host, agwPortB, ct);
        return monitor;
    }

    private async Task ListenAsync(string name, string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        clients.Add(client);
        var agw = new AgwFrameTransport(client.GetStream());
        await agw.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    var text = Encoding.Latin1.GetString(frame.Payload).Replace("\r", "\n").Trim();
                    lock (frames)
                    {
                        if (frames.Count > 0 && frames[^1] == (name, text)) continue;
                        frames.Add((name, text));
                    }
                }
            }
            catch
            {
                // Monitor closed.
            }
        });
    }

    /// <summary>Frames the node on side <paramref name="heardBy"/> received.</summary>
    public IReadOnlyList<string> HeardBy(string heardBy)
    {
        lock (frames) return [.. frames.Where(f => f.HeardBy == heardBy).Select(f => f.Text)];
    }

    /// <summary>Frames sent from <paramref name="from"/>'s side: heard by the other node.</summary>
    public IReadOnlyList<string> SentBy(string from) => HeardBy(from == "A" ? "B" : "A");

    public int CountSentBy(string from, string contains) => SentBy(from).Count(f => f.Contains(contains, StringComparison.Ordinal));

    public async Task<bool> WaitForAsync(string from, string contains, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountSentBy(from, contains) > 0) return true;
            await Task.Delay(100, ct);
        }
        return false;
    }

    /// <summary>Everything heard, for a failure message.</summary>
    public string Transcript()
    {
        lock (frames) return string.Join('\n', frames.Select(f => $"[{(f.HeardBy == "A" ? "B->A" : "A->B")}] {f.Text}"));
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var c in clients) c.Dispose();
        return ValueTask.CompletedTask;
    }
}
