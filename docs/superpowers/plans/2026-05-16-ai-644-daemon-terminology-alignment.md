# AI-644 Daemon Terminology Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename every "agent"-as-daemon reference (CLI command group, help file, state directory, log file, C# types) to "daemon", leaving "agent" only where it legitimately means a hosted Claude/Codex coding-agent session. Clean slate — no migration shims.

**Architecture:** This is a mechanical rename PR. The work is partitioned along commit-friendly boundaries: each task ends with a green build + green tests + a commit. Order matters because some renames cascade (renaming `AgentLockPaths` forces updates in 6 callers; deleting `AgentLockMigration` forces removing 3 call sites). Tasks are ordered so the tree compiles after every commit.

**Tech Stack:** .NET 10, NativeAOT, TUnit (Microsoft Testing Platform), embedded resources for help text.

**Source spec:** `docs/superpowers/specs/2026-05-16-ai-644-daemon-terminology-alignment-design.md`

---

## File Structure

**Renamed files (git mv):**
- `src/Kapacitor.Core/AgentLockPaths.cs` → `src/Kapacitor.Core/DaemonLockPaths.cs`
- `src/kapacitor/Commands/AgentCommands.cs` → `src/kapacitor/Commands/DaemonCommands.cs`
- `src/Kapacitor.Core/Resources/help-agent.txt` → `src/Kapacitor.Core/Resources/help-daemon.txt`
- `test/kapacitor.Tests.Unit/AgentLockPathsTests.cs` → `test/kapacitor.Tests.Unit/DaemonLockPathsTests.cs`
- `test/kapacitor.Tests.Unit/AgentLockEnumerationTests.cs` → `test/kapacitor.Tests.Unit/DaemonLockEnumerationTests.cs`

**Deleted files:**
- `src/Kapacitor.Core/AgentLockMigration.cs`
- `test/kapacitor.Tests.Unit/AgentLockMigrationTests.cs`

**Modified files (in-place edits):**
- `src/kapacitor/Program.cs` — offline-commands list, dispatch case
- `src/Kapacitor.Daemon/DaemonRunner.cs` — drop `AgentLockMigration.MigrateLegacyFiles` call, rename `AgentLockPaths` usages
- `src/Kapacitor.Daemon/DaemonLock.cs` — rename `AgentLockPaths` usages
- `src/Kapacitor.Core/DaemonNameResolver.cs` — rename `AgentLockPaths` usages (if any)
- `src/kapacitor/Commands/StatusCommand.cs` — rename `AgentLockPaths` usages, update comment text
- `src/kapacitor/Commands/SetupCommand.cs` — `kapacitor agent` → `kapacitor daemon` in user-facing strings
- `src/Kapacitor.Core/Resources/help-status.txt` — cross-references
- `src/Kapacitor.Core/Resources/help-usage.txt` — `agent <subcmd>` → `daemon <subcmd>` lines
- `src/Kapacitor.Core/Resources/help-repos.txt` — "agent daemon" → "daemon"
- `src/Kapacitor.Core/Resources/help-setup.txt` — wording fixes
- `README.md` — `### Agent daemon` heading, every `kapacitor agent` example, `agents/` path reference
- `npm/kapacitor/README.md` — two `kapacitor agent` lines

**Coding-agent terminology preserved (NO changes):**
- `--max-agents` / `MaxAgents` / `daemon.max_agents`
- `KAPACITOR_AGENT_ID`, `agent_host_id`
- `AgentOrchestrator`, `AgentDetector`, `CodingAgentsStep`
- `--agent-id` flag on `watch`, `subagent-start`/`subagent-stop` events
- `subagents/agent-{id}.jsonl` transcript path

---

## Task 1: Rename `AgentLockPaths` → `DaemonLockPaths` with `daemons/` directory

**Files:**
- Modify: `src/Kapacitor.Core/AgentLockPaths.cs` (rename file → `DaemonLockPaths.cs`, change directory constant)
- Modify: `test/kapacitor.Tests.Unit/AgentLockPathsTests.cs` (rename file → `DaemonLockPathsTests.cs`)
- Modify: `test/kapacitor.Tests.Unit/AgentLockEnumerationTests.cs` (rename file → `DaemonLockEnumerationTests.cs`)
- Modify (caller updates only): `src/Kapacitor.Daemon/DaemonRunner.cs`, `src/Kapacitor.Daemon/DaemonLock.cs`, `src/Kapacitor.Core/DaemonNameResolver.cs`, `src/kapacitor/Commands/StatusCommand.cs`, `src/kapacitor/Commands/AgentCommands.cs`, `src/Kapacitor.Core/AgentLockMigration.cs`

