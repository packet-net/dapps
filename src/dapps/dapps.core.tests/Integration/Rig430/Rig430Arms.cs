using System.Text;
using dapps.client;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>
/// One way of running MB7NPW's DAPPS on the replica: how each peer is set
/// up as a neighbour, and optionally another dapps.core build to run for
/// MB7NPW. Adding an arm is one entry in <see cref="All"/>.
/// </summary>
/// <param name="Columns">Extra neighbour columns to seed for a build whose
/// database has them (a dial-as callsign, say), keyed by column name.</param>
/// <param name="DaemonDll">A dapps.core.dll for MB7NPW instead of this tree's (0.57.0) build, or null.</param>
/// <param name="PeerHold">The session hold the peers offer (their system-wide setting and their row for
/// MB7NPW), or null for their default 120 s. A session is held for the lower of the two ends' values.</param>
internal sealed record Rig430Arm(
    string Id,
    string Title,
    string How,
    Func<Rig430Network, Rig430Node, DappsDaemon.Neighbour> NeighbourFor,
    Func<Rig430Network, Rig430Node, IReadOnlyDictionary<string, object>>? Columns = null,
    Func<string?>? DaemonDll = null,
    int? PeerHold = null)
{
    /// <summary>Long hold: the most a neighbour's session hold can be set to (600 s).</summary>
    public const int LongHold = 600;

    /// <summary>
    /// Kevin's set-up: Connect via CALNE (MB7NPW's LinBPQ makes that a
    /// NET/ROM circuit over NPR, as its 0.56.1 logs show), then a script at
    /// CALNE's prompt to the peer. The real scripts weren't surveyed; these
    /// are the shortest that work on this network:
    /// GB7AGM: NET/ROM to GB7BDH, then an L2 downlink over its AXUDP port
    /// (an XRouter node can't hand a call from its own prompt to an app on
    /// RHPv2, #425 Rig A); GB7LOX: NET/ROM all the way to its LinBPQ node,
    /// then the DAPPS application command; EI7KX: NET/ROM to G8PZT, then an
    /// L2 downlink over AXUDP.
    /// </summary>
    public static DappsDaemon.Neighbour ViaCalne(Rig430Network rig, Rig430Node peer, int? hold = null) =>
        new(peer.DappsCall!, Rig430Network.NpwNprPort - 1,
            SessionTailSeconds: hold,
            Script: ScriptAtCalne(rig, peer),
            ConnectVia: rig.Cal.Call);

    public static ConnectScript ScriptAtCalne(Rig430Network rig, Rig430Node peer)
    {
        if (peer == rig.Agm)
            return new([Step($"C {rig.Bdh.Call}", ConnectedTo(rig.Bdh), 120), Step($"C {Rig430Network.BdhAgmPort} {peer.DappsCall}", "DAPPSv1>", 90)]);
        if (peer == rig.Lox)
            return new([Step($"C {rig.Lox.Call}", ConnectedTo(rig.Lox), 180), Step("DAPPS", "DAPPSv1>", 60)]);
        if (peer == rig.Eik)
            return new([Step($"C {rig.Pzt.Call}", ConnectedTo(rig.Pzt), 120), Step($"C {Rig430Network.PztEikPort} {peer.DappsCall}", "DAPPSv1>", 90)]);
        throw new ArgumentException($"no script for {peer}");
    }

    /// <summary>
    /// What XRouter says when a connect from its prompt is up: "Connected to
    /// G9BDH", by call even when it knows the alias. Not the bare call
    /// ("Trying G9BDH..." comes first), and not "Connected to" alone: MB7NPW's
    /// LinBPQ says "Connected to CALNE:G9CAL" itself when the first hop is up.
    /// </summary>
    public static string ConnectedTo(Rig430Node n) => $"Connected to {n.Call}";

    private static ConnectScriptStep Step(string send, string expect, int timeout) => new(send, expect, timeout);

    /// <summary>
    /// How each peer reaches MB7NPW to send a reply when no session is open
    /// (a reply otherwise rides MB7NPW's open session): back the way MB7NPW
    /// came, ending with an L2 downlink from CALNE over NPR.
    /// </summary>
    public static DappsDaemon.Neighbour BackToNpw(Rig430Network rig, Rig430Node peer, int? hold = null)
    {
        var last = Step($"C {Rig430Network.CalNprPort} {rig.Npw.DappsCall}", "DAPPSv1>", 90);
        var bearer = Rig430Network.PeerBearerPort(peer);
        if (peer == rig.Agm)
            return new(rig.Npw.DappsCall!, bearer, SessionTailSeconds: hold, Script: new([Step($"C {rig.Cal.Call}", ConnectedTo(rig.Cal), 120), last]), ConnectVia: rig.Bdh.Call);
        if (peer == rig.Lox)
            return new(rig.Npw.DappsCall!, bearer, SessionTailSeconds: hold, Script: new([last]), ConnectVia: rig.Cal.Call);
        if (peer == rig.Eik)
            return new(rig.Npw.DappsCall!, bearer, SessionTailSeconds: hold, Script: new([Step($"C {rig.Cal.Call}", ConnectedTo(rig.Cal), 120), last]), ConnectVia: rig.Pzt.Call);
        throw new ArgumentException($"no route back from {peer}");
    }

    /// <summary>MB7NPW's dial-as callsign for a peer (#431): its own SSID per neighbour.</summary>
    public static string DialAs(Rig430Network rig, Rig430Node peer) => $"{rig.Npw.Call}-{4 + rig.Peers.ToList().IndexOf(peer)}";

    private static string? DialAsDll() => Environment.GetEnvironmentVariable("DAPPS_RIG_430_DIALAS_DLL");

    /// <summary>The #431 build's neighbour column for the dial-as callsign.</summary>
    public static string DialAsColumn => Environment.GetEnvironmentVariable("DAPPS_RIG_430_DIALAS_COLUMN") is { Length: > 0 } c ? c : "DialAs";

    public static readonly IReadOnlyList<Rig430Arm> All =
    [
        new("A", "0.57.0 as is", "MB7NPW on 0.57.0 (this tree), each peer Connect via CALNE with its script, default 120 s hold; a quiet session gives way after 10 s when mail for another peer waits.",
            (rig, peer) => ViaCalne(rig, peer)),
        new("L", "0.57.0, long holds at MB7NPW only", $"As A with MB7NPW's hold for each peer at {LongHold} s, the most the dashboard allows. The peers keep their default 120 s, and a session is held for the lower of the two, so this is A again.",
            (rig, peer) => ViaCalne(rig, peer, LongHold)),
        new("LL", "0.57.0, long holds at both ends", $"As A with a {LongHold} s hold everywhere: MB7NPW's rows and default, the peers' default and their row for MB7NPW.",
            (rig, peer) => ViaCalne(rig, peer, LongHold), PeerHold: LongHold),
        // #431 (dial a neighbour from its own source SSID): the build at
        // DAPPS_RIG_430_DIALAS_DLL, each peer dialled as G9NPW-4, -5, -6.
        new("S", "#431 dial-as SSIDs, long holds at MB7NPW only", $"MB7NPW on the #431 build (DAPPS_RIG_430_DIALAS_DLL): each peer dialled from its own SSID (G9NPW-4, -5, -6) on its own AGW connection, {LongHold} s holds at MB7NPW; the peers keep 120 s, which wins.",
            (rig, peer) => ViaCalne(rig, peer, LongHold), Columns: (rig, peer) => new Dictionary<string, object> { [DialAsColumn] = DialAs(rig, peer) }, DaemonDll: DialAsDll),
        new("SL", "#431 dial-as SSIDs, long holds at both ends", $"As S with a {LongHold} s hold everywhere, as LL.",
            (rig, peer) => ViaCalne(rig, peer, LongHold), Columns: (rig, peer) => new Dictionary<string, object> { [DialAsColumn] = DialAs(rig, peer) }, DaemonDll: DialAsDll, PeerHold: LongHold),
    ];

    /// <summary>The arms DAPPS_RIG_430_ARMS names (comma separated), default A,LL.</summary>
    public static IReadOnlyList<Rig430Arm> Selected()
    {
        var ids = (Environment.GetEnvironmentVariable("DAPPS_RIG_430_ARMS") is { Length: > 0 } s ? s : "A,LL")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. ids.Select(id => All.SingleOrDefault(a => a.Id == id) ?? throw new ArgumentException($"DAPPS_RIG_430_ARMS: no arm {id}"))];
    }
}

