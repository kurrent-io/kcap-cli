# AI-648 — Codex Stop hook becomes a turn-end no-op

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Linear:** [AI-648](https://linear.app/kurrent/issue/AI-648/codex-stop-hook-treats-turn-end-as-session-end-breaking-multi-turn) (related: [AI-638](https://linear.app/kurrent/issue/AI-638/codex-session-start-hook-produces-noise) — already implemented on this branch).

**Goal:** Stop treating Codex's `Stop` hook as session-end. Reduce `HandleStop` to a turn-end liveness check so Codex's per-turn `Stop` doesn't kill the watcher and falsely mark sessions ended after turn 1.

**Architecture:** Codex fires `Stop` at the end of *every* turn. The existing `HandleStop` kills the watcher and POSTs `/hooks/session-end/codex` per turn, which mismarks multi-turn sessions as ended on turn 1. The watcher's AI-647 parent-exit path (`WatchCommand.PostSessionEndOnParentExitAsync`) already fires session-end when codex actually exits — that becomes the *only* session-end signal. `HandleStop` is reduced to: call `EnsureWatcherRunning` as a liveness safety net (symmetric with Claude's `stop`/`notification` branch in `Program.cs`), emit `{"continue":true}` to stdout for the Codex hook parser, return 0.

**Tech Stack:** .NET 10 NativeAOT, TUnit on Microsoft Testing Platform, WireMock.Net for HTTP mocking.

---

## File Structure

**Modify:**
- `src/kapacitor/Commands/CodexHookCommand.cs` — rewrite `HandleStop`; delete now-dead `ShouldSpawnWhatsDone` helper; update route-mapping doc comment (lines 12–24).
- `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` — invert `Stop_maps_to_session_end_codex_route` (line 63), update `Stop_with_numeric_session_id_returns_zero_without_crash` comment (line 327), delete `ShouldSpawnWhatsDone_*` tests (lines 308–324).

**Untouched (intentional):**
- `src/kapacitor/Commands/WatchCommand.cs` — AI-647 parent-exit path is already correct; no change needed.
- `src/kapacitor/WatcherManager.cs` — `InlineDrainAsync` and `SpawnWhatsDoneGenerator` are still used by Claude / WatchCommand / AgentOrchestrator; keep.
- Server (`/Users/alexey/dev/eventstore/kapacitor-server`) — `HandleSessionEnd` is already idempotent and handles `reason: "parent_exited"`; no server change required.
- `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs` — hosted-agent session-end already flows through `EndAgentSessionAsync`, independent of `/hooks/session-end/codex`.

---

## Task 1: Invert the Stop test (TDD red)

**Files:**
- Modify: `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs:55–106`

The current test asserts that Stop POSTs to `/hooks/session-end/codex`. After the fix, Stop must *not* POST there. We write the inverted test first so we see it fail against the current implementation.

- [ ] **Step 1: Rename and rewrite the test**

Replace lines 55–106 of `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` with:

```csharp
    // AI-648: Codex 'Stop' fires at turn end, not session end. The hook must
    // NOT POST /hooks/session-end/codex (that path is reserved for the
    // watcher's parent-exit fallback in WatchCommand.cs). It must still emit
    // {"continue":true} on stdout to satisfy Codex's stop-hook JSON parser
    // (AI-635 invariant) and must NOT leak WatcherManager chatter to stdout.
    //
    // Globally sequential: this test captures Console.Out for the duration.
    // NotInParallel with no group key forces it to run on its own to avoid
    // interleaving with other stdout-mutating tests under TUnit's scheduler.
    [Test, NotInParallel]
    public async Task Stop_is_turn_end_no_op_and_does_not_post_session_end() {
        // Stub the route so any (incorrect) POST is recorded — we assert zero.
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "Stop",
              "session_id": "abc",
              "transcript_path": "/tmp/rollout.jsonl",
              "cwd": "/tmp"
            }
            """;

        var originalOut  = Console.Out;
        var stdoutWriter = new StringWriter();
        try {
            Console.SetOut(stdoutWriter);

            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);

            // Core invariant: Stop must NOT POST session-end.
            var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
            await Assert.That(endRequests.Count).IsEqualTo(0);

            // Defensive: also no other related routes.
            var wrong1 = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
            var wrong2 = _server.FindLogEntries(Request.Create().WithPath("/hooks/codex/stop").UsingPost());
            await Assert.That(wrong1.Count).IsEqualTo(0);
            await Assert.That(wrong2.Count).IsEqualTo(0);

            // AI-635 invariant: valid JSON object on stdout, no chatter.
            var stdout = stdoutWriter.ToString();
            var doc    = JsonDocument.Parse(stdout);
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
            await Assert.That(stdout.Contains("Watcher ")).IsFalse();
            await Assert.That(stdout.Contains("Inline drain")).IsFalse();
            await Assert.That(stdout.Contains("Spawned")).IsFalse();
        } finally {
            Console.SetOut(originalOut);
        }
    }
```