- [ ] **Step 1: Add a test that locks in the `daemons/` directory name**

Open `test/kapacitor.Tests.Unit/AgentLockPathsTests.cs` and add this test at the bottom of the class:

```csharp
[Test]
public async Task Directory_LivesUnderDaemonsFolder() {
    // Verify the production layout (no test override) uses the renamed
    // ~/.config/kapacitor/daemons/ path, not the pre-AI-644 agents/ path.
    await Assert.That(AgentLockPaths.Directory.Replace('\\', '/'))
        .EndsWith("/.config/kapacitor/daemons");
}
```

- [ ] **Step 2: Run the new test to verify it fails**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/AgentLockPathsTests/Directory_LivesUnderDaemonsFolder*"`

Expected: FAIL — assertion shows the path currently ends with `/agents`.

- [ ] **Step 3: Rename the production file and class**

```bash
git mv src/Kapacitor.Core/AgentLockPaths.cs src/Kapacitor.Core/DaemonLockPaths.cs
```

In `src/Kapacitor.Core/DaemonLockPaths.cs`:
- Change class name: `public static partial class AgentLockPaths` → `public static partial class DaemonLockPaths`
- Change the directory path: `.config", "kapacitor", "agents"` → `.config", "kapacitor", "daemons"`
- Update doc comment: `~/.config/kapacitor/agents/` → `~/.config/kapacitor/daemons/`
- Update the XML doc on `LockPath` summary text from "agent daemon" if present (search-and-fix any "agent daemon" / "agent" wording in this file).
- Update the XML doc on `EnumerateNames` that mentions `kapacitor agent doctor` → `kapacitor daemon doctor`
- Update the `OverrideDirectoryForTesting` XML doc text "redirects the agents directory" → "redirects the daemons directory"

- [ ] **Step 4: Update every caller of `AgentLockPaths`**

In each file below, replace `AgentLockPaths` with `DaemonLockPaths` (text replace, careful with comments too):
- `src/Kapacitor.Daemon/DaemonRunner.cs`
- `src/Kapacitor.Daemon/DaemonLock.cs`
- `src/Kapacitor.Core/DaemonNameResolver.cs`
- `src/kapacitor/Commands/StatusCommand.cs`
- `src/kapacitor/Commands/AgentCommands.cs`
- `src/Kapacitor.Core/AgentLockMigration.cs`

Quick sanity check after editing:

```bash
grep -rn 'AgentLockPaths' src/ test/ | grep -v 'bin/\|obj/'
```

Expected: only matches inside the test files we'll rename in Step 5.

- [ ] **Step 5: Rename the test files and class names**

```bash
git mv test/kapacitor.Tests.Unit/AgentLockPathsTests.cs test/kapacitor.Tests.Unit/DaemonLockPathsTests.cs
git mv test/kapacitor.Tests.Unit/AgentLockEnumerationTests.cs test/kapacitor.Tests.Unit/DaemonLockEnumerationTests.cs
```

In `DaemonLockPathsTests.cs`:
- Rename `public class AgentLockPathsTests` → `public class DaemonLockPathsTests`
- Replace every `AgentLockPaths.` call with `DaemonLockPaths.`
- Update XML doc reference `<see cref="AgentLockPaths"/>` → `<see cref="DaemonLockPaths"/>`

