# AI-730 — Cursor hooks dispatcher and setup wiring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `kapacitor hook --cursor` dispatcher that ingests Cursor IDE Agent sessions in real time via Cursor's hooks API, plus the setup/installer wiring required to register hooks in `~/.cursor/hooks.json`.

**Architecture:** A single CLI dispatcher entry (`CursorHookCommand`) parses Cursor's stdin JSON payload, normalizes it, drains a per-session spool of previously-failed canonical-event hooks, POSTs the current event to the matching `/hooks/<event>/cursor` route, then runs a resumable transcript JSONL backfill from the server's watermark to EOF — all bounded by a shared 2-second wall-clock budget. Setup writes `~/.cursor/hooks.json` invoking the bare PATH-resolved command for all 8 Cursor hooks, with a `.kapacitor-hooks-version` marker enabling the existing AI-734 postinstall to refresh hook strings on every CLI upgrade. The dispatcher uses a new `PostOnceAsync` HTTP helper (no retry, short per-call timeout) — the existing `PostWithRetryAsync` 30-second default is unsafe under the hook budget.

**Tech Stack:** .NET 10 NativeAOT, `System.Text.Json.Nodes`, `HttpClient` with `CancellationTokenSource`-linked deadlines, TUnit for tests, WireMock.Net for HTTP integration.

**Cross-repo dependency:** This PR must ship together with AI-731 (kapacitor-server). The CLI POSTs to server routes whose names are owned by AI-731; this plan uses the URL strings agreed in the design spec ([`docs/superpowers/specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md`](../specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md)). Any divergence must be reconciled before merge.

**Source spec:** [`docs/superpowers/specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md`](../specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md) (rev 7).

---

## File Structure

### New files

| Path | Responsibility |
|---|---|
| `src/Kapacitor.Cli.Core/Cursor/CursorHooksParser.cs` | Recognises kapacitor entries in `~/.cursor/hooks.json`; mirrors `CodexHooksParser`. |
| `src/Kapacitor.Cli.Core/CursorHooksInstaller.cs` | Marker file helpers (`.kapacitor-hooks-version` next to `hooks.json`); mirrors `CodexHooksInstaller`. |
| `src/Kapacitor.Cli/Commands/CursorHookCommand.cs` | Per-invocation dispatcher: stdin → spool drain → hook POST → transcript backfill, all under shared 2s budget. |
| `src/Kapacitor.Cli/Commands/CursorHookSpool.cs` | Per-session JSONL spool at `~/.cursor/kapacitor-pending/<sid>.jsonl` for failed canonical-event hooks. |
| `src/Kapacitor.Cli/Commands/CursorTranscriptBackfill.cs` | Watermark GET + line-by-line transcript POST loop bounded by the dispatcher budget. |
| `src/Kapacitor.Cli/Commands/CursorHookEventMap.cs` | Maps Cursor `hook_event_name` (camelCase) to server-route segment (kebab-case) and classifies each event as canonical-event-bearing vs telemetry-only. |
| `src/Kapacitor.Cli.Core/Resources/help-hook.txt` | Help text for the new `kapacitor hook` command. (Replaces ad-hoc per-event templating in `Program.cs` — see Task 12.) |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs` | Parser unit tests. |
| `test/Kapacitor.Cli.Tests.Unit/CursorHooksInstallerTests.cs` | Marker installer unit tests. |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookEventMapTests.cs` | Event-name → URL segment + telemetry classification tests. |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs` | Spool append/drain/cap/cleanup unit tests. |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorPathsIsInstalledTests.cs` | Per-OS `IsInstalled()` detection tests. |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs` | Dispatcher unit tests (normalization, disabled-session, malformed, budget, telemetry-only, thought ID stability). |
| `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorTranscriptBackfillTests.cs` | Watermark resume + line POST loop tests against a fake transport. |
| `test/Kapacitor.Cli.Tests.Unit/PluginCommandCursorTests.cs` | `plugin install --cursor` / `--remove --cursor` / `--if-installed` tests. |
| `test/Kapacitor.Cli.Tests.Unit/CursorHooksWriterTests.cs` | hooks.json merge preserves user-authored entries; new install writes all 8 events. |
| `test/Kapacitor.Cli.Tests.Integration/CursorHookDispatcherTests.cs` | WireMock end-to-end per route + slow-server budget enforcement + spool persistence across restarts. |

### Modified files

| Path | Change |
|---|---|
| `src/Kapacitor.Cli.Core/Cursor/CursorPaths.cs` | Add `IsInstalled()` (multi-OS user-dir probe), `UserHooksJson`, `SpoolDir` properties. |
| `src/Kapacitor.Cli.Core/HttpClientExtensions.cs` | Add `PostOnceAsync(url, content, timeout, ct)` and `GetOnceAsync(url, timeout, ct)` extensions — no retries, dedicated CTS linked to caller's `ct`. |
| `src/Kapacitor.Cli/Commands/PluginCommand.cs` | Add `InstallCursor` / `RemoveCursor` branches; add `InstallCursorHooks(hooksPath)` / `RemoveCursorHooks(hooksPath)` helpers. |
| `src/Kapacitor.Cli/Commands/SetupCommand.cs` | Detect Cursor via `CursorPaths.IsInstalled()`; route through `CodingAgentsStep`. |
| `src/Kapacitor.Cli/Commands/CodingAgentsStep.cs` | Extend `DetectedAgents` / `Options` / `Paths` / `Installers` / `Result` with Cursor; add `HandleCursorHooks` mirror of `HandleCodexHooks`. |
| `src/Kapacitor.Cli/Program.cs` | Add `case "hook":` dispatcher that requires a vendor flag. |
| `npm/kapacitor/bin/postinstall.js` | Add `plugin install --cursor --if-installed` to the refresh list. |
| `README.md` | Add Cursor hooks section under Getting started + per-command notes. |
| `src/Kapacitor.Cli.Core/Resources/help-setup.txt` | Document `--skip-cursor-hooks` flag and Cursor user-dir detection. |
| `src/Kapacitor.Cli.Core/Resources/help-plugin.txt` | Document `plugin install --cursor [--if-installed]` and `plugin remove --cursor`. |

---

## Server-route URL strings (must align with AI-731)

The CLI uses these route strings. AI-731 owns the server side; reconcile before merge.

| Hook event (Cursor) | Route segment | Full URL (against `baseUrl`) |
|---|---|---|
| `sessionStart` | `session-start/cursor` | `POST {baseUrl}/hooks/session-start/cursor` |
| `sessionEnd` | `session-end/cursor` | `POST {baseUrl}/hooks/session-end/cursor` |
| `beforeSubmitPrompt` | `user-prompt/cursor` | `POST {baseUrl}/hooks/user-prompt/cursor` |
| `afterAgentResponse` | `agent-response/cursor` | `POST {baseUrl}/hooks/agent-response/cursor` |
| `afterAgentThought` | `agent-thought/cursor` | `POST {baseUrl}/hooks/agent-thought/cursor` |
| `preToolUse` | `pre-tool-use/cursor` | `POST {baseUrl}/hooks/pre-tool-use/cursor` |
| `postToolUse` | `post-tool-use/cursor` | `POST {baseUrl}/hooks/post-tool-use/cursor` |
| `postToolUseFailure` | `post-tool-use-failure/cursor` | `POST {baseUrl}/hooks/post-tool-use-failure/cursor` |

Transcript backfill (one line per POST):

* Watermark GET: `GET {baseUrl}/api/cursor-sessions/{sessionId}/transcript-watermark` — returns `{"last_line_number": N}` on hit, 404 when no lines accepted yet. <500ms server budget.
* Transcript-line POST: `POST {baseUrl}/hooks/transcript-line/cursor` with body `{"session_id": "...", "line_index": N, "line": "<raw JSONL line>"}`. ~1s server budget per line.

---

## Per-call timeouts inside the 2-second hook budget

* Hook-event POST: 1000 ms.
* Watermark GET: 500 ms.
* Transcript-line POST: 1000 ms each.

The `Stopwatch` shared across all phases is authoritative — per-call timeouts only protect against a single hung call within a phase. If the shared budget fires first, the linked CTS cancels in-flight calls and the dispatcher returns 0.

---

## Task list

### Task 1: `CursorHooksParser` — recognise kapacitor entries in `hooks.json`

**Files:**

* Create: `src/Kapacitor.Cli.Core/Cursor/CursorHooksParser.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs`

Cursor's hooks.json shape is `{"version": 1, "hooks": {"<eventName>": [{"command": "..."}]}}`. Mirror the matcher pattern from `CodexHooksParser` but match `"kapacitor hook --cursor"` substring (not `"kapacitor codex-hook"`).

- [ ] **Step 1: Write the failing test file**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHooksParserTests {
    [Test]
    public async Task EntryReferencesKapacitorCursorHook_true_for_bare_command() {
        var entry = JsonNode.Parse("""{"command":"kapacitor hook --cursor"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_true_for_command_with_extra_flags() {
        var entry = JsonNode.Parse("""{"command":"kapacitor hook --cursor --debug"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_false_for_third_party_command() {
        var entry = JsonNode.Parse("""{"command":"/usr/local/bin/other"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_false_for_null() {
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(null)).IsFalse();
    }

    [Test]
    public async Task HasKapacitorHooksFor_true_when_every_event_has_kapacitor_entry() {
        var root = JsonNode.Parse("""
            {"hooks": {
                "sessionStart": [{"command":"kapacitor hook --cursor"}],
                "sessionEnd":   [{"command":"kapacitor hook --cursor"}]
            }}
        """)!.AsObject();
        await Assert.That(CursorHooksParser.HasKapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsTrue();
    }

    [Test]
    public async Task HasKapacitorHooksFor_false_when_event_missing() {
        var root = JsonNode.Parse("""{"hooks": {"sessionStart": [{"command":"kapacitor hook --cursor"}]}}""")!.AsObject();
        await Assert.That(CursorHooksParser.HasKapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksParserTests/*"`
Expected: FAIL — type `CursorHooksParser` not found.

- [ ] **Step 3: Implement `CursorHooksParser`**

```csharp
// src/Kapacitor.Cli.Core/Cursor/CursorHooksParser.cs
using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Core.Cursor;

/// <summary>
/// Parsing helpers for <c>~/.cursor/hooks.json</c>. Cursor's schema differs
/// from Codex's: entries are flat <c>{"command": "..."}</c> objects keyed
/// directly by the camelCase event name, not nested under <c>"hooks"</c>.
/// </summary>
public static class CursorHooksParser {
    /// <summary>The 8 Cursor hook events this dispatcher handles.</summary>
    public static readonly string[] CursorHookEvents = [
        "sessionStart",
        "sessionEnd",
        "beforeSubmitPrompt",
        "afterAgentResponse",
        "afterAgentThought",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure"
    ];

    /// <summary>
    /// True if <paramref name="entry"/> is an object whose <c>command</c>
    /// string contains <c>"kapacitor hook --cursor"</c>.
    /// </summary>
    public static bool EntryReferencesKapacitorCursorHook(JsonNode? entry) {
        if (entry?["command"] is JsonValue jv &&
            jv.TryGetValue<string>(out var cmd) &&
            cmd.Contains("kapacitor hook --cursor")) {
            return true;
        }
        return false;
    }

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one
    /// hooks.json entry referencing the kapacitor cursor command.
    /// </summary>
    public static bool HasKapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            var any = false;
            foreach (var entry in entries) {
                if (EntryReferencesKapacitorCursorHook(entry)) {
                    any = true;
                    break;
                }
            }
            if (!any) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksParserTests/*"`
