namespace MeshCorePoc;

/// <summary>
/// A localisation/region preset = the regulatory + network radio settings for a
/// locale. In the real DAPPS bearer this is part of the device-control API the
/// node exposes (alongside TX power and channel management): an operator picks a
/// region and DAPPS pushes the matching SET_RADIO_PARAMS / SET_RADIO_TX_POWER.
///
/// NOTE: only the two EU/UK presets here are hardware-confirmed. A production
/// build should source the full, current preset table from MeshCore upstream
/// (e.g. the api.meshcore.nz preset API) rather than hard-coding regulatory
/// values, and must enforce MaxPowerDbm per region.
/// </summary>
public sealed record RegionPreset(
    string Name, double FreqMhz, double BwKhz, byte Sf, byte Cr, byte MaxPowerDbm, string Notes);

public static class Regions
{
    public static readonly IReadOnlyList<RegionPreset> All =
    [
        new("uk-narrow", 869.618, 62.5, 8, 8, 27,
            "Current UK MeshCore net. 869.4-869.65 sub-band: 10% duty cycle, up to 500mW (27dBm) ERP."),
        new("eu-legacy", 869.525, 250.0, 11, 5, 14,
            "Deprecated EU/UK 'wide long range' (pre-2025). 0.1% sub-band, 25mW (14dBm)."),
        new("uk-test", 868.400, 62.5, 8, 8, 14,
            "Bench/prototype ISOLATION. 868.0-868.6 sub-band: 1% duty, 25mW (14dBm). Off the UK-narrow "
            + "repeater frequency so test floods aren't relayed across the public mesh. Verify UK legality before use."),
    ];

    public static RegionPreset? Find(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
