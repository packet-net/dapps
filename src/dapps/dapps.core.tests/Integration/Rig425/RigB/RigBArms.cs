using System.Text;

namespace dapps.core.tests.Integration.Rig425.RigB;

/// <summary>
/// One way of running MB7NPW's DAPPS for Rig B: which rig profile it needs,
/// how each peer is configured as a neighbour, any daemon settings, and
/// optionally another dapps.core build (a prototype branch) to run instead
/// of this tree's. Adding an arm is one entry in <see cref="All"/>.
/// </summary>
/// <param name="Id">Short name, used in DAPPS_RIG_425_B_ARMS and report files.</param>
/// <param name="Prototype">True when the arm is not today's DAPPS as an operator could configure it, but a stand-in for a design option.</param>
/// <param name="DaemonDll">A dapps.core.dll to run for MB7NPW instead of this tree's build, or null.</param>
internal sealed record RigBArm(
    string Id,
    string Title,
    RigBProfile Profile,
    Func<RigBNetwork, RigBNode, DappsDaemon.Neighbour> NeighbourFor,
    string HowItDials,
    bool Prototype = false,
    IReadOnlyDictionary<string, string>? Settings = null,
    Func<string?>? DaemonDll = null)
{
    /// <summary>Kevin's configuration today: dial CALNE, then <c>C 3 &lt;peer&gt;</c> at its prompt.</summary>
    private static DappsDaemon.Neighbour ViaCalne(RigBNetwork rig, RigBNode peer, int? holdSeconds = null) =>
        new(peer.DappsCall!, RigBNetwork.RfAgwPort,
            SessionTailSeconds: holdSeconds,
            Script: new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep($"C {RigBNetwork.CalneAxipPort} {peer.DappsCall}", "DAPPSv1>", 60)]),
            ConnectVia: rig.Calne.Call);

    /// <summary>Dial the peer's DAPPS call itself; LinBPQ routes it by its node table.</summary>
    private static DappsDaemon.Neighbour Direct(RigBNode peer) => new(peer.DappsCall!, RigBNetwork.RfAgwPort);

    public static readonly IReadOnlyList<RigBArm> All =
    [
        new("1", "Today: L2 to CALNE, connect script, 120 s hold", RigBProfile.L2,
            (rig, peer) => ViaCalne(rig, peer),
            "AGW connect to CALNE (plain L2, no NET/ROM on air), then `C 3 <peer>` at CALNE's prompt; default 120 s session hold."),
        new("2", "Short hold: as today with a 10 s hold", RigBProfile.L2,
            (rig, peer) => ViaCalne(rig, peer, holdSeconds: 10),
            "As arm 1, with each neighbour's session hold set to 10 s."),
        new("3", "L4 to CALNE, connect script, 120 s hold", RigBProfile.NetRom,
            (rig, peer) => ViaCalne(rig, peer),
            "NET/ROM on the radio link, so LinBPQ turns the AGW connect to CALNE into a NET/ROM circuit; then `C 3 <peer>` at CALNE. DAPPS's #419 busy guard (dial callsign and port) still serialises the three, and the one AGW key (MB7NPW-3, CALNE, port) would mix two at once anyway."),
        new("4", "Prototype of option 1: a NET/ROM circuit straight to each peer", RigBProfile.NetRomApps,
            (_, peer) => Direct(peer),
            "PROTOTYPE, test configuration only: each peer's LinBPQ advertises its DAPPS call on NET/ROM, and MB7NPW dials the peer's call with no connect script. LinBPQ makes each a NET/ROM circuit through CALNE, and DAPPS sees three different links, so the busy guard does not serialise them. Stands in for option 1 (sessions multiplexed as NET/ROM circuits) without code changes; 120 s hold.",
            Prototype: true),
        // A build from another branch, to try a design without touching this
        // tree: DAPPS_RIG_425_B_PROTO_DLL points at its dapps.core.dll,
        // DAPPS_RIG_425_B_PROTO_PROFILE picks the rig (l2, netrom,
        // netrom-apps), DAPPS_RIG_425_B_PROTO_ROUTE is "script" (via CALNE,
        // as arm 1) or "direct" (as arm 4), DAPPS_RIG_425_B_PROTO_SETTINGS
        // adds daemon settings as KEY=VALUE;KEY=VALUE.
        new("P", "Prototype build (DAPPS_RIG_425_B_PROTO_DLL)",
            RigBProfile.Parse(Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_PROTO_PROFILE") is { Length: > 0 } p ? p : "netrom"),
            (rig, peer) => Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_PROTO_ROUTE") == "direct" ? Direct(peer) : ViaCalne(rig, peer),
            "A dapps.core build from elsewhere, see DAPPS_RIG_425_B_PROTO_*.",
            Prototype: true,
            Settings: ProtoSettings(),
            DaemonDll: () => Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_PROTO_DLL")),
    ];

    private static Dictionary<string, string> ProtoSettings()
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in (Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_PROTO_SETTINGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim();
        }
        return d;
    }

    /// <summary>The arms DAPPS_RIG_425_B_ARMS names (comma separated), default 1 to 4.</summary>
    public static IReadOnlyList<RigBArm> Selected()
    {
        var ids = (Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_ARMS") is { Length: > 0 } s ? s : "1,2,3,4")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. ids.Select(id => All.SingleOrDefault(a => a.Id == id) ?? throw new ArgumentException($"DAPPS_RIG_425_B_ARMS: no arm {id}"))];
    }
}

/// <summary>One message the workload sends: to which peer, when (from the start), how big.</summary>
internal sealed record RigBMessage(string Tag, RigBNode Peer, TimeSpan At, byte[] Payload);

/// <summary>
/// What MB7NPW is asked to send. Each peer gets the same mix: four small
/// messages and two of about 2 KB, as text that compresses about as mail
/// does. <see cref="Burst"/> queues all of it at once; <see cref="Spread"/>
/// hands each message in at a random moment over a window.
/// </summary>
internal sealed record RigBWorkload(string Id, string Title, Func<RigBNetwork, int, IReadOnlyList<RigBMessage>> Build, TimeSpan Deadline)
{
    public const string App = "rigb";

    public static readonly int[] Sizes = PerPeerSizes();

    private static int[] PerPeerSizes()
    {
        var n = int.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_PER_PEER"), out var v) && v > 0 ? v : 6;
        int[] pattern = [180, 2000, 350, 250, 2000, 120];
        return [.. Enumerable.Range(0, n).Select(i => pattern[i % pattern.Length])];
    }

    public static TimeSpan SpreadWindow =>
        TimeSpan.FromMinutes(double.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_SPREAD_MINUTES"), System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 8);

    public static readonly RigBWorkload Burst = new("burst", "All queued at once",
        (rig, seed) => Messages(rig, seed, _ => TimeSpan.Zero), TimeSpan.FromMinutes(25));

    public static readonly RigBWorkload Spread = new("spread", "Arriving spread over time",
        (rig, seed) =>
        {
            var rng = new Random(seed ^ 0x5eed);
            return Messages(rig, seed, _ => TimeSpan.FromSeconds(rng.NextDouble() * SpreadWindow.TotalSeconds));
        }, SpreadWindow + TimeSpan.FromMinutes(20));

    public static IReadOnlyList<RigBWorkload> Selected()
    {
        var ids = (Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_WORKLOADS") is { Length: > 0 } s ? s : "burst,spread").Split(',', StringSplitOptions.TrimEntries);
        return [.. ids.Select(id => new[] { Burst, Spread }.Single(w => w.Id == id))];
    }

    /// <summary>Messages interleaved peer by peer, as mail for three places would queue.</summary>
    private static List<RigBMessage> Messages(RigBNetwork rig, int seed, Func<int, TimeSpan> at)
    {
        var rng = new Random(seed);
        var list = new List<RigBMessage>();
        var k = 0;
        for (var i = 0; i < Sizes.Length; i++)
        {
            foreach (var peer in rig.Peers)
            {
                var tag = $"rigb-{seed:x8}-{peer.Alias}-{i}";
                list.Add(new RigBMessage(tag, peer, at(k++), Text(rng, tag, peer, Sizes[i])));
            }
        }
        return [.. list.OrderBy(m => m.At)];
    }

    private static readonly string[] Words =
    [
        "the", "net", "packet", "node", "station", "signal", "report", "weather", "tonight", "repeater", "contest", "bulletin",
        "meeting", "club", "antenna", "mast", "feeder", "power", "battery", "solar", "frequency", "band", "condition", "good",
        "poor", "fading", "noise", "QSB", "QRM", "via", "link", "route", "mail", "forward", "check", "please", "thanks",
        "regards", "and", "for", "with", "from", "to", "at", "on", "in", "is", "was", "will", "be", "has", "not",
        "today", "tomorrow", "weekend", "Saturday", "Sunday", "morning", "evening", "hours", "minutes", "update", "test",
        "Calne", "Swindon", "Bath", "Bristol", "Chippenham", "Devizes", "Marlborough", "Wiltshire", "Somerset", "Cork",
        "firmware", "config", "port", "baud", "AFSK", "radio", "rig", "cable", "coax", "connector", "SWR", "watts",
    ];

    private static byte[] Text(Random rng, string tag, RigBNode peer, int size)
    {
        var sb = new StringBuilder($"[{tag}] To {peer.DappsCall}: ");
        while (sb.Length < size)
        {
            sb.Append(Words[rng.Next(Words.Length)]);
            sb.Append(rng.Next(12) switch { 0 => ". ", 1 => $" {rng.Next(1, 999)} ", 2 => ", ", _ => " " });
            if (rng.Next(40) == 0) sb.Append($"\n{(char)('A' + rng.Next(26))}{rng.Next(10)}{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))} ");
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }
}
