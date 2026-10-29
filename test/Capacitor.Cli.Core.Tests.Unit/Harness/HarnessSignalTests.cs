using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// What each vendor answers about a machine, over a layout the test hands it. Every harness here is
/// built with <c>Over</c>, so no override variable is read: an omitted override is provably unset
/// rather than a fall-through to the process environment, and these tests carry no exclusion.
/// </summary>
public class HarnessSignalTests {
    [TempHome] public required TempHome Home { get; init; }

    static UserHome Nowhere => new("/nonexistent-home");

    static DetectedAgent Detect(IHarness harness, BinaryProbe binaries) =>
        HarnessRegistry.Over(binaries, harness).Detect(harness.Id);

    // ── markers ──

    /// A desktop-app install runs Claude Code off PATH, so the files Claude Code writes are enough.
    [Test]
    [Arguments("history.jsonl", false)]
    [Arguments("stats-cache.json", false)]
    [Arguments("file-history", true)]
    [Arguments("shell-snapshots", true)]
    [Arguments("session-env", true)]
    [Arguments("todos", true)]
    [Arguments("statsig", true)]
    public async Task Claude_is_detected_by_a_file_it_writes(string entry, bool directory) {
        var claude = ClaudeHarness.Over(new ClaudePaths(Home, null));

        if (directory) Home.CreateDir(".claude", entry);
        else Home.CreateFile([".claude", entry], "{}");

        await Assert.That(claude.Signals.HasUserData).IsTrue();
    }

    /// Everything here is written by kcap — the plugin install, the skills sync, the daemon's
    /// launcher — so none of it may make Claude detect itself after it is uninstalled.
    [Test]
    public async Task Claude_ignores_what_kcap_writes_into_its_home() {
        var claude = ClaudeHarness.Over(new ClaudePaths(Home, null));

        Home.CreateDir(".claude", "skills", "kcap");
        Home.CreateDir(".claude", "projects", "-repo");
        Home.CreateFile([".claude", "settings.json"], "{}");
        Home.CreateFile(".claude.json", "{}");

        await Assert.That(claude.Signals.HasUserData).IsFalse();
    }

    [Test]
    [Arguments("sessions", true)]
    [Arguments("archived_sessions", true)]
    [Arguments("auth.json", false)]
    [Arguments("models_cache.json", false)]
    [Arguments("history.jsonl", false)]
    [Arguments("installation_id", false)]
    [Arguments("version.json", false)]
    [Arguments(".codex-global-state.json", false)]
    public async Task Codex_is_detected_by_a_file_it_writes(string entry, bool directory) {
        var codex = CodexHarness.Over(new CodexPaths(Home, null));

        if (directory) Home.CreateDir(".codex", entry);
        else Home.CreateFile([".codex", entry], "{}");

        await Assert.That(codex.Signals.HasUserData).IsTrue();
    }

    /// kcap's MCP setup, hooks install and skills sync write these.
    [Test]
    public async Task Codex_ignores_what_kcap_writes_into_its_home() {
        var codex = CodexHarness.Over(new CodexPaths(Home, null));

        Home.CreateDir(".codex", "skills", "kcap");
        Home.CreateFile([".codex", "config.toml"], "");
        Home.CreateFile([".codex", "hooks.json"], "{}");
        Home.CreateFile([".codex", ".kcap-hooks-version"], "1.0.0");

        await Assert.That(codex.Signals.HasUserData).IsFalse();
    }

    /// A bare <c>~/.gemini</c> is created by things other than a Gemini run; a settings file is not.
    [Test]
    public async Task Gemini_needs_a_marker_inside_dot_gemini_not_the_directory() {
        var gemini = GeminiHarness.Over(new GeminiPaths(Home, null));

        Home.CreateDir(".gemini");
        await Assert.That(gemini.Signals.HasUserData).IsFalse();

        Home.CreateFile([".gemini", "settings.json"], "{}");
        await Assert.That(gemini.Signals.HasUserData).IsTrue();
    }

    [Test]
    public async Task Kiro_reads_the_root_it_was_given_and_nothing_else() {
        using var tmp = new TempDir();

        await Assert.That(KiroHarness.Over(new KiroPaths(Nowhere, null)).Signals.HasUserData).IsFalse();
        await Assert.That(KiroHarness.Over(new KiroPaths(Nowhere, tmp.Path)).Signals.HasUserData).IsTrue();
    }

