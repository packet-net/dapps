using System.Text;
using dapps.client.Backhaul;
using dapps.meshcore;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// On-air soak harness for the integrated MeshCore bearer. Run symmetrically on
// two radios (self/peer swapped). Each node continuously originates sequence-
// numbered BackhaulMessages to the peer through MeshCoreCompanionBackhaul, and
// receives the peer's via MeshCoreInbound → a recording IBackhaulInbox. The
// MeshCoreLink watchdog + airtime governor + compression are all active.
//
//   dapps-meshcore-soak --self GB7AAA-1 --peer GB7BBB-1 [opts]
// ---------------------------------------------------------------------------

var a = Args.Parse(args);
string self = a.Get("self", "NODE-A");
string peer = a.Get("peer", "NODE-B");
int durationSec = a.GetInt("duration-sec", 600);
int intervalSec = a.GetInt("interval-sec", 30);
int forceResetAt = a.GetInt("force-reset-at-sec", 0); // 0 = no forced reset

var opts = new MeshCoreBearerOptions
{
    Enabled = true,
    SerialPort = a.Get("port", "/dev/ttyUSB0"),
    Region = a.Get("region", "uk-test"),
    TxPowerDbm = (byte)a.GetInt("tx-power", 8),
    ChannelIndex = (byte)a.GetInt("channel-index", 2),
    ChannelName = a.Get("channel", "dapps-soak"),
    ChannelPsk = a.Get("psk", "dapps-soak-channel"),
    NodeName = self,
    AirtimeBudgetSecPerHour = a.GetDouble("budget", 120),
    Compress = !a.Has("no-compress"),
    CongestionBackoffFraction = a.GetDouble("congestion", 0.5),
    LbtGuardMs = a.GetInt("lbt", 400),
    ReliableDelivery = !a.Has("no-reliable"),
    LocalCallsign = self,
    AppName = "dapps-soak",
};

using var lf = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = lf.CreateLogger("soak");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var inbox = new SoakInbox(self, log);
var budget = new TxBudget(opts.AirtimeBudgetSecPerHour);
await using var link = new MeshCoreLink(opts, lf.CreateLogger<MeshCoreLink>());

log.LogInformation("SOAK start: self={0} peer={1} ch[{2}]='{3}' region={4} budget={5}s/hr dur={6}s interval={7}s",
    self, peer, opts.ChannelIndex, opts.ChannelName, opts.Region, opts.AirtimeBudgetSecPerHour, durationSec, intervalSec);

try { await link.StartAsync(cts.Token); }
catch (Exception ex) { log.LogError(ex, "link failed to start"); return 2; }

var reliability = opts.ReliableDelivery ? new MeshCoreReliability() : null;
var backhaul = new MeshCoreCompanionBackhaul(
    link, opts, budget, lf.CreateLogger<MeshCoreCompanionBackhaul>(), reliability: reliability);

// Optional induced loss to exercise reliability resends (soak only).
double dropPct = a.GetDouble("drop-pct", 0);
Func<BackhaulMessage, bool>? drop = dropPct > 0 ? (_ => Random.Shared.NextDouble() * 100 < dropPct) : null;

var inbound = new MeshCoreInbound(
    link, inbox, lf.CreateLogger<MeshCoreInbound>(),
    reliability,
    sendAck: (ack, c) => backhaul.ResendAsync(ack, self, c),
    localCallsign: self,
    dropForTest: drop);
var route = new BackhaulRoute(peer, MeshCoreChannel: opts.ChannelName);

var inboundTask = Task.Run(() => inbound.RunAsync(cts.Token));
var resendTask = reliability is not null ? Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { break; }
        var now = DateTime.UtcNow;
        foreach (var m in reliability.DropExpired(now)) log.LogWarning("reliable delivery gave up on {0}", m.Id);
        foreach (var (msg, local) in reliability.DueResends(now))
        {
            try
            {
                var res = await backhaul.ResendAsync(msg, local, cts.Token);
                if (res.Accepted) reliability.MarkResent(msg.Id, DateTime.UtcNow);
            }
            catch { }
        }
    }
}) : Task.CompletedTask;

long sent = 0, accepted = 0, throttled = 0, backedOff = 0, failed = 0;
string[] samples =
[
    "73 de " + self, "QSL 73 GL", "GM all, nice signal this morning, 599 here",
    "{\"t\":21.4,\"h\":62,\"p\":1013}", "ack ok", "!5152.34N/00007.12W>DAPPS node QRV",
    "Anyone around for a sked on the DAPPS net at 1900 local? 73",
];

