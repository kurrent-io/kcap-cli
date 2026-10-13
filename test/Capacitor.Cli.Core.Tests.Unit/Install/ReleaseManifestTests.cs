using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.Tests.Unit.Install;

/// <summary>The manifest <c>scripts/build-cli-manifest.sh</c> writes reads back; anything that names no valid
/// version is not a manifest, so an update can never install from it.</summary>
public class ReleaseManifestTests {
    const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task The_release_job_shape_parses() {
        var manifest = ReleaseManifest.Parse($$$"""
            {"commit":"abc","platforms":{
              "osx-arm64":{"sha256":"{{{Sha}}}","size":1,"url":"https://example.invalid/kcap-osx-arm64.tar.gz"},
              "win-x64":{"sha256":"{{{Sha}}}","size":1,"url":"https://example.invalid/kcap-win-x64.zip"}},
             "version":"1.4.0-beta.2"}
            """);

        await Assert.That(manifest).IsNotNull();
        await Assert.That(manifest!.Version).IsEqualTo("1.4.0-beta.2");
        await Assert.That(manifest.Sha256ByRid["osx-arm64"]).IsEqualTo(Sha);
        await Assert.That(manifest.Sha256ByRid.ContainsKey("win-x64")).IsTrue();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"platforms":{}}""")]
    [Arguments("""{"version":"../../etc"}""")]
    [Arguments("""{"version":1}""")]
    public async Task Anything_without_a_valid_version_is_not_a_manifest(string json) {
        await Assert.That(ReleaseManifest.Parse(json)).IsNull();
    }

    [Test]
    public async Task Only_a_lowercase_64_hex_digest_is_a_checksum() {
        await Assert.That(ReleaseManifest.IsValidSha256(Sha)).IsTrue();
        await Assert.That(ReleaseManifest.IsValidSha256(Sha.ToUpperInvariant())).IsFalse();
        await Assert.That(ReleaseManifest.IsValidSha256(Sha[..63])).IsFalse();
        await Assert.That(ReleaseManifest.IsValidSha256(null)).IsFalse();
    }
}
