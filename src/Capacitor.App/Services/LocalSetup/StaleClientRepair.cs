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

    /// Each repair fixes the copy PATH finds first, which can uncover another behind it, and an entry that changed
    /// before it could be replaced is looked at again.
    const int MaxAttempts = 6;

    /// Printed by the administrator script when the entry is no longer the one that was found.
    const string EntryChanged = "kcap-entry-changed";

    public static StaleClientRepair ForApp(IProcessRunner runner, ILoginShellProbe probe, string? appCli, UserHome home) =>
        new(runner, probe, appCli, path => new LocalClientCopies(runner, path, appCli, LocalClientCopies.AppClis(home)));

    public async Task<ClientRepairOutcome> RunAsync(CancellationToken ct) {
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false) ?? Environment.GetEnvironmentVariable("PATH");
        var npmTried = new HashSet<string>(StringComparer.Ordinal);
        var relinked = new HashSet<string>(StringComparer.Ordinal);
        string? repaired = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++) {
            var stale = await FindStaleAsync(path, ct).ConfigureAwait(false);

            if (stale is null) return repaired is null ? new ClientRepairOutcome.Clear() : new ClientRepairOutcome.Repaired(repaired);
            if (stale.Kind == ClientCopyKind.DesktopApp) return new ClientRepairOutcome.OldApp(stale.Path);
            if (relinked.Contains(stale.Path)) return new ClientRepairOutcome.Failed(stale.Path, "it still reports an older version after it was replaced.");

            // Found again either way: npm may have left anything at that entry, updated or not.
            if (NpmPrefix(stale.Path) is { } prefix && npmTried.Add(stale.Path)) {
                if (await UpdateThroughNpmAsync(prefix, path, ct).ConfigureAwait(false)) repaired = stale.Path;
                continue;
            }

            if (await BoundIdentityAsync(path, stale.Path, ct).ConfigureAwait(false) is not { } found) continue;

            switch (await RelinkAsync(stale.Path, found, ct).ConfigureAwait(false)) {
                case Relink.Changed:
                    continue;
                case Relink.Refused(var refused):
                    return refused;
            }

            relinked.Add(stale.Path);
            repaired = stale.Path;
        }

        return new ClientRepairOutcome.Failed(repaired ?? "", "more older copies of kcap turned up than setup can replace.");
    }

    Task<ClientCopy?> FindStaleAsync(string? path, CancellationToken ct) =>
        Task.Run(() => copies(path).Find().FirstOrDefault(c => !LocalClientFloor.Admits(c.Version)), ct);

    /// The entry's identity, read before a probe that still finds it below the floor, so the version that probe reports
    /// belongs to the entry the identity describes: the replacement checks against it.
    async Task<string?> BoundIdentityAsync(string? path, string entry, CancellationToken ct) {
        if (await IdentityAsync(entry, ct).ConfigureAwait(false) is not { } before) return null;

        return await FindStaleAsync(path, ct).ConfigureAwait(false) is { } stale && stale.Path == entry ? before : null;
    }

    abstract record Relink {
        public sealed record Done : Relink;
        public sealed record Changed : Relink;
        public sealed record Refused(ClientRepairOutcome Outcome) : Relink;
    }

    /// The entry itself, not what it points at: device, inode, and modification and change times. Null when it
    /// cannot be read, which includes an entry that is gone.
    async Task<string?> IdentityAsync(string entry, CancellationToken ct) {
        try {
            var result = await runner.RunAsync(StatPath, [.. StatArgs, entry], new RunOptions(Timeout: TimeSpan.FromSeconds(5)), ct)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false } ? result.Stdout.Trim() : null;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return null;
        }
    }

    /// The global prefix of the npm install <paramref name="entry"/> runs, read from where its package lives; null when
    /// npm does not own it.
    static string? NpmPrefix(string entry) {
        var package = CanonicalPath.Resolve(entry);
        var at      = package.IndexOf($"/lib/node_modules/{NpmPackage}/", StringComparison.Ordinal);

        return at > 0 ? package[..at] : null;
    }

    /// Runs the npm of the prefix that owns the entry: another npm earlier on PATH would update a different install
    /// and leave this one older.
    async Task<bool> UpdateThroughNpmAsync(string prefix, string? searchPath, CancellationToken ct) {
        var bin = Path.Combine(prefix, "bin");
        if (BinaryProbe.Searching(bin).Resolve("npm") is not { } npm) return false;

        // npm is a node script, and its own prefix is where a node install keeps node.
        var env = new Dictionary<string, string> { ["PATH"] = string.IsNullOrEmpty(searchPath) ? bin : $"{bin}{Path.PathSeparator}{searchPath}" };
        try {
            var result = await runner.RunAsync(npm, ["install", "--global", $"{NpmPackage}@latest"], new RunOptions(env, NpmTimeout), ct)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false };
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return false;
        }
    }

    /// Whether this app's kcap is one the installer admits: linking PATH to anything else, a missing file included,
    /// would trade one refused copy for another.
    async Task<bool> TargetAdmittedAsync(CancellationToken ct) =>
        appCli is not null && await Task.Run(() => LocalClientFloor.Admits(LocalClientCopies.VersionOf(runner, appCli)), ct).ConfigureAwait(false);

    /// Replaces <paramref name="link"/> only while it is still the entry <paramref name="found"/> describes: anything
    /// that took its place in the meantime is someone else's and is looked at again. Neither rename nor ln can compare
    /// as it replaces, so an entry landing between the last check and the swap still loses, to this app's kcap.
    async Task<Relink> RelinkAsync(string link, string found, CancellationToken ct) {
        if (appCli is null || !OneLine(appCli) || !OneLine(link) || !OneLine(found) || !await TargetAdmittedAsync(ct).ConfigureAwait(false))
            return new Relink.Refused(new ClientRepairOutcome.Failed(link, "this app has no up-to-date kcap of its own to put there."));

        switch (await TryReplaceAsync(link, appCli, found, ct).ConfigureAwait(false)) {
            case true:  return new Relink.Done();
            case false: return new Relink.Changed();
        }

        ProcessResult result;
        try {
            result = await runner.RunAsync(OsascriptPath, OsascriptArgs(appCli, link, found), new RunOptions(CancelMode: CancelMode.KillTree), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return new Relink.Refused(new ClientRepairOutcome.Failed(link, ex.Message));
        }

        if (result.ExitCode == 0) return new Relink.Done();
        if (result.Stderr.Contains(EntryChanged, StringComparison.Ordinal)) return new Relink.Changed();
        if (result.Stderr.Contains("(-128)", StringComparison.Ordinal)) return new Relink.Refused(new ClientRepairOutcome.Cancelled(link));

        return new Relink.Refused(new ClientRepairOutcome.Failed(
            link, string.IsNullOrWhiteSpace(result.Stderr) ? "osascript failed." : result.Stderr.Trim()));
    }

    /// A line break would desynchronise osascript's argv from the script reading it.
    static bool OneLine(string path) => !path.Contains('\n') && !path.Contains('\r');

    /// Without an administrator prompt, where the folder is this user's to write: a link made beside the old copy is
    /// renamed over it, so PATH never goes without a kcap. True when replaced, false when the entry changed, null
    /// when the folder needs an administrator.
    async Task<bool?> TryReplaceAsync(string link, string target, string found, CancellationToken ct) {
        var temp = Path.Combine(Path.GetDirectoryName(link)!, $".kcap-{Guid.NewGuid():N}");
        try {
            File.CreateSymbolicLink(temp, target);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        try {
            if (await IdentityAsync(link, ct).ConfigureAwait(false) != found) return false;
            File.Move(temp, link, overwrite: true);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        } finally {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    const string StatPath = "/usr/bin/stat";

    /// BSD stat reads the entry with lstat unless told to follow it.
    static readonly string[] StatArgs = ["-f", "%d:%i:%m:%c"];

    /// The paths and the expected identity travel as argv and are read back through <c>quoted form</c>, never spliced
    /// into the script. The identity is compared in the same privileged shell that replaces the entry.
    internal static string[] OsascriptArgs(string target, string link, string found) => [
        "-e", "on run argv",
        "-e", "do shell script \"[ \\\"$(" + StatPath + " -f " + StatArgs[1] + " \" & quoted form of item 2 of argv & \")\\\" = \" & quoted form of item 3 of argv & \" ] || { echo " + EntryChanged + " >&2; exit 3; }; /bin/ln -sfh \" & quoted form of item 1 of argv & \" \" & quoted form of item 2 of argv with administrator privileges",
        "-e", "end run",
        "--", target, link, found,
    ];
}
