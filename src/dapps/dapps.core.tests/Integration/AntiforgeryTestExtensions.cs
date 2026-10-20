using System.Text.RegularExpressions;

namespace dapps.core.tests.Integration;

/// <summary>
/// The admin REST endpoints refuse state changes without an antiforgery
/// token (#320). A test acting as the dashboard reads the token from a
/// page the way the dashboard's own script does, and sends it as the
/// <c>RequestVerificationToken</c> header on every request after.
/// </summary>
public static class AntiforgeryTestExtensions
{
    public const string HeaderName = "RequestVerificationToken";

    /// <summary>Fetch <paramref name="page"/> (signed in) and send the
    /// token it carries on every later request from this client.</summary>
    public static async Task UseAntiforgeryTokenAsync(this HttpClient http, string page = "/Settings", CancellationToken ct = default)
    {
        var token = await http.GetAntiforgeryTokenAsync(page, ct);
        http.DefaultRequestHeaders.Remove(HeaderName);
        http.DefaultRequestHeaders.Add(HeaderName, token);
    }

    public static async Task<string> GetAntiforgeryTokenAsync(this HttpClient http, string page = "/Settings", CancellationToken ct = default)
    {
        var body = await http.GetStringAsync(page, ct);
        var token = Parse(body);
        if (token.Length == 0) throw new InvalidOperationException($"no antiforgery token on {page}");
        return token;
    }

    public static string Parse(string html)
    {
        var meta = Regex.Match(html, "<meta name=\"request-verification-token\" content=\"([^\"]+)\"");
        if (meta.Success) return System.Net.WebUtility.HtmlDecode(meta.Groups[1].Value);
        return System.Net.WebUtility.HtmlDecode(
            Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
    }
}
