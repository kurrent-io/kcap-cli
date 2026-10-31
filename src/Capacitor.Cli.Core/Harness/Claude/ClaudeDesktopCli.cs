using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// The Claude Code the desktop app downloads for itself, at
/// <c>Claude/claude-code/&lt;version&gt;/&lt;build&gt;/claude.app/Contents/MacOS/claude</c> under its data
/// directory. The version directory changes on every update and the app prunes old ones, so the path
/// is looked up each time it is asked for, never kept.
/// </summary>
public static class ClaudeDesktopCli {
    static readonly string Root       = Path.Combine("Claude", "claude-code");
    static readonly string Executable = Path.Combine("claude.app", "Contents", "MacOS", "claude");

    // The app writes this beside a build once its download has checked out; a build without it may
    // be half-written.
    const string VerifiedMarker = ".verified";

    static readonly BinaryProbe Launchable = BinaryProbe.Searching(null);

    /// <summary>The newest verified build's executable, or null.</summary>
    public static string? Find(DesktopAppProbe apps) =>
        apps.Directories(Root)
            .SelectMany(Versions)
            .OrderByDescending(v => v.Version)
            .ThenBy(v => v.Name.Length)
            .Select(v => NewestBuild(v.Path))
            .FirstOrDefault(exe => exe is not null);

    static IEnumerable<(string Path, string Name, Version Version)> Versions(string root) {
        foreach (var dir in SubDirectories(root)) {
            var name = Path.GetFileName(dir);

            // A pre-release suffix sorts after its release on the tie-break, never above a newer one.
            if (Version.TryParse(name.Split('-', '+')[0], out var version)) yield return (dir, name, version);
        }
    }

    static string? NewestBuild(string versionDir) =>
        SubDirectories(versionDir)
            .Where(build => File.Exists(Path.Combine(build, VerifiedMarker)))
            .OrderByDescending(build => File.GetLastWriteTimeUtc(Path.Combine(build, VerifiedMarker)))
            .Select(build => Launchable.Resolve(Path.Combine(build, Executable)))
            .FirstOrDefault(exe => exe is not null);

    // The app may prune a version while we walk it; a vanished directory is simply not a candidate.
    static string[] SubDirectories(string dir) {
        try {
            return Directory.GetDirectories(dir);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
    }
}
