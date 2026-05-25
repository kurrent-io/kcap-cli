# Codex Skill Parity + Auto-Resolved Session ID — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship five-Codex-skill parity with Claude Code (add `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan`) and auto-resolve the active Capacitor session ID for Codex CLI 0.81+ via the `CODEX_THREAD_ID` env var, eliminating the manual session-ID dance that today's Codex skills require.

**Architecture:** Add a `CODEX_THREAD_ID` step to the existing `ArgParsing.ResolveSessionId` env tail (Claude `KAPACITOR_SESSION_ID` keeps precedence). Extract the env-walk into a new `internal static ResolveSessionIdFromEnv()` so `set-title` (whose positional is `<title>`, not `<sessionId>`) can share it. Extend `kapacitor hide` and `kapacitor disable` to accept an optional positional `sessionId` so the new Codex skills can pass through what they resolve. All five Codex `SKILL.md` files install via `PluginCommand.CodexSkillNames`.

**Tech Stack:** .NET 10 NativeAOT CLI, TUnit unit tests, WireMock.Net for HTTP doubles. Spec: [`docs/superpowers/specs/2026-05-24-ai-676-codex-skill-parity-design.md`](../specs/2026-05-24-ai-676-codex-skill-parity-design.md).

**Spec section references:** Each task lists the spec section that drives it. Read that section before starting the task if anything below is unclear.

---

## Task 0: Day-1 probe — verify `CODEX_THREAD_ID` equals hook `session_id`

**Spec reference:** §7.1 (Single real risk).

**Why first.** The whole plan assumes the env var and the hook payload `session_id` refer to the same UUID modulo dash-stripping. If they don't, the rest of this plan changes (the marker-file fallback in §7.1 replaces the direct env read step). Verify before touching anything else.

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/CodexHookCommand.cs:81-107` (add one diagnostic line at top of `HandleSessionStart`)

- [ ] **Step 1: Add diagnostic line at top of `HandleSessionStart`**

Open `src/Kapacitor.Cli/Commands/CodexHookCommand.cs` and insert the first line of the method body at line 82 (immediately after the method signature opens, before `var enriched = await RepositoryDetection...`):

```csharp
Console.Error.WriteLine(
    $"[probe] CODEX_THREAD_ID={Environment.GetEnvironmentVariable("CODEX_THREAD_ID")} "
    + $"payload.session_id={TryGetString(node, "session_id")}");
```

- [ ] **Step 2: Build the CLI in Debug**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj`
Expected: Build succeeds. No new warnings.

- [ ] **Step 3: Make the freshly built binary the one Codex invokes**

The Codex hook command path is `kapacitor codex-hook` and it's resolved via `PATH`. Either temporarily replace the global binary at `~/.local/bin/kapacitor` with the Debug output, or set up a one-shot wrapper. The simplest path:

```bash
# Show where Codex would find kapacitor
which kapacitor

# Back up and replace
cp "$(which kapacitor)" "$(which kapacitor).bak"
cp src/Kapacitor.Cli/bin/Debug/net10.0/Kapacitor.Cli "$(which kapacitor)"
# macOS only — re-sign the AOT/Debug binary after copying:
codesign --force --sign - "$(which kapacitor)"
```

Expected: `kapacitor codex-hook --version` (or equivalent) runs without "killed: 9" / signing errors.

- [ ] **Step 4: Run one Codex session and capture probe output**

Start a fresh Codex CLI session in any directory that has the kapacitor hooks installed (`~/.codex/hooks.json` must contain the `kapacitor codex-hook` entries — `kapacitor plugin install --codex` if not). The probe writes to `stderr`. Codex routes hook stderr to its log:

```bash
# In another terminal, tail the Codex hook log
tail -f ~/.codex/log/codex-tui.log 2>/dev/null || tail -f ~/Library/Logs/codex/*.log
# Look for: [probe] CODEX_THREAD_ID=... payload.session_id=...
```

In Codex, type any prompt to fire `SessionStart`.

Expected: One `[probe]` line appears. Record both values.

- [ ] **Step 5: Compare values and decide**

The two values should be the same UUID modulo dash formatting.

- **If they match (expected outcome):** continue with Task 1 as written. Move on to Step 6 to clean up.
- **If they differ:** **stop**. The plan needs to switch to the marker-file fallback (spec §7.1, "If they differ" bullet). Update this plan inline (or open a new one) before continuing. Record the divergence in a comment on AI-677.
- **If `CODEX_THREAD_ID` is empty:** Codex CLI is too old (<0.81). Update Codex (`npm i -g @openai/codex@latest`) and re-run Step 4.

- [ ] **Step 6: Remove the probe line**

Revert the change from Step 1. The probe was temporary instrumentation.

```bash
git diff src/Kapacitor.Cli/Commands/CodexHookCommand.cs   # confirm only the probe line is gone
```

- [ ] **Step 7: Restore the original kapacitor binary**

```bash
mv "$(which kapacitor).bak" "$(which kapacitor)"
```

- [ ] **Step 8: Commit nothing**

No commit. This task is a decision gate — its only artifact is the recorded probe values and your decision. Carry forward into Task 1.

---

## Task 1: `ArgParsing.ResolveSessionIdFromEnv` helper (TDD)

**Spec reference:** §3.1 (Centralize session-ID resolution).

**Files:**
- Modify: `src/Kapacitor.Cli/ArgParsing.cs:1-30` (extract env tail into helper; main resolver delegates)
- Modify: `test/Kapacitor.Cli.Tests.Unit/ArgParsingTests.cs` (add cases, rename serialization group)

- [ ] **Step 1: Rename the existing test serialization group constant**

Open `test/Kapacitor.Cli.Tests.Unit/ArgParsingTests.cs` and change the existing constant near the bottom of the class:

```csharp
// Was:
const string KapacitorSessionIdEnvVar = "KAPACITOR_SESSION_ID";

// To:
const string SessionEnvVarMutation = "SessionEnvVarMutation";
const string KapacitorSessionIdEnvVar = "KAPACITOR_SESSION_ID";
const string CodexThreadIdEnvVar = "CODEX_THREAD_ID";
```

Update the two existing `[NotInParallel(nameof(KapacitorSessionIdEnvVar))]` attributes at the existing env-mutating tests to use `[NotInParallel(SessionEnvVarMutation)]`. Leave the body env-var reads unchanged (they still mutate `"KAPACITOR_SESSION_ID"`).

