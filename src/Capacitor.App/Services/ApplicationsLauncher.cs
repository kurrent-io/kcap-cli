using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

internal sealed class ApplicationsLauncher(IProcessRunner runner, Func<string, bool?> isRunning, TimeProvider time) {
    internal async Task<bool> OpenAsync(string path, CancellationToken ct) {
        var running = isRunning(path);
        if (running is null) return false;
        // Without -n, Launch Services can activate this disk-image process instead of the installed copy.
        string[] args = running.Value ? [path] : ["-n", path];
        var result = await runner.RunAsync("open", args, new RunOptions(Timeout: TimeSpan.FromSeconds(10)), ct);
        if (result.ExitCode != 0 || result.TimedOut) return false;

        var deadline = TimeSpan.FromSeconds(10);
        var started = time.GetTimestamp();
        do {
            ct.ThrowIfCancellationRequested();
            if (isRunning(path) == true) return true;
            if (time.GetElapsedTime(started) >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct);
        } while (true);
    }
}
