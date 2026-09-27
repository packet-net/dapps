using System.Text.Json;

namespace dapps.core.tests.Integration;

/// <summary>
/// When each simulated radio was on air, from net-sim's event stream
/// (<c>/api/events</c>), to the block (tens of milliseconds) where BPQ's
/// monitor only stamps whole seconds. This is what shows where an
/// exchange's time goes: on air, or in the quiet gaps between
/// transmissions (TX delay, turnarounds, the software on each end).
///
/// It uses what each receiver hears (<c>rx_decision</c>), which net-sim
/// plays out in real time. Its transmit events follow the modem's audio
/// output instead, and samoyed produces a burst's audio faster than real
/// time, so those would make every transmission look short.
///
/// net-sim's stream is lossy by design (a slow reader loses events). The
/// listener reconnects if the stream ends, e.g. across
/// <see cref="NetSimTwoBpqFixture.ChannelOutageAsync"/>.
/// </summary>
internal sealed class ChannelLog : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Over> overs = [];
    private readonly Dictionary<string, DateTime> keyed = [];
    private Task reader = Task.CompletedTask;

    /// <summary>One transmission. <see cref="Side"/> is "A" or "B", from net-sim's node ids a and b.</summary>
    public sealed record Over(string Side, DateTime Start, DateTime End)
    {
        public TimeSpan Length => End - Start;
    }

    private ChannelLog(HttpClient http) => this.http = http;

    public static async Task<ChannelLog> StartAsync(string host, int webPort, CancellationToken ct)
    {
        var log = new ChannelLog(new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{webPort}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        });
        var first = await log.OpenAsync(ct);
        log.reader = Task.Run(() => log.ReadAsync(first));
        return log;
    }

    private async Task<Stream> OpenAsync(CancellationToken ct)
    {
        var response = await http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private async Task ReadAsync(Stream stream)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using (var lines = new StreamReader(stream))
                {
                    while (await lines.ReadLineAsync(stop.Token) is { } line)
                    {
                        if (line.StartsWith("data: ", StringComparison.Ordinal)) Record(line[6..]);
                    }
                }
                await Task.Delay(500, stop.Token);
                stream = await OpenAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // net-sim restarting; try again shortly.
                try { await Task.Delay(500, stop.Token); stream = await OpenAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { stream = Stream.Null; }
            }
        }
    }

    private void Record(string json)
    {
        var e = JsonDocument.Parse(json).RootElement;
        if (e.GetProperty("type").GetString() != "rx_decision") return;
        // The receiver at one end hears the other end's transmissions.
        var heardBy = e.GetProperty("port").GetString() ?? "";
        var (side, sourcePort) = heardBy.StartsWith("b.", StringComparison.Ordinal) ? ("A", "a.")
            : heardBy.StartsWith("a.", StringComparison.Ordinal) ? ("B", "b.")
            : (null, null);
        if (side is null) return;
        var at = e.GetProperty("t").GetDateTime().ToUniversalTime();
        var hearing = e.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array
            && sources.EnumerateArray().Any(x => x.GetString()?.StartsWith(sourcePort!, StringComparison.Ordinal) == true);
        lock (overs)
        {
            if (hearing) keyed.TryAdd(side, at);
            else if (keyed.Remove(side, out var start)) overs.Add(new Over(side, start, at));
        }
    }

    /// <summary>Transmissions that started between <paramref name="from"/> and <paramref name="to"/> (UTC).</summary>
    public IReadOnlyList<Over> Between(DateTime from, DateTime to)
    {
        lock (overs) return [.. overs.Where(o => o.Start >= from && o.Start <= to).OrderBy(o => o.Start)];
    }

    /// <summary>Airtime, quiet gaps and overlaps for a stretch of the channel.</summary>
    public Summary Summarise(DateTime from, DateTime to) => new(Between(from, to), to - from);

    public sealed class Summary(IReadOnlyList<Over> overs, TimeSpan window)
    {
        public int Count => overs.Count;
        public int CountBy(string side) => overs.Count(o => o.Side == side);
        public TimeSpan Airtime => TimeSpan.FromTicks(overs.Sum(o => o.Length.Ticks));

        /// <summary>Time the channel was carrying something: the union of every transmission.</summary>
        public TimeSpan Busy
        {
            get
            {
                var busy = TimeSpan.Zero;
                DateTime? spanStart = null, spanEnd = null;
                foreach (var o in overs)
                {
                    if (spanEnd is { } e && o.Start <= e)
                    {
                        if (o.End > e) spanEnd = o.End;
                        continue;
                    }
                    if (spanStart is { } s) busy += spanEnd!.Value - s;
                    (spanStart, spanEnd) = (o.Start, o.End);
                }
                if (spanStart is { } last) busy += spanEnd!.Value - last;
                return busy;
            }
        }

        public double BusyPercent => window > TimeSpan.Zero ? 100.0 * Busy.Ticks / window.Ticks : 0;

        /// <summary>
        /// Quiet time between one transmission ending and the next
        /// starting, where the next came within <paramref name="within"/>
        /// (so waits for new traffic don't count): the turnaround cost.
        /// </summary>
        public IReadOnlyList<double> Gaps(TimeSpan within)
        {
            var gaps = new List<double>();
            for (var i = 1; i < overs.Count; i++)
            {
                var gap = overs[i].Start - overs[i - 1].End;
                if (gap >= TimeSpan.Zero && gap <= within) gaps.Add(gap.TotalSeconds);
            }
            gaps.Sort();
            return gaps;
        }

        /// <summary>Transmissions that started while the other side's was still on air.</summary>
        public int Doubles
        {
            get
            {
                var n = 0;
                for (var i = 1; i < overs.Count; i++)
                {
                    for (var j = i - 1; j >= 0 && j >= i - 3; j--)
                    {
                        if (overs[j].Side != overs[i].Side && overs[i].Start < overs[j].End) { n++; break; }
                    }
                }
                return n;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await reader; } catch { /* closed */ }
        http.Dispose();
        stop.Dispose();
    }
}
