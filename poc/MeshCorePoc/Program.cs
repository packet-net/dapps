using System.Security.Cryptography;
using System.Text;
using dapps.client.Backhaul;
using MeshCorePoc;

// ---------------------------------------------------------------------------
// DAPPS <-> MeshCore private-channel PoC (issue #137 / Phase H1).
// Independent of DAPPS core. Proves a real BackhaulMessage round-trips over a
// MeshCore private channel between two Heltec V3 radios.
//
//   info         <port>                read identity + radio params
//   provision    <port> [opts]         set UK-narrow radio params, name, channel
//   get-channel  <port> [opts]         dump a channel slot (name + PSK)
//   send-text    <port> "msg" [opts]   send a plain channel text (smoke test)
//   send-backhaul<port> [opts]         encode+fragment+send a BackhaulMessage
//   listen       <port> [opts]         receive; reassemble+decode DAPPS frames
// ---------------------------------------------------------------------------

var (cmd, port, opts) = ParseArgs(args);
if (cmd is null)
{
    PrintUsage();
    return 1;
}

// PoC defaults.
const string AppName = "dapps-poc";
double freq = GetD(opts, "freq", 869.618);   // UK 868 "narrow"
double bw = GetD(opts, "bw", 62.5);
byte sf = (byte)GetI(opts, "sf", 8);
byte cr = (byte)GetI(opts, "cr", 8);         // UK narrow uses CR 8 (firmware default is 5)
byte channelIndex = (byte)GetI(opts, "channel-index", 1);
string channelName = GetS(opts, "channel-name", "dapps-poc");
byte[] psk = DerivePsk(GetS(opts, "psk-phrase", "dapps-poc-channel-v1"));

