using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using dapps.client;
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

    /// <summary>Raised with the app name (as passed in, which callers have
    /// normalised) after its token is issued, rotated or revoked. The MQTT
    /// broker listens and drops that app's live connections, matching the
    /// name case-insensitively.</summary>
    public event Action<string>? TokenChanged;

    /// <summary>
    /// Generate a fresh token for <paramref name="appName"/>, store its
    /// hash, and return the plaintext for the caller to hand to the app
    /// owner. Re-issuing for an existing app rotates: the prior token
    /// stops verifying straight away, so REST calls using it fail on
    /// their very next request. The broker only checks the token at
    /// CONNECT, so it listens for <see cref="TokenChanged"/> and drops
    /// the app's already-connected MQTT clients; they must reconnect
    /// with the new token.
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
        // Every row for this name, whatever its case: databases from before
        // names were normalised can hold both "Chat" and "chat".
        await connection.ExecuteAsync("delete from apptokens where AppName = ? collate nocase", appName);

        await connection.InsertAsync(new DbAppToken
        {
            AppName = appName,
            TokenHash = Convert.ToHexString(hash),
            Salt = Convert.ToHexString(salt),
            TokenId = TokenIdOf(plaintext),
            CreatedAt = DateTime.UtcNow,
        });

        logger.LogInformation("Issued token for app {0}", appName);
        TokenChanged?.Invoke(appName);
        return plaintext;
    }

    /// <summary>
    /// Returns the app name the token belongs to, or null. Costs at most
    /// one PBKDF2 per call, and none once a token has been verified: a
    /// presented token's SHA-256 (<see cref="DbAppToken.TokenId"/>)
    /// selects its one row, and a success is remembered (keyed by id, and
    /// only valid while the row still holds the same hash, so revoking or
    /// rotating - even from another store instance - takes effect on the
    /// next call).
    ///
    /// Rows minted before the id column existed have none. Their
    /// plaintext is not stored, so they cannot be filled in up front;
    /// instead a token that matches no id is checked against just those
    /// legacy rows with PBKDF2, and the matching row gets its id then.
    /// Existing tokens therefore keep working with no reissue, and each
    /// pays the slow path once.
    /// </summary>
    public async Task<string?> VerifyAsync(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var connection = DbInfo.GetAsyncConnection();
        var id = TokenIdOf(plaintext);

        var row = (await connection.QueryAsync<DbAppToken>(
            "select * from apptokens where TokenId = ? limit 1", id)).FirstOrDefault();
        if (row is not null)
        {
            var fingerprint = row.TokenHash + row.Salt;
            if (verified.TryGetValue(id, out var known) && known == fingerprint)
                return row.AppName;
            if (Matches(plaintext, row))
            {
                verified[id] = fingerprint;
                return row.AppName;
            }
            return null;
        }

        var legacy = await connection.QueryAsync<DbAppToken>(
            "select * from apptokens where TokenId is null or TokenId = ''");
        foreach (var candidate in legacy)
        {
            if (!Matches(plaintext, candidate)) continue;
            await connection.ExecuteAsync(
                "update apptokens set TokenId = ? where AppName = ? and TokenHash = ?",
                id, candidate.AppName, candidate.TokenHash);
            verified[id] = candidate.TokenHash + candidate.Salt;
            return candidate.AppName;
        }
        return null;
    }

    /// <summary>Apps whose token row has no lookup id yet (issued before
    /// the id existed and not presented since).</summary>
    public async Task<IReadOnlyList<string>> LegacyAppNamesAsync()
    {
        var rows = await DbInfo.GetAsyncConnection().QueryAsync<DbAppToken>(
            "select * from apptokens where TokenId is null or TokenId = ''");
        return rows.Select(r => r.AppName).ToList();
    }

    private readonly ConcurrentDictionary<string, string> verified = new();

    private static bool Matches(string plaintext, DbAppToken row)
    {
        var attempt = Pbkdf2(plaintext, Convert.FromHexString(row.Salt));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.TokenHash), attempt);
    }

    private static string TokenIdOf(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>Remove a token, matching <paramref name="appName"/>
    /// case-insensitively against whatever's stored (see
    /// <see cref="CreateOrRotateAsync"/>). Returns true if a row was
    /// deleted. Immediate for REST, and the app's already-connected MQTT
    /// clients are dropped (see <see cref="CreateOrRotateAsync"/>).</summary>
    public async Task<bool> RevokeAsync(string appName)
    {
        var connection = DbInfo.GetAsyncConnection();
        var deleted = await connection.ExecuteAsync(
            "delete from apptokens where AppName = ? collate nocase", appName);
        TokenChanged?.Invoke(appName);
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
    /// rejected for the same reason nothing else accepts them, and '='
    /// because the name goes in the <c>dst=</c> token on the air. Lower-
    /// cases the result because MQTT CONNECT and the REST bearer check
    /// both compare app names case-insensitively already (see
    /// <see cref="MqttBrokerService"/> and <see cref="BearerAuthMiddleware"/>) -
    /// "chat" and "Chat" are the same app, so this store should only
    /// ever mint one token for either spelling.
    /// </summary>
    public static bool TryNormalizeAppName(string? raw, out string normalized, out string? error)
    {
        // The same rule the app interface applies to the app a message
        // is sent from or to (MessageFields, #313), so a token is only
        // ever minted for an app that can actually send.
        var trimmed = (raw ?? "").Trim();
        if (MessageFields.AppNameError(trimmed) is { } invalid)
        {
            normalized = "";
            error = invalid;
            return false;
        }

        normalized = trimmed.ToLowerInvariant();
        error = null;
        return true;
    }

    private static byte[] Pbkdf2(string plaintext, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(plaintext, salt, Iterations, Hash, HashBytes);
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
