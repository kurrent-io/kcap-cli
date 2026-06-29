# AI-632 — Hosted Codex PR review support

**Status:** Design approved (review feedback incorporated), pending final spec review
**Issue:** [AI-632](https://linear.app/kurrent/issue/AI-632) (follow-up to AI-68; related: AI-72 Windows, AI-633 sandbox/approval selector, AI-774 persistent reviewer loop)
**Date:** 2026-06-29

## Problem

The v1 hosted-Codex daemon path (AI-68) shipped with `LaunchKind.Review` explicitly
rejected for `vendor: codex`. `AgentOrchestrator.HandleLaunchAgent` emits
`"PR review for Codex is not yet supported"` before reaching `CodexLauncher`. Hosted PR
reviews therefore only work with Claude. This issue lifts that restriction so a hosted
Codex agent can review a PR with the same `kcap-review` MCP context the Claude path gets.

## Spike outcome (resolves the issue's two unknowns)

Verified against `codex-cli 0.142.3`. Both unknowns resolve in a way that makes the Codex
path **simpler than the Claude path** — it needs no temp files at all.

### MCP injection is ephemeral — no `config.toml` mutation
Codex registers MCP servers passed purely as `-c` overrides; nothing is written to
`~/.codex/config.toml`. Confirmed via `codex mcp list`:

```
codex mcp list \
  -c 'mcp_servers.kcap-review.command="kcap"' \
  -c 'mcp_servers.kcap-review.args=["mcp","review","--pr","42"]'
# → kcap-review appears in the list, lives only for that invocation
```

This is the **same `kcap mcp review` stdio server** already used by Claude (Claude reaches
it via a temp `--mcp-config` JSON file; Codex reaches it via `-c` overrides). The issue's
worry about appending a transient `[mcp_servers.kapacitor-review]` table and cleaning it up
on exit does not apply — the `-c` overrides exist only for that one process, so **there is
nothing to clean up**. (`kcap-review` is a valid TOML bare key — hyphens are allowed —
confirmed by the spike registering it.)

Codex also supports streamable-HTTP MCP servers (`-c 'mcp_servers.x.url="…"'`), but we reuse
the existing stdio server for parity and zero new server code.

### No `--system-prompt` flag — instructions go in as the initial prompt
Codex has no `--system-prompt` equivalent (the issue's guessed `system_prompt="…"` key does
not exist). Candidate base-instruction keys `base_instructions` / `experimental_instructions_file`
exist (accepted under `--strict-config`) and *replace* Codex's base system prompt, but
replacing Codex's base instructions risks stripping its own harness/tool scaffolding.

**Decision (approved):** pass the rendered `prompt-review.txt` as Codex's **initial prompt**
(positional `--` arg, exactly like the existing non-review Codex launch), leaving Codex's
base instructions intact. In interactive PTY mode `codex … -- "<prompt>"` starts the session
and submits the prompt as the first turn, so the agent begins reviewing immediately and the
human can attach and watch/intervene — matching how hosted Codex already launches.

### Native `codex review` / `codex exec review` — set aside
Codex 0.142 has native review subcommands, but they review *local git diffs*
(`--base`, `--commit`, `--uncommitted`) and don't fit our model of injecting rich PR context
(sessions, transcripts, file context) via the `kcap-review` MCP server. Not used here; noted
as possibly relevant to AI-774.

## Current architecture (touch-points)

- `src/Capacitor.Cli.Core/Commands/ReviewLaunchBuilder.cs` — `BuildAsync` renders
  `prompt-review.txt` (PR placeholders substituted) and writes a temp MCP JSON pointing at
  `kcap mcp review`; returns `ReviewLaunch(McpConfigPath, SystemPrompt)`. The MCP command is
  currently derived from `Environment.ProcessPath ?? "kcap"` — see the bug note below. Called
  by the interactive `kcap review` command (`ReviewCommand.cs`) and the daemon orchestrator.
- `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs` — review branch in `BuildArgs`
  emits `--mcp-config <path> --system-prompt <text>`; `Cleanup` deletes `agent.McpConfigPath`.
- `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs` — `BuildArgs` builds the interactive
  launch (`--cd`, `--sandbox`, `--ask-for-approval`, `-m`, `-c model_reasoning_effort`,
  `--no-alt-screen`, `-- <prompt>`); returns `McpConfigPath: null`; `Cleanup` is a no-op.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — rejects Codex review
  (lines ~239–243); for reviews bases the worktree on `refs/pull/{pr}/head` and re-validates
  origin (lines ~267–292, ~454); calls `ReviewLaunchBuilder.BuildAsync` (line ~341).
- `IHostedAgentLauncher` / `LauncherContext` carry `IsReview`, `Review`, `ReviewLaunch`.
- `AgentInstance.McpConfigPath` is the cleanup handle; `CleanupAgentAsync` calls
  `launcher.Cleanup(agent)`.
- `DaemonConfig.CapacitorPath` (default `"kcap"`) — the daemon's path to the **kcap CLI**
  binary, already used to spawn auxiliary kcap processes (`AgentOrchestrator.cs:757`).

### Pre-existing bug surfaced during review (must fix here)
`ReviewLaunchBuilder` derives the MCP server command from `Environment.ProcessPath ?? "kcap"`.
That is correct for the interactive `kcap review` CLI (the running process *is* `kcap`), but
**wrong inside the daemon**: the daemon is a separate binary (`Capacitor.Cli.Daemon.csproj`
sets `AssemblyName = kcap-daemon`), and `mcp review` only exists in the `kcap` CLI
(`Capacitor.Cli/Program.cs`). In the daemon, `Environment.ProcessPath` resolves to
`…/kcap-daemon`, so the injected MCP command becomes `kcap-daemon mcp review …`, which has no
such subcommand and cannot serve the review MCP server. This affects the **existing Claude
hosted-review path** too. The fix (below) threads the correct CLI path through the builder.

## Design

### 1. `ReviewLaunchBuilder` — vendor-aware, explicit CLI path
Surface the MCP server as a structured descriptor, take the CLI path explicitly (fixing the
`Environment.ProcessPath` bug), and only write the temp JSON for Claude:

```csharp
public record ReviewMcpServer(
    string Command,
    string[] Args,
    IReadOnlyDictionary<string, string> Env);

public record ReviewLaunch(string? McpConfigPath, string SystemPrompt, ReviewMcpServer Mcp);

public static async Task<ReviewLaunch> BuildAsync(
    string vendor, string cliPath, string baseUrl, string owner, string repo, int prNumber)
```

- Render `prompt-review.txt` first (unchanged ordering — render before any file write so a
  resource-load throw never leaks a temp file).
- Build the `ReviewMcpServer` descriptor once: `Command = cliPath`,
  `Args = ["mcp","review","--owner",owner,"--repo",repo,"--pr",pr]`, `Env = {KCAP_URL = baseUrl}`.
- `vendor == "claude"` → serialize the descriptor to the temp JSON (`mcpServers.kcap-review`
  shape, byte-identical to today), set `McpConfigPath`.
- `vendor == "codex"` → `McpConfigPath = null`, descriptor only, no file written.
- Callers pass the CLI path:
  - daemon `AgentOrchestrator` → `_config.CapacitorPath` (the kcap CLI, **not** the daemon).
  - interactive `ReviewCommand.cs` → `Environment.ProcessPath ?? "kcap"` (the running process
    is `kcap` there, which is correct).

### 2. `CodexLauncher.BuildArgs` — review branch
Add an `IsReview`/`ReviewLaunch` branch mirroring the Claude branch shape:

```
--cd <worktree> --sandbox workspace-write --ask-for-approval on-request
  -c mcp_servers.kcap-review.command="<cliPath>"
  -c mcp_servers.kcap-review.args=["mcp","review","--owner","o","--repo","r","--pr","42"]
  -c mcp_servers.kcap-review.env={KCAP_URL="…"}
  [-m <model>] --no-alt-screen -- "<rendered prompt-review.txt>"
```

- Reads `launch.Mcp` (descriptor) and `launch.SystemPrompt` from `ctx.ReviewLaunch`.
- Returns `McpConfigPath: null` (no temp file).
- Build the three `-c` values via a small TOML-encoding helper:
  - basic-string encoder (escape `"`, `\`, control chars) for `command`, env values, and each
    `args` element;
  - array literal `["a","b",…]` for `args`;
  - inline-table literal `{KCAP_URL="…"}` for `env`.
  Helper must be AOT-safe: plain string building, no reflection/serializer.
- `Prepare`/`Cleanup` unchanged: the hooks preflight and `.codex` overlay still apply to
  reviews (we want session capture), and there is nothing to clean up.

### 3. `AgentOrchestrator.HandleLaunchAgent`
- Delete the `isReview && cmd.Vendor == "codex"` rejection (lines ~239–243).
- Pass `cmd.Vendor` and `_config.CapacitorPath` into `ReviewLaunchBuilder.BuildAsync`.
- Worktree-on-PR-head-ref and origin re-validation already vendor-agnostic — no change.

### 4. Cleanup — free
Codex review produces no temp file, so `CodexLauncher.Cleanup` stays a no-op and
`AgentInstance.McpConfigPath` stays null for Codex reviews. The acceptance criterion "temp
injected config / file is cleaned up on agent exit" is satisfied by *not creating one*.

### 5. README
`README.md` line 413 currently states *"PR review for hosted Codex agents is not yet
supported (tracked in AI-632)."* Update it in the same PR (per the CLAUDE.md doc-sync rule).
The adjacent AI-633 note (sandbox/approval selectors) stays.

## Smaller decisions (approved)

- **Review sandbox/approval:** keep the existing hosted-Codex defaults
  (`workspace-write` / `on-request`) for parity rather than forcing read-only — a reviewer may
  want to run tests against the PR head. Read-only is a possible later refinement.
- **Prompt reuse:** use the same `prompt-review.txt` for both vendors in v1. It reads slightly
  like a system prompt delivered as a user turn, but the content is just instructions + tool
  guidance. A Codex-tailored variant is a possible follow-up, not v1.

## Testing

The real new logic is pure argument/descriptor construction, so the bulk of coverage lives in
**narrow unit tests that need no git fixture**:

- `CodexLauncherTests` (`test/.../Codex/CodexLauncherTests.cs`, existing): review-branch
  `BuildArgs` test — given a `LauncherContext` with `IsReview = true` and a `ReviewLaunch`
  descriptor, assert it emits `-- <prompt>` plus the three `-c mcp_servers.kcap-review.*`
  overrides, no `--system-prompt`, `McpConfigPath == null`, and correct TOML encoding of
  `command`/`args`/`env` (including escaping).
- `ReviewLaunchBuilder` tests (new): `vendor: claude` writes a temp file and populates the
  descriptor with `Command == cliPath`; `vendor: codex` returns `McpConfigPath == null` with
  the descriptor populated; `Args`/`Env`/`Command` correct for both. (Also pins the
  `Environment.ProcessPath` → `cliPath` fix.)

**Orchestrator level — replace the rejection test, don't "flip" it.** The existing
`Launch_review_kind_with_vendor_codex_emits_launch_failed` uses `/tmp/whatever` and relies on
the Codex gate firing *before* repo validation. Once the gate is removed, replace it with a
test asserting the gate is **lifted**: a Codex review launch now fails at the *same* point a
Claude review would (e.g. "Repo path does not exist" / origin mismatch) rather than emitting
"PR review for Codex is not yet supported". This proves the routing change cheaply.

A fully offline **happy-path** review launch through the orchestrator is **not feasible** and
is intentionally not attempted: the review path both (a) validates `origin` is
`github.com/{owner}/{repo}` (`GetOriginRemoteAsync`) and (b) fetches `refs/pull/{pr}/head`
from `origin` (`WorktreeManager.CreateAsync`). A local fixture can satisfy one or the other
but not both offline. This matches the current state — no happy-path review test exists at the
orchestrator level for Claude either. End-to-end coverage is the manual smoke test below.

Plus:
- Existing wire-format test already covers `LaunchKind.Review`; vendor `codex` round-trips.
- Verify no IL3050/IL2026 AOT warnings on `dotnet publish -c Release` (the TOML helper must be
  reflection-free).
- Manual smoke (acceptance): launch a hosted Codex PR review against a real PR; confirm the
  agent sees PR context (calls `get_pr_summary` etc.) against the PR head ref, and that the
  injected MCP command resolves to the real `kcap` CLI (not `kcap-daemon`).

## Out of scope

- Windows (AI-72).
- Generic Codex sandbox / approval selector in the launch dialog (AI-633).
- Persistent hosted Codex reviewer loop (AI-774).
- Codex-tailored review prompt variant (possible follow-up).

## Review feedback incorporated (2026-06-29)

1. **CLI path bug (High).** MCP command was `Environment.ProcessPath ?? "kcap"`, which is
   `kcap-daemon` inside the daemon (no `mcp review` subcommand) — also broken for the existing
   Claude path. Resolved: thread `cliPath` into `BuildAsync`; daemon passes
   `_config.CapacitorPath`, interactive CLI passes `Environment.ProcessPath ?? "kcap"`.
2. **Test strategy (Medium).** "Flip the rejection test" is insufficient — launch then hits
   repo/origin/PR-ref validation. Resolved: real coverage via narrow `CodexLauncher` /
   `ReviewLaunchBuilder` unit tests; orchestrator test asserts gate removal; documented that a
   fully offline happy-path orchestrator test isn't feasible (github-origin vs PR-ref fetch).
3. **Spec on wrong branch (High).** The first commit landed on `main` by mistake; relocated to
   `alexeyzimarev/ai-632-codex-hosted-codex-agents-pr-review-support` and `main` reset back.