// Localisation preset overrides the individual radio params when given.
if (opts.TryGetValue("region", out var regionName) && regionName is not null)
{
    var preset = Regions.Find(regionName)
        ?? throw new ArgumentException($"unknown region '{regionName}'; known: {string.Join(", ", Regions.All.Select(r => r.Name))}");
    freq = preset.FreqMhz; bw = preset.BwKhz; sf = preset.Sf; cr = preset.Cr;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// stdout is block-buffered when piped over ssh (non-tty); flush each line
// so a backgrounded listener's output appears in real time.
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

try
{
    switch (cmd)
    {
        case "info": await CmdInfo(); break;
        case "provision": await CmdProvision(); break;
        case "get-channel": await CmdGetChannel(); break;
        case "send-text": await CmdSendText(); break;
        case "send-backhaul": await CmdSendBackhaul(); break;
        case "send-data": await CmdSendData(); break;
        case "listen": await CmdListen(); break;
        case "selftest": return CmdSelfTest();
        case "budget-test": return CmdBudgetTest();
        case "characterise": CmdCharacterise(); break;
        case "regions": CmdRegions(); break;
        default: PrintUsage(); return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}
return 0;

// ---------------- commands ----------------

async Task<(MeshCoreClient, SelfInfo)> OpenAndStart()
{
    var c = new MeshCoreClient(port!);
    c.Open();
    var self = await c.AppStartAsync(AppName, cts.Token);
    return (c, self);
}

async Task CmdInfo()
{
    var (c, self) = await OpenAndStart();
    await using (c) PrintSelf(self);
}

async Task CmdProvision()
{
    await using var c = new MeshCoreClient(port!);
    c.Open();
    var self = await c.AppStartAsync(AppName, cts.Token);
    Console.WriteLine($"node {self.PublicKeyHex[..12]}  name='{self.Name}'");

    if (!opts.ContainsKey("no-radio"))
    {
        Console.WriteLine($"set radio: {freq:0.000} MHz / {bw:0.0} kHz / SF{sf} / CR{cr}");
        await c.SetRadioParamsAsync(freq, bw, sf, cr, cts.Token);
    }

    if (opts.TryGetValue("name", out var nm) && nm is not null)
    {
        Console.WriteLine($"set name: {nm}");
        await c.SetNameAsync(nm, cts.Token);
    }

    if (opts.ContainsKey("tx-power"))
    {
        byte dbm = (byte)GetI(opts, "tx-power", 22);
        Console.WriteLine($"set tx power: {dbm} dBm");
        await c.SetTxPowerAsync(dbm, cts.Token);
    }

    Console.WriteLine($"set channel[{channelIndex}] name='{channelName}' psk={Convert.ToHexString(psk).ToLowerInvariant()}");
    await c.SetChannelAsync(channelIndex, channelName, psk, cts.Token);

    var ch = await c.GetChannelAsync(channelIndex, cts.Token);
    Console.WriteLine($"verify channel[{ch.Index}]: name='{ch.Name}' psk={ch.SecretHex}");

    // Re-read identity to confirm radio params applied.
    var after = await c.AppStartAsync(AppName, cts.Token);
    Console.WriteLine($"after: {after.FreqMhz:0.000} MHz / {after.BwKhz:0.0} kHz / SF{after.Sf} / CR{after.Cr}");
    Console.WriteLine("provisioned OK");
}

async Task CmdGetChannel()
{
    await using var c = new MeshCoreClient(port!);
    c.Open();
    await c.AppStartAsync(AppName, cts.Token);
    var ch = await c.GetChannelAsync(channelIndex, cts.Token);
    Console.WriteLine($"channel[{ch.Index}]: name='{ch.Name}' psk={ch.SecretHex}");
}

async Task CmdSendText()
{
    var text = opts.TryGetValue("_text", out var t) ? t! : "ping from dapps-poc";
    await using var c = new MeshCoreClient(port!);
    c.Open();
    await c.AppStartAsync(AppName, cts.Token);
    var ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    await c.SendChannelTextAsync(channelIndex, text, ts, cts.Token);
    Console.WriteLine($"sent channel[{channelIndex}] text ({Encoding.UTF8.GetByteCount(text)} bytes): {text}");
}

async Task CmdSendBackhaul()
{
    string from = GetS(opts, "from", "POC-1");
    string dest = GetS(opts, "dest", "POC-2");
    string text = GetS(opts, "text", "hello from DAPPS over a MeshCore private channel");
    string id = GetS(opts, "id", Guid.NewGuid().ToString("N")[..7]);
    int gapMs = GetI(opts, "gap-ms", 1500);

    var compress = GetS(opts, "compress", "none").Equals("zstd", StringComparison.OrdinalIgnoreCase)
        ? DappsCompression.Mode.ZstdDict : DappsCompression.Mode.None;

    var msg = new BackhaulMessage(
        Id: id,
        Destination: dest,
        Salt: null,
        Ttl: 3600,
        Payload: Encoding.UTF8.GetBytes(text),
        Originator: from,
        LinkSourceCallsign: from);

    int encodedLen = dapps.client.Backhaul.Datagram.BackhaulMessageCodec.Encode(msg).Length;
    byte nonce = (byte)Random.Shared.Next(128);
    var frames = PrivateChannelTransport.ToFrames(msg, compress, ref nonce);
    Console.WriteLine($"backhaul id={id} {from} -> {dest}  payload={msg.Payload.Length}B encoded={encodedLen}B compress={compress}");
    Console.WriteLine($"  -> {frames.Count} binary frame(s) (max {frames.Max(f => f.Length)}B) @ mtu={PrivateChannelTransport.Mtu}, gap={gapMs}ms");

    // Good-citizen control: estimate airtime and gate on a self-enforced budget.
    // On Model A every send floods the whole same-preset network, so this is a hard gate.
    var budget = new TxBudget(GetD(opts, "tx-budget-sec-per-hour", TxBudget.DefaultSecondsPerHour));
    double totalAirMs = frames.Sum(f => Characterise.AirtimeMs(f.Length + Characterise.MeshCoreOnAirOverhead));
    Console.WriteLine($"  est. airtime {totalAirMs / 1000:0.00}s (budget {budget.BudgetSeconds:0}s/hr); NB each send floods the whole same-preset network");
    if (frames.Count > 4)
        Console.WriteLine($"  WARN: {frames.Count} packets is heavy for the public preset (Model A) — consider Model B/C for bulk");

    await using var c = new MeshCoreClient(port!);
    c.Open();
    await c.AppStartAsync(AppName, cts.Token);

    var swAll = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < frames.Count; i++)
    {
        double airMs = Characterise.AirtimeMs(frames[i].Length + Characterise.MeshCoreOnAirOverhead);
        if (!budget.TryReserve(airMs, DateTime.UtcNow, out var reason))
        {
            Console.WriteLine($"  THROTTLED at frame {i + 1}/{frames.Count}: {reason}");
            break;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await c.SendChannelDataAsync(channelIndex, frames[i], MeshCoreClient.DATA_TYPE_DEV, cts.Token);
        sw.Stop();
        Console.WriteLine($"  frame {i + 1}/{frames.Count} sent ({frames[i].Length}B, ~{airMs / 1000:0.00}s air) in {sw.ElapsedMilliseconds}ms");
        if (i < frames.Count - 1) await Task.Delay(gapMs, cts.Token);
    }
    swAll.Stop();
    Console.WriteLine($"all frames queued in {swAll.ElapsedMilliseconds}ms; airtime used this session {budget.UsedSeconds(DateTime.UtcNow):0.00}s ({budget.DutyPercent(DateTime.UtcNow):0.000}% duty)");
}

async Task CmdSendData()
{
    var textArg = opts.TryGetValue("_text", out var t) && t is not null ? t : "RAWBYTES";
    var bytes = Encoding.UTF8.GetBytes(textArg);
    await using var c = new MeshCoreClient(port!);
    c.Open();
    await c.AppStartAsync(AppName, cts.Token);
    await c.SendChannelDataAsync(channelIndex, bytes, MeshCoreClient.DATA_TYPE_DEV, cts.Token);
    Console.WriteLine($"sent raw channel-data ({bytes.Length}B) on channel[{channelIndex}]: {Convert.ToHexString(bytes)} \"{textArg}\"");
}

async Task CmdListen()
{
    int seconds = GetI(opts, "seconds", 0); // 0 = until Ctrl-C
    bool raw = opts.ContainsKey("raw");
    var transport = new PrivateChannelTransport();
    await using var c = new MeshCoreClient(port!);

    var wake = new SemaphoreSlim(0);
    c.MessageWaiting += () => { try { wake.Release(); } catch { } };

    c.Open();
    var self = await c.AppStartAsync(AppName, cts.Token);
    var ch = await c.GetChannelAsync(channelIndex, cts.Token);
    Console.WriteLine($"listening on {port} as {self.PublicKeyHex[..12]} channel[{ch.Index}]='{ch.Name}' " +
                      $"({self.FreqMhz:0.000}MHz/SF{self.Sf}/CR{self.Cr}){(raw ? " [raw]" : "")}. Ctrl-C to stop.");

    var deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
    while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        // Wake on MSG_WAITING push, or poll every 800ms as a safety net.
        await Task.WhenAny(wake.WaitAsync(cts.Token), Task.Delay(800, cts.Token));
        MeshCoreClient.InboundBatch batch;
        try { batch = await c.DrainAsync(cts.Token); }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Console.Error.WriteLine($"drain error: {ex.Message}"); continue; }

        // Text messages are human chatter on the channel - just surface them.
        foreach (var m in batch.Texts)
            Console.WriteLine($"[ch{m.ChannelIndex} text] {m.Text}");

        // Binary channel-data is the DAPPS carriage.
        foreach (var d in batch.Data)
        {
            var rx = $"[ch{d.ChannelIndex} snr={d.SnrDb:0.0}dB {(d.ReceivedDirect ? "direct" : $"flood/{d.PathLen}h")} {d.Payload.Length}B]";
            if (raw)
            {
                Console.WriteLine($"{rx} raw: {Convert.ToHexString(d.Payload)}  \"{Printable(d.Payload)}\"");
                continue;
            }
            var r = transport.Ingest(d.Payload, DateTime.UtcNow);
            switch (r.Kind)
            {
                case PrivateChannelTransport.Kind.FragmentPartial:
                    Console.WriteLine($"{rx} frag {r.Header!.Value.Seq + 1}/{r.Header!.Value.Count} of {r.Header!.Value.Id} (waiting for more)");
                    break;
                case PrivateChannelTransport.Kind.BackhaulComplete:
                    var bm = r.Message!;
                    Console.WriteLine($"{rx} >>> BACKHAUL id={bm.Id} {bm.Originator} -> {bm.Destination} " +
                                      $"ttl={bm.Ttl} linksrc={bm.LinkSourceCallsign} payload=\"{Encoding.UTF8.GetString(bm.Payload)}\"");
                    break;
                case PrivateChannelTransport.Kind.Bad:
                    Console.WriteLine($"{rx} non-DAPPS / undecodable: \"{Printable(d.Payload)}\"");
                    break;
            }
        }
    }
    Console.WriteLine("listener stopped.");
}

