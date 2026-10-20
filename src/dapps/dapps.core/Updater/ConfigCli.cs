using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using SQLite;

namespace dapps.core.Updater;

/// <summary>
/// Plan B7 follow-up - side-door <c>--show-config</c> subcommand.
/// Reads the on-disk <c>data/dapps.db</c> directly (no DI, no host)
/// and prints the persisted systemoptions rows in
/// <c>DAPPS_SCREAMING_SNAKE=value</c> form so a sysop can confirm what
/// the daemon would resolve at boot - useful when /Config has been
/// edited live and the persisted values diverge from the
/// <c>EnvironmentFile</c> the operator thinks is authoritative.
///
/// Exits 0 on success, 1 if the DB doesn't exist, 2 on any other
/// failure. Like the rest of <see cref="DappsCli"/>, runs before
/// host plumbing and works even when the daemon won't boot for an
/// unrelated reason.
/// </summary>
internal static class ConfigCli
{
    public static int ShowConfig()
    {
        try
        {
            using var db = DbInfo.GetConnection();
            // Defensive - CreateTable is CREATE-IF-NOT-EXISTS so this
            // is a no-op on a real node, but it stops a fresh checkout
            // crashing when nothing has booted dapps yet.
            db.CreateTable<DbSystemOption>();
            var rows = db.Query<DbSystemOption>("select * from systemoptions order by Option");
            if (rows.Count == 0)
            {
                Console.WriteLine("# (no rows in systemoptions - daemon has never booted)");
                return 0;
            }

            Console.WriteLine($"# resolved config from {db.DatabasePath}");
            foreach (var row in rows)
            {
                Console.WriteLine($"DAPPS_{ToScreamingSnake(row.Option)}={row.Value}");
            }
            return 0;
        }
        catch (SQLiteException ex) when (ex.Message.Contains("no such table"))
        {
            Console.Error.WriteLine($"show-config: systemoptions table missing (is {DbInfo.OverridePath ?? "data/dapps.db"} a dapps DB?): {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"show-config failed: {ex.Message}");
            return 2;
        }
    }

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
