using System.Globalization;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

/// <summary>Which account owns a path. No BCL API exposes a file's owner, and <c>struct stat</c> differs by
/// platform, architecture and libc, so the owner comes from the system's own <c>stat</c> command rather than
/// a hand-marshalled struct that could misread it on one target.</summary>
static partial class FileOwner {
    static readonly TimeSpan StatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>System locations only, never PATH: a directory on PATH another account can write would
    /// let it answer for every owner. NixOS keeps its coreutils under <c>/run/current-system</c>.</summary>
    static readonly string[] Candidates = ["/usr/bin/stat", "/bin/stat", "/run/current-system/sw/bin/stat"];

    /// <summary>The first candidate that reads <c>/</c> as root's, the one owner every system agrees on.
    /// A file that exists but cannot do that is no reader at all.</summary>
    static readonly Lazy<string?> Stat = new(() => Candidates.FirstOrDefault(c => File.Exists(c) && Read(c, "/") == 0));

    public static uint Effective => geteuid();

    /// <summary>Whether owners can be read on this system at all. False on Windows, which has no owner
    /// model this audit uses, and where no system <c>stat</c> reads <c>/</c> correctly.</summary>
    public static bool Available => !OperatingSystem.IsWindows() && Stat.Value is not null;

    /// <summary>The owning uid, or null when it cannot be read: on Windows, without a working
    /// <c>stat</c>, or for a path this process cannot stat.</summary>
    public static uint? Of(string path) => Available ? Read(Stat.Value!, path) : null;

    static uint? Read(string stat, string path) {
        try {
            var (code, stdout, _, timedOut) = ServiceProcess.RunBounded(
                stat, OperatingSystem.IsMacOS() ? ["-f", "%u", path] : ["-c", "%u", path], StatTimeout);

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