- [ ] **Step 2: Run the test, confirm it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/Stop_is_turn_end_no_op_and_does_not_post_session_end"
```

Expected: FAIL on `await Assert.That(endRequests.Count).IsEqualTo(0)` — current `HandleStop` POSTs once.

- [ ] **Step 3: Commit (red state)**

```bash
git add test/kapacitor.Tests.Unit/CodexHookCommandTests.cs
git commit -m "$(cat <<'EOF'
test(AI-648): invert codex Stop assertion to forbid session-end POST

Codex fires Stop at turn end. The hook must not POST /hooks/session-end/codex;
that POST is the watcher's parent-exit responsibility (AI-647).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Rewrite `HandleStop` as turn-end no-op (TDD green)

**Files:**
- Modify: `src/kapacitor/Commands/CodexHookCommand.cs:103–154`

Replace `HandleStop` and remove `ShouldSpawnWhatsDone` (now dead — only `HandleStop` called it; verified by grep over `src/`).

- [ ] **Step 1: Replace `HandleStop`**

In `src/kapacitor/Commands/CodexHookCommand.cs`, replace the current `HandleStop` (lines 103–137) and the `ShouldSpawnWhatsDone` block (lines 139–154) with:

```csharp
    static async Task<int> HandleStop(string baseUrl, JsonNode node) {
        // Codex 'Stop' fires at every turn end, NOT session end. Session-end
        // is fired by the watcher's parent-PID monitor in WatchCommand.cs
        // (AI-647) when the codex process actually exits — that path POSTs
        // /hooks/session-end/codex with reason: "parent_exited" and handles
        // generate_whats_done. Treating Stop as session-end here would kill
        // the watcher after turn 1 and mismark multi-turn sessions as ended
        // before they actually finish.
        //
        // Symmetric with Claude's stop/notification branch in Program.cs —
        // we just keep the watcher alive in case it crashed mid-session.
        var sessionId      = TryGetString(node, "session_id");
        var transcriptPath = TryGetString(node, "transcript_path");
        var cwd            = TryGetString(node, "cwd");

        if (sessionId is not null && transcriptPath is not null) {
            await WatcherManager.EnsureWatcherRunning(
                baseUrl, sessionId, transcriptPath,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex"
            );
        }

        // AI-635: Codex's stop-hook output parser rejects empty stdout as
        // "invalid stop hook JSON output". Emit the schema default explicitly.
        Console.Write(SessionScopedOutputJson);
        return 0;
    }
```

- [ ] **Step 2: Update the route-mapping doc comment**

Replace the `<remarks>` block at `src/kapacitor/Commands/CodexHookCommand.cs:12–24`:

```csharp
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → no server POST. Codex fires Stop at every turn end,
///                       not session end (AI-648). Session-end is owned by the
///                       watcher's parent-PID monitor (AI-647 — see
///                       WatchCommand.PostSessionEndOnParentExitAsync).
///                       HandleStop only refreshes watcher liveness and emits
///                       {"continue":true} so Codex's hook parser is satisfied.
///   PermissionRequest → in a daemon-launched hosted agent (KAPACITOR_DAEMON_URL set), bounce
///                       through the daemon's LocalPermissionBridge and wait for the dashboard's
///                       decision (fail-closed on bridge errors: deny + exit nonzero). Otherwise:
///                       POST /hooks/permission-record (fire-and-forget; CLI emits no decision so
///                       Codex's normal in-CLI approval prompt takes over).
///   UserPromptSubmit  → swallowed (v1 — neither vendor consumes them)
///   PreToolUse        → swallowed
///   PostToolUse       → swallowed
/// </remarks>
```

- [ ] **Step 3: Run the new test, confirm it passes**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/Stop_is_turn_end_no_op_and_does_not_post_session_end"
```

Expected: PASS.

- [ ] **Step 4: Run the full CodexHookCommandTests class**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/*"
```

Expected: Most tests pass. The `ShouldSpawnWhatsDone_*` tests will fail with "no such method `ShouldSpawnWhatsDone`" — that's expected and fixed in Task 3. `Stop_with_numeric_session_id_returns_zero_without_crash` will pass — null session_id still short-circuits before the `EnsureWatcherRunning` call.

