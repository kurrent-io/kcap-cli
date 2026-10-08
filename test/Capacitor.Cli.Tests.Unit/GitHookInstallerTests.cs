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

    /// <summary>The vendor suffixes Apple and Git for Windows append must not hide the version setup
    /// compares against the config-hook minimum.</summary>
    [Test]
    [Arguments("git version 2.50.1 (Apple Git-155)", "2.50.1")]
    [Arguments("git version 2.54.0.windows.1", "2.54.0")]
    [Arguments("git version 2.54.0", "2.54.0")]
    [Arguments("git version 3.0", "3.0.0")]
    public async Task ParseGitVersion_reads_vendor_builds(string output, string expected) {
        await Assert.That(GitHookInstaller.ParseGitVersion(output)).IsEqualTo(Version.Parse(expected));
    }

    [Test]
    [Arguments("")]
    [Arguments("git: command not found")]
    [Arguments("git version unknown")]
    public async Task ParseGitVersion_returns_null_for_output_it_cannot_read(string output) {
        await Assert.That(GitHookInstaller.ParseGitVersion(output)).IsNull();
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
