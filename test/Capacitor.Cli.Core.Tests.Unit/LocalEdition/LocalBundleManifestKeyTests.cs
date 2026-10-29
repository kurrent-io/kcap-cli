using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalBundleManifestKeyTests {
    // A throwaway key and a signature made by scripts/local-manifest-key.sh, so the format the
    // release workflow writes is the one this class reads.
    const string VectorPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGlW5e0SyuhAnNn5W14CJrX8NQiXg
        VlTJ8aj3l4V7XsEFbZxTpS8dDMS/A7CDB0tEU39YT17W54xNC+pMFL5bUQ==
        -----END PUBLIC KEY-----
        """;
    const string VectorManifest  = "{\"schema\":1,\"version\":\"1.0.0\"}\n";
    const string VectorSignature = "MEQCIFapJTlqZBprAKl0I5YVrZEGgHBzXDwAe5aU9p2NIxCqAiAVotpk0Foos9sQhUJPTJzbPB13biQARgrnMdWWilZxBA==";
    const string VectorKeyId     = "a14ba296bce671b8612eb5c68e39df6e34cddb2946be3cecb87ca1138b00a607";

    [Test]
    public async Task Embedded_key_is_a_P256_public_key() {
        using var key = LocalBundleManifestKey.Load();

        await Assert.That(key.KeySize).IsEqualTo(256);
        await Assert.That(key.ExportParameters(false).Curve.Oid.Value).IsEqualTo(ECCurve.NamedCurves.nistP256.Oid.Value);
    }

    [Test]
    public async Task Verifies_a_signature_made_by_the_release_script() {
        using var key = LocalBundleManifestKey.FromPem(VectorPublicKey);

        await Assert.That(LocalBundleManifestKey.Verify(key, Encoding.UTF8.GetBytes(VectorManifest), VectorSignature)).IsTrue();
    }

    [Test]
    public async Task Rejects_a_changed_manifest() {
        using var key = LocalBundleManifestKey.FromPem(VectorPublicKey);
        var changed = Encoding.UTF8.GetBytes(VectorManifest.Replace("1.0.0", "1.0.1"));

        await Assert.That(LocalBundleManifestKey.Verify(key, changed, VectorSignature)).IsFalse();
    }

    [Test]
    public async Task Rejects_a_signature_that_is_not_base64() {
        using var key = LocalBundleManifestKey.FromPem(VectorPublicKey);

        await Assert.That(LocalBundleManifestKey.Verify(key, Encoding.UTF8.GetBytes(VectorManifest), "not base64!")).IsFalse();
    }

    /// <summary>Base64 that decodes to bytes no DER parser accepts reads as a rejected signature, not a crypto error.</summary>
    [Test]
    [Arguments("MAA=")]
    [Arguments("AAECAwQ=")]
    [Arguments("MEQCIFapJTlqZBprAKl0I5YVrZEGgHBzXDwAe5aU9p2NIxCq")]
    [Arguments("MEUCIQ==")]
    public async Task Rejects_a_signature_that_is_not_DER(string signature) {
        using var key = LocalBundleManifestKey.FromPem(VectorPublicKey);

        await Assert.That(LocalBundleManifestKey.Verify(key, Encoding.UTF8.GetBytes(VectorManifest), signature)).IsFalse();
    }

    [Test]
    public async Task Key_id_matches_the_manifest_builder() {
        using var key = LocalBundleManifestKey.FromPem(VectorPublicKey);

        await Assert.That(LocalBundleManifestKey.KeyId(key)).IsEqualTo(VectorKeyId);
    }
}
