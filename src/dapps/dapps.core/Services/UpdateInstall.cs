using dapps.core.Services.Apt;

namespace dapps.core.Services;

/// <summary>How this DAPPS was installed, as far as updating it goes.</summary>
public enum InstallKind
{
    /// <summary>The .deb, from packet-net's apt repository: <c>apt upgrade</c> brings new versions.</summary>
    AptRepo,

    /// <summary>The .deb, installed by hand: a new version is a new .deb to download.</summary>
    Deb,

    /// <summary>Anything else: the one-liner installer, Docker, a pdn node, a dev build.</summary>
    Other,
}

/// <summary>
/// How this DAPPS was installed and Debian's name for this machine's
/// architecture (null when packet-net builds no .deb for it).
/// </summary>
public sealed record UpdateInstall(InstallKind Kind, string? Architecture)
{
    /// <summary>Where the .deb puts the payload; nothing else installs DAPPS there.</summary>
    public const string DebPayloadDirectory = "/usr/lib/dapps";

    public bool IsDeb => Kind is InstallKind.AptRepo or InstallKind.Deb;

    /// <summary>This process: a .deb install when it runs from <see cref="DebPayloadDirectory"/>.</summary>
    public static UpdateInstall Detect() =>
        new(Classify(AppContext.BaseDirectory, AptSources.ThisMachine), AptPackages.ThisArchitecture());

    /// <summary>
    /// A .deb install when <paramref name="baseDirectory"/> is
    /// <see cref="DebPayloadDirectory"/>; from the apt repository when
    /// <paramref name="fromAptRepo"/> also says the apt sources name it.
    /// </summary>
    public static InstallKind Classify(string baseDirectory, Func<bool> fromAptRepo)
    {
        if (!IsDebPayload(baseDirectory))
        {
            return InstallKind.Other;
        }
        return fromAptRepo() ? InstallKind.AptRepo : InstallKind.Deb;
    }

    /// <summary>Whether <paramref name="baseDirectory"/> is the .deb's payload directory.</summary>
    public static bool IsDebPayload(string baseDirectory) =>
        string.Equals(baseDirectory.TrimEnd('/'), DebPayloadDirectory, StringComparison.Ordinal);
}
