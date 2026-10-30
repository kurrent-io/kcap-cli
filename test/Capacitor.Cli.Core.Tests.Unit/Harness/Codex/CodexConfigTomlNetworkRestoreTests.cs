using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexConfigTomlNetworkRestoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Config => Tmp.GetResolvedPath("config.toml");
    string Ledger => Tmp.GetResolvedPath("mcp-ownership-v1.json");

    TomlTable ReadToml() => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(Config))!;

    static TomlTable Table(TomlTable root, params string[] path) {
        var table = root;
        foreach (var key in path) table = (TomlTable)table[key];
        return table;
    }

    [Test]
    public async Task Restore_after_install_on_a_default_config_leaves_only_the_users_keys() {
        File.WriteAllText(Config, """model = "gpt-5.5" """);
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai", "self.example.com"], Config);

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, []);

        await Assert.That(restore.Change).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(restore.Kept).IsEmpty();
        var root = ReadToml();
        await Assert.That(root.Keys.ToArray()).IsEquivalentTo(["model"]);
        await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");
    }

    [Test]
    public async Task Install_records_the_replaced_value_and_restore_puts_it_back() {
        File.WriteAllText(Config,
            """
            [sandbox_workspace_write]
            network_access = false
            writable_roots = ["/tmp/x"]
            """);

        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);
        var setting = JsonNode.Parse(File.ReadAllText(Ledger))!["sandbox_network"]!["settings"]!.AsArray()
            .Single(s => s!["path"]!.ToJsonString() == """["sandbox_workspace_write","network_access"]""")!;
        await Assert.That(setting["previous"]!["value"]!.GetValue<bool>()).IsFalse();

        CodexConfigToml.RestoreNetworkAccess(Config, []);

        var sandbox = Table(ReadToml(), "sandbox_workspace_write");
        await Assert.That((bool)sandbox["network_access"]).IsFalse();
        await Assert.That(sandbox.ContainsKey("writable_roots")).IsTrue();
        await Assert.That(ReadToml().ContainsKey("features")).IsFalse();
    }

    [Test]
    public async Task A_second_install_keeps_the_value_from_before_the_first() {
        File.WriteAllText(Config, "[sandbox_workspace_write]\nnetwork_access = false\n");
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai", "self.example.com"], Config);

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, []);

        await Assert.That(restore.Kept).IsEmpty();
        await Assert.That((bool)Table(ReadToml(), "sandbox_workspace_write")["network_access"]).IsFalse();
        await Assert.That(ReadToml().ContainsKey("features")).IsFalse();
    }

    [Test]
    public async Task A_setting_changed_after_install_is_kept_and_named() {
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);
        var root = ReadToml();
        Table(root, "features", "network_proxy", "domains")["**.kcap.ai"] = "deny";
        Table(root, "features", "network_proxy", "domains")["github.com"] = "allow";
        File.WriteAllText(Config, TomlSerializer.Serialize(root));

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, []);

        await Assert.That(restore.Change).IsEqualTo(CodexConfigToml.Change.UpdatedWithPreservedEntries);
        await Assert.That(restore.Kept.Single()).Contains("\"**.kcap.ai\" = \"deny\"");
        await Assert.That(restore.Kept.Single()).Contains("changed after kcap set it");
        var after   = ReadToml();
        var domains = Table(after, "features", "network_proxy", "domains");
        await Assert.That((string)domains["**.kcap.ai"]).IsEqualTo("deny");
        await Assert.That((string)domains["github.com"]).IsEqualTo("allow");
        await Assert.That(after.ContainsKey("sandbox_workspace_write")).IsFalse();
    }

    [Test]
    public async Task Restore_into_the_users_own_proxy_removes_only_kcaps_hosts() {
        File.WriteAllText(Config,
            """
            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "github.com" = "allow"
            """);
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);

        CodexConfigToml.RestoreNetworkAccess(Config, []);

        var root  = ReadToml();
        var proxy = Table(root, "features", "network_proxy");
        await Assert.That((bool)proxy["enabled"]).IsTrue();
        await Assert.That(Table(proxy, "domains").Keys.ToArray()).IsEquivalentTo(["github.com"]);
        await Assert.That(root.ContainsKey("sandbox_workspace_write")).IsFalse();
    }

    /// <summary>The proxy is the only thing narrowing an open sandbox, so it stays on whenever network
    /// access does, even though kcap claimed turning it on.</summary>
    [Test]
    public async Task A_claimed_proxy_stays_enabled_while_network_access_stays_on() {
        File.WriteAllText(Config,
            """
            [sandbox_workspace_write]
            network_access = true

            [features.network_proxy]
            enabled = true
            """);
        File.WriteAllText(Ledger,
            """
            {"version":1,"entries":{},"sandbox_network":{"settings":[
              {"path":["features","network_proxy","enabled"],"set":{"type":"bool","value":true}}
            ],"created_tables":[]}}
            """);

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, []);

        await Assert.That(restore.Change).IsEqualTo(CodexConfigToml.Change.PreservedUnownedEntries);
        await Assert.That(restore.Kept.Single()).Contains("features.network_proxy.enabled = true");
        await Assert.That((bool)Table(ReadToml(), "features", "network_proxy")["enabled"]).IsTrue();
    }

    /// <summary>An install from before kcap recorded its network change has no claim: the settings stay,
    /// byte for byte, and are reported rather than guessed at.</summary>
    [Test]
    public async Task Without_a_claim_kcap_looking_settings_are_kept_and_reported() {
        const string config = """
            [sandbox_workspace_write]
            network_access = true

            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "**.kcap.ai" = "allow"
            "self.example.com" = "allow"
            """;
        File.WriteAllText(Config, config);

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, ["self.example.com"]);

        await Assert.That(restore.Change).IsEqualTo(CodexConfigToml.Change.PreservedOwnershipUnknown);
        await Assert.That(restore.Kept.Count).IsEqualTo(4);
        await Assert.That(restore.Kept[0]).Contains("sandbox_workspace_write.network_access = true");
        await Assert.That(restore.Kept.All(k => k.Contains("no record"))).IsTrue();
        await Assert.That(File.ReadAllText(Config)).IsEqualTo(config);
    }

    /// <summary>An empty ledger proves nothing about the config, so its only consequence is that the ledger
    /// itself can go.</summary>
    [Test]
    public async Task An_empty_ledger_removes_nothing_from_the_config_but_itself() {
        const string config = """
            [sandbox_workspace_write]
            network_access = true

            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "**.kcap.ai" = "allow"
            """;
        File.WriteAllText(Config, config);
        File.WriteAllText(Ledger, """{"version":1,"entries":{}}""");

        var restore = CodexConfigToml.RestoreNetworkAccess(Config, []);
        var deleted = CodexConfigToml.DeleteEmptyOwnershipLedger(Config);

        await Assert.That(restore.Change).IsEqualTo(CodexConfigToml.Change.PreservedOwnershipUnknown);
        await Assert.That(deleted).IsTrue();
        await Assert.That(File.Exists(Ledger)).IsFalse();
        await Assert.That(File.ReadAllText(Config)).IsEqualTo(config);
    }

    [Test]
    public async Task A_full_uninstall_leaves_no_ledger_behind() {
        File.WriteAllText(Config, """model = "gpt-5.5" """);
        CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);
        CodexConfigToml.RegisterKcapMcpServers(Config);

        await Assert.That(CodexConfigToml.UnregisterKcapMcpServers(Config)).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(CodexConfigToml.RestoreNetworkAccess(Config, []).Change).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(CodexConfigToml.DeleteEmptyOwnershipLedger(Config)).IsTrue();

        await Assert.That(File.Exists(Ledger)).IsFalse();
        await Assert.That(ReadToml().Keys.ToArray()).IsEquivalentTo(["model"]);
    }

    [Test]
    public async Task A_ledger_still_claiming_or_unreadable_is_not_deleted() {
        CodexConfigToml.RegisterKcapMcpServers(Config);
        await Assert.That(CodexConfigToml.DeleteEmptyOwnershipLedger(Config)).IsFalse();
        await Assert.That(File.Exists(Ledger)).IsTrue();

        File.WriteAllText(Ledger, "not json");
        await Assert.That(CodexConfigToml.DeleteEmptyOwnershipLedger(Config)).IsFalse();
        await Assert.That(File.Exists(Ledger)).IsTrue();
    }

    [Test]
    public async Task Install_refuses_to_replace_a_non_table_with_a_table() {
        const string config = """features = "mine" """;
        File.WriteAllText(Config, config);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], Config);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(Config)).IsEqualTo(config);
        await Assert.That(File.Exists(Ledger)).IsFalse();
    }

    [Test]
    public async Task Restore_with_no_codex_home_does_nothing() {
        var restore = CodexConfigToml.RestoreNetworkAccess(Tmp.GetResolvedPath("absent", "config.toml"), []);

        await Assert.That(restore).IsEqualTo(CodexNetworkRestore.Nothing);
        await Assert.That(Directory.Exists(Tmp.GetResolvedPath("absent"))).IsFalse();
    }
}
