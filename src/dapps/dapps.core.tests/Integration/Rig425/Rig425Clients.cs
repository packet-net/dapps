using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using dapps.client.Transport.Agw;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.tests.Integration.Rig425;

/// <summary>A timestamped notebook shared by everything in one experiment.</summary>
public sealed class RigLog
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> lines = [];

    public void Note(string text)
    {
        lock (lines) lines.Add($"{clock.Elapsed.TotalSeconds,7:F2}  {text}");
    }

    public string Text()
    {
        lock (lines) return string.Join('\n', lines);
    }

    public static string Clean(string s) => s.Replace("\r", "|").Replace("\n", "|").Replace("\0", "");

    public static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan limit, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100, ct);
        }
        return true;
    }
}

/// <summary>One AGW event as the application saw it.</summary>
public sealed record AgwEvent(char Kind, byte Port, string From, string To, string Text);

/// <summary>
/// One AGW TCP connection carrying any number of registrations and
/// sessions, logging every frame it gets.
/// </summary>
public sealed class AgwRig : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly AgwFrameTransport agw;
    private readonly Action<string> note;
    private readonly CancellationTokenSource stop = new();
    private readonly List<AgwEvent> events = [];
    public string Name { get; }
    public bool Closed { get; private set; }

    private AgwRig(string name, TcpClient tcp, Action<string> note)
    {
        Name = name;
        this.tcp = tcp;
        agw = new AgwFrameTransport(tcp.GetStream());
        this.note = note;
    }

    public static async Task<AgwRig> OpenAsync(string name, string host, int port, Action<string> note, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);
        var rig = new AgwRig(name, tcp, note);
        _ = rig.PumpAsync();
        return rig;
    }

    public IReadOnlyList<AgwEvent> Events
    {
        get { lock (events) return [.. events]; }
    }

    /// <summary>Register a callsign; returns the reply byte (1 = accepted), or -1 for no reply.</summary>
    public async Task<int> RegisterAsync(string call, CancellationToken ct)
    {
        var before = Events.Count(e => e.Kind == 'X');
        await agw.WriteFrameAsync(new AgwFrame(0, 'X', 0, call, "", []), ct);
        await RigLog.WaitAsync(() => Events.Count(e => e.Kind == 'X') > before, TimeSpan.FromSeconds(5), ct);
        var reply = Events.Where(e => e.Kind == 'X').Skip(before).FirstOrDefault();
        var value = reply is null ? -1 : reply.Text.Length > 0 ? reply.Text[0] : -2;
        note($"{Name}: X {call} -> {(value == -1 ? "no reply" : $"0x{value:X2}")}");
        return value;
    }

    public async Task ConnectAsync(byte port, string from, string to, CancellationToken ct)
    {
        note($"{Name}: sends 'C' port {port} {from}->{to}");
        await agw.WriteFrameAsync(new AgwFrame(port, 'C', 0xF0, from, to, []), ct);
    }

    public async Task SendAsync(byte port, string from, string to, string text, CancellationToken ct)
    {
        note($"{Name}: sends 'D' port {port} {from}->{to}: {RigLog.Clean(text)}");
        await agw.WriteDataAsync(port, from, to, Encoding.ASCII.GetBytes(text), ct);
    }

    public async Task DisconnectAsync(byte port, string from, string to, CancellationToken ct)
    {
        note($"{Name}: sends 'd' port {port} {from}->{to}");
        await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, from, to, []), ct);
    }

    public async Task MonitorAsync(CancellationToken ct) =>
        await agw.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), ct);

    /// <summary>Events for one session, whichever way round the node names it.</summary>
    public IEnumerable<AgwEvent> For(byte port, string a, string b) =>
        Events.Where(e => e.Port == port && ((e.From == a && e.To == b) || (e.From == b && e.To == a)));

    public string DataText(byte port, string a, string b) =>
        string.Concat(For(port, a, b).Where(e => e.Kind == 'D').Select(e => e.Text));

    public bool Saw(byte port, string a, string b, char kind) => For(port, a, b).Any(e => e.Kind == kind);

    private async Task PumpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var f = await agw.ReadFrameAsync(stop.Token);
                var text = Encoding.Latin1.GetString(f.Payload);
                lock (events) events.Add(new AgwEvent(f.Kind, f.Port, f.CallFrom, f.CallTo, text));
                if (f.Kind is 'X' or 'm' or 'U' or 'I' or 'S' or 'T' or 'K' or 'G' or 'R' or 'y' or 'Y') continue;
                note($"{Name}: got '{f.Kind}' port {f.Port} {f.CallFrom}->{f.CallTo}: {RigLog.Clean(text)}");
            }
        }
        catch
        {
            Closed = true;
            if (!stop.IsCancellationRequested) note($"{Name}: socket closed");
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A node's telnet console, logged in, for looking at tables.</summary>
public sealed class RigTelnet : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly StringBuilder received = new();
    private readonly CancellationTokenSource stop = new();

    private RigTelnet(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    public static async Task<RigTelnet> OpenAsync(RigNode node, string host, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, node.TelnetPort, ct);
        var t = new RigTelnet(tcp);
        _ = t.PumpAsync();
        // Answer whatever login it asks for.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(700, ct);
            var seen = t.Text.ToLowerInvariant();
            if (seen.Contains("password") && !seen.Contains("#pw")) { await t.WriteAsync("test\r", ct); t.Mark("#pw"); }
            else if ((seen.Contains("user") || seen.Contains("call") || seen.Contains("login")) && !seen.Contains("#user"))
            {
                await t.WriteAsync((node.Kind == RigKind.Bpq ? "test" : "G9TST") + "\r", ct);
                t.Mark("#user");
            }
        }
        return t;
    }

    private string Text
    {
        get { lock (received) return received.ToString(); }
    }

    private void Mark(string m)
    {
        lock (received) received.Append(m);
    }

    private async Task WriteAsync(string s, CancellationToken ct)
    {
        var b = Encoding.ASCII.GetBytes(s.Replace("\r", "\r\n"));
        await stream.WriteAsync(b, ct);
    }

    public async Task<string> CommandAsync(string command, TimeSpan wait, CancellationToken ct)
    {
        var before = Text.Length;
        await WriteAsync(command + "\r", ct);
        await Task.Delay(wait, ct);
        return Text[before..];
    }

    private async Task PumpAsync()
    {
        var buf = new byte[4096];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf, stop.Token);
                if (n == 0) return;
                // Drop telnet negotiation (IAC sequences).
                var sb = new StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] == 255 && i + 2 < n) { i += 2; continue; }
                    sb.Append((char)buf[i]);
                }
                lock (received) received.Append(sb);
            }
        }
        catch
        {
            // closed
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// LinBPQ's RHP server: JSON over a WebSocket at /rhp on the HTTP port
/// (RHP.c). Its JSON reader wants no spaces after the colons, so requests
/// are written by hand. An open always lands at the node's command prompt,
/// whatever "remote" says.
/// </summary>
public sealed class BpqRhpRig : IAsyncDisposable
{
    private readonly ClientWebSocket ws = new();
    private readonly Action<string> note;
    private readonly CancellationTokenSource stop = new();
    private readonly List<JsonElement> messages = [];
    private int nextId = 1;
    public string Name { get; }
    public bool Closed { get; private set; }

    private BpqRhpRig(string name, Action<string> note)
    {
        Name = name;
        this.note = note;
    }

    public static async Task<BpqRhpRig> OpenAsync(string name, string host, int httpPort, Action<string> note, CancellationToken ct)
    {
        var rig = new BpqRhpRig(name, note);
        await rig.ws.ConnectAsync(new Uri($"ws://{host}:{httpPort}/rhp"), ct);
        _ = rig.PumpAsync();
        return rig;
    }

    public IReadOnlyList<JsonElement> Messages
    {
        get { lock (messages) return [.. messages]; }
    }

    /// <summary>Open a stream; returns the handle, or the error text.</summary>
    public async Task<(int Handle, string Reply)> OpenAsync(string local, string remote, int port, CancellationToken ct)
    {
        var id = nextId++;
        await SendRawAsync($"{{\"type\":\"open\",\"id\":{id},\"pfam\":\"ax25\",\"mode\":\"stream\",\"port\":\"{port}\",\"local\":\"{local}\",\"remote\":\"{remote}\",\"flags\":128}}", ct);
        await RigLog.WaitAsync(() => Messages.Any(m => Type(m) == "openReply" && Int(m, "id") == id), TimeSpan.FromSeconds(10), ct);
        var reply = Messages.FirstOrDefault(m => Type(m) == "openReply" && Int(m, "id") == id);
        return reply.ValueKind == JsonValueKind.Undefined ? (0, "no reply") : (Int(reply, "handle"), reply.GetRawText());
    }

    public async Task SendAsync(int handle, string text, CancellationToken ct)
    {
        note($"{Name}: send on handle {handle}: {RigLog.Clean(text)}");
        await SendRawAsync($"{{\"type\":\"send\",\"handle\":{handle},\"data\":\"{Escape(text)}\",\"id\":{nextId++}}}", ct);
    }

    public async Task CloseAsync(int handle, CancellationToken ct) =>
        await SendRawAsync($"{{\"type\":\"close\",\"handle\":{handle},\"id\":{nextId++}}}", ct);

    public string Received(int handle) => string.Concat(Messages
        .Where(m => Type(m) == "recv" && Int(m, "handle") == handle)
        .Select(m => m.GetProperty("data").GetString()));

    public bool SawClose(int handle) => Messages.Any(m => Type(m) == "close" && Int(m, "handle") == handle);

    /// <summary>Send any JSON as it stands, for probing what BPQ answers.</summary>
    public Task SendRawForTestAsync(string json, CancellationToken ct) => SendRawAsync(json, ct);

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        if (!json.Contains("\"send\"")) note($"{Name} -> {json}");
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        // BPQ handles each message on its own thread; keep them in order.
        await Task.Delay(150, ct);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r");

    private static string? Type(JsonElement m) => m.TryGetProperty("type", out var t) ? t.GetString() : null;

    private static int Int(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

    private async Task PumpAsync()
    {
        var buf = new byte[65536];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) throw new IOException("closed");
                    ms.Write(buf, 0, r.Count);
                }
                while (!r.EndOfMessage);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                note($"{Name} <- {RigLog.Clean(text)}");
                try
                {
                    var doc = JsonDocument.Parse(text).RootElement.Clone();
                    lock (messages) messages.Add(doc);
                }
                catch (JsonException)
                {
                    note($"{Name}: (not JSON)");
                }
            }
        }
        catch
        {
            Closed = true;
            if (!stop.IsCancellationRequested) note($"{Name}: websocket closed");
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        ws.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An RHPv2 connection (XRouter, pdn) with everything it hears logged.</summary>
public sealed class RhpRig : IAsyncDisposable
{
    private readonly RhpClient rhp;
    private readonly Action<string> note;
    private readonly Dictionary<int, StringBuilder> received = [];
    private readonly HashSet<int> closed = [];
    private readonly List<(int Listener, int Child, string Remote, string Local)> accepted = [];
    private readonly Dictionary<int, int> lastFlags = [];
    public string Name { get; }

    private RhpRig(string name, RhpClient rhp, Action<string> note)
    {
        Name = name;
        this.rhp = rhp;
        this.note = note;
        rhp.Received += (_, e) =>
        {
            var data = e.Message.Data ?? "";
            lock (received)
            {
                if (!received.TryGetValue(e.Message.Handle, out var sb)) received[e.Message.Handle] = sb = new StringBuilder();
                sb.Append(data);
            }
            note($"{Name}: recv handle {e.Message.Handle}: {RigLog.Clean(data)}");
        };
        rhp.Closed += (_, e) =>
        {
            lock (closed) closed.Add(e.Handle);
            note($"{Name}: close from server, handle {e.Handle}");
        };
        rhp.StatusChanged += (_, e) =>
        {
            lock (lastFlags) lastFlags[e.Message.Handle] = e.Message.Flags ?? -1;
            note($"{Name}: status handle {e.Message.Handle} flags {e.Message.Flags}");
        };
        rhp.Accepted += (_, e) =>
        {
            var m = e.Message;
            lock (accepted) accepted.Add((m.Handle, m.Child, m.Remote ?? "", m.Local ?? ""));
            note($"{Name}: accept on listener {m.Handle}: child {m.Child}, remote {m.Remote}, local {m.Local}, port {m.Port}");
        };
        rhp.UnknownReceived += (_, e) => note($"{Name}: unknown message {e.Message.Type}");
        rhp.Disconnected += (_, e) => note($"{Name}: connection ended {e?.Message}");
    }

    public static async Task<RhpRig> OpenAsync(string name, string host, int port, Action<string> note, CancellationToken ct)
    {
        var c = await RhpClient.ConnectAsync(host, port, ct);
        return new RhpRig(name, c, note);
    }

    /// <summary>An active stream open; returns the handle or the error.</summary>
    public async Task<(int Handle, string? Error)> OpenAsync(string pfam, string? port, string local, string remote, CancellationToken ct)
    {
        note($"{Name}: open {pfam} stream port {port ?? "(none)"} {local}->{remote}");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(100));
            var h = await rhp.OpenAsync(pfam, SocketMode.Stream, port, local, remote, OpenFlags.Active, limit.Token);
            note($"{Name}: open {local}->{remote} gave handle {h}");
            return (h, null);
        }
        catch (RhpServerException e)
        {
            note($"{Name}: open {local}->{remote} refused: error {e.ErrorCode} {e.Message}");
            return (0, $"error {e.ErrorCode}: {e.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            note($"{Name}: open {local}->{remote}: no reply in 100 s");
            return (0, "no reply in 100 s");
        }
    }

    /// <summary>socket, bind, listen; returns the listener handle or the error.</summary>
    public async Task<(int Handle, string? Error)> ListenAsync(string local, string? port, CancellationToken ct)
    {
        try
        {
            var h = await rhp.SocketAsync(RhpV2.Client.Protocol.ProtocolFamily.Ax25, SocketMode.Stream, ct);
            await rhp.BindAsync(h, local, port, ct);
            await rhp.ListenAsync(h, OpenFlags.Passive, ct);
            note($"{Name}: listening as {local} (port {port ?? "any"}), handle {h}");
            return (h, null);
        }
        catch (RhpServerException e)
        {
            note($"{Name}: listen as {local} refused: error {e.ErrorCode} {e.Message}");
            return (0, $"error {e.ErrorCode}: {e.Message}");
        }
    }

    public async Task<string?> SendAsync(int handle, string text, CancellationToken ct)
    {
        note($"{Name}: send on handle {handle}: {RigLog.Clean(text)}");
        try
        {
            await rhp.SendOnHandleAsync(handle, text, ct);
            return null;
        }
        catch (RhpServerException e)
        {
            note($"{Name}: send on {handle} refused: error {e.ErrorCode} {e.Message}");
            return $"error {e.ErrorCode}";
        }
    }

    public async Task CloseAsync(int handle, CancellationToken ct)
    {
        try { await rhp.CloseAsync(handle, ct); }
        catch (Exception e) { note($"{Name}: close {handle}: {e.Message}"); }
    }

    public string Received(int handle)
    {
        lock (received) return received.TryGetValue(handle, out var sb) ? sb.ToString() : "";
    }

    public bool SawClose(int handle)
    {
        lock (closed) return closed.Contains(handle);
    }

    public IReadOnlyList<(int Listener, int Child, string Remote, string Local)> Accepted
    {
        get { lock (accepted) return [.. accepted]; }
    }

    public ValueTask DisposeAsync() => rhp.DisposeAsync();
}

