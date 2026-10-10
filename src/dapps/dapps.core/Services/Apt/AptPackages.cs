using System.Runtime.InteropServices;

namespace dapps.core.Services.Apt;

/// <summary>One package in an apt repository's <c>Packages</c> index.</summary>
public sealed record AptPackage(string Name, string Version, string Architecture, string? Filename);

/// <summary>
/// Reads the <c>Packages</c> index of a flat apt repository, such as packet-net's at
/// https://packet-net.github.io/apt/: stanzas of <c>Field: value</c> lines, a blank line
/// between them.
/// </summary>
public static class AptPackages
{
    /// <summary>The package DAPPS ships as.</summary>
    public const string DappsPackage = "dapps";

    /// <summary>
    /// Every stanza with a Package, a valid Version and an Architecture. Throws
    /// <see cref="FormatException"/> when the text has no such stanza at all, which means it is
    /// not a Packages file (an error page, say).
    /// </summary>
    public static IReadOnlyList<AptPackage> Parse(string text)
    {
        var packages = new List<AptPackage>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool sawPackage = false;

        void Finish()
        {
            if (fields.TryGetValue("Package", out var name))
            {
                sawPackage = true;
                if (fields.TryGetValue("Version", out var version) && DebianVersion.IsValid(version)
                    && fields.TryGetValue("Architecture", out var arch))
                {
                    packages.Add(new AptPackage(name, version, arch, fields.GetValueOrDefault("Filename")));
                }
            }
            fields.Clear();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                Finish();
                continue;
            }
            if (line[0] is ' ' or '\t')
            {
                continue; // a continuation line (a description), which nothing here reads
            }
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        Finish();
        if (!sawPackage)
        {
            throw new FormatException("no packages in it");
        }
        return packages;
    }

    /// <summary>The newest <paramref name="name"/> for <paramref name="architecture"/>, or null when there is none.</summary>
    public static AptPackage? Newest(IEnumerable<AptPackage> packages, string name, string architecture)
    {
        AptPackage? best = null;
        foreach (var p in packages)
        {
            if (p.Name == name && (p.Architecture == architecture || p.Architecture == "all")
                && (best is null || DebianVersion.Compare(p.Version, best.Version) > 0))
            {
                best = p;
            }
        }
        return best;
    }

    /// <summary>Debian's name for this machine's architecture (amd64, arm64 or armhf), or null for one packet-net does not build for.</summary>
    public static string? ThisArchitecture() => Architecture(RuntimeInformation.ProcessArchitecture);

    /// <summary>Debian's name for <paramref name="architecture"/>, as the release builds name the .debs.</summary>
    public static string? Architecture(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "armhf",
        _ => null,
    };
}
