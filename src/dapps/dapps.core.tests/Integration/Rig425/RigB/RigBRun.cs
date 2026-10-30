using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace dapps.core.tests.Integration.Rig425.RigB;

/// <summary>What one run of one arm measured. Times are seconds from the first message handed to MB7NPW.</summary>
internal sealed record RigBResult
{
    public required string Arm { get; init; }
    public required string Workload { get; init; }
    public required int Rep { get; init; }
    public required int Seed { get; init; }
    public required string Host { get; init; }
    public required DateTime StartedUtc { get; init; }
    public int Messages { get; init; }
    public int Delivered { get; init; }
    public int Lost { get; init; }
    public int Duplicates { get; init; }
    public double? LastDelivery { get; init; }
    public Dictionary<string, double?> LastDeliveryByPeer { get; init; } = [];
    public double LastSubmitted { get; init; }
    public double? LatencyMedian { get; init; }
    public double? LatencyMax { get; init; }
    /// <summary>Airtime from the first message to the last delivery (sum of transmissions).</summary>
    public double AirtimeToLast { get; init; }
    /// <summary>Union of transmissions over that stretch, as a share of it.</summary>
    public double BusyPercent { get; init; }
    /// <summary>Airtime from the first message until the channel went quiet (sessions held and closed).</summary>
    public double AirtimeTotal { get; init; }
    public double QuietAt { get; init; }
    public int Transmissions { get; init; }
    /// <summary>Transmissions that started while the other station's was on air.</summary>
    public int Collisions { get; init; }
    public int FramesSentA { get; init; }
    public int FramesSentB { get; init; }
    public int FramesHeardFromA { get; init; }
    public int FramesHeardFromB { get; init; }
    public int FramesLostOnAir => Math.Max(0, FramesSentA - FramesHeardFromA) + Math.Max(0, FramesSentB - FramesHeardFromB);
    public int IFrames { get; init; }
    public int Retransmissions { get; init; }
    public int Rejs { get; init; }
    public int Polls { get; init; }
    public int RetryOuts { get; init; }
    public int L2TimeoutsA { get; init; }
    public int L2TimeoutsB { get; init; }
    public int L4ResentA { get; init; }
    public int L4ResentB { get; init; }
    /// <summary>SABMs heard on air, either way.</summary>
    public int L2Connects { get; init; }
    /// <summary>NET/ROM connect requests heard on air.</summary>
    public int NetRomConnects { get; init; }
    /// <summary>DAPPS sessions the peers took (their "Inbound session from" lines).</summary>
    public int DappsSessions { get; init; }
    /// <summary>DAPPS sessions MB7NPW took from the peers (the both workload).</summary>
    public int NpwInboundSessions { get; init; }
    /// <summary>MB7NPW's sessions that ended early for other mail (#425 give-way: "before its ... hold").</summary>
    public int GiveWays { get; init; }
    /// <summary>MB7NPW's deferrals because a dial would have reset a session already open.</summary>
    public int BusyDeferrals { get; init; }
    /// <summary>Messages the peers sent MB7NPW (the both workload).</summary>
    public int BackMessages { get; init; }
    /// <summary>How many peers had mail (DAPPS_RIG_425_B_MAIL_PEERS), of how many in the rig.</summary>
    public int MailPeers { get; init; }
    public int RigPeers { get; init; }
    public int NodesFrames { get; init; }
    /// <summary>SREJs heard on air (LinBPQ's node-to-node links negotiate AX.25 2.2).</summary>
    public int? Srejs { get; init; }
    /// <summary>DISCs between the two node calls: the node-to-node link (carrying NET/ROM) reset.</summary>
    public int? NodeLinkResets { get; init; }
    /// <summary>The LinBPQ image when not the test pin, plus " v2.0" with OnlyVer2point0; null for the stock pin on its defaults.</summary>
    public string? Bpq { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// Runs one arm on one rig, one repetition of one workload: starts fresh
/// DAPPS daemons (new databases), hands MB7NPW the messages, watches the
/// peers' inboxes, waits for the channel to go quiet, and measures.
/// </summary>
internal sealed class RigBRun(RigBNetwork rig, RigBArm arm, RigBWorkload workload, int rep, int seed, Action<string> note)
{
    private readonly List<DappsDaemon> daemons = [];
    private readonly Dictionary<string, TimeSpan> deliveredAt = [];
    private readonly Dictionary<string, TimeSpan> submittedAt = [];
    private readonly Dictionary<string, RigBNode> peerOf = [];
    private readonly HashSet<string> seenInboxIds = [];
    private int duplicates;

    public string Report { get; private set; } = "";

    public async Task<RigBResult> RunAsync(CancellationToken ct)
    {
        var messages = workload.Build(rig, seed);
        foreach (var m in messages) peerOf[m.Tag] = m.Peer;
        DappsDaemon? npw = null;
        var peers = new Dictionary<RigBNode, DappsDaemon>();
        RigBAir? air = null;
        ChannelLog? channel = null;
        string? failure = null;
        var clock = new Stopwatch();
        var t0 = DateTime.UtcNow;
        var quietAt = TimeSpan.Zero;
        (int A, int B) sentBefore = (0, 0), sentAfter = (0, 0);
        string statsBeforeA = "", statsBeforeB = "", statsAfterA = "", statsAfterB = "";
        DateTime snapFrom = DateTime.UtcNow, snapTo = DateTime.UtcNow;
        try
        {
            var sendsBack = messages.Any(m => m.Back);
            foreach (var p in rig.Peers)
            {
                // When the peers send too, MB7NPW is their neighbour: through
                // CALNE (`C 2 <MB7NPW>` at its prompt), or direct over AXUDP.
                DappsDaemon.Neighbour[] back = !sendsBack ? []
                    : p.Direct ? [new(rig.Mb7npw.DappsCall!, RigBNetwork.PeerAgwPort)]
                    : [new(rig.Mb7npw.DappsCall!, RigBNetwork.PeerAgwPort,
                        Script: new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep($"C {RigBNetwork.CalneRadioPort} {rig.Mb7npw.DappsCall}", "DAPPSv1>", 60)]),
                        ConnectVia: rig.Calne.Call)];
                var d = await DappsDaemon.StartAsync($"rigb-{p.Alias}", p.DappsCall!,
                    NodeAttachment.Agw(rig.Host, rig.AgwPort(p), RigBNetwork.PeerAgwPort), back, settings: null, ct);
                daemons.Add(d);
                peers[p] = d;
            }
            npw = await DappsDaemon.StartAsync("rigb-NPW", rig.Mb7npw.DappsCall!,
                NodeAttachment.Agw(rig.Host, rig.AgwPort(rig.Mb7npw), RigBNetwork.RfAgwPort),
                rig.Peers.Select(p => arm.NeighbourFor(rig, p)), arm.Settings, ct,
                daemonDll: arm.DaemonDll?.Invoke());
            daemons.Add(npw);
            note($"arm {arm.Id} {workload.Id} rep {rep}: daemons up");

            air = await RigBAir.StartAsync(rig, ct);
            channel = await ChannelLog.StartAsync(rig.Host, rig.NetSimWebPort, ct);
            statsBeforeA = await rig.NodeCommandAsync(rig.Mb7npw, "STATS", ct);
            statsBeforeB = await rig.NodeCommandAsync(rig.Calne, "STATS", ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            snapFrom = DateTime.UtcNow;
            sentBefore = await rig.FramesSentAsync(ct);

            using var stopPump = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t0 = DateTime.UtcNow;
            clock.Start();
            var pumps = peers.Select(kv => PumpAsync(kv.Value, clock, stopPump.Token)).ToList();
            if (sendsBack) pumps.Add(PumpAsync(npw, clock, stopPump.Token));
            var submitting = SubmitAsync(npw, peers, messages, clock, ct);

            while (clock.Elapsed < workload.Deadline && deliveredAt.Count < messages.Count && rig.Died is null && !pumps.Any(p => p.IsFaulted))
            {
                await Task.Delay(250, ct);
            }
            await submitting;
            note($"arm {arm.Id} {workload.Id} rep {rep}: {deliveredAt.Count}/{messages.Count} delivered at {clock.Elapsed.TotalSeconds:F0} s");

            // Keep reading inboxes (a late duplicate counts) until MB7NPW
            // has nothing left and the channel has been quiet for 15 s:
            // held sessions run out and close in that time.
            var settleUntil = clock.Elapsed + TimeSpan.FromMinutes(5);
            while (clock.Elapsed < settleUntil && rig.Died is null)
            {
                await Task.Delay(1000, ct);
                var lastFrame = air.Between(t0, DateTime.UtcNow).Select(f => f.At).DefaultIfEmpty(t0).Max();
                if (DateTime.UtcNow - lastFrame > TimeSpan.FromSeconds(15) && await npw.PendingOutboundAsync(ct) == 0
                    && (!sendsBack || (await Task.WhenAll(peers.Values.Select(p => p.PendingOutboundAsync(ct)))).All(n => n == 0))) break;
            }
            quietAt = clock.Elapsed;
            await stopPump.CancelAsync();
            foreach (var p in pumps)
            {
                try { await p; }
                catch (OperationCanceledException) { }
                catch (Exception e) { failure ??= $"inbox pump: {e.Message}"; }
            }
            snapTo = DateTime.UtcNow;
            sentAfter = await rig.FramesSentAsync(ct);
            statsAfterA = await rig.NodeCommandAsync(rig.Mb7npw, "STATS", ct);
            statsAfterB = await rig.NodeCommandAsync(rig.Calne, "STATS", ct);
            if (rig.Died is { } died) failure ??= died;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failure = e.ToString();
            snapTo = DateTime.UtcNow;
        }

        var logs = daemons.ToDictionary(d => d.Name, d => d.Log);
        try
        {
            logs["LinBPQ MB7NPW debug"] = await rig.DebugLogAsync(rig.Mb7npw, snapFrom, CancellationToken.None);
            logs["LinBPQ CALNE debug"] = await rig.DebugLogAsync(rig.Calne, snapFrom, CancellationToken.None);
        }
        catch (Exception e) { logs["LinBPQ debug"] = e.Message; }
        foreach (var d in daemons)
        {
            try { await d.DisposeAsync(); } catch (Exception) { /* best effort */ }
        }
        // Let the links the daemons held close before the next run.
        await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);

        var lastDelivery = deliveredAt.Count > 0 ? deliveredAt.Values.Max() : (TimeSpan?)null;
        var until = t0 + (lastDelivery ?? quietAt);
        var toLast = channel?.Summarise(t0, until);
        var total = channel?.Summarise(t0, t0 + quietAt);
        var window = air?.Between(snapFrom, snapTo) ?? [];
        var heardA = RigBAir.SentBy(window, "A").ToList();
        var heardB = RigBAir.SentBy(window, "B").ToList();
        var heard = window.Where(f => !f.Sent).OrderBy(f => f.At).ToList();
        var latencies = deliveredAt.Select(kv => (kv.Value - submittedAt.GetValueOrDefault(kv.Key)).TotalSeconds).Order().ToList();

        var result = new RigBResult
        {
            Arm = arm.Id,
            Workload = workload.Id,
            Rep = rep,
            Seed = seed,
            Host = Environment.MachineName,
            StartedUtc = t0,
            Messages = messages.Count,
            Delivered = deliveredAt.Count,
            Lost = messages.Count - deliveredAt.Count,
            Duplicates = duplicates,
            LastDelivery = lastDelivery?.TotalSeconds,
            LastDeliveryByPeer = rig.Peers.ToDictionary(p => p.Role,
                p => deliveredAt.Where(kv => peerOf[kv.Key] == p).Select(kv => (double?)kv.Value.TotalSeconds).DefaultIfEmpty(null).Max()),
            LastSubmitted = messages.Count > 0 ? messages.Max(m => m.At).TotalSeconds : 0,
            LatencyMedian = latencies.Count > 0 ? latencies[latencies.Count / 2] : null,
            LatencyMax = latencies.Count > 0 ? latencies[^1] : null,
            AirtimeToLast = toLast?.Airtime.TotalSeconds ?? 0,
            BusyPercent = toLast?.BusyPercent ?? 0,
            AirtimeTotal = total?.Airtime.TotalSeconds ?? 0,
            QuietAt = quietAt.TotalSeconds,
            Transmissions = total?.Count ?? 0,
            Collisions = total?.Doubles ?? 0,
            FramesSentA = sentAfter.A - sentBefore.A,
            FramesSentB = sentAfter.B - sentBefore.B,
            FramesHeardFromA = heardA.Count,
            FramesHeardFromB = heardB.Count,
            IFrames = heard.Count(f => f.Type == "I"),
            Retransmissions = RigBAir.Retransmissions(heard),
            Rejs = heard.Count(f => f.Type == "REJ"),
            Polls = heard.Count(f => f.Poll && f.Type is "RR" or "RNR" or "REJ"),
            RetryOuts = RigBAir.RetryOuts(heard, rig.RadioSettings.Retries),
            L2TimeoutsA = StatDiff(statsBeforeA, statsAfterA, "L2 Timeouts"),
            L2TimeoutsB = StatDiff(statsBeforeB, statsAfterB, "L2 Timeouts"),
            L4ResentA = L4Resent(statsAfterA) - L4Resent(statsBeforeA),
            L4ResentB = L4Resent(statsAfterB) - L4Resent(statsBeforeB),
            L2Connects = heard.Count(f => f.Type == "C"),
            NetRomConnects = heard.Count(f => f.Text.Contains("C REQ", StringComparison.Ordinal) || f.Text.Contains("CON REQ", StringComparison.Ordinal) || f.Text.Contains("<CONN REQ", StringComparison.Ordinal)),
            DappsSessions = logs.Where(kv => kv.Key != "rigb-NPW").Sum(kv => Regex.Matches(kv.Value, "Inbound session from").Count),
            NpwInboundSessions = Regex.Matches(logs.GetValueOrDefault("rigb-NPW") ?? "", "Inbound session from").Count,
            GiveWays = Regex.Matches(logs.GetValueOrDefault("rigb-NPW") ?? "", @"of quiet, before its \d+s hold").Count,
            BusyDeferrals = Regex.Matches(logs.GetValueOrDefault("rigb-NPW") ?? "", "dialling now would reset it").Count,
            BackMessages = messages.Count(m => m.Back),
            MailPeers = messages.Select(m => m.Peer).Distinct().Count(),
            RigPeers = rig.Peers.Count,
            NodesFrames = heard.Count(f => f.To == "NODES"),
            Srejs = heard.Count(f => f.Type == "SREJ"),
            NodeLinkResets = heard.Count(f => f.Type == "D"
                && ((f.From == rig.Mb7npw.Call && f.To == rig.Calne.Call) || (f.From == rig.Calne.Call && f.To == rig.Mb7npw.Call))),
            Bpq = RigBNetwork.BpqLabel,
            Failure = failure,
        };

        Report = BuildReport(result, channel?.Timeline(t0, t0 + quietAt) ?? "", RigBAir.Transcript(window, t0), logs, statsAfterA, statsAfterB);
        if (air is not null) await air.DisposeAsync();
        if (channel is not null) await channel.DisposeAsync();
        return result;
    }

