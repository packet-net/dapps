using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQLite;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>Airtime and frame counts on one link over a run.</summary>
internal sealed record Rig430LinkResult
{
    public double Airtime { get; init; }
    public double BusyPercent { get; init; }
    public int Transmissions { get; init; }
    public int Collisions { get; init; }
    public Dictionary<string, double> AirtimeBySource { get; init; } = [];
    public required LinkCounts Counts { get; init; }
}

/// <summary>What one run measured. Times are seconds from the first message handed in.</summary>
internal sealed record Rig430Result
{
    public required string Arm { get; init; }
    public required string Workload { get; init; }
    public required int Rep { get; init; }
    public required int Seed { get; init; }
    public required string Host { get; init; }
    public required DateTime StartedUtc { get; init; }
    public string? Bpq { get; init; }
    public int Messages { get; init; }
    public int Delivered { get; init; }
    public int Replies { get; init; }
    public int RepliesDelivered { get; init; }
    public int Lost { get; init; }
    public int Duplicates { get; init; }
    public double? LastDelivery { get; init; }
    public double? LatencyMedian { get; init; }
    public double? LatencyP90 { get; init; }
    public double? LatencyMax { get; init; }
    public double? ReplyLatencyMedian { get; init; }
    public double? ReplyLatencyP90 { get; init; }
    public double? ReplyLatencyMax { get; init; }
    /// <summary>Every one-way latency (MB7NPW to peer, then replies), for pooling across runs.</summary>
    public List<double> Latencies { get; init; } = [];
    public List<double> ReplyLatencies { get; init; } = [];
    public Dictionary<string, double?> LatencyMedianByPeer { get; init; } = [];
    public Dictionary<string, double?> LastDeliveryByPeer { get; init; } = [];
    public double QuietAt { get; init; }
    public Dictionary<string, Rig430LinkResult> Links { get; init; } = [];
    /// <summary>MB7NPW's dials ("AGW: requesting"), confirmed, and failed sends.</summary>
    public int Dials { get; init; }
    public int DialsConfirmed { get; init; }
    public int SendFailures { get; init; }
    public int ScriptFailures { get; init; }
    /// <summary>DAPPS sessions the peers took from MB7NPW, and MB7NPW took from the peers.</summary>
    public int PeerInboundSessions { get; init; }
    public int NpwInboundSessions { get; init; }
    public int GiveWays { get; init; }
    public int BusyDeferrals { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// One repetition of one workload on one arm: fresh DAPPS daemons (new
/// databases), the messages handed to MB7NPW on schedule, each peer's
/// inbox read as an app would (answering each instant message after a
/// pause), then wait for the links to go quiet and measure.
/// </summary>
internal sealed class Rig430Run(Rig430Network rig, Rig430Arm arm, Rig430Workload workload, int rep, int seed, Action<string> note)
{
    private readonly List<DappsDaemon> daemons = [];
    private readonly Dictionary<string, TimeSpan> deliveredAt = [];
    private readonly Dictionary<string, TimeSpan> submittedAt = [];
    private readonly Dictionary<string, Rig430Message> byTag = [];
    private readonly HashSet<string> seenIds = [];
    private readonly List<Task> replies = [];
    private int duplicates;

    public string Report { get; private set; } = "";

    public async Task<Rig430Result> RunAsync(CancellationToken ct)
    {
        var messages = workload.Build(rig, seed);
        foreach (var m in messages) byTag[m.Tag] = m;
        var expectedReplies = workload.Replies ? messages.Count : 0;
        DappsDaemon? npw = null;
        var peers = new Dictionary<Rig430Node, DappsDaemon>();
        Rig430Air? air = null;
        string? failure = null;
        var clock = new Stopwatch();
        var t0 = DateTime.UtcNow;
        var quietAt = TimeSpan.Zero;
        var rng = new Random(seed ^ 0x7e9);
        try
        {
            foreach (var p in rig.Peers)
            {
                // A peer reached through a node sees MB7NPW under another
                // SSID, so its own row doesn't apply: the hold it offers is
                // its system-wide setting, seeded before it starts.
                var d = await DappsDaemon.StartAsync($"r430-{p.Real}", p.DappsCall!, rig.Attachment(p, Rig430Network.PeerBearerPort(p)),
                    [Rig430Arm.BackToNpw(rig, p, arm.PeerHold)], settings: null, ct,
                    seed: arm.PeerHold is { } hold ? db =>
                    {
                        db.CreateTable<dapps.core.Models.DbSystemOption>();
                        db.InsertOrReplace(new dapps.core.Models.DbSystemOption { Option = "SessionTailSeconds", Value = hold.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    } : null);
                daemons.Add(d);
                peers[p] = d;
            }
            npw = await DappsDaemon.StartAsync("r430-MB7NPW", rig.Npw.DappsCall!, rig.Attachment(rig.Npw, Rig430Network.NpwNprPort - 1),
                rig.Peers.Select(p => arm.NeighbourFor(rig, p)), settings: null, ct,
                seed: arm.Columns is null ? null : db => SeedColumns(db, arm),
                daemonDll: arm.DaemonDll?.Invoke());
            daemons.Add(npw);
            note($"arm {arm.Id} {workload.Id} rep {rep}: daemons up");

            air = await Rig430Air.StartAsync(rig, userTraffic: true, seed, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            using var stopPump = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t0 = DateTime.UtcNow;
            clock.Start();
            var pumps = peers.Select(kv => PumpAsync(kv.Value, kv.Key, peers, clock, rng, stopPump.Token)).ToList();
            pumps.Add(PumpAsync(npw, null, peers, clock, rng, stopPump.Token));
            var submitting = SubmitAsync(npw, messages, clock, ct);

            bool Done()
            {
                lock (deliveredAt) return deliveredAt.Count >= messages.Count + expectedReplies;
            }
            while (clock.Elapsed < workload.Deadline && !Done() && rig.Died is null && !pumps.Any(p => p.IsFaulted))
            {
                await Task.Delay(500, ct);
            }
            await submitting;
            note($"arm {arm.Id} {workload.Id} rep {rep}: {deliveredAt.Count}/{messages.Count + expectedReplies} delivered at {clock.Elapsed.TotalSeconds:F0} s");

            // Let the links go quiet (nothing pending anywhere, the radio
            // channels quiet for 15 s), up to 3 minutes: a held session may
            // keep a link up for longer, and that's not counted.
            var settleUntil = clock.Elapsed + TimeSpan.FromMinutes(3);
            while (clock.Elapsed < settleUntil && rig.Died is null)
            {
                await Task.Delay(1000, ct);
                var last = air.Heard(t0, DateTime.UtcNow).Where(h => h.Frame.From != Rig430Network.UserCall).Select(h => h.At).DefaultIfEmpty(t0).Max();
                if (DateTime.UtcNow - last < TimeSpan.FromSeconds(15)) continue;
                var pending = await Task.WhenAll(daemons.Select(d => d.PendingOutboundAsync(ct)));
                if (pending.All(n => n == 0)) break;
            }
            quietAt = clock.Elapsed;
            await stopPump.CancelAsync();
            foreach (var p in pumps)
            {
                try { await p; }
                catch (OperationCanceledException) { }
                catch (Exception e) { failure ??= $"inbox pump: {e.Message}"; }
            }
            if (rig.Died is { } died) failure ??= died;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failure = e.ToString();
        }
        var t1 = DateTime.UtcNow;
        air?.CloseAll(t1);

        var logs = daemons.ToDictionary(d => d.Name, d => d.Log);
        var relayLog = await rig.RelayLogAsync();
        foreach (var d in daemons)
        {
            try { await d.DisposeAsync(); } catch (Exception) { /* best effort */ }
        }
        try { await Task.WhenAll(replies); } catch (Exception) { }

        var result = Measure(messages, expectedReplies, air, relayLog, logs, t0, t1, quietAt, failure);
        Report = BuildReport(result, air, relayLog, logs, t0, t1);
        if (air is not null) await air.DisposeAsync();
        // Let the circuits and links the daemons held close before the next run.
        await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
        return result;
    }

    /// <summary>Extra neighbour columns for a build that has them (<see cref="Rig430Arm.Columns"/>).</summary>
    private void SeedColumns(SQLiteConnection db, Rig430Arm a)
    {
        foreach (var peer in rig.Peers)
        {
            foreach (var (column, value) in a.Columns!(rig, peer))
            {
                var type = value is int or long or bool ? "INTEGER" : "TEXT";
                try { db.Execute($"ALTER TABLE neighbours ADD COLUMN \"{column}\" {type}"); }
                catch (SQLiteException) { /* already there */ }
                db.Execute($"UPDATE neighbours SET \"{column}\" = ? WHERE Callsign = ?", value, peer.DappsCall!);
            }
        }
    }

    private async Task SubmitAsync(DappsDaemon npw, IReadOnlyList<Rig430Message> messages, Stopwatch clock, CancellationToken ct)
    {
        foreach (var m in messages)
        {
            var wait = m.At - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (submittedAt) submittedAt[m.Tag] = clock.Elapsed;
            await npw.SubmitAsync(Rig430Workload.App, m.Peer.DappsCall!, m.Payload, ct, ttl: 3600);
        }
    }

    /// <summary>
    /// Read one inbox (a peer's, or MB7NPW's when <paramref name="peer"/> is
    /// null), acking each message; note when each tag first arrives, and
    /// (instant messages) have the peer answer after a pause.
    /// </summary>
    private async Task PumpAsync(DappsDaemon daemon, Rig430Node? peer, Dictionary<Rig430Node, DappsDaemon> peers, Stopwatch clock, Random rng, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var m in await daemon.InboundAsync(Rig430Workload.App, ct))
            {
                var text = Encoding.ASCII.GetString(m.Payload);
                var tag = Regex.Match(text, @"^\[(r430-[^\]]+)\]").Groups[1].Value;
                var at = clock.Elapsed;
                bool first;
                lock (deliveredAt)
                {
                    first = seenIds.Add(daemon.Name + "|" + m.Id) && !deliveredAt.ContainsKey(tag);
                    if (first) deliveredAt[tag] = at;
                    else duplicates++;
                }
                (await daemon.Http.PostAsync($"AppApi/inbound/{Rig430Workload.App}/{m.Id}/ack", null, ct)).EnsureSuccessStatusCode();
                if (first && peer is not null && workload.Replies && byTag.TryGetValue(tag, out var original))
                {
                    Rig430Message reply;
                    lock (rng) reply = Rig430Workload.ReplyTo(original, rig, rng, at);
                    lock (byTag) byTag[reply.Tag] = reply;
                    lock (replies) replies.Add(ReplyAsync(peers[peer], reply, clock, ct));
                }
            }
            await Task.Delay(300, ct);
        }
    }

    private async Task ReplyAsync(DappsDaemon peer, Rig430Message reply, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            var wait = reply.At - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (submittedAt) submittedAt[reply.Tag] = clock.Elapsed;
            await peer.SubmitAsync(Rig430Workload.App, rig.Npw.DappsCall!, reply.Payload, CancellationToken.None, ttl: 3600);
        }
        catch (OperationCanceledException) { }
    }

    private static double? Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private Rig430Result Measure(IReadOnlyList<Rig430Message> messages, int expectedReplies, Rig430Air? air, string relayLog,
        Dictionary<string, string> logs, DateTime t0, DateTime t1, TimeSpan quietAt, string? failure)
    {
        Dictionary<string, TimeSpan> delivered, submitted;
        lock (deliveredAt) delivered = new(deliveredAt);
        lock (submittedAt) submitted = new(submittedAt);
        Dictionary<string, Rig430Message> tags;
        lock (byTag) tags = new(byTag);

        double Lat(string tag) => (delivered[tag] - submitted.GetValueOrDefault(tag)).TotalSeconds;
        var outbound = messages.Where(m => delivered.ContainsKey(m.Tag)).Select(m => Lat(m.Tag)).Order().ToList();
        var back = tags.Values.Where(m => m.Back && delivered.ContainsKey(m.Tag)).Select(m => Lat(m.Tag)).Order().ToList();
        var repliesMade = tags.Values.Count(m => m.Back);

        var links = new Dictionary<string, Rig430LinkResult>();
        var retries = Rig430Network.Radio.Retries;
        if (air is not null)
        {
            foreach (var ch in rig.Channels)
            {
                var overs = air.Overs(ch.Id, t0, t1);
                var heard = air.Heard(t0, t1).Where(h => h.Channel == ch.Id).ToList();
                links[ch.Id] = new Rig430LinkResult
                {
                    Airtime = overs.Sum(o => o.Length.TotalSeconds),
                    BusyPercent = (t1 - t0).TotalSeconds > 0 ? 100 * Union(overs) / (t1 - t0).TotalSeconds : 0,
                    Transmissions = overs.Count,
                    Collisions = Collisions(overs),
                    AirtimeBySource = overs.GroupBy(o => o.Source).ToDictionary(g => g.Key, g => Math.Round(g.Sum(o => o.Length.TotalSeconds), 2)),
                    Counts = LinkCounts.From(heard, retries),
                };
            }
        }
        var npr = Rig430Air.NprFrames(relayLog, t0.AddSeconds(-1), t1.AddSeconds(1));
        links["npr"] = new Rig430LinkResult { Transmissions = npr.Count, Counts = LinkCounts.From(npr, retries) };

        var npwLog = logs.GetValueOrDefault("r430-MB7NPW") ?? "";
        var lastDelivery = delivered.Count > 0 ? delivered.Values.Max().TotalSeconds : (double?)null;
        return new Rig430Result
        {
            Arm = arm.Id,
            Workload = workload.Id,
            Rep = rep,
            Seed = seed,
            Host = Environment.MachineName,
            StartedUtc = t0,
            Bpq = Rig430Network.BpqLabel,
            Messages = messages.Count,
            Delivered = outbound.Count,
            Replies = expectedReplies,
            RepliesDelivered = back.Count,
            Lost = messages.Count - outbound.Count + (expectedReplies - back.Count),
            Duplicates = duplicates,
            LastDelivery = lastDelivery,
            LatencyMedian = Percentile(outbound, 0.5),
            LatencyP90 = Percentile(outbound, 0.9),
            LatencyMax = outbound.Count > 0 ? outbound[^1] : null,
            ReplyLatencyMedian = Percentile(back, 0.5),
            ReplyLatencyP90 = Percentile(back, 0.9),
            ReplyLatencyMax = back.Count > 0 ? back[^1] : null,
            Latencies = [.. outbound.Select(l => Math.Round(l, 2))],
            ReplyLatencies = [.. back.Select(l => Math.Round(l, 2))],
            LatencyMedianByPeer = rig.Peers.ToDictionary(p => p.Real,
                p => Percentile([.. messages.Where(m => m.Peer == p && delivered.ContainsKey(m.Tag)).Select(m => Lat(m.Tag)).Order()], 0.5)),
            LastDeliveryByPeer = rig.Peers.ToDictionary(p => p.Real,
                p => messages.Where(m => m.Peer == p && delivered.ContainsKey(m.Tag)).Select(m => (double?)delivered[m.Tag].TotalSeconds).DefaultIfEmpty(null).Max()),
            QuietAt = quietAt.TotalSeconds,
            Links = links,
            Dials = Regex.Matches(npwLog, "AGW: requesting").Count,
            DialsConfirmed = Regex.Matches(npwLog, "AGW: connect confirmed").Count,
            SendFailures = Regex.Matches(npwLog, "Failed to forward message").Count,
            ScriptFailures = Regex.Matches(npwLog, "connect-script step").Count,
            PeerInboundSessions = logs.Where(kv => kv.Key != "r430-MB7NPW").Sum(kv => Regex.Matches(kv.Value, "Inbound session from").Count),
            NpwInboundSessions = Regex.Matches(npwLog, "Inbound session from").Count,
            GiveWays = Regex.Matches(npwLog, @"of quiet, before its \d+s hold").Count,
            BusyDeferrals = Regex.Matches(npwLog, "dialling now would reset it").Count,
            Failure = failure ?? (outbound.Count < messages.Count || back.Count < expectedReplies ? null : null),
        };
    }

    /// <summary>Seconds the channel carried something (the union of transmissions).</summary>
    private static double Union(IReadOnlyList<Rig430Air.Over> overs)
    {
        var busy = 0.0;
        DateTime? s = null, e = null;
        foreach (var o in overs.OrderBy(o => o.Start))
        {
            if (e is { } end && o.Start <= end)
            {
                if (o.End > end) e = o.End;
                continue;
            }
            if (s is { } start) busy += (e!.Value - start).TotalSeconds;
            (s, e) = (o.Start, o.End);
        }
        if (s is { } last) busy += (e!.Value - last).TotalSeconds;
        return busy;
    }

    /// <summary>Transmissions that started while another station's was on air.</summary>
    private static int Collisions(IReadOnlyList<Rig430Air.Over> overs)
    {
        var sorted = overs.OrderBy(o => o.Start).ToList();
        var n = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            for (var j = i - 1; j >= 0 && j >= i - 6; j--)
            {
                if (sorted[j].Source != sorted[i].Source && sorted[i].Start < sorted[j].End) { n++; break; }
            }
        }
        return n;
    }

    private string BuildReport(Rig430Result r, Rig430Air? air, string relayLog, Dictionary<string, string> logs, DateTime t0, DateTime t1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rig 430: arm {arm.Id}, {workload.Id}, rep {rep}");
        sb.AppendLine();
        sb.AppendLine($"{arm.Title}. {arm.How}");
        sb.AppendLine();
        sb.AppendLine($"Radio: {Rig430Network.Modem} on net-sim, {Rig430Network.PathLossDb} dB, {Rig430Network.Radio}. NPR {Rig430Network.NprDelayMs} ms each way. LinBPQ {Rig430Network.BpqImage}. Seed {seed}. Host {Environment.MachineName}.");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Deliveries (s from the first message)");
        sb.AppendLine("```");
        lock (deliveredAt)
            foreach (var kv in deliveredAt.OrderBy(kv => kv.Value))
                sb.AppendLine($"{kv.Value.TotalSeconds,8:F1}  {kv.Key}  (handed in {submittedAt.GetValueOrDefault(kv.Key).TotalSeconds:F1}, {(kv.Value - submittedAt.GetValueOrDefault(kv.Key)).TotalSeconds:F1} s)");
        sb.AppendLine("```");
        if (air is not null)
        {
            foreach (var ch in rig.Channels)
            {
                sb.AppendLine();
                sb.AppendLine($"## {ch.Name} channel: transmissions");
                sb.AppendLine("```");
                foreach (var o in air.Overs(ch.Id, t0, t1)) sb.AppendLine($"{(o.Start - t0).TotalSeconds,8:F2}  {o.Source,-4} {o.Length.TotalSeconds,6:F2} s");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine($"## {ch.Name} channel: frames heard by the monitor");
                sb.AppendLine("```");
                foreach (var h in air.Heard(t0, t1).Where(h => h.Channel == ch.Id)) sb.AppendLine($"{(h.At - t0).TotalSeconds,8:F2}  {h.Frame.Describe()}");
                sb.AppendLine("```");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## NPR frames (relay log, whole seconds)");
        sb.AppendLine("```");
        foreach (var h in Rig430Air.NprFrames(relayLog, t0.AddSeconds(-1), t1.AddSeconds(1))) sb.AppendLine($"{(h.At - t0).TotalSeconds,6:F0}  {h.Direction}  {h.Frame.Describe()}");
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
