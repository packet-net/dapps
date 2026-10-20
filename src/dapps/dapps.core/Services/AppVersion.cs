using System.Reflection;

namespace dapps.core.Services;

/// <summary>The version baked into the running binary.</summary>
public static class AppVersion
{
    /// <summary>
    /// MSBuild's <c>&lt;Version&gt;</c>, without the trailing build metadata
    /// .NET appends for some publish modes ("0.8.0+abcd1234" becomes "0.8.0").
    /// </summary>
    public static string Current()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