- [ ] **Step 2: Add failing tests for the `CODEX_THREAD_ID` extension and the verbatim-positional rule**

Append to `ArgParsingTests.cs`:

```csharp
[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionId_falls_back_to_codex_thread_id_when_kapacitor_unset() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, "01234567-89ab-cdef-0123-456789abcdef");

    try {
        var id = ArgParsing.ResolveSessionId(["recap"]);
        await Assert.That(id).IsEqualTo("0123456789abcdef0123456789abcdef");
    } finally {
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);
    }
}

[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionId_prefers_kapacitor_over_codex_thread_id() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "claude-session-id");
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, "codex-session-id");

    try {
        var id = ArgParsing.ResolveSessionId(["recap"]);
        await Assert.That(id).IsEqualTo("claudesessionid");
    } finally {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);
    }
}

[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionId_returns_null_when_no_positional_and_no_env() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

    var id = ArgParsing.ResolveSessionId(["recap"]);
    await Assert.That(id).IsNull();
}

[Test]
public async Task ResolveSessionId_returns_positional_verbatim_without_stripping_dashes() {
    // Positional slugs (meta-session IDs, README.md:153) MUST survive intact.
    var id = ArgParsing.ResolveSessionId(["recap", "foo-bar-baz"]);
    await Assert.That(id).IsEqualTo("foo-bar-baz");
}

[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionIdFromEnv_returns_dashless_kapacitor() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "abc-def");
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

    try {
        var id = ArgParsing.ResolveSessionIdFromEnv();
        await Assert.That(id).IsEqualTo("abcdef");
    } finally {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
    }
}

[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionIdFromEnv_returns_dashless_codex_thread_id_when_kapacitor_unset() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, "ghi-jkl");

    try {
        var id = ArgParsing.ResolveSessionIdFromEnv();
        await Assert.That(id).IsEqualTo("ghijkl");
    } finally {
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);
    }
}

[Test]
[NotInParallel(SessionEnvVarMutation)]
public async Task ResolveSessionIdFromEnv_returns_null_when_both_envs_unset() {
    Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
    Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

    var id = ArgParsing.ResolveSessionIdFromEnv();
    await Assert.That(id).IsNull();
}
```