In `DaemonLockEnumerationTests.cs`:
- Rename `public class AgentLockEnumerationTests` → `public class DaemonLockEnumerationTests`
- Replace every `AgentLockPaths.` with `DaemonLockPaths.`
- Update the `[NotInParallel(...)]` lock key: `nameof(AgentLockPaths)` → `nameof(DaemonLockPaths)`
- Update XML doc reference `<see cref="AgentLockPaths.EnumerateNames"/>` → `<see cref="DaemonLockPaths.EnumerateNames"/>`
- Update the test comment that says "before AI-630 migration" — leave the AI-630 reference (it's still accurate history); only update the type name.
- Update the comment text `kapacitor agent doctor --clean` → `kapacitor daemon doctor --clean` if present.

- [ ] **Step 6: Run the full unit test suite**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`

Expected: All tests pass, including `Directory_LivesUnderDaemonsFolder`.

- [ ] **Step 7: Build to verify the daemon project still compiles**

Run: `dotnet build src/kapacitor/kapacitor.csproj src/Kapacitor.Daemon/Kapacitor.Daemon.csproj`

Expected: Build succeeded, 0 errors. (Warnings about unused `using` are fine — leave them; we'll clean them at the end.)

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: rename AgentLockPaths to DaemonLockPaths, move dir to daemons/

State directory moves from ~/.config/kapacitor/agents/ to
~/.config/kapacitor/daemons/. No migration — pre-public daemon, dogfood
users stop their running daemons before installing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Delete `AgentLockMigration`

**Files:**
- Delete: `src/Kapacitor.Core/AgentLockMigration.cs`
- Delete: `test/kapacitor.Tests.Unit/AgentLockMigrationTests.cs`
- Modify: `src/Kapacitor.Daemon/DaemonRunner.cs:125` (remove call site)
- Modify: `src/kapacitor/Commands/AgentCommands.cs:65,141` (remove call sites)

- [ ] **Step 1: Remove the call sites**

In `src/Kapacitor.Daemon/DaemonRunner.cs`, delete the line:

```csharp
AgentLockMigration.MigrateLegacyFiles(config.Name);
```

(Around line 125 — verify by searching for `AgentLockMigration.MigrateLegacyFiles`. If there's a preceding comment block describing the migration, delete the comment too.)

In `src/kapacitor/Commands/AgentCommands.cs`, delete both call sites (around line 65 and line 141):

```csharp
AgentLockMigration.MigrateLegacyFiles(name);
```

Also delete any `// Migrate legacy agent.pid → agents/<name>.pid` comment immediately preceding each call.

- [ ] **Step 2: Delete the migration source and tests**

```bash
git rm src/Kapacitor.Core/AgentLockMigration.cs
git rm test/kapacitor.Tests.Unit/AgentLockMigrationTests.cs
```

- [ ] **Step 3: Verify no stragglers**

Run: `grep -rn 'AgentLockMigration' src/ test/ | grep -v 'bin/\|obj/'`

Expected: zero matches.

- [ ] **Step 4: Build and test**

```bash
dotnet build src/kapacitor/kapacitor.csproj src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: delete AgentLockMigration (pre-AI-630 singleton migration)

The migration moved a singleton agent.pid to the per-name agents/<name>.pid
layout. Since we're doing a clean-slate rename for AI-644 (agents/ →
daemons/) without bringing the directory contents with us, this migration
no longer has a use case and only carries dead "agent" terminology.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Rename `AgentCommands` → `DaemonCommands` and switch log file to `daemon.log`

**Files:**
- Modify: `src/kapacitor/Commands/AgentCommands.cs` (rename file → `DaemonCommands.cs`, class rename, `agent.log` → `daemon.log`)
- Modify: `src/kapacitor/Program.cs:230` (update dispatch target)

- [ ] **Step 1: Rename the file**

```bash
git mv src/kapacitor/Commands/AgentCommands.cs src/kapacitor/Commands/DaemonCommands.cs
```

- [ ] **Step 2: Rename the class and log file**

In `src/kapacitor/Commands/DaemonCommands.cs`:
- Rename `public class AgentCommands` (or `public static class AgentCommands`) → `DaemonCommands`
- Update the `LogPath` constant: `PathHelpers.ConfigPath("agent.log")` → `PathHelpers.ConfigPath("daemon.log")`

Do NOT change any other text yet (error messages, comments, usage strings) — those are Task 6. Keep this commit focused so reviewers can see the rename clearly.

- [ ] **Step 3: Update the dispatcher in Program.cs**

In `src/kapacitor/Program.cs`, around line 230:

```csharp
    case "agent":
        return await AgentCommands.HandleAsync(args);
```

Change to:

```csharp
    case "agent":
        return await DaemonCommands.HandleAsync(args);
```

(Leave `case "agent"` — we'll switch the command name in Task 4.)

- [ ] **Step 4: Build and test**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: rename AgentCommands to DaemonCommands, log to daemon.log

Pure type + filename rename plus log file path change. CLI command name
still "agent" — switched in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Switch CLI command name from `agent` to `daemon`

**Files:**
- Modify: `src/kapacitor/Program.cs:68` (offline-commands list)
- Modify: `src/kapacitor/Program.cs:229` (dispatch case)

- [ ] **Step 1: Update offline-commands list**

In `src/kapacitor/Program.cs`, around line 68:

```csharp
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update", "plugin", "profile", "use", "repos", "login"];
```

Replace `"agent"` with `"daemon"`:

```csharp
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "daemon", "setup", "status", "update", "plugin", "profile", "use", "repos", "login"];
```

- [ ] **Step 2: Update the dispatch case**

In `src/kapacitor/Program.cs`, around line 229:

```csharp
    case "agent":
        return await DaemonCommands.HandleAsync(args);
