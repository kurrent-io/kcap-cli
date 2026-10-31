using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The shared <c>~/.agents/skills</c> tree through <c>kcap plugin</c>: removing or turning off one
/// integration keeps the skills another still uses, the last one takes them, and only skills kcap
/// installed unchanged are deleted.
/// </summary>
public class PluginCommandSharedSkillsTests {
    [TempHome] public required TempHome Home   { get; init; }
    [TempDir]  public required TempDir  BinDir { get; init; }

    PluginEnvironment? _env;

    PluginEnvironment Env {
        get {
            if (_env is not null) return _env;

            var binaries = TestBinaries.Searching(BinDir, "kcap");

            return _env = new PluginEnvironment(
                Home:              new(Home.Path),
                Profiles:          new ProfileConfig(),
                ResolvePluginPath: RepoTree.KcapDir,
                Stdout:            TextWriter.Null,
                Stderr:            TextWriter.Null
            ) {
                Harnesses            = TestHarnesses.Under(new(Home.Path), binaries),
                Binaries             = binaries,
                ResolveMcpBinaryPath = () => BinDir.PathTo("kcap"),
                FindStaleAgents      = _ => []
            };
        }
    }

    string SkillsDir => Env.Agents.UserSkillsDir;

    bool SkillsPresent => AgentsSkillsInstaller.SourceNames.All(n => AgentsSkillsInstaller.HasSkill(SkillsDir, n));

    bool NoBundledSkills => AgentsSkillsInstaller.SourceNames.All(n => !Directory.Exists(Path.Combine(SkillsDir, "kcap-" + n)));

    async Task<string> Run(params string[] args) {
        var stdout = new StringWriter();
        var exit   = await new PluginCommand(Env with { Stdout = stdout }, new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(args);

        await Assert.That(exit).IsEqualTo(0).Because($"`kcap {string.Join(' ', args)}` should succeed:\n{stdout}");

        return stdout.ToString();
    }

    static readonly string[] InstallCodex  = ["plugin", "install", "--codex", "--skip-codex-network-access"];
    static readonly string[] InstallCursor = ["plugin", "install", "--cursor"];

    [Test]
    public async Task Removing_codex_keeps_the_skills_cursor_still_uses() {
        await Run(InstallCursor);
        await Run(InstallCodex);

        var output = await Run("plugin", "remove", "--codex");

        await Assert.That(SkillsPresent).IsTrue();
        await Assert.That(output).Contains("still used by Cursor");
        await Assert.That(CursorHooksInstaller.IsInstalled(Env.Harnesses.Of<CursorHarness>().Paths.UserHooksJson)).IsTrue();
    }

    [Test]
    [Arguments("--codex", "--cursor")]
    [Arguments("--cursor", "--codex")]
    public async Task The_last_integration_removed_takes_the_skills_in_either_order(string first, string second) {
        await Run(InstallCursor);
        await Run(InstallCodex);

        await Run("plugin", "remove", first);
        await Assert.That(SkillsPresent).IsTrue().Because($"{second} still uses them");

        var output = await Run("plugin", "remove", second);

        await Assert.That(NoBundledSkills).IsTrue();
        await Assert.That(output).Contains("Agent skills removed");
        await Assert.That(AgentsSkillsInstaller.IsInstalled(SkillsDir)).IsFalse();
    }

    [Test]
    public async Task Removing_twice_changes_nothing_the_second_time() {
        await Run(InstallCursor);
        await Run("plugin", "remove", "--cursor");

        await Run("plugin", "remove", "--cursor");

        await Assert.That(NoBundledSkills).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(SkillsDir)).IsFalse();
    }

    [Test]
    public async Task Reinstalling_with_tools_off_takes_the_skills_back() {
        await Run(InstallCursor);
        await Assert.That(SkillsPresent).IsTrue();

        await Run("plugin", "install", "--cursor", "--skip-cursor-mcp", "--skip-cursor-skills");

        await Assert.That(NoBundledSkills).IsTrue();
        await Assert.That(CursorHooksInstaller.IsInstalled(Env.Harnesses.Of<CursorHarness>().Paths.UserHooksJson)).IsTrue();
    }

    /// <summary>Cursor reads the folder Codex installed into, so its own skip flag cannot hide the
    /// skills from it; the output has to say that rather than imply they are off.</summary>
    [Test]
    public async Task Tools_off_while_another_integration_uses_the_skills_says_they_stay_visible() {
        await Run(InstallCodex);

        var output = await Run("plugin", "install", "--cursor", "--skip-cursor-mcp", "--skip-cursor-skills");

        await Assert.That(SkillsPresent).IsTrue();
        await Assert.That(output).Contains("still used by Codex");
        await Assert.That(output).Contains("Cursor reads the same shared folder");
    }

    [Test]
    public async Task A_refresh_with_the_skip_flag_does_not_take_the_skills_back() {
        await Run(InstallCursor);

        await Run("plugin", "install", "--cursor", "--if-installed", "--skip-cursor-skills");

        await Assert.That(SkillsPresent).IsTrue();
    }

    [Test]
    public async Task The_last_removal_keeps_a_skill_the_user_edited_and_one_they_wrote() {
        await Run(InstallCursor);
        var edited = Path.Combine(SkillsDir, "kcap-recap", "SKILL.md");
        await File.AppendAllTextAsync(edited, "\nmy own step\n");
        var mine = Path.Combine(SkillsDir, "my-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        await File.WriteAllTextAsync(mine, "mine");

        var output = await Run("plugin", "remove", "--cursor");

        await Assert.That(await File.ReadAllTextAsync(edited)).Contains("my own step");
        await Assert.That(File.Exists(mine)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(SkillsDir, "kcap-errors"))).IsFalse();
        await Assert.That(output).Contains("Kept kcap-recap");
    }

    /// <summary>A tree from a build that recorded no consumers: Cursor is still configured, so removing
    /// Codex must not take the skills Cursor reads.</summary>
    [Test]
    public async Task An_unrecorded_tree_keeps_the_skills_for_an_integration_still_configured() {
        await Run(InstallCursor);
        await Run(InstallCodex);
        File.Delete(Path.Combine(SkillsDir, SharedSkillsConsumers.FileName));

        await Run("plugin", "remove", "--codex");

        await Assert.That(SkillsPresent).IsTrue();
        await Assert.That(SharedSkillsConsumers.ReadRecorded(SkillsDir)!).IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task Remove_skills_takes_them_from_everyone_and_names_who_used_them() {
        await Run(InstallCursor);

        var output = await Run("plugin", "remove", "--skills");

        await Assert.That(NoBundledSkills).IsTrue();
        await Assert.That(output).Contains("Cursor also used them");
    }

    [Test]
    public async Task The_kiro_skip_flag_takes_its_own_skills_back() {
        var kiroSkills = Env.Harnesses.Of<KiroHarness>().Paths.SkillsDir;
        await Run("plugin", "install", "--kiro", "--tools-only");
        await Assert.That(AgentsSkillsInstaller.IsInstalled(kiroSkills)).IsTrue();

        await Run("plugin", "install", "--kiro", "--tools-only", "--skip-kiro-skills");

        await Assert.That(AgentsSkillsInstaller.IsInstalled(kiroSkills)).IsFalse();
        await Assert.That(AgentsSkillsInstaller.HasSkill(kiroSkills, "recap")).IsFalse();
    }
}
