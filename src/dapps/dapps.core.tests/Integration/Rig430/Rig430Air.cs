using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>One AX.25 frame, decoded far enough to count what the links did.</summary>
public sealed record Ax25Frame(string From, string To, bool Command, string Type, int? Ns, int? Nr, bool PollFinal, int? Pid, byte[] Info)
{
    /// <summary>NET/ROM L4 opcode (1 connect request, 2 ack, 3 disconnect request, 4 ack, 5 info, 6 info ack), or null.</summary>
    public int? NetRomOp => Pid == 0xCF && Type is "I" && Info.Length >= 20 ? Info[19] & 0x0F : null;

    public bool IsNodes => Type == "UI" && To == "NODES";

    /// <summary>The unordered callsign pair, as a link key.</summary>
    public (string, string) Pair => string.CompareOrdinal(From, To) < 0 ? (From, To) : (To, From);

    public string Describe()
    {
        var flags = $"{(Command ? "C" : "R")}{(PollFinal ? (Command ? " P" : " F") : "")}{(Ns is { } s ? $" S{s}" : "")}{(Nr is { } r ? $" R{r}" : "")}";
        var nr = NetRomOp is { } op ? $" NR:{op switch { 1 => "CONN REQ", 2 => "CONN ACK", 3 => "DISC REQ", 4 => "DISC ACK", 5 => "INFO", 6 => "INFO ACK", 7 => "NAK", _ => op.ToString() }}" : "";
        return $"{From}>{To} <{Type} {flags}>{nr}{(Info.Length > 0 ? $" {Info.Length}B" : "")}";
    }

    private static string Address(ReadOnlySpan<byte> b, out bool cBit)
    {
        var call = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var c = (char)(b[i] >> 1);
            if (c != ' ') call.Append(c);
        }
        var ssid = (b[6] >> 1) & 0x0F;
        cBit = (b[6] & 0x80) != 0;
        return ssid == 0 ? call.ToString() : $"{call}-{ssid}";
    }

    /// <summary>Decode a raw AX.25 frame (no flags, no FCS). Null if it doesn't parse.</summary>
    public static Ax25Frame? Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length < 15) return null;
        var to = Address(f[..7], out var destC);
        var from = Address(f.Slice(7, 7), out var srcC);
        var i = 14;
        if ((f[13] & 1) == 0)
        {
            // digipeaters
            while (i + 7 <= f.Length)
            {
                var last = (f[i + 6] & 1) != 0;
                i += 7;
                if (last) break;
            }
        }
        if (i >= f.Length) return null;
        var ctl = f[i++];
        var command = destC && !srcC;
        string type;
        int? ns = null, nr = null, pid = null;
        bool pf;
        if ((ctl & 1) == 0)
        {
            type = "I";
            ns = (ctl >> 1) & 7;
            nr = ctl >> 5;
            pf = (ctl & 0x10) != 0;
            if (i < f.Length) pid = f[i++];
        }
        else if ((ctl & 3) == 1)
        {
            type = ((ctl >> 2) & 3) switch { 0 => "RR", 1 => "RNR", 2 => "REJ", _ => "SREJ" };
            nr = ctl >> 5;
            pf = (ctl & 0x10) != 0;
        }
        else
        {
            pf = (ctl & 0x10) != 0;
            type = (ctl & ~0x10) switch
            {
                0x2F => "SABM",
                0x6F => "SABME",
                0x43 => "DISC",
                0x0F => "DM",
                0x63 => "UA",
                0x87 => "FRMR",
                0x03 => "UI",
                0xAF => "XID",
                0xE3 => "TEST",
                _ => $"U{ctl:X2}",
            };
            if (type == "UI" && i < f.Length) pid = f[i++];
        }
        return new Ax25Frame(from, to, command, type, ns, nr, pf, pid, f[i..].ToArray());
    }
}

/// <summary>A frame as heard on one channel (or carried over NPR) at a moment.</summary>
public sealed record HeardFrame(DateTime At, string Channel, Ax25Frame Frame, string? Direction = null);

/// <summary>
/// A KISS TCP client on one of net-sim's ports: decodes what the modem
/// hears and can send frames (the user station).
/// </summary>
public sealed class KissClient : IAsyncDisposable
{
    private const byte Fend = 0xC0, Fesc = 0xDB, Tfend = 0xDC, Tfesc = 0xDD;
    private readonly string host;
    private readonly int port;
    private readonly CancellationTokenSource stop = new();
    private readonly Task reader;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? tcp;
    private NetworkStream? stream;

    /// <summary>Times the connection dropped and was made again.</summary>
    public int Reconnects { get; private set; }

