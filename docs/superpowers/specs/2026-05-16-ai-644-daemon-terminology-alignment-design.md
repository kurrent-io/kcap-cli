# AI-644: Daemon terminology alignment

## Problem

The CLI exposes a `kapacitor agent` command group that operates on what we call a **daemon** everywhere else: in docs, in the binary name (`kapacitor-daemon`), in the project layout (`Kapacitor.Daemon`), and in config keys (`daemon.max_agents`). Internally, "agent" is also used legitimately to mean a *coding agent* (Claude or Codex CLI sessions that the daemon hosts), so the term is overloaded.

This collision is most visible at the CLI surface (`kapacitor agent start --name X` is starting a daemon that hosts agents — a sentence that confuses everyone reading it). The daemon has not been released publicly yet, so this is the right moment to clean it up.

## Scope

Rename every "agent" reference that refers to the **daemon process itself** to "daemon". Keep "agent" where it legitimately refers to a **coding agent hosted by the daemon**.

### Renamed (category 1: daemon-self)

| Surface | Before | After |
|---|---|---|
| CLI command group | `kapacitor agent <start\|stop\|status\|logs\|doctor>` | `kapacitor daemon <…>` |
| Help file | `src/Kapacitor.Core/Resources/help-agent.txt` | `help-daemon.txt` |
| State directory | `~/.config/kapacitor/agents/` | `~/.config/kapacitor/daemons/` |
| Log file | `~/.config/kapacitor/agent.log` | `~/.config/kapacitor/daemon.log` |
| C# class | `AgentLockPaths` | `DaemonLockPaths` |
| C# class | `AgentCommands` | `DaemonCommands` |
| Test class | `AgentLockEnumerationTests` | `DaemonLockEnumerationTests` |
| Test class | `AgentLockPathsTests` | `DaemonLockPathsTests` |

Plus all user-facing text occurrences:

- `Program.cs:68` — offline-commands list entry `"agent"` → `"daemon"`
- `Program.cs:229` — dispatch `case "agent"` → `case "daemon"`
- Every `kapacitor agent …` example and "agent daemon" / "agent lock" phrase inside `AgentCommands.cs`, `SetupCommand.cs`, `StatusCommand.cs`, `DaemonRunner.cs`, `DaemonLock.cs`, `DaemonNameResolver.cs`
- Cross-references in `help-status.txt`, `help-usage.txt`, `help-repos.txt`, `help-setup.txt`
- `README.md` quick-start and `## CLI commands` section
- `npm/kapacitor/README.md`

### Deleted

- `src/Kapacitor.Core/AgentLockMigration.cs` and `test/kapacitor.Tests.Unit/AgentLockMigrationTests.cs` — this code migrated pre-AI-630 singleton `agent.pid` files to the per-name layout. We are doing a clean-slate rename (no migration), and keeping this file would leave "agent" terminology rotting in the codebase. Existing dogfood users who hit a stale `agents/` directory will get a fresh empty `daemons/` and can delete the old directory by hand.

### NOT renamed (category 2: legitimate coding-agent references)

These all really do refer to coding-agent (Claude/Codex) sessions hosted by the daemon, so the word "agent" is correct:

- `--max-agents` flag, `MaxAgents` property, `daemon.max_agents` config key (max concurrent hosted coding agents)
- `KAPACITOR_AGENT_ID` environment variable (ID of a hosted coding-agent invocation)
- `agent_host_id` hook payload field (the same ID, surfaced in hook payloads)
- `AgentOrchestrator` class (orchestrates hosted coding agents)
- `AgentDetector`, `CodingAgentsStep` (detects which coding agent CLIs are installed locally)
- `--agent-id` flag on `kapacitor watch` (Claude Code subagent ID)
- `subagent-start` / `subagent-stop` hook event names (Claude Code event names — we don't own them)
- Subagent transcript path `subagents/agent-{id}.jsonl`

## Migration strategy: clean slate

No automatic migration. Reasoning:

1. The daemon has not been publicly released, so the only users are us and other dogfooders who can stop their running daemons manually.
2. Carrying a one-shot migration would mean keeping `AgentLockPaths` (or a "legacy" version of it) around just to find the old directory, which defeats the goal of eliminating "agent"-as-daemon from the code.
3. Stale files in `~/.config/kapacitor/agents/` and `~/.config/kapacitor/agent.log` are harmless leftovers — they don't affect anything once the new build looks in `daemons/`.

The PR description must call this out so dogfood users stop their old `kapacitor agent` daemons before installing.

## Verification

- `dotnet run --project test/kapacitor.Tests.Unit` — all green
- `dotnet run --project test/kapacitor.Tests.Integration` — all green
- `dotnet publish src/kapacitor/kapacitor.csproj -c Release` — clean, no IL3050/IL2026 warnings
- `dotnet publish src/Kapacitor.Daemon/Kapacitor.Daemon.csproj -c Release` — clean, no IL3050/IL2026 warnings
- Manual smoke on the dev machine: `kapacitor daemon start --name test` → `kapacitor daemon status` → `kapacitor daemon stop --name test` round-trips. Path-handling is platform-touching, so the spec assumes `DaemonLockPaths` continues to use the same per-OS resolution logic as `AgentLockPaths` does today (no Windows-specific behavior changes in this PR).
- `grep -rn 'kapacitor agent\b' src/ test/ README.md npm/` returns zero hits (proof the user-facing rename is complete)

## Out of scope

- Renaming the conceptual model of category 2 (e.g., `KAPACITOR_AGENT_ID` → `KAPACITOR_HOSTED_AGENT_ID`). The split is clean enough; further renaming risks churn.
- Hidden `kapacitor agent` alias for backwards compatibility. Pre-public, not worth the maintenance surface.
- Touching anything in `Kapacitor.Daemon` project layout or the `kapacitor-daemon` binary name — those are already correct.