- [ ] **Step 3: Run the new tests — they MUST fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ArgParsingTests/*"`
Expected: Build fails on `ResolveSessionIdFromEnv` (doesn't exist yet). The other new tests would fail at runtime if the build succeeded — both are acceptable "red" states for TDD.

- [ ] **Step 4: Implement the helper and update `ResolveSessionId` to delegate**

Replace the body of `src/Kapacitor.Cli/ArgParsing.cs` with:

```csharp
namespace Kapacitor.Cli;

static class ArgParsing {
    /// <summary>
    /// Resolves a positional sessionId from a command's argument list, falling
    /// back to the env helper. Value-bearing flags (e.g. <c>--model sonnet</c>)
    /// must be declared via <paramref name="valueFlags"/> so their values aren't
    /// mistaken for the sessionId. Positional values are returned verbatim —
    /// they may be meta-session slugs and must not be dash-stripped.
    /// </summary>
    internal static string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) {
        var knownValueFlags = valueFlags is null or { Length: 0 }
            ? null
            : new HashSet<string>(valueFlags, StringComparer.Ordinal);

        for (var i = skipCount; i < args.Length; i++) {
            var token = args[i];
            if (token.StartsWith("--")) {
                if (knownValueFlags?.Contains(token) == true && i + 1 < args.Length) {
                    i++; // skip the value as well
                }

                continue;
            }

            return token;
        }

        return ResolveSessionIdFromEnv();
    }

    /// <summary>
    /// Walks the session-ID env chain: <c>KAPACITOR_SESSION_ID</c> (Claude
    /// Code hook-set) then <c>CODEX_THREAD_ID</c> (Codex CLI 0.81+ export).
    /// Both are dash-stripped before return. Used directly by commands whose
    /// positional argument is not a sessionId (e.g. <c>set-title</c>).
    /// </summary>
    internal static string? ResolveSessionIdFromEnv() {
        var kapacitor = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(kapacitor))
            return kapacitor.Replace("-", "");

        var codex = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(codex))
            return codex.Replace("-", "");

        return null;
    }
}
```

- [ ] **Step 5: Run the tests — they MUST pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ArgParsingTests/*"`
Expected: All `ArgParsingTests` pass (the original 8 plus the 7 new ones).

- [ ] **Step 6: Run the entire unit suite to confirm no regression**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Kapacitor.Cli/ArgParsing.cs test/Kapacitor.Cli.Tests.Unit/ArgParsingTests.cs
git commit -m "feat(cli): resolve sessionId from CODEX_THREAD_ID env var

Adds ArgParsing.ResolveSessionIdFromEnv() walking KAPACITOR_SESSION_ID
then CODEX_THREAD_ID. ResolveSessionId delegates to it. Positional
values are still returned verbatim so meta-session slugs survive.

Part of AI-676 / AI-677."
```

---

## Task 2: Migrate `hide` and `disable` to the resolver + optional positional

**Spec reference:** §3.1 (caller migration bullets), §3.2.

`hide` and `disable` are parallel changes against the same shape (env-only read → resolver call). One task, one commit.

**Files:**
- Modify: `src/Kapacitor.Cli/Program.cs:304-352` (`case "disable"`)
- Modify: `src/Kapacitor.Cli/Program.cs:353-…` (`case "hide"`)

- [ ] **Step 1: Replace the inline env read in `disable`**

At `src/Kapacitor.Cli/Program.cs:305`, change:

```csharp
case "disable": {
    var sessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

    if (sessionId is null) {
        Console.Error.WriteLine("KAPACITOR_SESSION_ID not set. Run this inside an active Claude Code session.");

        return 1;
    }
```

to:

```csharp
case "disable": {
    // Positional override may be a dashed UUID; the WatcherManager and
    // server-side path both expect dashless, so we strip here. Env-sourced
    // values come pre-normalized from the resolver.
    var sessionId = ResolveSessionId(args)?.Replace("-", "");

    if (sessionId is null) {
        Console.Error.WriteLine("Usage: kapacitor disable [sessionId]");
        Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

        return 1;
    }
```

- [ ] **Step 2: Replace the inline env read in `hide`**

At `src/Kapacitor.Cli/Program.cs:354`, change:

```csharp
case "hide": {
    var sessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

    if (sessionId is null) {
        Console.Error.WriteLine("KAPACITOR_SESSION_ID not set. Run this inside an active Claude Code session.");

        return 1;
    }
```

to:

```csharp
case "hide": {
    var sessionId = ResolveSessionId(args)?.Replace("-", "");

    if (sessionId is null) {
        Console.Error.WriteLine("Usage: kapacitor hide [sessionId]");
        Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

        return 1;
    }
```

- [ ] **Step 3: Build and confirm no warnings**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj`
Expected: Build succeeds; no warnings.

- [ ] **Step 4: Run the full unit suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All tests pass. The new resolver path is exercised indirectly.

- [ ] **Step 5: Spot-check both commands by hand against a non-existent session**

Run: `KAPACITOR_SESSION_ID= CODEX_THREAD_ID= dotnet run --project src/Kapacitor.Cli/Kapacitor.Cli.csproj -- hide`
Expected stderr (exact wording):
```
Usage: kapacitor hide [sessionId]
  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.
```
Repeat for `disable`. Confirm exit code 1.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Program.cs
git commit -m "feat(cli): hide/disable accept optional [sessionId] positional

Routes both commands through ArgParsing.ResolveSessionId so an
explicit positional, KAPACITOR_SESSION_ID, or CODEX_THREAD_ID
all resolve. Updated error message names both Claude and Codex.

Part of AI-676."
```

---

## Task 3: Migrate `set-title` to the env helper with tailored error wording

**Spec reference:** §3.1 (set-title bullet).

`set-title` does **not** use the args resolver — its positional is `<title>`, not `<sessionId>`. Instead it calls `ArgParsing.ResolveSessionIdFromEnv()` directly. Both error branches get rewritten.

**Files:**
- Modify: `src/Kapacitor.Cli/Program.cs:498-510` (the `set-title` case and its `args.Length < 2` guard above)

- [ ] **Step 1: Rewrite the "missing title" branch (line 498)**

Currently:

```csharp
case "set-title" when args.Length < 2:
    Console.Error.WriteLine("Usage: kapacitor set-title <title>");
    Console.Error.WriteLine("  KAPACITOR_SESSION_ID must be set.");

    return 1;
```

Replace with:

```csharp
case "set-title" when args.Length < 2:
    Console.Error.WriteLine("Usage: kapacitor set-title <title>");

    return 1;
```

The second line (`KAPACITOR_SESSION_ID must be set`) is misleading — a user missing the title shouldn't see a session-id diagnosis. Drop it.

- [ ] **Step 2: Rewrite the "missing session env" branch (line 504-510)**

Currently:

```csharp
case "set-title": {
    var stSessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

    if (stSessionId is null) {
        Console.Error.WriteLine("KAPACITOR_SESSION_ID not set");

        return 1;
    }
```

Replace with:

```csharp
case "set-title": {
    var stSessionId = ArgParsing.ResolveSessionIdFromEnv();

    if (stSessionId is null) {
        Console.Error.WriteLine("No session ID found in KAPACITOR_SESSION_ID or CODEX_THREAD_ID.");
        Console.Error.WriteLine("Run set-title inside an active Claude Code / Codex CLI 0.81+ session.");

        return 1;
    }
```

The rest of the `set-title` case (lines 511+ — title joining, validation, HTTP call) is unchanged.

- [ ] **Step 3: Build**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj`
Expected: Build succeeds; no warnings.

- [ ] **Step 4: Smoke test both error branches by hand**

```bash
# Missing title — should print usage only, no session-id mention
dotnet run --project src/Kapacitor.Cli/Kapacitor.Cli.csproj -- set-title

# Missing env — should print the new tailored message
KAPACITOR_SESSION_ID= CODEX_THREAD_ID= dotnet run --project src/Kapacitor.Cli/Kapacitor.Cli.csproj -- set-title "any title here"
```

Expected: First emits `Usage: kapacitor set-title <title>` only. Second emits the two-line message about `KAPACITOR_SESSION_ID or CODEX_THREAD_ID`.

- [ ] **Step 5: Run the unit suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Program.cs
git commit -m "feat(cli): set-title resolves session via ArgParsing helper

Uses ResolveSessionIdFromEnv directly (positional is <title>, not
<sessionId>). Drops misleading KAPACITOR_SESSION_ID hint from the
missing-title branch. New error names both env sources.

Part of AI-677."
```

---

## Task 4: Consolidate error message strings for resolver callers

**Spec reference:** §3.1 (error message collapse bullet).

The existing positional-accepting commands (`recap`, `errors`, `validate-plan`, `eval`) each have a near-duplicate two-line error message at Program.cs:93-95, 113-115, 126-128, 143-146. Consolidate the wording for consistency with `hide`/`disable`/`set-title`.

**Files:**
- Modify: `src/Kapacitor.Cli/Program.cs:88-100` (`case "errors"`)
- Modify: `src/Kapacitor.Cli/Program.cs:101-121` (`case "recap"`)
- Modify: `src/Kapacitor.Cli/Program.cs:122-133` (`case "validate-plan"`)
- Modify: `src/Kapacitor.Cli/Program.cs:134-173` (`case "eval"`)

- [ ] **Step 1: Update `errors` error wording**

At line 93-95, change:

```csharp
Console.Error.WriteLine("Usage: kapacitor errors [--chain] [sessionId]");
Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");
```

to:

```csharp
Console.Error.WriteLine("Usage: kapacitor errors [--chain] [sessionId]");
Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");
```

- [ ] **Step 2: Update `recap` error wording**

At line 113-115, change:

```csharp
Console.Error.WriteLine("Usage: kapacitor recap [--chain] [--full] [--repo] [sessionId]");
Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");
Console.Error.WriteLine("  Use --repo to see recent session summaries for the current repository.");
```

to:

```csharp
Console.Error.WriteLine("Usage: kapacitor recap [--chain] [--full] [--repo] [sessionId]");
Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");
Console.Error.WriteLine("  Use --repo to see recent session summaries for the current repository.");
```

(`--repo` hint is preserved — it's `recap`-specific guidance, unrelated to env resolution.)

- [ ] **Step 3: Update `validate-plan` error wording**

At line 126-128, change:

```csharp
Console.Error.WriteLine("Usage: kapacitor validate-plan [sessionId]");
Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");
```

to:

```csharp
Console.Error.WriteLine("Usage: kapacitor validate-plan [sessionId]");
Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");
```

- [ ] **Step 4: Update `eval` error wording**

At line 143-146, change:

```csharp
Console.Error.WriteLine("Usage: kapacitor eval [--model sonnet] [--chain] [--threshold N]");
Console.Error.WriteLine("                     [--questions <csv> | --skip <csv>] [sessionId]");
Console.Error.WriteLine("       kapacitor eval --list-questions");
Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");
```

to:

```csharp
Console.Error.WriteLine("Usage: kapacitor eval [--model sonnet] [--chain] [--threshold N]");
Console.Error.WriteLine("                     [--questions <csv> | --skip <csv>] [sessionId]");
Console.Error.WriteLine("       kapacitor eval --list-questions");
Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");
```

- [ ] **Step 5: Build and run all unit tests**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj && dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Program.cs
git commit -m "refactor(cli): unify session-id missing error wording

All resolver-using commands print the same two-line message naming
both KAPACITOR_SESSION_ID (Claude) and CODEX_THREAD_ID (Codex 0.81+)
as supported environments.

Part of AI-677."
```

---

## Task 5: `PluginCommand` — add 3 skill names, make array internal, preflight validation (TDD)

**Spec reference:** §4.1 (PluginCommand.cs + bug fix bundled in), §6.2 (test scenarios).

Three things in one task because they all touch `PluginCommand.InstallCodexSkills`: array entries, accessibility, and preflight semantics.

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs:14-17` (the `CodexSkillNames` array)
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs:322-344` (`InstallCodexSkills` body)
- Modify: `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs` (extend existing tests + add new ones)

- [ ] **Step 1: Add failing tests for the new skill names and preflight semantics**

In `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`, after the existing `InstallCodexSkills_copies_known_skills` test (around line 197), add:

```csharp
[Test]
public async Task InstallCodexSkills_copies_all_five_known_skills() {
    using var tmp    = new TempDir();
    var       source = Path.Combine(tmp.Path, "codex-skills");
    var       target = Path.Combine(tmp.Path, "skills");
    foreach (var name in PluginCommand.CodexSkillNames) {
        WriteSkill(source, name, $"{name} body");
    }

    var ok = PluginCommand.InstallCodexSkills(source, target);
    await Assert.That(ok).IsTrue();

    foreach (var name in PluginCommand.CodexSkillNames) {
        var path = Path.Combine(target, name, "SKILL.md");
        await Assert.That(File.Exists(path)).IsTrue();
        var body = await File.ReadAllTextAsync(path);
        await Assert.That(body).IsEqualTo($"{name} body");
    }
}

[Test]
public async Task CodexSkillNames_contains_expected_five() {
    var expected = new[] {
        "kapacitor-recap",
        "kapacitor-errors",
        "kapacitor-hide",
        "kapacitor-disable",
        "kapacitor-validate-plan"
    };

    await Assert.That(PluginCommand.CodexSkillNames).IsEquivalentTo(expected);
}

[Test]
public async Task InstallCodexSkills_returns_false_when_known_folder_missing() {
    using var tmp    = new TempDir();
    var       source = Path.Combine(tmp.Path, "codex-skills");
    var       target = Path.Combine(tmp.Path, "skills");

    // Write four of the five expected names — leave kapacitor-validate-plan missing.
    WriteSkill(source, "kapacitor-recap",         "r");
    WriteSkill(source, "kapacitor-errors",        "e");
    WriteSkill(source, "kapacitor-hide",          "h");
    WriteSkill(source, "kapacitor-disable",       "d");

    // Pre-existing target folder for one of the known skills. The preflight
    // must NOT delete it because the install is aborted before any destructive
    // step runs.
    WriteSkill(target, "kapacitor-recap", "stale recap that must survive");

    var ok = PluginCommand.InstallCodexSkills(source, target);
    await Assert.That(ok).IsFalse();

    // Pre-existing target folder unchanged — preflight bailed before deletion.
    var preserved = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
    await Assert.That(preserved).IsEqualTo("stale recap that must survive");

    // None of the other expected target folders were created.
    await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-errors"))).IsFalse();
    await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-hide"))).IsFalse();
    await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-disable"))).IsFalse();
    await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-validate-plan"))).IsFalse();
}
```

- [ ] **Step 2: Run the new tests — they MUST fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/*"`
Expected: Build fails (`CodexSkillNames` not accessible from tests — it's currently private) or the tests fail at runtime. Either is a valid "red".

- [ ] **Step 3: Promote `CodexSkillNames` to `internal` and add the three new entries**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs:14`, change:

```csharp
static readonly string[] CodexSkillNames = [
    "kapacitor-recap",
    "kapacitor-errors"
];
```

to:

```csharp
internal static readonly string[] CodexSkillNames = [
    "kapacitor-recap",
    "kapacitor-errors",
    "kapacitor-hide",
    "kapacitor-disable",
    "kapacitor-validate-plan"
];
```

(`InternalsVisibleTo Include="Kapacitor.Cli.Tests.Unit"` is already declared in `src/Kapacitor.Cli/Kapacitor.Cli.csproj:17`, so the tests can see internal members.)

- [ ] **Step 4: Add preflight validation to `InstallCodexSkills`**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs:322-344`, replace the entire method body:

```csharp
public static bool InstallCodexSkills(string sourceDir, string targetDir) {
    if (!Directory.Exists(sourceDir)) return false;

    try {
        Directory.CreateDirectory(targetDir);

        foreach (var name in CodexSkillNames) {
            var src = Path.Combine(sourceDir, name);

            if (!Directory.Exists(src)) continue;

            var dst = Path.Combine(targetDir, name);

            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);

            CopyDirectory(src, dst);
        }

        return true;
    } catch {
        return false;
    }
}
```

with:

```csharp
public static bool InstallCodexSkills(string sourceDir, string targetDir) {
    if (!Directory.Exists(sourceDir)) return false;

    // Preflight: every known skill must have a folder under sourceDir. If
    // any is missing, fail BEFORE doing anything destructive — otherwise a
    // packaging error would leave the target dir half-overwritten.
    var missing = CodexSkillNames
        .Where(name => !Directory.Exists(Path.Combine(sourceDir, name)))
        .ToList();

    if (missing.Count > 0) {
        Console.Error.WriteLine(
            $"Cannot install Codex skills: missing source folder(s) under {sourceDir}: "
            + string.Join(", ", missing)
        );
        return false;
    }

    try {
        Directory.CreateDirectory(targetDir);

        foreach (var name in CodexSkillNames) {
            var src = Path.Combine(sourceDir, name);
            var dst = Path.Combine(targetDir, name);

            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);

            CopyDirectory(src, dst);
        }

        return true;
    } catch {
        return false;
    }
}
```

- [ ] **Step 5: Run the targeted tests — they MUST now pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/*"`
Expected: All `PluginCommandCodexTests` pass, including the three new tests and the original install/remove tests.

- [ ] **Step 6: Run the entire unit suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "feat(plugin): register 3 new Codex skills, fail fast on missing source

Adds kapacitor-hide, kapacitor-disable, and kapacitor-validate-plan
to CodexSkillNames (promoted to internal so tests can value-check it).

Preflight validation in InstallCodexSkills returns false if any known
source folder is absent — atomic install, no partial overwrite of an
existing target dir.

Part of AI-676."
```

---

## Task 6: Create the three new Codex `SKILL.md` files

**Spec reference:** §2.1.

**Files:**
- Create: `kapacitor/codex-skills/kapacitor-hide/SKILL.md`
- Create: `kapacitor/codex-skills/kapacitor-disable/SKILL.md`
- Create: `kapacitor/codex-skills/kapacitor-validate-plan/SKILL.md`

- [ ] **Step 1: Create `kapacitor-hide/SKILL.md`**

`mkdir -p kapacitor/codex-skills/kapacitor-hide` then write the file with contents:

```markdown
---
name: kapacitor-hide
description: >-
  This skill should be used when the user asks to "hide this session",
  "make this private", "hide session", "owner only", "make private",
  "hide from others", "set private", "don't show this session",
  or wants to change the current session visibility to owner-only.
  Sets session visibility so only the owner can see it.
---

# Kapacitor Hide

Hide the current session so only you (the owner) can see it.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor hide

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor hide <sessionId>
```

This sets the session visibility to "none" (owner-only). Other users will no longer see this session in the Capacitor dashboard.

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly. Use `kapacitor recap --repo` to list recent session IDs in this repository.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Notes

- This is reversible — visibility can be changed back via the Capacitor dashboard.
- The session data remains on the server, just hidden from other users.
- Unlike `kapacitor disable`, recording continues normally.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
```

- [ ] **Step 2: Create `kapacitor-disable/SKILL.md`**

`mkdir -p kapacitor/codex-skills/kapacitor-disable` then write:

```markdown
---
name: kapacitor-disable
description: >-
  This skill should be used when the user asks to "disable recording",
  "stop recording", "delete this session", "don't record this",
  "remove my session data", "erase this session", "turn off tracking",
  "stop tracking", "disable kapacitor", "remove session",
  or wants to stop the current session from being recorded.
  Stops the watcher, prevents future hooks, and deletes server data.
---

# Kapacitor Disable

Stop recording the current session and delete all data from the server.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor disable

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor disable <sessionId>
```

This will:
1. Stop the transcript watcher process
2. Prevent all future hook events from being sent for this session
3. Delete all session data from the server (event streams and read models)

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly. Use `kapacitor recap --repo` to list recent session IDs in this repository.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Notes

- This action is irreversible — all session data will be permanently deleted from the server.
- The local transcript file (Codex rollout `.jsonl`) is not affected — only server-side data is removed.
- Subsequent hooks (session-end, etc.) will be silently skipped.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
```

- [ ] **Step 3: Create `kapacitor-validate-plan/SKILL.md`**

`mkdir -p kapacitor/codex-skills/kapacitor-validate-plan` then write:

```markdown
---
name: kapacitor-validate-plan
description: >-
  This skill should be used when the user asks to "validate plan",
  "verify plan", "check plan completion", "did I finish everything",
  "is the plan done", "what's left to do", "validate my work",
  or wants to verify that all planned items were completed.
---

# Kapacitor Validate Plan

Verify that all items in the current session's plan have been completed.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor validate-plan

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor validate-plan <sessionId>
```

## What It Returns

The command outputs three sections:

- **`## Plan`** — the full plan text.
- **`## What's Done`** — two sub-sections:
  - **Summary** — AI-generated summary of what was accomplished (from `WhatsDoneGenerated` events, if available).
  - **Details** — list of files created (`Write`) and modified (`Edit`) during the session.
- **`## Instructions`** — asks you to compare the plan against the summary and file list.

## What To Do With The Output

1. Read the plan carefully and identify each distinct planned item.
2. Compare each item against the summary and file list under "What's Done".
3. If all items are complete, confirm to the user that the plan is fully implemented.
4. If there are gaps, list the missing items and complete them now.

## When No Plan Is Found

If the output says "No plan found for this session", inform the user that no plan was detected for this session. Plans are recorded when a session emits an `ExitPlanMode`-style event; not every session has one.

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
```

- [ ] **Step 4: Confirm Codex sees the files (no test rig, just shape)**

```bash
ls -la kapacitor/codex-skills/
# Expect: kapacitor-disable, kapacitor-errors, kapacitor-hide, kapacitor-recap, kapacitor-validate-plan
find kapacitor/codex-skills -name SKILL.md | wc -l
# Expect: 5
```

- [ ] **Step 5: Re-run the unit suite (now exercises the real five-folder layout indirectly via the array test)**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add kapacitor/codex-skills/kapacitor-hide kapacitor/codex-skills/kapacitor-disable kapacitor/codex-skills/kapacitor-validate-plan
git commit -m "feat(skills): add hide/disable/validate-plan Codex skills

Ports the three Claude session-management skills (session-hide,
session-disable, validate-plan) to Codex with auto-resolved session
ID via CODEX_THREAD_ID.

Closes the user-visible parity gap with Claude Code skills.

Part of AI-676."
```

---

## Task 7: Update the two existing Codex `SKILL.md` files

**Spec reference:** §2.2.

The existing `kapacitor-recap` and `kapacitor-errors` skills treat manual session ID as the path of least resistance. After auto-resolve, the current session is the natural entry point. Rewrite the lead paragraphs and primary usage examples accordingly.

**Files:**
- Modify: `kapacitor/codex-skills/kapacitor-recap/SKILL.md`
- Modify: `kapacitor/codex-skills/kapacitor-errors/SKILL.md`

- [ ] **Step 1: Update `kapacitor-recap/SKILL.md`**

Replace the entire file with:

```markdown
---
name: kapacitor-recap
description: >-
  Use when the user asks to "read a previous session", "get session history",
  "recap session", "what happened in session X", "load context from a previous
  session", "continue from session", "what did we do last time", "catch me up",
  "summarize session", "what have we been working on", "recent changes",
  "recent sessions", or references prior work in this repo. Retrieves session
  summaries recorded by Kurrent Capacitor via the `kapacitor recap` CLI.
---

# Kapacitor Recap

Retrieve session history recorded by Kurrent Capacitor. Codex CLI 0.81+ exports `CODEX_THREAD_ID`; `kapacitor recap` uses it the same way it uses `KAPACITOR_SESSION_ID` for Claude. No args needed for the current session.

## Usage

Run the `kapacitor recap` CLI via the shell. Do NOT call the HTTP API directly — the CLI handles formatting, error handling, and server URL resolution.

```bash
# Current session — auto-resolved from CODEX_THREAD_ID
kapacitor recap

# Recent session summaries for the current repository (discovery)
kapacitor recap --repo

# A specific session by ID (from --repo output)
kapacitor recap <sessionId>

# Full transcript for a specific session
kapacitor recap --full <sessionId>

# Full continuation chain (all linked sessions, oldest first)
kapacitor recap --chain <sessionId>

# Both: full transcript across all chained sessions
kapacitor recap --chain --full <sessionId>
```

## Default Output (Summary)

Shows the plan (if any) and an AI-generated summary with:
- **Context** — why the work was done.
- **Key decisions** — trade-offs and design choices that matter for future work.
- **Unfinished/Risks** — anything deferred or left incomplete.

If no summary is available (e.g., active session), a hint is shown to use `--full`.

## Repository Recap (`--repo`)

Returns AI-generated summaries from the most recent ended sessions in the current git repository. Each entry includes the session title, date, summary, and the session ID needed to drill in further.

**Use this for discovery — not as the default entry point:**
- The user says "what have we been working on recently?"
- The user references prior work ("recently we implemented X")
- You need context about other sessions in this repo, not the current one

**Progressive disclosure:** Start with `kapacitor recap` (current session) for in-session work. Switch to `--repo` when you need cross-session context. Drill into a specific summary with `kapacitor recap --full <sessionId>`.

## Full Output (`--full`)

The complete transcript with these section types:

- **`## User Prompt`** — what the user asked.
- **`## Assistant`** — text responses.
- **`## Plan`** — plans that were created (Claude Code sessions only).
- **`## Write <path>`** — files that were created (with syntax-highlighted content).
- **`## Edit <path>`** — files that were edited (with diff content).

When using `--chain`, sessions are separated by `# Session <id>` headers, and agent activity appears under `### Agent (<type>)` sub-headers.

## When to Use Each Flag

- **No flag** (`kapacitor recap`) — quick context on the current session.
- **`--repo`** — recent session summaries across the repo.
- **`<sessionId>`** — quick context on a specific session (from `--repo` output).
- **`--full <sessionId>`** — when you need exact prompts, responses, or file contents.
- **`--chain <sessionId>`** — understanding the full history of a task that spanned multiple sessions.
- **`--chain --full <sessionId>`** — complete transcript across all continuations.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).

## Tips

- Start with `kapacitor recap` (no args) for the active session, then `--repo` for cross-session discovery.
- Summarize key decisions and changes for the user rather than echoing the full recap output verbatim.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints "Session not found" and exits with code 1.
- If not in a git repository (for `--repo`), the command prints an error and exits with code 1.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
```

- [ ] **Step 2: Update `kapacitor-errors/SKILL.md`**

Replace the entire file with:

```markdown
---
name: kapacitor-errors
description: >-
  Use when the user asks to "show errors", "extract errors", "what went wrong",
  "find tool errors", "review errors from session", "check session errors",
  "list failures", "what failed in session X", "error report", "show mistakes",
  or wants to review tool call errors from a recorded session. Extracts tool
  errors via the `kapacitor errors` CLI.
---

# Kapacitor Errors

Extract tool call errors from a session recorded by Kurrent Capacitor. The output lists each failed tool call — shell commands, file reads/writes, agent delegations, etc. — along with the error message and the tool that caused it.

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; `kapacitor errors` uses it the same way it uses `KAPACITOR_SESSION_ID` for Claude. No args needed for the current session.

## Usage

```bash
# Current session — auto-resolved from CODEX_THREAD_ID
kapacitor errors

# Errors from the full continuation chain of the current session
kapacitor errors --chain

# Errors from a specific session
kapacitor errors <sessionId>

# Errors from the full continuation chain starting at a session
kapacitor errors --chain <sessionId>
```

## Output Format

Each error is printed as a block with:

- **Session ID** and optional **agent ID** (if the error occurred in a subagent).
- **Event number** and **timestamp**.
- **Tool name** — the tool that failed (e.g., Bash, Read, Edit, Write, Grep, Glob).
- **Error message** — the error output or failure reason.

When using `--chain`, errors from all sessions in the continuation chain are included, ordered chronologically.

## When to Use Each Flag

- **No flag** (`kapacitor errors`) — reviewing errors from the current session.
- **`<sessionId>`** — reviewing errors from a specific session (e.g. from `kapacitor recap --repo`).
- **`--chain`** — reviewing errors across a full task that spanned multiple sessions.

## Practical Applications

- **Post-mortem review** — after finishing a session, identify recurring mistakes and update project rules (CLAUDE.md / AGENTS.md) with avoidance guidance.
- **Debugging** — quickly find what went wrong in a session without scrolling through the full timeline.
- **Pattern detection** — use `--chain` across a multi-session task to spot repeated error patterns (e.g., wrong file paths, incorrect API usage).

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).

## Tips

- After extracting errors, look for patterns: the same tool failing repeatedly, or the same type of mistake across sessions.
- Propose concrete avoidance rules based on the errors found — these can be added to AGENTS.md / CLAUDE.md.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints an error and exits with code 1.
- If no errors are found, the command prints "No errors found." — this is a good outcome.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
```

- [ ] **Step 3: Verify both files parse cleanly (frontmatter intact)**

```bash
head -8 kapacitor/codex-skills/kapacitor-recap/SKILL.md
head -8 kapacitor/codex-skills/kapacitor-errors/SKILL.md
```

Expected: each begins with `---` then `name:` then `description:` then `---`. No stray whitespace.

- [ ] **Step 4: Commit**

```bash
git add kapacitor/codex-skills/kapacitor-recap/SKILL.md kapacitor/codex-skills/kapacitor-errors/SKILL.md
git commit -m "docs(skills): drop manual-session-id guidance from recap/errors

CODEX_THREAD_ID now auto-resolves the active session, so the
two-step ‘list with --repo, then re-invoke’ dance is no longer
the default. --repo demoted from entry point to discovery flow.

Part of AI-677."
```

---

## Task 8: Help text updates (9 resource files)

**Spec reference:** §3.4.

All nine edits are mechanical text changes. One task, one commit.

**Files:**
- Modify: `src/Kapacitor.Cli.Core/Resources/help-hide.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-disable.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-recap.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-errors.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-validate-plan.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-eval.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-set-title.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-usage.txt`
- Modify: `src/Kapacitor.Cli.Core/Resources/help-plugin.txt`

- [ ] **Step 1: Update `help-hide.txt`**

Replace the file contents with:

```
kapacitor hide — Hide session from other users

Usage: kapacitor hide [sessionId]

Sets the current session's visibility to owner-only. Other users
will no longer see this session in the dashboard.

Arguments:
  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID
                          on Claude or CODEX_THREAD_ID on Codex 0.81+)
```

- [ ] **Step 2: Update `help-disable.txt`**

Replace with:

```
kapacitor disable — Stop recording and delete all session data

Usage: kapacitor disable [sessionId]

Stops the watcher, prevents future hook events from being sent,
and deletes all session data from the server (streams + read models).

Arguments:
  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID
                          on Claude or CODEX_THREAD_ID on Codex 0.81+)
```

- [ ] **Step 3: Update `help-recap.txt` line 11**

Change:

```
  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID)
```

to:

```
  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID
                          on Claude or CODEX_THREAD_ID on Codex 0.81+)
```

- [ ] **Step 4: Update `help-errors.txt` line 9**

Same edit as Step 3:

```
  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID
                          on Claude or CODEX_THREAD_ID on Codex 0.81+)
```

- [ ] **Step 5: Update `help-validate-plan.txt` line 6**

Same edit as Step 3.

- [ ] **Step 6: Update `help-eval.txt` lines 30-31**

Change:

```
  - If the session ID is omitted, the currently-recorded session (as set by
    the session-start hook in KAPACITOR_SESSION_ID) is used.
```

to:

```
  - If the session ID is omitted, the currently-recorded session is used —
    resolved from KAPACITOR_SESSION_ID (Claude) or CODEX_THREAD_ID
    (Codex 0.81+).
```

- [ ] **Step 7: Update `help-set-title.txt`**

Replace the entire file with:

```
kapacitor set-title — Set session title

Usage: kapacitor set-title <title>

Arguments:
  title                   Session title (max 120 characters)

Environment:
  Session ID is auto-resolved from KAPACITOR_SESSION_ID (Claude)
  or CODEX_THREAD_ID (Codex 0.81+).
```

- [ ] **Step 8: Update `help-usage.txt` line 67**

Find the existing environment-variable section (search for `KAPACITOR_SESSION_ID` near line 67 — the actual line number may shift slightly). The current line:

```
  KAPACITOR_SESSION_ID       Session ID (set automatically by SessionStart hook)
```

becomes two lines:

```
  KAPACITOR_SESSION_ID       Session ID set by the Claude Code SessionStart hook
  CODEX_THREAD_ID            Session ID exported by Codex CLI 0.81+
```

- [ ] **Step 9: Update `help-plugin.txt` lines 13-16**

Change:

```
Notes:
  --codex installs hooks (~/.codex/hooks.json or <repo>/.codex/hooks.json with
  --project) plus kapacitor-recap and kapacitor-errors skills in
  ~/.codex/skills/. Skills are always user-wide; --project only affects hooks.
```

to:

```
Notes:
  --codex installs hooks (~/.codex/hooks.json or <repo>/.codex/hooks.json with
  --project) plus five skills in ~/.codex/skills/: kapacitor-recap,
  kapacitor-errors, kapacitor-hide, kapacitor-disable, and
  kapacitor-validate-plan. Skills are always user-wide; --project only
  affects hooks.
```

- [ ] **Step 10: Build and verify the resource bundle still loads**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj`
Expected: Build succeeds. Sanity-spot-check by running `dotnet run --project src/Kapacitor.Cli/Kapacitor.Cli.csproj -- hide --help` (or equivalent help command — adjust to the real `--help` plumbing if `hide --help` isn't supported, e.g. `dotnet run -- help hide`).

- [ ] **Step 11: Run unit tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: All pass.

- [ ] **Step 12: Commit**

```bash
git add src/Kapacitor.Cli.Core/Resources/help-*.txt
git commit -m "docs(cli): help text names CODEX_THREAD_ID alongside KAPACITOR_SESSION_ID

Updates hide/disable to advertise the optional [sessionId] positional;
recap/errors/validate-plan/eval/set-title/usage to name both env vars;
plugin to enumerate all five Codex skills.

Part of AI-676 and AI-677."
```

---

## Task 9: README updates

**Spec reference:** §5. CLAUDE.md mandates README sync on any user-facing CLI surface change.

**Files:**
- Modify: `README.md` — Codex section (around line 250-260, the `#### Hosted Codex agents` block and the install paragraph above it), and the per-command rows under `## CLI commands` for `hide` and `disable` (search for the existing `Plan validation` section starting at line 133 — the new sections go near there).

- [ ] **Step 1: Add the auto-resolve note to the Codex section**

Find the existing line near line 247:
> Hosted Codex agents require the Codex hook surface — if you said yes during `kapacitor setup`, you already have it.

Above the `kapacitor plugin install --codex` block, add:

```markdown
Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kapacitor reads it the same way it reads `KAPACITOR_SESSION_ID` for Claude sessions — no manual session ID needed for any of the Codex skills (`kapacitor-recap`, `kapacitor-errors`, `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan`).
```

- [ ] **Step 2: Add a Codex skills enumeration block**

After the `kapacitor plugin install --codex` examples in the same section, add:

```markdown
Installing with `--codex` writes five skills under `~/.codex/skills/`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kapacitor-recap` | `kapacitor recap` | Session summary / continuation chain / repo history |
| `kapacitor-errors` | `kapacitor errors` | Tool-call error extraction |
| `kapacitor-hide` | `kapacitor hide` | Mark session owner-only |
| `kapacitor-disable` | `kapacitor disable` | Stop recording + delete server data |
| `kapacitor-validate-plan` | `kapacitor validate-plan` | Verify plan items were completed |

All five auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session.
```

- [ ] **Step 3: Add hide/disable command examples to `## CLI commands`**

After the `### Plan validation` block (around line 145), add two new subsections:

```markdown
### Hide session

Mark a session as owner-only so other users no longer see it in the dashboard.

```bash
kapacitor hide                 # current session
kapacitor hide <sessionId>     # specific session
```

### Disable recording

Stop the watcher, silence future hooks, and delete server-side data for a session.

```bash
kapacitor disable                 # current session
kapacitor disable <sessionId>     # specific session
```

This is irreversible on the server side; the local transcript file is untouched.
```

(Place after Plan validation, before the next major section. Use the existing heading style — three hashes.)

- [ ] **Step 4: Build the CLI once more (no behavior change, just sanity)**

Run: `dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj`
Expected: Build succeeds; no warnings.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs(readme): document Codex skill parity and CODEX_THREAD_ID resolution

Adds an auto-resolve note plus a five-skill enumeration table to the
Codex section. Adds hide/disable command examples to CLI commands.

Part of AI-676."
```

---

## Task 10: AOT publish verification + final smoke test

**Spec reference:** §8 (Acceptance).

CLAUDE.md flags this: AOT trimming/dynamic-code warnings only surface on publish, not on debug build. Plus the user-visible acceptance criteria need a manual run-through.

- [ ] **Step 1: AOT publish for the current platform**

Run:
```bash
dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release 2>&1 | tee /tmp/aot-publish.log
```

Expected: Build succeeds.

- [ ] **Step 2: Grep for trimming/AOT warnings**

Run:
```bash
grep -E 'IL[23][01][0-9]{2}' /tmp/aot-publish.log || echo "no AOT warnings"
```

Expected: `no AOT warnings`. If any appear, **STOP** — investigate (CLAUDE.md is explicit: these don't show in `dotnet build`). The likely culprits would be the new test code (which isn't AOT'd) or JSON serialization paths in `PluginCommand` — but no JSON change was made in this plan, so a regression would be unexpected.

- [ ] **Step 3: Install the fresh CLI into PATH for smoke testing**

```bash
cp src/Kapacitor.Cli/bin/Release/net10.0/*/publish/Kapacitor.Cli ~/.local/bin/kapacitor.smoke
# macOS only:
codesign --force --sign - ~/.local/bin/kapacitor.smoke
```

- [ ] **Step 4: Smoke test the install command**

```bash
~/.local/bin/kapacitor.smoke plugin install --codex
ls ~/.codex/skills/
```

Expected: Five folders present — `kapacitor-recap`, `kapacitor-errors`, `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan`.

- [ ] **Step 5: Smoke test resolver in an active Codex session**

Start a fresh Codex CLI session (with hooks already installed) and ask:

```
Run kapacitor recap with no arguments and tell me the session title.
```

Expected: Codex invokes `kapacitor recap`, gets back the current session's summary, and reads back the title. No "session not found" or env-var error.

Repeat for the four other skills (`kapacitor errors`, `kapacitor hide`, `kapacitor disable`, `kapacitor validate-plan`) — invoke each via a natural-language ask. For `hide` and `disable`, do this in a throwaway session so you don't accidentally hide a real session.

- [ ] **Step 6: Smoke test the error path**

In a non-Codex shell (no `CODEX_THREAD_ID`, no `KAPACITOR_SESSION_ID`):

```bash
unset KAPACITOR_SESSION_ID CODEX_THREAD_ID
~/.local/bin/kapacitor.smoke hide
```

Expected: Exits with code 1 and prints:
```
Usage: kapacitor hide [sessionId]
  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.