```

Change to:

```csharp
    case "daemon":
        return await DaemonCommands.HandleAsync(args);
```

- [ ] **Step 3: Build the CLI**

Run: `dotnet build src/kapacitor/kapacitor.csproj`

Expected: Build succeeded.

- [ ] **Step 4: Sanity-check the dispatch with the built CLI**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- daemon --help 2>&1 | head -5
```

Expected: prints `kapacitor daemon` help (currently still says "agent" inside — Task 5 fixes the help text). The important thing is that the dispatcher accepts `daemon` and not `agent`.

Then verify the old name is rejected:

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- agent --help 2>&1 | head -5
```

Expected: "Unknown command" or similar — confirms hard cutover.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: switch CLI command from `kapacitor agent` to `kapacitor daemon`

Hard cutover — no hidden alias. The daemon has not been publicly released
so this only breaks dogfood users who must learn the new command.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Rename `help-agent.txt` → `help-daemon.txt` and rewrite content

**Files:**
- Modify: `src/Kapacitor.Core/Resources/help-agent.txt` (rename → `help-daemon.txt`)

- [ ] **Step 1: Rename the help file**

```bash
git mv src/Kapacitor.Core/Resources/help-agent.txt src/Kapacitor.Core/Resources/help-daemon.txt
```

The help dispatcher at `src/kapacitor/Program.cs:970` loads `help-{cmd}.txt`, so this rename alone makes `kapacitor daemon --help` find the new file.

- [ ] **Step 2: Rewrite the help text**

Replace the entire content of `src/Kapacitor.Core/Resources/help-daemon.txt` with:

```
kapacitor daemon — Manage the daemon process

Usage: kapacitor daemon <subcommand>

Subcommands:
  start [-d]              Start the daemon (foreground, or -d for background)
  stop [--name N] [--yes] Stop a running daemon (prompts on multi unless --yes)
  status [--name N]       Show daemon status (lists all when --name omitted)
  logs                    Show recent daemon log output
  doctor [--clean]        Diagnose lock-file state, optionally clean stale entries

Options for start:
  --name <name>           Daemon name (defaults to OS username)
  --server-url <url>      Server URL
  --max-agents <n>        Max concurrent hosted coding agents (default: 5)
  --log-file <path>       Log to file instead of console
  -d, --detach            Run in background (logs to file automatically)

Options for stop:
  --name <name>           Stop just the named daemon. Without it, all daemons
                          are listed and the command asks for confirmation
                          before stopping them (unless --yes is passed).
  --yes, -y               Unattended mode: stop all running daemons without
                          prompting. Useful in cleanup scripts.

Options for doctor:
  --clean                 Remove stale entries (lock files whose holder is
                          gone). Held entries are never touched. Without
                          --clean, doctor reports state without modifying.

Notes:
  Each daemon holds an exclusive flock on ~/.config/kapacitor/daemons/<name>.lock
  for its entire lifetime. The kernel releases the lock automatically on
  process exit (including SIGKILL or power-off), so stale lock files on disk
  are never a blocker — only a live process holding the kernel-level lock
  can prevent another daemon from acquiring the same name.

  Two daemons with different --name values can run side-by-side. Two daemons
  under the same name on the same machine collide on the flock and the
  second one exits with code 2. If they somehow both connect to the server
  anyway, the server-side check rejects the second one with exit code 3.
```

Note the `--max-agents` line clarification — "Max concurrent hosted coding agents" makes the legitimate use of "agent" unambiguous.

- [ ] **Step 3: Verify help renders**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- daemon --help 2>&1
```

Expected: shows the new help text.

- [ ] **Step 4: Build**

Run: `dotnet build src/kapacitor/kapacitor.csproj`

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: rename help-agent.txt to help-daemon.txt, rewrite content

EmbeddedResources resolves help-{cmd}.txt by command name, so the rename
is enough to wire it to `kapacitor daemon --help`. Body rewritten to drop
"agent daemon"; --max-agents description clarifies it refers to hosted
coding-agent CLI sessions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Replace "agent" wording inside `DaemonCommands.cs`

**Files:**
- Modify: `src/kapacitor/Commands/DaemonCommands.cs`