Expected: PASS, 6/6.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/Cursor/CursorHooksParser.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs
git commit -m "[AI-730] Add CursorHooksParser

$(cat <<'EOF'
Mirror of CodexHooksParser for Cursor's flat hooks.json schema. Recognises
"kapacitor hook --cursor" entries; HasKapacitorHooksFor gates the marker
installer's pre-marker detection.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Extend `CursorPaths` with `IsInstalled()`, `UserHooksJson`, `SpoolDir`

**Files:**

* Modify: `src/Kapacitor.Cli.Core/Cursor/CursorPaths.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorPathsIsInstalledTests.cs`

The design forbids using `AgentDetector.IsInstalled("cursor")` — PATH probe misses Cursor IDE users who never installed the `cursor` shell command. Detect by user-dir presence per OS.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorPathsIsInstalledTests.cs
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorPathsIsInstalledTests {
    [Test]
    public async Task IsInstalled_true_when_user_home_has_dot_cursor() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".cursor"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_macos_user_dir_exists() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Library", "Application Support", "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.MacOs, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_linux_config_user_dir_exists() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".config", "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_windows_appdata_user_dir_exists() {
        using var tmp = new TempHome();
        var appData = Path.Combine(tmp.Path, "AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(appData, "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Windows, appData)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_no_cursor_dirs_exist() {
        using var tmp = new TempHome();
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsFalse();
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.MacOs, appData: null)).IsFalse();
    }

    [Test]
    public async Task UserHooksJson_is_dot_cursor_hooks_json_under_home() {
        var resolved = CursorPaths.UserHooksJson(home: "/tmp/h", platform: OsPlatform.Linux);
        await Assert.That(resolved).IsEqualTo("/tmp/h/.cursor/hooks.json");
    }

    [Test]
    public async Task SpoolDir_is_dot_cursor_kapacitor_pending_under_home() {
        var resolved = CursorPaths.SpoolDir(home: "/tmp/h", platform: OsPlatform.Linux);
        await Assert.That(resolved).IsEqualTo("/tmp/h/.cursor/kapacitor-pending");
    }

    sealed class TempHome : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-paths-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempHome() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorPathsIsInstalledTests/*"`
Expected: FAIL — methods not found.

- [ ] **Step 3: Extend `CursorPaths`**

Append to `src/Kapacitor.Cli.Core/Cursor/CursorPaths.cs` (after the existing record):

```csharp
public sealed record CursorPaths(string UserDir, string WorkspaceStorageDir, string GlobalStateDb) {
    // ... existing members unchanged ...

    /// <summary>
    /// True when any of the OS-specific Cursor user dirs exists. Detection by
    /// directory presence — Cursor IDE users without the <c>cursor</c> shell
    /// command on PATH must still be detected (AI-730 design, Q7).
    /// </summary>
    public static bool IsInstalled(string? home = null, OsPlatform? platform = null, string? appData = null) {
        home     ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        platform ??= OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                  :  OperatingSystem.IsWindows() ? OsPlatform.Windows
                  :                                OsPlatform.Linux;

        // Universal: ~/.cursor/ (settings + hooks.json land here on every OS).
        if (Directory.Exists(Path.Combine(home, ".cursor"))) return true;

        // Per-OS Electron user dir.
        var perOs = platform switch {
            OsPlatform.MacOs   => Path.Combine(home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => Path.Combine(
                appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User"),
            _                  => Path.Combine(home, ".config", "Cursor", "User")
        };
        return Directory.Exists(perOs);
    }

    /// <summary>Path to <c>~/.cursor/hooks.json</c> — same on every OS.</summary>
    public static string UserHooksJson(string? home = null, OsPlatform? platform = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cursor", "hooks.json");
    }

    /// <summary>Hook-event spool directory at <c>~/.cursor/kapacitor-pending/</c>.</summary>
    public static string SpoolDir(string? home = null, OsPlatform? platform = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cursor", "kapacitor-pending");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorPathsIsInstalledTests/*"`
Expected: PASS, 7/7.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/Cursor/CursorPaths.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorPathsIsInstalledTests.cs
git commit -m "[AI-730] CursorPaths: add IsInstalled, UserHooksJson, SpoolDir

$(cat <<'EOF'
Detection by user-dir presence (~/.cursor/ universal + per-OS Electron User
dirs), not by PATH — Cursor IDE users without the cursor shell command must
still be detected. UserHooksJson and SpoolDir centralise the on-disk paths
the hook dispatcher and installer use.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `CursorHooksInstaller` — marker file helpers

**Files:**

* Create: `src/Kapacitor.Cli.Core/CursorHooksInstaller.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/CursorHooksInstallerTests.cs`

Direct mirror of `CodexHooksInstaller` from AI-734. Marker filename `.kapacitor-hooks-version` (same as Codex's by design — both live next to a `hooks.json`, in separate dirs).

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/CursorHooksInstallerTests.cs
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class CursorHooksInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "does-not-exist", "hooks.json");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, CursorHooksInstaller.MarkerFileName), "1.2.3");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_hooks_json_has_kapacitor_entry_but_no_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kapacitor hook --cursor"}]}}
            """);
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_has_only_third_party_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"/usr/local/bin/other"}]}}
            """);
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_is_malformed() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, "{not json");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        await Assert.That(CursorHooksInstaller.ReadMarker(hooksPath))
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task ReadMarker_returns_null_when_marker_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await Assert.That(CursorHooksInstaller.ReadMarker(hooksPath)).IsNull();
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        CursorHooksInstaller.DeleteMarker(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CursorHooksInstaller.MarkerFileName))).IsFalse();
        CursorHooksInstaller.DeleteMarker(hooksPath); // idempotent
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-hooks-installer-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksInstallerTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement `CursorHooksInstaller`**

```csharp
// src/Kapacitor.Cli.Core/CursorHooksInstaller.cs
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Marker + detection helpers for <c>~/.cursor/hooks.json</c>. Mirror of
/// <see cref="CodexHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class CursorHooksInstaller {
    public const string MarkerFileName = ".kapacitor-hooks-version";

    /// <summary>
    /// True when the user has previously installed Cursor hooks via setup or
    /// <c>kapacitor plugin install --cursor</c>. Marker file presence OR an
    /// existing <c>kapacitor hook --cursor</c> entry in hooks.json — the
    /// hooks-json fallback covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(hooksPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is not JsonArray arr) continue;
                foreach (var entry in arr) {
                    if (CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)) return true;
                }
            }
        } catch { /* Malformed → treat as not installed. */ }
        return false;
    }

    public static string? ReadMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), KapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksInstallerTests/*"`
Expected: PASS, 8/8.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/CursorHooksInstaller.cs test/Kapacitor.Cli.Tests.Unit/CursorHooksInstallerTests.cs
git commit -m "[AI-730] Add CursorHooksInstaller marker helpers

$(cat <<'EOF'
Mirror of CodexHooksInstaller from AI-734. Tracks the .kapacitor-hooks-version
marker next to ~/.cursor/hooks.json so the npm postinstall hook can refresh
Cursor hook command strings on CLI upgrade.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `HttpClientExtensions.PostOnceAsync` / `GetOnceAsync` — no-retry helpers

**Files:**

* Modify: `src/Kapacitor.Cli.Core/HttpClientExtensions.cs`
* Test: extend `test/Kapacitor.Cli.Tests.Unit/HttpClientExtensionsTests.cs` (or create if missing — verify with `ls test/Kapacitor.Cli.Tests.Unit/HttpClientExtensionsTests.cs`).

`PostWithRetryAsync`'s 30-second default is unsafe under the 2s hook budget. Add no-retry, short-timeout overloads that respect a linked `ct`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/HttpClientExtensionsPostOnceTests.cs
using System.Net;
using System.Text;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class HttpClientExtensionsPostOnceTests {
    [Test]
    public async Task PostOnceAsync_returns_response_on_success() {
        using var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1));

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task PostOnceAsync_does_not_retry_on_transient_failure() {
        var attempts = 0;
        using var handler = new StubHandler(req => {
            attempts++;
            throw new HttpRequestException("connect refused");
        });
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        await Assert.That(async () =>
            await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1))
        ).Throws<HttpRequestException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task PostOnceAsync_respects_caller_ct_cancellation() {
        using var cts     = new CancellationTokenSource();
        cts.Cancel();
        using var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        await Assert.That(async () =>
            await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1), cts.Token)
        ).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task PostOnceAsync_times_out_after_specified_duration() {
        using var handler = new StubHandler(async req => {
            await Task.Delay(2_000);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.That(async () =>
            await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromMilliseconds(100))
        ).Throws<OperationCanceledException>();
        sw.Stop();
        // Generous upper bound to avoid CI flakiness.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1_500);
    }

    sealed class StubHandler : HttpMessageHandler {
        readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(req => Task.FromResult(sync(req))) { }
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) => _impl = impl;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            var task = _impl(request);
            using var reg = ct.Register(() => { /* propagate */ });
            return await task.WaitAsync(ct);
        }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HttpClientExtensionsPostOnceTests/*"`
Expected: FAIL — `PostOnceAsync` not defined.

- [ ] **Step 3: Add `PostOnceAsync` and `GetOnceAsync` to the `extension(HttpClient client)` block in `HttpClientExtensions.cs`**

Edit the existing `extension(HttpClient client) { ... }` block (around line 112-142) to add:

```csharp
extension(HttpClient client) {
    // ... existing PostWithRetryAsync / GetWithRetryAsync / PutWithRetryAsync / DeleteWithRetryAsync unchanged ...

    /// <summary>
    /// Single-attempt POST with a hard per-call timeout. No retry, no
    /// backoff. Used by hook-path call sites where retries would burst
    /// the shared dispatcher budget. <paramref name="ct"/> is honoured;
    /// expiry of <paramref name="timeout"/> surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<HttpResponseMessage> PostOnceAsync(
            string            url,
            HttpContent       content,
            TimeSpan          timeout,
            CancellationToken ct = default
        ) {
        EnsureAbsolute(url);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await client.PostAsync(url, content, linkedCts.Token);
    }

    /// <summary>Single-attempt GET — see <see cref="PostOnceAsync"/>.</summary>
    public async Task<HttpResponseMessage> GetOnceAsync(
            string            url,
            TimeSpan          timeout,
            CancellationToken ct = default
        ) {
        EnsureAbsolute(url);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await client.GetAsync(url, linkedCts.Token);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HttpClientExtensionsPostOnceTests/*"`
Expected: PASS, 4/4.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/HttpClientExtensions.cs test/Kapacitor.Cli.Tests.Unit/HttpClientExtensionsPostOnceTests.cs
git commit -m "[AI-730] HttpClientExtensions: add PostOnceAsync, GetOnceAsync

$(cat <<'EOF'
No-retry single-attempt overloads with a hard per-call timeout. The retry
wrapper's 30s default is unsafe under the 2s hook-dispatcher budget; under
server idempotency the next hook invocation is a free retry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: `CursorHookEventMap` — event-name → URL segment + telemetry classification

**Files:**

* Create: `src/Kapacitor.Cli/Commands/CursorHookEventMap.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookEventMapTests.cs`

Centralises Cursor's camelCase → kebab-case mapping and the canonical-event-vs-telemetry classification. Keeping it as a separate type makes the dispatcher trivially testable.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookEventMapTests.cs
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHookEventMapTests {
    [Test]
    [Arguments("sessionStart",        "session-start/cursor",         true)]
    [Arguments("sessionEnd",          "session-end/cursor",           true)]
    [Arguments("beforeSubmitPrompt",  "user-prompt/cursor",           true)]
    [Arguments("afterAgentThought",   "agent-thought/cursor",         true)]
    [Arguments("afterAgentResponse",  "agent-response/cursor",        false)]
    [Arguments("preToolUse",          "pre-tool-use/cursor",          false)]
    [Arguments("postToolUse",         "post-tool-use/cursor",         false)]
    [Arguments("postToolUseFailure",  "post-tool-use-failure/cursor", false)]
    public async Task TryResolve_known_events_map_correctly(string evt, string expectedSegment, bool expectedCanonical) {
        await Assert.That(CursorHookEventMap.TryResolve(evt, out var mapping)).IsTrue();
        await Assert.That(mapping.RouteSegment).IsEqualTo(expectedSegment);
        await Assert.That(mapping.SpoolOnFailure).IsEqualTo(expectedCanonical);
    }

    [Test]
    public async Task TryResolve_unknown_event_returns_false() {
        await Assert.That(CursorHookEventMap.TryResolve("madeUpEvent", out _)).IsFalse();
    }

    [Test]
    public async Task TryResolve_empty_or_null_event_returns_false() {
        await Assert.That(CursorHookEventMap.TryResolve("", out _)).IsFalse();
        await Assert.That(CursorHookEventMap.TryResolve(null!, out _)).IsFalse();
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookEventMapTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement `CursorHookEventMap`**

```csharp
// src/Kapacitor.Cli/Commands/CursorHookEventMap.cs
namespace Kapacitor.Cli.Commands;

/// <summary>
/// Maps a Cursor <c>hook_event_name</c> (camelCase) to its server route
/// segment (kebab-case) and flags whether its POST failure should land
/// in the per-session spool (canonical-event-bearing) or be dropped
/// (telemetry-only).
/// </summary>
public static class CursorHookEventMap {
    public readonly record struct Mapping(string RouteSegment, bool SpoolOnFailure);

    // SpoolOnFailure = true for the four canonical-event-bearing hooks
    // (sessionStart, sessionEnd, beforeSubmitPrompt, afterAgentThought).
    // The other four are telemetry-only — failures are accepted lossy.
    static readonly Dictionary<string, Mapping> Map = new(StringComparer.Ordinal) {
        ["sessionStart"]        = new("session-start/cursor",         SpoolOnFailure: true),
        ["sessionEnd"]          = new("session-end/cursor",           SpoolOnFailure: true),
        ["beforeSubmitPrompt"]  = new("user-prompt/cursor",           SpoolOnFailure: true),
        ["afterAgentThought"]   = new("agent-thought/cursor",         SpoolOnFailure: true),
        ["afterAgentResponse"]  = new("agent-response/cursor",        SpoolOnFailure: false),
        ["preToolUse"]          = new("pre-tool-use/cursor",          SpoolOnFailure: false),
        ["postToolUse"]         = new("post-tool-use/cursor",         SpoolOnFailure: false),
        ["postToolUseFailure"]  = new("post-tool-use-failure/cursor", SpoolOnFailure: false),
    };

    public static bool TryResolve(string? eventName, out Mapping mapping) {
        if (string.IsNullOrEmpty(eventName)) { mapping = default; return false; }
        return Map.TryGetValue(eventName, out mapping);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookEventMapTests/*"`
Expected: PASS, 10/10 (8 arguments + 2 fall-through).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/CursorHookEventMap.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookEventMapTests.cs
git commit -m "[AI-730] Add CursorHookEventMap

$(cat <<'EOF'
Centralises camelCase->kebab-case event-name mapping and the canonical-event
vs telemetry classification used by both the dispatcher and the spool.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `CursorHookSpool` — per-session JSONL spool for failed canonical-event hooks

**Files:**

* Create: `src/Kapacitor.Cli/Commands/CursorHookSpool.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs`

Per-session file at `~/.cursor/kapacitor-pending/<dashless-sid>.jsonl`. Append on POST failure (eligible events only). Drain FIFO at the top of every invocation. Cap 1 MB per file. Cleanup on successful `sessionEnd`. 30-day reaper.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHookSpoolTests {
    [Test]
    public async Task Append_creates_file_and_writes_one_line_per_call() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);

        spool.Append("abc123", "sessionStart", """{"hook_event_name":"sessionStart","session_id":"abc123"}""");
        spool.Append("abc123", "sessionEnd",   """{"hook_event_name":"sessionEnd","session_id":"abc123"}""");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc123.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("sessionStart");
        await Assert.That(lines[1]).Contains("sessionEnd");
    }

    [Test]
    public async Task Drain_yields_entries_in_FIFO_order() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionStart", """{"k":"a"}""");
        spool.Append("abc", "sessionEnd",   """{"k":"b"}""");

        var seen = new List<(string Event, string Body)>();
        await foreach (var entry in spool.DrainAsync("abc", CancellationToken.None)) {
            seen.Add((entry.EventName, entry.Body));
            await entry.MarkDeliveredAsync();
        }

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Event).IsEqualTo("sessionStart");
        await Assert.That(seen[1].Event).IsEqualTo("sessionEnd");
        // File deleted (empty) after all entries marked delivered.
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "abc.jsonl"))).IsFalse();
    }

    [Test]
    public async Task Drain_stops_on_first_undelivered_and_preserves_remaining() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionStart", """{"k":"a"}""");
        spool.Append("abc", "sessionEnd",   """{"k":"b"}""");

        await foreach (var entry in spool.DrainAsync("abc", CancellationToken.None)) {
            if (entry.EventName == "sessionStart") {
                await entry.MarkDeliveredAsync();
            } else {
                break; // simulate POST failure — leave for next time
            }
        }

        var remaining = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc.jsonl"));
        await Assert.That(remaining.Length).IsEqualTo(1);
        await Assert.That(remaining[0]).Contains("sessionEnd");
    }

    [Test]
    public async Task Append_evicts_oldest_when_over_one_MB() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path, capBytes: 4_096);
        var big = new string('x', 1_500);
        spool.Append("abc", "afterAgentThought", $"\"{big}-first\"");
        spool.Append("abc", "afterAgentThought", $"\"{big}-second\"");
        spool.Append("abc", "afterAgentThought", $"\"{big}-third\"");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc.jsonl"));
        await Assert.That(lines.Length).IsLessThanOrEqualTo(3);
        // The oldest line must have been evicted.
        await Assert.That(lines.Any(l => l.Contains("-first"))).IsFalse();
        await Assert.That(lines.Last()).Contains("-third");
    }

    [Test]
    public async Task DeleteSession_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionEnd", """{"k":"x"}""");

        spool.DeleteSession("abc");
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "abc.jsonl"))).IsFalse();
        spool.DeleteSession("abc"); // idempotent
    }

    [Test]
    public async Task ReapOlderThan_deletes_old_files_only() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("oldsession", "sessionEnd", """{"k":"o"}""");
        spool.Append("newsession", "sessionEnd", """{"k":"n"}""");

        var oldFile = Path.Combine(tmp.Path, "oldsession.jsonl");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31));

        spool.ReapOlderThan(TimeSpan.FromDays(30));

        await Assert.That(File.Exists(oldFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "newsession.jsonl"))).IsTrue();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-spool-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookSpoolTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement `CursorHookSpool`**

```csharp
// src/Kapacitor.Cli/Commands/CursorHookSpool.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Per-session JSONL spool for canonical-event hooks whose POST failed.
/// File layout: <c>{spoolDir}/&lt;dashless-sid&gt;.jsonl</c>, one
/// <c>{"hook_event_name": "...", "body": &lt;raw payload string&gt;}</c>
/// object per line, in arrival order. Reads/writes are unsynchronised —
/// only one <c>kapacitor hook --cursor</c> per session typically runs at
/// any time. Concurrent invocations may interleave; the worst case is a
/// duplicated line, which the server's idempotency keys deduplicate.
/// </summary>
public sealed class CursorHookSpool {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    readonly string _spoolDir;
    readonly int    _capBytes;

    public CursorHookSpool(string spoolDir, int capBytes = DefaultCapBytes) {
        _spoolDir = spoolDir;
        _capBytes = capBytes;
    }

    string PathFor(string sessionId) => Path.Combine(_spoolDir, $"{sessionId}.jsonl");

    public void Append(string sessionId, string eventName, string rawPayloadJson) {
        try {
            Directory.CreateDirectory(_spoolDir);
            var line = new JsonObject {
                ["hook_event_name"] = eventName,
                ["body"]            = rawPayloadJson
            }.ToJsonString();

            var path = PathFor(sessionId);
            // Evict oldest entries until appending stays under cap.
            EnsureUnderCap(path, line.Length + 1);
            File.AppendAllText(path, line + "\n");
        } catch { /* best effort — losing one event here is acceptable */ }
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;
            var size = new FileInfo(path).Length;
            if (size + incomingBytes <= _capBytes) return;

            // Drop oldest lines until size + incoming <= cap.
            var lines = File.ReadAllLines(path).ToList();
            while (lines.Count > 0 && lines.Sum(l => l.Length + 1) + incomingBytes > _capBytes) {
                lines.RemoveAt(0);
            }
            File.WriteAllLines(path, lines);
        } catch { }
    }

    public readonly struct Entry {
        public string EventName { get; init; }
        public string Body      { get; init; }
        internal int  Index     { get; init; }
        internal Func<Task> Deliver { get; init; }

        public Task MarkDeliveredAsync() => Deliver();
    }

    /// <summary>
    /// FIFO drain. Yields one entry per call. Caller MUST invoke
    /// <see cref="Entry.MarkDeliveredAsync"/> after a successful POST.
    /// Stop iterating to leave the rest of the queue for next time.
    /// </summary>
    public async IAsyncEnumerable<Entry> DrainAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken ct
        ) {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) yield break;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { yield break; }

        // Track how many leading lines have been delivered; rewrite the file
        // each time we shorten the queue. The simple-file approach trades
        // throughput for crash safety: a power loss between deliveries leaves
        // the queue intact, just slightly longer than needed.
        var delivered = 0;
        for (var i = 0; i < lines.Length; i++) {
            ct.ThrowIfCancellationRequested();

            var line = lines[i];
            string? eventName;
            string? body;
            try {
                var node = JsonNode.Parse(line);
                eventName = node?["hook_event_name"]?.GetValue<string>();
                body      = node?["body"]?.GetValue<string>();
            } catch { eventName = null; body = null; }

            if (eventName is null || body is null) {
                // Skip corrupt line, count as delivered so we don't loop on it.
                delivered = i + 1;
                continue;
            }

            var capturedDelivered = delivered;
            yield return new Entry {
                EventName = eventName,
                Body      = body,
                Index     = i,
                Deliver   = () => {
                    delivered = capturedDelivered + 1;
                    return WriteRemainingAsync(path, lines, delivered);
                }
            };
        }

        if (delivered == lines.Length) {
            try { File.Delete(path); } catch { }
        }
    }

    static async Task WriteRemainingAsync(string path, string[] lines, int delivered) {
        try {
            if (delivered >= lines.Length) {
                File.Delete(path);
                return;
            }
            await File.WriteAllLinesAsync(path, lines.Skip(delivered));
        } catch { }
    }

    public void DeleteSession(string sessionId) {
        var path = PathFor(sessionId);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void ReapOlderThan(TimeSpan age) {
        try {
            if (!Directory.Exists(_spoolDir)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var file in Directory.EnumerateFiles(_spoolDir, "*.jsonl")) {
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                } catch { }
            }
        } catch { }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookSpoolTests/*"`
Expected: PASS, 6/6.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/CursorHookSpool.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs
git commit -m "[AI-730] Add CursorHookSpool

$(cat <<'EOF'
Per-session JSONL spool for failed canonical-event hooks (sessionStart,
sessionEnd, beforeSubmitPrompt, afterAgentThought). FIFO drain at next
invocation; 1 MB cap with oldest-first eviction; 30-day reaper.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: `CursorTranscriptBackfill` — watermark resume + line POST loop

**Files:**

* Create: `src/Kapacitor.Cli/Commands/CursorTranscriptBackfill.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorTranscriptBackfillTests.cs`

GET watermark; resume from `last_line_number + 1`; POST one JSONL line at a time until EOF, budget expiry, or POST failure. Each call uses `PostOnceAsync` / `GetOnceAsync` with the shared `ct` linked to the dispatcher budget.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorTranscriptBackfillTests.cs
using System.Net;
using System.Text;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorTranscriptBackfillTests {
    [Test]
    public async Task RunAsync_returns_zero_when_transcript_path_null() {
        using var tmp = new TempDir();
        var sent = new List<string>();
        using var handler = new RecordingHandler(sent);
        using var client  = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: null, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(sent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_resumes_from_last_line_number_plus_one() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "line0", "line1", "line2", "line3" });

        var posted = new List<(int Index, string Line)>();
        using var handler = new RecordingHandler(
            getResponse: req => req.RequestUri!.AbsolutePath.EndsWith("/transcript-watermark")
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"last_line_number":1}""")
                }
                : null,
            postCapture: (req, body) => {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body)!;
                posted.Add(((int)node["line_index"]!.GetValue<int>(), node["line"]!.GetValue<string>()));
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(posted[0]).IsEqualTo((2, "line2"));
        await Assert.That(posted[1]).IsEqualTo((3, "line3"));
    }

    [Test]
    public async Task RunAsync_stops_when_budget_expires() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, Enumerable.Range(0, 50).Select(i => $"line{i}"));

        var posted = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                posted++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        // Budget fires after 3 lines.
        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript,
            budget: () => posted >= 3,
            CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(3);
    }

    [Test]
    public async Task RunAsync_stops_on_first_POST_failure() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "a", "b", "c" });

        var posted = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                posted++;
                return new HttpResponseMessage(posted < 2 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(1);
        await Assert.That(stats.Failed).IsTrue();
    }

    [Test]
    public async Task RunAsync_fails_open_on_watermark_GET_failure() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "a", "b" });

        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        // Watermark unavailable -> no backfill attempted; return cleanly.
        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsTrue();
    }

    sealed class RecordingHandler : HttpMessageHandler {
        readonly Func<HttpRequestMessage, HttpResponseMessage?>? _get;
        readonly Func<HttpRequestMessage, string, HttpResponseMessage>? _post;
        public List<string> Sent { get; }

        public RecordingHandler(List<string> sent) : this(sent, null, null) { }
        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage?>? getResponse,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture)
            : this(new(), getResponse, postCapture) { }
        RecordingHandler(
            List<string> sent,
            Func<HttpRequestMessage, HttpResponseMessage?>? getResponse,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture) {
            Sent  = sent;
            _get  = getResponse;
            _post = postCapture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get) {
                return _get?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Sent.Add(body);
            return _post?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-backfill-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorTranscriptBackfillTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement `CursorTranscriptBackfill`**

```csharp
// src/Kapacitor.Cli/Commands/CursorTranscriptBackfill.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// One-shot transcript-line backfill. Reads the watermark for
/// <paramref name="sessionId"/>, opens the transcript JSONL, and POSTs
/// each line whose index is past <c>last_line_number</c> until the
/// dispatcher budget expires (signalled via <paramref name="budget"/>) or
/// the transcript is fully drained or a POST fails. No internal retry —
/// the next hook invocation re-reads the (advanced) watermark.
/// </summary>
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan LinePostTimeout  = TimeSpan.FromSeconds(1);

    public readonly record struct Stats(int LinesPosted, bool Failed);

    public static async Task<Stats> RunAsync(
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            CancellationToken ct
        ) {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) {
            return new Stats(0, false);
        }

        // Watermark lookup. Fail-open: any failure (network, non-2xx, parse)
        // returns Stats(0, Failed: true) so caller can log but never blocks
        // Cursor.
        int resumeFrom;
        try {
            using var resp = await client.GetOnceAsync(
                $"{baseUrl}/api/cursor-sessions/{sessionId}/transcript-watermark",
                WatermarkTimeout, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) {
                resumeFrom = 0;
            } else if (!resp.IsSuccessStatusCode) {
                return new Stats(0, Failed: true);
            } else {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                resumeFrom = doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                    ? ln.GetInt32() + 1
                    : 0;
            }
        } catch { return new Stats(0, Failed: true); }

        var posted = 0;

        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lineIndex = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null) {
            if (lineIndex < resumeFrom) { lineIndex++; continue; }
            if (budget()) return new Stats(posted, Failed: false);
            ct.ThrowIfCancellationRequested();

            var payload = new JsonObject {
                ["session_id"] = sessionId,
                ["line_index"] = lineIndex,
                ["line"]       = line
            }.ToJsonString();

            HttpResponseMessage? resp = null;
            try {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                resp = await client.PostOnceAsync(
                    $"{baseUrl}/hooks/transcript-line/cursor", content, LinePostTimeout, ct);
                if (!resp.IsSuccessStatusCode) return new Stats(posted, Failed: true);
                posted++;
            } catch { return new Stats(posted, Failed: true); }
            finally { resp?.Dispose(); }

            lineIndex++;
        }

        return new Stats(posted, Failed: false);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorTranscriptBackfillTests/*"`
Expected: PASS, 5/5.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/CursorTranscriptBackfill.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorTranscriptBackfillTests.cs
git commit -m "[AI-730] Add CursorTranscriptBackfill

$(cat <<'EOF'
Watermark-GET-then-line-POST loop bounded by the dispatcher budget. Uses
GetOnceAsync / PostOnceAsync with 500ms / 1s per-call timeouts. No retries
— next hook invocation resumes from the advanced server watermark.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: `CursorHookCommand` — the dispatcher

**Files:**

* Create: `src/Kapacitor.Cli/Commands/CursorHookCommand.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs`

Orchestrates: parse stdin → normalize → disabled-session check → spool drain → POST current event → transcript backfill. All bound to a 2s `Stopwatch`-armed CTS.

The dispatcher is structured so the unit tests can drive it with an injected `HttpClient` and `CursorHookSpool`. A thin `Handle(string baseUrl, TextReader stdin)` entry point — invoked from `Program.cs` — wires up the production seams (`HttpClientExtensions.CreateAuthenticatedClientAsync`, the real spool dir).

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHookCommandTests {
    [Test]
    public async Task malformed_stdin_returns_zero() {
        using var fx = new Fixture();
        var exit = await fx.HandleAsync("not a json payload");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task missing_hook_event_name_returns_zero() {
        using var fx = new Fixture();
        var exit = await fx.HandleAsync("""{"session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task session_id_is_normalised_dashless_in_outgoing_payload() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"8c3276c2-c8f7-43ce-9889-8c2becf5240a"}""");
        var sent = fx.SentToHook("session-start/cursor");
        await Assert.That(JsonNode.Parse(sent)!["session_id"]!.GetValue<string>())
            .IsEqualTo("8c3276c2c8f743ce98898c2becf5240a");
    }

    [Test]
    public async Task home_dir_and_agent_host_id_are_injected() {
        Environment.SetEnvironmentVariable("KAPACITOR_AGENT_ID", "host-42");
        try {
            using var fx = new Fixture();
            await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
            var sent = fx.SentToHook("session-start/cursor");
            var node = JsonNode.Parse(sent)!;
            await Assert.That(node["home_dir"]?.GetValue<string>()).IsNotNull();
            await Assert.That(node["agent_host_id"]?.GetValue<string>()).IsEqualTo("host-42");
        } finally {
            Environment.SetEnvironmentVariable("KAPACITOR_AGENT_ID", null);
        }
    }

    [Test]
    public async Task disabled_session_suppresses_POST() {
        var sid = Guid.NewGuid().ToString("N");
        DisabledSessions.Mark(sid);
        try {
            using var fx = new Fixture();
            await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");
            await Assert.That(fx.Sent).IsEmpty();
        } finally {
            DisabledSessions.RemoveMarker(sid);
        }
    }

    [Test]
    public async Task telemetry_events_post_but_do_not_spool_on_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync("""{"hook_event_name":"preToolUse","session_id":"abc","tool_name":"Glob"}""");
        await Assert.That(fx.Spool.Files).IsEmpty();
    }

    [Test]
    public async Task canonical_events_spool_on_POST_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync("""{"hook_event_name":"sessionEnd","session_id":"abc"}""");
        await Assert.That(fx.Spool.Files.Single()).EndsWith("abc.jsonl");
    }

    [Test]
    public async Task spool_drain_runs_before_current_event_under_budget() {
        using var fx = new Fixture();
        fx.Spool.Append("abc", "sessionStart", """{"hook_event_name":"sessionStart","session_id":"abc"}""");
        await fx.HandleAsync("""{"hook_event_name":"sessionEnd","session_id":"abc"}""");
        // Drained sessionStart first, then current sessionEnd.
        await Assert.That(fx.RouteOrder).IsEquivalentTo(new[] { "session-start/cursor", "session-end/cursor" });
    }

    [Test]
    public async Task afterAgentThought_canonical_id_is_stable_across_replays() {
        // Same generation_id + text => same canonical event ID. Test by
        // capturing two consecutive identical-content POSTs and verifying
        // an explicit Idempotency-Key or body hash field is constant.
        using var fx = new Fixture();
        var body = """{"hook_event_name":"afterAgentThought","session_id":"abc","generation_id":"gen1","text":"hello"}""";
        await fx.HandleAsync(body);
        await fx.HandleAsync(body);
        var sent = fx.SentToHook("agent-thought/cursor");
        // Two POSTs, both must carry the same canonical-event ID we stamp.
        var ids = fx.AllSentTo("agent-thought/cursor")
            .Select(b => JsonNode.Parse(b)!["canonical_event_id"]!.GetValue<string>())
            .Distinct()
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task null_transcript_path_does_not_trigger_backfill() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc","transcript_path":null}""");
        await Assert.That(fx.AllSentTo("transcript-line/cursor")).IsEmpty();
    }

    [Test]
    public async Task hook_without_vendor_flag_exits_nonzero() {
        // This is exercised in Program.cs (Task 9); placeholder here to
        // remind the engineer that CursorHookCommand itself does not check
        // for the vendor flag — that is Program.cs's job.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Test seam: drives CursorHookCommand.HandleCore against an in-memory
    /// HttpMessageHandler and a fresh CursorHookSpool rooted at a TempDir.
    /// </summary>
    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(
            Path.GetTempPath(), $"kapacitor-cursor-hook-test-{Guid.NewGuid().ToString("N")[..8]}");

        public List<string> Sent       { get; } = new();
        public List<string> RouteOrder { get; } = new();
        public CursorHookSpool Spool   { get; }
        readonly HttpClient _client;

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            Spool = new CursorHookSpool(Path.Combine(_tmpHome, "spool"));
            var handler = new StubHandler(async req => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                Sent.Add($"{req.RequestUri!.AbsolutePath}|{body}");
                RouteOrder.Add(req.RequestUri.AbsolutePath.Replace("/hooks/", ""));
                return new HttpResponseMessage(postStatus);
            });
            _client = new HttpClient(handler);
        }

        public Task<int> HandleAsync(string stdin) =>
            CursorHookCommand.HandleCore(
                _client, baseUrl: "http://localhost", stdin: new StringReader(stdin),
                spool: Spool, budgetTotal: TimeSpan.FromSeconds(2));

        public string SentToHook(string segment) =>
            Sent.First(s => s.StartsWith($"/hooks/{segment}")).Split('|', 2)[1];

        public IEnumerable<string> AllSentTo(string segment) =>
            Sent.Where(s => s.StartsWith($"/hooks/{segment}")).Select(s => s.Split('|', 2)[1]);

        public void Dispose() {
            _client.Dispose();
            try { Directory.Delete(_tmpHome, true); } catch { }
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookCommandTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement `CursorHookCommand`**

```csharp
// src/Kapacitor.Cli/Commands/CursorHookCommand.cs
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Cursor hooks. Cursor invokes the same
/// command for every hook event with <c>hook_event_name</c> in the JSON
/// payload, so we collapse the 8 event handlers behind one CLI entry
/// point. Mirrors <see cref="CodexHookCommand"/>'s shape but adds a
/// shared 2-second wall-clock budget, a per-session canonical-event
/// spool, and a watermark-driven transcript-line backfill.
/// </summary>
public static class CursorHookCommand {
    static readonly TimeSpan DispatcherBudget = TimeSpan.FromSeconds(2);
    static readonly TimeSpan HookPostTimeout  = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Production entry point. Resolves the home-dir spool and
    /// authenticated <see cref="HttpClient"/>, then delegates to
    /// <see cref="HandleCore"/>.
    /// </summary>
    public static async Task<int> Handle(string baseUrl, TextReader stdin) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var spool = new CursorHookSpool(CursorPaths.SpoolDir());
        // 30-day reap on every invocation. Cheap when the spool dir is empty.
        spool.ReapOlderThan(TimeSpan.FromDays(30));
        return await HandleCore(client, baseUrl, stdin, spool, DispatcherBudget);
    }

    /// <summary>
    /// Test-friendly core. Caller owns the <see cref="HttpClient"/> and
    /// <see cref="CursorHookSpool"/>.
    /// </summary>
    public static async Task<int> HandleCore(
            HttpClient        client,
            string            baseUrl,
            TextReader        stdin,
            CursorHookSpool   spool,
            TimeSpan          budgetTotal
        ) {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(budgetTotal);
        var ct = cts.Token;
        bool BudgetExpired() => sw.Elapsed >= budgetTotal;

        // Parse stdin defensively.
        var body = await stdin.ReadToEndAsync(ct);
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { return 0; }
        if (node is null) return 0;

        var eventName = node["hook_event_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(eventName)) return 0;
        if (!CursorHookEventMap.TryResolve(eventName, out var mapping)) return 0;

        // Normalize session_id and inject standard fields.
        NormalizeGuidField(node, "session_id");
        node["home_dir"] = PathHelpers.HomeDirectory;
        var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");
        if (agentHostId is not null) node["agent_host_id"] = agentHostId;

        // Stable canonical event ID for content-bearing afterAgentThought.
        if (eventName == "afterAgentThought") {
            var sid = node["session_id"]?.GetValue<string>() ?? "";
            var gen = node["generation_id"]?.GetValue<string>() ?? "";
            var txt = node["text"]?.GetValue<string>() ?? "";
            node["canonical_event_id"] = StableThoughtId(sid, gen, txt);
        }

        var sessionId = node["session_id"]?.GetValue<string>();

        // Disabled session: skip POST and skip spool.
        if (sessionId is not null && DisabledSessions.IsDisabled(sessionId)) return 0;

        var normalized = node.ToJsonString();

        // Drain the per-session spool first.
        if (sessionId is not null) {
            await foreach (var entry in spool.DrainAsync(sessionId, ct)) {
                if (BudgetExpired()) return 0;
                if (!CursorHookEventMap.TryResolve(entry.EventName, out var entryMapping)) {
                    await entry.MarkDeliveredAsync();
                    continue;
                }
                var ok = await TryPostHookAsync(client, baseUrl, entryMapping.RouteSegment, entry.Body, ct);
                if (!ok) break; // Leave the rest for next time.
                await entry.MarkDeliveredAsync();
            }
        }

        // POST the current event under the remaining budget.
        if (BudgetExpired()) return 0;

        var posted = await TryPostHookAsync(client, baseUrl, mapping.RouteSegment, normalized, ct);
        if (!posted && mapping.SpoolOnFailure && sessionId is not null) {
            spool.Append(sessionId, eventName, normalized);
        }

        // Successful sessionEnd cleans up the spool.
        if (posted && eventName == "sessionEnd" && sessionId is not null) {
            spool.DeleteSession(sessionId);
        }

        // Transcript backfill: only when the payload includes a non-null path.
        if (!BudgetExpired() && sessionId is not null) {
            var transcriptPath = node["transcript_path"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(transcriptPath)) {
                await CursorTranscriptBackfill.RunAsync(
                    client, baseUrl, sessionId, transcriptPath,
                    budget: BudgetExpired,
                    ct);
            }
        }

        return 0;
    }

    static async Task<bool> TryPostHookAsync(
            HttpClient client,
            string     baseUrl,
            string     routeSegment,
            string     bodyJson,
            CancellationToken ct
        ) {
        try {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var resp = await client.PostOnceAsync(
                $"{baseUrl}/hooks/{routeSegment}", content, HookPostTimeout, ct);
            return resp.IsSuccessStatusCode;
        } catch { return false; }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = node[fieldName]?.GetValue<string>();
        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    static string StableThoughtId(string sessionId, string generationId, string text) {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var hash16 = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return $"{sessionId}:reasoning:{generationId}:{hash16}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookCommandTests/*"`
Expected: PASS, 10/10 (one is a placeholder reminder).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/CursorHookCommand.cs test/Kapacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs
git commit -m "[AI-730] Add CursorHookCommand dispatcher

$(cat <<'EOF'
Reads stdin JSON, normalises session_id, injects home_dir / agent_host_id,
honours DisabledSessions, drains the per-session spool, POSTs the current
event, and runs a watermark-driven transcript backfill — all bounded by a
2s wall-clock budget. afterAgentThought gets a stable (session, gen, text)
canonical event ID. Telemetry-only hooks (afterAgentResponse, preToolUse,
postToolUse, postToolUseFailure) never spool.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: `Program.cs` — add `hook` command with vendor flag

**Files:**

* Modify: `src/Kapacitor.Cli/Program.cs`
* Test: Integration smoke via existing `Program` exit-code coverage; unit-level test of the dispatch branch added in this task.

The new `hook` case dispatches to `CursorHookCommand.Handle` when `--cursor` is present. Without any vendor flag, it prints a usage line and returns 1.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/Cursor/HookCommandDispatchTests.cs
// (a Program-level smoke test — the simplest shape is to invoke the
// kapacitor binary as a subprocess. If the test project already prefers
// in-process invocation, follow that pattern; otherwise:)
using System.Diagnostics;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class HookCommandDispatchTests {
    [Test]
    public async Task hook_without_vendor_flag_prints_usage_and_returns_one() {
        var binary = FindKapacitorBinary();
        var psi = new ProcessStartInfo(binary, "hook") {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await Assert.That(proc.ExitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("vendor flag");
    }

    static string FindKapacitorBinary() {
        // Search relative to the test bin dir for the published kapacitor.
        // Fall back to dotnet run if the binary isn't built yet.
        // ... implementer chooses the project's existing convention.
        var probe = Path.Combine(AppContext.BaseDirectory, "../../../../../src/Kapacitor.Cli/bin/Debug/net10.0/kapacitor");
        return File.Exists(probe) ? probe : throw new FileNotFoundException(probe);
    }
}
```

If the project doesn't have a Program-subprocess test convention, instead skip this dedicated test and rely on the existing `CursorHookCommandTests` plus a manual smoke (Task 17). The Program.cs change itself is small enough that the type-checker covers it.

- [ ] **Step 2: Add the `hook` case to `Program.cs`**

Insert after the `case "codex-hook":` block (around line 602):

```csharp
case "hook": {
    if (args.Contains("--cursor")) {
        return await CursorHookCommand.Handle(baseUrl!, Console.In);
    }
    // Future: --codex, --claude routes here under AI-732 / AI-733.
    Console.Error.WriteLine("kapacitor hook requires a vendor flag (e.g. --cursor)");
    return 1;
}
```

Also add `"hook"` to the `offlineCommands` array if there's any case where the dispatcher should run without a configured server — **do not**. Hooks need `baseUrl`, so let the existing `baseUrl is null` guard handle the no-server case. The dispatcher will silently fail-open if the server is unreachable, which matches the design.

- [ ] **Step 3: Run unit tests including the Cursor suite to verify regressions are zero**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/Cursor/*"`
Expected: PASS (all Cursor-related tests).

- [ ] **Step 4: Commit**

```bash
git add src/Kapacitor.Cli/Program.cs test/Kapacitor.Cli.Tests.Unit/Cursor/HookCommandDispatchTests.cs 2>/dev/null || true
git commit -m "[AI-730] Program.cs: add vendor-agnostic hook command

$(cat <<'EOF'
kapacitor hook --cursor dispatches to CursorHookCommand.Handle. Without a
vendor flag, prints a usage message and exits 1. --codex and --claude will
join here under AI-732 / AI-733.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: `PluginCommand` — `--cursor` install/remove + `InstallCursorHooks` writer

**Files:**

* Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/PluginCommandCursorTests.cs`
* Test: `test/Kapacitor.Cli.Tests.Unit/CursorHooksWriterTests.cs`

Mirror `InstallCodex` / `RemoveCodex` / `InstallCodexHooks` / `RemoveCodexHooks` exactly, but for the Cursor hooks.json schema. Cursor's schema has `{"version":1, "hooks": {"<eventName>": [{"command": "..."}]}}` — flat command objects, no nested `"hooks"` array, no per-event timeout (Cursor's hook timeout is not configurable in hooks.json).

The `--cursor` install path also runs a **PATH precheck**: if `kapacitor` is not resolvable on PATH, surface a setup error rather than write a config that will silently fail. Use the existing `AgentDetector.IsInstalled("kapacitor")`.

- [ ] **Step 1: Write the failing writer test**

```csharp
// test/Kapacitor.Cli.Tests.Unit/CursorHooksWriterTests.cs
using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit;

public class CursorHooksWriterTests {
    [Test]
    public async Task fresh_install_writes_all_eight_events() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(ok).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        foreach (var evt in new[] {
            "sessionStart", "sessionEnd", "beforeSubmitPrompt",
            "afterAgentResponse", "afterAgentThought",
            "preToolUse", "postToolUse", "postToolUseFailure"
        }) {
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsGreaterThanOrEqualTo(1);
            var cmd = entries[0]!["command"]!.GetValue<string>();
            await Assert.That(cmd).IsEqualTo("kapacitor hook --cursor");
        }
    }

    [Test]
    public async Task install_preserves_user_authored_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"/usr/local/bin/other"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var start = root["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "/usr/local/bin/other")).IsTrue();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "kapacitor hook --cursor")).IsTrue();
    }

    [Test]
    public async Task install_replaces_existing_kapacitor_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kapacitor hook --cursor --legacy"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var start = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!
            .AsObject()["hooks"]!["sessionStart"]!.AsArray();
        // Only the current entry remains.
        await Assert.That(start.Count(e => e!["command"]!.GetValue<string>().Contains("kapacitor hook --cursor")))
            .IsEqualTo(1);
        await Assert.That(start.Single(e => e!["command"]!.GetValue<string>().Contains("kapacitor hook --cursor"))!["command"]!.GetValue<string>())
            .IsEqualTo("kapacitor hook --cursor");
    }

    [Test]
    public async Task install_stamps_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kapacitor-hooks-version"))).IsTrue();
    }

    [Test]
    public async Task remove_strips_kapacitor_entries_and_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        var removed = PluginCommand.RemoveCursorHooks(hooksPath);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kapacitor-hooks-version"))).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-writer-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Write the failing plugin-command test (`--if-installed` short-circuit + PATH precheck)**

```csharp
// test/Kapacitor.Cli.Tests.Unit/PluginCommandCursorTests.cs
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class PluginCommandCursorTests {
    [Test]
    public async Task install_cursor_if_installed_noops_when_marker_absent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        // No marker, no existing hooks.json — IsInstalled returns false.

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(hooksPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_if_installed_short_circuits_on_same_version_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        File.WriteAllText(hooksPath, "{}");

        var marker = CursorHooksInstaller.ReadMarker(hooksPath);
        await Assert.That(marker).IsEqualTo(KapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        // File untouched.
        await Assert.That(File.ReadAllText(hooksPath)).IsEqualTo("{}");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-pluginc-cursor-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

NOTE on `--cursor-hooks-path`: the existing `PluginCommandCodexTests` resolves the hooks path via `CodexPaths.UserHooksJson`. To keep tests hermetic for Cursor, **add a hidden `--cursor-hooks-path` argv** parsed by `PluginCommand` (analogous to how Codex tests are kept hermetic via `--project` resolving against `Environment.CurrentDirectory`). If the engineer prefers a different seam, document it inline — the goal is tests that don't write into `~/.cursor/`.

- [ ] **Step 3: Verify tests fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksWriterTests/* /*/*/PluginCommandCursorTests/*"`
Expected: FAIL.

- [ ] **Step 4: Extend `PluginCommand` with Cursor branches**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs`:

1. Add `const string CursorHookCommand = "kapacitor hook --cursor";` next to `CodexHookCommand`.
2. Reject `--cursor` together with `--codex` or `--skills`:

```csharp
static async Task<int> Install(string[] args) {
    if ((args.Contains("--codex")  && args.Contains("--skills"))
     || (args.Contains("--cursor") && args.Contains("--skills"))
     || (args.Contains("--cursor") && args.Contains("--codex"))) {
        await Console.Error.WriteLineAsync(
            "--cursor, --codex, and --skills are mutually exclusive.");
        return 1;
    }

    if (args.Contains("--skills")) return await InstallSkills(args);
    if (args.Contains("--codex"))  return await InstallCodex(args);
    if (args.Contains("--cursor")) return await InstallCursor(args);
    return await InstallClaude(args);
}

static async Task<int> Remove(string[] args) {
    if ((args.Contains("--codex")  && args.Contains("--skills"))
     || (args.Contains("--cursor") && args.Contains("--skills"))
     || (args.Contains("--cursor") && args.Contains("--codex"))) {
        await Console.Error.WriteLineAsync(
            "--cursor, --codex, and --skills are mutually exclusive.");
        return 1;
    }

    if (args.Contains("--skills")) return await RemoveSkills(args);
    if (args.Contains("--codex"))  return await RemoveCodex(args);
    if (args.Contains("--cursor")) return await RemoveCursor(args);
    return await RemoveClaude(args);
}
```

3. Add `InstallCursor`, `RemoveCursor`, `InstallCursorHooks`, `RemoveCursorHooks`:

```csharp
static async Task<int> InstallCursor(string[] args) {
    var hooksPath = GetArg(args, "--cursor-hooks-path") ?? CursorPaths.UserHooksJson();

    var refreshOnly = args.Contains("--if-installed");

    if (refreshOnly && !CursorHooksInstaller.IsInstalled(hooksPath)) return 0;
    if (refreshOnly &&
        CursorHooksInstaller.ReadMarker(hooksPath) == KapacitorVersion.Current()) {
        return 0;
    }

    // PATH precheck. hooks.json writes the bare `kapacitor hook --cursor`
    // command; we must verify Cursor will actually find it. Skip the
    // precheck on the postinstall path (--if-installed) so an in-flight
    // npm install doesn't fail just because the new symlink isn't on the
    // child process's PATH yet.
    if (!refreshOnly && !AgentDetector.IsInstalled("kapacitor")) {
        await Console.Error.WriteLineAsync(
            "Cannot install Cursor hooks: 'kapacitor' is not on PATH. "
            + "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
        return 1;
    }

    if (!InstallCursorHooks(hooksPath)) {
        if (refreshOnly) return 0;
        await Console.Error.WriteLineAsync("Could not write Cursor hooks file.");
        return 1;
    }

    await Console.Out.WriteLineAsync(refreshOnly
        ? $"Cursor hooks refreshed ({hooksPath})"
        : $"Cursor hooks installed ({hooksPath})");
    return 0;
}

static async Task<int> RemoveCursor(string[] args) {
    var hooksPath = GetArg(args, "--cursor-hooks-path") ?? CursorPaths.UserHooksJson();
    if (!File.Exists(hooksPath)) {
        await Console.Out.WriteLineAsync("Nothing to remove — Cursor hooks file not found.");
        return 0;
    }
    var removed = RemoveCursorHooks(hooksPath);
    await Console.Out.WriteLineAsync(removed
        ? $"Cursor hooks removed ({hooksPath})"
        : "Cursor hooks were not installed.");
    return 0;
}

/// <summary>
/// Writes (or merges into) <paramref name="hooksPath"/> a Cursor hooks.json
/// invoking <c>kapacitor hook --cursor</c> for every event. Preserves
/// user-authored entries; replaces existing kapacitor entries.
/// </summary>
public static bool InstallCursorHooks(string hooksPath) {
    try {
        JsonObject root = [];
        if (File.Exists(hooksPath)) {
            try { if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject obj) root = obj; }
            catch { /* Malformed — start fresh */ }
        }

        if (root["version"] is null) root["version"] = 1;
        if (root["hooks"] is not JsonObject hooks) { hooks = []; root["hooks"] = hooks; }

        foreach (var evt in CursorHooksParser.CursorHookEvents) {
            var kapacitorEntry = new JsonObject {
                ["command"] = CursorHookCommand
            };

            if (hooks[evt] is not JsonArray entries) {
                hooks[evt] = new JsonArray(kapacitorEntry);
                continue;
            }

            var preserved = new JsonArray();
            foreach (var entry in entries) {
                if (entry is null) continue;
                if (!CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)) {
                    preserved.Add(entry.DeepClone());
                }
            }
            preserved.Add((JsonNode)kapacitorEntry);
            hooks[evt] = preserved;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
        CursorHooksInstaller.WriteMarker(hooksPath);
        return true;
    } catch { return false; }
}

