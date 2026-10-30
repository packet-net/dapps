using System.Text;

namespace dapps.core.tests.Integration.Rig425;

/// <summary>
/// Rig A for packet-net/dapps#425: how real nodes behave when one
/// application wants sessions to several peers behind one node at once.
/// Raw AGW and RHP clients only, no DAPPS daemon, so what is seen is the
/// node's own behaviour.
///
/// Each test drives every API LOCAL offers (its "rows"), writes what each
/// side saw to scenario-reports/rig425-*.md, and asserts the observations
/// recorded in <see cref="Rig425Expected"/>, so a node release that behaves
/// differently fails here. Only runs with DAPPS_RIG_425 set.
///
/// Callsigns: LOCAL G9LOC, CALNE G9CAL, PEER1 G9PRA, PEER2 G9PRB. The
/// application on LOCAL is G9APP-3 (and -4, -5 for the multi-call
/// questions). CALNE and the peers run an echo app as their -3 call, not on
/// NET/ROM, as a DAPPS daemon is today; a LinBPQ peer's app also answers its
/// -5 call, which BPQ advertises on NET/ROM.
/// </summary>
public abstract class Rig425Experiments(Rig425Mesh mesh) : IAsyncLifetime
{
    private readonly RigLog log = new();
    private RigAir? air;
    private readonly List<EchoApp> apps = [];
    private readonly List<IAsyncDisposable> clients = [];
    private readonly List<(string Row, string Observation)> observed = [];
    private readonly StringBuilder notes = new();

