# AI-628 — Agent-neutral `kapacitor setup` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `kapacitor setup` detect Claude Code and Codex CLI on `PATH`, ask one yes/no per detected agent, and install hooks user-wide for each — removing the current Claude-only step 4 and the separate `kapacitor plugin install --codex` ritual.

**Architecture:** Add a pure `AgentDetector` (PATH probe, no subprocess) and a fully-injectable `CodingAgentsStep` static helper (paths + installer delegates injected from the caller). Replace `SetupCommand`'s Claude-only step 4 with a call to `CodingAgentsStep.RunAsync` that drives prompts and side-effects through those injection seams. Keep `kapacitor plugin install [--codex] [--project]` unchanged as the path for project-scope and post-setup re-installs.

**Tech Stack:** C# 12 / .NET 10 / NativeAOT, Spectre.Console for prompts, TUnit 1.18 for tests.

**Reference spec:** `docs/superpowers/specs/2026-05-15-ai-628-agent-neutral-setup-design.md`

---

## File Map

**Source — new:**
- `src/kapacitor/Commands/AgentDetector.cs` — `PATH` probe with `PATHEXT` (Windows) / Unix executable-bit (Linux/macOS).
- `src/kapacitor/Commands/CodingAgentsStep.cs` — pure logic for the wizard's agent step; takes injected `Paths`, `Installers`, prompt, and writeLine.

**Source — modified:**
- `src/kapacitor/Commands/SetupCommand.cs` — replace inline step 4 with `CodingAgentsStep.RunAsync`. Parse new flags. Map `--plugin-scope` legacy values.
- `src/kapacitor/Commands/PluginCommand.cs` — `InstallCodex` prints the `/hooks` trust hint after a fresh hooks install.
- `src/Kapacitor.Core/Resources/help-setup.txt` — document new flags, mark `--plugin-scope` as legacy.
- `README.md` — rewrite step 4 description, drop "Also using Codex CLI?" sub-section, add CI-flags note, fold Codex `/hooks` trust hint into the quick-start.

**Tests — new:**
- `test/kapacitor.Tests.Unit/AgentDetectorTests.cs` — pure overload tests + one Unix-only real-filesystem test.
- `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs` — drives every branch via injected fakes.

**Tests — modified:**
- `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs` — assert trust hint appears in stdout after `InstallCodex` success.

---

## Conventions used in this plan

- **TUnit syntax:** `[Test]`, `await Assert.That(x).IsEqualTo(y)`, `[NotInParallel("token")]` for env-var-mutating tests. The repo does not use any `[SkipOnOS]` attribute — for OS-specific tests, guard with `if (OperatingSystem.IsWindows()) return;` at the top of the test body.
- **TempDir helper:** several test files in the repo define a private `sealed class TempDir : IDisposable` (see `PluginCommandCodexTests.cs:288`). The new test files should each declare their own copy at the bottom of the file — match the existing pattern, do not refactor into a shared helper.
- **AnsiConsole-free testing:** never instantiate `AnsiConsole` from a test. Tests for `CodingAgentsStep` pass a `(string) => bool` prompt callback and an `Action<string>` writeLine sink that records what would have been printed.
- **Test commands:** `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentDetectorTests/*"` runs one class. Use `--treenode-filter` glob syntax, **never** `--filter` (TUnit uses Microsoft Testing Platform).
- **Commits:** Linear-prefixed (`[AI-628]`). One commit per task. Use `git add <specific-files>`; never `git add .`.

---

### Task 1: Add `AgentDetector` with pure internal overload

**Files:**
- Create: `src/kapacitor/Commands/AgentDetector.cs`
- Test: `test/kapacitor.Tests.Unit/AgentDetectorTests.cs`

- [ ] **Step 1: Write failing tests for the pure internal overload**

Create `test/kapacitor.Tests.Unit/AgentDetectorTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class AgentDetectorTests {
    [Test]
    public async Task Pure_returns_true_when_path_dir_has_executable_match() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["/usr/local/bin", "/usr/bin"],
            extensions: [""],
            isExecutable: path => path == "/usr/local/bin/claude");

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Pure_returns_false_when_predicate_rejects_all_candidates() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["/usr/local/bin"],
            extensions: [""],
            isExecutable: _ => false);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_returns_false_when_paths_empty() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [],
            extensions: [""],
            isExecutable: _ => true);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_windows_shaped_detects_cmd_extension() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [@"C:\Users\me\AppData\Roaming\npm"],
            extensions: [".EXE", ".CMD"],
            isExecutable: path => path.EndsWith(".CMD"));

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Pure_windows_shaped_rejects_bare_name_when_pathext_set() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [@"C:\some\dir"],
            extensions: [".EXE", ".CMD"],
            isExecutable: path => !path.EndsWith(".EXE") && !path.EndsWith(".CMD"));

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_skips_empty_path_entry_without_throwing() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["", "/usr/local/bin"],
            extensions: [""],
            isExecutable: path => path == "/usr/local/bin/claude");

        await Assert.That(found).IsTrue();
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail to compile**

Run: `dotnet build test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: build fails with "type or namespace 'AgentDetector' could not be found".

- [ ] **Step 3: Create `AgentDetector` with the pure internal overload**

Create `src/kapacitor/Commands/AgentDetector.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Detects whether a coding-agent CLI is installed by probing every directory
/// on PATH for an executable file. Cross-platform: walks PATHEXT on Windows;
/// checks the executable bit on Unix. The pure internal overload accepts
/// fully-injected dependencies so unit tests don't touch the real environment.
/// </summary>
public static class AgentDetector {
    /// <summary>
    /// Pure, OS-agnostic core. Iterates the cartesian product of
    /// <paramref name="paths"/> × <paramref name="extensions"/>, returning
    /// true on the first <paramref name="isExecutable"/> hit.
    /// </summary>
    internal static bool IsInstalled(
        string binaryName,
        IEnumerable<string> paths,
        IEnumerable<string> extensions,
        Func<string, bool> isExecutable) {
        foreach (var dir in paths) {
            if (string.IsNullOrEmpty(dir)) continue;

            foreach (var ext in extensions) {
                var candidate = Path.Combine(dir, binaryName + ext);
                if (isExecutable(candidate)) return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify pure overload passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentDetectorTests/*"`
Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/AgentDetector.cs test/kapacitor.Tests.Unit/AgentDetectorTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] Add AgentDetector pure PATH-probe core

