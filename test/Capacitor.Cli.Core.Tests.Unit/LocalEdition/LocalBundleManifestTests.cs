using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalBundleManifestTests {
    const string Version = "1.3.0";
    const string Rid     = "osx-arm64";
    static readonly string Sha = new('a', 64);

    static string Json(ECDsa key, string version = Version, string rid = Rid, string? archive = null, string? sha = null,
                       long size = 10, int schema = 1, string? keyId = null) =>
        $$"""{"archive":"{{archive ?? $"kcap-local-{Version}-{Rid}.tar.gz"}}","key_id":"{{keyId ?? LocalBundleManifestKey.KeyId(key)}}","rid":"{{rid}}","schema":{{schema}},"sha256":"{{sha ?? Sha}}","size":{{size}},"version":"{{version}}"}""";

    static (byte[] Bytes, string Sig) Sign(ECDsa key, string json) {
        var bytes = Encoding.UTF8.GetBytes(json);

        return (bytes, Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    static async Task AssertRefused(Func<ECDsa, string> build, Func<(byte[] Bytes, string Sig), (byte[] Bytes, string Sig)>? tamper = null) {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (bytes, sig) = Sign(key, build(key));
        if (tamper is not null) (bytes, sig) = tamper((bytes, sig));

        var e = Assert.Throws<LocalBundleException>(() => LocalBundleManifest.Verify(bytes, sig, key, Version, Rid));
        await Assert.That(e.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
    }

    [Test]
    public async Task A_good_manifest_verifies() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (bytes, sig) = Sign(key, Json(key));

        var m = LocalBundleManifest.Verify(bytes, sig, key, Version, Rid);

        await Assert.That(m).IsEqualTo(new LocalBundleManifest(Version, Rid, "kcap-local-1.3.0-osx-arm64.tar.gz", Sha, 10, LocalBundleManifestKey.KeyId(key)));
    }

    [Test]
    public Task A_bad_signature_is_refused() => AssertRefused(k => Json(k), t => (t.Bytes, "AAAA"));

    [Test]
    public Task A_manifest_changed_after_signing_is_refused() =>
        AssertRefused(k => Json(k), t => (Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(t.Bytes).Replace("\"size\":10", "\"size\":11")), t.Sig));

    [Test]
    public Task A_foreign_key_id_is_refused() => AssertRefused(k => Json(k, keyId: new string('b', 64)));

    [Test]
    public Task The_wrong_version_is_refused() => AssertRefused(k => Json(k, version: "1.3.1"));

    [Test]
    public Task The_wrong_rid_is_refused() => AssertRefused(k => Json(k, rid: "linux-x64"));

    [Test]
    public Task The_wrong_archive_name_is_refused() => AssertRefused(k => Json(k, archive: "evil.tar.gz"));

    [Test]
    public Task A_malformed_sha_is_refused() => AssertRefused(k => Json(k, sha: "xyz"));

    [Test]
    public Task A_zero_size_is_refused() => AssertRefused(k => Json(k, size: 0));

    [Test]
    public Task Schema_2_is_refused() => AssertRefused(k => Json(k, schema: 2));

    [Test]
    public Task Non_json_is_refused() => AssertRefused(_ => "not json");

    const string ModelJson = """{"model_id":"mxbai-embed-large-v1","contract":1,"model_set":"%SET%","files":[{"name":"model.onnx","sha256":"%SHA%","size":336983163,"source_url":"https://x/y"},{"name":"vocab.txt","sha256":"%SHA%","size":231508,"source_url":"https://x/z"}]}""";

    static string WithModel(ECDsa key, string model) =>
        Json(key)[..^1] + $$""","embedding_model":{{model}}}""";

    static string Model(string set = "", string sha = "") =>
        ModelJson.Replace("%SET%", set.Length > 0 ? set : new string('b', 64)).Replace("%SHA%", sha.Length > 0 ? sha : new string('c', 64));

    [Test]
    public async Task A_manifest_without_an_embedding_model_has_none() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (bytes, sig) = Sign(key, Json(key));

        await Assert.That(LocalBundleManifest.Verify(bytes, sig, key, Version, Rid).EmbeddingModel).IsNull();
    }

    [Test]
    public async Task A_valid_embedding_model_is_parsed() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (bytes, sig) = Sign(key, WithModel(key, Model()));

        var m = LocalBundleManifest.Verify(bytes, sig, key, Version, Rid).EmbeddingModel!;

        await Assert.That(m.ModelId).IsEqualTo("mxbai-embed-large-v1");
        await Assert.That(m.Contract).IsEqualTo(1);
        await Assert.That(m.ModelSet).IsEqualTo(new string('b', 64));
        await Assert.That(m.Files.Select(f => f.Name)).IsEquivalentTo(["model.onnx", "vocab.txt"]);
        await Assert.That(m.Files[0].Size).IsEqualTo(336983163L);
        await Assert.That(m.TotalSize).IsEqualTo(336983163L + 231508);
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"bad","files":[]}""")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"%SET%","files":[]}""")]
    [Arguments("""{"model_id":"m","contract":0,"model_set":"%SET%","files":[{"name":"a","sha256":"%SET%","size":1}]}""")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"%SET%","files":[{"name":"../a","sha256":"%SET%","size":1}]}""")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"%SET%","files":[{"name":"a","sha256":"xyz","size":1}]}""")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"%SET%","files":[{"name":"a","sha256":"%SET%","size":0}]}""")]
    [Arguments("""{"model_id":"m","contract":1,"model_set":"%SET%","files":[{"name":"a","sha256":"%SET%","size":1},{"name":"A","sha256":"%SET%","size":1}]}""")]
    public Task A_malformed_embedding_model_is_refused(string entry) =>
        AssertRefused(k => WithModel(k, entry.Replace("%SET%", new string('b', 64))));

    [Test]
    public async Task Every_failure_code_has_a_distinct_snake_case_wire_name() {
        var names = Enum.GetValues<LocalInstallFailureCode>().Select(c => c.Wire()).ToList();

        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count);
        await Assert.That(names.Count).IsEqualTo(20);
        await Assert.That(names.All(n => n.All(ch => char.IsAsciiLetterLower(ch) || ch == '_'))).IsTrue();
        await Assert.That(LocalInstallFailureCode.BundleNotPublished.Wire()).IsEqualTo("bundle_not_published");
        await Assert.That(LocalInstallFailureCode.SocketPathTooLong.Wire()).IsEqualTo("socket_path_too_long");
        await Assert.That(LocalInstallFailureCode.ClientBelowFloor.Wire()).IsEqualTo("client_below_floor");
        await Assert.That(LocalInstallFailureCode.Cancelled.Wire()).IsEqualTo("cancelled");
    }
}
