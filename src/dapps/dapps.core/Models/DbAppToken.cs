using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per app that has a credential issued. The token is never
/// stored - only its PBKDF2-HMAC-SHA256 hash plus the per-row salt that
/// derived it. Operators see the plaintext exactly once at creation
/// time and store it elsewhere.
/// </summary>
[Table("apptokens")]
public sealed class DbAppToken
{
    [PrimaryKey]
    public string AppName { get; set; } = "";

    /// <summary>Hex-encoded PBKDF2-HMAC-SHA256 output (32 bytes → 64 hex chars).</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>Hex-encoded random salt fed into the KDF (16 bytes → 32 hex chars).</summary>
    public string Salt { get; set; } = "";

    /// <summary>
    /// Hex SHA-256 of the plaintext token: a non-secret lookup id, so a
    /// presented token selects its one row before the slow PBKDF2 runs.
    /// Safe to store because tokens are 256 random bits (nothing to
    /// brute-force), and the PBKDF2 hash is still what authenticates.
    /// Null on rows minted before this column existed; those are
    /// filled in the first time their token is presented.
    /// </summary>
    [Indexed]
    public string? TokenId { get; set; }

    /// <summary>UTC instant the token was issued. Useful for audit and rotation policies.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
