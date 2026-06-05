using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class VersionNudgeEmitterTests {
    static JsonNode? ResponseWithVersion(string? version) {
        if (version is null) return JsonNode.Parse("{}");
        return JsonNode.Parse($$"""{ "version": {{System.Text.Json.JsonSerializer.Serialize(version)}} }""");
    }

    [Test]
    public async Task Returns_null_when_response_node_is_null() {
        var result = VersionNudgeEmitter.BuildFragment(responseNode: null, currentCliVersion: "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_absent() {
        var result = VersionNudgeEmitter.BuildFragment(JsonNode.Parse("{}"), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_empty_string() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion(""), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_whitespace() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("   "), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_unparseable() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("not-a-version"), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_equals_server() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_is_strictly_newer() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.7.0");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_is_unknown_literal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "unknown");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_prerelease_makes_cores_equal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5-alpha.1");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_build_metadata_makes_cores_equal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5+abcdef");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_fragment_when_server_strictly_newer() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3");
        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Fragment_contains_both_versions() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result).Contains("0.6.3");
        await Assert.That(result).Contains("0.6.5");
    }

    [Test]
    public async Task Fragment_contains_npm_install_command() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result).Contains("npm install -g @kurrent/kcap");
    }

    [Test]
    public async Task Fragment_is_plain_text_not_json() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result.TrimStart().StartsWith("{")).IsFalse();
        await Assert.That(result).DoesNotContain("hookSpecificOutput");
    }
}
