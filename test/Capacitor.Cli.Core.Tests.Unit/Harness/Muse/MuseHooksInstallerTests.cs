using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseHooksInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Absent_file_is_created_with_schema_version_and_every_event() {
        var path = Tmp.PathTo("settings.json");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(MuseHooksParser.InstalledEvents(root)).IsEquivalentTo(MuseHooksParser.MuseHookEvents);
        await Assert.That(root["hooks"]!["UserPromptSubmit"]![0]!["matcher"]).IsNull();
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsTrue();
    }

    [Test]
    public async Task Foreign_members_and_foreign_groups_survive_install_and_remove() {
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"model":"muse-spark-1.3","hooks":{"Stop":[{"hooks":[{"type":"command","command":"say done","timeout":5}]}]}}""");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Changed);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("muse-spark-1.3");
        await Assert.That(root["hooks"]!["Stop"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(root["hooks"]!["SessionStart"]).IsNull();
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task Remove_keeps_a_user_command_that_shares_a_group_with_kcaps() {
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"hooks":{"Stop":[{"hooks":[{"type":"command","command":"kcap hook --muse --no-update-check","timeout":5},{"type":"command","command":"say done","timeout":5}]}]}}""");

        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Changed);

        var stop = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!;
        await Assert.That(stop.Count).IsEqualTo(1);
        var hooks = stop[0]!["hooks"]!.AsArray();
        await Assert.That(hooks.Count).IsEqualTo(1);
        await Assert.That(hooks[0]!["command"]!.GetValue<string>()).IsEqualTo("say done");
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task Malformed_file_is_refused_untouched() {
        var path = Tmp.CreateFile("settings.json", "{\"schema_version\":1,");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{\"schema_version\":1,");
        await Assert.That(File.Exists(Path.Combine(Tmp.Path, MuseHooksInstaller.MarkerFileName))).IsFalse();
    }

    [Test]
    public async Task A_hooks_member_that_is_not_an_object_is_left_alone() {
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"hooks":"nope"}""");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.ReadAllText(path)).Contains("\"nope\"");
    }

    [Test]
    [Arguments("""{"type":"command","command":42}""")]
    [Arguments("""{"type":"command","command":["say","done"]}""")]
    [Arguments("""{"type":"command","command":{"cmd":"say"}}""")]
    public async Task A_foreign_group_whose_command_is_not_a_string_neither_throws_nor_is_touched(string hook) {
        var foreign = $$"""{"hooks":[{{hook}}]}""";
        var path = Tmp.CreateFile("settings.json", $$$"""{"schema_version":1,"hooks":{"Stop":[{{{foreign}}}]}}""");

        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Changed);

        var stop = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!;
        await Assert.That(stop.Count).IsEqualTo(1);
        await Assert.That(JsonNode.DeepEquals(stop[0], JsonNode.Parse(foreign))).IsTrue();
    }

    [Test]
    public async Task A_flat_group_with_a_non_string_command_neither_throws_nor_is_touched() {
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"hooks":{"Stop":[{"command":7}]}}""");

        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Changed);

        var stop = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!;
        await Assert.That(stop.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_marker_records_the_installed_version_and_remove_deletes_it() {
        var path = Tmp.PathTo("settings.json");
        MuseHooksInstaller.Install(path);
        await Assert.That(MuseHooksInstaller.ReadMarker(path)).IsEqualTo(CapacitorVersion.Current());

        MuseHooksInstaller.Remove(path);
        await Assert.That(MuseHooksInstaller.ReadMarker(path)).IsNull();
    }

    [Test]
    public async Task A_null_element_in_a_hook_array_survives_install_and_remove() {
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"hooks":{"Stop":[null]}}""");

        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Changed);

        var stop = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!;
        await Assert.That(stop.Count).IsEqualTo(1);
        await Assert.That(stop[0]).IsNull();
    }

    /// <summary>Hooks on the other events would read as installed while the install reports failure.</summary>
    [Test]
    public async Task An_event_whose_value_is_not_an_array_refuses_the_whole_install() {
        const string content = """{"schema_version":1,"hooks":{"Stop":"nope"}}""";
        var path = Tmp.CreateFile("settings.json", content);

        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(content);
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsFalse();
        await Assert.That(MuseHooksInstaller.ReadMarker(path)).IsNull();
    }

    [Test]
    public async Task Remove_on_a_malformed_file_keeps_the_marker() {
        var path   = Tmp.CreateFile("settings.json", "{not json");
        var marker = Tmp.CreateFile(MuseHooksInstaller.MarkerFileName, "1.2.3");

        await Assert.That(MuseHooksInstaller.Remove(path)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    /// <summary>The marker survives a hand edit, so it cannot be what says the hooks are there.</summary>
    [Test]
    public async Task Hooks_removed_by_hand_read_as_neither_wired_nor_installed_despite_the_marker() {
        var path = Tmp.PathTo("settings.json");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        File.WriteAllText(path, """{"schema_version":1}""");

        await Assert.That(MuseHooksInstaller.ReadMarker(path)).IsNotNull();
        await Assert.That(MuseHooksInstaller.IsWired(path)).IsFalse();
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task A_partial_install_reads_as_wired_and_installed() {
        var path = Tmp.PathTo("settings.json");
        await Assert.That(MuseHooksInstaller.Install(path)).IsEqualTo(SettingsEdit.Changed);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        root["hooks"]!.AsObject().Remove("Stop");
        File.WriteAllText(path, root.ToJsonString());

        await Assert.That(MuseHooksInstaller.IsWired(path)).IsTrue();
        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsTrue();
    }

    [Test]
    public async Task The_marker_keeps_a_missing_or_unparseable_file_installed_but_not_wired() {
        var path = Tmp.PathTo("settings.json");
        Tmp.CreateFile(MuseHooksInstaller.MarkerFileName, "1.2.3");

        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(MuseHooksInstaller.IsWired(path)).IsFalse();

        Tmp.CreateFile("settings.json", "{not json");

        await Assert.That(MuseHooksInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(MuseHooksInstaller.IsWired(path)).IsFalse();
    }
}
