using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Installs one release into a script install, the way the install script does: download the RID's
/// archive, verify it against the release manifest's sha256, extract it into its own
/// <c>versions/&lt;v&gt;</c> and switch <c>current</c> to it. A running kcap keeps its own files, because
/// no version directory that is in use is ever written or pruned.
///
/// <para>Verification fails closed: a manifest that cannot be read, has no valid checksum for this RID,
/// or does not match the downloaded bytes installs nothing.</para>
/// </summary>
public sealed partial class ScriptUpdater(KcapReleaseClient releases) {
    /// <summary>Switches <c>current</c> to a version directory. A seam: Windows needs a junction, and
    /// a test cannot make one off Windows.</summary>
    internal Action<ScriptInstallLayout, string> SwitchCurrent { get; init; } = DefaultSwitch;

    /// <summary>Whether something runs from a version directory, so pruning must keep it.</summary>
    internal Func<string, bool> InUse { get; init; } = AnyFileInUse;

    internal async Task<bool> InstallAsync(
            ScriptInstallLayout layout, string version, string channel, string rid, TextWriter stdout, TextWriter stderr,
            CancellationToken ct) {
        if (!ReleaseManifest.IsValidVersion(version)) {
            await stderr.WriteLineAsync($"Refusing to install '{version}': not a release version.");
            return false;
        }

        var manifest = await releases.GetVersionManifestAsync(version, ct);
        if (manifest is null || manifest.Version != version) {
            await stderr.WriteLineAsync($"Could not read the manifest for kcap {version} ({releases.VersionManifestUrl(version)}). Nothing was installed.");
            return false;
        }

        if (!manifest.Sha256ByRid.TryGetValue(rid, out var expected)) {
            await stderr.WriteLineAsync($"kcap {version} has no build for {rid}. Nothing was installed.");
            return false;
        }

        if (!ReleaseManifest.IsValidSha256(expected)) {
            await stderr.WriteLineAsync($"The manifest for kcap {version} has an invalid checksum for {rid}. Nothing was installed.");
            return false;
        }

        var current = new DirectoryInfo(layout.Current);
        if (current.LinkTarget is null && (current.Exists || File.Exists(layout.Current))) {
            await stderr.WriteLineAsync($"{layout.Current} is not a link the installer made. Nothing was installed.");
            return false;
        }

        var dest     = layout.VersionDir(version);
        var previous = ActiveVersionDir(layout);
        if (previous is not null && PathsEqual(previous, dest)) {
            await stdout.WriteLineAsync($"kcap {version} is already the active version.");
            TryWriteMarker(layout, channel, stderr);
            return true;
        }

        Directory.CreateDirectory(layout.Versions);
        var work = Path.Combine(layout.Versions, $".update-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);

        try {
            var url     = releases.ArchiveUrl(version, rid);
            var archive = Path.Combine(work, Path.GetFileName(new Uri(url).AbsolutePath));

            await stdout.WriteLineAsync($"Downloading kcap {version} ({rid})");
            try {
                await releases.DownloadAsync(url, archive, ct);
            } catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) {
                await stderr.WriteLineAsync($"Could not download {url}: {ex.Message}. Nothing was installed.");
                return false;
            }

            var actual = await Sha256Async(archive, ct);
            if (actual != expected) {
                await stderr.WriteLineAsync($"Checksum mismatch for {url}: expected {expected}, got {actual}. Nothing was installed.");
                return false;
            }

            var staging = Path.Combine(work, "staging");
            try {
                await ExtractAsync(archive, staging, ct);
            } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
                await stderr.WriteLineAsync($"Could not extract {Path.GetFileName(archive)}: {ex.Message}. Nothing was installed.");
                return false;
            }

            var exe = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";
            if (!File.Exists(Path.Combine(staging, "bin", exe))) {
                await stderr.WriteLineAsync($"The archive does not contain bin/{exe}. Nothing was installed.");
                return false;
            }

            // A process started from a leftover directory of this version may still run there, so it is
            // moved aside rather than overwritten; pruning deletes it once nothing uses it.
            var aside = Path.Combine(layout.Versions, $".replaced-{version}-{Guid.NewGuid():N}");
            try {
                if (Directory.Exists(dest)) Directory.Move(dest, aside);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await stderr.WriteLineAsync($"Could not replace {dest}, which may be in use: {ex.Message}. Nothing was installed.");
                return false;
            }

            try {
                Directory.Move(staging, dest);
                SwitchCurrent(layout, dest);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
                await stderr.WriteLineAsync($"Could not activate kcap {version}: {ex.Message}");
                return false;
            }

            TryWriteMarker(layout, channel, stderr);
            await stdout.WriteLineAsync($"Installed kcap {version} to {layout.Root}");

            var pruned = PruneVersions(layout, keep: [dest, previous]);
            if (pruned > 0) await stdout.WriteLineAsync($"Removed {pruned} unused kcap version{(pruned == 1 ? "" : "s")}.");

            return true;
        } finally {
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
        }
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken ct) {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    static async Task ExtractAsync(string archive, string destination, CancellationToken ct) {
        Directory.CreateDirectory(destination);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: false, ct);
            return;
        }

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
    }

    /// <summary>The installer's marker, rewritten with the channel this update followed. The version is
    /// already active when this runs, so a failure is reported and the update still succeeds.</summary>
    internal static void TryWriteMarker(ScriptInstallLayout layout, string channel, TextWriter stderr) {
        var json = new JsonObject { ["source"] = "script", ["channel"] = channel }.ToJsonString();
        var tmp  = $"{layout.Marker}.tmp-{Environment.ProcessId}";
        try {
            File.WriteAllText(tmp, json + "\n");
            File.Move(tmp, layout.Marker, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            stderr.WriteLine($"Could not record the update channel in {layout.Marker}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the version directories and moved-aside copies an update leaves behind, keeping
    /// <paramref name="keep"/>: the new version and the one it replaced, which a daemon or agent started
    /// before the switch is likely still running. Anything else in use stays too. Returns how many went.
    /// </summary>
    internal int PruneVersions(ScriptInstallLayout layout, IEnumerable<string?> keep) {
        var kept = keep.OfType<string>().ToArray();
        var pruned = 0;

        IEnumerable<DirectoryInfo> candidates;
        try {
            candidates = new DirectoryInfo(layout.Versions).EnumerateDirectories()
                .Where(d => d.LinkTarget is null)
                .Where(d => ReleaseManifest.IsValidVersion(d.Name) || d.Name.StartsWith(".replaced-", StringComparison.Ordinal))
                .Where(d => !kept.Any(k => PathsEqual(k, d.FullName)))
                .ToArray();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return 0;
        }

        foreach (var dir in candidates) {
            if (InUse(dir.FullName)) continue;

            try {
                dir.Delete(recursive: true);
                pruned++;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Started after the probe, or not ours to delete: the next update tries again.
            }
        }

        return pruned;
    }

    /// <summary>
    /// True when a file in <paramref name="dir"/> cannot be opened exclusively for writing. A running
    /// executable refuses that on Windows (sharing violation) and Linux (ETXTBSY); macOS does not, but
    /// deleting a running binary there leaves the process its file.
    /// </summary>
    internal static bool AnyFileInUse(string dir) {
        // A link inside the directory would make whatever it reaches count; an unreadable entry counts.
        var options = new EnumerationOptions {
            RecurseSubdirectories = true,
            AttributesToSkip      = FileAttributes.ReparsePoint,
            IgnoreInaccessible    = false,
        };

        try {
            foreach (var file in Directory.EnumerateFiles(dir, "*", options)) {
                using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return false;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return true;
        }
    }

    /// <summary>The version directory <c>current</c> points at, or null when it is not a link.</summary>
    internal static string? ActiveVersionDir(ScriptInstallLayout layout) =>
        new DirectoryInfo(layout.Current).LinkTarget is { } target
            ? Path.GetFullPath(Path.Combine(layout.Root, target))
            : null;

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>On Unix a new relative link renamed over <c>current</c>, which rename(2) does atomically: there is
    /// no moment without one. On Windows a junction, replaced as the install script does.</summary>
    static void DefaultSwitch(ScriptInstallLayout layout, string versionDir) {
        if (OperatingSystem.IsWindows()) {
            WindowsJunction.Replace(layout.Current, versionDir);
            return;
        }

        SwitchLink(layout.Current, Path.GetRelativePath(layout.Root, versionDir));
    }

    /// <summary>Atomically points the symlink at <paramref name="link"/> to <paramref name="target"/>.</summary>
    internal static void SwitchLink(string link, string target) {
        var tmp = $"{link}.new-{Environment.ProcessId}";
        if (File.Exists(tmp) || Directory.Exists(tmp)) File.Delete(tmp);
        File.CreateSymbolicLink(tmp, target);

        if (rename(tmp, link) != 0) {
            var errno = Marshal.GetLastPInvokeError();
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw new IOException($"Could not switch {link} to {target} (errno {errno}).");
        }
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int rename(string oldPath, string newPath);
}