    private RigNode Local => mesh.Local;
    private RigNode Calne => mesh.Calne;
    private RigNode Peer1 => mesh.Peer1;
    private RigNode Peer2 => mesh.Peer2;
    private const string App3 = Rig425Mesh.App3;
    private const string App4 = Rig425Mesh.App4;
    private const string App5 = Rig425Mesh.App5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!Rig425Mesh.Wanted) return;
        air = await RigAir.StartAsync(mesh, Ct);
        foreach (var n in new[] { Calne, Peer1, Peer2 }) apps.Add(await EchoApp.StartAsync(n, mesh.Host, log.Note, Ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (!Rig425Mesh.Wanted) return;
        foreach (var c in clients) await c.DisposeAsync();
        foreach (var a in apps) await a.DisposeAsync();
        if (air is not null) await air.DisposeAsync();
        // Let links and circuits finish closing before the next test.
        await Task.Delay(8000);
    }

    private void Skip() => Assert.SkipUnless(Rig425Mesh.Wanted, "Set DAPPS_RIG_425 to run the #425 rig experiments.");

    // ---------------------------------------------------------------- Q1

    [Fact]
    public async Task Q1_SecondL2ConnectForAPairAlreadyUp()
    {
        // App G9APP-3 holds an L2 session to CALNE's app G9CAL-3 (not on
        // NET/ROM, so it can only be L2), then opens a second one for the
        // same pair on the same port.
        Skip();
        var target = Calne.AppCall!;
        foreach (var row in L2Rows())
        {
            await SecondSessionAsync(row, () => L2Session(row, App3, target, "S1"), () => L2Session(row, App3, target, "S2"), target);
        }
        foreach (var row in NodeRows())
        {
            // From LOCAL's own prompt: "C <port> G9CAL-3" twice, from two node sessions.
            var cmd = $"C {mesh.PortTo(Local, Calne)} {target}";
            await SecondSessionAsync(row + $", typing '{cmd}'",
                () => NodeSessionTyping(row, App3, cmd, 0, "S1"), () => NodeSessionTyping(row, App3, cmd, 1, "S2"), target);
        }
        Finish("q1-l2");
    }

    [Fact]
    public async Task Q1_SecondConnectToTheCalneNodeItself()
    {
        // As DAPPS through a connect script does: G9APP-3 connects to the
        // node CALNE (G9CAL, which is on NET/ROM), twice.
        Skip();
        foreach (var row in L2Rows().Concat(NodeRows()))
        {
            await SecondSessionAsync(row, () => ToNode(row, App3, Calne, 0, "S1"), () => ToNode(row, App3, Calne, 1, "S2"), Calne.Call);
        }
        Finish("q1-node");
    }

    /// <summary>
    /// Open S1, check it carries data, open S2 for the same pair, then see
    /// what each still does. Records one categorical observation.
    /// </summary>
    private async Task SecondSessionAsync(string row, Func<Task<RigSession>> first, Func<Task<RigSession>> second, string target)
    {
        Note($"=== {row}");
        var s1 = await first();
        if (!s1.Connected)
        {
            Observe(row, $"first session failed: {s1.Error}");
            return;
        }
        // An app echoes the line; a node answers a command it doesn't know.
        var isApp = target.Contains('-');
        async Task<(bool OnSelf, bool OnOther)> Try(RigSession self, RigSession other, string line)
        {
            var (a, b) = (self.Text.Length, other.Text.Length);
            await self.TypeAsync(line, Ct);
            return isApp
                ? (self.Echoed(line), !ReferenceEquals(self, other) && other.Echoed(line))
                : (self.Text.Length > a, !ReferenceEquals(self, other) && other.Text.Length > b);
        }
        var (firstWorks, _) = await Try(s1, s1, "firstprobe");
        var s2 = await second();
        await Task.Delay(4000, Ct);
        var s2State = s2.Error is not null ? $"refused ({Short(s2.Error)})" : s2.Closed ? "closed" : s2.Connected ? "accepted" : "no answer";
        string whereS2;
        if (SameKey(s1, s2)) whereS2 = "S2 has S1's AGW key (port, from, to), so the node can't tell their data apart";
        else if (s2.Connected && s2.Error is null && !s2.Closed)
        {
            var (self, other) = await Try(s2, s1, "twoprobe");
            whereS2 = self ? "S2 carries data" : other ? "S2's data answered on S1" : "S2's data not answered";
        }
        else whereS2 = "-";
        var (after, _) = await Try(s1, s1, "afterprobe");
        var s1After = s1.Closed ? "S1 closed" : after ? "S1 still works" : "S1 silent";
        Observe(row, $"first works: {Yes(firstWorks)}; second: {s2State}; {whereS2}; {s1After}");
        await CloseAsync(s1, s2);
    }

    // ---------------------------------------------------------------- Q2

    [Fact]
    public async Task Q2_TwoPeersAtOnce_OverNetRom()
    {
        // G9APP-3 opens two sessions at once, one to each peer's app behind
        // CALNE, each through NET/ROM, and sends lines on both turn about.
        Skip();
        foreach (var row in NodeRows())
        {
            // a. NET/ROM to each peer node, then connect to its app there.
            if (CanReachAppFromPrompt(Peer1) && CanReachAppFromPrompt(Peer2))
            {
                await TwoAtOnceAsync($"{row}: NET/ROM to each peer node, then 'C <app>' there",
                    i => ToNodeThen(row, App3, i == 0 ? Peer1 : Peer2, i, $"C {(i == 0 ? Peer1 : Peer2).AppCall}"));
            }
            else Observe($"{row}: NET/ROM to each peer node, then 'C <app>' there", "n/a: an XRouter peer's prompt can't reach an app registered over RHP or AGW");

            // b. NET/ROM to CALNE twice, then an L2 downlink from CALNE to each peer's app.
            await TwoAtOnceAsync($"{row}: NET/ROM to CALNE twice, then L2 downlink from CALNE",
                i => ToNodeThen(row, App3, Calne, i, $"C {mesh.PortTo(Calne, i == 0 ? Peer1 : Peer2)} {(i == 0 ? Peer1 : Peer2).AppCall}"));

            // c. A circuit straight to an app BPQ advertises on NET/ROM.
            if (Peer1.NetRomAppCall is not null && Peer2.NetRomAppCall is not null)
            {
                await TwoAtOnceAsync($"{row}: NET/ROM circuit straight to each peer's advertised app",
                    i => StraightTo(row, App3, (i == 0 ? Peer1 : Peer2).NetRomAppCall!, i));
            }
        }
        if (Local.Kind == RigKind.Bpq && Peer1.NetRomAppCall is not null && Peer2.NetRomAppCall is not null)
        {
            // BPQ's AGW 'C' to a callsign in its NET/ROM table makes a circuit, not an L2 link.
            var agw = await LocalAgw("AGW");
            await agw.RegisterAsync(App3, Ct);
            await TwoAtOnceAsync("BPQ AGW 'C' to each peer's advertised app",
                async i =>
                {
                    var s = new AgwSession(agw, mesh.AgwPortTo(Local, Calne), App3, (i == 0 ? Peer1 : Peer2).NetRomAppCall!) { Label = $"AGW C {(i == 0 ? Peer1 : Peer2).NetRomAppCall}" };
                    await s.OpenAsync(Ct);
                    await Task.Delay(2000, Ct);
                    return s;
                });
        }
        Finish("q2-netrom");
    }

    [Fact]
    public async Task Q2_TwoPeersAtOnce_L2ConnectScript()
    {
        // As a connect script does it: G9APP-3 connects at L2 to CALNE and
        // types "C <port> <peer app>"; a second session does the same for
        // the other peer while the first is up.
        Skip();
        foreach (var row in L2Rows())
        {
            await TwoAtOnceAsync($"{row}: to CALNE, then L2 downlink from CALNE",
                async i =>
                {
                    var peer = i == 0 ? Peer1 : Peer2;
                    var s = await ToNode(row, App3, Calne, i, $"S{i + 1}");
                    if (s.Connected && s.Error is null) await s.TypeAsync($"C {mesh.PortTo(Calne, peer)} {peer.AppCall}", Ct, 4000);
                    return s;
                });
        }
        Finish("q2-script");
    }

    /// <summary>Open two sessions (i = 0 to PEER1, 1 to PEER2), then send three lines on each, alternately.</summary>
    private async Task TwoAtOnceAsync(string row, Func<int, Task<RigSession>> open)
    {
        Note($"=== {row}");
        var a = await open(0);
        var b = await open(1);
        for (var n = 0; n < 3; n++)
        {
            if (a.Connected) await a.SendAsync($"one-{n}\r", Ct);
            if (b.Connected) await b.SendAsync($"two-{n}\r", Ct);
            await Task.Delay(200, Ct);
        }
        await Task.Delay(5000, Ct);
        string Of(RigSession s, string tag, RigNode peer)
        {
            if (!s.Connected || s.Error is not null) return $"{tag}: {(s.Error is not null ? $"refused ({Short(s.Error)})" : "not up")}";
            var reached = s.Sightings().FirstOrDefault(x => x.EndsWith(">" + peer.AppCall, StringComparison.Ordinal) || x.EndsWith(">" + peer.NetRomAppCall, StringComparison.Ordinal));
            var echoes = Enumerable.Range(0, 3).Count(n => s.Echoed($"{tag}-{n}"));
            return $"{tag}: {(reached is not null ? $"reached {peer.Role}" : $"did not reach {peer.Role}")}, {echoes}/3 echoed";
        }
        var both = a.Connected && b.Connected && !a.Closed && !b.Closed;
        Observe(row, $"{Of(a, "one", Peer1)}; {Of(b, "two", Peer2)}; both up together: {Yes(both)}");
        await CloseAsync(a, b);
    }

    // ---------------------------------------------------------------- Q3

    [Fact]
    public async Task Q3_SeveralCallsOnOneClientConnection()
    {
        // One client connection to LOCAL carrying G9APP-3, -4 and -5 at
        // once: registered (AGW 'X') or bound (RHP), dialling out from each,
        // and taking a call to each from CALNE.
        Skip();
        var target = Calne.AppCall!;
        switch (Local.Kind)
        {
            case RigKind.Bpq:
            case RigKind.Xrouter:
            {
                var row = Local.Kind == RigKind.Bpq ? "BPQ AGW, one socket" : "XRouter AGW, one socket";
                var agw = await LocalAgw("AGW");
                var replies = new List<string>();
                foreach (var call in new[] { App3, App4, App5 }) replies.Add($"{call} {await agw.RegisterAsync(call, Ct):X2}");
                Observe($"{row}: 'X' replies", string.Join(", ", replies));
                var outs = new List<AgwSession>();
                foreach (var from in new[] { App4, App5 })
                {
                    var s = new AgwSession(agw, mesh.AgwPortTo(Local, Calne), from, target) { Label = $"C {from}->{target}" };
                    await s.OpenAsync(Ct);
                    outs.Add(s);
                }
                await Task.Delay(3000, Ct);
                Observe($"{row}: dial out from -4 and -5 at once", string.Join("; ", outs.Select(s => $"from {s.From}: {(s.Connected ? $"far app sees {Seen(s)}" : $"failed ({s.Error})")}")));
                foreach (var s in outs) await s.CloseAsync(Ct);
                await Task.Delay(4000, Ct);
                foreach (var call in new[] { App3, App4, App5 })
                {
                    var before = agw.Events.Count;
                    var result = await CallFromCalneAsync(call);
                    var got = agw.Events.Skip(before).FirstOrDefault(e => e.Kind == 'C' && e.To == call);
                    Observe($"{row}: CALNE calls {call}", $"{result}; {(got is not null ? $"delivered on this socket as 'C' {got.From}->{got.To}" : "nothing on this socket")}");
                }
                break;
            }
            case RigKind.Pdn:
                await RhpSeveralCallsAsync("pdn RHP, one connection", "1");
                break;
        }
        if (Local.Kind == RigKind.Xrouter) await RhpSeveralCallsAsync("XRouter RHP, one connection", mesh.PortTo(Local, Calne).ToString());
        if (Local.Kind == RigKind.Bpq)
        {
            var rhp = await LocalBpqRhp("BPQ RHP");
            var outs = new List<RigSession>();
            foreach (var from in new[] { App3, App4 })
            {
                var s = new BpqRhpSession(rhp, from) { Label = $"RHP open as {from}" };
                await s.OpenAsync(Ct);
                await s.TypeAsync($"C {mesh.PortTo(Local, Calne)} {target}", Ct, 4000);
                outs.Add(s);
            }
            Observe("BPQ RHP, one websocket: opens as -3 and -4, each 'C 2 G9CAL-3'", string.Join("; ", outs.Select(s => $"{s.Label}: far app sees {Seen(s)}")));
            await CloseAsync([.. outs]);
            // BPQ's RHP has no socket/bind/listen: an unknown type gets no reply at all.
            var count = rhp.Messages.Count;
            await rhp.SendRawForTestAsync("{\"type\":\"socket\",\"id\":99,\"pfam\":\"ax25\",\"mode\":\"stream\"}", Ct);
            await Task.Delay(3000, Ct);
            Observe("BPQ RHP: a listener (socket/bind/listen)", rhp.Messages.Count > count ? "socket answered" : "no reply to 'socket': no listeners over RHP, inbound calls can't arrive this way");
        }
        Finish("q3");
    }

    private async Task RhpSeveralCallsAsync(string row, string port)
    {
        var rhp = await LocalRhp("RHP");
        var binds = new List<string>();
        foreach (var call in new[] { App3, App4 })
        {
            var (_, err) = await rhp.ListenAsync(call, null, Ct);
            binds.Add($"{call} {(err is null ? "ok" : err)}");
        }
        Observe($"{row}: listen as -3 and -4", string.Join(", ", binds));
        var outs = new List<RigSession>();
        foreach (var from in new[] { App3, App4 })
        {
            var s = new RhpSession(rhp, "ax25", port, from, Calne.AppCall!) { Label = $"open {from}->{Calne.AppCall}" };
            await s.OpenAsync(Ct);
            outs.Add(s);
        }
        await Task.Delay(3000, Ct);
        Observe($"{row}: open from -3 and -4 at once", string.Join("; ", outs.Select(s => $"{s.Label}: {(s.Connected ? $"far app sees {Seen(s)}" : $"failed ({Short(s.Error)})")}")));
        await CloseAsync([.. outs]);
        await Task.Delay(3000, Ct);
        foreach (var call in new[] { App3, App4 })
        {
            var before = rhp.Accepted.Count;
            var result = await CallFromCalneAsync(call);
            var got = rhp.Accepted.Skip(before).FirstOrDefault();
            Observe($"{row}: CALNE calls {call}", $"{result}; {(got != default ? $"accepted on the {got.Local} listener, remote {got.Remote}" : "no accept")}");
        }
    }

    /// <summary>CALNE's own client (G9CAL-7) makes an L2 call to <paramref name="to"/> on LOCAL and says hello.</summary>
    private async Task<string> CallFromCalneAsync(string to)
    {
        const string from = "G9CAL-7";
        RigSession s;
        if (Calne.Kind == RigKind.Pdn)
        {
            var rhp = await RhpRig.OpenAsync("CALNE caller", mesh.Host, Calne.RhpPort, log.Note, Ct);
            clients.Add(rhp);
            s = new RhpSession(rhp, "ax25", mesh.PortTo(Calne, Local).ToString(), from, to) { Label = $"CALNE {from}->{to}" };
        }
        else
        {
            var agw = await AgwRig.OpenAsync("CALNE caller", mesh.Host, Calne.AgwPort, log.Note, Ct);
            clients.Add(agw);
            await agw.RegisterAsync(from, Ct);
            s = new AgwSession(agw, mesh.AgwPortTo(Calne, Local), from, to) { Label = $"CALNE {from}->{to}" };
        }
        await s.OpenAsync(Ct);
        var result = s.Connected && s.Error is null ? "connected" : $"not connected ({Short(s.Error)})";
        if (s.Connected) await s.TypeAsync("hello", Ct, 2000);
        await s.CloseAsync(Ct);
        await Task.Delay(3000, Ct);
        return result;
    }

    // ---------------------------------------------------------------- Q4

    [Fact]
    public async Task Q4_WhichCallArrivesAtTheFarApp()
    {
        // For G9APP-3 and G9APP-4, each way of reaching a peer's app: what
        // callsign does the app see?
        Skip();
        foreach (var app in new[] { App3, App4 })
        {
            foreach (var peer in new[] { Peer1, Peer2 })
            {
                var down = $"C {mesh.PortTo(Calne, peer)} {peer.AppCall}";
                foreach (var row in L2Rows())
                {
                    await SeenAsync($"{row} to CALNE, then L2 downlink, {app} -> {peer.Role}",
                        async () => { var s = await ToNode(row, app, Calne, 0, "S"); if (s.Connected && s.Error is null) await s.TypeAsync(down, Ct, 4000); return s; });
                }
                foreach (var row in NodeRows())
                {
                    await SeenAsync($"{row}: NET/ROM to CALNE, then L2 downlink, {app} -> {peer.Role}",
                        () => ToNodeThen(row, app, Calne, 0, down));
                    if (CanReachAppFromPrompt(peer))
                        await SeenAsync($"{row}: NET/ROM to {peer.Role} node, then 'C <app>', {app} -> {peer.Role}",
                            () => ToNodeThen(row, app, peer, 0, $"C {peer.AppCall}"));
                    if (peer.NetRomAppCall is not null)
                        await SeenAsync($"{row}: NET/ROM circuit straight to the advertised app, {app} -> {peer.Role}",
                            () => StraightTo(row, app, peer.NetRomAppCall, 0));
                }
            }
        }
        Finish("q4");
    }

    private async Task SeenAsync(string row, Func<Task<RigSession>> open)
    {
        Note($"=== {row}");
        var s = await open();
        await Task.Delay(1000, Ct);
        Observe(row, s.Error is not null ? $"failed ({Short(s.Error)})" : $"far app sees {Seen(s)}");
        await CloseAsync(s);
    }

    // ---------------------------------------------------------------- rows and sessions

    /// <summary>LOCAL's ways of making a plain connect (L2, unless the node decides otherwise).</summary>
    private IEnumerable<string> L2Rows() => Local.Kind switch
    {
        RigKind.Bpq => ["BPQ AGW 'C' (one socket)", "BPQ AGW 'C' (second socket)"],
        RigKind.Xrouter => ["XRouter AGW 'C'", "XRouter RHP ax25 open with port"],
        _ => ["pdn RHP ax25 open with port"],
    };

    /// <summary>LOCAL's ways of getting a session through its own node (prompt or NET/ROM).</summary>
    private IEnumerable<string> NodeRows() => Local.Kind switch
    {
        RigKind.Bpq => ["BPQ AGW 'C' to SWITCH", "BPQ RHP"],
        RigKind.Xrouter => ["XRouter RHP netrom open"],
        _ => ["pdn RHP open with no port"],
    };

    private readonly Dictionary<string, AgwRig> agwByRow = [];

    private async Task<RigSession> L2Session(string row, string from, string to, string label)
    {
        RigSession s;
        if (row.Contains("AGW", StringComparison.Ordinal))
        {
            var key = row.Contains("second socket", StringComparison.Ordinal) ? row + label : row;
            if (!agwByRow.TryGetValue(key, out var agw))
            {
                agw = await LocalAgw(key);
                agwByRow[key] = agw;
            }
            await agw.RegisterAsync(from, Ct);
            s = new AgwSession(agw, mesh.AgwPortTo(Local, Calne), from, to) { Label = $"{label} {row} {from}->{to}" };
        }
        else
        {
            var rhp = await LocalRhp(row);
            s = new RhpSession(rhp, "ax25", mesh.PortTo(Local, Calne).ToString(), from, to) { Label = $"{label} {row} {from}->{to}" };
        }
        await s.OpenAsync(Ct);
        if (s.Connected) await Task.Delay(2000, Ct);
        return s;
    }

    /// <summary>A session that reaches <paramref name="node"/>'s prompt: L2 rows connect to its call, node rows go by NET/ROM.</summary>
    private Task<RigSession> ToNode(string row, string from, RigNode node, int index, string label) =>
        NodeRows().Contains(row) ? ToNodeThen(row, from, node, index, null) : L2Session(row, from, node.Call, label);

    /// <summary>A session through LOCAL's node to <paramref name="node"/>, then <paramref name="then"/> typed there.</summary>
    private async Task<RigSession> ToNodeThen(string row, string from, RigNode node, int index, string? then)
    {
        RigSession s;
        if (row.StartsWith("BPQ", StringComparison.Ordinal))
        {
            s = await NodeSessionTyping(row, from, $"C {node.Alias}", index, $"S{index + 1}");
        }
        else
        {
            var rhp = await LocalRhp(row);
            s = Local.Kind == RigKind.Xrouter
                ? new RhpSession(rhp, "netrom", null, from, node.Call) { Label = $"S{index + 1} netrom {from}->{node.Call}" }
                : new RhpSession(rhp, "ax25", null, from, node.Call) { Label = $"S{index + 1} portless {from}->{node.Call}" };
            await s.OpenAsync(Ct);
            await Task.Delay(1500, Ct);
        }
        if (then is not null && s.Connected && s.Error is null) await s.TypeAsync(then, Ct, 4000);
        return s;
    }

    /// <summary>A BPQ node session (AGW SWITCH key <paramref name="index"/>, or RHP) with <paramref name="command"/> typed.</summary>
    private async Task<RigSession> NodeSessionTyping(string row, string from, string command, int index, string label)
    {
        RigSession s;
        if (row.StartsWith("BPQ AGW", StringComparison.Ordinal))
        {
            if (!agwByRow.TryGetValue(row, out var agw))
            {
                agw = await LocalAgw(row);
                agwByRow[row] = agw;
            }
            await agw.RegisterAsync(from, Ct);
            // Each node session needs its own AGW key: SWITCH, SWITCH-1, ... (BPQ only checks the first six letters).
            s = new AgwSession(agw, 0, from, index == 0 ? "SWITCH" : $"SWITCH-{index}") { Label = $"{label} {from} SWITCH{(index == 0 ? "" : "-" + index)}" };
        }
        else
        {
            var rhp = await LocalBpqRhp(row);
            s = new BpqRhpSession(rhp, from) { Label = $"{label} RHP as {from}" };
        }
        await s.OpenAsync(Ct);
        await Task.Delay(500, Ct);
        if (s.Connected) await s.TypeAsync(command, Ct, 3000);
        return s;
    }

    /// <summary>A NET/ROM circuit that ends at an app's own callsign.</summary>
    private Task<RigSession> StraightTo(string row, string from, string appCall, int index) => row.StartsWith("BPQ", StringComparison.Ordinal)
        ? NodeSessionTyping(row, from, $"C {appCall}", index, $"S{index + 1}")
        : OpenRhpStraight(row, from, appCall, index);

    private async Task<RigSession> OpenRhpStraight(string row, string from, string appCall, int index)
    {
        var rhp = await LocalRhp(row);
        var s = new RhpSession(rhp, Local.Kind == RigKind.Xrouter ? "netrom" : "ax25", null, from, appCall) { Label = $"S{index + 1} {from}->{appCall}" };
        await s.OpenAsync(Ct);
        await Task.Delay(2000, Ct);
        return s;
    }

    private static bool CanReachAppFromPrompt(RigNode peer) => peer.Kind != RigKind.Xrouter;

    private RhpRig? sharedRhp;
    private BpqRhpRig? sharedBpqRhp;

    private async Task<RhpRig> LocalRhp(string name)
    {
        if (sharedRhp is null)
        {
            sharedRhp = await RhpRig.OpenAsync("LOCAL RHP", mesh.Host, Local.RhpPort, log.Note, Ct);
            clients.Add(sharedRhp);
        }
        return sharedRhp;
    }

    private async Task<BpqRhpRig> LocalBpqRhp(string name)
    {
        if (sharedBpqRhp is null)
        {
            sharedBpqRhp = await BpqRhpRig.OpenAsync("LOCAL RHP", mesh.Host, Local.HttpPort, log.Note, Ct);
            clients.Add(sharedBpqRhp);
        }
        return sharedBpqRhp;
    }

    private async Task<AgwRig> LocalAgw(string name)
    {
        var agw = await AgwRig.OpenAsync($"LOCAL {name}", mesh.Host, Local.AgwPort, log.Note, Ct);
        clients.Add(agw);
        return agw;
    }

    // ---------------------------------------------------------------- helpers

    private static bool SameKey(RigSession a, RigSession b) =>
        a is AgwSession x && b is AgwSession y && x.Port == y.Port && x.From == y.From && x.To == y.To;

    private static string Seen(RigSession s)
    {
        var seen = s.Sightings();
        return seen.Count == 0 ? "nothing (no greeting)" : string.Join(" and ", seen.Select(x => x.Split('>')[0]).Distinct());
    }

    private static string Yes(bool b) => b ? "yes" : "no";

    private static string Short(string? e) => e is null ? "" : e.Length > 70 ? e[..70] : e;

    private async Task CloseAsync(params RigSession[] sessions)
    {
        foreach (var s in sessions)
        {
            try { if (s.Connected && !s.Closed) await s.CloseAsync(Ct); }
            catch (Exception e) { Note($"closing {s.Label}: {e.Message}"); }
        }
        await Task.Delay(5000, Ct);
    }

    private void Note(string text) => log.Note(text);

    private void Observe(string row, string observation)
    {
        observed.Add((row, observation));
        Note($">>> {row}: {observation}");
    }

    /// <summary>Write the report, then check every observation against <see cref="Rig425Expected"/>.</summary>
    private void Finish(string test)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rig A (#425), {mesh.Name}: {test}");
        sb.AppendLine();
        sb.AppendLine("| Row | Observed |");
        sb.AppendLine("|---|---|");
        foreach (var (row, obs) in observed) sb.AppendLine($"| {row} | {obs} |");
        sb.AppendLine();
        sb.AppendLine("What the clients and echo apps saw, in order:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(log.Text());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("What each node heard (NODES broadcasts left out):");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(air?.Transcript());
        sb.AppendLine("```");
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var tag = mesh.GetType().Name.Replace("Rig425", "").ToLowerInvariant();
        File.WriteAllText(Path.Combine(dir, $"rig425-{tag}-{test}.md"), sb.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(sb.ToString());

        var mismatches = new List<string>();
        foreach (var (row, obs) in observed)
        {
            if (!Rig425Expected.Table.TryGetValue((tag, test, row), out var want))
                mismatches.Add($"            [(\"{tag}\", \"{test}\", \"{row}\")] = \"{obs}\",");
            else if (want != obs)
                mismatches.Add($"CHANGED {tag}/{test}/{row}:\n  was:  {want}\n  now:  {obs}");
        }
        Assert.True(mismatches.Count == 0, "Not what Rig425Expected records:\n" + string.Join('\n', mismatches));
    }
}

[Collection("Rig425 all BPQ")]
[Trait("Category", "Integration")]
public sealed class Rig425ExperimentsAllBpq(Rig425AllBpq mesh) : Rig425Experiments(mesh);

[Collection("Rig425 all XRouter")]
[Trait("Category", "Integration")]
public sealed class Rig425ExperimentsAllXrouter(Rig425AllXrouter mesh) : Rig425Experiments(mesh);

[Collection("Rig425 all pdn")]
[Trait("Category", "Integration")]
public sealed class Rig425ExperimentsAllPdn(Rig425AllPdn mesh) : Rig425Experiments(mesh);

[Collection("Rig425 mixed")]
[Trait("Category", "Integration")]
public sealed class Rig425ExperimentsMixed(Rig425Mixed mesh) : Rig425Experiments(mesh);