Every user-visible string that says "agent" or "agent daemon" must be replaced. Internal variable names and comments where the *coding-agent* meaning applies should stay; everywhere else, replace.

- [ ] **Step 1: Replace user-visible "agent" strings**

In `src/kapacitor/Commands/DaemonCommands.cs`, apply these replacements:

| Find | Replace with |
|---|---|
| `kapacitor agent start --name {name}` | `kapacitor daemon start --name {name}` |
| `kapacitor agent stop --name {name}` | `kapacitor daemon stop --name {name}` |
| `kapacitor agent status --name {name}` | `kapacitor daemon status --name {name}` |
| `kapacitor agent doctor` | `kapacitor daemon doctor` |
| `kapacitor agent <start\|stop\|status\|logs\|doctor>` | `kapacitor daemon <start\|stop\|status\|logs\|doctor>` |
| `No agent daemons are running.` | `No daemons are running.` |
| `No agent daemon '{name}' running` | `No daemon '{name}' running` |
| `No agent daemon files found` | `No daemon files found` |
| `Start the agent daemon` | `Start the daemon` |
| `Stop a running agent daemon` | `Stop a running daemon` |
| `Show agent daemon status` | `Show daemon status` |
| `Max concurrent agents` | `Max concurrent hosted coding agents` |

For the XML doc comments around the start-lock and the usage method (lines ~43, ~516, ~541, ~599-610), also fix any "agent" → "daemon" where it refers to the daemon process itself. Read the doc comment in context — if it's talking about the daemon, rename; if it's about hosted coding agents (e.g. `--max-agents`), leave.

- [ ] **Step 2: Verify with grep**

```bash
grep -n 'agent' src/kapacitor/Commands/DaemonCommands.cs
```

Expected: zero matches for "agent" referring to the daemon. Any remaining matches must be in the `--max-agents` flag context, hosted-agent variable names, or `AgentOrchestrator`-related code.

- [ ] **Step 3: Build and test**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 4: Smoke-test the error paths**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- daemon status --name nope-does-not-exist 2>&1 | head -3
```

Expected: error message says "No daemon 'nope-does-not-exist' running" (or similar — depending on actual code path), NOT "No agent daemon".

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: rewrite user-facing text in DaemonCommands

Every error message, usage line, and doc comment that referred to the
daemon as an "agent" now says "daemon". --max-agents description
clarifies that it's the hosted-coding-agents limit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Update cross-references in other help files

**Files:**
- Modify: `src/Kapacitor.Core/Resources/help-status.txt`
- Modify: `src/Kapacitor.Core/Resources/help-usage.txt`
- Modify: `src/Kapacitor.Core/Resources/help-repos.txt`
- Modify: `src/Kapacitor.Core/Resources/help-setup.txt`

- [ ] **Step 1: Update `help-status.txt`**

Replace:
```
kapacitor status — Show server, auth, hook, and agent status
```
With:
```
kapacitor status — Show server, auth, hook, and daemon status
```

Replace:
```
  Agent      Whether the agent daemon is running (PID file check)
```
With:
```
  Daemon     Whether the daemon is running (PID file check)
```

- [ ] **Step 2: Update `help-usage.txt`**

Replace:
```
  status                           Show server, auth, and agent status
```
With:
```
  status                           Show server, auth, and daemon status
```

Replace these four lines (the `agent <subcmd>` listing):
```
  agent start [-d] [--name N]      Start agent daemon (foreground, or -d for background; refuses on duplicate name)
  agent stop [--name N] [--yes]    Stop a daemon (prompts on multi unless --yes)
  agent status [--name N]          Show agent daemon status (lists all when --name omitted)
  agent doctor [--clean]           Diagnose lock-file state, optionally clean stale entries
```
With:
```
  daemon start [-d] [--name N]     Start the daemon (foreground, or -d for background; refuses on duplicate name)
  daemon stop [--name N] [--yes]   Stop a daemon (prompts on multi unless --yes)
  daemon status [--name N]         Show daemon status (lists all when --name omitted)
  daemon doctor [--clean]          Diagnose lock-file state, optionally clean stale entries
```

- [ ] **Step 3: Update `help-repos.txt`**

Replace:
```
Manage known repository paths for the agent daemon launch dialog.
```
With:
```
Manage known repository paths for the daemon launch dialog.
```

- [ ] **Step 4: Review `help-setup.txt` (probably no changes needed)**

Open `src/Kapacitor.Core/Resources/help-setup.txt` and check the two lines that mention "agent" (around lines 17-18):

```
The setup wizard detects every supported coding agent on PATH (claude, codex)
and asks one yes/no per detected agent. Hooks are installed user-wide. For
```

These both refer to **coding agents** (Claude, Codex), not the daemon. Leave them unchanged.

Also verify line 7 already says `--daemon-name` (not `--agent-name`):

```
  --daemon-name <name>        Daemon name (skip prompt)
