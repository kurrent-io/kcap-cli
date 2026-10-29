using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>A bundle publication: a manifest signed by a throwaway key over its exact bytes, its signature, and a
/// <see cref="TestBundles"/> archive holding an executable <c>kcap-server</c> unless other entries are given.</summary>
sealed class FakeBundle : HttpMessageHandler {
    public const string BaseUrl = "https://bundles.test/local";

    readonly string _version;
    readonly byte[] _archive;
    readonly string _rid;

    public FakeBundle(string archivePath, string version, string rid = "osx-arm64",
        params (string Name, System.Formats.Tar.TarEntryType Type, string? Content, string? LinkName)[] entries) {
        _rid     = rid;
        _version = version;

        TestBundles.Write(archivePath, entries.Length > 0 ? entries : [TestBundles.RegularFile("./kcap-server", "#!/bin/sh\n")]);
        _archive = File.ReadAllBytes(archivePath);
    }

    /// <summary>The manifest's <c>embedding_model</c> entry; none when null, unless <see cref="ModelFiles"/> is set.</summary>
    public JsonObject? EmbeddingModel { get; init; }

    /// <summary>A built-in model set this publication serves under <see cref="ModelSet"/>, and names in its manifest.</summary>
    public IReadOnlyDictionary<string, byte[]>? ModelFiles { get; init; }

    public string ModelSet { get; init; } = new('e', 64);

    JsonObject? ModelEntry => EmbeddingModel ?? (ModelFiles is null ? null : new JsonObject {
        ["model_id"]  = "builtin-test",
        ["contract"]  = 1,
        ["model_set"] = ModelSet,
        ["files"]     = new JsonArray([.. ModelFiles.Select(f => (JsonNode)new JsonObject {
            ["name"] = f.Key, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Value)), ["size"] = f.Value.Length,
        })]),
    });

    byte[] Manifest => field ??= BuildManifest();

    byte[] BuildManifest() {
        var manifest = new JsonObject {
            ["schema"]  = 1,
            ["key_id"]  = LocalBundleManifestKey.KeyId(Key),
            ["version"] = _version,
            ["rid"]     = _rid,
            ["archive"] = $"kcap-local-{_version}-{_rid}.tar.gz",
            ["sha256"]  = Convert.ToHexStringLower(SHA256.HashData(_archive)),
            ["size"]    = _archive.Length,
        };

        if (ModelEntry is { } entry) manifest["embedding_model"] = entry.DeepClone();

        return Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    string Signature => field ??= Convert.ToBase64String(Key.SignData(Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The manifest answers 404, as an unpublished version does.</summary>
    public bool ManifestMissing { get; set; }

    public List<string> Requests { get; } = [];

    public LocalBundleClient Client() => new(new HttpClient(this), TimeProvider.System) { BaseUrl = BaseUrl };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(path);

        if (ModelFiles is not null && path.StartsWith($"/local/models/{ModelSet}/", StringComparison.Ordinal))
            return Task.FromResult(ModelFiles.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        if (!path.StartsWith($"/local/{_rid}/", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        HttpResponseMessage response = path switch {
            _ when path.EndsWith(".json", StringComparison.Ordinal) && ManifestMissing => new(HttpStatusCode.NotFound),
            _ when path.EndsWith(".json", StringComparison.Ordinal)     => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest) },
            _ when path.EndsWith(".json.sig", StringComparison.Ordinal) => new(HttpStatusCode.OK) { Content = new StringContent(Signature) },
            _ when path.EndsWith(".tar.gz", StringComparison.Ordinal)   => new(HttpStatusCode.OK) { Content = new ByteArrayContent(_archive) },
            _                                                           => new(HttpStatusCode.NotFound),
        };

        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) Key.Dispose();
        base.Dispose(disposing);
    }
}