Iterates PATH × PATHEXT and returns true on the first executable
match. Pure internal overload takes paths/extensions/isExecutable
predicate as parameters so tests don't touch the real environment.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Add `AgentDetector` public overload + Unix executable-bit test

**Files:**
- Modify: `src/kapacitor/Commands/AgentDetector.cs`
- Modify: `test/kapacitor.Tests.Unit/AgentDetectorTests.cs`

- [ ] **Step 1: Write failing test for `PATH` null/empty handling**

Append to `AgentDetectorTests.cs` inside the class:

```csharp
[Test, NotInParallel("PATH_env_mutation")]
public async Task Public_returns_false_when_path_env_is_empty() {
    var original = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", "");
    try {
        await Assert.That(AgentDetector.IsInstalled("anything-at-all")).IsFalse();
    } finally {
        Environment.SetEnvironmentVariable("PATH", original);
    }
}

[Test, NotInParallel("PATH_env_mutation")]
public async Task Public_returns_false_when_path_env_is_null() {
    var original = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", null);
    try {
        await Assert.That(AgentDetector.IsInstalled("anything-at-all")).IsFalse();
    } finally {
        Environment.SetEnvironmentVariable("PATH", original);
    }
}
```

- [ ] **Step 2: Write failing Unix executable-bit test**

Append to `AgentDetectorTests.cs` inside the class:

```csharp
[Test, NotInParallel("PATH_env_mutation")]
public async Task Public_unix_requires_any_execute_bit() {
    if (OperatingSystem.IsWindows()) return; // Unix-only

    using var tmp     = new TempDir();
    var       exec    = Path.Combine(tmp.Path, "agentprobe-exec");
    var       nonExec = Path.Combine(tmp.Path, "agentprobe-nonexec");

    await File.WriteAllTextAsync(exec, "#!/bin/sh\nexit 0\n");
    await File.WriteAllTextAsync(nonExec, "not executable");
    File.SetUnixFileMode(exec,    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700
    File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);                              // 0600

    var original = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", tmp.Path);
    try {
        await Assert.That(AgentDetector.IsInstalled("agentprobe-exec")).IsTrue();
        await Assert.That(AgentDetector.IsInstalled("agentprobe-nonexec")).IsFalse();
    } finally {
        Environment.SetEnvironmentVariable("PATH", original);
    }
}
```

Also append the TempDir helper at the bottom of the file, **outside** the test class but inside the namespace:

```csharp
file sealed class TempDir : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
    );
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
```

- [ ] **Step 3: Run tests to confirm they fail to compile**

Run: `dotnet build test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: build fails with "'AgentDetector' does not contain a definition for 'IsInstalled' that takes 1 argument".

- [ ] **Step 4: Add the public overload to `AgentDetector`**

Append inside the `AgentDetector` class in `src/kapacitor/Commands/AgentDetector.cs`:

```csharp
    /// <summary>
    /// Probes the current process's PATH for <paramref name="binaryName"/>.
    /// Returns false on a null/empty PATH. On Unix, requires at least one of
    /// the user/group/other execute bits; on Windows, walks PATHEXT
    /// (defaulting to .EXE/.CMD/.BAT) and accepts any file that exists.
    /// </summary>
    public static bool IsInstalled(string binaryName) {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;

        var paths      = pathEnv.Split(Path.PathSeparator);
        var extensions = OperatingSystem.IsWindows() ? GetWindowsExtensions() : [""];

        return IsInstalled(binaryName, paths, extensions, IsExecutable);
    }

    static string[] GetWindowsExtensions() {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var raw     = string.IsNullOrEmpty(pathExt) ? ".EXE;.CMD;.BAT" : pathExt;

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    static bool IsExecutable(string path) {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true; // PATHEXT already filtered the candidates

        // Unix: any of UGO execute bits is enough — an intentional heuristic.
        // True access(X_OK) would require P/Invoke against the effective UID/GID.
        // The rare false positive (binary with execute bits but unrelated owner)
        // degrades to the same outcome as a runtime-broken binary.
        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }
```

- [ ] **Step 5: Run all `AgentDetectorTests` to verify everything passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentDetectorTests/*"`
Expected: all 9 tests pass on macOS/Linux; on Windows, 8 pass and the Unix-only test no-ops.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/AgentDetector.cs test/kapacitor.Tests.Unit/AgentDetectorTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] AgentDetector public overload with platform behaviour

Real PATH/PATHEXT read, null/empty PATH guard, Unix any-execute-bit
check. Tests cover the env-mutation cases under NotInParallel; the
Unix executable-bit test exercises the real filesystem and a real
chmod 0700 vs 0600.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Add `CodingAgentsStep` types and skeleton

**Files:**
- Create: `src/kapacitor/Commands/CodingAgentsStep.cs`

This task lands the type surface and an empty (always-skipping) `RunAsync` so subsequent tasks fill in branches one by one with TDD. No tests yet — they come in Task 4.

- [ ] **Step 1: Create the skeleton**