static string Printable(byte[] b) =>
    new string(b.Select(x => x >= 0x20 && x < 0x7F ? (char)x : '.').ToArray());

void CmdRegions()
{
    Console.WriteLine("region presets (DAPPS-controllable localisation):");
    foreach (var r in Regions.All)
        Console.WriteLine($"  {r.Name,-12} {r.FreqMhz,8:0.000} MHz  BW {r.BwKhz,5:0.#}  SF{r.Sf} CR4/{r.Cr}  ≤{r.MaxPowerDbm}dBm  — {r.Notes}");
}

void CmdCharacterise()
{
    var report = Characterise.Run();
    Console.WriteLine(report);
    var path = GetS(opts, "out", "characterisation.md");
    File.WriteAllText(path, report);
    Console.Error.WriteLine($"(written to {path})");
}

int CmdBudgetTest()
{
    // Software demo of the good-citizen airtime governor - NO transmission
    // (deliberately: we're on the live public preset; soak-testing the air would
    // be exactly the bad behaviour the governor exists to prevent).
    double budgetSec = GetD(opts, "tx-budget-sec-per-hour", TxBudget.DefaultSecondsPerHour);
    var msgs = Characterise.Corpus(2).Select(x =>
    {
        byte n = 0;
        var frames = PrivateChannelTransport.ToFrames(x.msg, DappsCompression.Mode.ZstdDict, ref n);
        double air = frames.Sum(f => Characterise.AirtimeMs(f.Length + Characterise.MeshCoreOnAirOverhead));
        return (frames.Count, air);
    }).ToList();
    double meanAir = msgs.Average(m => m.air);
    double meanPkts = msgs.Average(m => m.Item1);

    Console.WriteLine($"budget-test: {budgetSec:0}s/hr governor; compressed corpus mean {meanAir / 1000:0.00}s airtime/msg ({meanPkts:0.0} pkts/msg)");
    Console.WriteLine($"  sustainable rate ≈ {budgetSec / (meanAir / 1000):0} msgs/hr (~{3600 / (budgetSec / (meanAir / 1000)):0}s between msgs); duty {budgetSec / 36:0.00}%");

    // Burst: offer 500 messages with no spacing and watch the hard gate cap them.
    var budget = new TxBudget(budgetSec);
    var t0 = DateTime.UnixEpoch;
    int sent = 0, refused = 0;
    for (int i = 0; i < 500; i++)
        if (budget.TryReserve(msgs[i % msgs.Count].air, t0, out _)) sent++; else refused++;
    Console.WriteLine($"  burst (no spacing): {sent} admitted then {refused} REFUSED — governor capped the burst at {budget.UsedSeconds(t0):0.0}s ({budget.DutyPercent(t0):0.00}% duty)");
    Console.WriteLine("  (the real long-running bearer accumulates over a trailing hour; this PoC governor is per-process)");
    return 0;
}

