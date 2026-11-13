using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration.Rig425.RigB;

/// <summary>
/// The radio channel as MB7NPW's and CALNE's LinBPQ monitors report it
/// (AGW 'm'), radio port only. BPQ reports what a node received (AGW 'U',
/// 'I', 'S') and, where it does, what it sent ('T'); each frame twice, so
/// consecutive repeats are folded. A frame from A that B's monitor reports
/// was heard; one Dire Wolf sent (net-sim's count) that B never reports was
/// lost on air.
/// </summary>
public sealed partial class RigBAir : IAsyncDisposable
{
    /// <summary>One monitored frame. <see cref="Side"/> is whose monitor ("A" MB7NPW, "B" CALNE); <see cref="Sent"/> for 'T'.</summary>
    public sealed partial record Frame(DateTime At, string Side, bool Sent, string Text)
    {
        private static readonly Regex Header = HeaderRegex();

        public Match Parsed => Header.Match(Text);
        public string From => Parsed.Groups["from"].Value;
        public string To => Parsed.Groups["to"].Value;

        /// <summary>BPQ's frame type token: I, RR, RNR, REJ, C (SABM), D (DISC), UA, DM, FRMR, UI.</summary>
        public string Type => Parsed.Groups["type"].Value;

        /// <summary>The flags after the type: "C P S3 R2" and so on.</summary>
        public string Flags => Parsed.Groups["flags"].Value;

        public bool Command => Regex.IsMatch(Flags, @"(^| )C( |$)");
        public bool Poll => Command && Regex.IsMatch(Flags, @"(^| )P( |$)");
        public int? Ns => Regex.Match(Flags, @"S(\d)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

        [GeneratedRegex(@"Fm (?<from>\S+) To (?<to>\S+)(?: Via \S+)? <(?<type>[A-Z?]+)(?<flags>[^>]*)>")]
        private static partial Regex HeaderRegex();
    }

    private readonly List<Frame> frames = [];
    private readonly List<TcpClient> sockets = [];
    private readonly CancellationTokenSource stop = new();

    public static async Task<RigBAir> StartAsync(RigBNetwork rig, CancellationToken ct)
    {
        var air = new RigBAir();
        await air.TapAsync("A", rig.Host, rig.AgwPort(rig.Mb7npw), ct);
        await air.TapAsync("B", rig.Host, rig.AgwPort(rig.Calne), ct);
        return air;
    }

    private async Task TapAsync(string side, string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        sockets.Add(tcp);
        await tcp.ConnectAsync(host, port, ct);
        var agw = new AgwFrameTransport(tcp.GetStream());
        await agw.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);
        _ = Task.Run(async () =>
        {
            (char Kind, string Text)? last = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var f = await agw.ReadFrameAsync(stop.Token);
                    if (f.Kind is not ('U' or 'I' or 'S' or 'T') || f.Port != RigBNetwork.RfAgwPort) continue;
                    var text = Encoding.Latin1.GetString(f.Payload).Replace("\r", "\n").Trim();
                    if (last is { } l && l.Kind == f.Kind && l.Text == text) continue;
                    last = (f.Kind, text);
                    lock (frames) frames.Add(new Frame(DateTime.UtcNow, side, f.Kind == 'T', text));
                }
            }
            catch (Exception)
            {
                // Monitor closed.
            }
        }, CancellationToken.None);
    }

    public IReadOnlyList<Frame> Between(DateTime from, DateTime to)
    {
        lock (frames) return [.. frames.Where(f => f.At >= from && f.At <= to)];
    }

    /// <summary>Frames heard from the other side: received by <paramref name="side"/>.</summary>
    public static IEnumerable<Frame> HeardBy(IEnumerable<Frame> window, string side) => window.Where(f => f.Side == side && !f.Sent);

    /// <summary>Frames sent by a side as the other side's monitor heard them.</summary>
    public static IEnumerable<Frame> SentBy(IEnumerable<Frame> window, string side) => HeardBy(window, side == "A" ? "B" : "A");

    /// <summary>
    /// I-frames heard twice: per link (from, to), an I-frame with the same
    /// N(S) and the same contents as one of the last seven heard on that
    /// link. A frame lost the first time and heard once is not counted here
    /// (it is in the frames lost on air). A SABM resets the link.
    /// </summary>
    public static int Retransmissions(IEnumerable<Frame> heard)
    {
        var recent = new Dictionary<(string, string), Queue<(int, string)>>();
        var count = 0;
        foreach (var f in heard)
        {
            var key = (f.From, f.To);
            if (f.Type == "C") { recent.Remove(key); continue; }
            if (f.Type != "I" || f.Ns is not { } ns) continue;
            var body = f.Text.IndexOf('\n') is var nl and >= 0 ? f.Text[(nl + 1)..] : "";
            var q = recent.TryGetValue(key, out var existing) ? existing : recent[key] = new Queue<(int, string)>();
            if (q.Contains((ns, body))) { count++; continue; }
            q.Enqueue((ns, body));
            if (q.Count > 7) q.Dequeue();
        }
        return count;
    }

    /// <summary>
    /// Links given up on: <paramref name="retries"/> or more polls (or
    /// SABMs) in a row from one station to another with nothing heard back
    /// in between, as LinBPQ sends before it reports RETRYOUT. Counted from
    /// what the far end heard, so a lower bound when frames are lost.
    /// </summary>
    public static int RetryOuts(IEnumerable<Frame> both, int retries)
    {
        var run = new Dictionary<(string, string), int>();
        var count = 0;
        foreach (var f in both.Where(f => !f.Sent))
        {
            run.Remove((f.To, f.From));
            if (f.Poll || f.Type == "C")
            {
                var n = run.GetValueOrDefault((f.From, f.To)) + 1;
                run[(f.From, f.To)] = n;
                if (n == retries + 1) count++;
            }
            else
            {
                run.Remove((f.From, f.To));
            }
        }
        return count;
    }

    public static string Transcript(IEnumerable<Frame> window, DateTime origin) =>
        string.Join('\n', window.Where(f => !f.Sent).Select(f =>
            $"{(f.At - origin).TotalSeconds,8:F2}  {(f.Side == "A" ? "CALNE->MB7NPW" : "MB7NPW->CALNE")}  {Printable(f.Text.Replace("\n", " | "))}"));

    private static string Printable(string text)
    {
        var s = new string([.. text.Select(c => c >= ' ' && c <= '~' ? c : '.')]);
        return s.Length > 160 ? s[..160] + "..." : s;
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var s in sockets) s.Dispose();
        return ValueTask.CompletedTask;
    }
}
