using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.Setup;

public enum ShimPreflight { Installable, AlreadyInstalled, Conflict }

public enum ShimOutcome { Installed, InstalledButNotOnPath, Cancelled, Failed }

public sealed record ShimResult(ShimOutcome Outcome, string? Detail, string? SudoFallback);

/// Installs a `kcap` symlink to a resolved CLI so a terminal PATH that omits the CLI's own location
/// still finds it: in the first stable login-PATH folder this user can already write to, else at
/// `destination`, which needs an admin prompt only when it lies outside the user's home. A user who
/// cannot authorize that prompt gets the link in their own folder and that folder on PATH through
/// `profile` instead. Mechanics only — the once-ever offer and tray-menu wiring live in the desktop
/// app's ShimOfferCoordinator. `destination` is a parameter so tests drive the real filesystem
/// taxonomy against a temp path instead of the actual /usr/local/bin/kcap.
public sealed partial class PathShimInstaller(
        IProcessRunner runner, ILoginShellProbe probe, string destination = PathShimInstaller.Destination, UserHome? home = null,
        ShellProfile? profile = null, Func<CancellationToken, Task<bool>>? mayElevate = null)
    : ICliPathInstaller {
    public const string Destination = "/usr/local/bin/kcap";

    const string OsascriptPath = "/usr/bin/osascript";

    public string Disclosure => UserOwned
        ? "This links kcap into a folder on your terminal's PATH, so kcap works from any terminal."
        : "This links kcap into a folder on your terminal's PATH, so kcap works from any terminal. " +
          "If none of those folders is writable, macOS asks once for your admin password." +
          (profile?.Disclosure is { } line
              ? $" Without admin rights, kcap links it into ~/.local/bin instead and adds {line}, a marked line that uninstalling kcap removes."
              : "");

    /// Follows InstallAsync's choice of folder without creating anything; a folder on PATH counts
    /// when this user can write it, since the real install would try a link there first.
    public async Task<IReadOnlyList<DisclosureSpan>> DescribeAsync(string target, CancellationToken ct) {
        if (await WritableFolderOnTerminalPathAsync(target, ct).ConfigureAwait(false) is { } folder) return LinkedInto(folder, ".");
        if (!UserOwned && profile is not null && home is not null && !await MayElevateAsync(ct).ConfigureAwait(false))
            return DescribeProfileRoute(target, profile, home);
        if (Preflight(destination, target) == ShimPreflight.Conflict) return Untouched(destination);
        var admin = Path.GetDirectoryName(destination)!;
        return LinkedInto(admin, UserOwned ? "." : ". macOS asks once for your admin password.");
    }

    DisclosureSpan[] DescribeProfileRoute(string target, ShellProfile profile, UserHome home) {
        var folder = ShellProfile.Folder(home);
        if (profile is not { ProfilePath: { } file, Disclosure: not null })
            return [new("kcap can't set PATH for this shell, so you'll need to put "), new("kcap", Code: true), new(" on it yourself.")];
        if (Preflight(Path.Combine(folder, "kcap"), target) == ShimPreflight.Conflict) return Untouched(Path.Combine(folder, "kcap"));
        return [.. LinkedInto(folder, ", and "), new(ShellProfile.Tilde(home, file), Code: true),
            new(" gets one marked line adding that folder to PATH. Uninstalling kcap removes it.")];
    }

    DisclosureSpan[] LinkedInto(string folder, string rest) =>
        [new("It's linked into "), new(Display(folder), Code: true), new(rest)];

    DisclosureSpan[] Untouched(string link) =>
        [new(Display(link), Code: true), new(" belongs to another install, so kcap leaves it alone and can't link there.")];

    string Display(string path) => home is null ? path : ShellProfile.Tilde(home, path);

    async Task<string?> WritableFolderOnTerminalPathAsync(string target, CancellationToken ct) {
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
        if (path is null) return null;

        var stable = StableFolders();
        foreach (var entry in path.Split(':', StringSplitOptions.RemoveEmptyEntries)) {
            var dir = Path.TrimEndingDirectorySeparator(entry);
            if (!Path.IsPathFullyQualified(dir) || !stable.Contains(dir)) continue;
            switch (Preflight(Path.Combine(dir, "kcap"), target)) {
                case ShimPreflight.AlreadyInstalled: return dir;
                case ShimPreflight.Installable when Writable(dir): return dir;
            }
        }
        return null;
    }

    const int W_OK = 2;

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    static bool Writable(string dir) => !OperatingSystem.IsWindows() && Directory.Exists(dir) && Access(dir, W_OK) == 0;

    bool UserOwned => home is not null
        && Path.GetFullPath(destination).StartsWith(Path.TrimEndingDirectorySeparator(home.Path) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public ShimPreflight Preflight(string target) => Preflight(destination, target);

    public Task<ShimResult> InstallAsync(string target, CancellationToken ct) =>
        InstallAsync(target, destination, ct);

    public async Task<ShimResult> InstallAsync(string target, string destination, CancellationToken ct) {
        if (!LooksLikeTarget(target))
            return new ShimResult(ShimOutcome.Failed, "CLI path contains a newline or carriage return and cannot be used.", null);

        if (await TryLinkOnTerminalPathAsync(target, ct).ConfigureAwait(false) is { } linked) return linked;

        var preflight = Preflight(destination, target);
        // A link to our target is not by itself "on PATH": the login shell's PATH can still omit its
        // folder, so it gets the same probe as a fresh link.
        if (preflight == ShimPreflight.AlreadyInstalled) {
            var existing = await ProbeOutcomeAsync(destination, ct).ConfigureAwait(false);
            if (existing.Outcome != ShimOutcome.InstalledButNotOnPath || UserOwned || profile is null || home is null
                || await MayElevateAsync(ct).ConfigureAwait(false)) return existing;
            return await LinkThroughProfileAsync(target, profile, home, ct).ConfigureAwait(false);
        }

        // Ahead of the conflict check: a user who could not replace that entry anyway still has a folder of their own.
        if (!UserOwned && profile is not null && home is not null && !await MayElevateAsync(ct).ConfigureAwait(false))
            return await LinkThroughProfileAsync(target, profile, home, ct).ConfigureAwait(false);

        if (preflight == ShimPreflight.Conflict)
            return new ShimResult(ShimOutcome.Failed, $"{destination} already exists ({Describe(destination)}) and was left untouched.", null);

        if (UserOwned) return await LinkInHomeAsync(target, destination, ct).ConfigureAwait(false);

        var result = await runner.RunAsync(OsascriptPath, OsascriptArgs(target), new RunOptions(), ct).ConfigureAwait(false);

        if (result.ExitCode == 0) return await ProbeOutcomeAsync(destination, ct).ConfigureAwait(false);

        // The parenthesized form only — a bare "-128" substring can appear unescaped inside a
        // genuine failure's shell error text (e.g. a target path like ".../app-128/kcap"), which
        // would otherwise misclassify a real Failed as Cancelled and silently drop the Detail and
        // SudoFallback recovery command.
        if (result.Stderr.Contains("(-128)"))
            return new ShimResult(ShimOutcome.Cancelled, null, null);

        var sudoFallback = "sudo mkdir -p /usr/local/bin && sudo ln -s " + PosixQuote(target) + " /usr/local/bin/kcap";
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? "osascript failed." : result.Stderr.Trim();
        return new ShimResult(ShimOutcome.Failed, detail, sudoFallback);
    }

    async Task<ShimResult> LinkInHomeAsync(string target, string destination, CancellationToken ct) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.CreateSymbolicLink(destination, target);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new ShimResult(ShimOutcome.Failed, ex.Message, null);
        }
        return await ProbeOutcomeAsync(destination, ct).ConfigureAwait(false);
    }

    async Task<ShimResult> LinkThroughProfileAsync(string target, ShellProfile profile, UserHome home, CancellationToken ct) {
        if (profile.Disclosure is null)
            return new ShimResult(ShimOutcome.Failed, "This shell or home path does not support automatic PATH setup. Create ~/.local/bin, link kcap in that folder to your installed CLI, then add ~/.local/bin to your shell's PATH manually.", null);

        var link = Path.Combine(ShellProfile.Folder(home), "kcap");
        switch (Preflight(link, target)) {
            case ShimPreflight.Conflict:
                return new ShimResult(ShimOutcome.Failed, $"{link} already exists ({Describe(link)}) and was left untouched.", null);
            case ShimPreflight.Installable:
                try {
                    Directory.CreateDirectory(ShellProfile.Folder(home));
                    File.CreateSymbolicLink(link, target);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    return new ShimResult(ShimOutcome.Failed, ex.Message, null);
                }
                break;
        }

        try {
            if (!profile.Add())
                return new ShimResult(ShimOutcome.Failed, "The shell profile could not be configured. Add ~/.local/bin to your shell's PATH manually.", null);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new ShimResult(ShimOutcome.Failed, $"kcap was linked at {link}, but {profile.ProfilePath} could not be updated: {ex.Message}", null);
        }
        return await ProbeOutcomeAsync(link, ct).ConfigureAwait(false);
    }

    Task<bool> MayElevateAsync(CancellationToken ct) => mayElevate?.Invoke(ct) ?? IsAdminAsync(runner, ct);

    /// Whether this user is in the `admin` group the authorization prompt asks a password from. An
    /// unanswered question keeps the prompt, which an admin can still complete.
    internal static async Task<bool> IsAdminAsync(IProcessRunner runner, CancellationToken ct) {
        try {
            var result = await runner.RunAsync("/usr/bin/id", ["-Gn"], new RunOptions(Timeout: TimeSpan.FromSeconds(5)), ct)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut) return true;
            return result.Stdout.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("admin", StringComparer.Ordinal);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return true;
        }
    }

    async Task<ShimResult?> TryLinkOnTerminalPathAsync(string target, CancellationToken ct) {
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
        if (path is null) return null;

        var stable = StableFolders();
        foreach (var entry in path.Split(':', StringSplitOptions.RemoveEmptyEntries)) {
            var dir = Path.TrimEndingDirectorySeparator(entry);
            if (!Path.IsPathFullyQualified(dir) || !stable.Contains(dir)) continue;

            var link = Path.Combine(dir, "kcap");
            var created = false;
            switch (Preflight(link, target)) {
                case ShimPreflight.Conflict: continue;
                case ShimPreflight.Installable:
                    try {
                        File.CreateSymbolicLink(link, target);
                        created = true;
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        continue;
                    }
                    break;
            }

            if (await ResolvesToAsync(link, ct).ConfigureAwait(false)) return new ShimResult(ShimOutcome.Installed, null, null);
            if (created) TryDelete(link);
        }
        return null;
    }

    // A version manager's per-version or per-shell folder sits early on PATH and is replaced on the
    // next switch, taking the link with it; only folders that outlive that are candidates.
    HashSet<string> StableFolders() {
        var folders = new HashSet<string>(StringComparer.Ordinal) { "/opt/homebrew/bin", Path.GetDirectoryName(destination)! };
        if (home is not null) folders.UnionWith(KcapLinkFolders.For(home));
        return folders;
    }

    // Another kcap earlier on PATH would still win, so presence alone is not success.
    async Task<bool> ResolvesToAsync(string link, CancellationToken ct) {
        if (await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false) != true) return false;
        var resolved = await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        return resolved is not null && PathComparison.Equal(Path.GetFullPath(resolved), Path.GetFullPath(link));
    }

    static void TryDelete(string link) {
        try { File.Delete(link); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// Shared post-install/AlreadyInstalled mapping (spec §5: "never report success on the symlink
    /// alone"): re-run the login-shell PATH probe and only call it Installed when `kcap` actually
    /// resolves to the installed link — otherwise InstalledButNotOnPath with the same actionable Detail. Forces a FRESH
    /// probe (never the pre-install cached answer, which the offer decision itself already
    /// consumed and is now stale — the install just changed the filesystem). An UNKNOWN re-probe
    /// (both attempts failed/timed out) is not the same as "not on PATH": asserting the PATH's
    /// contents from a probe that returned nothing would be a guess, so it fails closed to Failed
    /// with a "could not re-verify" detail instead.
    async Task<ShimResult> ProbeOutcomeAsync(string destination, CancellationToken ct) {
        var onPath = await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        if (onPath == true) {
            var resolved = await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
            if (resolved is null)
                return new ShimResult(ShimOutcome.Failed,
                    $"kcap was linked at {destination}, but the login-shell probe could not re-verify which kcap command resolves.", null);
            if (PathComparison.Equal(Path.GetFullPath(resolved), Path.GetFullPath(destination)))
                return new ShimResult(ShimOutcome.Installed, null, null);
            return new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"kcap was linked at {destination}, but another kcap at {resolved} takes precedence. Put {Path.GetDirectoryName(destination)} earlier on your shell's PATH to use this install.", null);
        }
        return onPath switch {
            false => new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"kcap was linked at {destination}, but your login shell's PATH doesn't include {Path.GetDirectoryName(destination)}. Add: export PATH=\"{Path.GetDirectoryName(destination)}:$PATH\"",
                null),
            _     => new ShimResult(ShimOutcome.Failed,
                $"kcap was linked at {destination}, but the login-shell probe could not re-verify whether it is on your terminal PATH.",
                null),
        };
    }

    /// lstat taxonomy on `destination`, never following through a foreign link: absent →
    /// Installable; a symlink resolving (through any chain) to `target` → AlreadyInstalled;
    /// anything else — foreign symlink, broken symlink, regular file, directory — → Conflict.
    /// LinkTarget/ResolveLinkTarget never follow past the top-level lstat on their own, so a
    /// regular file's LinkTarget is null (not "not a link" vs "absent" — both null) and only
    /// FileInfo.Exists/Directory.Exists distinguish those two afterwards.
    public static ShimPreflight Preflight(string destination, string target) {
        var info = new FileInfo(destination);

        if (info.LinkTarget is not null) {
            var resolved = TryResolveFinalTarget(info);
            if (resolved is null || !resolved.Exists) return ShimPreflight.Conflict; // broken link
            return string.Equals(resolved.FullName, Path.GetFullPath(target), StringComparison.Ordinal)
                ? ShimPreflight.AlreadyInstalled
                : ShimPreflight.Conflict;
        }

        if (info.Exists) return ShimPreflight.Conflict; // regular file
        if (Directory.Exists(destination)) return ShimPreflight.Conflict;
        return ShimPreflight.Installable;
    }

    // Broad catch: an unresolvable link (broken, too-deep, permission-denied) all read the same
    // way here — never overwrite something we can't positively identify as the target.
    static FileSystemInfo? TryResolveFinalTarget(FileInfo info) {
        try {
            return info.ResolveLinkTarget(returnFinalTarget: true);
        } catch (Exception) {
            return null;
        }
    }

    static string Describe(string destination) {
        var info = new FileInfo(destination);
        if (info.LinkTarget is { } linkTarget) return $"a symlink to {linkTarget}";
        if (info.Exists) return "a regular file";
        if (Directory.Exists(destination)) return "a directory";
        return "an unexpected filesystem entry";
    }

    /// argv for `osascript`: the target is passed as an <c>-- &lt;target&gt;</c> argv element and read back
    /// via `quoted form of item 1 of argv` — never string-interpolated into the script source, so
    /// spaces/quotes/backslashes/etc. in the path can't break out of the shell command. `ln -s` is
    /// non-forcing: a race lands as a failed creation, never a clobber.
    internal static string[] OsascriptArgs(string target) => [
        "-e", "on run argv",
        "-e", "do shell script \"mkdir -p /usr/local/bin && ln -s \" & quoted form of item 1 of argv & \" /usr/local/bin/kcap\" with administrator privileges",
        "-e", "end run",
        "--", target,
    ];

    /// POSIX single-quote escaping for the copyable sudo fallback shown on failure: close the
    /// quote, emit an escaped literal quote, reopen it.
    internal static string PosixQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    /// CR/LF would either break the single-line sudo fallback or (via osascript's argv) desync
    /// script and data; rejected before any prompt.
    internal static bool LooksLikeTarget(string s) => !s.Contains('\n') && !s.Contains('\r');
}