var senderTask = Task.Run(async () =>
{
    int seq = 0;
    while (!cts.IsCancellationRequested)
    {
        var text = $"{samples[seq % samples.Length]} #{seq}";
        var msg = new BackhaulMessage(
            Id: Guid.NewGuid().ToString("N")[..7], Destination: peer, Salt: null, Ttl: 3600,
            Payload: Encoding.UTF8.GetBytes(text), Originator: self, LinkSourceCallsign: self,
            Headers: new Dictionary<string, string> { ["seq"] = seq.ToString(), ["app"] = "soak" });
        try
        {
            var r = await backhaul.SendAsync(msg, route, self, cts.Token);
            Interlocked.Increment(ref sent);
            if (r.Accepted) Interlocked.Increment(ref accepted);
            else if (r.Error?.Contains("budget") == true) { Interlocked.Increment(ref throttled); log.LogWarning("TX seq={0} throttled: {1}", seq, r.Error); }
            else if (r.Error?.Contains("congested") == true) { Interlocked.Increment(ref backedOff); log.LogWarning("TX seq={0} backoff: {1}", seq, r.Error); }
            else { Interlocked.Increment(ref failed); log.LogWarning("TX seq={0} failed: {1}", seq, r.Error); }
        }
        catch (Exception ex) { Interlocked.Increment(ref failed); log.LogWarning("TX seq={0} exception: {1}", seq, ex.Message); }
        seq++;
        try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), cts.Token); } catch { break; }
    }
});

// Optional: force one watchdog recovery mid-soak to prove on-air reset+recover.
if (forceResetAt > 0)
{
    _ = Task.Run(async () =>
    {
        try { await Task.Delay(TimeSpan.FromSeconds(forceResetAt), cts.Token); } catch { return; }
        log.LogWarning("=== forcing watchdog recovery (demonstrating on-air reset) ===");
        await link.RecoverAsync(cts.Token);
    });
}

// Run for the duration, then stop.
try { await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token); } catch { }
cts.Cancel();
try { await Task.WhenAll(senderTask, inboundTask, resendTask); } catch { }

var (recv, maxSeq, distinct) = inbox.Snapshot();
double lossPct = maxSeq >= 0 ? 100.0 * (1.0 - (double)distinct / (maxSeq + 1)) : 0;
log.LogInformation("================= SOAK SUMMARY ({0}) =================", self);
log.LogInformation("TX: offered={0} accepted={1} throttled={2} backoff={3} failed={4}", sent, accepted, throttled, backedOff, failed);
log.LogInformation("RX: delivered={0} distinctSeq={1} maxSeqFromPeer={2} loss={3:0.0}%", recv, distinct, maxSeq, lossPct);
log.LogInformation("Channel occupancy (trailing 60s): {0:0.0}%", backhaul.Occupancy * 100);
log.LogInformation("Airtime used (trailing hr): {0:0.0}s ({1:0.000}% duty); link resets={2}; link state={3}",
    budget.UsedSeconds(DateTime.UtcNow), budget.DutyPercent(DateTime.UtcNow), link.ResetCount, link.State);
if (reliability is not null)
    log.LogInformation("Reliability: confirmed={0} expired={1} pending={2} (induced drop {3:0.#}%)",
        reliability.Confirmed, reliability.Expired, reliability.PendingCount, dropPct);
return 0;

// ---------------- helpers ----------------

sealed class SoakInbox(string self, ILogger log) : IBackhaulInbox
{
    private readonly object _l = new();
    private readonly HashSet<int> _seqs = new();
    private long _received;
    private int _maxSeq = -1;

    public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
    {
        if (!string.Equals(message.Destination, self, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask; // not addressed to us (promiscuous channel)

        int seq = message.Headers is not null && message.Headers.TryGetValue("seq", out var sv) && int.TryParse(sv, out var s) ? s : -1;
        lock (_l)
        {
            _received++;
            if (seq >= 0) { _seqs.Add(seq); if (seq > _maxSeq) _maxSeq = seq; }
        }
        log.LogInformation("RX {0} from {1} seq={2} \"{3}\"", message.Id, sourceCallsign, seq, Encoding.UTF8.GetString(message.Payload));
        return Task.CompletedTask;
    }

    public (long received, int maxSeq, int distinct) Snapshot()
    {
        lock (_l) return (_received, _maxSeq, _seqs.Count);
    }
}

sealed class Args
{
    private readonly Dictionary<string, string?> _o = new();
    public bool Has(string k) => _o.ContainsKey(k);
    public string Get(string k, string d) => _o.TryGetValue(k, out var v) && v is not null ? v : d;
    public int GetInt(string k, int d) => _o.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : d;
    public double GetDouble(string k, double d) => _o.TryGetValue(k, out var v) && double.TryParse(v, out var x) ? x : d;

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            string? val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            a._o[key] = val;
        }
        return a;
    }
}