int CmdSelfTest()
{
    // In-process loopback: exercises the real vendored codec + packetiser
    // + base64 channel framing with no radio. Tests in-order AND out-of-order
    // fragment delivery (LoRa floods can reorder).
    string payload = string.Concat(Enumerable.Repeat("DAPPS-over-MeshCore PoC payload. ", 9)); // ~290 bytes
    var original = new BackhaulMessage(
        Id: "ab12cd3", Destination: "GB7ABC-1", Salt: 42, Ttl: 1800,
        Payload: Encoding.UTF8.GetBytes(payload),
        Originator: "M0LTE-7", LinkSourceCallsign: "M0LTE-7");

    bool ok = true;
    foreach (var mode in new[] { DappsCompression.Mode.None, DappsCompression.Mode.ZstdDict })
    {
        byte nonce = 0;
        var frames = PrivateChannelTransport.ToFrames(original, mode, ref nonce);
        int maxLen = frames.Max(f => f.Length);
        Console.WriteLine($"selftest [{mode}]: {original.Payload.Length}B payload -> {frames.Count} frame(s), max {maxLen}B channel-data");
        if (maxLen > 165) { Console.WriteLine($"  FAIL: frame {maxLen}B exceeds 165B cap"); ok = false; }

        foreach (var order in new[] { "in-order", "reversed" })
        {
            var t = new PrivateChannelTransport();
            var seq = order == "reversed" ? frames.Reverse().ToList() : frames.ToList();
            BackhaulMessage? got = null;
            foreach (var f in seq)
            {
                var r = t.Ingest(f, DateTime.UtcNow);
                if (r.Kind == PrivateChannelTransport.Kind.BackhaulComplete) got = r.Message;
            }
            bool pass = got is not null
                && got.Id == original.Id && got.Destination == original.Destination
                && got.Originator == original.Originator && got.Ttl == original.Ttl
                && got.Salt == original.Salt && got.LinkSourceCallsign == original.LinkSourceCallsign
                && got.Payload.AsSpan().SequenceEqual(original.Payload);
            Console.WriteLine($"  {order}: {(pass ? "PASS" : "FAIL")}");
            ok &= pass;
        }
    }
    Console.WriteLine(ok ? "selftest PASS" : "selftest FAIL");
    return ok ? 0 : 3;
}

