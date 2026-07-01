using System.Security.Cryptography;
using System.Text;

namespace dapps.meshcore;

/// <summary>Configuration for the MeshCore bearer. In the dapps host these are
/// populated from <c>SystemOptions</c> / <c>DAPPS_MESHCORE_*</c> env vars.</summary>
public sealed class MeshCoreBearerOptions
{
    public bool Enabled { get; set; }
    public string SerialPort { get; set; } = "/dev/ttyUSB0";
    public string Region { get; set; } = "uk-test";
    public byte TxPowerDbm { get; set; } = 8;
    public byte ChannelIndex { get; set; } = 1;
    public string ChannelName { get; set; } = "dapps";
    /// <summary>16-byte channel PSK as hex (32 chars), or a passphrase to derive one.</summary>
    public string ChannelPsk { get; set; } = "dapps-default-channel";
    public string NodeName { get; set; } = "DAPPS";
    public double AirtimeBudgetSecPerHour { get; set; } = TxBudget.DefaultSecondsPerHour;
    public bool Compress { get; set; } = true;
    public string AppName { get; set; } = "dapps";

    /// <summary>Adaptive congestion backoff (#157): refuse sends when the channel's
    /// trailing-window occupancy is at or above this fraction (0..1). 0 disables.</summary>
    public double CongestionBackoffFraction { get; set; } = 0.5;

    /// <summary>Listen-before-talk guard (ms): if a packet was overheard more
    /// recently than this, wait out the remainder (plus jitter) before transmitting,
    /// to avoid colliding with an in-progress flood. 0 disables.</summary>
    public int LbtGuardMs { get; set; } = 400;

    public RegionPreset ResolveRegion() =>
        Regions.Find(Region) ?? throw new ArgumentException($"unknown MeshCore region '{Region}'");

    /// <summary>The 16-byte channel secret: a 32-char hex string is used verbatim,
    /// otherwise the value is treated as a passphrase and hashed to 16 bytes.</summary>
    public byte[] ResolvePsk()
    {
        var v = ChannelPsk?.Trim() ?? "";
        if (v.Length == 32 && v.All(Uri.IsHexDigit))
            return Convert.FromHexString(v);
        return SHA256.HashData(Encoding.UTF8.GetBytes(v))[..16];
    }
}