Create `src/kapacitor/Commands/CodingAgentsStep.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kapacitor setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    internal record Options(bool SkipClaude, bool SkipCodex, bool NoPrompt, bool LegacyProjectScope);
    internal record DetectedAgents(bool Claude, bool Codex);
    internal record Paths(
        string ClaudeSettingsPath,
        string? PluginDir,
        string CodexHooksPath,
        string CodexSkillsDir);
    internal record Installers(
        Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
        Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
        Func<string /*src*/, string /*dst*/, bool>                InstallCodexSkills);
    internal record Result(bool ClaudeInstalled, bool CodexHooksInstalled, bool CodexSkillsInstalled);

    /// <summary>
    /// Drives the agent-detection branches and dispatches to the installer
    /// delegates. Subsequent tasks fill in Claude, Codex hooks, Codex skills,
    /// and neither-detected behaviour.
    /// </summary>
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        // Filled in by Tasks 4–8.
        return Task.FromResult(new Result(false, false, false));
    }
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build src/kapacitor/kapacitor.csproj`
Expected: build succeeds with no errors or new warnings.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Commands/CodingAgentsStep.cs
git commit -m "$(cat <<'EOF'
[AI-628] Add CodingAgentsStep type surface

Internal records for Options, DetectedAgents, Paths, Installers,
Result. RunAsync skeleton returns a no-op result; branches are
filled in by subsequent commits with TDD per branch.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Implement Claude install branch in `CodingAgentsStep`

**Files:**
- Create: `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs`
- Modify: `src/kapacitor/Commands/CodingAgentsStep.cs`

This task introduces the test harness and covers the Claude install path: detected/not-detected, declined, success, failure.

- [ ] **Step 1: Write failing tests for the Claude branch**

Create `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs`:

```csharp
using kapacitor.Commands;
using static kapacitor.Commands.CodingAgentsStep;

namespace kapacitor.Tests.Unit;

public class CodingAgentsStepTests {
    [Test]
    public async Task Claude_detected_and_accepted_calls_installer_with_settings_path() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, NoPrompt: false, LegacyProjectScope: false);
        var paths    = TestPaths();
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, paths, calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo((paths.ClaudeSettingsPath, paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code plugin installed"));
    }

    [Test]
    public async Task Claude_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(false, true, false, false);
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code") && l.Contains("not installed"));
    }

    [Test]
    public async Task Claude_not_detected_skips_prompt_and_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var promptCount = 0;
        var options  = new Options(false, true, false, false);
        var detected = new DetectedAgents(Claude: false, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCount++; return true; }, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code not found"));
    }

    [Test]
    public async Task Claude_installer_failure_emits_warning_and_returns_false() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { ClaudeReturns = false };
        var options  = new Options(false, true, false, false);
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update Claude settings"));
    }

    static Paths TestPaths() => new(
        ClaudeSettingsPath: "/fake/.claude/settings.json",
        PluginDir:          "/fake/plugin",
        CodexHooksPath:     "/fake/.codex/hooks.json",
        CodexSkillsDir:     "/fake/.codex/skills");

    sealed class Sink {
        public List<string> Lines { get; } = [];
        public void Write(string s) => Lines.Add(s);
    }

    sealed class InstallerCalls {
        public bool ClaudeCalled { get; private set; }
        public (string Settings, string PluginDir)? ClaudeArgs { get; private set; }
        public bool ClaudeReturns { get; set; } = true;

        public bool CodexHooksCalled { get; private set; }
        public string? CodexHooksArg  { get; private set; }
        public bool CodexHooksReturns { get; set; } = true;

        public bool CodexSkillsCalled { get; private set; }
        public (string Src, string Dst)? CodexSkillsArgs { get; private set; }
        public bool CodexSkillsReturns { get; set; } = true;

        public Installers AsInstallers() => new(
            InstallClaudePlugin: (s, p) => { ClaudeCalled = true; ClaudeArgs = (s, p); return ClaudeReturns; },
            InstallCodexHooks:   h      => { CodexHooksCalled = true; CodexHooksArg = h; return CodexHooksReturns; },
            InstallCodexSkills:  (s, d) => { CodexSkillsCalled = true; CodexSkillsArgs = (s, d); return CodexSkillsReturns; }
        );
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: all 4 tests fail — `RunAsync` is currently a stub that always returns `(false, false, false)`.

- [ ] **Step 3: Implement the Claude branch**

Replace the body of `RunAsync` in `src/kapacitor/Commands/CodingAgentsStep.cs`:

```csharp
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        var claudeInstalled = HandleClaude(options, detected, paths, installers, prompt, writeLine);

        // Codex hooks + skills wired in subsequent tasks.
        return Task.FromResult(new Result(claudeInstalled, false, false));
    }

    static bool HandleClaude(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        if (!detected.Claude) {
            writeLine("  [dim]· Claude Code not found on PATH — skipping[/]");
            return false;
        }

        writeLine("  [green]✓[/] Claude Code detected");

        if (options.SkipClaude) {
            writeLine("  [dim]· Claude Code plugin skipped by flag[/]");
            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Claude Code plugin (hooks, skills, memory)?");

        if (!shouldInstall) {
            writeLine("  [dim]· Claude Code plugin not installed (you can run kapacitor plugin install later)[/]");
            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Plugin directory not found. Install manually inside Claude Code: [cyan]/plugin install <pluginPath>[/]");
            return false;
        }

        var ok = installers.InstallClaudePlugin(paths.ClaudeSettingsPath, paths.PluginDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Could not update Claude settings file. Install manually inside Claude Code: [cyan]/plugin install {paths.PluginDir}[/]");
            return false;
        }

        writeLine($"  [green]✓[/] Claude Code plugin installed (user: {paths.ClaudeSettingsPath})");
        return true;
    }
```

- [ ] **Step 4: Run tests to verify Claude branch passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/CodingAgentsStep.cs test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] CodingAgentsStep: Claude install branch

Covers detected+accepted, detected+declined, not-detected, and
installer-returns-false. Test harness records installer calls and
captures writeLine output for assertions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Implement Codex hooks branch (with `/hooks` trust hint)

**Files:**
- Modify: `src/kapacitor/Commands/CodingAgentsStep.cs`
- Modify: `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs`

- [ ] **Step 1: Write failing tests for the Codex hooks branch**

Append to `CodingAgentsStepTests`:

```csharp
[Test]
public async Task Codex_detected_and_accepted_installs_hooks_and_prints_trust_hint() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
    var detected = new DetectedAgents(Claude: false, Codex: true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexHooksInstalled).IsTrue();
    await Assert.That(calls.CodexHooksArg).IsEqualTo("/fake/.codex/hooks.json");
    await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed"));
    await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
}

