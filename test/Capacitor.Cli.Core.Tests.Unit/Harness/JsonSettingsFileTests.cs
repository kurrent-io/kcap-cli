using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

public class JsonSettingsFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Edit_creates_a_missing_file_and_its_directory() {
        var path = Tmp.PathTo("sub", "settings.json");

        var result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(path))!["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_preserves_unrelated_keys() {
        var path = Tmp.CreateFile("settings.json", """{ "theme": "dark", "a": 0 }""");

        JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");
        await Assert.That(root["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_leaves_a_malformed_file_untouched() {
        var path = Tmp.CreateFile("settings.json", "{ not json");

        var result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task Edit_treats_a_non_object_root_as_malformed() {
        var path = Tmp.CreateFile("settings.json", "[1,2]");

        await Assert.That(JsonSettingsFile.Edit(path, _ => true)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("[1,2]");
    }

    [Test]
    public async Task Edit_does_not_write_when_nothing_changed() {
        var path = Tmp.CreateFile("settings.json", "{ }");
        var before = File.GetLastWriteTimeUtc(path);

        var result = JsonSettingsFile.Edit(path, _ => false);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
    }

    [Test]
    public async Task Edit_without_create_skips_a_missing_file() {
        var path = Tmp.PathTo("absent.json");

        var result = JsonSettingsFile.Edit(path, _ => true, createIfMissing: false);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.Exists(path)).IsFalse();
    }
}
