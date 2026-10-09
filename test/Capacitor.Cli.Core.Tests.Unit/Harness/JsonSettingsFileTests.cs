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

    [Test]
    [Arguments("")]
    [Arguments("  \n\t ")]
    public async Task Edit_treats_an_empty_file_as_an_empty_object(string content) {
        var path = Tmp.CreateFile("settings.json", content);

        var result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(path))!["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_keeps_a_symlinked_settings_file_a_symlink() {
        Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI does not have");
        var target = Tmp.CreateFile("dotfiles/settings.json", """{ "theme": "dark" }""");
        var link   = Tmp.PathTo("settings.json");
        File.CreateSymbolicLink(link, target);

        var result = JsonSettingsFile.Edit(link, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        var root = JsonNode.Parse(File.ReadAllText(target))!;
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");
        await Assert.That(root["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Two_concurrent_edits_of_one_file_both_survive() {
        var path    = Tmp.CreateFile("settings.json", "{}");
        using var gate = new Barrier(2);

        SettingsEdit EditAdding(string key) => JsonSettingsFile.Edit(path, root => {
            root[key] = true;
            Thread.Sleep(300);
            return true;
        });

        var results = await Task.WhenAll(
            Task.Run(() => { gate.SignalAndWait(); return EditAdding("a"); }),
            Task.Run(() => { gate.SignalAndWait(); return EditAdding("b"); }));

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        await Assert.That(results).IsEquivalentTo([SettingsEdit.Changed, SettingsEdit.Changed]);
        await Assert.That(root.ContainsKey("a")).IsTrue();
        await Assert.That(root.ContainsKey("b")).IsTrue();
    }

    [Test]
    public async Task An_edit_that_cannot_take_the_file_lock_fails_without_writing() {
        var path = Tmp.CreateFile("settings.json", "{}");

        using (ConfigFileLock.Acquire(PhysicalPath.Of(path))) {
            var result = await Task.Run(() => JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; }, lockTimeout: TimeSpan.FromMilliseconds(200)));

            await Assert.That(result).IsEqualTo(SettingsEdit.Failed);
        }

        await Assert.That(File.ReadAllText(path)).IsEqualTo("{}");
    }

    /// <summary>Discriminates on Windows only, where a read that denies Write fails against the
    /// vendor's own open handle.</summary>
    [Test]
    public async Task Edit_reads_a_file_the_vendor_holds_open_for_writing() {
        var path = Tmp.CreateFile("settings.json", """{ "theme": "dark" }""");

        SettingsEdit result;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(path))!["theme"]!.GetValue<string>()).IsEqualTo("dark");
    }
}
