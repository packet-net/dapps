using System.Globalization;

namespace dapps.core.Services.Apt;

/// <summary>
/// Debian's version ordering, as dpkg and apt use it: so DAPPS says a newer version is
/// ready exactly when <c>apt install --only-upgrade</c> would take it.
/// </summary>
public static class DebianVersion
{
    /// <summary>
    /// The version without a local build's <c>+...</c> suffix (the .NET SDK adds <c>+</c> and the
    /// git commit to a build's informational version), surrounding space removed.
    /// </summary>
    public static string WithoutBuild(string version)
    {
        string v = version.Trim();
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? v : v[..plus];
    }

    /// <summary>Whether <paramref name="version"/> is one dpkg would accept: [epoch:]upstream[-revision], the upstream starting with a digit.</summary>
    public static bool IsValid(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }
        var (epoch, upstream, revision) = Split(version);
        if (epoch is null || upstream.Length == 0 || !char.IsAsciiDigit(upstream[0]))
        {
            return false;
        }
        bool hasRevision = revision.Length > 0 || version.Contains('-', StringComparison.Ordinal);
        foreach (char c in upstream)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~' || (c == '-' && hasRevision)))
            {
                return false;
            }
        }
        if (hasRevision && revision.Length == 0)
        {
            return false;
        }
        foreach (char c in revision)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compares two versions as dpkg does: negative when <paramref name="a"/> is older, 0 when equal, positive when newer.</summary>
    public static int Compare(string a, string b)
    {
        var (ea, ua, ra) = Split(a);
        var (eb, ub, rb) = Split(b);
        int byEpoch = (ea ?? 0).CompareTo(eb ?? 0);
        if (byEpoch != 0)
        {
            return byEpoch;
        }
        int byUpstream = Part(ua, ub);
        return byUpstream != 0 ? byUpstream : Part(ra, rb);
    }

    /// <summary>The epoch (null when it is not a number), the upstream version and the revision.</summary>
    private static (long? Epoch, string Upstream, string Revision) Split(string version)
    {
        string v = version.Trim();
        long? epoch = 0;
        int colon = v.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            epoch = long.TryParse(v.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out long e) ? e : null;
            v = v[(colon + 1)..];
        }
        int dash = v.LastIndexOf('-');
        return dash < 0 ? (epoch, v, "") : (epoch, v[..dash], v[(dash + 1)..]);
    }

    /// <summary>dpkg's verrevcmp: runs of non-digits by <see cref="Order"/>, then runs of digits as numbers.</summary>
    private static int Part(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                int ac = Order(a, i), bc = Order(b, j);
                if (ac != bc)
                {
                    return ac - bc;
                }
                i++;
                j++;
            }
            while (i < a.Length && a[i] == '0')
            {
                i++;
            }
            while (j < b.Length && b[j] == '0')
            {
                j++;
            }
            int firstDiff = 0;
            while (i < a.Length && j < b.Length && char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                if (firstDiff == 0)
                {
                    firstDiff = a[i] - b[j];
                }
                i++;
                j++;
            }
            if (i < a.Length && char.IsAsciiDigit(a[i]))
            {
                return 1;
            }
            if (j < b.Length && char.IsAsciiDigit(b[j]))
            {
                return -1;
            }
            if (firstDiff != 0)
            {
                return firstDiff;
            }
        }
        return 0;
    }

    /// <summary>
    /// A character's weight: the end of the string and digits 0, letters their code, other
    /// characters after all letters, and <c>~</c> before everything, even the end.
    /// </summary>
    private static int Order(string s, int i)
    {
        if (i >= s.Length)
        {
            return 0;
        }
        char c = s[i];
        return char.IsAsciiDigit(c) ? 0 : char.IsAsciiLetter(c) ? c : c == '~' ? -1 : c + 256;
    }
}
