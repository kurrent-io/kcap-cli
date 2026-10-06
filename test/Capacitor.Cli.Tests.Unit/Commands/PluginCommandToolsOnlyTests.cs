using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// `plugin install --tools-only` writes a vendor's MCP registration, skills and steering, never the
/// hooks, extension or agent that record sessions. kcap is off PATH unless a test says otherwise, so a
/// pass also proves the tools need no PATH check.
/// </summary>
public class PluginCommandToolsOnlyTests {
    [TempHome] public required TempHome Home   { get; init; }
    [TempDir]  public required TempDir  BinDir { get; init; }

    const string TestBinaryPath = "/opt/kcap-test/bin/kcap";

    PluginEnvironment Env(bool kcapOnPath = false, StringWriter? stdout = null, StringWriter? stderr = null) {
        var binaries = kcapOnPath ? TestBinaries.Searching(BinDir, "kcap") : TestBinaries.None;

        return new PluginEnvironment(
            Home:              new(Home.Path),
            Profiles:          new ProfileConfig(),
            ResolvePluginPath: RepoTree.KcapDir,
            Stdout:            stdout ?? TextWriter.Null,
            Stderr:            stderr ?? TextWriter.Null
        ) {
            Harnesses            = TestHarnesses.Under(new(Home.Path), binaries),
            Binaries             = binaries,
            ResolveMcpBinaryPath = () => TestBinaryPath
        };
    }

    static Task<int> Run(PluginEnvironment env, params string[] args) =>
        new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(args);

    string GitConfig => Path.Combine(Home.Path, ".gitconfig");