    private KissClient(string host, int port, Action<DateTime, byte[]> onFrame)
    {
        this.host = host;
        this.port = port;
        reader = Task.Run(() => ReadAsync(onFrame));
    }

    /// <summary>Connect (retrying for 30 s), and keep reconnecting if the port drops.</summary>
    public static async Task<KissClient> ConnectAsync(string host, int port, Action<DateTime, byte[]> onFrame, CancellationToken ct)
    {
        var k = new KissClient(host, port, onFrame);
        await k.connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return k;
    }

    private async Task ReadAsync(Action<DateTime, byte[]> onFrame)
    {
        var buf = new byte[4096];
        while (!stop.IsCancellationRequested)
        {
            var frame = new List<byte>();
            var esc = false;
            try
            {
                var t = new TcpClient();
                await t.ConnectAsync(host, port, stop.Token);
                tcp = t;
                stream = t.GetStream();
                var got = false;
                while (!stop.IsCancellationRequested)
                {
                    // A port that accepts and closes at once (nothing behind
                    // Docker's proxy) isn't connected; the first byte or a
                    // quiet second says it is.
                    var read = stream.ReadAsync(buf, stop.Token).AsTask();
                    if (!got && await Task.WhenAny(read, Task.Delay(1000, stop.Token)) != read)
                    {
                        got = true;
                        connected.TrySetResult();
                    }
                    var n = await read;
                    if (n == 0) break;
                    got = true;
                    connected.TrySetResult();
                    var at = DateTime.UtcNow;
                    for (var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == Fend)
                        {
                            if (frame.Count > 1 && (frame[0] & 0x0F) == 0) onFrame(at, [.. frame.Skip(1)]);
                            frame.Clear();
                            esc = false;
                        }
                        else if (esc)
                        {
                            frame.Add(b == Tfend ? Fend : b == Tfesc ? Fesc : b);
                            esc = false;
                        }
                        else if (b == Fesc) esc = true;
                        else frame.Add(b);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* drop and retry */ }
            tcp?.Dispose();
            if (connected.Task.IsCompleted) Reconnects++;
            try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task SendAsync(byte command, byte[] payload, CancellationToken ct)
    {
        var o = new List<byte> { Fend, command };
        foreach (var b in payload)
        {
            if (b == Fend) { o.Add(Fesc); o.Add(Tfend); }
            else if (b == Fesc) { o.Add(Fesc); o.Add(Tfesc); }
            else o.Add(b);
        }
        o.Add(Fend);
        if (stream is { } st) await st.WriteAsync(o.ToArray(), ct);
    }

    public Task SendFrameAsync(byte[] ax25, CancellationToken ct) => SendAsync(0x00, ax25, ct);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        tcp?.Dispose();
        try { await reader; } catch (Exception) { }
    }

    /// <summary>A UI frame, PID F0.</summary>
    public static byte[] Ui(string from, string to, byte[] info)
    {
        var o = new List<byte>();
        o.AddRange(Addr(to, command: true, last: false));
        o.AddRange(Addr(from, command: false, last: true));
        o.Add(0x03);
        o.Add(0xF0);
        o.AddRange(info);
        return [.. o];
    }

    private static byte[] Addr(string call, bool command, bool last)
    {
        var parts = call.Split('-');
        var c = parts[0].PadRight(6)[..6];
        var ssid = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var b = new byte[7];
        for (var i = 0; i < 6; i++) b[i] = (byte)(c[i] << 1);
        b[6] = (byte)(0x60 | (ssid << 1) | (command ? 0x80 : 0) | (last ? 1 : 0));
        return b;
    }
}

/// <summary>
/// What crossed each link, from three sources: a receive-only monitor on
/// each radio channel (every frame heard, decoded), net-sim's rx_decision
/// events at those monitors (when each station was on air, to about 10 ms),
/// and the NPR relay's log. The user station on 70 cm runs from here too.
/// </summary>
public sealed class Rig430Air : IAsyncDisposable
{
    private readonly List<HeardFrame> frames = [];
    private readonly List<KissClient> monitors = [];
    private readonly Dictionary<string, List<Over>> overs = [];
    private readonly Dictionary<string, Dictionary<string, DateTime>> keyed = [];
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> tasks = [];
    private HttpClient? http;

    /// <summary>One transmission: which channel, which station (net-sim node id), from and to.</summary>
    public sealed record Over(string Channel, string Source, DateTime Start, DateTime End)
    {
        public TimeSpan Length => End - Start;
    }

    public static async Task<Rig430Air> StartAsync(Rig430Network rig, bool userTraffic, int seed, CancellationToken ct)
    {
        var air = new Rig430Air();
        foreach (var ch in rig.Channels)
        {
            var id = ch.Id;
            air.monitors.Add(await KissClient.ConnectAsync(rig.Host, rig.MappedKiss("mon", id), (at, raw) =>
            {
                if (Ax25Frame.Parse(raw) is { } f) lock (air.frames) air.frames.Add(new HeardFrame(at, id, f));
            }, ct));
            air.overs[id] = [];
            air.keyed[id] = [];
        }
        air.http = new HttpClient { BaseAddress = new Uri($"http://{rig.Host}:{rig.NetSimWebPort}/"), Timeout = Timeout.InfiniteTimeSpan };
        air.tasks.Add(Task.Run(() => air.EventsAsync()));
        if (userTraffic) air.tasks.Add(Task.Run(() => air.UserStationAsync(rig, seed)));
        return air;
    }

    // ------------------------------------------------------------ net-sim events

    private async Task EventsAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var response = await http!.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                using var lines = new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token));
                while (await lines.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal)) Record(line[6..]);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { try { await Task.Delay(500, stop.Token); } catch (OperationCanceledException) { return; } }
        }
    }

    private void Record(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.GetProperty("type").GetString() != "rx_decision") return;
        var port = e.GetProperty("port").GetString() ?? "";
        if (!port.StartsWith("mon.", StringComparison.Ordinal)) return;
        var ch = port[4..];
        var at = e.GetProperty("t").GetDateTime().ToUniversalTime();
        var now = new HashSet<string>();
        if (e.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var x in s.EnumerateArray()) if (x.GetString() is { } src) now.Add(src.Split('.')[0]);
        lock (overs)
        {
            if (!keyed.TryGetValue(ch, out var on)) return;
            foreach (var src in now) on.TryAdd(src, at);
            foreach (var src in on.Keys.Where(k => !now.Contains(k)).ToList())
            {
                overs[ch].Add(new Over(ch, src, on[src], at));
                on.Remove(src);
            }
        }
    }

    // ------------------------------------------------------------ user station

    /// <summary>
    /// The 70 cm channel is also user access: a station sends a UI frame of
    /// 60 to 200 bytes every 45 s on average (exponential gaps), through its
    /// own pdn modem with the same TXDELAY and CSMA as the nodes.
    /// </summary>
    private async Task UserStationAsync(Rig430Network rig, int seed)
    {
        var rng = new Random(seed ^ 0x05e7);
        try
        {
            await using var kiss = await KissClient.ConnectAsync(rig.Host, rig.MappedKiss("usr", "c70"), (_, _) => { }, stop.Token);
            var r = Rig430Network.Radio;
            await kiss.SendAsync(0x01, [(byte)(r.TxDelayMs / 10)], stop.Token);
            await kiss.SendAsync(0x02, [(byte)r.Persist], stop.Token);
            await kiss.SendAsync(0x03, [(byte)(r.SlotTimeMs / 10)], stop.Token);
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(-Math.Log(1 - rng.NextDouble()) * UserMeanGapSeconds), stop.Token);
                var text = $"user traffic {++n}: " + new string('x', rng.Next(40, 180));
                await kiss.SendFrameAsync(KissClient.Ui(Rig430Network.UserCall, "CQ", Encoding.ASCII.GetBytes(text)), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    public static double UserMeanGapSeconds =>
        double.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_430_USER_GAP_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 45;

    // ------------------------------------------------------------ queries

    public IReadOnlyList<HeardFrame> Heard(DateTime from, DateTime to)
    {
        lock (frames) return [.. frames.Where(f => f.At >= from && f.At <= to)];
    }

    public IReadOnlyList<Over> Overs(string channel, DateTime from, DateTime to)
    {
        lock (overs) return [.. overs[channel].Where(o => o.Start >= from && o.Start <= to).OrderBy(o => o.Start)];
    }

    /// <summary>Close anything still on air (at the end of a run).</summary>
    public void CloseAll(DateTime at)
    {
        lock (overs)
        {
            foreach (var (ch, on) in keyed)
            {
                foreach (var (src, start) in on) overs[ch].Add(new Over(ch, src, start, at));
                on.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        foreach (var m in monitors) await m.DisposeAsync();
        foreach (var t in tasks) { try { await t; } catch (Exception) { } }
        http?.Dispose();
    }

    /// <summary>NPR frames from the relay's log, between two moments.</summary>
    public static IReadOnlyList<HeardFrame> NprFrames(string relayLog, DateTime from, DateTime to)
    {
        var list = new List<HeardFrame>();
        foreach (var line in relayLog.Split('\n'))
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length != 3 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var unix)) continue;
            var at = DateTime.UnixEpoch.AddSeconds(unix);
            if (at < from || at > to) continue;
            byte[] raw;
            try { raw = Convert.FromHexString(parts[2]); } catch (FormatException) { continue; }
            // AXUDP carries the AX.25 frame and its 2-byte FCS.
            if (raw.Length < 17) continue;
            if (Ax25Frame.Parse(raw.AsSpan(0, raw.Length - 2)) is { } f) list.Add(new HeardFrame(at, "npr", f, parts[1]));
        }
        return list;
    }
}

