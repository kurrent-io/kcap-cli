using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeStatusLineTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Settings => Tmp.PathTo("settings.json");

    StatusLineBackup? _backup;

    SettingsEdit Install(string id = "acct1", bool saves = true) =>
        ClaudeStatusLine.Install(Settings, id, _backup, b => { if (saves) _backup = b; return saves; }, posixShell: true);

    (SettingsEdit Edit, bool BackupMissing) Remove() => ClaudeStatusLine.Remove(Settings, _backup);

    void ForgetBackup() => _backup = null;

    JsonObject Root() => JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();

    string? Command() => Root()["statusLine"]?["command"]?.GetValue<string>();

    [Test]
    public async Task Install_then_unwire_round_trips_settings() {
        const string original = """{"model":"opus","statusLine":{"type":"command","command":"printf \"%s\\n\" mine","padding":2},"theme":"dark"}""";
        Tmp.CreateFile("settings.json", original);

        await Assert.That(Install()).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(Root()["statusLine"]!["padding"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(Remove().Edit).IsEqualTo(SettingsEdit.Changed);

        var after = Root();
        await Assert.That(JsonNode.DeepEquals(after, JsonNode.Parse(original))).IsTrue();
        await Assert.That(string.Join(",", after.Select(p => p.Key))).IsEqualTo("model,statusLine,theme");
        await Assert.That(string.Join(",", after["statusLine"]!.AsObject().Select(p => p.Key))).IsEqualTo("type,command,padding");
    }

    [Test]
    public async Task Install_with_no_original_records_none_and_unwire_removes_statusLine() {
        Tmp.CreateFile("settings.json", """{"model":"opus"}""");

        Install();

        await Assert.That(_backup).IsEqualTo(new StatusLineBackup(null));
        await Assert.That(Root()["statusLine"]!["type"]!.GetValue<string>()).IsEqualTo("command");
        await Assert.That(ClaudeStatusLineCommand.IsOurs(Command())).IsTrue();

        Remove();

        await Assert.That(Root().ContainsKey("statusLine")).IsFalse();
        await Assert.That(Root()["model"]!.GetValue<string>()).IsEqualTo("opus");
    }

    [Test]
    public async Task User_edit_then_rewire_backs_up_the_edit_and_reinstalls() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo first"}}""");
        Install();
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo second"}}""");

        await Assert.That(Install()).IsEqualTo(SettingsEdit.Changed);

        await Assert.That(_backup).IsEqualTo(new StatusLineBackup("echo second"));
        await Assert.That(Command()).IsEqualTo(ClaudeStatusLineCommand.For("acct1", "echo second", posixShell: true));
    }

    [Test]
    public async Task Rewire_with_ours_installed_changes_nothing() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Install();
        var before = File.ReadAllText(Settings);
        var saves  = 0;

        var edit = ClaudeStatusLine.Install(Settings, "acct1", _backup, _ => { saves++; return true; }, posixShell: true);

        await Assert.That(edit).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(saves).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    public async Task Ours_for_another_account_is_rewritten_from_the_backup() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Install("old");

        await Assert.That(Install("new")).IsEqualTo(SettingsEdit.Changed);

        await Assert.That(Command()).IsEqualTo(ClaudeStatusLineCommand.For("new", "echo mine", posixShell: true));
        await Assert.That(_backup).IsEqualTo(new StatusLineBackup("echo mine"));
    }

    [Test]
    public async Task User_edit_then_unwire_keeps_the_edit() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo first"}}""");
        Install();
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine now"}}""");
        var before = File.ReadAllText(Settings);

        var (edit, missing) = Remove();

        await Assert.That(edit).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(missing).IsFalse();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    public async Task Padding_edit_with_ours_installed_survives_unwire() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Install();
        var root = Root();
        root["statusLine"]!["padding"] = 4;
        Tmp.CreateFile("settings.json", root.ToJsonString());

        Remove();

        await Assert.That(Command()).IsEqualTo("echo mine");
        await Assert.That(Root()["statusLine"]!["padding"]!.GetValue<int>()).IsEqualTo(4);
    }

    [Test]
    public async Task Padding_alone_keeps_the_statusLine_object_when_there_was_no_original() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"padding":1}}""");
        Install();

        Remove();

        await Assert.That(Root()["statusLine"]!.ToJsonString()).IsEqualTo("""{"padding":1}""");
    }

    [Test]
    public async Task Our_command_is_never_adopted_as_original() {
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("someone", null, true) } };
        Tmp.CreateFile("settings.json", settings.ToJsonString());
        var saved = new List<StatusLineBackup>();

        ClaudeStatusLine.Install(Settings, "acct1", null, b => { saved.Add(b); return true; }, posixShell: true);

        await Assert.That(saved).IsEquivalentTo([new StatusLineBackup(null)]);
        await Assert.That(Command()).IsEqualTo(ClaudeStatusLineCommand.For("acct1", null, posixShell: true));
    }

    public static IEnumerable<Func<string>> EmbeddedOriginals() {
        yield return () => "echo 'single quoted' \"it's\" # a trailing comment";
        yield return () => "while read -r l; do echo \"got $l\"; done <<'EOF'\nfirst\nEOF\necho after";
    }

    [Test]
    [MethodDataSource(nameof(EmbeddedOriginals))]
    public async Task Ours_for_a_lost_account_without_a_backup_recovers_the_embedded_original(string original) {
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("lost-id", original, true), ["padding"] = 1 } };
        Tmp.CreateFile("settings.json", settings.ToJsonString());

        await Assert.That(Install("new-id")).IsEqualTo(SettingsEdit.Changed);

        await Assert.That(_backup).IsEqualTo(new StatusLineBackup(original));
        await Assert.That(Command()).IsEqualTo(ClaudeStatusLineCommand.For("new-id", original, posixShell: true));

        Remove();

        await Assert.That(Command()).IsEqualTo(original);
        await Assert.That(Root()["statusLine"]!["padding"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_save_of_the_embedded_original_writes_nothing() {
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("lost-id", "echo mine", true) } };
        Tmp.CreateFile("settings.json", settings.ToJsonString());
        var before = File.ReadAllText(Settings);

        await Assert.That(Install("new-id", saves: false)).IsEqualTo(SettingsEdit.Unchanged);

        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    public async Task Ours_for_this_account_without_a_backup_recovers_it_and_leaves_the_file() {
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("acct1", "echo mine", true) } };
        Tmp.CreateFile("settings.json", settings.ToJsonString());
        var before = File.ReadAllText(Settings);

        await Assert.That(Install()).IsEqualTo(SettingsEdit.Unchanged);

        await Assert.That(_backup).IsEqualTo(new StatusLineBackup("echo mine"));
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    public async Task The_plain_shape_without_a_backup_recovers_nothing() {
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("lost-id", "echo mine", false) } };
        Tmp.CreateFile("settings.json", settings.ToJsonString());

        Install("new-id");

        await Assert.That(_backup).IsNull();
        await Assert.That(Command()).IsEqualTo(ClaudeStatusLineCommand.For("new-id", null, posixShell: true));
    }

    [Test]
    public async Task Missing_backup_with_ours_installed_removes_command_and_reports() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine","padding":1}}""");
        Install();
        ForgetBackup();

        var (edit, missing) = Remove();

        await Assert.That(edit).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(missing).IsTrue();
        await Assert.That(Root()["statusLine"]!.ToJsonString()).IsEqualTo("""{"padding":1}""");
    }

    [Test]
    public async Task A_refused_backup_save_leaves_the_settings_alone() {
        Tmp.CreateFile("settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        var before = File.ReadAllText(Settings);

        await Assert.That(Install(saves: false)).IsEqualTo(SettingsEdit.Unchanged);

        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    [Arguments("""{"statusLine":"echo mine"}""")]
    [Arguments("""{"statusLine":["echo mine"]}""")]
    [Arguments("""{"statusLine":{"type":"command","command":42}}""")]
    [Arguments("""{"statusLine":{"type":"command","command":{"x":1}}}""")]
    public async Task Non_object_statusLine_is_refused_unchanged(string content) {
        Tmp.CreateFile("settings.json", content);
        var saved = false;

        var install = ClaudeStatusLine.Install(Settings, "acct1", null, _ => saved = true, posixShell: true);
        var remove  = ClaudeStatusLine.Remove(Settings, new StatusLineBackup("x")).Edit;

        await Assert.That(install).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(remove).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(saved).IsFalse();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(content);
    }

    [Test]
    public async Task Unparseable_settings_are_refused_unchanged() {
        Tmp.CreateFile("settings.json", "{ nope");

        await Assert.That(Install()).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(_backup).IsNull();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo("{ nope");
    }

    [Test]
    public async Task Remove_without_a_settings_file_creates_nothing() {
        var (edit, missing) = Remove();

        await Assert.That(edit).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(missing).IsFalse();
        await Assert.That(File.Exists(Settings)).IsFalse();
    }
}
