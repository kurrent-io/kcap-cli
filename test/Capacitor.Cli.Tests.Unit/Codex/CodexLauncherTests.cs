using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Codex;

[NotInParallel("HomeEnvVarMutation")]
public class CodexLauncherTests {
    static CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex" }, NullLogger<CodexLauncher>.Instance);

    static LauncherContext NewCtx(
        string? prompt = null,
        string  model  = "gpt-5.3-codex",
        string? effort = null
    ) => new(
        AgentId: "a-1",
        SourceRepoPath: "/tmp/repo",
        Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
        Prompt: prompt,
        Model: model,
        Effort: effort,
        Tools: null,
        IsReview: false,
        Review: null,
        ReviewLaunch: null
    );

    [Test]
    public async Task BuildArgs_includes_workspace_write_sandbox_and_on_request_approval() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--sandbox");
        await Assert.That(args).Contains("workspace-write");
        await Assert.That(args).Contains("--ask-for-approval");
        await Assert.That(args).Contains("on-request");
    }

    [Test]
    public async Task BuildArgs_maps_effort_max_to_xhigh() {
        var args = NewLauncher().BuildArgs(NewCtx(effort: "max")).Args;
        var joined = string.Join(' ', args);
        await Assert.That(joined).Contains("model_reasoning_effort=\"xhigh\"");
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task BuildArgs_passes_effort_through_unchanged(string effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).Contains($"model_reasoning_effort=\"{effort}\"");
    }

    [Test]
    [Arguments(null)]
    [Arguments("auto")]
    public async Task BuildArgs_omits_effort_when_null_or_auto(string? effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).DoesNotContain("model_reasoning_effort");
    }

    [Test]
    public async Task BuildArgs_appends_prompt_after_double_dash_when_present() {
        var args = NewLauncher().BuildArgs(NewCtx(prompt: "do a thing")).Args;
        var dashIdx = Array.IndexOf(args, "--");
        await Assert.That(dashIdx).IsGreaterThan(-1);
        await Assert.That(args[dashIdx + 1]).IsEqualTo("do a thing");
    }

    [Test]
    public async Task BuildArgs_emits_no_alt_screen_flag() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--no-alt-screen");
    }

    [Test]
    public async Task BuildArgs_includes_cd_with_worktree_path() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        var cdIdx = Array.IndexOf(args, "--cd");
        await Assert.That(cdIdx).IsGreaterThan(-1);
        await Assert.That(args[cdIdx + 1]).IsEqualTo("/tmp/wt");
    }

    [Test]
    public async Task BuildArgs_includes_model_when_set() {
        var args = NewLauncher().BuildArgs(NewCtx(model: "gpt-5.4")).Args;
        var mIdx = Array.IndexOf(args, "-m");
        await Assert.That(mIdx).IsGreaterThan(-1);
        await Assert.That(args[mIdx + 1]).IsEqualTo("gpt-5.4");
    }

    [Test]
    public async Task Prepare_overlays_codex_settings_dir_from_source_repo() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            var srcCodex = Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex")).FullName;
            File.WriteAllText(Path.Combine(srcCodex, "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            await Assert.That(File.Exists(Path.Combine(worktree, ".codex", "hooks.json"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_throws_when_no_hooks_json_anywhere() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            var ex = await Assert.ThrowsAsync<CodexHooksNotInstalledException>(async () => {
                NewLauncher().Prepare(ctx);
                await Task.CompletedTask;
            });
            await Assert.That(ex!.Message).Contains("kcap plugin install --codex");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_succeeds_when_user_scope_hooks_json_has_all_three_critical_events() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            await Assert.That(File.Exists(Path.Combine(home, ".codex", "config.toml"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_succeeds_when_project_scope_hooks_json_present_after_overlay() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex"));
            File.WriteAllText(Path.Combine(sourceRepo, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);
            await Assert.That(File.Exists(Path.Combine(home, ".codex", "config.toml"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_invokes_codex_config_writer_with_worktree_path() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            var configToml = File.ReadAllText(Path.Combine(home, ".codex", "config.toml"));
            await Assert.That(configToml).Contains($"\"{worktree}\"");
            await Assert.That(configToml).Contains("trust_level = \"trusted\"");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    static LauncherContext NewCtxWith(string source, string worktree) => new(
        AgentId: "a-1",
        SourceRepoPath: source,
        Worktree: new WorktreeInfo(Path: worktree, Branch: "br", SourceRepo: source),
        Prompt: null,
        Model: "gpt-5.3-codex",
        Effort: null,
        Tools: null,
        IsReview: false,
        Review: null,
        ReviewLaunch: null
    );
}