    [Test]
    public async Task Pi_reads_the_agent_dir_it_was_given_and_nothing_else() {
        using var tmp = new TempDir();

        await Assert.That(PiHarness.Over(new PiPaths(Nowhere, null)).Signals.HasUserData).IsFalse();
        await Assert.That(PiHarness.Over(new PiPaths(Nowhere, tmp.Path)).Signals.HasUserData).IsTrue();
    }

    [Test]
    public async Task Copilot_reads_the_home_it_was_given_and_nothing_else() {
        using var tmp = new TempDir();

        await Assert.That(CopilotHarness.Over(new CopilotPaths(Nowhere, null)).Signals.HasUserData).IsFalse();
        await Assert.That(CopilotHarness.Over(new CopilotPaths(Nowhere, tmp.Path)).Signals.HasUserData).IsTrue();
    }

    [Test]
    public async Task OpenCode_reads_the_config_dir_it_was_given() {
        using var tmp = new TempDir();
        var       paths = new OpenCodePaths(Nowhere, tmp.Path, null, null);

        await Assert.That(OpenCodeHarness.Over(paths).Signals.HasUserData).IsTrue();
    }

    /// Cursor's second signal is this host's Electron user dir, wherever that host keeps it. Windows
    /// keeps it under the real Roaming AppData, which no temp home can stand in for.
    [Test]
    public async Task Cursor_detects_the_electron_user_dir_this_host_keeps() {
        Skip.When(OperatingSystem.IsWindows(), "the Windows Electron dir lies outside any temp home");

        var cursor = CursorHarness.Over(new CursorPaths(Home));

        Directory.CreateDirectory(cursor.Paths.UserDir);

        await Assert.That(cursor.Signals.HasUserData).IsTrue();
    }

    // ── desktop apps ──

    static IHarness[] Apps() => [
        ClaudeHarness.Over(new ClaudePaths(Nowhere, null)),
        CodexHarness.Over(new CodexPaths(Nowhere, null)),
        CursorHarness.Over(new CursorPaths(Nowhere)),
        KiroHarness.Over(new KiroPaths(Nowhere, null)),
        AntigravityHarness.Over(GeminiHarness.Over(new GeminiPaths(Nowhere, null))),
        OpenCodeHarness.Over(new OpenCodePaths(Nowhere, null, null, null)),
        MuseHarness.Over(new MusePaths(Nowhere, null, null)),
    ];

    /// The CEO's machine: only the desktop apps, their CLIs bundled inside them and none on PATH.
    [Test]
    [Arguments(HarnessId.Claude, "Claude.app")]
    [Arguments(HarnessId.Claude, "Claude/claude-code")]
    [Arguments(HarnessId.Claude, "AnthropicClaude")]
    [Arguments(HarnessId.Codex, "Codex.app")]
    [Arguments(HarnessId.Codex, "ChatGPT.app/Contents/Resources/codex-cli")]
    [Arguments(HarnessId.Codex, "Codex")]
    [Arguments(HarnessId.Codex, "com.openai.codex")]
    [Arguments(HarnessId.Cursor, "Cursor.app")]
    [Arguments(HarnessId.Kiro, "Kiro.app")]
    [Arguments(HarnessId.Kiro, "Kiro CLI.app")]
    [Arguments(HarnessId.Kiro, "kiro-cli")]
    [Arguments(HarnessId.Antigravity, "Antigravity.app")]
    [Arguments(HarnessId.OpenCode, "OpenCode.app")]
    [Arguments(HarnessId.Muse, "Muse.app/Contents/Resources/hatch")]
    public async Task A_desktop_app_alone_detects_its_vendor(HarnessId id, string entry) {
        using var root = new TempDir();
        root.CreateDir(entry.Split('/'));

        var detected = HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(root.Path), Apps()).Detect(id);

