namespace Capacitor.Cli.Tests.Unit;

[ParallelLimiter<SubprocessLimit>]
public class GitHookInstallerTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Command = """'/opt/kcap'\''s/kcap' git-hook""";

    GitHookInstaller Installer => field ??= new(Home.Home, () => "/opt/kcap's/kcap");

    string? Entry(string key, string file = ".gitconfig") {
        var git = GitRepo.At(Home.Path).Try("config", "--file", Path.Combine(Home.Path, file), "--get-all", key);

        return git.ExitCode == 0 ? git.Text : null;
    }

    [Test]
    public async Task A_refresh_adds_no_entry_and_an_install_adds_one_for_every_event() {
        await Assert.That(Installer.Refresh()).IsFalse();
        await Assert.That(Entry("hook.kcap.command")).IsNull();

        await Assert.That(Installer.Install()).IsTrue();
        await Assert.That(Entry("hook.kcap.command")).IsEqualTo(Command);
        await Assert.That(Entry("hook.kcap.event")).IsEqualTo("post-commit\npost-merge");

        await Assert.That(Installer.Refresh()).IsTrue();
        await Assert.That(Entry("hook.kcap.event")).IsEqualTo("post-commit\npost-merge");
    }

    [Test]
    public async Task A_refresh_repoints_an_entry_at_another_binary() {
        Home.CreateFile(".gitconfig", "[hook \"kcap\"]\n\tcommand = '/old/kcap' git-hook\n\tevent = post-commit\n\tevent = post-rewrite\n\tenabled = false\n");

        await Assert.That(Installer.Refresh()).IsTrue();

        await Assert.That(Entry("hook.kcap.command")).IsEqualTo(Command);
        await Assert.That(Entry("hook.kcap.event")).IsEqualTo("post-commit\npost-merge");
        await Assert.That(Entry("hook.kcap.enabled")).IsEqualTo("false");
    }

    [Test]
    public async Task An_install_writes_the_xdg_config_when_only_that_exists() {
        Home.CreateFile([".config", "git", "config"], "[user]\n\tname = t\n");

        Installer.Install();

        await Assert.That(Entry("hook.kcap.command", ".config/git/config")).IsEqualTo(Command);
        await Assert.That(File.Exists(Path.Combine(Home.Path, ".gitconfig"))).IsFalse();
    }

    [Test]
    public async Task Remove_takes_the_entry_out_and_leaves_the_rest() {
        Home.CreateFile(".gitconfig", "[user]\n\tname = t\n");
        Installer.Install();

        Installer.Remove();

        await Assert.That(Entry("hook.kcap.command")).IsNull();
        await Assert.That(Entry("user.name")).IsEqualTo("t");
    }
}