/// <summary>
/// The echo application on a peer node: accepts connects to its call, says
/// who called ("APP G9PRA-3 sees G9APP-3"), and echoes each line back as
/// "echo G9PRA-3: line". On BPQ it is an AGW client; on XRouter and pdn an
/// RHPv2 listener.
/// </summary>
public sealed class EchoApp : IAsyncDisposable
{
    private readonly RigNode node;
    private readonly Action<string> note;
    private AgwRig? agw;
    private RhpRig? rhp;
    private readonly CancellationTokenSource stop = new();
    private readonly List<string> callers = [];

    public string Call => node.AppCall!;
    private IReadOnlyList<string> Calls => [.. node.AppCalls];

    private EchoApp(RigNode node, Action<string> note)
    {
        this.node = node;
        this.note = note;
    }

    /// <summary>Every connect, as "caller>called", in order.</summary>
    public IReadOnlyList<string> Callers
    {
        get { lock (callers) return [.. callers]; }
    }

    public static async Task<EchoApp> StartAsync(RigNode node, string host, Action<string> note, CancellationToken ct)
    {
        var app = new EchoApp(node, note);
        if (node.Kind == RigKind.Bpq)
        {
            app.agw = await AgwRig.OpenAsync($"{node.Role} echo app", host, node.AgwPort, note, ct);
            foreach (var call in app.Calls) await app.agw.RegisterAsync(call, ct);
            _ = app.AgwLoopAsync();
        }
        else
        {
            app.rhp = await RhpRig.OpenAsync($"{node.Role} echo app", host, node.RhpPort, note, ct);
            foreach (var call in app.Calls)
            {
                var (_, err) = await app.rhp.ListenAsync(call, null, ct);
                if (err is not null) throw new InvalidOperationException($"{node}: echo app can't listen as {call}: {err}");
            }
            _ = app.RhpLoopAsync();
        }
        return app;
    }

