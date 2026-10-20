using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// #385: <c>dapps reset-admin-password [--db PATH]</c>, for a sysop who
/// has forgotten the dashboard password and has a shell on the node.
/// Sets a new password straight into the database the daemon uses,
/// with the same rules as the dashboard (<see cref="AdminPasswordStore"/>),
/// and signs out every existing dashboard login. Never starts the
/// daemon, and works whether it is running or not: SQLite locks the
/// file, and this waits for the daemon's own writes to finish.
///
/// Finding the database: <c>--db</c> if given (the file, or the folder
/// dapps runs in); otherwise the working directory, by the daemon's own
/// rule (<see cref="DbInfo.PathIn"/>); otherwise the packaged installs'
/// state folders. It never creates a database.
///
/// On a TTY it asks twice without echo; otherwise it reads one line
/// from stdin, so a script can feed it from a file.
/// </summary>
public static class AdminPasswordCli
{
    public const string Command = "reset-admin-password";

    /// <summary>Where installs on Linux and macOS run dapps: the .deb's
    /// and the install script's systemd unit (and the macOS launchd
    /// example), and a packet.net node's app state folder. Windows has
    /// no standard place, so it searches only the current folder.</summary>
    internal static IReadOnlyList<string> InstalledDirectories =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? ["/var/lib/dapps", "/var/lib/packetnet/apps/dapps"]
            : [];

    private const string Usage =
        "Usage: dapps reset-admin-password [--db PATH]\n" +
        "Sets a new dashboard admin password and signs out every dashboard login.\n" +
        "Run it on the node, as root or the user dapps runs as. It finds the database\n" +
        "in the current folder or the install's state folder; --db names the database\n" +
        "file, or the folder dapps runs in. It asks for the password twice, or reads\n" +
        "one line from stdin when that isn't a terminal.";