/// <summary>One message: from MB7NPW to a peer, or a peer's reply (<see cref="Reply"/> names the message it answers).</summary>
internal sealed record Rig430Message(string Tag, Rig430Node Peer, bool Back, TimeSpan At, byte[] Payload, string? Reply = null);

/// <summary>
/// What MB7NPW is asked to send. <see cref="Im"/>: instant messaging, each
/// peer gets short messages at random (exponential gaps, mean
/// DAPPS_RIG_430_IM_GAP_SECONDS, default 60 s, over
/// DAPPS_RIG_430_IM_MINUTES, default 10) and answers each one after 5 to
/// 30 s of "typing". <see cref="Burst"/>: three messages for each peer
/// (200, 600 and 1500 bytes) handed in at once, no replies.
/// </summary>
internal sealed record Rig430Workload(string Id, string Title, bool Replies, Func<Rig430Network, int, IReadOnlyList<Rig430Message>> Build, TimeSpan Deadline)
{
    public const string App = "rig430";

    public static TimeSpan ImWindow => TimeSpan.FromMinutes(Num("DAPPS_RIG_430_IM_MINUTES", 10));
    public static double ImGapSeconds => Num("DAPPS_RIG_430_IM_GAP_SECONDS", 60);

    private static double Num(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    public static readonly Rig430Workload Im = new("im", "Instant messages at random, each answered", true, (rig, seed) =>
    {
        var rng = new Random(seed);
        var list = new List<Rig430Message>();
        foreach (var peer in rig.Peers)
        {
            var t = 0.0;
            var i = 0;
            while (true)
            {
                t += -Math.Log(1 - rng.NextDouble()) * ImGapSeconds;
                if (t > ImWindow.TotalSeconds) break;
                var tag = $"r430-{seed:x8}-{peer.Role}-{i++}";
                list.Add(new Rig430Message(tag, peer, false, TimeSpan.FromSeconds(t), Text(rng, tag, peer.DappsCall!, rng.Next(40, 200))));
            }
        }
        return [.. list.OrderBy(m => m.At)];
    }, ImWindow + TimeSpan.FromMinutes(20));

    public static readonly Rig430Workload Burst = new("burst", "Three messages for each peer at once", false, (rig, seed) =>
    {
        var rng = new Random(seed);
        var list = new List<Rig430Message>();
        int[] sizes = [200, 600, 1500];
        for (var i = 0; i < sizes.Length; i++)
            foreach (var peer in rig.Peers)
            {
                var tag = $"r430-{seed:x8}-{peer.Role}-b{i}";
                list.Add(new Rig430Message(tag, peer, false, TimeSpan.Zero, Text(rng, tag, peer.DappsCall!, sizes[i])));
            }
        return list;
    }, TimeSpan.FromMinutes(20));

    /// <summary>A peer's answer to <paramref name="original"/>, 5 to 30 s after it arrived.</summary>
    public static Rig430Message ReplyTo(Rig430Message original, Rig430Network rig, Random rng, TimeSpan arrived) =>
        new(original.Tag + "-re", original.Peer, true, arrived + TimeSpan.FromSeconds(5 + 25 * rng.NextDouble()),
            Text(rng, original.Tag + "-re", rig.Npw.DappsCall!, rng.Next(40, 160)), original.Tag);

    public static IReadOnlyList<Rig430Workload> Selected()
    {
        var ids = (Environment.GetEnvironmentVariable("DAPPS_RIG_430_WORKLOADS") is { Length: > 0 } s ? s : "im,burst").Split(',', StringSplitOptions.TrimEntries);
        return [.. ids.Select(id => new[] { Im, Burst }.Single(w => w.Id == id))];
    }

    private static readonly string[] Words =
    [
        "hello", "are", "you", "on", "tonight", "the", "net", "link", "is", "up", "again", "thanks", "for", "the", "mail",
        "see", "you", "at", "club", "weather", "here", "is", "wet", "any", "news", "from", "Swindon", "Calne", "Cork",
        "test", "message", "ok", "good", "copy", "73", "will", "check", "later", "node", "rebooted", "antenna", "fixed",
    ];

    public static byte[] Text(Random rng, string tag, string to, int size)
    {
        var sb = new StringBuilder($"[{tag}] To {to}: ");
        while (sb.Length < size) sb.Append(Words[rng.Next(Words.Length)]).Append(rng.Next(10) == 0 ? ". " : " ");
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }
}