    private async Task AgwLoopAsync()
    {
        var seen = 0;
        var buffers = new Dictionary<(byte, string), string>();
        while (!stop.IsCancellationRequested)
        {
            var events = agw!.Events;
            for (; seen < events.Count; seen++)
            {
                var e = events[seen];
                if (e.Kind == 'C' && Calls.Contains(e.To))
                {
                    lock (callers) callers.Add($"{e.From}>{e.To}");
                    note($"{node.Role} echo app: connect from {e.From} to {e.To}");
                    await agw.SendAsync(e.Port, e.To, e.From, $"APP {e.To} sees {e.From}\r", stop.Token);
                }
                else if (e.Kind == 'D' && Calls.Contains(e.To))
                {
                    var key = (e.Port, e.From);
                    var text = buffers.GetValueOrDefault(key, "") + e.Text;
                    var cut = text.LastIndexOf('\r');
                    if (cut >= 0)
                    {
                        foreach (var line in text[..cut].Split('\r'))
                            await agw.SendAsync(e.Port, e.To, e.From, $"echo {e.To}: {line}\r", stop.Token);
                        text = text[(cut + 1)..];
                    }
                    buffers[key] = text;
                }
            }
            await Task.Delay(50);
        }
    }

    private async Task RhpLoopAsync()
    {
        var seenAccepts = 0;
        var consumed = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        while (!stop.IsCancellationRequested)
        {
            var acc = rhp!.Accepted;
            for (; seenAccepts < acc.Count; seenAccepts++)
            {
                var a = acc[seenAccepts];
                var local = a.Local.Length > 0 ? a.Local : Call;
                lock (callers) callers.Add($"{a.Remote}>{local}");
                note($"{node.Role} echo app: connect from {a.Remote} to {local}");
                consumed[a.Child] = 0;
                names[a.Child] = local;
                await rhp.SendAsync(a.Child, $"APP {local} sees {a.Remote}\r", stop.Token);
            }
            foreach (var child in consumed.Keys.ToList())
            {
                var all = rhp.Received(child);
                var cut = all.LastIndexOf('\r');
                if (cut >= consumed[child])
                {
                    foreach (var line in all[consumed[child]..cut].Split('\r'))
                        await rhp.SendAsync(child, $"echo {names[child]}: {line}\r", stop.Token);
                    consumed[child] = cut + 1;
                }
            }
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (agw is not null) await agw.DisposeAsync();
        if (rhp is not null) await rhp.DisposeAsync();
    }
}

/// <summary>
/// Frames each node heard: BPQ and XRouter by AGW monitor ('m'), pdn from
/// its frame feed. NODES broadcasts and ID beacons are left out.
/// </summary>
public sealed class RigAir : IAsyncDisposable
{
    private readonly List<string> frames = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<IAsyncDisposable> taps = [];
    private readonly CancellationTokenSource stop = new();

