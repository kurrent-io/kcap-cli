using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginWriterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Install_enables_the_plugin_and_registers_the_marketplace() {
        var settings = Tmp.PathTo("settings.json");

        var result = ClaudePluginWriter.Install(settings, "/opt/kcap/plugin");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo("/opt/kcap/plugin");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settings)).IsTrue();
    }

    [Test]
    public async Task Install_removes_legacy_entries_and_keeps_user_settings() {
        var settings = Tmp.CreateFile("settings.json", """
            { "model": "opus", "enabledPlugins": { "kapacitor@kapacitor": true, "other@x": true },
              "extraKnownMarketplaces": { "kurrent": {} } }
            """);

        ClaudePluginWriter.Install(settings, "/p");

        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(root["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();
        await Assert.That(root["extraKnownMarketplaces"]!["kurrent"]).IsNull();
    }

    [Test]
    public async Task Install_refuses_a_malformed_settings_file() {
        var settings = Tmp.CreateFile("settings.json", "{ broken");

        await Assert.That(ClaudePluginWriter.Install(settings, "/p")).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo("{ broken");
    }

    [Test]
    public async Task Remove_drops_only_kcap_entries() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        root["enabledPlugins"]!["other@x"] = true;
        File.WriteAllText(settings, root.ToJsonString());

        var result = ClaudePluginWriter.Remove(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var after = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(after["enabledPlugins"]!["kcap@kcap"]).IsNull();
        await Assert.That(after["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(ClaudePluginInstaller.ReadMarker(settings)).IsNull();
    }

    [Test]
    public async Task Remove_on_a_missing_file_is_unchanged() {
        await Assert.That(ClaudePluginWriter.Remove(Tmp.PathTo("nope.json"))).IsEqualTo(SettingsEdit.Unchanged);
    }

    [Test]
    public async Task Install_repairs_a_non_bool_plugin_flag() {
        var settings = Tmp.CreateFile("settings.json", """{"enabledPlugins":{"kcap@kcap":"yes"}}""");

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(settings))!["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Install_repairs_a_malformed_marketplace_entry() {
        var settings = Tmp.CreateFile("settings.json", """{"extraKnownMarketplaces":{"kcap":{"source":"x"}}}""");

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var path = JsonNode.Parse(File.ReadAllText(settings))!["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>();
        await Assert.That(path).IsEqualTo("/p");
    }

    [Test]
    public async Task Repeated_install_is_unchanged_and_still_writes_the_marker() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        ClaudePluginInstaller.DeleteMarker(settings);

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settings)).IsNotNull();
    }
}
