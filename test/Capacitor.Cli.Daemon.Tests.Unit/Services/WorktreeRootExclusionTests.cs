using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The exclude file belongs to the user: the worktree line is appended once, and every byte already
/// there stays as it was.</summary>
public class WorktreeRootExclusionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    [Arguments("", "/.capacitor/worktrees/")]
    [Arguments("sub/dir/", "/sub/dir/.capacitor/worktrees/")]
    [Arguments("a*b/c?[d]/e\\f/", "/a\\*b/c\\?\\[d]/e\\\\f/.capacitor/worktrees/")]
    public async Task The_pattern_is_anchored_at_the_launch_directory_with_glob_characters_escaped(string prefix, string expected) =>
        await Assert.That(WorktreeRootExclusion.Pattern(prefix)).IsEqualTo(expected);

    [Test]
    public async Task A_prefix_no_exclude_line_can_spell_has_no_pattern() =>
        await Assert.That(WorktreeRootExclusion.Pattern("a\nb/")).IsNull();

    [Test]
    public async Task A_missing_file_is_created_with_just_the_line() {
        var path = Tmp.PathTo("common", "info", "exclude");

        await Assert.That(WorktreeRootExclusion.Apply(path, "/.capacitor/worktrees/")).IsTrue();
        await Assert.That(File.ReadAllText(path)).IsEqualTo("/.capacitor/worktrees/\n");
    }

    [Test]
    [Arguments("# mine\n*.log\n", "# mine\n*.log\n/.capacitor/worktrees/\n")]
    [Arguments("# mine\n*.log", "# mine\n*.log\n/.capacitor/worktrees/\n")]
    [Arguments("# mine\r\n*.log", "# mine\r\n*.log\r\n/.capacitor/worktrees/\r\n")]
    public async Task The_line_is_appended_after_the_users_content_in_its_line_ending(string existing, string expected) {
        var path = Tmp.CreateFile("exclude", existing);

        await Assert.That(WorktreeRootExclusion.Apply(path, "/.capacitor/worktrees/")).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(path))).IsEqualTo(expected);
    }

    [Test]
    public async Task A_line_already_present_leaves_the_file_untouched() {
        var path   = Tmp.CreateFile("exclude", "*.log\r\n/.capacitor/worktrees/\r\n# trailing");
        var before = File.ReadAllBytes(path);

        await Assert.That(WorktreeRootExclusion.Apply(path, "/.capacitor/worktrees/")).IsFalse();
        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }
}
