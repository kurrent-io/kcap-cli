using Capacitor.Cli.Commands;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UninstallFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Removes_config_beside_a_nested_local_root_without_following_unrelated_links() {
        var config = Tmp.CreateDir("config");
        var local = Tmp.CreateDir("config/nested/local");
        Tmp.CreateFile("config/nested/local/install.json", "registry");
        Tmp.CreateFile("config/nested/local/lifecycle.lock");
        Tmp.CreateFile("config/nested/profile.json", "delete");
        var unrelated = Tmp.CreateFile("outside/user-file", "keep");
        Directory.CreateSymbolicLink(Path.Combine(config, "linked"), Tmp.PathTo("outside"));

        UninstallFiles.DeleteConfig(config, local);

        await Assert.That(File.ReadAllText(Path.Combine(local, "install.json"))).IsEqualTo("registry");
        await Assert.That(File.Exists(Tmp.PathTo("config/nested/profile.json"))).IsFalse();
        await Assert.That(File.ReadAllText(unrelated)).IsEqualTo("keep");
    }
}
