namespace dapps.core.Services.Apt;

/// <summary>
/// Whether this machine installs from packet-net's apt repository, as docs/install/linux.md sets it up
/// (<c>/etc/apt/sources.list.d/packet-net.list</c>), or had the .deb installed by hand.
/// </summary>
public static class AptSources
{
    /// <summary>The repository's host, which any entry for it names.</summary>
    public const string RepoHost = "packet-net.github.io";

    /// <summary>Where apt keeps its sources.</summary>
    public const string SourcesDirectory = "/etc/apt/sources.list.d";

    /// <summary>
    /// Whether any of <paramref name="files"/> (each file's text) has a live entry naming
    /// <see cref="RepoHost"/>: a one-line <c>deb</c> entry, or a deb822 <c>URIs:</c> field, not a
    /// comment, and not in a deb822 stanza marked <c>Enabled: no</c>.
    /// </summary>
    public static bool NamesRepo(IEnumerable<string> files)
    {
        foreach (string text in files)
        {
            foreach (string stanza in text.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
            {
                var lines = stanza.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                bool disabled = lines.Any(l => l.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase)
                    && l["Enabled:".Length..].Trim().Equals("no", StringComparison.OrdinalIgnoreCase));
                if (!disabled && lines.Any(l => l.Contains(RepoHost, StringComparison.OrdinalIgnoreCase)
                    && (l.StartsWith("deb ", StringComparison.Ordinal) || l.StartsWith("deb\t", StringComparison.Ordinal)
                        || l.StartsWith("URIs:", StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The check against this machine's own apt sources: every file in <see cref="SourcesDirectory"/>. Never throws.</summary>
    public static bool ThisMachine()
    {
        try
        {
            if (!Directory.Exists(SourcesDirectory))
            {
                return false;
            }
            var texts = Directory.EnumerateFiles(SourcesDirectory)
                .Where(f => f.EndsWith(".list", StringComparison.Ordinal) || f.EndsWith(".sources", StringComparison.Ordinal))
                .Select(f =>
                {
                    try
                    {
                        return File.ReadAllText(f);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return "";
                    }
                });
            return NamesRepo(texts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
