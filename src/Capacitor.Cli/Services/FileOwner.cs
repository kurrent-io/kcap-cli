using System.Globalization;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

/// <summary>Which account owns a path. No BCL API exposes a file's owner, and <c>struct stat</c> differs by
/// platform, architecture and libc, so the owner comes from the system's own <c>stat</c> command rather than
/// a hand-marshalled struct that could misread it on one target.</summary>
static partial class FileOwner {
    static readonly TimeSpan StatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The usual locations first; PATH covers a system that keeps it elsewhere, such as NixOS. An
    /// account that can change this user's PATH can already replace the unit itself.</summary>
    static readonly Lazy<string?> Stat = new(() =>
        new[] { "/usr/bin/stat", "/bin/stat" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(dir => Path.Combine(dir, "stat")))
            .FirstOrDefault(File.Exists));

    public static uint Effective => geteuid();

    /// <summary>Whether owners can be read on this system at all. False on Windows, which has no owner
    /// model this audit uses, and on a system with no <c>stat</c>.</summary>
    public static bool Available => !OperatingSystem.IsWindows() && Stat.Value is not null;

    /// <summary>The owning uid, or null when it cannot be read: on Windows, without <c>stat</c>, or for a
    /// path this process cannot stat.</summary>
    public static uint? Of(string path) {
        if (!Available) return null;

        try {
            var (code, stdout, _, timedOut) = ServiceProcess.RunBounded(
                Stat.Value!, OperatingSystem.IsMacOS() ? ["-f", "%u", path] : ["-c", "%u", path], StatTimeout);

            return !timedOut && code == 0
                && uint.TryParse(stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                ? uid
                : null;
        } catch (Exception) {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint geteuid();
}