    private async Task SubmitAsync(DappsDaemon npw, Dictionary<RigBNode, DappsDaemon> peers, IReadOnlyList<RigBMessage> messages, Stopwatch clock, CancellationToken ct)
    {
        foreach (var m in messages)
        {
            var wait = m.At - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (submittedAt) submittedAt[m.Tag] = clock.Elapsed;
            if (m.Back) await peers[m.Peer].SubmitAsync(RigBWorkload.App, rig.Mb7npw.DappsCall!, m.Payload, ct, ttl: 3600);
            else await npw.SubmitAsync(RigBWorkload.App, m.Peer.DappsCall!, m.Payload, ct, ttl: 3600);
        }
    }

    /// <summary>Read a peer's inbox as an app would, acking each message; note when each tag first arrives.</summary>
    private async Task PumpAsync(DappsDaemon peer, Stopwatch clock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var m in await peer.InboundAsync(RigBWorkload.App, ct))
            {
                var text = Encoding.ASCII.GetString(m.Payload);
                var tag = Regex.Match(text, @"^\[(rigb-[^\]]+)\]").Groups[1].Value;
                lock (deliveredAt)
                {
                    if (!seenInboxIds.Add(peer.Name + "|" + m.Id) || deliveredAt.ContainsKey(tag)) duplicates++;
                    else deliveredAt[tag] = clock.Elapsed;
                }
                (await peer.Http.PostAsync($"AppApi/inbound/{RigBWorkload.App}/{m.Id}/ack", null, ct)).EnsureSuccessStatusCode();
            }
            await Task.Delay(300, ct);
        }
    }

    /// <summary>A port counter from LinBPQ's STATS, radio port (the second column), after less before.</summary>
    private static int StatDiff(string before, string after, string label) => PortStat(after, label) - PortStat(before, label);

    private static int PortStat(string stats, string label)
    {
        foreach (var line in stats.Split('\r', '\n'))
        {
            if (!line.StartsWith(label, StringComparison.Ordinal)) continue;
            var numbers = Regex.Matches(line[label.Length..], @"-?\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            return numbers.Count > 1 ? numbers[1] : 0;
        }
        return 0;
    }

    private static int L4Resent(string stats)
    {
        var m = Regex.Match(stats, @"L4 Frames TX/RX/Resent/Reseq\s+(\d+)\s+(\d+)\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
    }

    private string BuildReport(RigBResult r, string timeline, string transcript, Dictionary<string, string> logs, string statsA, string statsB)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rig B: arm {arm.Id}, {workload.Id}, rep {rep}");
        sb.AppendLine();
        sb.AppendLine($"{arm.Title}. {arm.HowItDials}");
        sb.AppendLine();
        sb.AppendLine($"Radio port: BPQ {rig.RadioSettings}, AFSK 1200 on Dire Wolf, {rig.PathLoss} dB path loss. Seed {seed}. Host {Environment.MachineName}. LinBPQ {RigBNetwork.BpqImage}{(RigBNetwork.OnlyV20 ? ", OnlyVer2point0=1" : "")}.");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Deliveries (s from first submit)");
        sb.AppendLine("```");
        foreach (var kv in deliveredAt.OrderBy(kv => kv.Value)) sb.AppendLine($"{kv.Value.TotalSeconds,8:F1}  {kv.Key}  (submitted {submittedAt.GetValueOrDefault(kv.Key).TotalSeconds:F1})");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Transmissions");
        sb.AppendLine("```");
        sb.Append(timeline);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Frames heard on the radio channel");
        sb.AppendLine("```");
        sb.AppendLine(transcript);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## LinBPQ STATS after");
        sb.AppendLine("```");
        sb.AppendLine(statsA.Replace("\r", "\n"));
        sb.AppendLine(statsB.Replace("\r", "\n"));
        sb.AppendLine("```");
        foreach (var (name, log) in logs)
        {
            sb.AppendLine();
            sb.AppendLine($"## {name} log");
            sb.AppendLine("```");
            sb.AppendLine(log);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }
}
