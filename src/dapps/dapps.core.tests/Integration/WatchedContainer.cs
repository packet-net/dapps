using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// One of a fixture's containers, watched from when it starts: if it stops
/// by itself, <see cref="Exited"/> completes with its exit code. linbpq has
/// been seen to exit about 10 s after its container starts, and nothing
/// gets through that node after that (#201). A container the fixture stops
/// itself isn't reported.
/// </summary>
internal sealed class WatchedContainer(string name, IContainer container) : IAsyncDisposable
{
    private readonly CancellationTokenSource watching = new();
    private readonly TaskCompletionSource<long> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime stoppedAt;

    /// <summary>What it is, for reports: e.g. "BPQ N0BBB".</summary>
    public string Name => name;

    public IContainer Container => container;

    public DateTime Started { get; private set; }

    /// <summary>Completes with the exit code if it stops by itself; never otherwise.</summary>
    public Task<long> Exited => exited.Task;

    /// <summary>E.g. "BPQ N0BBB exited with code 1, 11 s after it started", or null while it runs.</summary>
    public string? Died => exited.Task.IsCompletedSuccessfully
        ? $"{name} exited with code {exited.Task.Result}{Meaning(exited.Task.Result)}, {(stoppedAt - Started).TotalSeconds:F0} s after it started"
        : null;

    /// <summary>Start it and watch it; a container that doesn't start is
    /// removed, and the exception carries the end of its log.</summary>
    public async Task StartAsync()
    {
        try
        {
            await container.StartAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var log = await LogTailAsync(80);
            await container.DisposeAsync();
            throw new InvalidOperationException($"{name} didn't start: {e.Message}\n{log}", e);
        }
        Started = DateTime.UtcNow;
        _ = WatchAsync();
    }

    private async Task WatchAsync()
    {
        try
        {
            // Docker's wait: returns when the container stops.
            var code = await container.GetExitCodeAsync(watching.Token);
            stoppedAt = DateTime.UtcNow;
            exited.TrySetResult(code);
        }
        catch (Exception)
        {
            // Stopped watching (the fixture is stopping it), or Docker went away.
        }
    }

    /// <summary>
    /// The last <paramref name="lines"/> of its log, headed with whether it
    /// is running. linbpq writes a backtrace there when it dies of SIGSEGV
    /// or SIGABRT, then exits with code 1.
    /// </summary>
    public async Task<string> LogTailAsync(int lines)
    {
        var state = Died ?? $"{name}, running";
        try
        {
            // Both streams, in the order Docker stamped them.
            var (stdout, stderr) = await container.GetLogsAsync(timestampsEnabled: true);
            var all = (stdout + "\n" + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(Stamp, StringComparer.Ordinal)
                .ToList();
            return $"--- {state}: the last {Math.Min(lines, all.Count)} of {all.Count} log lines ---\n" + string.Join('\n', all.TakeLast(lines));
        }
        catch (Exception e)
        {
            return $"--- {state}: no log ({e.Message}) ---";
        }
    }

    /// <summary>A log line's Docker timestamp (e.g. 2026-09-29T08:01:02.12Z),
    /// its fraction padded to 9 digits so the stamps sort as text.</summary>
    private static string Stamp(string line)
    {
        var stamp = line.Split(' ', 2)[0];
        var dot = stamp.IndexOf('.');
        return dot < 0 || !stamp.EndsWith('Z') ? stamp : stamp[..dot] + "." + stamp[(dot + 1)..^1].PadRight(9, '0') + "Z";
    }

    private static string Meaning(long code) => code switch
    {
        134 => " (SIGABRT)",
        137 => " (SIGKILL: out of memory, or killed)",
        139 => " (SIGSEGV)",
        141 => " (SIGPIPE)",
        _ => "",
    };

    public async ValueTask DisposeAsync()
    {
        await watching.CancelAsync();
        try
        {
            await container.DisposeAsync();
        }
        finally
        {
            watching.Dispose();
        }
    }
}
