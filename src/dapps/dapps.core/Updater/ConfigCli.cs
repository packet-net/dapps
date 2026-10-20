using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using SQLite;

namespace dapps.core.Updater;

/// <summary>
/// Plan B7 follow-up - side-door <c>--show-config [--db PATH]</c>
/// subcommand. Reads the on-disk database directly (no DI, no host)
/// and prints the persisted systemoptions rows in
/// <c>DAPPS_SCREAMING_SNAKE=value</c> form so a sysop can confirm what
/// the daemon would resolve at boot - useful when /Config has been
/// edited live and the persisted values diverge from the
/// <c>EnvironmentFile</c> the operator thinks is authoritative.
///
/// Finds the database the way <c>reset-admin-password</c> does
/// (<see cref="AdminPasswordCli.FindDatabase"/>): <c>--db</c>, else the
/// working directory, else the packaged installs' state folders, and
/// asks for <c>--db</c> when more than one exists. Opens it read-only
/// and never creates one. Secrets (the admin password hash and salt,
/// the RHP password, MeshCore keys) are printed as "(set, hidden)" so
/// the output is safe to paste into a support thread (#320).
///
/// Exits 0 on success, 1 if there is no database, 2 on any other
/// failure. Like the rest of <see cref="DappsCli"/>, runs before
/// host plumbing and works even when the daemon won't boot for an
/// unrelated reason.
/// </summary>
internal static class ConfigCli
{
    private const string Usage =
        "Usage: dapps --show-config [--db PATH]\n" +
        "Prints the saved settings. It finds the database in the current folder or the\n" +
        "install's state folder; --db names the database file, or the folder dapps runs in.\n" +
        "Secrets are hidden. The state folder is private to the dapps user, so on a packaged\n" +
        "install run it as that user: sudo -u dapps dapps --show-config";

    public static int ShowConfig(string[] args) =>
        ShowConfig(args, Directory.GetCurrentDirectory(), AdminPasswordCli.InstalledDirectories, Console.Out, Console.Error);

    internal static int ShowConfig(
        string[] args, string currentDirectory, IReadOnlyList<string> installedDirectories,
        TextWriter stdout, TextWriter stderr)
    {
        string? dbArg = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbArg = args[++i];
                    break;
                case "-h" or "--help":
                    stdout.WriteLine(Usage);
                    return 0;
                default:
                    stderr.WriteLine(Usage);
                    return 1;
            }
        }

        var dbPath = AdminPasswordCli.FindDatabase(dbArg, currentDirectory, installedDirectories, out var findError);
        if (dbPath is null)
        {
            stderr.WriteLine($"show-config: {findError}");
            return 1;
        }

        try
        {
            using var db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadOnly);
            var rows = db.Query<DbSystemOption>("select * from systemoptions order by Option");
            if (rows.Count == 0)
            {
                stdout.WriteLine($"# (no rows in systemoptions in {dbPath} - daemon has never booted)");
                return 0;
            }

            stdout.WriteLine($"# resolved config from {dbPath}");
            foreach (var row in rows)
            {
                stdout.WriteLine($"DAPPS_{ToScreamingSnake(row.Option)}={Display(row)}");
            }
            return 0;
        }
        catch (SQLiteException ex) when (ex.Message.Contains("no such table"))
        {
            stderr.WriteLine($"show-config: {dbPath} isn't a DAPPS database (no systemoptions table).");
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"show-config: couldn't read {dbPath}: {ex.Message}. " +
                "The state folder is private to the dapps user; try: sudo -u dapps dapps --show-config");
            return 2;
        }
    }

    /// <summary>Options holding a password, key or hash; their values
    /// are never printed.</summary>
    internal static bool IsSecret(string option) =>
        new[] { "Hash", "Salt", "Pass", "Password", "Psk", "Key", "Token", "Secret" }
            .Any(suffix => option.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string Display(DbSystemOption row) =>
        IsSecret(row.Option) ? (string.IsNullOrEmpty(row.Value) ? "" : "(set, hidden)") : row.Value;

    private static string ToScreamingSnake(string identifier)
    {
        var sb = new StringBuilder(identifier.Length + 4);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
