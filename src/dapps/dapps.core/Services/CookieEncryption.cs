using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace dapps.core.Services;

/// <summary>
/// #248: browsers send a host's cookies to every port on that host,
/// so other web apps on the node get DAPPS's cookies too, and
/// XRouter's dashboard rejects request header lines of 512 bytes or
/// more. With the default AES-CBC + HMAC data protection, the admin
/// and antiforgery cookies come to exactly 512; AES-GCM's smaller
/// overhead brings that down to about 430. Program.cs configures GCM
/// for new keys, and this makes sure the key in use is one of them.
/// </summary>
public static class CookieEncryption
{
    /// <summary>
    /// When the GCM key in use was activated. Admin cookies issued
    /// before this were sealed with an older, bigger key and get
    /// reissued.
    /// </summary>
    public static DateTimeOffset GcmKeySince { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// An existing install's key ring holds a CBC key that would stay
    /// the default for up to 90 days; if the newest usable key isn't
    /// GCM, make one that's active now. Old keys stay in the ring, so
    /// cookies they sealed still decrypt.
    /// </summary>
    public static void EnsureGcmKey(IKeyManager keys, ILogger logger)
    {
        var now = DateTimeOffset.UtcNow;
        var current = keys.GetAllKeys()
            .Where(k => !k.IsRevoked && k.ActivationDate <= now && k.ExpirationDate > now)
            .OrderByDescending(k => k.ActivationDate)
            .FirstOrDefault();
        if (current is not null && IsGcm(current))
        {
            GcmKeySince = current.ActivationDate;
            return;
        }

        var created = keys.CreateNewKey(now, now.AddDays(90));
        GcmKeySince = created.ActivationDate;
        logger.LogInformation("Cookie encryption: created an AES-GCM data protection key, so dashboard cookies stay small (#248)");
    }

    private static bool IsGcm(IKey key) =>
        key.Descriptor.ExportToXml().SerializedDescriptorElement
            .Element("encryption")?.Attribute("algorithm")?.Value?.Contains("GCM", StringComparison.Ordinal) == true;
}