```

If correct, no edits to `help-setup.txt`.

- [ ] **Step 5: Verify with grep**

```bash
grep -n 'agent' src/Kapacitor.Core/Resources/help-status.txt src/Kapacitor.Core/Resources/help-usage.txt src/Kapacitor.Core/Resources/help-repos.txt
```

Expected: zero matches in `help-status.txt`, `help-usage.txt`, `help-repos.txt`.

- [ ] **Step 6: Build and test**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: update daemon-related cross-references in help files

help-status, help-usage, and help-repos all referenced "agent" or
"agent daemon" — rewritten to "daemon". help-setup left alone: its
"agent" references are to hosted coding agents and are correct.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Update remaining source-file text references

**Files:**
- Modify: `src/Kapacitor.Daemon/DaemonRunner.cs`
- Modify: `src/Kapacitor.Daemon/DaemonLock.cs`
- Modify: `src/Kapacitor.Core/DaemonNameResolver.cs`
- Modify: `src/kapacitor/Commands/StatusCommand.cs`
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

These files were touched in Task 1 to update `AgentLockPaths` → `DaemonLockPaths`. Now sweep for any **textual** "agent" references in comments, log messages, and user-visible strings that should be "daemon".

- [ ] **Step 1: Find and triage every remaining "agent" reference**

Run:

```bash
grep -n 'agent' src/Kapacitor.Daemon/DaemonRunner.cs src/Kapacitor.Daemon/DaemonLock.cs src/Kapacitor.Core/DaemonNameResolver.cs src/kapacitor/Commands/StatusCommand.cs src/kapacitor/Commands/SetupCommand.cs
```

For each match, classify:

- **Daemon-self reference** ("agent daemon", "agent lock", "the agent", `kapacitor agent ...`) → rewrite to "daemon" / `kapacitor daemon ...`
- **Coding-agent reference** (`AgentOrchestrator`, `--max-agents`, "coding agent", `KAPACITOR_AGENT_ID`, `agent_host_id`) → leave alone
- **AI-630 comments** mentioning the historical migration → can stay (history is accurate) but update file paths if they say `agents/` → `daemons/`

- [ ] **Step 2: Apply the rewrites**

Common patterns to fix:

| Find | Replace with |
|---|---|
| `kapacitor agent doctor` | `kapacitor daemon doctor` |
| `kapacitor agent stop` | `kapacitor daemon stop` |
| `kapacitor agent status` | `kapacitor daemon status` |
| `~/.config/kapacitor/agents/` | `~/.config/kapacitor/daemons/` |
| `agent.pid` (in comments only) | `<name>.pid` (since there's no singleton any more) |
| `the agent daemon` | `the daemon` |
| `agent lock` (the daemon's flock) | `daemon lock` |

In `src/kapacitor/Commands/StatusCommand.cs`, also check the comment at line 59 that refers to `~/.config/kapacitor/agents/` and update the path.

- [ ] **Step 3: Verify with a targeted grep**

```bash
grep -n 'kapacitor agent\b\|agent daemon\|kapacitor/agents/\|agent\.pid' src/Kapacitor.Daemon/DaemonRunner.cs src/Kapacitor.Daemon/DaemonLock.cs src/Kapacitor.Core/DaemonNameResolver.cs src/kapacitor/Commands/StatusCommand.cs src/kapacitor/Commands/SetupCommand.cs
```

Expected: zero matches.

- [ ] **Step 4: Build and test**

```bash
dotnet build src/kapacitor/kapacitor.csproj src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
AI-644: clean up remaining agent-as-daemon text in source files

Comments, log messages, and user-visible strings in DaemonRunner,
DaemonLock, DaemonNameResolver, StatusCommand, SetupCommand all use
"daemon" terminology now. Coding-agent references (--max-agents,
AgentOrchestrator) are intentionally preserved.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Update README.md and npm/kapacitor/README.md

**Files:**
- Modify: `README.md`
- Modify: `npm/kapacitor/README.md`

- [ ] **Step 1: Update `README.md` section heading and commands**

Around line 211, replace:
```
### Agent daemon
```
With:
```
### Daemon
```

