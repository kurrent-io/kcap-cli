using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Downloads, verifies and unpacks one bundle version into <c>bin/&lt;version&gt;</c>. It never touches
/// <c>bin/current</c>: setup switches it straight away, an upgrade only once the old server is down.</summary>
sealed class LocalBundleStager(LocalRoot root, LocalBundleClient bundles, ECDsa manifestKey, LocalPlatform platform) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    string Bin => LocalBin.Directory(root);

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

        byte[] bytes;
        string signature;

        try {
            (bytes, signature) = await bundles.FetchManifestAsync(version, rid, ct);
        } catch (ArgumentException ex) {
            throw new LocalInstallFailure(LocalInstallFailureCode.InvalidArguments, ex.Message);
        }

        var manifest = LocalBundleManifest.Verify(bytes, signature, manifestKey, version, rid);

        progress.Report(new LocalInstallEvent.Downloading($"{bundles.BaseUrl}/{rid}/{manifest.Archive}", manifest.Size));

        var archive = Path.Combine(staging, "archive.tar.gz");
        await bundles.DownloadAsync(manifest, archive,
            new SyncProgress<long>(received => progress.Report(new LocalInstallEvent.DownloadProgress(received, manifest.Size))), ct);

        progress.Report(new LocalInstallEvent.Verified(manifest.Sha256));

        var tree = Path.Combine(staging, "tree");
        await LocalBundleUnpacker.UnpackAsync(archive, tree, ct);

        var versionDir = Path.Combine(Bin, version);
        if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);

        Directory.Move(tree, versionDir);
        DurableDirectory.Sync(Bin);

        Directory.Delete(staging, recursive: true);
        DurableDirectory.Sync(Bin);

        progress.Report(new LocalInstallEvent.Unpacked(versionDir));

        return (versionDir, manifest);
    }
}
