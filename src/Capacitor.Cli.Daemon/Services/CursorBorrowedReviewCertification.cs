using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

internal sealed record CursorBorrowedReviewArtifact(string Version, string LauncherPath, string BundleDigest);

/// <summary>Exact-build certification for Cursor's borrowed-review process boundary. Ordinary
/// Cursor launches remain available when this check fails; only the borrowed capability is
/// withdrawn. The check is repeated immediately before spawning a borrowed reviewer.</summary>
internal static class CursorBorrowedReviewCertification {
    internal const string Version = "2026.07.20-8cc9c0b";
    internal const string LauncherSha256 = "eed61c5224668c9236334c4c68936a16aecc37374b592f59e31eb50433817831";
    // SHA-256 of sorted UTF-8 lines: "<file-sha256>  ./<relative-path>\n".
    internal const string BundleDigest = "1dd66852ef6c94a0344226fa733f6fc1f3552a8ccf16dd000e1e38134575e10b";
    internal const string Containment = "independent-snapshot";

    internal static CursorBorrowedReviewArtifact? TryCertify(string configuredPath) {
        if (!OperatingSystem.IsMacOS()) return null;
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return null;
        try {
            var resolved = CliResolver.ResolveExecutable(configuredPath);
            if (resolved is null) return null;
            var launcher = ResolveFinalLink(resolved);
            var versionDir = Directory.GetParent(launcher)?.FullName;
            if (versionDir is null || !Path.GetFileName(versionDir).Equals(Version, StringComparison.Ordinal)) return null;
            if (!SafeUnixPath(launcher, versionDir)) return null;
            if (!Sha256File(launcher).Equals(LauncherSha256, StringComparison.Ordinal)) return null;
            var digest = ComputeBundleDigest(versionDir);
            return digest.Equals(BundleDigest, StringComparison.Ordinal)
                ? new CursorBorrowedReviewArtifact(Version, launcher, digest)
                : null;
        } catch {
            return null;
        }
    }

    internal static string ComputeBundleDigest(string versionDir) {
        var lines = Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(versionDir, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(x => !x.Relative.Equals(".running", StringComparison.Ordinal) &&
                        !x.Relative.StartsWith(".running/", StringComparison.Ordinal))
            .OrderBy(x => x.Relative, StringComparer.Ordinal)
            .Select(x => $"{Sha256File(x.Path)}  ./{x.Relative}\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines) hash.AppendData(Encoding.UTF8.GetBytes(line));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    static string ResolveFinalLink(string path) {
        var info = new FileInfo(path);
        var final = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(final?.FullName ?? info.FullName);
    }

    [SupportedOSPlatform("macos")]
    static bool SafeUnixPath(string launcher, string versionDir) {
        var paths = new List<string> { launcher };
        for (DirectoryInfo? dir = new(versionDir); dir is not null; dir = dir.Parent)
            paths.Add(dir.FullName);
        foreach (var path in paths) {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
        }
        return true;
    }

    static string Sha256File(string path) {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