Around line 213, the intro paragraph:
```
The agent daemon connects to the Capacitor server and runs Claude Code agents in isolated git worktrees, controlled from the dashboard. The daemon supports hosted Claude and Codex agents on macOS and Linux — choose the vendor from the dashboard's launch dialog.
```
Replace with:
```
The daemon connects to the Capacitor server and runs Claude Code or Codex agents in isolated git worktrees, controlled from the dashboard. The daemon supports hosted Claude and Codex agents on macOS and Linux — choose the vendor from the dashboard's launch dialog.
```

The command block (lines 216–224) — replace every `kapacitor agent` with `kapacitor daemon`:

```
kapacitor daemon start                   # start in foreground (defaults --name to your OS username)
kapacitor daemon start -d                # start in background (daemonize)
kapacitor daemon start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kapacitor daemon status                  # list all running daemons
kapacitor daemon status --name laptop    # show status of a specific daemon
kapacitor daemon stop --name laptop      # stop just that one
kapacitor daemon stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kapacitor daemon doctor                  # diagnose lock-file state for every daemon name
kapacitor daemon doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

Around line 227, update the path reference:
```
Each daemon process holds an exclusive `flock` on `~/.config/kapacitor/agents/<name>.lock`
```
Replace `agents/` with `daemons/`:
```
Each daemon process holds an exclusive `flock` on `~/.config/kapacitor/daemons/<name>.lock`
```

Around line 40, in the setup-wizard step-list:
```
5. **Agent daemon** — configure the daemon name for remote agent execution
```
Replace with:
```
5. **Daemon** — configure the daemon name for remote agent execution
```

The mentions on lines 39, 50, 52, 55, 71, 79, 93, 95, 147, 159 all refer to **coding agents** (Claude/Codex), not the daemon. Leave them alone. Spot-check by reading each in context.

The `#### Hosted Codex agents` heading on line 231 is correct ("agents" = coding agents). Leave it.

- [ ] **Step 2: Update `npm/kapacitor/README.md`**

Lines 24–25:
```
kapacitor agent start [-d]       Start agent daemon
kapacitor agent stop             Stop agent daemon
```
Replace with:
```
kapacitor daemon start [-d]      Start the daemon
kapacitor daemon stop            Stop the daemon
```

- [ ] **Step 3: Verify with grep**

```bash
grep -n 'kapacitor agent\b\|kapacitor/agents/' README.md npm/kapacitor/README.md
```

Expected: zero matches.

- [ ] **Step 4: Skim both READMEs in a viewer**