- [ ] **Step 5: Commit (green state, partial)**

```bash
git add src/kapacitor/Commands/CodexHookCommand.cs
git commit -m "$(cat <<'EOF'
feat(AI-648): make codex Stop hook a turn-end no-op

Codex fires Stop at every turn end, not session end. Treating it as
session-end killed the watcher after turn 1 and mismarked multi-turn
sessions as ended.

HandleStop now only refreshes watcher liveness (symmetric with Claude's
stop branch in Program.cs) and emits the AI-635 JSON output. Session-end
flows exclusively through the watcher's AI-647 parent-PID monitor.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Delete dead `ShouldSpawnWhatsDone` tests and update numeric-session-id test comment

**Files:**
- Modify: `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs:308–340`

- [ ] **Step 1: Delete `ShouldSpawnWhatsDone_ParsesGenerateFlag` and `ShouldSpawnWhatsDone_NullBody_ReturnsFalse`**

Remove lines 308–324 from `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` (the `[Test] [Arguments(...)]` test and the null-body test). The helper they exercise no longer exists.

- [ ] **Step 2: Update the comment on `Stop_with_numeric_session_id_returns_zero_without_crash`**

Replace the comment at `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs:326–340` (or wherever it ends up after Step 1's deletion) with the AI-648-aware version. The body stays — we still want a regression guard that a non-string session_id doesn't crash.

Find the test and replace its comment block:

```csharp
    // Fix #3 / AI-648: non-string session_id in a Stop payload must not crash.
    // session_id falls to null via the safe TryGetString helper, so HandleStop
    // short-circuits before EnsureWatcherRunning. No server POST is expected
    // (AI-648 made Stop a turn-end no-op), but we still stub /hooks/session-end/codex
    // so a regression that reintroduces the POST surfaces as a test failure
    // via the WireMock log assertion below.
    [Test]
    public async Task Stop_with_numeric_session_id_returns_zero_without_crash() {
        var payload = """{"hook_event_name": "Stop", "session_id": 12345, "transcript_path": "/tmp/r.jsonl"}""";

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        // AI-648: Stop must never POST session-end, even with a malformed payload.
        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);
    }
```

- [ ] **Step 3: Run the full CodexHookCommandTests class**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/*"
```

Expected: PASS — all CodexHookCommandTests green.

- [ ] **Step 4: Commit**

```bash
git add test/kapacitor.Tests.Unit/CodexHookCommandTests.cs
git commit -m "$(cat <<'EOF'
test(AI-648): drop dead ShouldSpawnWhatsDone tests, tighten Stop numeric-id test

ShouldSpawnWhatsDone was only used by HandleStop, which no longer POSTs
session-end. Delete the helper's tests and add an explicit AI-648 guard
to the numeric-session-id test asserting Stop never POSTs session-end.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Full verification

- [ ] **Step 1: Run the entire unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass. Total should match the 750 from the baseline minus the 2 deleted `ShouldSpawnWhatsDone_*` tests, minus 0 added (the inverted Stop test replaces, not adds), so 748 succeeded / 0 failed. If TUnit reports otherwise, investigate before proceeding.

- [ ] **Step 2: Run the relevant integration tests**

`WatcherParentExitPostTests` covers the AI-647 path that now owns codex session-end exclusively. `HookRoundTripTests` exercises hook→server flows.

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj -- --treenode-filter "/*/*/WatcherParentExitPostTests/*"
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj -- --treenode-filter "/*/*/HookRoundTripTests/*"
```

Expected: both pass.

- [ ] **Step 3: Verify AOT publish has no IL3050/IL2026 warnings**

CLAUDE.md mandates this check on every CLI change because trimming warnings only surface on `publish`, not `build`.

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output (no warnings). If anything matches, investigate before proceeding.

- [ ] **Step 4: Verify the noise fix for AI-638 is still in place**

This branch already contains the AI-638 fix (two stderr writes removed from `WatcherManager.cs`). Confirm nothing regressed:

```bash
grep -n "Spawned watcher\|not running, respawning" src/kapacitor/WatcherManager.cs
```

Expected: no matches.

---

## Task 5: Update README / docs if needed

CLAUDE.md flags this as a recurring miss. The change is to internal hook handling — no new CLI command, no new flag, no changed default behavior visible at the CLI surface. So in principle README needs no update. But:

- [ ] **Step 1: Search README and help text for any Stop/session-end claims that this change invalidates**

```bash
grep -n -i "codex.*stop\|stop hook\|session.end\|session end" README.md src/Kapacitor.Core/Resources/help-*.txt
```