// ---------------- helpers ----------------

void PrintSelf(SelfInfo s) => Console.WriteLine(
    $"name='{s.Name}' pubkey={s.PublicKeyHex[..12]}... adv_type={s.AdvType} txp={s.TxPower}/{s.MaxTxPower} " +
    $"{s.FreqMhz:0.000}MHz BW{s.BwKhz:0.0} SF{s.Sf} CR{s.Cr}");

static byte[] DerivePsk(string phrase) => SHA256.HashData(Encoding.UTF8.GetBytes(phrase))[..16];

static (string?, string?, Dictionary<string, string?>) ParseArgs(string[] a)
{
    if (a.Length == 0) return (null, null, new());
    string cmd = a[0];
    string port = "/dev/ttyUSB0";
    var opts = new Dictionary<string, string?>();
    var positionals = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        if (a[i].StartsWith("--"))
        {
            string key = a[i][2..];
            string? val = (i + 1 < a.Length && !a[i + 1].StartsWith("--")) ? a[++i] : null;
            opts[key] = val;
        }
        else positionals.Add(a[i]);
    }
    if (positionals.Count > 0) port = positionals[0];
    if (positionals.Count > 1) opts["_text"] = positionals[1];
    return (cmd, port, opts);
}

static double GetD(Dictionary<string, string?> o, string k, double dflt) =>
    o.TryGetValue(k, out var v) && double.TryParse(v, out var d) ? d : dflt;
static int GetI(Dictionary<string, string?> o, string k, int dflt) =>
    o.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : dflt;
static string GetS(Dictionary<string, string?> o, string k, string dflt) =>
    o.TryGetValue(k, out var v) && v is not null ? v : dflt;

void PrintUsage()
{
    Console.WriteLine("""
    meshcore-poc <command> [<port=/dev/ttyUSB0>] [options]

      info         <port>
      regions
      provision    <port> [--region uk-narrow] [--name N] [--tx-power 8]
                          [--channel-index 1] [--channel-name dapps-poc] [--no-radio]
      get-channel  <port> [--channel-index 1]
      send-text    <port> "message" [--channel-index 1]          (diagnostic; text path)
      send-data    <port> "bytes"   [--channel-index 1]          (raw binary; proves no prefix)
      send-backhaul<port> [--from POC-1] [--dest POC-2] [--text "..."] [--id 7hex]
                          [--compress none|zstd] [--tx-budget-sec-per-hour 30]
                          [--channel-index 1] [--gap-ms 1500]
      listen       <port> [--channel-index 1] [--seconds N] [--raw]
      selftest                                                   (no radio)
      budget-test  [--tx-budget-sec-per-hour 30]                 (no radio)
      characterise [--out characterisation.md]                   (no radio)
    """);
}
