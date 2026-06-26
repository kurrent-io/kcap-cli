using Capacitor.Cli.Core;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Tests.Unit.Codex;

// EnableNetworkAccess/TrustWorktree take an explicit config path, so these tests
// use a temp file and never touch HOME — safe to run in parallel.
public class CodexConfigTomlTests {
    static string TempConfig() =>
        Path.Combine(Directory.CreateTempSubdirectory("kcap-codextoml-").FullName, "config.toml");

    static TomlTable ReadToml(string path) =>
        TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;

    // ── BuildAllowDomains ────────────────────────────────────────────────────

    [Test]
    public async Task BuildAllowDomains_collapses_kcap_ai_tenants_to_one_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://acme.kcap.ai", "https://globex.kcap.ai"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "**.kcap.ai" });
    }

    [Test]
    public async Task BuildAllowDomains_keeps_self_hosted_hosts_exact_and_sorted_after_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://team.kcap.ai", "https://kcap.internal.corp", "https://capacitor.example.com"
        ]);

        // Wildcard first (kcap.ai tenant present), then self-hosted hosts sorted.
        await Assert.That(domains).IsEquivalentTo(new[] {
            "**.kcap.ai", "capacitor.example.com", "kcap.internal.corp"
        });
    }

    [Test]
    public async Task BuildAllowDomains_pure_self_hosted_has_no_kcap_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://capacitor.example.com:8443"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "capacitor.example.com" });
    }

    [Test]
    public async Task BuildAllowDomains_skips_null_blank_and_dedupes() {
        var domains = CodexConfigToml.BuildAllowDomains([
            null, "", "  ", "https://capacitor.example.com", "https://capacitor.example.com"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "capacitor.example.com" });
    }

    [Test]
    public async Task BuildAllowDomains_accepts_bare_host_without_scheme() {
        var domains = CodexConfigToml.BuildAllowDomains(["my-tenant.kcap.ai", "self.example.com"]);

        await Assert.That(domains).IsEquivalentTo(new[] { "**.kcap.ai", "self.example.com" });
    }

    // ── EnableNetworkAccess: default config ──────────────────────────────────

    [Test]
    public async Task EnableNetworkAccess_on_missing_config_writes_access_and_proxy_allowlist() {
        var path = TempConfig();

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root  = ReadToml(path);
        var sww   = (TomlTable)root["sandbox_workspace_write"];
        await Assert.That((bool)sww["network_access"]).IsTrue();

        var proxy = (TomlTable)((TomlTable)root["features"])["network_proxy"];
        await Assert.That((bool)proxy["enabled"]).IsTrue();
        await Assert.That((string)((TomlTable)proxy["domains"])["**.kcap.ai"]).IsEqualTo("allow");
    }

    [Test]
    public async Task EnableNetworkAccess_is_idempotent() {
        var path = TempConfig();

        var first  = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);
        var second = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(first).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(second).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    [NotInParallel("CwdMutation")]
    public async Task EnableNetworkAccess_writes_when_config_path_has_no_directory_component() {
        // GetDirectoryName("config.toml") is empty; CreateDirectory("") would throw and
        // silently turn the write into Change.Failed without the guard.
        var dir         = Directory.CreateTempSubdirectory("kcap-codextoml-cwd-").FullName;
        var originalCwd = Environment.CurrentDirectory;

        try {
            Environment.CurrentDirectory = dir;

            var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], "config.toml");

            await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
            await Assert.That(File.Exists(Path.Combine(dir, "config.toml"))).IsTrue();
        } finally {
            Environment.CurrentDirectory = originalCwd;
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task EnableNetworkAccess_empty_allowlist_is_noop() {
        var path = TempConfig();

        var change = CodexConfigToml.EnableNetworkAccess([], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    // ── EnableNetworkAccess: respect existing config ─────────────────────────

    [Test]
    public async Task EnableNetworkAccess_fully_open_no_proxy_is_left_untouched() {
        var path = TempConfig();
        File.WriteAllText(path,
            """
            [sandbox_workspace_write]
            network_access = true
            """);
        var before = File.ReadAllText(path);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task EnableNetworkAccess_merges_into_existing_proxy_preserving_user_entries() {
        var path = TempConfig();
        File.WriteAllText(path,
            """
            model = "gpt-5.5"

            [sandbox_workspace_write]
            network_access = true

            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "github.com" = "allow"
            """);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai", "self.example.com"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root    = ReadToml(path);
        await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");

        var domains = (TomlTable)((TomlTable)((TomlTable)root["features"])["network_proxy"])["domains"];
        await Assert.That((string)domains["github.com"]).IsEqualTo("allow");     // user's preserved
        await Assert.That((string)domains["**.kcap.ai"]).IsEqualTo("allow");     // ours added
        await Assert.That((string)domains["self.example.com"]).IsEqualTo("allow");
    }

    [Test]
    public async Task EnableNetworkAccess_existing_proxy_without_network_access_turns_it_on() {
        var path = TempConfig();
        File.WriteAllText(path,
            """
            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "github.com" = "allow"
            """);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root = ReadToml(path);
        await Assert.That((bool)((TomlTable)root["sandbox_workspace_write"])["network_access"]).IsTrue();
    }

    [Test]
    public async Task EnableNetworkAccess_malformed_config_is_not_overwritten() {
        var path = TempConfig();
        const string garbage = "{{{ not valid TOML";
        File.WriteAllText(path, garbage);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(garbage);
    }
}
