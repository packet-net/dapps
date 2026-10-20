namespace dapps.core.Updater;

/// <summary>
/// Command-line flags on the dapps binary that don't boot the host.
/// Recognised before <c>WebApplication.CreateBuilder</c> runs, so they work
/// even when the on-disk dapps.db is incompatible or a port is wedged:
/// <list type="bullet">
/// <item><c>--version</c> - print the version, exit 0.</item>
/// <item><c>--show-config</c> - see <see cref="ConfigCli"/>.</item>
/// </list>
/// DAPPS does not update itself: upgrade with apt, or replace the binary.
/// The old <c>--apply-update</c>, <c>--check-update</c> and <c>--rollback</c>
/// flags are only recognised so that a dapps-updater timer left over from an
/// older install (which runs <c>dapps --apply-update</c> every minute) does not
/// start a second daemon; they print a notice and do nothing.
/// </summary>
public static class DappsCli
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        switch (args[0])
        {
            case "--version":
                Console.WriteLine(dapps.core.Services.AppVersion.Current());
                return true;

            case "--show-config":
                exitCode = ConfigCli.ShowConfig(args[1..]);
                return true;

            case "--apply-update":
                // Exit 0: a leftover timer fires this every minute; do not make it a failing unit.
                Console.Error.WriteLine(RemovedNotice(args[0]));
                return true;

            case "--check-update":
            case "--rollback":
                Console.Error.WriteLine(RemovedNotice(args[0]));
                exitCode = 2;
                return true;

            default:
                return false;
        }
    }

    private static string RemovedNotice(string flag) =>
        $"dapps: {flag} has been removed; DAPPS no longer updates itself. " +
        "Upgrade with: sudo apt update && sudo apt upgrade (apt installs), or replace the binary / rebuild the Docker image. " +
        "If you still have a dapps-updater timer, remove it: sudo systemctl disable --now dapps-updater.timer";
}
