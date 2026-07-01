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

    // ── RegisterKcapMcpServers ───────────────────────────────────────────────

    static string[] ArgsOf(TomlTable server) =>
        ((TomlArray)server["args"]).Select(v => (string)v!).ToArray();

    [Test]
    public async Task RegisterKcapMcpServers_on_missing_config_writes_both_servers() {
        var path = TempConfig();

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        var review  = (TomlTable)servers["kcap-review"];
        var sessions = (TomlTable)servers["kcap-sessions"];

        await Assert.That((string)review["command"]).IsEqualTo("kcap");
        await Assert.That(ArgsOf(review)).IsEquivalentTo(new[] { "mcp", "review" });
        await Assert.That((string)sessions["command"]).IsEqualTo("kcap");
        await Assert.That(ArgsOf(sessions)).IsEquivalentTo(new[] { "mcp", "sessions" });
    }

    [Test]
    public async Task RegisterKcapMcpServers_emits_snake_case_mcp_servers_table() {
        // Codex config.toml uses the snake_case `mcp_servers` table — NOT the
        // camelCase `mcpServers` key the plugin *descriptor* JSON requires.
        var path = TempConfig();

        CodexConfigToml.RegisterKcapMcpServers(path);

        var text = File.ReadAllText(path);
        await Assert.That(text).Contains("[mcp_servers.kcap-review]");
        await Assert.That(text).Contains("[mcp_servers.kcap-sessions]");
        await Assert.That(text).DoesNotContain("mcpServers");
    }

    [Test]
    public async Task RegisterKcapMcpServers_is_idempotent() {
        var path = TempConfig();

        var first  = CodexConfigToml.RegisterKcapMcpServers(path);
        var second = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(first).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(second).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    public async Task RegisterKcapMcpServers_preserves_user_config_and_servers() {
        var path = TempConfig();
        File.WriteAllText(path,
            """
            model = "gpt-5.5"

            [mcp_servers.my-tool]
            command = "my-tool"
            args = ["serve"]
            """);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root    = ReadToml(path);
        await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");

        var servers = (TomlTable)root["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["my-tool"])["command"]).IsEqualTo("my-tool"); // user's preserved
        await Assert.That(servers.ContainsKey("kcap-review")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
    }

    [Test]
    public async Task RegisterKcapMcpServers_does_not_clobber_existing_kcap_entry() {
        // A user who set an absolute-path command (e.g. for a GUI host) must keep it.
        var path = TempConfig();
        File.WriteAllText(path,
            """
            [mcp_servers.kcap-sessions]
            command = "/opt/homebrew/bin/kcap"
            args = ["mcp", "sessions"]
            """);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        // kcap-review added; kcap-sessions left as-is → overall Updated.
        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["kcap-sessions"])["command"]).IsEqualTo("/opt/homebrew/bin/kcap");
        await Assert.That((string)((TomlTable)servers["kcap-review"])["command"]).IsEqualTo("kcap");
    }

    [Test]
    public async Task RegisterKcapMcpServers_non_table_mcp_servers_is_failure_not_clobber() {
        // A non-table `mcp_servers` value must not be silently replaced (honours the
        // non-destructive contract) — register fails and leaves the file untouched.
        var path = TempConfig();
        const string content = "mcp_servers = \"oops\"\n";
        File.WriteAllText(path, content);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(content);
    }

    [Test]
    public async Task RegisterKcapMcpServers_malformed_config_is_not_overwritten() {
        var path = TempConfig();
        const string garbage = "{{{ not valid TOML";
        File.WriteAllText(path, garbage);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(garbage);
    }

    // ── UnregisterKcapMcpServers ─────────────────────────────────────────────

    [Test]
    public async Task UnregisterKcapMcpServers_removes_kcap_entries_and_drops_empty_table() {
        var path = TempConfig();
        CodexConfigToml.RegisterKcapMcpServers(path);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(ReadToml(path).ContainsKey("mcp_servers")).IsFalse();
    }

    [Test]
    public async Task UnregisterKcapMcpServers_preserves_user_servers() {
        var path = TempConfig();
        File.WriteAllText(path,
            """
            [mcp_servers.my-tool]
            command = "my-tool"
            args = ["serve"]
            """);
        CodexConfigToml.RegisterKcapMcpServers(path);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That(servers.ContainsKey("my-tool")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-review")).IsFalse();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsFalse();
    }

    [Test]
    public async Task UnregisterKcapMcpServers_is_noop_when_absent() {
        var path = TempConfig();
        File.WriteAllText(path, """model = "gpt-5.5" """);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }
}
