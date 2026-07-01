namespace dapps.meshcore;

/// <summary>
/// A localisation/region preset: regulatory + network radio settings for a
/// locale, pushed to the radio via SET_RADIO_PARAMS / SET_RADIO_TX_POWER.
/// Only the EU/UK presets are hardware-confirmed; a production build should
/// source the live table from MeshCore upstream and enforce MaxPowerDbm.
/// </summary>
public sealed record RegionPreset(
    string Name, double FreqMhz, double BwKhz, byte Sf, byte Cr, byte MaxPowerDbm, string Notes);

public static class Regions
{
    public static readonly IReadOnlyList<RegionPreset> All =
    [
        new("uk-narrow", 869.618, 62.5, 8, 8, 27,
            "Current UK MeshCore net. 869.4-869.65 sub-band: 10% duty, up to 500mW (27dBm) ERP."),
        new("eu-legacy", 869.525, 250.0, 11, 5, 14,
            "Deprecated EU/UK 'wide long range' (pre-2025). 0.1% sub-band, 25mW (14dBm)."),
        new("uk-test", 868.400, 62.5, 8, 8, 14,
            "Bench/prototype ISOLATION. 868.0-868.6 sub-band: 1% duty, 25mW (14dBm). Off the UK-narrow "
            + "repeater frequency so test floods aren't relayed across the public mesh. Verify UK legality."),
    ];

    public static RegionPreset? Find(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The region name that selects an operator-defined preset parsed from a
    /// <see cref="ParseCustom"/> spec, rather than a baked entry in <see cref="All"/>.</summary>
    public const string CustomName = "custom";

    /// <summary>
    /// Build a one-off preset for a dedicated DAPPS frequency/SF (deployment model C —
    /// physical isolation on the operator's own radio settings). Spec is KV pairs
    /// separated by ';' or ',', e.g. <c>freq=868.4;bw=62.5;sf=8;cr=8;pwr=14</c> where
    /// freq is MHz, bw is kHz, and pwr is the max TX power in dBm the bearer will clamp
    /// to. All five fields are required; ranges are validated so a fat-fingered value
    /// can't push the radio somewhere illegal or nonsensical. Regulatory compliance for
    /// a custom frequency/power is the operator's responsibility.
    /// </summary>
    public static RegionPreset ParseCustom(string spec)
    {
        var kv = ParseKv(spec);
        double freq = ReqDouble(kv, "freq", 100.0, 2000.0);   // MHz, spans the LoRa ISM bands
        double bw = ReqDouble(kv, "bw", 1.0, 1000.0);         // kHz
        byte sf = (byte)ReqInt(kv, "sf", 5, 12);
        byte cr = (byte)ReqInt(kv, "cr", 5, 8);               // 4/5..4/8
        byte pwr = (byte)ReqInt(kv, "pwr", 1, 30);            // max dBm
        return new RegionPreset(CustomName, freq, bw, sf, cr, pwr,
            "Operator-defined dedicated DAPPS preset (deployment model C) - own frequency/SF for physical isolation.");
    }

    private static Dictionary<string, string> ParseKv(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("custom MeshCore preset is empty; set a spec like 'freq=868.4;bw=62.5;sf=8;cr=8;pwr=14'");
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in spec.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"malformed custom-preset field '{part}' (expected key=value)");
            kv[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return kv;
    }

    private static double ReqDouble(Dictionary<string, string> kv, string key, double min, double max)
    {
        if (!kv.TryGetValue(key, out var raw))
            throw new ArgumentException($"custom preset missing required field '{key}'");
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            throw new ArgumentException($"custom preset field '{key}={raw}' is not a number");
        if (v < min || v > max)
            throw new ArgumentException($"custom preset field '{key}={raw}' out of range [{min}..{max}]");
        return v;
    }

    private static int ReqInt(Dictionary<string, string> kv, string key, int min, int max)
    {
        if (!kv.TryGetValue(key, out var raw))
            throw new ArgumentException($"custom preset missing required field '{key}'");
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
            throw new ArgumentException($"custom preset field '{key}={raw}' is not an integer");
        if (v < min || v > max)
            throw new ArgumentException($"custom preset field '{key}={raw}' out of range [{min}..{max}]");
        return v;
    }
}
