using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Downloads, verifies and unpacks one bundle version into <c>bin/&lt;version&gt;</c>. It never touches
/// <c>bin/current</c>: setup switches it straight away, an upgrade only once the old server is down.
///
/// <para><c>cache/</c> holds a bundle fetched ahead of an install. Only its archive is staged, and only when the
/// archive's digest matches a manifest verified in this run. A successful stage empties it.</para></summary>
sealed class LocalBundleStager(LocalRoot root, LocalBundleClient bundles, ECDsa manifestKey, LocalPlatform platform) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    string Bin => LocalBin.Directory(root);

    public static string CacheDirectory(LocalRoot root) => Path.Combine(root.Directory, "cache");

    /// <summary>Fetches model sets from the publication this stager stages from.</summary>
    public LocalModelSetDownloader ModelSets() => new(root, bundles);

    /// <summary>Durable before it returns. A <c>bin/&lt;version&gt;</c> left by an earlier attempt is replaced, so the
    /// caller must never stage the version <c>bin/current</c> points at.</summary>
    /// <returns>The version directory, and the verified manifest it was staged from.</returns>
    public async Task<(string VersionDirectory, LocalBundleManifest Manifest)> StageAsync(string version, string stagingName,
        IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        var staging = LocalBin.StagingDir(root, stagingName);
        var rid     = platform.Rid;

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        DurableDirectory.Ensure(staging, root.Directory, OwnerOnly);

        var (manifest, _, _) = await VerifiedManifestAsync(version, rid, fromCache: true, ct);
        var archive          = Path.Combine(staging, "archive.tar.gz");

        if (!await TakeCachedAsync(manifest, archive, ct)) {
            progress.Report(new LocalInstallEvent.Downloading($"{bundles.BaseUrl}/{rid}/{manifest.Archive}", manifest.Size));

            await bundles.DownloadAsync(manifest, archive,
                new SyncProgress<long>(received => progress.Report(new LocalInstallEvent.DownloadProgress(received, manifest.Size))), ct);
        }

        progress.Report(new LocalInstallEvent.Verified(manifest.Sha256));

        var tree = Path.Combine(staging, "tree");
        await LocalBundleUnpacker.UnpackAsync(archive, tree, ct);

        var versionDir = Path.Combine(Bin, version);
        if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);

        Directory.Move(tree, versionDir);
        DurableDirectory.Sync(Bin);

        Directory.Delete(staging, recursive: true);
        DurableDirectory.Sync(Bin);

        DropCache();

        progress.Report(new LocalInstallEvent.Unpacked(versionDir));

        return (versionDir, manifest);
    }

    /// <summary>Downloads and verifies <paramref name="version"/>'s bundle into <c>cache/</c> beside its manifest and
    /// signature, removing whatever else is there. An archive already there with the right digest is kept.</summary>
    public async Task<LocalBundleManifest> FetchAsync(string version, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        var rid                          = platform.Rid;
        var (manifest, bytes, signature) = await VerifiedManifestAsync(version, rid, fromCache: false, ct);
        var cache                        = CacheDirectory(root);
        var manifestName                 = ManifestName(version, rid);

        root.EnsureCreated();
        DurableDirectory.Ensure(cache, root.Directory, OwnerOnly);

        string[] keep = [manifest.Archive, manifestName, manifestName + ".sig"];
        foreach (var file in Directory.EnumerateFiles(cache).Where(f => !keep.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList())
            File.Delete(file);

        var archive = Path.Combine(cache, manifest.Archive);

        if (!await MatchesAsync(archive, manifest, ct)) {
            if (File.Exists(archive)) File.Delete(archive);

            var partial = archive + ".partial";
            if (File.Exists(partial)) File.Delete(partial);

            progress.Report(new LocalInstallEvent.Downloading($"{bundles.BaseUrl}/{rid}/{manifest.Archive}", manifest.Size));

            await bundles.DownloadAsync(manifest, partial,
                new SyncProgress<long>(received => progress.Report(new LocalInstallEvent.DownloadProgress(received, manifest.Size))), ct);

            File.Move(partial, archive);
        }

        progress.Report(new LocalInstallEvent.Verified(manifest.Sha256));

        await File.WriteAllBytesAsync(Path.Combine(cache, manifestName), bytes, ct);
        await File.WriteAllTextAsync(Path.Combine(cache, manifestName + ".sig"), signature, ct);
        DurableDirectory.Sync(cache);

        progress.Report(new LocalInstallEvent.Fetched(version, cache));

        return manifest;
    }

    /// <summary>The manifest from the publication or, with <paramref name="fromCache"/> and the publication out of
    /// reach, the one a fetch cached: it carries the same signature and is checked exactly as a downloaded one is.</summary>
    async Task<(LocalBundleManifest Manifest, byte[] Bytes, string Signature)> VerifiedManifestAsync(string version, string rid, bool fromCache,
        CancellationToken ct) {
        byte[] bytes;
        string signature;

        try {
            (bytes, signature) = await bundles.FetchManifestAsync(version, rid, ct);
        } catch (ArgumentException ex) {
            throw new LocalInstallFailure(LocalInstallFailureCode.InvalidArguments, ex.Message);
        } catch (LocalBundleException ex) when (fromCache && ex.Code == LocalInstallFailureCode.DownloadFailed && CachedManifest(version, rid) is { } cached) {
            (bytes, signature) = cached;
        }

        return (LocalBundleManifest.Verify(bytes, signature, manifestKey, version, rid), bytes, signature);
    }

    (byte[] Bytes, string Signature)? CachedManifest(string version, string rid) {
        var path = Path.Combine(CacheDirectory(root), ManifestName(version, rid));

        try {
            return (File.ReadAllBytes(path), File.ReadAllText(path + ".sig"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static string ManifestName(string version, string rid) => $"kcap-local-{version}-{rid}.json";

    /// <summary>Moved first and checked where it lands, so nothing can swap the file between the check and its use.</summary>
    async Task<bool> TakeCachedAsync(LocalBundleManifest manifest, string destination, CancellationToken ct) {
        var cached = Path.Combine(CacheDirectory(root), manifest.Archive);

        try {
            if (!File.Exists(cached)) return false;

            File.Move(cached, destination);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }

        if (await MatchesAsync(destination, manifest, ct)) return true;

        File.Delete(destination);
        return false;
    }

    static async Task<bool> MatchesAsync(string archive, LocalBundleManifest manifest, CancellationToken ct) {
        try {
            await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != manifest.Size) return false;

            var digest = await SHA256.HashDataAsync(stream, ct);
            return string.Equals(Convert.ToHexStringLower(digest), manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Best effort: a fetch still writing into it leaves it for the next stage.</summary>
    void DropCache() {
        try {
            if (Directory.Exists(CacheDirectory(root))) Directory.Delete(CacheDirectory(root), recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
