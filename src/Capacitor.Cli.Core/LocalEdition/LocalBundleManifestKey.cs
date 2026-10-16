using System.Security.Cryptography;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The public key that signs a local-edition bundle manifest: ECDSA P-256 over SHA-256, the
/// <c>.sig</c> file holding the DER signature in base64, over the manifest's bytes as downloaded.
/// The release workflow refuses to sign with a private key that does not match this resource, so a
/// CLI can always verify the manifest of its own version.
/// </summary>
public static class LocalBundleManifestKey {
    public const string ResourceName = "local-bundle-manifest-key.pem";

    public static ECDsa Load() => FromPem(EmbeddedResources.Load(ResourceName));

    public static ECDsa FromPem(string pem) {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    /// <summary>The manifest's <c>key_id</c>: lowercase hex SHA-256 of the SubjectPublicKeyInfo.</summary>
    public static string KeyId(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    public static bool Verify(ECDsa key, ReadOnlySpan<byte> manifest, string signatureBase64) {
        byte[] signature;

        try {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        } catch (FormatException) {
            return false;
        }

        return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
