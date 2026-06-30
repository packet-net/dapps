namespace dapps.meshcore;

/// <summary>
/// LoRa time-on-air estimate (Semtech formula). Used by the airtime governor
/// and stats. On-air bytes = the channel-data payload + MeshCore packet overhead.
/// </summary>
public static class LoRaAirtime
{
    /// <summary>MeshCore packet header + 1-byte channel hash + cipher block/MAC,
    /// on top of our channel-data payload. Approximate; refine from LOG_RX_DATA.</summary>
    public const int MeshCoreOnAirOverhead = 16;

    public static double Ms(int onAirPayloadBytes, int sf = 8, double bwHz = 62_500, int crDenom = 8, int preamble = 8)
    {
        double tSym = Math.Pow(2, sf) / bwHz * 1000.0;
        double tPreamble = (preamble + 4.25) * tSym;
        const int de = 0, ih = 0, crcOn = 1;       // explicit header, CRC on, no low-rate-opt
        int cr = crDenom - 4;
        double num = 8 * onAirPayloadBytes - 4 * sf + 28 + 16 * crcOn - 20 * ih;
        double den = 4 * (sf - 2 * de);
        int symb = 8 + (int)Math.Max(Math.Ceiling(num / den) * (cr + 4), 0);
        return tPreamble + symb * tSym;
    }

    /// <summary>Airtime for a single channel-data frame of <paramref name="payloadBytes"/>.</summary>
    public static double FrameMs(int payloadBytes, RegionPreset region) =>
        Ms(payloadBytes + MeshCoreOnAirOverhead, region.Sf, region.BwKhz * 1000, region.Cr);
}
