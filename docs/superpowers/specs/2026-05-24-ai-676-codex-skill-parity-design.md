# AI-676 / AI-677 — Codex skill parity and auto-resolved session ID

**Status:** design proposed, pending implementation plan
**Linear:** [AI-676](https://linear.app/kurrent/issue/AI-676), [AI-677](https://linear.app/kurrent/issue/AI-677)
**Milestone:** Closed public preview

## 1. Architecture overview

Two changes are bundled into one spec because they share the same audience (Codex CLI users) and the same milestone, and shipping AI-676 alone would require SKILL.md files documenting a workaround that AI-677 immediately reverses.

**Skill parity (AI-676).** Three new SKILL.md files under `kapacitor/codex-skills/` — `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan` — mirror the existing Claude Code skills `session-hide`, `session-disable`, `validate-plan`. Registered in `PluginCommand.CodexSkillNames` so `kapacitor plugin install --codex` copies them into `~/.codex/skills/`. The setup wizard and README enumerate all five Codex skills.

**Auto-resolve (AI-677).** Extend the session-ID resolution chain used by `recap`, `errors`, `validate-plan`, `eval`, `hide`, `disable`, and `set-title` to read `CODEX_THREAD_ID` directly from the environment. `eval` benefits incidentally because it already calls `ResolveSessionId` (Program.cs:140); `set-title` benefits via a small env-only sibling helper (§3.1) because its positional is `<title>`, not `<sessionId>`. No marker file, no SessionStart write path. **This supersedes the marker-file fallback sketched in AI-677**, which proposed `~/.cache/kapacitor/codex-sessions/<id>`. `CODEX_THREAD_ID` was added to Codex CLI in [openai/codex#10096](https://github.com/openai/codex/pull/10096) (merged 2026-02-03) and ships in Codex CLI 0.81+. Local Codex (0.133.0) has it. The marker-file design is documented in §7.1 as the fallback if the day-1 probe finds the env and the hook-payload `session_id` diverge.

Resolution chain (single place — `ArgParsing.ResolveSessionId`):

```
1. positional <sessionId>           explicit override (returned verbatim)
2. KAPACITOR_SESSION_ID, dashless    Claude Code hook-set
3. CODEX_THREAD_ID, dashless         Codex CLI-exported (0.81+)
4. null → caller prints actionable error message
```

Normalization caveat — only **env-sourced** values are dash-stripped; **positional** values are returned verbatim. This preserves the existing behavior documented at README.md:125 and README.md:153 where the positional argument accepts a session GUID *or* a meta-session slug (slugs contain hyphens that are semantically meaningful). Today, individual callers strip dashes after the env fallback (e.g. Program.cs:305 / 354 / 504); after this change, normalization lives in the resolver for env-sourced values only.

**CLI surface change (consequence of bundling).** `kapacitor hide` and `kapacitor disable` today read `KAPACITOR_SESSION_ID` directly and fail without it. They grow an optional positional `sessionId` arg, mirroring `recap` / `errors` / `validate-plan`, and route through `ArgParsing.ResolveSessionId`. Without this change the three new SKILL.md files would document a `KAPACITOR_SESSION_ID=<id> kapacitor hide` shell prefix as a workaround, which is exactly what AI-677 exists to eliminate.

## 2. Skill files

### 2.1 New SKILL.md files (3)

Each new file is a Codex-flavored mirror of its Claude counterpart with two adjustments: it drops the "must be inside Claude Code session" framing, and it documents both auto-resolve paths under **Requirements**.

- `kapacitor/codex-skills/kapacitor-hide/SKILL.md` — wraps `kapacitor hide`.
- `kapacitor/codex-skills/kapacitor-disable/SKILL.md` — wraps `kapacitor disable`.
- `kapacitor/codex-skills/kapacitor-validate-plan/SKILL.md` — wraps `kapacitor validate-plan`.

Trigger phrases (the skill `description` frontmatter) are copied verbatim from the Claude skills — they're the user-visible activation surface and need no Codex-specific phrasing. Skill bodies follow the same shape as the existing `kapacitor-recap` / `kapacitor-errors` files: usage block, requirements block, notes/tips, error handling.

Body template for the three new skills (illustrative, `kapacitor-hide`):

```
## Usage
kapacitor hide                  # auto-resolves the active session
kapacitor hide <sessionId>      # explicit override (from `kapacitor recap --repo`)

## Requirements
Run inside an active Codex CLI session (0.81+) — the active session is
auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its
ID explicitly. Use `kapacitor recap --repo` to list recent session IDs in
this repository.
```

### 2.2 Updated SKILL.md files (2)

Both existing Codex skills today treat `--repo` as the entry point (kapacitor-recap/SKILL.md:21: *"the recommended entry point is the **repository recap**"*) because there was no way to recap the current session without a manual session ID. After auto-resolve lands, the current session *is* the natural starting point. Both files need a usage and decision-flow rewrite, not just a sentence deletion.

- `kapacitor/codex-skills/kapacitor-recap/SKILL.md`:
  - **Promote `kapacitor recap` (no args) to the primary usage example.** Place it first in the Usage block, above `kapacitor recap --repo`.
  - **Rewrite the opening paragraph.** Remove the "Codex sessions do not export `KAPACITOR_SESSION_ID`" framing. Replace with: *"Codex CLI 0.81+ exports `CODEX_THREAD_ID`; `kapacitor recap` uses it the same way it uses `KAPACITOR_SESSION_ID` for Claude. No args needed for the current session."*
  - **Demote `--repo` from "entry point" to "discovery"** in the "Use this when" list: keep its existing utility ("what have we been working on recently?", drilling into other sessions) but remove language that implies it must be the first call.
  - The default-output / full-output / chain sections are unchanged.
- `kapacitor/codex-skills/kapacitor-errors/SKILL.md`:
  - **Promote `kapacitor errors` (no args) to the primary usage example.** Today the SKILL.md says explicit ID is required; flip that.
  - **Drop the "you must pass the session ID explicitly" sentence**, replace with the same one-line auto-resolve note used in `kapacitor-recap`.
  - When invoked with no args, the skill now operates on the current Codex session by default, matching the Claude `session-errors` skill's UX exactly.

No shared template or generation step. Five files are well within the cost of plain markdown editing, and each SKILL.md is user-readable docs that benefits from being self-contained.

## 3. CLI changes

### 3.1 Centralize session-ID resolution

`src/Kapacitor.Cli/ArgParsing.cs:10` — `ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null)` already walks the args array and skips boolean / value-bearing flags before falling back to `KAPACITOR_SESSION_ID` at line 28. The signature is preserved exactly; only the env-fallback tail gains a third source. Existing callers (`recap`, `errors`, `validate-plan`, `eval` — see Program.cs:90, 110, 123, 140) need no change beyond keeping their current invocations.

```csharp
internal static string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) {
    // ... existing positional walk unchanged (returns verbatim if a positional is found) ...

    return ResolveSessionIdFromEnv();
}

internal static string? ResolveSessionIdFromEnv() {
    var kapacitor = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID");
    if (!string.IsNullOrWhiteSpace(kapacitor))
        return kapacitor.Replace("-", "");

    var codex = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
    if (!string.IsNullOrWhiteSpace(codex))
        return codex.Replace("-", "");

    return null;
}
```

`set-title` (§3.1 set-title bullet) and any future env-only caller use `ResolveSessionIdFromEnv()` directly. Arg-bearing commands continue to call `ResolveSessionId(args, ...)`. Both paths share the same env precedence and, if the §7.1 fallback ever activates, the same marker-read shim.

Behavior delta against today:

- **Positional values are returned verbatim** (no `.Replace("-", "")`). Matches today's behavior and preserves meta-session slugs (README.md:125, 153). Callers that need a dashless GUID must normalize explicitly — but for positionals, that's already not appropriate.
- **Env values are dash-stripped** in the resolver. Today, individual callers do this after the resolver returns. After this change, `hide`/`disable` (which previously read the env directly and stripped dashes inline at Program.cs:305 / 354) no longer need to strip — the resolver returns the already-normalized value when it sourced from env.
- **`CODEX_THREAD_ID` is appended as the last env fallback.** Order matters: `KAPACITOR_SESSION_ID` wins because Claude-side hooks set it deliberately to the active Capacitor session ID, whereas `CODEX_THREAD_ID` is exported globally by Codex regardless of whether the Capacitor watcher is attached.

Caller migration:

- `src/Kapacitor.Cli/Program.cs:305` (`hide`) — replace inline env read with `ResolveSessionId(args)`. Today `hide` takes no positional, so the resolver walks zero positionals and returns the env fallback. Adding the positional in §3.2 is then a one-line UX improvement; the resolver call itself is unchanged.
- `src/Kapacitor.Cli/Program.cs:354` (`disable`) — same.
- The error messages at sites 94, 114, 127, 146, 308, 357 collapse into one shared string: *"No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+."* Today the codebase has two near-duplicate forms; both get replaced.
- **`set-title` (Program.cs:498 / 504 / 507) uses a sibling helper, not the args resolver.** Its positional is `<title>` (multi-word, joined via `string.Join(' ', args.Skip(1))` at line 513), not `<sessionId>`. Running `args` through `ResolveSessionId` would interpret the title as a session ID. Instead, factor the env-fallback tail (the two `Environment.GetEnvironmentVariable` reads + dash-strip) into an **`internal static` helper** `ArgParsing.ResolveSessionIdFromEnv()` (it must be `internal`, not `private`, because `set-title` lives in `Program.cs` — different file, same assembly). `ResolveSessionId` calls it internally so the chains stay synchronised; `set-title` calls it directly. The helper has the same precedence as the env tail of the main resolver — `KAPACITOR_SESSION_ID` then `CODEX_THREAD_ID`, each dash-stripped, `null` if both unset.

  Error wording for `set-title` is **not** the shared "Pass one explicitly" message — `set-title` accepts no `<sessionId>` positional, so telling the user to pass one would be misleading. Tailored wording:

  - **Missing title** (Program.cs:498 branch): rewrite to `"Usage: kapacitor set-title <title>"` only. Drop the *"KAPACITOR_SESSION_ID must be set"* line at 500 — it confuses the diagnosis (a user with the env var set but no title shouldn't be told the env var is missing).
  - **Missing session env** (Program.cs:507 branch): replace *"KAPACITOR_SESSION_ID not set"* with *"No session ID found in `KAPACITOR_SESSION_ID` or `CODEX_THREAD_ID`. Run `set-title` inside an active Claude Code / Codex CLI 0.81+ session."*

  This keeps chain semantics consistent across **all** session-id-needing commands without forcing `set-title` to pretend its positional is a sessionId.

### 3.2 `hide` and `disable` accept an optional positional `sessionId`

Today both commands read the env var directly (Program.cs:305, 354) and exit with "KAPACITOR_SESSION_ID not set" otherwise. Replace the env-var read with `ArgParsing.ResolveSessionId(args)` — same shape used by `recap` (Program.cs:110), `errors` (Program.cs:90), and `validate-plan` (Program.cs:123). The resolver walks `args` from `skipCount: 1`, skips flags (none for these two commands today), and returns the positional if present or the env fallback otherwise. No `args[1]` indexing.

The positional is optional. If both positional and env are absent, the resolver returns `null` and the caller emits the shared error message. No other behavior change — HTTP calls, exit codes, and idempotency are unchanged.

### 3.3 Out of scope for the CLI layer

`kapacitor codex-hook` and the watcher already receive the session ID via the hook payload directly, not via env. They do not call `ResolveSessionId` and need no change.

### 3.4 Help text

Nine resource files in `src/Kapacitor.Cli.Core/Resources/`:

- **`help-hide.txt`** — add a `sessionId` positional row; rewrite the "set automatically" wording to "auto-resolved from `KAPACITOR_SESSION_ID` (Claude) or `CODEX_THREAD_ID` (Codex 0.81+)".
- **`help-disable.txt`** — same edit.
- **`help-recap.txt`** — extend the existing `(defaults to KAPACITOR_SESSION_ID)` note to also name `CODEX_THREAD_ID`.
- **`help-errors.txt`** — same.
- **`help-validate-plan.txt`** — same.
- **`help-eval.txt`** — update the env-var sentence (currently line 31: *"the session-start hook in KAPACITOR_SESSION_ID"*) to name both env vars.
- **`help-set-title.txt`** — update the env-var line (currently *"Session ID (required)"* with the implicit env name) to spell out both env sources. `set-title` reads them via the `ResolveSessionIdFromEnv` sibling helper introduced in §3.1, so the docs and behavior are aligned.
- **`help-usage.txt`** — list `CODEX_THREAD_ID` alongside `KAPACITOR_SESSION_ID` in the env-var section (line 67 today).
- **`help-plugin.txt`** — line 14-16 currently reads *"`--codex` installs hooks (...) plus kapacitor-recap and kapacitor-errors skills"*. Rewrite to enumerate all five skill names.

## 4. Installer and setup wizard updates

### 4.1 `PluginCommand.cs`

Append the three new names to `CodexSkillNames` (line 14) **and promote its access modifier from implicit `private` to `internal`** so the test assembly can value-check it (`InternalsVisibleTo Include="Kapacitor.Cli.Tests.Unit"` is already set in `Kapacitor.Cli.csproj:17`):

```csharp
internal static readonly string[] CodexSkillNames = [
    "kapacitor-recap",
    "kapacitor-errors",
    "kapacitor-hide",
    "kapacitor-disable",
    "kapacitor-validate-plan"
];
```

`InstallCodexSkills` (line 322) iterates the array; `RemoveCodexSkills` (line 351) iterates the same array. Upgrades and removals get parity automatically. The existing wholesale-replace semantics in `InstallCodexSkills` (`Directory.Delete(dst, recursive: true)` before copy, line 335) means upgraders with stale folders get refreshed cleanly, and foreign user-installed skills are untouched.

**Bug fix bundled in — preflight validation.** `InstallCodexSkills` line 328-331 silently `continue`s when a name in `CodexSkillNames` has no matching folder under `sourceDir`. This is the wrong shape for a contract that promises "install all five skills" — a packaging error that omits a folder would currently report success and leave the user with a partial install. **And** simply returning `false` mid-loop is worse: by then the loop has already deleted prior target folders (line 335) and copied them, so a mid-loop bail leaves the install in a half-replaced state.

The fix is **preflight, then act**:

1. Walk `CodexSkillNames` once and verify every name exists under `sourceDir`.
2. If any are missing, write a single `Console.Error` line naming all missing folders and return `false` immediately. Target dir is untouched.
3. Only after the preflight passes, run the existing delete-then-copy loop.

This keeps the all-or-nothing install contract intact and gives `RemoveCodexSkills` no new responsibility (removal stays best-effort, since a folder that doesn't exist there is already a no-op at line 358).

### 4.2 `CodingAgentsStep.cs`

No code changes. It already dispatches to `installers.InstallCodexSkills(src, paths.CodexSkillsDir)` (line 60), which walks the array above. The user-facing prompt text at line 90 and the success line at line 67 do not enumerate skill names and stay accurate.

### 4.3 No new skill registration with Codex

Codex auto-discovers skill folders under `~/.codex/skills/` — the existing two skills rely on this mechanism. The three new skills use the same path.

## 5. README updates

CLAUDE.md mandates README sync on any CLI surface change. Three edits:

1. **Codex section enumerates all five skills by name.** Currently the README mentions Claude skills by name (`/kapacitor:session-recap`, `/kapacitor:validate-plan`) but glosses over Codex skills generically. Add a short block under the Codex install paragraph listing all five Codex skill names alongside what each does, so users discover that `hide`/`disable`/`validate-plan` parity exists.
2. **Auto-resolve note.** Add one sentence to the Codex setup section: *"Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kapacitor reads it the same way it reads `KAPACITOR_SESSION_ID` for Claude sessions — no manual session ID needed."*
3. **`hide` / `disable` command examples in `## CLI commands`.** Show both forms:
   ```bash
   kapacitor hide                 # current session
   kapacitor hide <sessionId>     # specific session
   ```
   Same for `disable`.

Quick-start section is untouched — the getting-started flow does not exercise these commands.

## 6. Testing

The bar is moderate. This is plumbing, not new business logic.

### 6.1 `ArgParsing.ResolveSessionId` precedence (unit)

Extend the existing `test/Kapacitor.Cli.Tests.Unit/ArgParsingTests.cs` (8 tests today) with new cases covering:

- Positional wins over both env vars.
- `KAPACITOR_SESSION_ID` wins over `CODEX_THREAD_ID`.
- `CODEX_THREAD_ID` is used when both args and `KAPACITOR_SESSION_ID` are unset.
- **Positional is returned verbatim** — a positional `foo-bar` is *not* dash-stripped (covers the meta-session slug case from README.md:153).
- **Env values are dash-stripped** — `KAPACITOR_SESSION_ID=abc-def` resolves to `abcdef`; same for `CODEX_THREAD_ID`.
- All three sources unset returns `null`.

**Test isolation.** The existing tests at ArgParsingTests.cs:54 / 70 use `[NotInParallel(nameof(KapacitorSessionIdEnvVar))]` where the constant evaluates to the literal `"KAPACITOR_SESSION_ID"`. The new `CODEX_THREAD_ID` tests must serialize against the same lock — TUnit's `NotInParallel` keys are string-identity-compared. Two acceptable approaches; pick whichever produces less churn:

- **Rename the group constant.** Replace `KapacitorSessionIdEnvVar` with a `SessionEnvVarMutation` constant evaluating to a stable literal (e.g. `"SessionEnvVarMutation"`), update all existing tests using it, and use that same constant for the new `CODEX_THREAD_ID` tests. Cleaner long-term name; touches the existing tests.
- **Reuse the existing literal**: pass `[NotInParallel("KAPACITOR_SESSION_ID")]` on the new tests. Zero churn but the group name is technically misleading for `CODEX_THREAD_ID` tests.

Recommendation: rename. The existing constant is private to `ArgParsingTests` and the rename is mechanical.

Do **not** use the `HomeEnvVarMutation` group from `PluginCommandCodexTests.cs:310` — that's for HOME-mutation, an orthogonal concern.

### 6.2 `PluginCommand` skill registration (unit)

- Extend `PluginCommandCodexTests.InstallCodexSkills_copies_known_skills` (line 197) to assert all five names land in `target/`.
- Add `CodexSkillNames_contains_expected_five` — a one-shot value check that the `CodexSkillNames` array equals the five expected names. Cheap; catches the case where someone reorders the array or drops a name. A disk-walk variant would require resolving the repo root from a test runner's working directory, which is brittle — value check is enough.
- **Add `InstallCodexSkills_returns_false_when_known_folder_missing`** — covers the bug fix from §4.1. Write only four of the five expected folders into the temp source dir, call `InstallCodexSkills`, assert it returns `false`, that the error names the missing folder, and crucially that **no target subfolder was created and no pre-existing target folder was deleted** (preflight-not-partial-install). Without this test the silent-skip regression can return, or a future "fix" can revert to mid-loop bail and leave half-installed state.

Existing `_preserves_foreign_skills` and `_overwrites_existing_kapacitor_skill` tests cover the install/remove semantics generically — no per-skill duplication needed.

### 6.3 `hide` / `disable` accept positional ID (integration)

Light integration tests against WireMock.Net (already in use per CLAUDE.md). Confirm the positional ID reaches the HTTP call. Skip exhaustive coverage of the verbs — the HTTP wire is unchanged from today.

### 6.4 Out of automated scope

- SKILL.md content. Markdown read by humans, not asserted by tests. The existing `InstallCodexSkills_copies_known_skills` test already proves install plumbing transfers the file faithfully.
- Manual smoke test on a real Codex CLI 0.133.0+ session — included in the implementation plan as the AI-676 acceptance check, not the spec.

## 7. Risks, fallback, and out-of-scope

### 7.1 Single real risk: `CODEX_THREAD_ID` vs hook-payload `session_id`

Whether `CODEX_THREAD_ID` (env var exported by Codex) equals the `session_id` field Codex passes in hook payloads, after normalization. PR #10096's description ("so that the agent (and skills) can refer to the current thread / session ID") implies they match, but there is no formal contract from OpenAI.

**Mitigation — day-1 verification (implementation-plan task, not spec):**

Add a diagnostic log line to `CodexHookCommand.HandleSessionStart`:

```csharp
Console.Error.WriteLine(
    $"[probe] CODEX_THREAD_ID={Environment.GetEnvironmentVariable("CODEX_THREAD_ID")} "
    + $"payload.session_id={TryGetString(node, "session_id")}");
```

Run one real Codex session, compare values. Decision gate before writing `ArgParsing.ResolveSessionId`.

- **If they match (expected outcome):** ship as designed. Remove probe line.
- **If they differ:** fall back to a marker-file design. By construction, `CODEX_THREAD_ID` is then **not** a valid Capacitor session ID, so the chain must *replace* the direct Codex env step with a marker read — keeping the direct read would shadow the marker and return a known-bad ID. `CodexHookCommand.HandleSessionStart` writes `~/.cache/kapacitor/codex-sessions/<CODEX_THREAD_ID>` containing the resolved Capacitor session ID (the one Codex passes in the hook payload, server-acknowledged). The marker read lives in **`ArgParsing.ResolveSessionIdFromEnv()`** (the sibling helper introduced in §3.1), *not* directly in `ResolveSessionId` — that way both arg-bearing commands (via `ResolveSessionId` which delegates to the env helper) and `set-title` (which calls the env helper directly) pick up the marker fallback uniformly. Under this fallback branch the env helper becomes:

  `KAPACITOR_SESSION_ID` → marker file keyed by `CODEX_THREAD_ID` → null/error

  (The primary design — used when the probe confirms `CODEX_THREAD_ID == session_id` — keeps `KAPACITOR_SESSION_ID` → `CODEX_THREAD_ID` direct → null/error.) Sweep at next `SessionStart` with a 7-day cutoff to bound directory growth. The marker code is ~30 lines and slots into the env helper — no spec rewrite.
- **If `CODEX_THREAD_ID` is unset (older Codex):** chain falls through to step 4. The error message names the version requirement, giving an actionable hint.

### 7.2 Out of scope

Tracked elsewhere or deferred:

- **Daemon-spawned Codex agents** (`KAPACITOR_DAEMON_URL` set). They receive the session ID via `KAPACITOR_AGENT_ID` and the daemon's launch env. The resolution chain needs no new branch for them.
- **Cross-machine session resolution** (shared-daemon setups). Out per AI-677's "Out of scope".
- **Active marker cleanup via Codex `SessionEnd`.** Not needed under the env-var path. Under the marker fallback, the SessionStart sweep is sufficient — Codex's `Stop` hook fires per-turn, not at session end, so it's not a reliable cleanup signal.
- **Cross-vendor session linking** (one Capacitor session spanning Claude and Codex turns). Out per AI-677.

## 8. Acceptance

Derived from AI-676 and AI-677:

- `kapacitor plugin install --codex` writes all five skills under `~/.codex/skills/`. If any source folder is missing, install **fails atomically** — no target subfolder is created and no prior target folder is deleted.
- On a fresh Codex CLI 0.81+ session, invoking each of `kapacitor-recap`, `kapacitor-errors`, `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan` with no arguments resolves the active session and runs the right CLI command.
- `kapacitor hide` / `kapacitor disable` succeed when invoked with either an explicit `<sessionId>` or with `CODEX_THREAD_ID` set in the environment.
- Old behavior preserved: invocations inside Claude Code still resolve via `KAPACITOR_SESSION_ID`.
- Error path: with no positional and neither env var set, all five commands print the shared error message naming both Claude Code and Codex CLI 0.81+ as supported environments.
- **Incidental beneficiaries** (no new acceptance tests, but verified by the shared resolver behaviour): `kapacitor eval` and `kapacitor set-title` now also resolve `CODEX_THREAD_ID` when invoked from a Codex session. `eval` because it already calls `ArgParsing.ResolveSessionId` at Program.cs:140; `set-title` because of the new `ResolveSessionIdFromEnv` sibling helper (§3.1). Help text for both is updated accordingly (§3.4).
