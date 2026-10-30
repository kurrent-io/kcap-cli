using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.Setup;

public enum ShimPreflight { Installable, AlreadyInstalled, Conflict }

public enum ShimOutcome { Installed, InstalledButNotOnPath, Failed }

public sealed record ShimResult(ShimOutcome Outcome, string? Detail);

/// Installs a `kcap` symlink to a resolved CLI so a terminal PATH that omits the CLI's own location
/// still finds it: in the first stable login-PATH folder this user can already write to, else in
/// `~/.local/bin`, which `profile` (when given) then puts on PATH. It never asks for an admin
/// password: a system-owned folder is used only when this user can already write it. Mechanics
/// only — the once-ever offer and tray-menu wiring live in the desktop app's ShimOfferCoordinator.
public sealed partial class PathShimInstaller(ILoginShellProbe probe, UserHome home, ShellProfile? profile = null)
    : ICliPathInstaller {
    string Link => Path.Combine(ShellProfile.Folder(home), "kcap");

    public string Disclosure =>
        "This links kcap into a folder on your terminal's PATH, so kcap works from any terminal." +
        (profile?.Disclosure is { } line
            ? $" If none of those folders is writable, kcap links it into ~/.local/bin and adds {line}, a marked line that uninstalling kcap removes."
            : "");

    /// Follows InstallAsync's choice of folder without creating anything; a folder on PATH counts
    /// when this user can write it, since the real install would try a link there first.
    public async Task<IReadOnlyList<DisclosureSpan>> DescribeAsync(string target, CancellationToken ct) {
        if (await WritableFolderOnTerminalPathAsync(target, ct).ConfigureAwait(false) is { } folder) return LinkedInto(folder, ".");
        if (profile is { ProfilePath: null } or { Disclosure: null })
            return [new("kcap can't set PATH for this shell, so you'll need to put "), new("kcap", Code: true), new(" on it yourself.")];
        if (Preflight(Link, target) == ShimPreflight.Conflict) return Untouched(Link);
        if (profile is null) return LinkedInto(ShellProfile.Folder(home), ".");
        return [.. LinkedInto(ShellProfile.Folder(home), ", and "), new(ShellProfile.Tilde(home, profile.ProfilePath!), Code: true),
            new(" gets one marked line adding that folder to PATH. Uninstalling kcap removes it.")];
    }

    DisclosureSpan[] LinkedInto(string folder, string rest) =>
        [new("It's linked into "), new(ShellProfile.Tilde(home, folder), Code: true), new(rest)];

    DisclosureSpan[] Untouched(string link) =>
        [new(ShellProfile.Tilde(home, link), Code: true), new(" belongs to another install, so kcap leaves it alone and can't link there.")];

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

    public ShimPreflight Preflight(string target) => Preflight(Link, target);

    public async Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
        if (!LooksLikeTarget(target))
            return new ShimResult(ShimOutcome.Failed, "CLI path contains a newline or carriage return and cannot be used.");

        if (await TryLinkOnTerminalPathAsync(target, ct).ConfigureAwait(false) is { } linked) return linked;

        return await LinkInLocalBinAsync(target, ct).ConfigureAwait(false);
    }

    async Task<ShimResult> LinkInLocalBinAsync(string target, CancellationToken ct) {
        if (profile is { ProfilePath: null } or { Disclosure: null })
            return new ShimResult(ShimOutcome.Failed, "This shell or home path does not support automatic PATH setup. Create ~/.local/bin, link kcap in that folder to your installed CLI, then add ~/.local/bin to your shell's PATH manually.");

        var link = Link;
        switch (Preflight(link, target)) {
            case ShimPreflight.Conflict:
                return new ShimResult(ShimOutcome.Failed, $"{link} already exists ({Describe(link)}) and was left untouched.");
            case ShimPreflight.Installable:
                try {
                    Directory.CreateDirectory(ShellProfile.Folder(home));
                    File.CreateSymbolicLink(link, target);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    return new ShimResult(ShimOutcome.Failed, ex.Message);
                }
                break;
        }

        if (profile is not null) {
            try {
                if (!profile.Add())
                    return new ShimResult(ShimOutcome.Failed, "The shell profile could not be configured. Add ~/.local/bin to your shell's PATH manually.");
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return new ShimResult(ShimOutcome.Failed, $"kcap was linked at {link}, but {profile.ProfilePath} could not be updated: {ex.Message}");
            }
        }
        return await ProbeOutcomeAsync(link, ct).ConfigureAwait(false);
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

            if (await ResolvesToAsync(link, ct).ConfigureAwait(false)) return new ShimResult(ShimOutcome.Installed, null);
            if (created) TryDelete(link);
        }
        return null;
    }

    // A version manager's per-version or per-shell folder sits early on PATH and is replaced on the
    // next switch, taking the link with it; only folders that outlive that are candidates.
    HashSet<string> StableFolders() => new(KcapLinkFolders.For(home), StringComparer.Ordinal);

    // Another kcap earlier on PATH would still win, so presence alone is not success.
    async Task<bool> ResolvesToAsync(string link, CancellationToken ct) {
        if (await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false) != true) return false;
        var resolved = await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        return resolved is not null && PathComparison.Equal(Path.GetFullPath(resolved), Path.GetFullPath(link));
    }

    static void TryDelete(string link) {
        try { File.Delete(link); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// Never reports success on the symlink alone: a fresh login-shell probe (the cached answer
    /// predates the link) must resolve `kcap` to it. An unknown probe is not "not on PATH", so it
    /// fails closed rather than guessing at the PATH's contents.
    async Task<ShimResult> ProbeOutcomeAsync(string link, CancellationToken ct) {
        var folder = Path.GetDirectoryName(link);
        var onPath = await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        if (onPath == true) {
            var resolved = await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
            if (resolved is null)
                return new ShimResult(ShimOutcome.Failed,
                    $"kcap was linked at {link}, but the login-shell probe could not re-verify which kcap command resolves.");
            if (PathComparison.Equal(Path.GetFullPath(resolved), Path.GetFullPath(link)))
                return new ShimResult(ShimOutcome.Installed, null);
            return new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"kcap was linked at {link}, but another kcap at {resolved} takes precedence. Put {folder} earlier on your shell's PATH to use this install.");
        }
        return onPath switch {
            false => new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"kcap was linked at {link}, but your login shell's PATH doesn't include {folder}. Add: export PATH=\"{folder}:$PATH\""),
            _     => new ShimResult(ShimOutcome.Failed,
                $"kcap was linked at {link}, but the login-shell probe could not re-verify whether it is on your terminal PATH."),
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

    /// A line break would split the login shell's one-line `command -v` answer the install is verified against.
    internal static bool LooksLikeTarget(string s) => !s.Contains('\n') && !s.Contains('\r');
}
