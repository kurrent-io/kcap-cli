using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The signed description of one local-edition bundle archive.</summary>
public sealed record LocalBundleManifest(string Version, string Rid, string Archive, string Sha256, long Size, string KeyId) {
    /// <summary>The built-in embedding model set this release ships with, when the manifest names one.</summary>
    public LocalEmbeddingModelManifest? EmbeddingModel { get; init; }

    /// <summary>Checks the signature over the bytes as downloaded, then that the manifest describes
    /// exactly the bundle that was asked for.</summary>
    public static LocalBundleManifest Verify(ReadOnlySpan<byte> manifest, string signature, ECDsa key, string version, string rid) {
        if (!LocalBundleManifestKey.Verify(key, manifest, signature))
            throw Refused("The bundle manifest's signature does not verify.");

        JsonObject obj;

        try {
            obj = JsonNode.Parse(manifest) as JsonObject ?? throw Refused("The bundle manifest is not a JSON object.");
        } catch (JsonException e) {
            throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, "The bundle manifest is not valid JSON.", e);
        }

        if (Int(obj, "schema") != 1) throw Refused("The bundle manifest has an unsupported schema.");

        var keyId = Str(obj, "key_id");
        if (keyId != LocalBundleManifestKey.KeyId(key)) throw Refused("The bundle manifest names a different signing key.");

        if (Str(obj, "version") != version) throw Refused("The bundle manifest is for a different version.");
        if (Str(obj, "rid") != rid) throw Refused("The bundle manifest is for a different platform.");

        var archive = Str(obj, "archive");
        if (archive != $"kcap-local-{version}-{rid}.tar.gz") throw Refused("The bundle manifest names an unexpected archive.");

        var sha = Str(obj, "sha256");
        if (!ReleaseManifest.IsValidSha256(sha)) throw Refused("The bundle manifest carries a malformed checksum.");

        var size = Int(obj, "size");
        if (size is null or <= 0) throw Refused("The bundle manifest carries no usable size.");

        LocalEmbeddingModelManifest? model = null;

        if (obj.TryGetPropertyValue("embedding_model", out var entry))
            model = LocalEmbeddingModelManifest.Parse(entry) ?? throw Refused("The bundle manifest carries a malformed embedding model entry.");

        return new(version, rid, archive, sha!, size.Value, keyId!) { EmbeddingModel = model };
    }

    static LocalBundleException Refused(string message) => new(LocalInstallFailureCode.VerificationFailed, message);

    static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long? Int(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}