```

- [ ] **Step 7: Clean up the smoke binary**

```bash
rm ~/.local/bin/kapacitor.smoke
```

- [ ] **Step 8: Final review commit (if anything was tweaked during smoke testing)**

If Steps 4-6 surfaced any wording or behavior tweaks, commit them now with a single `chore: smoke-test fixups` commit. If everything passed cleanly, **no commit** — this task is a verification gate, not new code.

---

## Self-review summary

**Spec coverage:**
- §1 architecture overview → Tasks 1 (resolver), 2/3 (callers), 5 (plugin), 6 (new skills), 7 (updated skills) ✓
- §2.1 new SKILL.md files → Task 6 ✓
- §2.2 updated SKILL.md files → Task 7 ✓
- §3.1 resolver + set-title bullet → Tasks 1, 3 ✓
- §3.2 hide/disable positional → Task 2 ✓
- §3.4 help text (9 files) → Task 8 ✓
- §4.1 PluginCommand array + preflight → Task 5 ✓
- §5 README → Task 9 ✓
- §6 testing → Tasks 1 (resolver tests), 5 (plugin tests). Integration tests for hide/disable described in §6.3 are deferred to a follow-up (the wire path is unchanged; smoke-tested in Task 10 Step 5) ✓
- §7.1 probe → Task 0 ✓
- §8 acceptance → Task 10 ✓

**Placeholder scan:** No TBDs, no "implement later", no "add appropriate X". Each step includes the literal code or text to write.

**Type/signature consistency:** Resolver returns `string?` everywhere; both `ResolveSessionId` and `ResolveSessionIdFromEnv` agreed across Tasks 1, 2, 3, 4. `CodexSkillNames` is `internal static readonly string[]` consistently in Task 5 and the test in §6.2. Error strings are identical across Tasks 2 and 4.

**Deferred (called out in spec §6.3, not blocking acceptance):** WireMock.Net integration tests for `hide`/`disable` positional ID landing on the HTTP wire. The HTTP shape didn't change in this plan; manual smoke testing in Task 10 Step 5 covers it for now. Add to a follow-up if regression risk warrants it.