public static bool RemoveCursorHooks(string hooksPath) {
    try {
        if (!File.Exists(hooksPath)) return false;
        if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
        if (root["hooks"] is not JsonObject hooks) return false;

        var changed = false;
        foreach (var evt in CursorHooksParser.CursorHookEvents) {
            if (hooks[evt] is not JsonArray entries) continue;
            var preserved = new JsonArray();
            foreach (var entry in entries) {
                if (entry is null) continue;
                if (CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)) {
                    changed = true;
                } else {
                    preserved.Add(entry.DeepClone());
                }
            }
            hooks[evt] = preserved;
        }

        if (changed) {
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            CursorHooksInstaller.DeleteMarker(hooksPath);
        }
        return changed;
    } catch { return false; }
}
```

Also add a `GetArg` helper if the class doesn't have one already; or inline-parse `--cursor-hooks-path` from the args.

4. Update `PrintUsage`:

```csharp
static int PrintUsage() {
    Console.Error.WriteLine(
        "Usage: kapacitor plugin <install|remove> [--project] [--codex|--cursor|--skills] [--if-installed]");
    return 1;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHooksWriterTests/* /*/*/PluginCommandCursorTests/*"`
Expected: PASS, 7/7.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs test/Kapacitor.Cli.Tests.Unit/CursorHooksWriterTests.cs test/Kapacitor.Cli.Tests.Unit/PluginCommandCursorTests.cs
git commit -m "[AI-730] PluginCommand: add --cursor install/remove

$(cat <<'EOF'
Adds kapacitor plugin install --cursor [--if-installed] and remove --cursor
following the existing --codex shape. InstallCursorHooks writes the bare
'kapacitor hook --cursor' command for all 8 events into ~/.cursor/hooks.json,
preserving user-authored entries and replacing existing kapacitor entries.
PATH precheck on the non-postinstall path guards against writing a config
that will silently fail.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 11: `npm/kapacitor/bin/postinstall.js` — refresh Cursor hooks on upgrade

**Files:**

* Modify: `npm/kapacitor/bin/postinstall.js`

Append one more entry to the `refreshes` array. No tests — the file is the contract.

- [ ] **Step 1: Edit `postinstall.js`**

```javascript
const refreshes = [
  ["plugin", "install", "--skills", "--if-installed"],
  ["plugin", "install", "--codex",  "--if-installed"],
  ["plugin", "install", "--cursor", "--if-installed"],
  ["plugin", "install",             "--if-installed"], // Claude
];
```

- [ ] **Step 2: Smoke-validate by running `node npm/kapacitor/bin/postinstall.js` with a stubbed kapacitor binary**

Quick sanity: `node -e "require('./npm/kapacitor/bin/postinstall.js')"` should exit 0 (and no-op outside a global install).

- [ ] **Step 3: Commit**

```bash
git add npm/kapacitor/bin/postinstall.js
git commit -m "[AI-730] postinstall: refresh Cursor hooks on npm upgrade

$(cat <<'EOF'
Adds plugin install --cursor --if-installed to the npm postinstall refresh
list. Gated by the .kapacitor-hooks-version marker — no-ops on machines
where Cursor hooks were never opted into.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 12: `SetupCommand` + `CodingAgentsStep` — Cursor wiring in Step 4

**Files:**

* Modify: `src/Kapacitor.Cli/Commands/CodingAgentsStep.cs`
* Modify: `src/Kapacitor.Cli/Commands/SetupCommand.cs`
* Test: extend the existing `CodingAgentsStepTests` (search for the file) with Cursor branches.

Add Cursor as a third detected agent alongside Claude / Codex, with `--skip-cursor-hooks` argv flag and a Step 4 prompt.

- [ ] **Step 1: Locate existing `CodingAgentsStep` tests and read the test conventions**

Run: `ls test/Kapacitor.Cli.Tests.Unit/Setup/CodingAgentsStepTests.cs 2>/dev/null || ls test/Kapacitor.Cli.Tests.Unit/CodingAgentsStepTests.cs 2>/dev/null || grep -rln 'CodingAgentsStep' test/`
Expected: locate the existing test file.

- [ ] **Step 2: Extend `CodingAgentsStep` records and dispatch**

```csharp
internal record Options(bool SkipClaude, bool SkipCodex, bool SkipCursor, bool NoPrompt);
internal record DetectedAgents(bool Claude, bool Codex, bool Cursor);
internal record Paths(
    string ClaudeSettingsPath,
    string ClaudeScopeLabel,
    string? PluginDir,
    string CodexHooksPath,
    string CursorHooksPath,
    string AgentsSkillsDir,
    string LegacyCodexSkillsDir);
internal record Installers(
    Func<string, string, bool> InstallClaudePlugin,
    Func<string, bool>         InstallCodexHooks,
    Func<string, bool>         InstallCursorHooks,
    Func<string, string, bool> InstallAgentSkills,
    Func<string, bool>         CleanLegacyCodexSkills);
internal record Result(
    bool ClaudeInstalled,
    bool CodexHooksInstalled,
    bool CodexSkillsInstalled,
    bool CursorHooksInstalled);
```

Update `RunAsync` to call a new `HandleCursorHooks` helper, mirroring `HandleCodexHooks`:

```csharp
internal static Task<Result> RunAsync(
    Options options,
    DetectedAgents detected,
    Paths paths,
    Installers installers,
    Func<string, bool> prompt,
    Action<string> writeLine) {
    var claudeInstalled      = HandleClaude(options, detected, paths, installers, prompt, writeLine);
    var codexHooksInstalled  = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);
    var codexSkillsInstalled = codexHooksInstalled
        ? HandleCodexSkills(paths, installers, writeLine)
        : false;
    var cursorHooksInstalled = HandleCursorHooks(options, detected, paths, installers, prompt, writeLine);

    if (!detected.Claude && !detected.Codex && !detected.Cursor) {
        writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code, Codex CLI, or Cursor to start capturing sessions.");
    }

    return Task.FromResult(new Result(
        claudeInstalled, codexHooksInstalled, codexSkillsInstalled, cursorHooksInstalled));
}

static bool HandleCursorHooks(
    Options options,
    DetectedAgents detected,
    Paths paths,
    Installers installers,
    Func<string, bool> prompt,
    Action<string> writeLine) {
    if (!detected.Cursor) {
        writeLine("  [dim]· Cursor not detected — skipping[/]");
        return false;
    }

    writeLine("  [green]✓[/] Cursor detected");

    if (options.SkipCursor) {
        writeLine("  [dim]· Cursor hooks skipped by flag[/]");
        return false;
    }

    var shouldInstall = options.NoPrompt || prompt("Install Cursor IDE hooks?");
    if (!shouldInstall) {
        writeLine("  [dim]· Cursor hooks not installed (you can run kapacitor plugin install --cursor later)[/]");
        return false;
    }

    var ok = installers.InstallCursorHooks(paths.CursorHooksPath);
    if (!ok) {
        writeLine("  [yellow]⚠[/] Could not write Cursor hooks file.");
        return false;
    }

    writeLine($"  [green]✓[/] Cursor hooks installed ({Spectre.Console.Markup.Escape(paths.CursorHooksPath)})");
    return true;
}
```

- [ ] **Step 3: Update `SetupCommand.HandleAsync`**

In `src/Kapacitor.Cli/Commands/SetupCommand.cs`:

1. Parse `--skip-cursor-hooks`:
   ```csharp
   var skipCursorFlag = args.Contains("--skip-cursor-hooks");
   ```

2. Detection:
   ```csharp
   var detected = new CodingAgentsStep.DetectedAgents(
       Claude: AgentDetector.IsInstalled("claude"),
       Codex:  AgentDetector.IsInstalled("codex"),
       Cursor: CursorPaths.IsInstalled());
   ```

3. `Options` / `Paths` / `Installers`:
   ```csharp
   var stepOptions = new CodingAgentsStep.Options(
       SkipClaude: skipClaude, SkipCodex: skipCodexFlag, SkipCursor: skipCursorFlag, NoPrompt: noPrompt);

   var stepPaths = new CodingAgentsStep.Paths(
       ClaudeSettingsPath:   claudeSettingsPath,
       ClaudeScopeLabel:     legacyProjectScope ? "project" : "user",
       PluginDir:            pluginPath,
       CodexHooksPath:       CodexPaths.UserHooksJson,
       CursorHooksPath:      CursorPaths.UserHooksJson(),
       AgentsSkillsDir:      AgentsPaths.UserSkillsDir,
       LegacyCodexSkillsDir: Path.Combine(CodexPaths.Home, "skills"));

   var stepInstallers = new CodingAgentsStep.Installers(
       InstallClaudePlugin:    InstallPlugin,
       InstallCodexHooks:      PluginCommand.InstallCodexHooks,
       InstallCursorHooks:     PluginCommand.InstallCursorHooks,
       InstallAgentSkills:     Kapacitor.Cli.Core.AgentsSkillsInstaller.Install,
       CleanLegacyCodexSkills: legacyDir => Kapacitor.Cli.Core.AgentsSkillsInstaller.CleanLegacyCodexSkills(legacyDir).RemovedAny);
   ```

- [ ] **Step 4: Extend `CodingAgentsStepTests` with Cursor branches**

Mirror the existing Codex branches: a `detected.Cursor: true` test that produces `cursorHooksInstalled: true`, a `SkipCursor: true` test that skips, and a failed-write test. Use the existing test file's helpers — don't reinvent.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Commands/SetupCommand.cs src/Kapacitor.Cli/Commands/CodingAgentsStep.cs test/Kapacitor.Cli.Tests.Unit/
git commit -m "[AI-730] Setup: detect Cursor by user-dir, wire hooks install

$(cat <<'EOF'
CodingAgentsStep gains a Cursor branch parallel to Codex. SetupCommand
detects Cursor via CursorPaths.IsInstalled() (user-dir presence) rather
than AgentDetector.IsInstalled (PATH probe) so IDE users without the
cursor shell command are still detected. --skip-cursor-hooks suppresses
the prompt.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 13: README + help text updates

**Files:**

* Modify: `README.md`
* Modify: `src/Kapacitor.Cli.Core/Resources/help-setup.txt`
* Modify: `src/Kapacitor.Cli.Core/Resources/help-plugin.txt`
* Create: `src/Kapacitor.Cli.Core/Resources/help-hook.txt` (replace the per-event templated `help-hook.txt` if it currently lives in Program.cs, or extend it)

Per CLAUDE.md the README sync is a hard rule. Add a Cursor section to the Getting Started and a `kapacitor plugin install --cursor` line in the CLI commands section. Help text must mention `--skip-cursor-hooks`.

- [ ] **Step 1: Read existing README sections to pattern-match the Codex prose**

Run: `grep -n 'Codex' README.md | head -20`
Pattern-match the existing Codex copy.

- [ ] **Step 2: Add Cursor sections**

In `README.md` under `## Getting started`:

```markdown
### Cursor IDE

`kapacitor setup` writes `~/.cursor/hooks.json` invoking `kapacitor hook --cursor` for every Cursor hook event. Cursor Agent sessions stream to your server live — no manual import needed.

Detection is by directory presence (`~/.cursor/`, OS-specific Cursor User dir), not by PATH, so Cursor IDE users without the `cursor` shell command are still detected.

Pass `--skip-cursor-hooks` to skip Cursor wiring during setup. To install or remove later:

```bash
kapacitor plugin install --cursor
kapacitor plugin remove  --cursor
```

The legacy SQLite backfill (`kapacitor import --cursor`) is unchanged and still works for historical sessions.
```

In `## CLI commands` under `plugin`, append:

```markdown
- `kapacitor plugin install --cursor` — writes `~/.cursor/hooks.json` invoking `kapacitor hook --cursor` for all 8 Cursor hook events. Preserves any user-authored entries.
- `kapacitor plugin remove  --cursor` — strips kapacitor entries from `~/.cursor/hooks.json`.
```

- [ ] **Step 3: Update help text**

`help-setup.txt`: add a line for `--skip-cursor-hooks`.

`help-plugin.txt`: add `--cursor` to the synopsis and a short description.

`help-hook.txt` (new or replace the templated version): document `kapacitor hook --cursor` as the only currently-supported vendor flag and note that the command is invoked by Cursor's hook system, not by users.

- [ ] **Step 4: Update the docs site reminder**

Memory note in `MEMORY.md`: Kapacitor web docs at `../kapacitor-web` track CLI surface changes. Open a follow-up TODO (in the PR description, not in code) to mirror these docs updates in `commands.md` and the getting-started pages there.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Kapacitor.Cli.Core/Resources/help-setup.txt src/Kapacitor.Cli.Core/Resources/help-plugin.txt src/Kapacitor.Cli.Core/Resources/help-hook.txt
git commit -m "[AI-730] Docs: Cursor hooks + plugin install --cursor

$(cat <<'EOF'
README and help-* now describe kapacitor plugin install --cursor and the
--skip-cursor-hooks setup flag. Detection-by-user-dir behaviour is called
out so IDE users without the cursor shell command know they're covered.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 14: Integration tests — WireMock per route + slow-server budget + spool persistence

**Files:**

* Create: `test/Kapacitor.Cli.Tests.Integration/CursorHookDispatcherTests.cs`

Use the project's existing WireMock fixture conventions (see how `CodexHookCommandTests` in the integration project is wired). Cover:

1. Each of the 8 routes returns 200 → dispatcher exits 0; verify outgoing payload shape via a Verify snapshot.
2. Watermark GET 404 + transcript JSONL with 3 lines → dispatcher POSTs 3 transcript lines.
3. Slow server (every POST takes 800ms) → dispatcher exits within budget with partial progress; spool retains the unposted canonical events.
4. Process restart simulation: invoke `HandleCore` twice against the same spool dir; second invocation drains the spool from the first.

Each sub-test follows the same TDD shape as the unit tests:

- [ ] **Step 1**: Write the failing test.
- [ ] **Step 2**: Verify it fails.
- [ ] **Step 3**: Add WireMock mappings + Verify snapshot files.
- [ ] **Step 4**: Verify pass.
- [ ] **Step 5**: Commit.

Because the exact WireMock plumbing depends on patterns already in the integration project, the engineer should pattern-match `CodexHookCommandTests` (if present) before authoring. If no equivalent exists, lift the WireMock setup from `test/Kapacitor.Cli.Tests.Integration/HttpClientExtensionsTests.cs` (or whichever WireMock'd file is closest).

Commit message:

```
[AI-730] Integration: WireMock dispatcher coverage

WireMock fixture per /hooks/<event>/cursor route; Verify snapshots of
outgoing payload shape per event; slow-server simulation proving the
2s budget bounds the dispatcher; spool persistence across simulated
process restarts.
```

---

### Task 15: AOT publish verification — zero IL3050/IL2026 warnings

**Files:** none modified; this is a CI gate.

- [ ] **Step 1: AOT publish**

Run: `dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release 2>&1 | tee /tmp/ai730-publish.log`
Expected: build succeeds.

- [ ] **Step 2: Scan for trimming warnings**

Run: `grep -E 'IL[23][01][0-9]{2}' /tmp/ai730-publish.log`
Expected: NO output.

If warnings appear, common culprits in the new code:

* `JsonNode.Parse` / `JsonObject` mutations — used throughout the existing Codex path, so they should already be safe. If a new warning surfaces, replace any `obj["k"] = something` with explicit `JsonValue.Create(...)` calls.
* Collection-expression `JsonArray` initialisation. Use `new JsonArray(item1, item2)` (already in plan).
* `Convert.ToHexString` is AOT-safe.

If a warning is genuine, fix it and re-run.

- [ ] **Step 3: Commit if any fixes were required**

Otherwise no commit.

---

### Task 16: Run the full test suite (unit + integration)

- [ ] **Step 1: Unit tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 2: Integration tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Integration/Kapacitor.Cli.Tests.Integration.csproj`
Expected: PASS.

If either suite has failures, do NOT mark the work complete — root-cause and fix.

---

### Task 17: Manual smoke (against a real Cursor install — gated on AI-731 being deployed)

This task does not produce code or commits, but it is **required before merge** per the design's manual-smoke checklist.

- [ ] **Step 1: `kapacitor setup`** in a clean home; verify `~/.cursor/hooks.json` and `~/.cursor/.kapacitor-hooks-version` exist.
- [ ] **Step 2: New Cursor Agent composer.** Verify `sessionStart` POSTs and the session appears live in the dashboard.
- [ ] **Step 3: Submit a prompt that triggers a tool.** Verify the per-event hook POSTs are visible in server logs and the transcript lines appear, no duplicates, in the dashboard.
- [ ] **Step 4: End the conversation.** Verify `sessionEnd` POSTs; the spool file (if it exists for this session) is deleted.
- [ ] **Step 5: Mid-session install.** Open a Cursor composer, run a couple of turns, then `kapacitor plugin install --cursor`. On the next event, verify transcript backfill replays turns 1..N and subsequent hooks append cleanly.
- [ ] **Step 6: Server outage simulation.** Stop the server mid-session, fire a few hook events, restart the server. Verify the spool replays canonical events on the next hook.
- [ ] **Step 7: `kapacitor disable <sessionId>`.** Verify subsequent hook POSTs are suppressed (server-side: no new events for that session ID).

---

## Self-review

### Spec coverage check

| Spec requirement | Task(s) |
|---|---|
| `CursorHookCommand` reads stdin, parses JSON, branches on `hook_event_name` | 8 |
| Single `hook` command in `Program.cs` with vendor flag dispatch | 9 |
| Dispatcher 2-second wall-clock budget | 8 |
| No `PostWithRetryAsync` in hook path; `PostOnceAsync` overload | 4, 7, 8 |
| Per-call timeouts (hook ~1s, watermark ~500ms, transcript ~1s) | 4, 7, 8 |
| Read stdin, parse JSON, return 0 on malformed | 8 |
| Normalize `session_id` (dashless) | 8 |
| Inject `home_dir` and `agent_host_id` | 8 |
| Honor `DisabledSessions.IsDisabled(sessionId)` | 8 |
| Drain per-session spool first | 6, 8 |
| POST to `/hooks/<event>/cursor` | 5, 8 |
| Emit nothing on stdout | 8 (no `Console.Write` in dispatcher) |
| Per-event handlers for 8 hooks | 5 (the mapping handles all 8) |
| Telemetry-only POSTs for `afterAgentResponse` / `preToolUse` / `postToolUse` / `postToolUseFailure` | 5, 8 |
| `afterAgentThought` canonical event ID from `(sid, "reasoning", gen, sha256(text)[:16])` | 8 |
| Hook-event spool at `~/.cursor/kapacitor-pending/<sid>.jsonl` | 2, 6 |
| Spool eligibility: 4 canonical-event hooks only | 5, 6, 8 |
| Spool 1 MB cap with oldest-first eviction | 6 |
| Spool cleanup on successful `sessionEnd` | 8 |
| 30-day reaper | 6 (`ReapOlderThan`), 8 (called from `Handle`) |
| Transcript backfill only when `transcript_path` non-null | 7, 8 |
| GET watermark; resume from `last_line_number + 1` | 7 |
| Loop POSTing JSONL lines until EOF, budget, or failure | 7 |
| No in-hook retries | 4, 7, 8 |
| `CursorHooksInstaller` mirror of `CodexHooksInstaller` | 3 |
| Marker at `~/.cursor/.kapacitor-hooks-version`, stamped with `KapacitorVersion.Current()` | 3, 10 |
| `IsInstalled` returns true when marker OR pre-marker kapacitor entries detected | 3 |
| `kapacitor plugin install --cursor [--if-installed]` | 10 |
| `kapacitor plugin remove --cursor` | 10 |
| `--if-installed` short-circuits on same-version marker | 10 |
| `npm/postinstall.js` calls `--cursor --if-installed` | 11 |
| Setup detects Cursor by user-dir presence, not PATH | 2, 12 |
| `CursorPaths.IsInstalled()` checks all 4 OS user dirs | 2 |
| `CursorPaths.UserHooksJson` | 2, 10 |
| `InstallCursorHooks` added to `CodingAgentsStep.Installers` | 12 |
| JSON merger preserves user-authored hooks | 10 |
| `hooks.json` writes bare `kapacitor hook --cursor` command | 10 |
| Setup precheck: `kapacitor` resolvable on PATH | 10 |
| `--skip-cursor-hooks` argv and Cursor section in Step 4 | 12 |
| README updated; `help-*.txt` updated | 13 |
| Unit tests covering branching, normalization, disabled, malformed, telemetry-only, thought ID stability, null path, budget, fail-open, paths.IsInstalled, setup precheck, spool, hook without vendor flag, marker round-trip, `--if-installed` short-circuit | 1, 2, 3, 5, 6, 7, 8, 9, 10 |
| Integration tests with WireMock per route, Verify snapshots, JSONL backfill, slow-server, spool persistence | 14 |
| No IL3050/IL2026 warnings | 15 |

All requirements mapped. No gaps.

### Placeholder scan

* No "TBD" or "implement later" — every step has complete code or a precise instruction.
* Two soft pointers: Task 9's Program-subprocess test ("if the project's test convention prefers in-process invocation, follow that") and Task 12's `CodingAgentsStep` test extension ("mirror the existing Codex branches — don't reinvent"). Both reference concrete existing patterns and are minor enough to leave to the implementer.
* Task 14 (integration tests) is intentionally lighter than the unit tasks because the WireMock plumbing follows existing project conventions; the engineer pattern-matches from sibling integration tests.

### Type consistency check

* `CursorHookEventMap.Mapping` — used identically in `CursorHookCommand.HandleCore` (Task 8) and in tests (Task 5).
* `CursorHookSpool.Entry` — used in `CursorHookCommand.HandleCore` (Task 8) via `DrainAsync` (Task 6).
* `CursorTranscriptBackfill.Stats` — defined in Task 7, consumed in Task 8.
* `CursorHooksParser.CursorHookEvents` — defined in Task 1, consumed in Task 10 (`InstallCursorHooks` loop).
* `CursorHooksInstaller` API — defined in Task 3, consumed in Task 10 and Task 12.
* `CursorPaths.IsInstalled`, `UserHooksJson`, `SpoolDir` — defined in Task 2, consumed in Tasks 8, 10, 12.
* `PostOnceAsync` / `GetOnceAsync` — defined in Task 4, consumed in Tasks 7 and 8.

All signatures consistent.

---

## Risk callouts

1. **Server-route URLs are owned by AI-731.** The strings used in this plan are the design's; reconcile with AI-731 before merge. If AI-731 picks different segments, search-and-replace in `CursorHookEventMap.Map`, `CursorTranscriptBackfill.RunAsync`, and the integration tests.
2. **`canonical_event_id` field name** — the design doesn't pin the JSON field name on outgoing thought payloads, only the algorithm. The plan uses `"canonical_event_id"`; AI-731 may prefer a different shape (e.g. `"event_id"` or stamping it as a header). Align before merge.
3. **`--cursor-hooks-path` test seam** — chosen to keep unit tests hermetic. If the project's convention is to use a different seam (env var, fixture base class), follow that instead.
4. **`hook` command help text** — currently `Program.cs` builds `help-hook.txt` from a templated per-event string (`help-hook.txt` exists with a `{cmd}` placeholder). The new `kapacitor hook` command is *not* one of the templated `hookCommands`, so the existing templating path doesn't apply. Either dedicate a new help file or extend the help dispatch to handle `hook` as a separate case. Task 13 calls this out.

---

**Plan length:** ~17 tasks, ~5 sub-steps each ≈ 85 actionable steps. Bite-sized: each step is 2-5 minutes of real work; commits at the end of each task keep the history reviewable.
