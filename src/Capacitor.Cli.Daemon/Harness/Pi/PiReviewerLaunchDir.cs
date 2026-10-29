using Capacitor.Cli.Core.Harness.Pi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// One unattended Pi reviewer launch's directory: the extension it loads, the manifest that extension
/// reads, the system prompt, the session store and the readiness report. Owner-only and outside the
/// worktree, so the repository under review can neither read nor replace any of it.
/// </summary>
internal static class PiReviewerLaunchDir {
    const string Prefix = "kcap-pi-reviewer-";

    const UnixFileMode OwnerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "pi-reviewers");

    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    internal static PiReviewerLaunchPaths Create(
            string stateDir, string daemonEpoch, string launchId, string manifestJson, ILogger? log = null) {
        log ??= NullLogger.Instance;

        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var dir = Path.Combine(root, NameFor(daemonEpoch, launchId));
        if (Path.Exists(dir)) Delete(dir, stateDir, log);
        CreateOwnerOnly(dir);

        if (Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException(
                $"pi_reviewer_launch_dir_not_empty: '{dir}' still holds a previous launch's content that "
              + "could not be removed. Refusing rather than handing a reviewer another review's transcript.");

        var paths = new PiReviewerLaunchPaths(
            Dir:          dir,
            Extension:    Path.Combine(dir, PiReviewerExtension.FileName),
            Manifest:     Path.Combine(dir, "manifest.json"),
            SystemPrompt: Path.Combine(dir, PiReviewerSystemPrompt.FileName),
            Sessions:     Path.Combine(dir, "sessions"),
            Ready:        Path.Combine(dir, PiReviewerExtension.ReadyFileName));

        try {
            WriteOwnerOnly(paths.Extension, PiReviewerExtension.Content);
            WriteOwnerOnly(paths.Manifest, manifestJson);
            WriteOwnerOnly(paths.SystemPrompt, PiReviewerSystemPrompt.Text);
            CreateOwnerOnly(paths.Sessions);
        } catch {
            // All or nothing: a partial directory must never be launched against.
            Delete(dir, stateDir, log);
            throw;
        }

        return paths;
    }

    internal static void SweepStale(string stateDir, string currentEpoch, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        IReadOnlyList<string> candidates;
        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex, "Could not enumerate the Pi reviewer launch root {Root}; skipping the sweep", root);
            return;
        }

        foreach (var dir in candidates) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(live, StringComparison.Ordinal)) continue;

            Delete(dir, stateDir, log);
        }
    }

    /// <summary>Never throws: a directory that cannot be removed is logged, because it holds a review
    /// transcript and its retention must not be silent.</summary>
    internal static void Delete(string dir, string stateDir, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Pi reviewer launch directory {Path} resolves outside {Root}; refusing to delete", full, root);
            return;
        }

        try {
            var attributes = new FileInfo(full).Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(full);
                else                                             File.Delete(full);

                return;
            }

            DeleteTreeNoFollow(full);
        } catch (Exception ex) {
            log.LogWarning(ex, "Failed to delete Pi reviewer launch directory {Path}", full);
        }
    }

    static void DeleteTreeNoFollow(string path) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
            var attributes  = new FileInfo(entry).Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink      = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isDirectory && isLink) Directory.Delete(entry);   // the link; its target is untouched
            else if (isDirectory)      DeleteTreeNoFollow(entry);
            else                       File.Delete(entry);
        }

        Directory.Delete(path);
    }

    static void WriteOwnerOnly(string path, string content) {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, OwnerOnlyFile);
    }

    static void CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "pi_reviewer_unsupported_platform: a reviewer launch directory cannot be created owner-only here.");

        Directory.CreateDirectory(path, OwnerOnlyDir);

        // An existing directory keeps its own mode — CreateDirectory's mode argument applies only when
        // it creates. Verifying covers that, and any filesystem that ignores the request.
        var mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupRead  | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead  | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                $"pi_reviewer_launch_dir_not_owner_only: '{path}' is mode {mode}. The reviewer's launch "
              + "directory, and so the review context it carries, would be readable by other users on this host.");
    }

    /// <summary>What keeps a launch id from naming a path outside the root.</summary>
    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
