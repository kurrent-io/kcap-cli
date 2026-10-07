using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Installs one release into a script install, the way the install script does: download the RID's
/// archive, verify it against the release manifest's sha256, extract it into its own
/// <c>versions/&lt;v&gt;</c> and switch <c>current</c> to it. A running kcap keeps its own files, because
/// no version directory that is in use is ever written.
///
/// <para>Verification fails closed: a manifest that cannot be read, has no valid checksum for this RID,
/// or does not match the downloaded bytes installs nothing.</para>
/// </summary>
public sealed partial class ScriptUpdater(KcapReleaseClient releases) {
    /// <summary>Switches <c>current</c> to a version directory. A seam: Windows needs a junction, and
    /// a test cannot make one off Windows.</summary>
    internal Action<ScriptInstallLayout, string> SwitchCurrent { get; init; } = DefaultSwitch;

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

        var current = new FileInfo(layout.Current);
        if (current.LinkTarget is null && (current.Exists || Directory.Exists(layout.Current))) {
            await stderr.WriteLineAsync($"{layout.Current} is not a link the installer made. Nothing was installed.");
            return false;
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

            // A leftover directory of this version is not current (only an older one is), so nothing runs from it.
            var dest = layout.VersionDir(version);
            try {
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                Directory.Move(staging, dest);
                SwitchCurrent(layout, dest);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
                await stderr.WriteLineAsync($"Could not activate kcap {version}: {ex.Message}");
                return false;
            }

            WriteMarker(layout, channel);
            await stdout.WriteLineAsync($"Installed kcap {version} to {layout.Root}");

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

    /// <summary>The installer's marker, rewritten with the channel this update followed.</summary>
    static void WriteMarker(ScriptInstallLayout layout, string channel) {
        var json = new JsonObject { ["source"] = "script", ["channel"] = channel }.ToJsonString();
        var tmp  = $"{layout.Marker}.tmp-{Environment.ProcessId}";
        File.WriteAllText(tmp, json + "\n");
        File.Move(tmp, layout.Marker, overwrite: true);
    }

    /// <summary>
    /// On Unix a new relative link renamed over <c>current</c>, which rename(2) does atomically: there is
    /// no moment without one. Windows has no atomic replace for a junction, so it is removed and recreated,
    /// as the install script does.
    /// </summary>
    static void DefaultSwitch(ScriptInstallLayout layout, string versionDir) {
        if (OperatingSystem.IsWindows()) {
            if (Directory.Exists(layout.Current) || File.Exists(layout.Current)) Directory.Delete(layout.Current);
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") {
                ArgumentList = { "/d", "/c", "mklink", "/J", layout.Current, versionDir },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            })!;
            var err = mklink.StandardError.ReadToEnd();
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            if (mklink.ExitCode != 0) throw new IOException($"mklink /J failed: {err.Trim()}");
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
