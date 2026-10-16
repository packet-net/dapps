using System.Security.Cryptography;
using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// Mints, verifies, and revokes per-app credentials. Tokens are hashed
/// at rest via PBKDF2-HMAC-SHA256 with a 16-byte per-row salt and 100k
/// iterations - more than enough for the threat model (a hostile
/// neighbour on the LAN), well under the cost budget for verifying a
/// credential on every MQTT CONNECT or REST call.
///
/// Plaintext tokens are returned only at creation time. There is no
/// "look up the token" path - only "does this plaintext match what we
/// have on file."
/// </summary>
public sealed class AppTokenStore(ILogger<AppTokenStore> logger)
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    /// <summary>
    /// Generate a fresh token for <paramref name="appName"/>, store its
    /// hash, and return the plaintext for the caller to hand to the app
    /// owner. Re-issuing for an existing app rotates: the prior token
    /// stops verifying straight away, so REST calls using it fail on
    /// their very next request. An MQTT client that's already connected
    /// keeps working until it disconnects and reconnects, though - the
    /// broker only checks the token at CONNECT, not per message.
    ///
    /// Looks up any existing row for <paramref name="appName"/> case-
    /// insensitively, so rotating "chat" also replaces a pre-existing
    /// "Chat" row rather than leaving two rows for what callers treat as
    /// one app (callers normalise new input to lowercase before calling
    /// this - see <see cref="TryNormalizeAppName"/> - but older rows
    /// written before that existed may still carry mixed case).
    /// </summary>
    public async Task<string> CreateOrRotateAsync(string appName)
    {
        var plaintext = GenerateRandomToken();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(plaintext, salt);

        var connection = DbInfo.GetAsyncConnection();
        var existing = await FindRowCaseInsensitiveAsync(connection, appName);
        if (existing != null)
        {
            await connection.DeleteAsync<DbAppToken>(existing.AppName);
        }

        await connection.InsertAsync(new DbAppToken
        {
            AppName = appName,
            TokenHash = Convert.ToHexString(hash),
            Salt = Convert.ToHexString(salt),
            CreatedAt = DateTime.UtcNow,
        });

        logger.LogInformation("Issued token for app {0}", appName);
        return plaintext;
    }

    /// <summary>
    /// Returns the app name the token belongs to, or null if no row's
    /// hash matches. Iterates the table because tokens aren't indexed
    /// (and shouldn't be - knowing a hash maps to an app would be a
    /// downgrade). For the expected scale (a few apps per node) this is
    /// fine; if it ever isn't, store the plaintext-prefix as a
    /// non-secret index column.
    /// </summary>
    public async Task<string?> VerifyAsync(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var connection = DbInfo.GetAsyncConnection();
        var rows = await connection.QueryAsync<DbAppToken>("select * from apptokens");
        foreach (var row in rows)
        {
            var salt = Convert.FromHexString(row.Salt);
            var stored = Convert.FromHexString(row.TokenHash);
            var attempt = Pbkdf2(plaintext, salt);
            if (CryptographicOperations.FixedTimeEquals(stored, attempt))
            {
                return row.AppName;
            }
        }
        return null;
    }

    /// <summary>Remove a token, matching <paramref name="appName"/>
    /// case-insensitively against whatever's stored (see
    /// <see cref="CreateOrRotateAsync"/>). Returns true if a row was
    /// deleted. Immediate for REST; an already-connected MQTT client for
    /// that app keeps working until it next reconnects (see
    /// <see cref="CreateOrRotateAsync"/> for why).</summary>
    public async Task<bool> RevokeAsync(string appName)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = await FindRowCaseInsensitiveAsync(connection, appName);
        if (existing is null) return false;
        var deleted = await connection.ExecuteAsync(
            "delete from apptokens where AppName=?", existing.AppName);
        return deleted > 0;
    }

    /// <summary>List the apps that have a token issued. Token data not exposed.</summary>
    public async Task<IReadOnlyList<AppTokenInfo>> ListAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        var rows = await connection.QueryAsync<DbAppToken>("select * from apptokens");
        return rows.Select(r => new AppTokenInfo(r.AppName, r.CreatedAt)).ToList();
    }

    /// <summary>True if any tokens are configured at all. Cheap probe used by
    /// the auth middleware to skip enforcement on a freshly-deployed node.</summary>
    public async Task<bool> AnyAsync()
    {
        var connection = DbInfo.GetAsyncConnection();
        var count = await connection.ExecuteScalarAsync<int>("select count(*) from apptokens");
        return count > 0;
    }

    /// <summary>
    /// Validates and lowercases a candidate app name to what the app
    /// interface actually accepts. The app name becomes an MQTT topic
    /// segment (<c>dapps/in/&lt;app&gt;</c> etc.) and the <c>{app}</c>
    /// REST route segment, so '/', '+', '#' (MQTT topic wildcards and
    /// separator) and whitespace would either break topic matching or
    /// never round-trip through a URL cleanly; control characters are
    /// rejected for the same reason nothing else accepts them. Lower-
    /// cases the result because MQTT CONNECT and the REST bearer check
    /// both compare app names case-insensitively already (see
    /// <see cref="MqttBrokerService"/> and <see cref="BearerAuthMiddleware"/>) -
    /// "chat" and "Chat" are the same app, so this store should only
    /// ever mint one token for either spelling.
    /// </summary>
    public static bool TryNormalizeAppName(string? raw, out string normalized, out string? error)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            normalized = "";
            error = "App name is required.";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '+' or '#')
            {
                normalized = "";
                error = "App name can't contain '/', '+', '#', whitespace, or control characters.";
                return false;
            }
        }

        normalized = trimmed.ToLowerInvariant();
        error = null;
        return true;
    }

    private static byte[] Pbkdf2(string plaintext, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(plaintext, salt, Iterations, Hash, HashBytes);
    }

    /// <summary>Case-insensitive lookup by app name. The table holds a
    /// handful of rows at most (a few apps per node), so a full scan
    /// plus an ordinal-ignore-case comparison in C# is simpler than a
    /// COLLATE NOCASE query and costs nothing measurable at this scale.</summary>
    private static async Task<DbAppToken?> FindRowCaseInsensitiveAsync(SQLiteAsyncConnection connection, string appName)
    {
        var rows = await connection.QueryAsync<DbAppToken>("select * from apptokens");
        return rows.FirstOrDefault(r => string.Equals(r.AppName, appName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateRandomToken()
    {
        // 32 random bytes → base64url, no padding. ~43 chars; plenty of
        // entropy without being annoying to copy/paste.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record AppTokenInfo(string AppName, DateTime CreatedAt);
