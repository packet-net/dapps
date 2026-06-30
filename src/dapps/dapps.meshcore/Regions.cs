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
}