    /// <summary>Console I/O, swappable for tests.</summary>
    internal interface ITerminal
    {
        bool IsInteractive { get; }
        TextWriter Out { get; }
        TextWriter Error { get; }
        /// <summary>Prompt and read a line without echoing it.</summary>
        string? ReadSecret(string prompt);
        string? ReadLine();
    }

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != Command) return false;
        exitCode = RunAsync(args[1..], new ConsoleTerminal(), Directory.GetCurrentDirectory(), InstalledDirectories)
            .GetAwaiter().GetResult();
        return true;
    }

    /// <summary>Exit codes: 0 set, 1 a usage or password problem
    /// (nothing changed), 2 no database or it couldn't be written.</summary>
    internal static async Task<int> RunAsync(
        string[] args, ITerminal term, string currentDirectory, IReadOnlyList<string> installedDirectories)
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
                    term.Out.WriteLine(Usage);
                    return 0;
                default:
                    term.Error.WriteLine(Usage);
                    return 1;
            }
        }

        var dbPath = FindDatabase(dbArg, currentDirectory, installedDirectories, out var findError);
        if (dbPath is null)
        {
            term.Error.WriteLine(findError);
            return 2;
        }

        string? password;
        if (term.IsInteractive)
        {
            term.Out.WriteLine($"Database: {dbPath}");
            password = term.ReadSecret("New admin password: ");
            if (AdminPasswordStore.NewPasswordError(password) is { } early)
            {
                term.Error.WriteLine($"{early} Nothing changed.");
                return 1;
            }
            if (term.ReadSecret("Same again: ") != password)
            {
                term.Error.WriteLine("Passwords don't match. Nothing changed.");
                return 1;
            }
        }
        else
        {
            password = term.ReadLine()?.TrimEnd('\r');
        }
        if (AdminPasswordStore.NewPasswordError(password) is { } error)
        {
            term.Error.WriteLine($"{error} Nothing changed.");
            return 1;
        }

        var previousOverride = DbInfo.OverridePath;
        DbInfo.OverridePath = dbPath;
        var db = DbInfo.GetAsyncConnection();
        try
        {
            // The daemon may be mid-write; wait for it rather than fail.
            await db.SetBusyTimeoutAsync(TimeSpan.FromSeconds(30));
            var hasTable = await db.ExecuteScalarAsync<int>(
                "select count(*) from sqlite_master where type='table' and name='systemoptions'");
            if (hasTable == 0)
            {
                term.Error.WriteLine($"{dbPath} isn't a DAPPS database (no systemoptions table). Nothing changed.");
                return 2;
            }

            await new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance).SetAsync(password);
        }
        catch (Exception ex) when (ex is SQLiteException or UnauthorizedAccessException or IOException)
        {
            term.Error.WriteLine($"Couldn't write {dbPath}: {ex.Message}. Run this as root (sudo) or as the user dapps runs as.");
            return 2;
        }
        finally
        {
            // Close the file before fixing ownership, and before the
            // process exits.
            await db.CloseAsync();
            DbInfo.OverridePath = previousOverride;
        }

        MatchSideFileOwnership(dbPath, term);

        term.Out.WriteLine($"Admin password set in {dbPath}. Every dashboard login is signed out; log in again with the new password.");
        return 0;
    }

    /// <summary>The database to change, or null with a reason.</summary>
    internal static string? FindDatabase(
        string? dbArg, string currentDirectory, IReadOnlyList<string> installedDirectories, out string error)
    {
        error = "";
        if (dbArg is not null)
        {
            var full = Path.GetFullPath(dbArg, currentDirectory);
            var path = Directory.Exists(full) ? DbInfo.PathIn(full) : full;
            if (File.Exists(path)) return path;
            error = $"No DAPPS database at {path}.";
            return null;
        }

        // Every candidate that exists: the current folder by the
        // daemon's rule, then the install folders. More than one, and
        // which is meant is a guess, so ask.
        var here = DbInfo.PathIn(currentDirectory);
        var found = new[] { here }.Concat(installedDirectories.Select(DbInfo.PathIn))
            .Where(File.Exists)
            .Select(p => Path.GetFullPath(p))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        switch (found.Count)
        {
            case 1:
                return found[0];
            case 0:
                error = $"No DAPPS database found in {currentDirectory} or the usual install folders. " +
                    "Run this in the folder dapps runs in, or name the database with --db PATH.";
                return null;
            default:
                error = $"Found more than one DAPPS database ({string.Join(", ", found)}). " +
                    "Name the one to change with --db PATH.";
                return null;
        }
    }

    /// <summary>
    /// Run as root, a write can leave a SQLite side file (journal, WAL)
    /// that root owns next to a database the daemon's own user owns,
    /// and the daemon then can't write its database. SQLite normally
    /// avoids that itself; this makes sure, by giving any side file
    /// still there the database file's owner.
    /// </summary>
    private static void MatchSideFileOwnership(string dbPath, ITerminal term)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess) return;
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var side = dbPath + suffix;
            if (!File.Exists(side)) continue;
            try
            {
                using var chown = Process.Start(new ProcessStartInfo("chown", ["--reference=" + dbPath, side])
                {
                    UseShellExecute = false,
                });
                chown?.WaitForExit();
                if (chown is null || chown.ExitCode != 0)
                {
                    term.Error.WriteLine($"Warning: couldn't give {side} the owner of {dbPath}; check it with ls -l.");
                }
            }
            catch (Exception ex)
            {
                term.Error.WriteLine($"Warning: couldn't give {side} the owner of {dbPath} ({ex.Message}); check it with ls -l.");
            }
        }
    }

    private sealed class ConsoleTerminal : ITerminal
    {
        public bool IsInteractive => !Console.IsInputRedirected;
        public TextWriter Out => Console.Out;
        public TextWriter Error => Console.Error;
        public string? ReadLine() => Console.In.ReadLine();

        public string? ReadSecret(string prompt)
        {
            Console.Out.Write(prompt);
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
            Console.Out.WriteLine();
            return sb.ToString();
        }
    }
}
