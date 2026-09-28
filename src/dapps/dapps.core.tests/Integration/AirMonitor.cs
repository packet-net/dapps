using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// What went over the air between the two test nodes, from each node's own
/// monitor. A BPQ node's AGW monitor ('m') reports the frames that node
/// receives, not the ones it sends, so this listens on both: frames BPQ-B
/// heard were sent by A's side, and the other way round. BPQ reports each
/// monitored frame twice; consecutive repeats are folded into one.
///
/// A pdn node's monitor is its frame feed (<c>/api/v1/events</c>), which
/// carries both directions; only the frames it received are kept, so both
/// kinds of node mean the same thing by "heard". Those frames are written
/// out in BPQ's style (<see cref="Render"/>), so the tests can read either.
///
/// Lines look like BPQ's monitor output, e.g.
/// <c>2:Fm N0AAA-9 To N0BBB-9 &lt;SABM P&gt;[20:33:53]</c>, with an
/// I-frame's text following its header.
/// </summary>
internal sealed class AirMonitor : IAirMonitor
{
    private readonly List<(string HeardBy, string Text)> frames = [];
    private readonly List<IDisposable> connections = [];
    private readonly CancellationTokenSource stop = new();

    /// <summary>One node's monitor: BPQ's AGW port, or pdn's web port.</summary>
    public sealed record Tap(string Side, string Host, int Port, bool Pdn)
    {
        public static Tap Bpq(string side, string host, int agwPort) => new(side, host, agwPort, Pdn: false);

        public static Tap PdnNode(string side, string host, int httpPort) => new(side, host, httpPort, Pdn: true);
    }

    public static Task<AirMonitor> StartAsync(string host, int agwPortA, int agwPortB, CancellationToken ct) =>
        StartAsync([Tap.Bpq("A", host, agwPortA), Tap.Bpq("B", host, agwPortB)], ct);

    public static async Task<AirMonitor> StartAsync(IEnumerable<Tap> taps, CancellationToken ct)
    {
        var monitor = new AirMonitor();
        try
        {
            foreach (var tap in taps)
            {
                if (tap.Pdn) await monitor.ListenPdnAsync(tap.Side, tap.Host, tap.Port, ct);
                else await monitor.ListenBpqAsync(tap.Side, tap.Host, tap.Port, ct);
            }
        }
        catch
        {
            await monitor.DisposeAsync();
            throw;
        }
        return monitor;
    }

    private async Task ListenBpqAsync(string name, string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        connections.Add(client);
        await client.ConnectAsync(host, port, ct);
        var agw = new AgwFrameTransport(client.GetStream());
        await agw.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await agw.ReadFrameAsync(stop.Token);
                    Add(name, Encoding.Latin1.GetString(frame.Payload).Replace("\r", "\n").Trim());
                }
            }
            catch
            {
                // Monitor closed.
            }
        });
    }

    /// <summary>
    /// Read a pdn node's frame feed. The feed can drop (a slow reader, the
    /// node restarting), so the reader reconnects; a frame received while it
    /// was away is missed, as it would be by BPQ's monitor.
    /// </summary>
    private async Task ListenPdnAsync(string name, string host, int port, CancellationToken ct)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        connections.Add(http);
        var first = await OpenFeedAsync(http, ct);
        _ = Task.Run(async () =>
        {
            var stream = first;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var lines = new StreamReader(stream))
                    {
                        while (await lines.ReadLineAsync(stop.Token) is { } line)
                        {
                            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                            var e = JsonDocument.Parse(line[6..]).RootElement;
                            if (e.TryGetProperty("direction", out var d) && d.GetString() == "in") Add(name, Render(e));
                        }
                    }
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    // Dropped; reconnect below.
                }
                try
                {
                    await Task.Delay(500, stop.Token);
                    stream = await OpenFeedAsync(http, stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { stream = Stream.Null; }
            }
        });
    }

    private static async Task<Stream> OpenFeedAsync(HttpClient http, CancellationToken ct)
    {
        var response = await http.GetAsync("api/v1/events", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// One of pdn's monitor events as BPQ would print it: a SABM (or a
    /// v2.2 SABME, marked as such) is <c>&lt;C C P&gt;</c>, a DISC
    /// <c>&lt;D C P&gt;</c>, anything else by its own name, with C or R,
    /// P or F, and an I-frame's sequence numbers; then the frame's text.
    /// </summary>
    internal static string Render(JsonElement e)
    {
        var type = e.GetProperty("type").GetString() ?? "?";
        var kind = type switch { "SABM" or "SABME" => "C", "DISC" => "D", _ => type };
        var command = e.GetProperty("command").GetBoolean();
        var poll = e.GetProperty("pf").GetInt32() == 1 ? command ? " P" : " F" : "";
        var ns = e.TryGetProperty("ns", out var n) && n.ValueKind == JsonValueKind.Number ? $" S{n.GetInt32()}" : "";
        var nr = e.TryGetProperty("nr", out var r) && r.ValueKind == JsonValueKind.Number ? $" R{r.GetInt32()}" : "";
        var via = e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0
            ? " Via " + string.Join(',', p.EnumerateArray().Select(x => x.GetString()))
            : "";
        var at = e.TryGetProperty("timestamp", out var t) ? $"[{t.GetDateTimeOffset().UtcDateTime:HH:mm:ss.fff}]" : "";
        var header = $"Fm {e.GetProperty("source").GetString()} To {e.GetProperty("dest").GetString()}{via} " +
            $"<{kind} {(command ? "C" : "R")}{poll}{ns}{nr}>{(type == "SABME" ? " SABME" : "")}{at}";

        var infoLength = e.TryGetProperty("infoLength", out var il) ? il.GetInt32() : 0;
        if (infoLength <= 0 || !e.TryGetProperty("raw", out var raw)) return header;
        var bytes = raw.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
        var info = bytes.AsSpan(Math.Max(0, bytes.Length - infoLength));
        return (header + "\n" + Encoding.Latin1.GetString(info).Replace("\r", "\n")).Trim();
    }

    private void Add(string heardBy, string text)
    {
        lock (frames)
        {
            if (frames.Count > 0 && frames[^1] == (heardBy, text)) return;
            frames.Add((heardBy, text));
        }
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
        foreach (var c in connections) c.Dispose();
        return ValueTask.CompletedTask;
    }
}