        await Assert.That(detected.DesktopAppFound).IsTrue();
        await Assert.That(detected.BinaryFound).IsFalse();
        await Assert.That(detected.Detected).IsTrue();
    }

    /// One root per desktop app shared by every vendor, so an app answers for its own vendor only.
    [Test]
    public async Task A_desktop_app_detects_no_other_vendor() {
        using var root = new TempDir();
        root.CreateDir("Claude.app");

        var registry = HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(root.Path), Apps());

        await Assert.That(registry.Where(h => registry.Detect(h.Id).DesktopAppFound).Select(h => h.Id))
            .IsEquivalentTo([HarnessId.Claude]);
    }

    /// "Muse" alone names unrelated apps, so only the bundle carrying Muse Code's agent counts.
    [Test]
    public async Task A_bare_Muse_app_is_not_Muse_Code() {
        using var root = new TempDir();
        root.CreateDir("Muse.app");
        root.CreateDir("Muse");

        var detected = HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(root.Path), Apps()).Detect(HarnessId.Muse);

        await Assert.That(detected.DesktopAppFound).IsFalse();
    }

    /// A registry built over a test's layout looks in no real application folder.
    [Test]
    public async Task Over_without_a_probe_finds_no_desktop_app() {
        var detected = HarnessRegistry.Over(TestBinaries.None, Apps()).Detect(HarnessId.Claude);

        await Assert.That(detected.DesktopAppFound).IsFalse();
    }

    // ── binaries ──

    /// Cursor ships as an editor: no name is probed, so an executable called <c>cursor</c> on the
    /// search path must not detect it.
    [Test]
    public async Task Cursor_probes_no_binary_at_all() {
        using var bin = new TempDir();

        var cursor   = CursorHarness.Over(new CursorPaths(Nowhere));
        var detected = Detect(cursor, TestBinaries.Searching(bin, "cursor"));

        await Assert.That(cursor.Signals.LaunchSignal).IsNull();
        await Assert.That(detected.BinaryFound).IsFalse();
    }

    /// Antigravity's CLI is <c>agy</c> and its IDE is <c>antigravity</c>; either alone installs the
    /// vendor, so both names are probed.
    [Test]
    [Arguments("agy")]
    [Arguments("antigravity")]
    public async Task Antigravity_is_detected_by_either_of_its_names(string staged) {
        using var bin = new TempDir();

        var antigravity = AntigravityHarness.Over(GeminiHarness.Over(new GeminiPaths(Nowhere, null)));

        await Assert.That(Detect(antigravity, TestBinaries.Searching(bin, staged)).BinaryFound).IsTrue();
    }

    /// Kiro's agent CLI is <c>kiro-cli</c> and its IDE is <c>kiro</c>; a machine can carry either.
    [Test]
    [Arguments("kiro-cli")]
    [Arguments("kiro")]
    public async Task Kiro_is_detected_by_either_of_its_names(string staged) {
        using var bin = new TempDir();

        var kiro = KiroHarness.Over(new KiroPaths(Nowhere, null));

        await Assert.That(Detect(kiro, TestBinaries.Searching(bin, staged)).BinaryFound).IsTrue();
    }

    [Test]
    public async Task Unreadable_search_path_entries_do_not_throw() {
        var claude = ClaudeHarness.Over(new ClaudePaths(Nowhere, null));

        await Assert.That(Detect(claude, BinaryProbe.Searching("/nonexistent-a:/nonexistent-b")).Detected).IsFalse();
    }

    // ── wiring ──

    /// Cursor's hooks installer treats the version marker beside hooks.json as installed.
    [Test]
    public async Task Cursor_is_wired_once_its_hooks_marker_is_there() {
        var cursor = CursorHarness.Over(new CursorPaths(Home));

        Home.CreateDir(".cursor");
        await Assert.That(cursor.Signals.IsWired).IsFalse();

        Home.CreateFile([".cursor", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(cursor.Signals.IsWired).IsTrue();
    }

    /// Claude's wired-check is the enabled-plugin flag, so a plugin present but switched off reads
    /// as unwired.
    [Test]
    public async Task Claude_is_wired_only_while_its_plugin_is_enabled() {
        var claude = ClaudeHarness.Over(new ClaudePaths(Home, null));

        await Assert.That(claude.Signals.IsWired).IsFalse();

        Home.CreateDir(".claude");
        Home.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":false}}""");
        await Assert.That(claude.Signals.IsWired).IsFalse();

        Home.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":true}}""");
        await Assert.That(claude.Signals.IsWired).IsTrue();
    }

    /// Detection and the wiring probe come off one instance, so a Kiro root taken from an override
    /// is what both read — never an ambient KIRO_HOME. The home points elsewhere and holds no
    /// marker, so a true answer proves the injected root drove the probe.
    [Test]
    public async Task Kiro_wiring_reads_the_root_it_was_given() {
        using var tmp  = new TempDir();
        var       kiro = KiroHarness.Over(new KiroPaths(Nowhere, tmp.CreateDir("kh").Path));

        tmp.CreateDir(["kh", "agents"]);
        await Assert.That(kiro.Signals.IsWired).IsFalse();

        tmp.CreateFile(["kh", "agents", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(kiro.Signals.IsWired).IsTrue();
    }

}
