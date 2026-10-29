using System.Net;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalEmbeddingOfferTests {
    const string Version = "1.3.0";
    const string Rid     = "osx-arm64";

    static readonly string ModelEntry =
        $$""","embedding_model":{"model_id":"mxbai-embed-large-v1","contract":1,"model_set":"{{new string('c', 64)}}","files":[{"name":"model.onnx","sha256":"{{new string('d', 64)}}","size":42}]}""";

    static string Manifest(ECDsa key, string entry) =>
        $$"""{"archive":"kcap-local-{{Version}}-{{Rid}}.tar.gz","key_id":"{{LocalBundleManifestKey.KeyId(key)}}","rid":"{{Rid}}","schema":1,"sha256":"{{new string('a', 64)}}","size":10,"version":"{{Version}}"{{entry}}}""";

    static LocalEmbeddingOffer Offer(ECDsa key, Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new LocalBundleClient(new HttpClient(new Handler(reply)), TimeProvider.System) { BaseUrl = "https://bundles.test/local" }, key, Rid);

    static Func<HttpRequestMessage, HttpResponseMessage> Publishing(ECDsa key, string entry) {
        var bytes = Encoding.UTF8.GetBytes(Manifest(key, entry));
        var sig   = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        return r => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = r.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? new StringContent(sig) : new ByteArrayContent(bytes),
        };
    }

    [Test]
    public async Task A_manifest_naming_a_model_set_offers_it_with_its_size() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var offered = await Offer(key, Publishing(key, ModelEntry)).LookupAsync(Version, CancellationToken.None);

        await Assert.That(offered?.ModelSet).IsEqualTo(new string('c', 64));
        await Assert.That(offered?.TotalSize).IsEqualTo(42L);
    }

    [Test]
    public async Task A_manifest_without_a_model_entry_offers_none() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await Assert.That(await Offer(key, Publishing(key, "")).LookupAsync(Version, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_manifest_signed_by_another_key_offers_none() {
        using var key   = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await Assert.That(await Offer(key, Publishing(other, ModelEntry)).LookupAsync(Version, CancellationToken.None)).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task A_publication_that_does_not_answer_offers_none(HttpStatusCode status) {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await Assert.That(await Offer(key, _ => new HttpResponseMessage(status)).LookupAsync(Version, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_network_failure_offers_none() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await Assert.That(await Offer(key, _ => throw new HttpRequestException("down")).LookupAsync(Version, CancellationToken.None)).IsNull();
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
