using System.ComponentModel;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// Brings every kcap the local installer would refuse up to <see cref="LocalClientFloor"/> before the install starts,
/// so the user is never asked to: an npm install is updated through npm, and anything else on the terminal's
/// <c>PATH</c> becomes a link to this app's own kcap, which always meets the floor.
/// </summary>
public sealed class StaleClientRepair(
        IProcessRunner runner, ILoginShellProbe probe, string? appCli, Func<string?, ILocalClientCopies> copies) {
    const string OsascriptPath = "/usr/bin/osascript";
    const string NpmPackage    = "@kurrent/kcap";

    static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(5);

    /// Each repair fixes the copy PATH finds first, which can uncover another behind it.
    const int MaxRepairs = 4;

    public static StaleClientRepair ForApp(IProcessRunner runner, ILoginShellProbe probe, string? appCli, UserHome home) =>
        new(runner, probe, appCli, path => new LocalClientCopies(runner, path, appCli, LocalClientCopies.AppClis(home)));

    public async Task<ClientRepairOutcome> RunAsync(CancellationToken ct) {
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false) ?? Environment.GetEnvironmentVariable("PATH");
        var npmTried = new HashSet<string>(StringComparer.Ordinal);
        var relinked = new HashSet<string>(StringComparer.Ordinal);
        string? repaired = null;

        for (var attempt = 0; attempt < MaxRepairs; attempt++) {
            var stale = await Task.Run(() => copies(path).Find().FirstOrDefault(c => !LocalClientFloor.Admits(c.Version)), ct)
                .ConfigureAwait(false);

            if (stale is null) return repaired is null ? new ClientRepairOutcome.Clear() : new ClientRepairOutcome.Repaired(repaired);
            if (stale.Kind == ClientCopyKind.DesktopApp) return new ClientRepairOutcome.OldApp(stale.Path);
            if (relinked.Contains(stale.Path)) return new ClientRepairOutcome.Failed(stale.Path, "it still reports an older version after it was replaced.");

            if (NpmOwned(stale.Path) && npmTried.Add(stale.Path) && await UpdateThroughNpmAsync(path, ct).ConfigureAwait(false)) {
                repaired = stale.Path;
                continue;
            }

            if (await RelinkAsync(stale.Path, ct).ConfigureAwait(false) is { } refused) return refused;

            relinked.Add(stale.Path);
            repaired = stale.Path;
        }

        return new ClientRepairOutcome.Failed(repaired ?? "", "more older copies of kcap turned up than setup can replace.");
    }

    static bool NpmOwned(string path) =>
        CanonicalPath.Resolve(path).Contains($"/node_modules/{NpmPackage}/", StringComparison.Ordinal);

    async Task<bool> UpdateThroughNpmAsync(string? searchPath, CancellationToken ct) {
        if (BinaryProbe.Searching(searchPath).Resolve("npm") is not { } npm) return false;

        // npm is a node script, so node has to resolve on the same PATH.
        var env = searchPath is null ? null : new Dictionary<string, string> { ["PATH"] = searchPath };
        try {
            var result = await runner.RunAsync(npm, ["install", "--global", $"{NpmPackage}@latest"], new RunOptions(env, NpmTimeout), ct)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false };
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return false;
        }
    }

    /// Null once <paramref name="link"/> runs this app's kcap.
    async Task<ClientRepairOutcome?> RelinkAsync(string link, CancellationToken ct) {
        if (appCli is null || !OneLine(appCli) || !OneLine(link))
            return new ClientRepairOutcome.Failed(link, "this app has no kcap of its own to put there.");

        if (TryReplace(link, appCli)) return null;

        ProcessResult result;
        try {
            result = await runner.RunAsync(OsascriptPath, OsascriptArgs(appCli, link), new RunOptions(), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return new ClientRepairOutcome.Failed(link, ex.Message);
        }

        if (result.ExitCode == 0) return null;
        if (result.Stderr.Contains("(-128)", StringComparison.Ordinal)) return new ClientRepairOutcome.Cancelled(link);

        return new ClientRepairOutcome.Failed(link, string.IsNullOrWhiteSpace(result.Stderr) ? "osascript failed." : result.Stderr.Trim());
    }

    /// A line break would desynchronise osascript's argv from the script reading it.
    static bool OneLine(string path) => !path.Contains('\n') && !path.Contains('\r');

    /// Without an administrator prompt, where the folder is this user's to write: a link made beside the old copy is
    /// renamed over it, so PATH never goes without a kcap.
    static bool TryReplace(string link, string target) {
        var temp = Path.Combine(Path.GetDirectoryName(link)!, $".kcap-{Guid.NewGuid():N}");
        try {
            File.CreateSymbolicLink(temp, target);
            File.Move(temp, link, overwrite: true);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// Both paths travel as argv and are read back through <c>quoted form</c>, never spliced into the script.
    internal static string[] OsascriptArgs(string target, string link) => [
        "-e", "on run argv",
        "-e", "do shell script \"ln -sfh \" & quoted form of item 1 of argv & \" \" & quoted form of item 2 of argv with administrator privileges",
        "-e", "end run",
        "--", target, link,
    ];
}