Render `README.md` (e.g. open in your editor's markdown preview or run `glow README.md`) and re-read the "Daemon" section + Getting Started step 4 to confirm nothing else says "agent daemon".

- [ ] **Step 5: Commit**

```bash
git add README.md npm/kapacitor/README.md
git commit -m "$(cat <<'EOF'
AI-644: update README and npm README for `kapacitor daemon`

Section heading "Agent daemon" → "Daemon", every `kapacitor agent`
example → `kapacitor daemon`, and the `agents/` path → `daemons/`.

CLAUDE.md flags README sync as a recurring miss — covered here.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Final verification

**Files:** none modified — verification only.

- [ ] **Step 1: Sanity grep — no stray daemon-as-agent terminology**

```bash
grep -rn 'kapacitor agent\b\|agent daemon\|kapacitor/agents/' src/ test/ npm/ README.md 2>/dev/null | grep -v 'bin/\|obj/'
```

Expected: zero matches. (If matches appear, they must be either fixed or proven to refer to legitimate coding-agent contexts — none should slip through.)

- [ ] **Step 2: AOT publish — CLI**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | tee /tmp/kapacitor-publish.log
grep -E 'IL[23][01][0-9]{2}' /tmp/kapacitor-publish.log
```

Expected: publish succeeds; grep returns zero matches (no IL3050/IL2026 warnings). Per CLAUDE.md, this is the only build that surfaces AOT warnings.

- [ ] **Step 3: AOT publish — daemon**

```bash
dotnet publish src/Kapacitor.Daemon/Kapacitor.Daemon.csproj -c Release 2>&1 | tee /tmp/kapacitor-daemon-publish.log
grep -E 'IL[23][01][0-9]{2}' /tmp/kapacitor-daemon-publish.log
```

Expected: publish succeeds; grep returns zero matches.

- [ ] **Step 4: Run unit tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all green.

- [ ] **Step 5: Run integration tests**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: all green. (If a test fails, do NOT assume it's pre-existing — CI requires all green to merge.)

- [ ] **Step 6: Manual smoke**

Use the freshly-built binary (from the AOT publish step) or `dotnet run` to round-trip a daemon:

```bash
# Pick a throwaway name to avoid colliding with your real daemon
DOTNET_TEST_NAME="ai644-smoke"

dotnet run --project src/kapacitor/kapacitor.csproj -- daemon start --name "$DOTNET_TEST_NAME" -d
dotnet run --project src/kapacitor/kapacitor.csproj -- daemon status --name "$DOTNET_TEST_NAME"
dotnet run --project src/kapacitor/kapacitor.csproj -- daemon stop --name "$DOTNET_TEST_NAME" --yes
ls ~/.config/kapacitor/daemons/ 2>/dev/null
```

Expected: start succeeds, status reports it running, stop returns cleanly, and `~/.config/kapacitor/daemons/` exists (likely empty after stop, since stop removes pid files). Also confirm `~/.config/kapacitor/daemon.log` exists if running in detached mode.

- [ ] **Step 7: Confirm the old command is rejected**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- agent --help 2>&1 | head -3
```

Expected: prints the top-level usage or an "Unknown command" — proves the hard cutover took.

- [ ] **Step 8: Open the PR**

PR title: `[AI-644] Rename daemon CLI command and align terminology`

PR description must include:

```
## Summary
- Renames `kapacitor agent` command group to `kapacitor daemon` (hard cutover, no alias).
- Renames state directory `~/.config/kapacitor/agents/` → `daemons/` and log file `agent.log` → `daemon.log`.
- Renames C# types: `AgentLockPaths` → `DaemonLockPaths`, `AgentCommands` → `DaemonCommands`.
- Deletes the pre-AI-630 `AgentLockMigration` (clean slate, no migration carried).
- Coding-agent terminology preserved: `--max-agents`, `KAPACITOR_AGENT_ID`, `agent_host_id`, `AgentOrchestrator`.

## ⚠️ Dogfood users: stop your existing daemon BEFORE installing

Run `kapacitor agent stop --yes` on the previous build, then install this one.
Any leftover files in `~/.config/kapacitor/agents/` and `~/.config/kapacitor/agent.log` are harmless — delete them at your leisure.

## Test plan
- [x] Unit tests pass
- [x] Integration tests pass
- [x] `dotnet publish -c Release` clean (no IL3050/IL2026 warnings) for both CLI and daemon
- [x] Manual: `daemon start/status/stop` round-trips, files land in `daemons/`
- [x] Grep proof: zero matches for `kapacitor agent\b`, `agent daemon`, or `kapacitor/agents/`

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

---

## Self-review

**Spec coverage check:**

| Spec requirement | Task covering it |
|---|---|
| Rename CLI command `agent` → `daemon` | Task 4 |
| Rename `help-agent.txt` → `help-daemon.txt` | Task 5 |
| State dir `agents/` → `daemons/` | Task 1 |
| Log file `agent.log` → `daemon.log` | Task 3 |
| Rename `AgentLockPaths` → `DaemonLockPaths` | Task 1 |
| Rename `AgentCommands` → `DaemonCommands` | Task 3 |
| Rename test classes | Task 1 |
| Delete `AgentLockMigration` + tests | Task 2 |
| Update `Program.cs:68` offline-commands list | Task 4 |
| Update `Program.cs:229` dispatch case | Task 4 |
| Error/usage text in `AgentCommands.cs` | Task 6 |
| Cross-refs in `help-status.txt`, `help-usage.txt`, `help-repos.txt`, `help-setup.txt` | Task 7 |
| Text in `SetupCommand`, `StatusCommand`, `DaemonRunner`, `DaemonLock`, `DaemonNameResolver` | Task 8 |
| README updates (both) | Task 9 |
| Preserve `--max-agents`, `KAPACITOR_AGENT_ID`, `agent_host_id`, `AgentOrchestrator`, etc. | Task 6 step 2, Task 8 step 1 |
| Verification (unit + integration + publish + manual + grep proof) | Task 10 |
| PR description calls out manual daemon-stop requirement | Task 10 step 8 |

No spec gaps.

**Placeholder scan:** no TBD/TODO/"handle edge cases"/etc. Every step has the actual replacement text or the actual command.

**Type consistency:** `DaemonLockPaths` referenced consistently across tasks. `DaemonCommands.HandleAsync(args)` matches the existing `AgentCommands.HandleAsync` signature. `daemons/` directory name is consistent everywhere.

Plan is ready.