- [ ] **Step 2: If matches describe per-turn session-end behavior, update them. Otherwise skip.**

Expected: probably no user-facing doc changes needed (the README documents CLI commands, not internal hook routing). If you do find a stale reference, update it inline and stage with the rest of the changes.

- [ ] **Step 3: If docs were touched, commit**

```bash
git add README.md src/Kapacitor.Core/Resources/help-*.txt
git commit -m "$(cat <<'EOF'
docs(AI-648): align README/help with codex Stop turn-end semantics

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

If no doc changes, skip this commit.

---

## Task 6: PR

The branch already has the AI-638 noise-fix commit on it. We're shipping AI-648 + AI-638 together as the issue intended.

- [ ] **Step 1: Push the branch and open a PR**

The Linear branch name is `alexeyzimarev/ai-648-codex-stop-hook-treats-turn-end-as-session-end-breaking` (from the issue's `gitBranchName`), but if work is already on a different branch, push that branch instead — Linear auto-links via the PR description.

```bash
git push -u origin HEAD
```

- [ ] **Step 2: Open PR via gh**

PR title must start with the Linear ID per project convention (memory: `feedback_linear_in_pr_title.md`).

```bash
gh pr create --title "[AI-648] Codex Stop hook becomes a turn-end no-op (+ AI-638 noise fix)" --body "$(cat <<'EOF'
## Summary

- **AI-648:** `HandleStop` no longer kills the watcher or POSTs `/hooks/session-end/codex`. Codex's `Stop` fires at every turn end, not session end — the previous behavior killed the watcher after turn 1 and mismarked multi-turn sessions as ended. Session-end now flows exclusively through the AI-647 watcher parent-exit path (`PostSessionEndOnParentExitAsync`), which already handles `generate_whats_done` and is idempotent on the server side.
- **AI-638:** Drop two informational stderr writes in `WatcherManager` (`Spawned watcher for ...`, `Watcher ... not running, respawning...`) that Codex surfaces as "hook context" noise in the TUI. Error paths preserved.

Both touch the same hook surface and are bundled to avoid two adjacent PRs.

## Why this is safe

- Server's `HandleSessionEnd` is idempotent (read-model fast-path + post-drain stream-tail re-check) and accepts `reason: "parent_exited"` for codex.
- Server-side trivial-session cleanup (`DeleteTrivialSessionAsync`) fires regardless of which client POSTs session-end.
- Hosted codex agents run their own session-end via `AgentOrchestrator.EndAgentSessionAsync` — the hook's POST was redundant.

## Test plan

- [ ] Unit suite: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj` passes.
- [ ] Integration: `WatcherParentExitPostTests`, `HookRoundTripTests` pass.
- [ ] AOT publish: no IL3050/IL2026 warnings.
- [ ] Manual smoke: run a real codex session locally with the new hooks installed. Confirm:
  - No "hook context: Watcher ... not running, respawning..." line on session start.
  - After turn 2 ends, dashboard still shows the session Active (was Ended on turn 1 previously).
  - On codex exit, dashboard marks the session Ended within ~5s, and `generate_whats_done` fires if applicable.
EOF
)"
```

- [ ] **Step 3: Return the PR URL**

The `gh pr create` output prints the URL — capture and surface it.

---

## Self-Review

- **Spec coverage:** AI-648 description's "Fix" section enumerates four removals + one addition (`EnsureWatcherRunning` safety net) + one preserved invariant (`{"continue":true}` stdout). Task 2 covers all six. Test plan in the issue lists three items — the inverted Stop test (Task 1), multi-turn smoke (Task 6 manual), `generate_whats_done` verification (Task 4 integration). Covered.
- **Placeholder scan:** Each code step shows the full replacement code. The README task explicitly says "skip if no matches" rather than mandating an edit, so no placeholder.
- **Type consistency:** `EnsureWatcherRunning` signature in Task 2 matches the actual definition (`agentId, sessionIdOverride, cwd, skipTitle, vendor` — see `WatcherManager.cs:178–187` post-AI-638-fix). `SessionScopedOutputJson` is the existing constant on line 31 of `CodexHookCommand.cs` — no need to redefine. `TryGetString` is the existing private helper at line 275 — reused.
- **Branch state assumption:** The plan assumes the working branch already contains the AI-638 noise-fix commit. If executing on a different branch where that hasn't landed, also apply the two edits to `src/kapacitor/WatcherManager.cs` lines 91 and 193 (remove the two `Console.Error.WriteLineAsync` calls) before Task 1.