[Test]
public async Task Codex_detected_and_declined_skips_installer_and_trust_hint() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, Codex: true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => false, writeLine: sink.Write);

    await Assert.That(result.CodexHooksInstalled).IsFalse();
    await Assert.That(calls.CodexHooksCalled).IsFalse();
    await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
}

[Test]
public async Task Codex_not_detected_skips_prompt_and_emits_skip_line() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, false);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexHooksInstalled).IsFalse();
    await Assert.That(calls.CodexHooksCalled).IsFalse();
    await Assert.That(sink.Lines).Contains(l => l.Contains("Codex CLI not found"));
}

[Test]
public async Task Codex_hooks_installer_failure_emits_warning_and_skips_trust_hint() {
    var sink     = new Sink();
    var calls    = new InstallerCalls { CodexHooksReturns = false };
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexHooksInstalled).IsFalse();
    await Assert.That(calls.CodexHooksCalled).IsTrue();
    await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Codex hooks"));
    await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: the 4 new tests fail; the 4 from Task 4 still pass.

- [ ] **Step 3: Implement the Codex hooks branch**

Update `RunAsync` and add `HandleCodexHooks` in `src/kapacitor/Commands/CodingAgentsStep.cs`:

```csharp
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        var claudeInstalled    = HandleClaude(options, detected, paths, installers, prompt, writeLine);
        var codexHooksInstalled = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);

        // Codex skills wired in Task 6.
        return Task.FromResult(new Result(claudeInstalled, codexHooksInstalled, false));
    }

    static bool HandleCodexHooks(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        if (!detected.Codex) {
            writeLine("  [dim]· Codex CLI not found on PATH — skipping[/]");
            return false;
        }

        writeLine("  [green]✓[/] Codex CLI detected");

        if (options.SkipCodex) {
            writeLine("  [dim]· Codex CLI hooks skipped by flag[/]");
            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Codex CLI hooks (and skills)?");

        if (!shouldInstall) {
            writeLine("  [dim]· Codex CLI hooks not installed (you can run kapacitor plugin install --codex later)[/]");
            return false;
        }

        var ok = installers.InstallCodexHooks(paths.CodexHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Codex hooks file.");
            return false;
        }

        writeLine($"  [green]✓[/] Codex hooks installed (user: {paths.CodexHooksPath})");
        writeLine("  [dim]  Next: run /hooks inside Codex and trust each kapacitor entry —[/]");
        writeLine("  [dim]  Codex won't execute hooks until each is explicitly trusted.[/]");
        return true;
    }
```

- [ ] **Step 4: Run tests to verify all 8 pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: all 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/CodingAgentsStep.cs test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] CodingAgentsStep: Codex hooks branch with /hooks trust hint

Hooks install is independent of the plugin dir (hooks.json content
is just the CLI command string + timeout). Trust hint prints only
when InstallCodexHooks returns true.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Implement Codex skills branch (depends on hooks success and plugin dir)

**Files:**
- Modify: `src/kapacitor/Commands/CodingAgentsStep.cs`
- Modify: `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs`

- [ ] **Step 1: Write failing tests for the Codex skills branch**

Append to `CodingAgentsStepTests`:

```csharp
[Test]
public async Task Codex_skills_installed_when_hooks_succeed_and_plugin_dir_present() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexSkillsInstalled).IsTrue();
    await Assert.That(calls.CodexSkillsArgs).IsEqualTo(("/fake/plugin/codex-skills", "/fake/.codex/skills"));
    await Assert.That(sink.Lines).Contains(l => l.Contains("Codex skills installed"));
}

[Test]
public async Task Codex_skills_not_attempted_when_hooks_fail() {
    var sink     = new Sink();
    var calls    = new InstallerCalls { CodexHooksReturns = false };
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexSkillsInstalled).IsFalse();
    await Assert.That(calls.CodexSkillsCalled).IsFalse();
}

[Test]
public async Task Codex_skills_failure_still_keeps_trust_hint() {
    var sink     = new Sink();
    var calls    = new InstallerCalls { CodexSkillsReturns = false };
    var options  = new Options(true, false, false, false);
    var detected = new DetectedAgents(false, true);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.CodexHooksInstalled).IsTrue();
    await Assert.That(result.CodexSkillsInstalled).IsFalse();
    await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed but skills"));
    await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
}
```