/// <summary>Counts over one link's frames (a radio channel or NPR).</summary>
public sealed class LinkCounts
{
    public int Frames { get; init; }
    public int IFrames { get; init; }
    public int IFramesRepeated { get; init; }
    public int Sabm { get; init; }
    public int Ua { get; init; }
    public int Disc { get; init; }
    public int Dm { get; init; }
    public int Frmr { get; init; }
    public int Rej { get; init; }
    public int Srej { get; init; }
    public int Polls { get; init; }
    public int NetRomConnects { get; init; }
    public int NetRomDisconnects { get; init; }
    public int Nodes { get; init; }
    public int UserFrames { get; init; }
    /// <summary>A SABM for a pair whose link was up (a reset), or a DM or FRMR on an up link.</summary>
    public int Resets { get; init; }
    /// <summary>Retries+1 polls or SABMs in a row from one end, nothing back.</summary>
    public int RetryOuts { get; init; }

    public static LinkCounts From(IReadOnlyList<HeardFrame> heard, int retries)
    {
        var up = new HashSet<(string, string)>();
        var resets = 0;
        var recent = new Dictionary<(string, string), Queue<(int, string)>>();
        var repeated = 0;
        var run = new Dictionary<(string, string), int>();
        var retryOuts = 0;
        foreach (var h in heard.OrderBy(h => h.At))
        {
            var f = h.Frame;
            var pair = f.Pair;
            switch (f.Type)
            {
                case "SABM" or "SABME":
                    if (up.Contains(pair)) { resets++; up.Remove(pair); }
                    recent.Remove((f.From, f.To));
                    break;
                case "UA":
                    up.Add(pair);
                    break;
                case "DISC":
                    up.Remove(pair);
                    break;
                case "DM" or "FRMR":
                    if (up.Remove(pair)) resets++;
                    break;
                case "I" when f.Ns is { } ns:
                    var key = (f.From, f.To);
                    var body = Convert.ToHexString(f.Info);
                    var q = recent.TryGetValue(key, out var existing) ? existing : recent[key] = new Queue<(int, string)>();
                    if (q.Contains((ns, body))) repeated++;
                    else
                    {
                        q.Enqueue((ns, body));
                        if (q.Count > 7) q.Dequeue();
                    }
                    break;
            }
            // retry-outs: polls or SABMs from one end with nothing from the other in between
            run.Remove((f.To, f.From));
            var poll = f.Command && f.PollFinal && f.Type is "RR" or "RNR" or "REJ" or "I";
            if (poll || f.Type is "SABM" or "SABME")
            {
                var n = run.GetValueOrDefault((f.From, f.To)) + 1;
                run[(f.From, f.To)] = n;
                if (n == retries + 1) retryOuts++;
            }
            else if (f.Type != "UI")
            {
                run.Remove((f.From, f.To));
            }
        }
        return new LinkCounts
        {
            Frames = heard.Count,
            IFrames = heard.Count(h => h.Frame.Type == "I"),
            IFramesRepeated = repeated,
            Sabm = heard.Count(h => h.Frame.Type is "SABM" or "SABME"),
            Ua = heard.Count(h => h.Frame.Type == "UA"),
            Disc = heard.Count(h => h.Frame.Type == "DISC"),
            Dm = heard.Count(h => h.Frame.Type == "DM"),
            Frmr = heard.Count(h => h.Frame.Type == "FRMR"),
            Rej = heard.Count(h => h.Frame.Type == "REJ"),
            Srej = heard.Count(h => h.Frame.Type == "SREJ"),
            Polls = heard.Count(h => h.Frame.Command && h.Frame.PollFinal && h.Frame.Type is "RR" or "RNR" or "REJ" or "I"),
            NetRomConnects = heard.Count(h => h.Frame.NetRomOp == 1),
            NetRomDisconnects = heard.Count(h => h.Frame.NetRomOp == 3),
            Nodes = heard.Count(h => h.Frame.IsNodes),
            UserFrames = heard.Count(h => h.Frame.From == Rig430Network.UserCall),
            Resets = resets,
            RetryOuts = retryOuts,
        };
    }
}