    [Test]
    public async Task Cursor_tools_only_writes_mcp_and_skills_but_no_hooks() {
        var env    = Env();
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--cursor", "--tools-only")).IsEqualTo(0);

        await Assert.That(HarnessMcpProjections.Cursor.OwnsAnything(cursor.UserMcpJson, env.Home)).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(env.Agents.UserSkillsDir)).IsTrue();
        await Assert.That(File.Exists(cursor.UserHooksJson)).IsFalse();
        await Assert.That(CursorHooksInstaller.IsInstalled(cursor.UserHooksJson)).IsFalse();
        await Assert.That(File.Exists(GitConfig)).IsFalse();
    }

    [Test]
    public async Task Cursor_tools_only_honours_the_skip_flags() {
        var env    = Env();
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--cursor", "--tools-only", "--skip-cursor-mcp")).IsEqualTo(0);

        await Assert.That(File.Exists(cursor.UserMcpJson)).IsFalse();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(env.Agents.UserSkillsDir)).IsTrue();
    }

    [Test]
    public async Task Tools_only_install_discloses_that_capture_is_unchanged() {
        var stdout = new StringWriter();

        await Assert.That(await Run(Env(stdout: stdout), "plugin", "install", "--cursor", "--tools-only")).IsEqualTo(0);

        await Assert.That(stdout.ToString()).Contains("Cursor capture unchanged (--tools-only)");
        await Assert.That(stdout.ToString()).DoesNotContain("hooks installed");
    }

    /// <summary>The npm upgrade runs the plain refresh; a tools-only install must stay tools-only.</summary>
    [Test]
    public async Task Plain_refresh_of_a_tools_only_cursor_install_heals_mcp_without_adding_hooks() {
        var env    = Env();
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;
        await Run(env, "plugin", "install", "--cursor", "--tools-only");
        File.Delete(cursor.UserMcpJson);
        var partial = KcapMcpServers.All.Where(s => s.Name == "kcap-review").ToList();
        JsonMcpConfigWriter.Register(cursor.UserMcpJson, partial, McpConfigShape.Standard, cwd: null, new McpMarker("cursor", env.Home));

        await Assert.That(await Run(env, "plugin", "install", "--cursor", "--if-installed")).IsEqualTo(0);

        await Assert.That(await File.ReadAllTextAsync(cursor.UserMcpJson)).Contains("kcap-sessions");
        await Assert.That(File.Exists(cursor.UserHooksJson)).IsFalse();
    }

    [Test]
    public async Task Tools_only_refresh_of_a_full_install_leaves_the_hooks_alone() {
        var env    = Env();
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;
        Home.CreateFile([".cursor", "hooks.json"], """{"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}""");
        var gitBefore = "[hook \"kcap\"]\n\tcommand = '/old/kcap' git-hook\n\tevent = post-commit\n";
        Home.CreateFile([".gitconfig"], gitBefore);
        var before = await File.ReadAllTextAsync(cursor.UserHooksJson);

        await Assert.That(await Run(env, "plugin", "install", "--cursor", "--tools-only", "--if-installed")).IsEqualTo(0);

        await Assert.That(await File.ReadAllTextAsync(cursor.UserHooksJson)).IsEqualTo(before);
        await Assert.That(await File.ReadAllTextAsync(GitConfig)).IsEqualTo(gitBefore);
        await Assert.That(HarnessMcpProjections.Cursor.OwnsAnything(cursor.UserMcpJson, env.Home)).IsTrue();
    }

    [Test]
    public async Task Refresh_with_nothing_installed_stays_a_no_op() {
        var env    = Env();
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--cursor", "--tools-only", "--if-installed")).IsEqualTo(0);

        await Assert.That(File.Exists(cursor.UserMcpJson)).IsFalse();
    }

    [Test]
    public async Task Pi_tools_only_writes_the_mcp_bridge_and_steering_but_not_the_ingest_extension() {
        var env = Env(kcapOnPath: true);
        var pi  = env.Harnesses.Of<PiHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--pi", "--tools-only")).IsEqualTo(0);

        await Assert.That(File.Exists(pi.KcapMcpExtension)).IsTrue();
        await Assert.That(AgentInstructionsWriter.IsInstalled(pi.AgentsMd)).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(env.Agents.UserSkillsDir)).IsTrue();
        await Assert.That(File.Exists(pi.KcapExtension)).IsFalse();
        await Assert.That(PiExtensionInstaller.IsInstalled(pi.KcapExtension)).IsFalse();
    }

    /// <summary>The bridge runs the bare `kcap mcp`, so it still needs PATH; the steering does not.</summary>
    [Test]
    public async Task Pi_tools_only_needs_kcap_on_path_only_for_the_bridge() {
        var env = Env();
        var pi  = env.Harnesses.Of<PiHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--pi", "--tools-only")).IsEqualTo(1);
        await Assert.That(File.Exists(pi.KcapMcpExtension)).IsFalse();

        await Assert.That(await Run(env, "plugin", "install", "--pi", "--tools-only", "--skip-pi-mcp")).IsEqualTo(0);
        await Assert.That(AgentInstructionsWriter.IsInstalled(pi.AgentsMd)).IsTrue();
        await Assert.That(File.Exists(pi.KcapExtension)).IsFalse();
    }

    [Test]
    public async Task Plain_refresh_of_a_tools_only_pi_install_does_not_add_the_ingest_extension() {
        var env = Env(kcapOnPath: true);
        var pi  = env.Harnesses.Of<PiHarness>().Paths;
        await Run(env, "plugin", "install", "--pi", "--tools-only");
        File.Delete(pi.KcapMcpExtension);

        await Assert.That(await Run(env, "plugin", "install", "--pi", "--if-installed")).IsEqualTo(0);

        await Assert.That(File.Exists(pi.KcapMcpExtension)).IsTrue();
        await Assert.That(File.Exists(pi.KcapExtension)).IsFalse();
    }

    [Test]
    public async Task Kiro_tools_only_writes_mcp_and_skills_without_cloning_or_flipping_the_default_agent() {
        var env  = Env();
        var kiro = env.Harnesses.Of<KiroHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--kiro", "--tools-only")).IsEqualTo(0);

        await Assert.That(HarnessMcpProjections.Kiro.OwnsAnything(kiro.SettingsMcpJson, env.Home)).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(kiro.SkillsDir)).IsTrue();
        await Assert.That(File.Exists(kiro.KcapAgentJson)).IsFalse();
        await Assert.That(File.Exists(kiro.SettingsFile)).IsFalse();
    }

    [Test]
    public async Task Kiro_tools_only_gives_crew_the_skills_but_not_the_hook() {
        if (OperatingSystem.IsWindows()) return;

        var env  = Env(kcapOnPath: true);
        var crew = env.Harnesses.Of<KiroHarness>().Crew;
        Home.CreateDir(".kiro", "crew");

        await Assert.That(await Run(env, "plugin", "install", "--kiro", "--tools-only")).IsEqualTo(0);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(crew.SkillsDir)).IsTrue();
        await Assert.That(File.Exists(crew.SpawnHookScript)).IsFalse();
    }

    /// <summary>~/.kiro/skills alone is an opt-in a refresh keeps current, still without the agent.</summary>
    [Test]
    public async Task Plain_refresh_of_a_skills_only_kiro_install_heals_mcp_without_cloning() {
        var env  = Env();
        var kiro = env.Harnesses.Of<KiroHarness>().Paths;
        await Run(env, "plugin", "install", "--kiro", "--tools-only", "--skip-kiro-mcp");

        await Assert.That(await Run(env, "plugin", "install", "--kiro", "--if-installed")).IsEqualTo(0);

        await Assert.That(HarnessMcpProjections.Kiro.OwnsAnything(kiro.SettingsMcpJson, env.Home)).IsTrue();
        await Assert.That(File.Exists(kiro.KcapAgentJson)).IsFalse();
    }

    [Test]
    public async Task Claude_refuses_tools_only() {
        var stderr = new StringWriter();
        var env    = Env(stderr: stderr);

        await Assert.That(await Run(env, "plugin", "install", "--tools-only")).IsEqualTo(1);

        await Assert.That(stderr.ToString()).Contains("--tools-only is not supported for Claude Code");
        await Assert.That(File.Exists(env.Harnesses.Of<ClaudeHarness>().Paths.UserSettings)).IsFalse();
        await Assert.That(File.Exists(GitConfig)).IsFalse();
    }

    [Test]
    public async Task Codex_refuses_tools_only() {
        var stderr = new StringWriter();
        var env    = Env(stderr: stderr);
        var codex  = env.Harnesses.Of<CodexHarness>().Paths;

        await Assert.That(await Run(env, "plugin", "install", "--codex", "--tools-only")).IsEqualTo(1);

        await Assert.That(stderr.ToString()).Contains("--tools-only is not supported for Codex");
        await Assert.That(File.Exists(codex.UserHooksJson)).IsFalse();
        await Assert.That(File.Exists(codex.ConfigToml)).IsFalse();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(env.Agents.UserSkillsDir)).IsFalse();
    }
}
