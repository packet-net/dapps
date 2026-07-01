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

    /// <summary>Deployment model C: when <see cref="Region"/> is "custom", the dedicated
    /// preset spec (e.g. <c>freq=868.4;bw=62.5;sf=8;cr=8;pwr=14</c>) for a DAPPS-only
    /// frequency/SF. Ignored for baked regions. See README "Containment".</summary>
    public string CustomPreset { get; set; } = "";

    /// <summary>Deployment model B: flood-scope key. Empty = unscoped (model A - floods are
    /// carried network-wide by any same-preset public repeater). Non-empty tells the radio
    /// to tag our floods with this scope so nodes/repeaters that don't share it drop them,
    /// containing DAPPS traffic to our own scoped infra. See README "Containment" for the
    /// firmware caveats.</summary>
    public string FloodScopeKey { get; set; } = "";
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

    /// <summary>End-to-end reliability (#26): ACK received messages addressed to us
    /// and resend our own unacked messages until acked or their lifetime expires.</summary>
    public bool ReliableDelivery { get; set; } = true;

    /// <summary>This node's DAPPS callsign — decides which received messages to ACK
    /// (those addressed to us) and is the ACK originator.</summary>
    public string LocalCallsign { get; set; } = "";

    public RegionPreset ResolveRegion() =>
        Region.Equals(Regions.CustomName, StringComparison.OrdinalIgnoreCase)
            ? Regions.ParseCustom(CustomPreset)
            : Regions.Find(Region) ?? throw new ArgumentException($"unknown MeshCore region '{Region}'");

    /// <summary>Which deployment model this config selects, for logging/observability.
    /// A = unscoped public preset, B = scoped public preset, C = dedicated/custom preset.</summary>
    public string DeploymentModel()
    {
        bool scoped = ResolveFloodScopeKey() is not null;   // resolved, so an all-zero key reads as unscoped
        bool dedicated = Region.Equals(Regions.CustomName, StringComparison.OrdinalIgnoreCase);
        return dedicated ? "C (dedicated preset)" : scoped ? "B (scoped public preset)" : "A (unscoped public preset)";
    }

    /// <summary>The 16-byte channel secret: a 32-char hex string is used verbatim,
    /// otherwise the value is treated as a passphrase and hashed to 16 bytes.</summary>
    public byte[] ResolvePsk()
    {
        var v = ChannelPsk?.Trim() ?? "";
        if (v.Length == 32 && v.All(Uri.IsHexDigit))
            return Convert.FromHexString(v);
        return SHA256.HashData(Encoding.UTF8.GetBytes(v))[..16];
    }

    /// <summary>The 16-byte flood-scope key (deployment model B), or null when unscoped
    /// (empty <see cref="FloodScopeKey"/>). A 32-char hex string is used verbatim; any
    /// other value is treated as a PUBLIC region NAME and hashed as SHA256("#"+name)[..16]
    /// - the same derivation MeshCore uses for public region keys, so repeaters configured
    /// with <c>region put &lt;name&gt;</c> (flood-allowed) carry our traffic and everyone
    /// else drops it. A truly-secret key would be dropped network-wide: the firmware's
    /// private ($-prefixed) keystore is stubbed in v1.16.x.</summary>
    public byte[]? ResolveFloodScopeKey()
    {
        var v = FloodScopeKey?.Trim() ?? "";
        if (v.Length == 0) return null;
        var key = (v.Length == 32 && v.All(Uri.IsHexDigit))
            ? Convert.FromHexString(v)
            : SHA256.HashData(Encoding.UTF8.GetBytes("#" + v))[..16];
        // An all-zero key is what the radio treats as "unscoped" (SetFloodScopeAsync sends
        // the clear frame for it), so report it as unscoped here too - otherwise
        // DeploymentModel()/the link log would claim model B while the wire is unscoped.
        return key.All(b => b == 0) ? null : key;
    }
}
