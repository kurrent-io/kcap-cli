using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The built-in model set a local-edition version offers, read from its signed bundle manifest.</summary>
public sealed class LocalEmbeddingOffer(LocalBundleClient bundles, ECDsa manifestKey, string rid) {
    /// <returns>Null when the manifest names none, or cannot be fetched or verified: built-in is then not offered.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not a release version.</exception>
    public async Task<LocalEmbeddingModelManifest?> LookupAsync(string version, CancellationToken ct) {
        try {
            var (bytes, signature) = await bundles.FetchManifestAsync(version, rid, ct);

            return LocalBundleManifest.Verify(bytes, signature, manifestKey, version, rid).EmbeddingModel;
        } catch (LocalBundleException) {
            return null;
        }
    }
}