- [ ] **Step 2: Run tests to confirm 2 of the 3 fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: `Codex_skills_installed_when_hooks_succeed...` and `Codex_skills_failure_still_keeps_trust_hint` fail; `Codex_skills_not_attempted_when_hooks_fail` already passes (since skills isn't called yet at all).

- [ ] **Step 3: Add the Codex skills branch**

In `src/kapacitor/Commands/CodingAgentsStep.cs`, update `RunAsync`:

```csharp
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        var claudeInstalled     = HandleClaude(options, detected, paths, installers, prompt, writeLine);
        var codexHooksInstalled = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);
        var codexSkillsInstalled = codexHooksInstalled
            ? HandleCodexSkills(paths, installers, writeLine)
            : false;

        return Task.FromResult(new Result(claudeInstalled, codexHooksInstalled, codexSkillsInstalled));
    }

    static bool HandleCodexSkills(
        Paths paths,
        Installers installers,
        Action<string> writeLine) {
        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Codex hooks installed but skills could not be copied (plugin directory not found).");
            return false;
        }

        var src = Path.Combine(paths.PluginDir, "codex-skills");
        var ok  = installers.InstallCodexSkills(src, paths.CodexSkillsDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Codex hooks installed but skills could not be copied to {paths.CodexSkillsDir}");
            return false;
        }

        writeLine($"  [green]✓[/] Codex skills installed (user: {paths.CodexSkillsDir})");
        return true;
    }
```

Note: the trust hint already lives inside `HandleCodexHooks` (printed when hooks succeed). The skills failure test therefore relies on hooks success keeping the hint visible — no rewiring needed.

- [ ] **Step 4: Run tests to verify all 11 pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: 11 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/CodingAgentsStep.cs test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] CodingAgentsStep: Codex skills branch

Skills only attempted when hooks succeeded. Trust hint stays visible
even if skills install fails — hooks are what need trusting.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Handle `paths.PluginDir == null` and neither-detected warning

**Files:**
- Modify: `src/kapacitor/Commands/CodingAgentsStep.cs`
- Modify: `test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs`

- [ ] **Step 1: Write failing tests for null plugin dir and neither-detected**

Append to `CodingAgentsStepTests`:

```csharp
[Test]
public async Task Plugin_dir_null_skips_claude_install_but_keeps_codex_hooks() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(false, false, false, false);
    var detected = new DetectedAgents(true, true);
    var paths    = TestPaths() with { PluginDir = null };

    var result = await RunAsync(options, detected, paths, calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.ClaudeInstalled).IsFalse();
    await Assert.That(calls.ClaudeCalled).IsFalse();
    await Assert.That(result.CodexHooksInstalled).IsTrue();
    await Assert.That(calls.CodexHooksCalled).IsTrue();
    await Assert.That(result.CodexSkillsInstalled).IsFalse();
    await Assert.That(calls.CodexSkillsCalled).IsFalse();
    await Assert.That(sink.Lines).Contains(l => l.Contains("Plugin directory not found"));
    await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed but skills could not be copied"));
}

[Test]
public async Task Neither_detected_emits_warning_and_no_installer_calls() {
    var sink     = new Sink();
    var calls    = new InstallerCalls();
    var options  = new Options(false, false, false, false);
    var detected = new DetectedAgents(false, false);

    var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
        prompt: _ => true, writeLine: sink.Write);

    await Assert.That(result.ClaudeInstalled).IsFalse();
    await Assert.That(result.CodexHooksInstalled).IsFalse();
    await Assert.That(result.CodexSkillsInstalled).IsFalse();
    await Assert.That(calls.ClaudeCalled).IsFalse();
    await Assert.That(calls.CodexHooksCalled).IsFalse();
    await Assert.That(calls.CodexSkillsCalled).IsFalse();
    await Assert.That(sink.Lines).Contains(l => l.Contains("No supported agent CLI detected"));
}
```

- [ ] **Step 2: Run tests to confirm failures**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: both new tests fail — `Plugin_dir_null...` currently fails because Codex hooks should be `true` but the test passes already if the existing code works (verify); `Neither_detected...` fails because no warning is emitted yet.

- [ ] **Step 3: Add the "neither detected" warning at the end of `RunAsync`**

Update `RunAsync` body in `src/kapacitor/Commands/CodingAgentsStep.cs`:

```csharp
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        var claudeInstalled     = HandleClaude(options, detected, paths, installers, prompt, writeLine);
        var codexHooksInstalled = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);
        var codexSkillsInstalled = codexHooksInstalled
            ? HandleCodexSkills(paths, installers, writeLine)
            : false;

        if (!detected.Claude && !detected.Codex) {
            writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code or Codex CLI to start capturing sessions.");
        }

        return Task.FromResult(new Result(claudeInstalled, codexHooksInstalled, codexSkillsInstalled));
    }
```

The `paths.PluginDir == null` behavior is already correct from Tasks 4 and 6: `HandleClaude` checks and returns false on null; `HandleCodexHooks` ignores `PluginDir` entirely; `HandleCodexSkills` checks and returns false on null.

