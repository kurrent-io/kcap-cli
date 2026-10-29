namespace Capacitor.Cli.Core.Tests.Unit;

public class GitConfigFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string? Get(string content, string section = "worktree", string name = "useRelativePaths") =>
        GitConfigFile.Get(Tmp.CreateFile("config", content), section, name);

    [Test]
    public async Task The_last_value_wins_and_names_ignore_case() {
        await Assert.That(Get("""
            [worktree]
                useRelativePaths = false
            [core]
                bare = false
            [Worktree]
                userelativepaths = true
            """)).IsEqualTo("true");
    }

    [Test]
    public async Task A_bare_name_is_true() {
        await Assert.That(Get("[worktree]\n\tuseRelativePaths\n")).IsEqualTo("true");
    }

    [Test]
    public async Task Quotes_and_trailing_comments_are_stripped() {
        await Assert.That(Get("[kcap]\n  relativeWorktrees = \"declined\" # asked once\n", "kcap", "relativeWorktrees"))
            .IsEqualTo("declined");
    }

    [Test]
    public async Task A_variable_on_the_section_line_is_read() {
        await Assert.That(Get("[worktree] useRelativePaths = yes\n")).IsEqualTo("yes");
    }

    [Test]
    public async Task A_subsection_or_another_section_does_not_match() {
        await Assert.That(Get("[worktree \"x\"]\n  useRelativePaths = true\n[core]\n  useRelativePaths = true\n")).IsNull();
    }

    [Test]
    public async Task A_missing_file_is_null() {
        await Assert.That(GitConfigFile.Get(Tmp.PathTo("absent"), "worktree", "useRelativePaths")).IsNull();
    }

    [Test]
    [Arguments("true", true)]
    [Arguments("Yes", true)]
    [Arguments("on", true)]
    [Arguments("1", true)]
    [Arguments("false", false)]
    [Arguments("0", false)]
    [Arguments("", false)]
    public async Task Booleans_follow_git(string value, bool expected) {
        await Assert.That(GitConfigFile.IsTrue(value)).IsEqualTo(expected);
    }
}
