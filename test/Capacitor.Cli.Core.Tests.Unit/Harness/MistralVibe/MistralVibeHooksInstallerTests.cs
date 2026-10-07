using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Toml;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.MistralVibe;

public class MistralVibeHooksInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static int KcapCount(string path) =>
        MistralVibeHooksParser.HooksArray(TomlConfigFile.Read(path)!)
            .Count(MistralVibeHooksParser.EntryReferencesCapacitorVibeHook);

    [Test]
    public async Task Install_adds_one_entry_per_type_and_is_idempotent() {
        var path = Tmp.PathTo("hooks.toml");

        await Assert.That(MistralVibeHooksInstaller.Install(path)).IsEqualTo(TomlConfigFile.Outcome.Updated);
        await Assert.That(MistralVibeHooksInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(KcapCount(path)).IsEqualTo(3);
        await Assert.That(MistralVibeHooksParser.HasCapacitorHooksFor(
            TomlConfigFile.Read(path)!, MistralVibeHooksParser.VibeHookTypes)).IsTrue();

        // A second install sees exactly our set and writes nothing.
        await Assert.That(MistralVibeHooksInstaller.Install(path)).IsEqualTo(TomlConfigFile.Outcome.Unchanged);
    }

    [Test]
    public async Task Install_preserves_user_hooks_and_remove_strips_only_ours() {
        var path = Tmp.CreateFile("hooks.toml",
            "[[hooks]]\nname = \"mine\"\ntype = \"pre_tool\"\ncommand = \"echo hi\"\n");

        MistralVibeHooksInstaller.Install(path);
        var arr = MistralVibeHooksParser.HooksArray(TomlConfigFile.Read(path)!);
        await Assert.That(arr.Count).IsEqualTo(4);                       // 1 user + 3 kcap
        await Assert.That(arr.Any(UserHook)).IsTrue();

        await Assert.That(MistralVibeHooksInstaller.Remove(path)).IsEqualTo(TomlConfigFile.Outcome.Updated);
        var after = MistralVibeHooksParser.HooksArray(TomlConfigFile.Read(path)!);
        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(after.Any(UserHook)).IsTrue();                 // the user's hook survives
    }

    [Test]
    public async Task Remove_on_a_file_without_our_hooks_is_a_no_op() {
        var path = Tmp.CreateFile("hooks.toml",
            "[[hooks]]\nname = \"mine\"\ntype = \"pre_tool\"\ncommand = \"echo hi\"\n");

        await Assert.That(MistralVibeHooksInstaller.Remove(path)).IsEqualTo(TomlConfigFile.Outcome.Unchanged);
    }

    static bool UserHook(TomlTable entry) =>
        entry.TryGetValue("command", out var c) && c is "echo hi";
}