- [ ] **Step 4: Run tests to verify all 13 pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodingAgentsStepTests/*"`
Expected: 13 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/CodingAgentsStep.cs test/kapacitor.Tests.Unit/CodingAgentsStepTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] CodingAgentsStep: neither-detected warning + null plugin dir

PluginDir null: Claude install warns and skips, Codex hooks still
install (hooks.json doesn't reference plugin dir), Codex skills warn
and skip. Neither-detected emits a yellow warning at the end of the
step; the rest of setup continues.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Wire `CodingAgentsStep` into `SetupCommand` step 4

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

This is the only task that touches `SetupCommand`. It replaces the existing step 4 block (the `// Step 4: Claude Code plugin` section, roughly lines 137–187 in the current `SetupCommand.cs`) with a call to `CodingAgentsStep.RunAsync`. It also threads the new flags through.

- [ ] **Step 1: Read the existing step 4 block to know what you're replacing**

Run: `sed -n '137,187p' src/kapacitor/Commands/SetupCommand.cs`
Expected: see the current `// Step 4: Claude Code plugin` block ending at the blank line before `// Step 5: Daemon name + save`.

- [ ] **Step 2: Update flag parsing at the top of `HandleAsync`**

In `src/kapacitor/Commands/SetupCommand.cs`, find this block near the top of `HandleAsync`:

```csharp
        var serverUrlArg = GetArg(args, "--server-url");
        var noPrompt     = args.Contains("--no-prompt");
        var forceDevice  = args.Contains("--device");
```

Replace it with:

```csharp
        var serverUrlArg     = GetArg(args, "--server-url");
        var noPrompt         = args.Contains("--no-prompt");
        var forceDevice      = args.Contains("--device");
        var skipClaudeFlag   = args.Contains("--skip-claude-hooks");
        var skipCodexFlag    = args.Contains("--skip-codex-hooks");
        var legacyPluginScope = GetArg(args, "--plugin-scope"); // "user" | "project" | "skip" | null
        var skipClaude       = skipClaudeFlag || legacyPluginScope == "skip";
        var legacyProjectScope = legacyPluginScope == "project";
```

- [ ] **Step 3: Replace the step 4 block**

In `src/kapacitor/Commands/SetupCommand.cs`, find the section that starts with this comment:

```csharp
        // Step 4: Claude Code plugin
        AnsiConsole.Write(new Rule("[yellow]Step 4/5 — Claude Code Plugin[/]").LeftJustified());
```

…and ends at the blank line before:

```csharp
        // Step 5: Daemon name + save
```

Replace the entire block (Step 4 only) with:

```csharp
        // Step 4: Coding agents
        AnsiConsole.Write(new Rule("[yellow]Step 4/5 — Coding agents[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  Kapacitor records sessions by installing hooks into your coding agent CLIs.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude: AgentDetector.IsInstalled("claude"),
            Codex:  AgentDetector.IsInstalled("codex"));

        var claudeSettingsPath = legacyProjectScope
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var stepOptions = new CodingAgentsStep.Options(
            SkipClaude:         skipClaude,
            SkipCodex:          skipCodexFlag,
            NoPrompt:           noPrompt,
            LegacyProjectScope: legacyProjectScope);

        var stepPaths = new CodingAgentsStep.Paths(
            ClaudeSettingsPath: claudeSettingsPath,
            PluginDir:          pluginPath,
            CodexHooksPath:     CodexPaths.UserHooksJson,
            CodexSkillsDir:     CodexPaths.UserSkillsDir);

        var stepInstallers = new CodingAgentsStep.Installers(
            InstallClaudePlugin: InstallPlugin,
            InstallCodexHooks:   PluginCommand.InstallCodexHooks,
            InstallCodexSkills:  PluginCommand.InstallCodexSkills);

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var _ = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

        await Console.Out.WriteLineAsync();
```

- [ ] **Step 4: Build and run the full unit test suite to confirm nothing regressed**

Run: `dotnet build src/kapacitor/kapacitor.csproj`
Expected: build succeeds.

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: every test passes.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs
git commit -m "$(cat <<'EOF'
[AI-628] Wire CodingAgentsStep into setup step 4

Replaces the Claude-only step 4 with a call to CodingAgentsStep.RunAsync
that drives both Claude and Codex. New flags --skip-claude-hooks /
--skip-codex-hooks parsed alongside existing --plugin-scope; legacy
"project" routes to project-scope Claude install; legacy "skip" maps
to --skip-claude-hooks.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Add `/hooks` trust hint to standalone `PluginCommand.InstallCodex`

**Files:**
- Modify: `src/kapacitor/Commands/PluginCommand.cs`
- Modify: `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`

The wizard and the standalone `kapacitor plugin install --codex` should emit the same trust hint. The standalone command currently emits only the project-`.codex`-directory trust note for `--project` installs; we add the per-hook trust hint for every successful Codex hook install.

- [ ] **Step 1: Write a failing test asserting the trust hint appears**

Open `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs` and check whether existing tests capture stdout. If they do not, the simplest path is to add a test that calls `PluginCommand.InstallCodexHooks` directly (which the wizard now also calls) and asserts on the hook file shape — the wizard-side stdout is already covered by `CodingAgentsStepTests`.

For the standalone path, add a small focused test using stdout capture:

```csharp
[Test, NotInParallel("Console.Out_redirect")]
public async Task InstallCodex_prints_hooks_trust_hint_after_success() {
    using var tmp = new TempDir();
    // Redirect HOME so CodexPaths.UserHooksJson points inside the temp dir.
    var originalHome = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
    Environment.SetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", tmp.Path);

    var capturedOut = new StringWriter();
    var originalOut = Console.Out;
    Console.SetOut(capturedOut);

    try {
        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"]);
        await Assert.That(exit).IsEqualTo(0);

        var stdout = capturedOut.ToString();
        await Assert.That(stdout).Contains("/hooks");
        await Assert.That(stdout).Contains("trust");
    } finally {
        Console.SetOut(originalOut);
        Environment.SetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", originalHome);
    }
}
```

Note: This test relies on `PathHelpers.HomeDirectory` reading HOME/USERPROFILE at call time. If `PathHelpers.HomeDirectory` caches its result eagerly, this test will instead need to use a different seam — check `src/Kapacitor.Core/PathHelpers.cs` and adjust. If the path is captured per-call, this test works as written.

- [ ] **Step 2: Run the test to confirm it fails**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/InstallCodex_prints_hooks_trust_hint_after_success"`
Expected: test fails because the trust hint is not yet emitted.

- [ ] **Step 3: Emit the trust hint after a successful hooks install**

In `src/kapacitor/Commands/PluginCommand.cs`, find this line inside `InstallCodex`:

```csharp
        await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");
```

Immediately after it (and before the `// Skills are user-scoped only.` comment block), add:

```csharp
        await Console.Out.WriteLineAsync(
            "Next: run /hooks inside Codex and trust each kapacitor entry — " +
            "Codex won't execute hooks until each is explicitly trusted."
        );
```

- [ ] **Step 4: Run the new test plus the existing `PluginCommandCodexTests` to verify nothing regressed**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/*"`
Expected: every test passes.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/PluginCommand.cs test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "$(cat <<'EOF'
[AI-628] PluginCommand: print /hooks trust hint after Codex install

Same string as the wizard so users see consistent guidance regardless
of which install path they took.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: Update `help-setup.txt`

**Files:**
- Modify: `src/Kapacitor.Core/Resources/help-setup.txt`

- [ ] **Step 1: Replace the file contents**

Open `src/Kapacitor.Core/Resources/help-setup.txt` and replace the whole file with:

```text
kapacitor setup — Configure server, login, and install hooks

Usage: kapacitor setup [options]

Options:
  --server-url <url>          Server URL (skip prompt)
  --daemon-name <name>        Daemon name (skip prompt)
  --default-visibility <vis>  Default visibility: private, org_public, public
  --no-prompt                 Non-interactive mode (requires --server-url)
  --device                    Force GitHub Device Flow during the login step
                              (use in SSH / headless envs)
  --skip-claude-hooks         Don't install the Claude Code plugin even if
                              the claude CLI is detected on PATH.
  --skip-codex-hooks          Don't install Codex CLI hooks/skills even if
                              the codex CLI is detected on PATH.

The setup wizard detects every supported coding agent on PATH (claude, codex)
and asks one yes/no per detected agent. Hooks are installed user-wide. For
project-scope or post-setup re-installs, use:

  kapacitor plugin install [--codex] [--project]

Legacy:
  --plugin-scope <user|project|skip>
        Retained for backwards compatibility. Maps as:
          user    → no-op (matches new default)
          project → install the Claude plugin into <repo>/.claude/settings.local.json
          skip    → alias for --skip-claude-hooks
        New scripts should use --skip-claude-hooks / --skip-codex-hooks. For
        project-scope installs, prefer `kapacitor plugin install --project`.
```

- [ ] **Step 2: Verify the help file is wired up by running `kapacitor setup --help`**

Run: `dotnet run --project src/kapacitor/kapacitor.csproj -- setup --help`
Expected: the new help text prints. (If the project uses `--help` differently — e.g., routes through a global usage handler — check `Program.cs` to confirm; the help file content is the only piece this task is responsible for.)

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Core/Resources/help-setup.txt
git commit -m "$(cat <<'EOF'
[AI-628] Help: document --skip-claude-hooks / --skip-codex-hooks

--plugin-scope kept and marked legacy with explicit value→behavior
mapping.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 11: Update README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Rewrite the "Setup wizard walks you through" list**

Find this section in `README.md` (currently around lines 34–40):

```markdown
The setup wizard walks you through:

1. **Server URL** — enter the URL your admin provided
2. **Login** — authenticates via GitHub Device Flow (if the server requires auth)
3. **Default visibility** — choose how your sessions are visible to others
4. **Claude Code plugin** — installs hooks, skills, and collaborative memory (user-wide or project-only)
5. **Agent daemon** — configure the daemon name for remote agent execution
```

Replace item 4 only with:

```markdown
4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH` and offers to install hooks/skills for each (user-wide)
```

- [ ] **Step 2: Add CI-friendly flag note under the non-interactive example**

Find this block (currently around lines 44–48):

```markdown
For non-interactive environments:

```bash
kapacitor setup --server-url https://capacitor.example.com --default-visibility org_public --no-prompt
```
```

Immediately after the code block, add:

```markdown
In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks` and/or `--skip-codex-hooks`.
```

- [ ] **Step 3: Delete the "Also using Codex CLI?" sub-section and replace with a one-line follow-up note**

Find this section (currently around lines 50–64), starting at `#### Also using Codex CLI?` and ending at the empty line before `### 3. Import existing sessions (optional)`. Delete all of it. Replace with:

```markdown
> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kapacitor plugin install [--codex] [--project]`. After installing Codex hooks, run `/hooks` inside Codex and trust each kapacitor entry — Codex doesn't execute hooks until each is explicitly trusted. After a `--project` install, also run `codex` once in the repo and accept the trust prompt.

> **Need at least one agent to capture sessions:** the setup wizard runs to completion without an agent CLI on `PATH` (it'll still configure your profile, auth, and daemon), but kapacitor only records work once Claude Code or Codex CLI is installed and the hooks are in place.
```

- [ ] **Step 4: Trim the "Hosted Codex agents" sub-section's language**

Find this section (currently around line 214). Locate this paragraph:

```markdown
To launch hosted Codex agents from the dashboard, the Codex hook surface must be installed first:
```

Replace with:

```markdown
Hosted Codex agents require the Codex hook surface — if you said yes during `kapacitor setup`, you already have it. Otherwise install it manually:
```

(Everything below that paragraph in the Hosted Codex agents section stays as-is, including the upgrading note.)

- [ ] **Step 5: Build the docs locally to confirm no broken anchors**

There is no docs build for this repo; the README is rendered by GitHub. Spot-check that the markdown structure is intact by running:

Run: `grep -c '^#### ' README.md`
Expected: count should be one less than before (the deleted `#### Also using Codex CLI?` is gone).

Run: `grep -n '^### ' README.md`
Expected: the H3 headings still resolve to the original sections (`### 1. Install the CLI` … `### 4. Open the dashboard`).

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
[AI-628] README: agent-neutral wording for setup

Item 4 is now "Coding-agent hooks" not "Claude Code plugin". The
"Also using Codex CLI?" sub-section is gone — its content collapses
into one follow-up note for post-setup / project-scope installs, plus
a callout that the wizard runs without an agent CLI but only captures
sessions once one is installed. Hosted Codex agents section reworded
to acknowledge setup may have already installed the hooks.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 12: AOT publish verification + final test sweep

**Files:** none — verification only.

- [ ] **Step 1: Run the full unit test suite**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: every test passes. No "skipped" tests other than the Unix-only test on Windows hosts.

- [ ] **Step 2: AOT publish and grep for trimming/AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: command produces no output (empty grep). If there is output, investigate: the new code uses only `File.Exists`, `File.GetUnixFileMode`, `Environment.GetEnvironmentVariable`, `Path.Combine`, `Path.PathSeparator`, and `OperatingSystem.IsWindows()` — all AOT-safe. Any warning is almost certainly something the wiring step (Task 8) imported unintentionally.

- [ ] **Step 3: Manual smoke — both detected**

Run on a Mac with both `claude` and `codex` on PATH:

Run: `dotnet run --project src/kapacitor/kapacitor.csproj -- setup --server-url https://capacitor-staging.example.com`
(Use whatever staging server URL is appropriate.)

Expected output for step 4:

```
── Step 4/5 — Coding agents ──────────────────────────────────
  Kapacitor records sessions by installing hooks into your coding agent CLIs.
  ✓ Claude Code detected
  ✓ Codex CLI detected
  Install Claude Code plugin (hooks, skills, memory)? [Y/n]
  Install Codex CLI hooks (and skills)? [Y/n]
  ✓ Claude Code plugin installed (user: ~/.claude/settings.json)
  ✓ Codex hooks installed (user: ~/.codex/hooks.json)
    Next: run /hooks inside Codex and trust each kapacitor entry —
    Codex won't execute hooks until each is explicitly trusted.
  ✓ Codex skills installed (user: ~/.codex/skills/)
```

- [ ] **Step 4: Manual smoke — Codex missing**

Run: `PATH=$(echo $PATH | tr ':' '\n' | grep -v codex | tr '\n' ':') dotnet run --project src/kapacitor/kapacitor.csproj -- setup ...`

Expected: step 4 shows `· Codex CLI not found on PATH — skipping` instead of the Codex prompt, and only Claude is offered.

- [ ] **Step 5: Manual smoke — neither installed**

Run: `PATH=/tmp dotnet run --project src/kapacitor/kapacitor.csproj -- setup ...`

Expected: step 4 shows two skip lines, then the yellow `⚠ No supported agent CLI detected.` warning, then the wizard moves to step 5 normally.

- [ ] **Step 6: Manual smoke — legacy `--plugin-scope skip` still skips Claude**

Run: `dotnet run --project src/kapacitor/kapacitor.csproj -- setup --server-url ... --no-prompt --plugin-scope skip`

Expected: setup completes; `~/.claude/settings.json` is not modified by this run (test this on a machine where the file is already absent so the absence after the run proves the skip).

- [ ] **Step 7: Push branch and open PR**

```bash
git push -u origin HEAD
gh pr create --title "[AI-628] Agent-neutral kapacitor setup (Claude + Codex)" --body "$(cat <<'EOF'
## Summary

- `kapacitor setup` step 4 now detects Claude Code and Codex CLI on `PATH` and asks one yes/no per detected agent. No more separate `kapacitor plugin install --codex` ritual during onboarding.
- New `--skip-claude-hooks` / `--skip-codex-hooks` flags for `--no-prompt`. Legacy `--plugin-scope` is preserved as a silent back-compat alias.
- Codex `/hooks` per-entry trust gotcha is now surfaced in both setup output and the standalone `kapacitor plugin install --codex` output, plus the README.

Spec: `docs/superpowers/specs/2026-05-15-ai-628-agent-neutral-setup-design.md`
Plan: `docs/superpowers/plans/2026-05-15-ai-628-agent-neutral-setup.md`

## Test plan

- [x] Unit tests pass (`dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`)
- [x] AOT publish clean (`dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` is empty)
- [x] Manual: both agents detected → both prompts, both installs
- [x] Manual: Codex missing → skip line, Claude-only prompt
- [x] Manual: neither detected → warning, setup continues
- [x] Manual: `--no-prompt --plugin-scope skip` legacy mapping still skips Claude

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-Review

**Spec coverage check:**

| Spec section | Plan task(s) |
|---|---|
| AgentDetector design (PATH probe + executable check + null PATH guard) | Tasks 1–2 |
| Setup step 4 layout (detected/skipped/declined branches + Codex trust hint) | Tasks 4–7 (CodingAgentsStep) + Task 8 (SetupCommand wiring) |
| Refactor for testability (Paths, Installers injection) | Task 3 (types) + Tasks 4–7 (logic) |
| CLI flags (`--skip-claude-hooks`, `--skip-codex-hooks`, `--plugin-scope` legacy) | Task 8 |
| Standalone command `/hooks` trust hint parity | Task 9 |
| README changes (5 items) | Task 11 |
| Help-text update | Task 10 |
| AgentDetector unit tests | Tasks 1–2 |
| CodingAgentsStep unit tests (12 scenarios) | Tasks 4–7 |
| PluginCommand.InstallCodex trust hint test | Task 9 |
| AOT publish clean | Task 12 |

Every spec section has at least one corresponding task. The 12 spec test scenarios for `CodingAgentsStep` are covered: 4 in Task 4 (Claude), 4 in Task 5 (Codex hooks), 3 in Task 6 (skills), 2 in Task 7 (null plugin dir + neither detected). The `--plugin-scope project` legacy mapping is exercised end-to-end by Task 8's wiring and the existing `SetupCommand` integration tests — no dedicated unit test is added because there's no `SetupCommand.HandleAsync` test harness today and bolting one on is well out of scope for AI-628.

**Placeholder scan:** every step has either complete code, a literal command with expected output, or both. No "TBD" / "implement later" / "add error handling" anywhere.

**Type consistency:** `CodingAgentsStep.Options` / `DetectedAgents` / `Paths` / `Installers` / `Result` are introduced in Task 3 and referenced verbatim in Tasks 4–8. `InstallClaudePlugin` / `InstallCodexHooks` / `InstallCodexSkills` field names match across the type def, the test harness, and the wiring step. `AgentDetector.IsInstalled` has the two overload signatures specified consistently across Tasks 1, 2, and 8.

---

## Execution

**Plan complete and saved to `docs/superpowers/plans/2026-05-15-ai-628-agent-neutral-setup.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
