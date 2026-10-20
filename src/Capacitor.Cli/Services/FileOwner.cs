using System.Globalization;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

/// <summary>Which account owns a path. No BCL API exposes a file's owner, and <c>struct stat</c> differs by
/// platform, architecture and libc, so the owner comes from the system's own <c>stat</c> command rather than
/// a hand-marshalled struct that could misread it on one target.</summary>
static partial class FileOwner {
    static readonly TimeSpan StatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>An absolute path, so a directory earlier on PATH cannot answer for it.</summary>
    const string Stat = "/usr/bin/stat";

    public static uint Effective => geteuid();

    /// <summary>The owning uid, or null when it cannot be read: on Windows, without <c>/usr/bin/stat</c>, or
    /// for a path this process cannot stat.</summary>
    public static uint? Of(string path) {
        if (OperatingSystem.IsWindows() || !File.Exists(Stat)) return null;

        try {
            var (code, stdout, _, timedOut) = ServiceProcess.RunBounded(
                Stat, OperatingSystem.IsMacOS() ? ["-f", "%u", path] : ["-c", "%u", path], StatTimeout);

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