    public static async Task<RigAir> StartAsync(Rig425Mesh mesh, CancellationToken ct)
    {
        var air = new RigAir();
        foreach (var node in mesh.Nodes)
        {
            try
            {
                if (node.Kind == RigKind.Pdn)
                {
                    var mon = await AirMonitor.StartAsync([AirMonitor.Tap.PdnNode(node.Role, mesh.Host, node.HttpPort)], ct);
                    air.taps.Add(mon);
                    _ = air.CopyPdnAsync(node.Role, mon);
                }
                else
                {
                    var agw = await AgwRig.OpenAsync($"{node.Role} monitor", mesh.Host, node.AgwPort, _ => { }, ct);
                    await agw.MonitorAsync(ct);
                    air.taps.Add(agw);
                    _ = air.CopyAgwAsync(node.Role, agw);
                }
            }
            catch (Exception e)
            {
                air.Add(node.Role, $"(no monitor: {e.Message})");
            }
        }
        return air;
    }

    private async Task CopyAgwAsync(string role, AgwRig agw)
    {
        var seen = 0;
        string? last = null;
        while (!stop.IsCancellationRequested)
        {
            var ev = agw.Events;
            for (; seen < ev.Count; seen++)
            {
                if (ev[seen].Kind is not ('I' or 'S' or 'U' or 'T')) continue;
                var text = ev[seen].Text.Replace("\r", " ").Replace("\n", " ").Replace("\0", "").Trim();
                if (text == last) continue;
                last = text;
                Add(role, text);
            }
            await Task.Delay(100);
        }
    }

    private async Task CopyPdnAsync(string role, AirMonitor mon)
    {
        var seen = 0;
        while (!stop.IsCancellationRequested)
        {
            var heard = mon.HeardBy(role);
            for (; seen < heard.Count; seen++) Add(role, heard[seen].Replace("\n", " "));
            await Task.Delay(100);
        }
    }

    private void Add(string role, string text)
    {
        if (text.Contains("To NODES", StringComparison.Ordinal) || text.Contains(" To ID ", StringComparison.Ordinal)) return;
        if (text.Length > 200) text = text[..200] + "...";
        lock (frames) frames.Add($"{clock.Elapsed.TotalSeconds,7:F2}  {role} heard: {text}");
    }

    public string Transcript()
    {
        lock (frames) return string.Join('\n', frames);
    }

    public bool Any(Func<string, bool> f)
    {
        lock (frames) return frames.Any(f);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var t in taps) await t.DisposeAsync();
    }
}
