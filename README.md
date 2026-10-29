# kcap

> Full observability for your AI coding-agent sessions — record every session, watch agent activity in real time, and review code changes grounded in the transcripts that produced them.

[![npm](https://img.shields.io/npm/v/@kurrent/kcap?color=cb3837&logo=npm&label=%40kurrent%2Fkcap)](https://www.npmjs.com/package/@kurrent/kcap)
[![license](https://img.shields.io/badge/license-Kurrent%20v1-blue)](LICENSE.md)
[![platforms](https://img.shields.io/badge/platform-macOS%20%7C%20Linux%20%7C%20Windows-lightgrey)](#requirements)
[![built with](https://img.shields.io/badge/.NET%2010-NativeAOT-512bd4?logo=dotnet&logoColor=white)](#)

**kcap** records your coding-agent sessions and forwards them to a kcap server, where a real-time dashboard and PR-review tools surface the context no diff can give you: *why* code changed, what alternatives were weighed, and how it was actually built. It works across twelve agents — Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity, Mistral Vibe, Grok, and Muse Code — capturing the full picture: session lifecycle, transcripts, subagent trees, tool calls, and token usage.

## Contents

- [Why kcap](#why-kcap)
- [Requirements](#requirements)
- [Getting started](#getting-started) — [Install](#1-install-and-set-up) · [New session](#2-start-a-new-agent-session) · [Dashboard](#3-check-it-in-the-dashboard) · [Other ways to install](#other-ways-to-install)
- [Desktop app](#desktop-app) — [macOS](#desktop-app-on-macos) · [Windows](#desktop-app-on-windows)
- [What it records](#what-it-records)
- [CLI commands](#cli-commands)
  - Setup: [setup](#initial-setup) · [unattended](#unattended-setup) · [after setup](#after-setup) · [agent notes](#agent-specific-notes) · [local server](#local-server-kcap-local)
  - Approvals: [policy](#approval-policy)
  - Sessions: [recap](#session-recap) · [validate-plan](#plan-validation) · [hide](#hide-session) · [disable](#disable-recording) · [errors](#error-extraction) · [eval](#session-evaluation-llm-as-judge)
  - Reviewing: [review](#pr-review-with-full-context) · [curate](#curate-guidelines)
  - MCP servers: [overview](#mcp-servers-for-agents) · [sessions](#sessions-mcp-server-for-agents) · [flows](#flows-mcp-server-for-agents) · [flow-result](#flow-result-mcp-server-hosted-reviewers) · [memory](#memory-mcp-server-for-agents)
  - Importing: [import](#loading-historical-sessions) · [remap](#renamed-repo-directories-kcap-remap) · [muse-assistant](#muse-assistant)
  - Agents & daemon: [daemon](#daemon) · [agent](#local-agents-kcap-agent) · [repos](#repository-paths)
  - Account: [projects](#projects) · [profiles](#profiles) · [config](#configuration) · [telemetry](#telemetry) · [uninstall](#uninstalling) · [other](#other-commands)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Why kcap

- **Records everything, automatically.** Once set up, `kcap` runs silently in the background and captures every session — lifecycle, transcripts, subagent trees, tool calls, and token usage — with no change to how you work.
- **Real-time visibility.** Transcript data streams live over SignalR to a dashboard that shows repositories, sessions, and agents as they happen.
- **PR review with real context.** `kcap review` launches a reviewer equipped with MCP tools that query the actual implementation transcripts — ask *why* something changed, not just *what*.
- **Recall past work from your agent.** MCP servers let agents search and reuse prior sessions, durable team memories, and structured review flows without leaving the chat.
- **Works with your agents.** One install covers Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity, Mistral Vibe, Grok, and Muse Code.

## Requirements

- **A kcap workspace.** `kcap setup` signs you in and finds yours, or offers to create one; a self-hosted server takes its URL instead.
- **Nothing else to install.** `kcap` is a self-contained NativeAOT executable with no runtime dependency. Node.js and npm are needed only if you install it [through npm](#other-ways-to-install).
- **At least one supported coding agent** so there's something to record: Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity, Mistral Vibe, Grok, or Muse Code.
- **GitHub CLI (`gh`), optional** — the desktop app reads a linked pull request (description, checks, reviews, inline threads, conversation) through your own `gh` sign-in when a `gh` recent enough for `gh auth status --json` is installed and signed in, including GitHub Enterprise hosts. Without it the card says what to install; a tenant with the server-side GitHub App enabled reads without it.
- **A supported platform:**

  | Platform | Architecture |
  |----------|-------------|
  | macOS | ARM64 (Apple Silicon) |
  | Linux | x64, ARM64 |
  | Linux (Alpine/musl) | x64, ARM64 |
  | Windows | x64 |
- Desktop app: macOS 15 (Sequoia) or later on Apple silicon, or Windows 10 or later on x64.

## Getting started

### 1. Install and set up

macOS and Linux:

```bash
curl -fsSL https://www.kurrent.io/install | bash
```

Windows (PowerShell):

```powershell
irm https://www.kurrent.io/install.ps1 | iex
```

The installer checks the download against its published checksum, installs the native `kcap` binary for your [platform](#requirements), and, on a first install from a terminal, runs `kcap setup` unless you pass `--no-setup` (run headless with no setup options, it only installs; run `kcap setup` yourself afterward). On macOS and Windows the [desktop app](#desktop-app) is the easier way in; `kcap setup` is for the terminal, and for scripts — see [Unattended setup](#unattended-setup) for both editions' recipes. Setup asks whether you want kcap locally on this Mac or for your team in the cloud (sign-up opens in your browser), signs you in, finds your workspace, offers to install kcap into every coding agent it detects, and offers to import this machine's past sessions. On a server that offers it, you finish in the browser. [Initial setup](#initial-setup) covers every step and unattended runs.

### 2. Start a new agent session

Use an agent kcap is installed into: setup installs it unless you decline, and `kcap plugin install` adds one later. Hooks load when a session starts, so a session that was already running is not recorded live. Start a new one (or `claude --continue`) and give it a prompt.

### 3. Check it in the dashboard

Open your workspace URL (e.g. `https://acme.kcap.ai`). The session you just started should be there, updating as you work — that is the check that recording works. `kcap status` shows that kcap is configured and can reach the server, and when each agent last ran a kcap hook on this machine; that proves the hooks loaded, not that a session reached the dashboard. If the session is missing, see [Sessions are not recorded](#sessions-are-not-recorded).

kcap reports anonymous usage data by default; see [Telemetry](#telemetry) to opt out.

### Other ways to install

- **Name the workspace up front:** pass setup options after a second `--`, e.g. `curl -fsSL https://www.kurrent.io/install | bash -s -- -- --server-url https://acme.kcap.ai` (on Windows, set `KCAP_SETUP_ARGS`). Without a terminal the installer adds `--no-prompt`, which needs a workspace (`<tenant>` or `--server-url`), or `--org` and `--slug` to create one; see [Unattended setup](#unattended-setup). `--no-setup` installs without running setup, and `--beta` installs the beta channel.
- **npm:** `npm install -g @kurrent/kcap`, then `kcap setup`. This needs Node.js; see [npm installs and upgrades](#npm-installs-and-upgrades) for npm 11's install-script gate.
- **Desktop app:** on macOS or Windows the [desktop app](#desktop-app) bundles `kcap` and the daemon.
- **Run the server on this machine:** `kcap setup --local` installs a kcap server on a Mac instead of using a hosted workspace; `kcap local status` then says how it is doing. With [Ollama](https://ollama.com) running, it also sets up search by meaning. See [Local edition](#local-edition) and [Local server](#local-server-kcap-local).

## Desktop app

### Desktop app on macOS

Download `kcap-osx-arm64.dmg` from https://www.kurrent.io/download/mac (Apple silicon, macOS 15 or later), open it and drag **kcap** to **Applications**. The app bundles its own `kcap` CLI and daemon: you do not need the npm install as well, and the first run offers to link `kcap` onto your terminal PATH. On the first run, the cloud edition opens kcap.ai's sign-up in your browser, where the invite code, organization, workspace address and trial are handled; once the page says your workspace is ready, **Continue** in the app finds it and setup carries on. The local edition first asks how to search your sessions: by meaning, with the built-in model kcap downloads and runs itself, or through [Ollama](https://ollama.com) with `mxbai-embed-large` or a model you name, or by keyword only; the install shows the built-in or Ollama model download and ends by saying which one search uses, and why when it is keyword. The search item beside **kcap local** in the sidebar changes it later, by running setup again. Signing in installs the daemon as a background service and starts it without asking, named after this machine (with `-1`, `-2`, … when another machine of yours already uses that name), and removes the service of the name it replaced — it asks only before replacing a daemon service that belongs to another workspace. Whenever the app finds the daemon not running, it starts the service again. Sessions running on your other machines' daemons open in the app too — their chat, their prompts, and for a terminal harness a read-only view of the terminal — over the server, without a local daemon. Wheel and trackpad gestures scroll the terminal history in small increments. When an agent enables terminal mouse tracking, wheel reports go to the source in either screen buffer so the agent scrolls its own history; this requires a desktop app and source daemon with wheel-input support.

For sessions hosted on this machine, Claude Code panels such as `/status` and `/cost` appear in chat with **Close** to send Escape. Opening a panel confirms the latest slash command sent at an idle prompt; unrelated messages stay pending until their own receipt arrives.

The session rail on the left lists the sessions the server knows for your account, not only the ones this machine's daemon is running: an agent started in a plain terminal appears with a **terminal** chip and opens read-only, and an ended session stays listed after its agent has gone, opening read-only on its transcript with the work-context pane beside it. **Mine** / **Team** sets whose sessions you see and **All** / **Live** / **Ended** which of them; the **Group** picker arranges them by repository, date or status, or not at all, and the **Sort** picker orders each group by last update, creation time or title, with the arrow beside it flipping the direction. The rail opens on your own sessions grouped by repository, newest first, and remembers every change per signed-in user, so two people sharing a machine each see it their way. Ended sessions arrive 20 at a time, with **Show 20 more** under the list and a count of how many are loaded; at the server's listing cap the rail asks you to search or narrow the scope instead. A finished agent's open workspace follows its session onto the ended row rather than closing, and a hosted agent whose daemon drops it waits half a minute for the server to list the session before giving up on it.

When chat delivery cannot be confirmed, the pending row says **Delivery unconfirmed**. **Send again** is available when the server is not holding that attempt; a retry can duplicate a message that already ran. **Discard** removes the unresolved attempt from the pending list without cancelling delivery or changing the composer draft. Remote discard waits until the server identifies the attempt and requires a server with queued-input discard support.

When a Claude Code session spawns subagents, the chat shows a strip above the composer while any of them run ("2 subagents running"), and the work-context pane lists them under **SUBAGENTS** — the agent type, a *background* tag for one launched in the background, and its state: running with its elapsed time, done, failed or stopped. The section starts collapsed, its header showing how many subagents are running, completed, failed and stopped and its list holding the ones still running; click the header to open the full list. Both are read from the transcript; a session on another machine's daemon shows the strip alone, since the work-context pane is not part of the remote session view. Rows are not links.

When the agent declares a plan through the `kcap-plans` tools, the work-context pane shows it under **PLAN**, between the pull request and the subagents. The section starts collapsed, its header showing how many tasks are pending, in progress and done — a skipped task counts as done — and its list holding the tasks in progress. Click the header to open it: the plan, spec and design documents it named, then every task with its state — pending, in progress, completed or skipped — and any note the agent left on it, under a header that reads "2 of 6 done". The list follows the agent closely: a task the session updates moves within a second or two, and a change made anywhere else arrives within half a minute. Click a document to read it: it opens in the **Pages** tab beside Chat and Terminal, and the open one is marked in the list.

The **Pages** tab appears in the centre switch when the session has a plan, spec or design document, declared or reconstructed from the transcript, or a published page; a remote session gets the same tab in its own switch. It lists them on the left and renders the chosen one on the right as the agent declared or published it, with a chip when the server holds only a truncated body or none; for a local session the document reader says when the working copy has moved on from the declared text.

Calls to the `kcap-*` MCP tools read in plain words in the chat ("Attached work item · AI-3084", "Saved memory · …", "Searched sessions · …") instead of the raw tool name, grouped and folded as other tool calls are. A call that produced something you can open becomes a card once its result lands: a published page carries Copy link and Open, a declared document carries Open into the Pages tab. Tools from other MCP servers read as "Server · tool name" with their first argument.

The app must run from the Applications folder — launched from the disk image or from Downloads it offers to move itself there first, because the terminal link and the background service point at its location.

Open **Settings…** from the application menu (⌘,) or the tray to edit the daemon for the app's selected profile. **Save** applies capacity to a current running daemon immediately; lowering it leaves existing agents running and limits new launches. Set it to **0** for no limit. When the daemon is stopped or needs an update, the saved capacity applies when it next starts. **Rename and restart daemon** is available when no agents are active and the new name is free. After confirmation it replaces the old background service and relaunches the app. An unbundled development build asks you to restart the app yourself. Rename waits for startup to finish and requires a CLI that supports retiring the old service. If `KCAP_DAEMON_NAME` sets the name, remove that override and restart the app before renaming.

The **Notifications** tab in Settings controls permission requests, questions, and idle-agent alerts independently; all three start enabled and apply while the app is in the background. Permission notifications offer **Allow**, **Always**, and **Decline** when the agent supports those choices. Questions open the agent in the app to respond. Idle alerts open a local agent after its turn and subagents finish; remote daemons do not yet report idle state. macOS asks for permission the first time the app window is active; choose **Allow**, because an unanswered prompt counts as a refusal. If notifications are turned off in macOS, the tab says so and links to System Settings → Notifications. These are local desktop notifications: the app must remain running, including in the tray. Clicking a notification opens that session in the app; the app registers for the macOS **Alerts** style so a notice stays on screen until you act on it. If a notice slides away after a few seconds, macOS is still using the Banners style for the app: switch it under System Settings → Notifications → kcap → Alert style.

The **Accounts** tab in Settings lists this machine's Claude and Codex config directories (see [Accounts](#accounts)), grouped by vendor: each one's label, directory and recording state — Recording, Needs approval in Codex (a Codex account whose kcap hooks wait to be approved the next time Codex starts there), Needs repair or Not set up — with a **Default** badge on the one the daemon uses when a session names no account. When that default directory is not registered, it shows as a row of its own, Not set up, with **Add**. Each row also shows the account's plan and email, a bar per usage window with when it resets, the reading's age and the last refresh error, and summary cards above the list show which accounts are ready for a long run, the next reset and which need a fix. A registered account's menu offers **Refresh** (asks the vendor again, as `kcap accounts refresh` does), **Rename**, **Reconnect** and **Remove**, and **Add account** lists the config directories found on this machine, then a folder picker for any other. Each action runs the matching `kcap accounts` command, one at a time, and the tab says when it could not confirm the result; the list always shows the daemon's current state. Signing in to an account stays the vendor CLI's job. While the daemon is not connected the tab lists nothing and says so, and a daemon too old to manage accounts gets a note to update kcap on this machine.

Updates arrive through the app: it checks a few times a day, downloads in the background and asks before restarting ("Check for Updates…" in the menu bar checks now). A bundled `kcap update` reports this and does nothing else. The bundled CLI follows the app's channel; the npm package stays the headless/CI channel. After an update the app opens on a progress window until the daemon runs the new version and, with a local server, until that server has been upgraded to match; the main window follows, and **Open kcap** skips the wait. The first launch of a new version upgrades the local server whichever way the app was updated: in the app, from the disk image, or by replacing it in Finder. A failed upgrade, or one whose server version is not published yet, is tried again on later launches. On macOS, a copy of kcap in both `/Applications` and `~/Applications` makes the app ask which to keep; it removes the other and points the daemon and the terminal command at the kept one. Install and update milestones are written to `~/.config/kcap/app-lifecycle.jsonl`.

Help → Report a Bug… / Send Feedback… (or the help button in the session rail's footer) sends a report to Kurrent support; replies arrive by email. On the local server, which has no support intake, it opens a prefilled email to team@kcap.ai in your mail app instead and copies the report to your clipboard.

**Uninstall kcap…** in the application menu removes what [`kcap uninstall`](#uninstalling) removes, plus the app, the `kcap` links it made and the PATH line it added to your shell profile. It first lists what will go, and warns when the local server is installed (or a change to it was interrupted), because its recorded sessions are deleted with it; sessions already sent to a cloud server stay there. One progress window stays open through cleanup and confirms completion after the app is moved to the Trash. **Show details** reveals the removal history; **Done** closes the window when finished. Restart coding agents that were open during uninstall so they stop using previously loaded kcap hooks. On Linux the item is in the help menu at the foot of the session rail, beside Settings: it also removes the daemon's systemd user unit and the CLI the AppImage copied to `~/.local/share/kcap/desktop`, then moves the AppImage to your Trash.

**Upgrading an existing macOS installation:** use **Check for Updates…** in the installed app, or open **kcap** from the new disk image and choose **Quit and update**. The installation stays in its Applications folder. Moving the app to the other Applications folder updates owned terminal links and background-service paths, then refreshes previously installed agent integrations with the bundled CLI to repair MCP commands, git hooks, and the Claude marketplace path. A failed integration refresh is reported and retried on the next launch. An update preserves sign-in and settings, and keeps supported agent sessions for resumption unless `daemon.auto_resume` is off. An installer that finds valid installations in both Applications folders, or another app occupying the destination, asks for a destination first. An unrelated app is never replaced. Other copies the replacement supersedes, an older **Kurrent Capacitor** app included, are removed once it opens; the copy you ran the installer from is removed by the installed app after that copy has quit. A failed update restores the installation or retains recovery backups and reports what needs attention. An equal or newer installed version offers **Open installed copy**.

### Desktop app on Windows

Download `kcap-<version>-win-x64-Setup.exe` from the [GitHub release](https://github.com/kurrent-io/kcap-cli/releases) (Windows 10 or later, x64) and run it. It installs per user into `%LocalAppData%\kcap` with no administrator prompt and adds a Start-menu entry. Like the macOS app it bundles its own `kcap` CLI and daemon. The first run offers to add the app's folder to your user PATH, so `kcap` works from any new terminal. Signing in installs the daemon as a background service and starts it, as on macOS: the app offers to run it under the machine's `kcap-service` Windows service, which takes one administrator prompt, and keeps a per-user Scheduled Task that starts at sign-in when you decline. See [Windows service](#windows-service).

Windows draws no application menu, so **Settings…**, **Changelog** and the app version sit in the help button in the session rail's footer, next to Documentation and the report items. Closing the window keeps the app running in the notification area — quit from the tray icon's menu. Updates arrive through the app the same way as on macOS, from the Windows feed.

### Rebrand compatibility

kcap keeps existing configuration filenames, data directories, worktree locations,
and branch prefixes; no data-directory migration is required for the rebrand.

On macOS an installed **Kurrent Capacitor** app updates itself to kcap in place and then
finishes the update by moving to `kcap.app` in the same Applications folder, repointing the
`kcap` terminal link and the daemon's background service. Opening the kcap disk image does the
same for a Kurrent Capacitor app that has not updated yet, and quits it first if it is running.

The connection skill is now `connect-kcap` (`kcap-connect-kcap` in the shared
agent skill tree), and the Muse recording skill is `kcap-recording`. Refreshing
agent skills replaces the former `kcap-connect-capacitor` installation. Muse
setup replaces the old skill names and reuses existing scheduled tasks while
keeping their data and consent in place. Existing integration matchers continue
to recognize legacy names. Previously shared `connect-capacitor/SKILL.md` links
remain available as redirects to `connect-kcap`.

## What it records

Once set up, kcap runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Durable lifecycle delivery** — if the server is briefly unreachable when a `SessionStart`/`SessionEnd` hook (or a per-subagent `SubagentStop` carrying an `agent_id`) fires (for example during a deploy), or rejects your credentials (HTTP 401), the event is spooled to `~/.config/kcap/spool/` and automatically re-sent on the next hook — after `kcap login`, for a rejected credential — so sessions don't get stuck "active", lose their start record, or leave subagents stuck "running". No action needed beyond logging back in; stale spool entries are reaped after 30 days.
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage
- **In-agent upgrade prompts** — in Claude Code sessions, when the server is running a newer kcap release than the local CLI, additional context is injected into the session so the agent can offer the user an upgrade via `kcap update`. The stderr `kcap` update hint continues to fire for direct command-line use, and every request also carries the CLI's version to the server so it can surface its own out-of-date banner/notification (see [`kcap update`](#other-commands) for the full picture, including how `update_check: false` turns all of this off).
- **SessionStart context injection** — at every session start the server injects top evaluation-derived fact clusters for the current repo into the session's context. The injected block is split into two sections: `## Known patterns` (repo/project facts relevant to any reader) and `## Guidance from past sessions` (agent-targeted action items derived from prior eval suggestions with `audience: "agent"`). Delivered for every supported harness (Claude Code, Codex CLI, GitHub Copilot CLI, Gemini CLI, AWS Kiro CLI, Google Antigravity, Pi, OpenCode, and Cursor's `cursor-agent`): Claude reads it from its hook response, while the other eight fetch `GET /api/repositories/{hash}/guidelines` alongside the team-memory index and emit both in one combined block, through the same per-harness delivery seam as that index (see the next bullet). Opt out by setting `disable_session_guidelines: true` in `~/.config/kcap/config.json` or via `kcap config set disable_session_guidelines true`.
- **SessionStart team-memory index** — at every session start (Claude Code, Codex CLI, GitHub Copilot CLI, Gemini CLI, AWS Kiro CLI, Google Antigravity, Pi, OpenCode, and Cursor CLI's `cursor-agent` — see the capability matrix below for the full per-harness rollout) `kcap` also fetches a compact index of durable [team memories](#memory-mcp-server-for-agents) visible for the current repo/machine and appends a `## Team memory` block to the session's injected context (`additionalContext` for Claude, Codex, Copilot, and Gemini, `additional_context` for Cursor, raw stdout for Kiro, `injectSteps`/`userMessage` for Antigravity, system-prompt append via the kcap Pi extension for Pi, and system-entry append via the kcap OpenCode plugin for OpenCode): one `slug [scope]: description` line per memory, grouped **Org / Team / Yours**, with a log's open-entry count after its slug (`inbox (3 open) [repo]: …`), with a nudge to call `get_memory` / `search_memories` for full content. The `[scope]` tag annotates the memory's home scope — a project memory shows `[project: <slug>]`, a repo one `[repo]`, and org-scoped memories stay untagged. Above the list, a lead-in names the projects the current repo belongs to and the slug to pass on `save_memory`'s `project` argument (`This repo belongs to project "capacitor" (kcap). Save learnings that span its repos with project: "capacitor".`) — one line per project, and nothing at all for a repo in no project or a server that does not report them. The lead-in is injected even when the repo has no memories yet, which is when the agent most needs the slug. Only the index is injected — never the bodies — so the cost stays roughly flat as the pool grows (mirrors a local `MEMORY.md`). Best-effort and fail-open (a slow or failed fetch injects nothing, never blocking the hook), and only ever injected once per conversation. Opt out with `disable_memory_index: true` in `~/.config/kcap/config.json` or `kcap config set disable_memory_index true`.
- **SessionStart work-items nudge** — at every session start (Claude Code, Codex CLI, GitHub Copilot CLI, Gemini CLI, AWS Kiro CLI, Google Antigravity, Pi, OpenCode, and Cursor CLI's `cursor-agent`) `kcap` appends a short `## Work items` block carrying the current session id and a reminder to register the session with its work item via the [`kcap-workitems` MCP tools](#work-items-mcp-server-for-agents) (`declare_work_item`) and to declare structure as it is discovered (`declare_work_breakdown` for a parent→parts split, `declare_work_relation` for a `blocks`/`blocked_by` dependency). It rides the same per-harness delivery seam as the team-memory index, is composed independently of that index (so it never affects the index's once-per-session lease), and is shown only when `kcap-workitems` is actually registered for the harness and the tenant's plan includes Work Items. The plan comes from the last `X-Kcap-Plan` response header the CLI saw from the configured server, cached per server and shared by every harness on the machine — so a Free tenant is not told to call a tool that would refuse, and a plan change costs at most one stale nudge. An unknown plan (an older server, or one never reached) nudges. Opt out with `disable_workitems_nudge: true` in `~/.config/kcap/config.json` or `kcap config set disable_workitems_nudge true`.
- **SessionStart plans nudge** — at every session start, on the same harnesses and through the same delivery seam as the work-items nudge, `kcap` appends a two-sentence `## Plans` block telling the agent to declare the plan, spec or design document it works from and the plan's task list through the [`kcap-plans` MCP tools](#plans-mcp-server-for-agents) (`declare_plan_document`, `set_plan_tasks`, `update_plan_task`, and `get_plan` to recover the list after compaction), carrying the current session id. It is shown only when `kcap-plans` is actually registered for the harness — for Claude Code, only when the installed plugin's `.mcp.json` names it, so a plugin installed before the server existed is never nudged toward a tool it lacks. Opt out with `disable_plans_nudge: true` in `~/.config/kcap/config.json` or `kcap config set disable_plans_nudge true`.
- **SessionStart worktree-links nudge** — git links a worktree to its repository by absolute path, so when the repository is opened from a sandbox or container and from the host that mounts it at another path, a worktree made on one side cannot be opened from the other. When a session looks sandboxed (a container cgroup, `$SANDBOX_NAME` or `$container`, the repository on a `virtiofs`, 9p or FUSE mount) and the repository has not enabled `worktree.useRelativePaths` (in its own or the global git config) nor declined with `kcap.relativeWorktrees`, `kcap` appends a `## Worktree links` block telling the agent to ask the user before switching to relative links (git 2.48+ then required everywhere the repository is opened) and pointing it at the `worktrees` skill. Not shown to flow participants. Decline for one repository with `git config kcap.relativeWorktrees declined`, or for all with `kcap config set disable_relative_worktrees_nudge true`.
- **Plan-read nudge (Claude Code)** — when the agent reads a Markdown file, a synchronous `PostToolUse` hook on `Read` (`kcap hook --claude --plan-read`) asks the server whether it is a plan, spec or design document in a session that has declared no plan; if so, the agent gets a reminder to declare it, once per session and document. Only the head of the file is sent. Same gate and opt-out as the SessionStart plans nudge.
- **Declared-document refresh (Claude Code)** — the server holds the copy of a document `declare_plan_document` sent, not the file. When the agent then changes a document the session declared with `Write`, `Edit` or `MultiEdit`, a synchronous `PostToolUse` hook (`kcap hook --claude --plan-edit`) declares it again with the same arguments, to the server that accepted the declaration, so the server holds the current text. The declarations it can replay are kept locally under `~/.config/kcap/declared-documents/`, so a resumed session keeps them, and are removed 30 days after the session last declared or edited one. Other harnesses, and edits made any other way (a shell command, a patch tool), rely on the agent declaring the document again, which the tool description and the plans nudge ask for. It has no opt-out of its own.
- **Task-list nudge (Claude Code)** — a synchronous `PostToolUse` hook (`kcap hook --claude --task-list`) on the agent's own progress records: Claude's task tools (`TaskCreate`, `TaskUpdate`, `TodoWrite`, which recent models only get with `CLAUDE_CODE_ENABLE_TODO_TOOLS=1`) and writes to a workflow ledger named `progress.md`, as Superpowers' plan-execution skills keep, plus the `kcap-plans` writes. While the session has written no kcap plan tasks (a declared document alone does not count), the agent is reminded to mirror its tasks through `set_plan_tasks` and `update_plan_task`: once when it starts the list or ledger and once when it first records a finished task. Once the session has plan tasks, the hook keeps the statuses the plan tools last reported, and a ledger line (`Task 3: dispatched`, `Task 3: complete`) or an implementer dispatch (`Implement Task 3: …`) that the plan has not caught up with gets one reminder naming the `task_id` and status to send. Subagents' task lists are left alone. Local only, with no server round trip. Same gate and opt-out as the SessionStart plans nudge.
- **Session title prompt (Claude Code)** — on a session's first prompt, a `UserPromptSubmit` hook (`kcap hook --claude --title-prompt`) asks the agent to name the session with `kcap set-title --auto`, which never replaces a title chosen at start or a rename. The SessionStart hook also exports `KCAP_SESSION_ID` through Claude's `CLAUDE_ENV_FILE`, so the `kcap` commands the agent runs find their own session. Both run inside the native `kcap` binary, so neither needs bash or `jq`.
- **SessionStart session id** — when neither nudge above is shown but at least one kcap MCP server is registered for the harness, `kcap` appends one line stating the current session id, so a tool that cannot resolve the session by itself can still be handed it. Either nudge already carries the id, so the line is left out whenever one of them is shown. It has no opt-out of its own.
- **SessionStart coordination notices** — at every session start (Claude Code / the generic route only) `kcap` advertises a `coordination_notices` capability on its `/hooks/session-start` request, and when the server has pending coordination notices for you — a heads-up that other people have in-flight work that may overlap yours (work-overlap / work-item adjacency) — it appends a `## Coordination notices` block to the session's injected context (`additionalContext`), one short line per notice (bounded, with a `+N more in the notification centre` tail when there are more). The same notices always reach the in-app notification centre and Slack regardless; this block just surfaces the most relevant few directly in the agent's context at the moment you start. Best-effort and fail-open (a missing or malformed field injects nothing, never blocking the hook), and the capability is advertised only on a live session start — never from `kcap import`/backfill. Opt out with `disable_coordination_notices: true` in `~/.config/kcap/config.json` or `kcap config set disable_coordination_notices true`; when set, the capability is not sent at all, so the notices stay in the notification centre / Slack only.
- **SessionStart next work** — at every live session start (Claude Code / the generic route only) `kcap` advertises a `next_work` capability on its `/hooks/session-start` request, and when the server's next-work feed has rows for you it appends page one of it (up to three rows, each with its because-clause and link) inside a `<next-work-data>` block, followed by guidance to finish a listed item before starting new work, to declare a loose end with `declare_loose_end` at the moment something is deferred (a flaky test that failed and then passed on a rerun, hit and left unfixed, included), and at completion to call `get_next_work` and tell the user what to consider next. Row text is sanitised and marked as data. The capability is never sent from a spooled replay or `kcap import`. Opt out with `disable_nextwork_nudge: true` in `~/.config/kcap/config.json` or `kcap config set disable_nextwork_nudge true`; when set, the capability is not sent, nothing is injected, and the completion nudge below is never written.
- **Completion nudge (Claude Code)** — when the server's `/hooks/stop` ack asks for it (`completion_nudge: true`) and the agent's last message reads as a wrap-up, the Stop hook blocks the stop once and asks the agent to declare its remaining loose ends and present next work from `get_next_work`. It is never written while the agent is already continuing from a Stop block, when the `kcap-workitems` tools are not registered, or under `disable_nextwork_nudge`.
- **First-run notice** — the session that runs `kcap setup` has no hooks, skills or MCP servers, because an agent reads those when it starts: it is not recorded, and it cannot run the guided tour. Setup leaves a one-shot marker, and the next session that starts with hooks in place opens with a short block saying setup completed and kcap's hooks are loaded, and offering the guided tour where the MCP servers it reads through are registered. It claims no more than that: not that this is the first recorded session (re-running setup arms it again), and not that the session reaches the server — a rejected token already has its own notice. It is armed only when setup installed something, claimed under the config lock so several agents starting at once deliver it once between them, and suppressed by `kcap config set disable_first_run_notice true` (which leaves the marker alone, so re-enabling before the next session still delivers it).
- **Crash resilience** — if a `kcap` command hits an unexpected error it records the exception (with stack trace) to `~/.config/kcap/crash.log` (honours `KCAP_CONFIG_DIR`; size-capped) and exits cleanly instead of aborting. Hook and detached-generator commands the coding agent spawns **fail open** (exit 0, nothing surfaced to the agent); other commands exit non-zero with a one-line stderr message pointing at the log.

The SessionStart memory foundation is deliberately separate from harness activation. Every row uses
the same typed fetch/render, lifecycle, fenced lease, and golden output contracts. Claude, Cursor,
Codex, Copilot, Gemini, Kiro, Pi, OpenCode, and Antigravity are wired; each remaining adapter is activated and
live-certified by its own AI-1456 child issue.

| Harness | Shared foundation | Hook/extension wired | Live receipt | Upstream status |
|---------|-------------------|----------------------|--------------|-----------------|
| Claude Code | yes | yes | existing baseline | available |
| Codex CLI | yes | yes (`SessionStart`'s `hookSpecificOutput.additionalContext`, combined with the `continue` handshake) | **certified** — gated live cert, `codex-cli 0.144.3`, needs kcap ≥ the release carrying this adapter | available |
| Cursor CLI (`cursor-agent`) | yes | yes | manual, recorded live-cert gate (needs `cursor-agent` + a reachable server + a memory in scope; not run in CI) | available — **supported** |
| Cursor IDE (Agent Window) | yes | yes (same adapter — `sessionStart`'s `additional_context`) | **upstream-degraded**: the hook emits the correct JSON, but whether the IDE's Agent Window actually surfaces it to the model is not guaranteed — a known Cursor IDE limitation, not a `kcap` defect | hook output correct; model receipt not guaranteed |
| GitHub Copilot CLI | yes | yes (`sessionStart`'s top-level `additionalContext`; silent when there is nothing to inject) | **certified** — gated live cert, `copilot 1.0.75` | available |
| Gemini CLI | yes | yes (`sessionStart`'s top-level `additionalContext`; falls back to the plain `{"continue":true}` allow payload when there is nothing to inject) | pending | available |
| Kiro CLI | yes | yes (`agentSpawn` raw stdout — no envelope; Kiro appends hook stdout to agent context verbatim) | **certified** — gated live cert, `kiro-cli 2.12.1`. The once-per-session dedupe is unit-covered only: a resumed `kiro-cli` invocation carries a different hook session id, so it cannot certify it | available — injects **once per session** despite `agentSpawn` firing every prompt |
| Pi | yes | yes (extension: the session-start hook prints the fragment raw on stdout — zero bytes when there is nothing to inject — and `kcap.ts` appends it to each turn's chained system prompt in `before_agent_start`; the fetch stays once per session behind the durable lease, keyed on the session **file**, so resume dedupes and fork re-fetches) | pending — gated cert shipped (`KCAP_PI_MEMORY_LIVE=1` + `KCAP_URL`, positive + negative control, echo-detector guard), not yet run live | available |
| OpenCode | yes | yes (plugin `experimental.chat.system.transform` — the fragment is appended as a new system entry, never replacing one; the CLI writes it raw on the start hook's stdout and zero bytes when there is nothing to inject) | **certified** — gated live cert, `opencode 1.18.9`, positive case plus negative control. Injection rides an **experimental** OpenCode API, so this cert is the only thing that would notice an upstream change silently ceasing delivery — it already earned that: it caught a start/first-request race no unit test could see, where a one-turn session got no index at all. Needs the plugin AND the `kcap` on `PATH` to be the same build; the positive case asks a model to echo 32 random hex characters, so a small free model flakes on transcription — the failure message distinguishes that from a delivery failure | available; transform contract measured on `opencode` 1.18.9. Appends on **every request** (OpenCode rebuilds the system array per request), while the fetch stays **once per session** behind the durable lease |
| Antigravity | yes | yes (`PreInvocation` → `{"injectSteps":[{"userMessage":…}]}`; zero bytes when there is no index) | **certified — interactive CLI** (a real `agy` 1.1.11 session on 2026-08-07 carried the injected index as its own transcript event). **Print mode (`agy -p`) injects too as of the workspace fallback**: agy honours `injectSteps` in print mode (probe-verified — injected steps land in the transcript on every invocation), but sends `"workspacePaths": []` where interactive sends the launch dir, which starved the index fetch of its scope; the hook now recovers the workspace from the `agy` process's own cwd when the payload omits it (payload always wins, so a vendor fix takes over silently). Two operational facts, both measured on 1.1.11: hooks do not fire at all under `AGY_ADC_AUTH=1` (ADC/Vertex auth) — capture and injection both require the OAuth login; and the gated live cert (`KCAP_ANTIGRAVITY_MEMORY_LIVE=1` + `KCAP_URL`) must therefore run under OAuth. **Antigravity 2.0 (GUI app): certified 2026-08-08** — a real app conversation on the shipped 0.11.14 was captured AND injected (model echoed the first memory slug under a tool ban; the index marker appears in the conversation checkpoint; no tool calls in the transcript), so the app populates `workspacePaths` itself and needs no fallback | available — interactive CLI (certified) and print mode (workspace fallback); nothing under ADC auth (vendor: hooks disabled); injects **once per conversation** despite `PreInvocation` firing every invocation |

## CLI commands

At a glance — each links to its section below:

| Command | What it does |
|---------|--------------|
| [`kcap setup`](#initial-setup) | Interactive wizard — server, auth, agent hooks, daemon (`--local` runs the server on this machine) |
| [`kcap local`](#local-server-kcap-local) | Status, start, stop, restart, upgrade, logs, web UI and uninstall of the server `kcap setup --local` installed |
| [`.kcap/approvals.yaml`](#approval-policy) | Auto-allow, deny, or force-ask specific tool calls |
| [`kcap import`](#loading-historical-sessions) | Backfill past sessions from every detected agent |
| [`kcap muse-assistant`](#muse-assistant) | Record Muse Assistant chats as private sessions, run by Muse's own cron inside its VM |
| [`kcap recap`](#session-recap) | AI summary + per-turn outline of a session |
| [`kcap sessions`](#listing-sessions-on-a-repository) | List the sessions you can see, on one repo or all, optionally within a period |
| [`kcap validate-plan`](#plan-validation) | Check that every planned item was completed |
| [`kcap hide`](#hide-session) | Mark a session owner-only |
| [`kcap disable`](#disable-recording) | Stop recording and delete server-side data |
| [`kcap errors`](#error-extraction) | Extract tool-call errors from a session |
| [`kcap eval`](#session-evaluation-llm-as-judge) | Score a session with LLM-as-judge |
| [`kcap review`](#pr-review-with-full-context) | Launch a PR review with full transcript context |
| [`kcap mcp <server>`](#sessions-mcp-server-for-agents) | Run an MCP server (sessions / flows / memory / …) for agents |
| [`kcap curate apply`](#curate-guidelines) | Sync promoted guidelines into `CLAUDE.md` / `AGENTS.md` |
| [`kcap memories import`](#import-local-memories) | Copy Claude Code's local memory notes into server memories |
| [`kcap page`](#pages) | Publish a self-contained HTML page and get a link to share |
| [`kcap skills sync`](#skills-sync) | Materialize the repo's approved skill docs into every present harness's skills tree |
| [`kcap daemon …`](#daemon) | Run and manage the agent daemon |
| [`kcap agent`](#local-agents-kcap-agent) | Start, list, attach to, and stop daemon-hosted agents |
| [`kcap repos`](#repository-paths) | Manage known repo paths for the launch dialog |
| [`kcap projects` / `project`](#projects) | List and inspect projects |
| [`kcap accounts`](#accounts) | Record every Claude and Codex account, not only the default |
| [`kcap statusline`](#claude-status-line-kcap-statusline) | Internal: the status line wiring installs in each Claude account; not run by hand |
| [`kcap profile` / `use`](#profiles) | Manage and switch between servers/profiles |
| [`kcap machine`](#machine-credentials-headless-recording) | Create credentials for CI runners and agent sandboxes |
| [`kcap config`](#configuration) | Show and set configuration |
| [`kcap experimental`](#codex-app-server-transport) | List experimental features and turn one on or off for the profile in use |
| [`kcap remap`](#renamed-repo-directories-kcap-remap) | Map renamed repo directories for import |
| [`kcap ignore`](#configuration) | Exclude paths from recording |
| [`kcap allow`](#configuration) | Restrict recording to named paths |
| [`kcap update`](#other-commands) | Upgrade the CLI and refresh agent plugins |
| [`kcap refresh`](#other-commands) | Re-run the agent-integration refresh after an install or update |
| [`kcap uninstall`](#uninstalling) | Remove kcap from this machine |
| [`kcap status` / `whoami` / `login` / `logout`](#other-commands) | Health, identity, and auth |
| [`kcap harness`](#new-harness-detection) | List / dismiss / reset the "set kcap up for this agent" nudges |
| [`kcap feedback`](#other-commands) | Report a bug or send feedback to Kurrent support |

### Initial setup

```bash
kcap setup                                   # interactive wizard (local, cloud, or a workspace you have)
kcap setup <tenant>                          # shorthand: https://<tenant>.kcap.ai
kcap setup --server-url <url> --no-prompt    # CI / scripted
kcap setup --local --single-user --yes --no-prompt   # local edition, unattended
kcap setup --server-url <url> --no-prompt --json     # …with JSON progress for a wrapper
kcap setup --discover [--json]               # report workspaces only, configure nothing
kcap setup --org "Acme" --slug acme --no-prompt   # create a workspace, unattended
kcap setup --local                           # run a kcap server on this machine
kcap setup --local --embedding-model <model> # …embedding with this Ollama model
kcap setup --local --builtin-embeddings      # …embedding with the built-in model
kcap local uninstall [--purge]               # remove it, keeping (or deleting) its data
```

The install scripts run `kcap setup` after a first install, so this is usually how you met it; re-run it any time.

`kcap setup --discover` stops after the sign-in and reports the workspaces it found, configuring nothing — no profile, no token, so the run that follows signs in again. It is for a tool that has to ask someone which workspace to use before it can name one. It takes only `--json`, `--github`, `--device` and `--no-prompt`: a workspace argument would answer the question it exists to ask, and any other option has nothing to apply to, so both are refused. With `--json` the report is the only thing on stdout (the sign-in narrates on stderr):

```json
{"workspaces": [{"slug": "acme", "url": "https://acme.kcap.ai", "name": "Acme Corp"}],
 "can_create": false, "provider": "workos"}
```

`provider` is `workos` for org SSO and `GitHubApp` for `--github`. `slug` and `name` are null when the provider gives none — a GitHub sign-in identifies a workspace by `url` alone. `can_create` means no workspace was found and this sign-in is the one that can create one (`kcap setup --org … --slug …`); it is never true for `--github`, and it is not a promise — a workspace already being created for the account is only reported by the create itself.

On `kcap login` the same flag means something else: `kcap login --discover` forces discovery even when a server is configured, and saves the workspace you pick.

When org SSO finds you in **more than one** workspace, that pick happens in your browser rather than in the terminal: kcap opens a page listing the workspaces you belong to, each with its address, and waits. The link is printed as well as opened, and **pressing any key goes back to choosing in the terminal** — so a machine whose browser never appears is never stuck. The pick also stays in the terminal when you signed in with a device code (there is no browser to open), when you signed in with `--github`, and against a server whose auth service does not offer the page. One workspace still selects itself and no workspace still offers to create one, so this only appears when there is a genuine choice to make.

SSO discovery signs in through a `127.0.0.1` browser callback, which a browser on another machine can't reach. So it also offers a **device code**: kcap prints a URL and a short code, and you approve on whatever machine has a browser. Press `d` while the browser sign-in is waiting to switch to it, or pass `--device` up front to skip the browser entirely — the flag works the same way for org SSO and for GitHub. A run whose input is redirected has no key to press, so it goes straight to a device code. `--server-url <url>` remains the way to configure a workspace you already have, and `--github` still routes discovery to the legacy GitHub App path, which is being phased out.

The setup wizard walks you through the steps below. Re-run it any time to update the configuration.

1. **Server** — a bare `kcap setup` in a terminal first asks the desktop app's question, how you want to run kcap: **For you, locally** runs the [local edition](#local-edition) (offered on macOS only); **For your team, cloud** opens kcap-web's sign-up in your browser, where you create the workspace, and once you press Enter carries on to discovery, which finds it; **Already have a workspace?** goes straight to discovery. With no `--server-url`/`<tenant>`, kcap **discovers** your tenant: it signs you in with your organization's single sign-on (pass `--github` to use GitHub instead), then lets you choose from the tenants you belong to. A bare `<tenant>` slug expands to `https://<tenant>.kcap.ai`; a full URL is used as-is. If you sign in with your organization's single sign-on and discovery finds no kcap tenant, `kcap setup` asks how to continue: create one for you (name, workspace URL and plan, with the 30-day Team trial offered first; provisioned and waited for until it's live), point at a workspace you already belong to (enter its slug or URL — the same as `kcap setup <tenant>`), or cancel. That middle choice matters because SSO discovery only lists workspaces that use org SSO: a workspace whose members sign in with the GitHub App shows up here as "no tenant", so pick **I already have a workspace**, or re-run with `--github`.
2. **Login** — authenticates via your tenant's configured sign-in method; discovery completes the sign-in inline

   **Finishing in the browser.** Once you are signed in, if that server offers browser setup, kcap creates a setup link, opens your browser on it, and waits while you work through the screens there:

   ```
     Opening your browser to finish setup.
     If it didn't open:  https://acme.kcap.ai/setup?s=b7f3…

     ✓ Signed in
     ✓ Agents: Claude Code, Pi
     ⠋ Choose what to import in the browser
       t to carry on here  ·  ctrl+c to stop
   ```

   Each screen you finish ticks up in the terminal, above one line saying what the browser is waiting on. The link is printed as well as opened, so a machine with no browser of its own can be finished from a browser on another. Press **t** to stop waiting and carry on in the terminal. In a terminal the offer stops being shown once you have answered a screen, because a decision made in the browser after that would go unseen — the key still works, since a closed tab needs it. With output redirected there is no pinned line to withdraw it from, so it is said once and stands. Setup carries on in the terminal whatever happens in the browser — the steps below run either way. Skipped on servers that do not offer it, on servers that need no sign-in (auth provider `None` — there is no identity for a flow to belong to), and under `--no-prompt`.

   kcap reports the coding agents it found on this machine — which are on your `PATH`, which have config on disk, which kcap is already wired into, and whether your login shell can find `kcap` — since only the machine can know that, and on a device-code sign-in the browser is a different box entirely. **Whatever you choose there is what step 4 installs**, per agent and per choice, instead of the terminal prompt; a `--skip-<agent>` flag still wins, and "Not now" installs nothing.

   That screen also asks who may read the sessions this machine records from now on, and **step 3 then reports that answer instead of asking again**. Declining every agent and still choosing an audience is a coherent answer, so the two are read separately. Answer that screen without setting an audience and step 3 keeps whatever your profile already has, rather than re-asking — its prompt preselects the workspace's default (`org_public` on a server that cannot say), so re-asking could widen a narrower setting you never changed. If the screen was never answered at all, step 3 prompts as before.

   **The Import screen's figures come from this machine, and its answer runs here.** Once you have answered the Agents screen, kcap scans for past sessions and reports what it found — per repository and per history window (last 30 days / last 90 days / everything), plus how many sessions could not be attributed to a repository at all. It scans only the agents you kept: an agent you were offered and left off is skipped, while one with history on disk but nothing installed now was never offered, so its history still counts. The report is capped at 200 repositories, newest activity first, and the screen says how many it is not showing.

   When you answer that screen, **the newest sessions import in the terminal while you are still on the browser's last screen** — about five from repositories you kept to yourself (`--private`) and about five from those you shared, each scoped to the window you chose. The rest of each choice continues in a detached background import that logs to `~/.config/kcap/import-<run>.log` and outlives setup, so waiting resumes as soon as the newest sessions land, and step 6 below reports what ran (and that the rest is still importing) instead of asking again. The Done screen, and the end of setup in the terminal, offer a `Follow my kcap import` prompt to paste into a coding agent so you can watch the import and its evals from there. A shared repository gets an explicit per-session visibility write, because leaning on the profile default would deliver owner-only on any server that is not using the shared GitHub App.

   **The browser can ask this machine to do two named things while you wait.** If your login shell cannot find `kcap`, the Agents screen offers to link it, and pressing that button runs the same `kcap daemon shim ensure` you could run yourself — so on macOS **you will be asked for your admin password** while you are still looking at the browser. kcap prints a line naming what asked, before the dialog appears. The Done screen separately offers to run the agent daemon as a service, so this machine stays reachable when you are not at the keyboard: that runs the same `kcap daemon service ensure`, needs no password (all three service managers are per-user), and **happens as the rest of setup finishes** — the service unit bakes the profile, the server URL and the daemon name, so it waits until the steps that choose those have written them, and the screen says so while you wait. Each screen reports what happened either way; nothing is attempted unless you press the button, and **only a named capability can be requested** — what crosses is a token from a closed set, never a path or a command, so the browser cannot ask this machine to run anything else.
3. **Default visibility** — choose how your sessions are visible to others. Answered on the browser's Agents screen, this step reports that choice rather than prompting
4. **Coding-agent hooks** — detects each agent by any one of several signals: Claude Code by `claude` on `PATH`, files it writes under `~/.claude/` (history, file history, shell snapshots) or the Claude desktop app; Codex by `codex` on `PATH`, files it writes under `~/.codex/` (sessions, sign-in, history) or the Codex app inside ChatGPT; Cursor by user-dir presence (`~/.cursor/`), GitHub Copilot CLI by `~/.copilot/` or `copilot` on `PATH`, Google Gemini CLI by `~/.gemini/` or `gemini` on `PATH`, AWS Kiro CLI by `~/.kiro/` or `kiro`/`kiro-cli` on `PATH`, Pi by `~/.pi/` or `pi` on `PATH`, SST OpenCode by `~/.config/opencode/` (or `~/.local/share/opencode/`) or `opencode` on `PATH`, and Google Antigravity by `~/.gemini/antigravity/` (GUI) or `~/.gemini/antigravity-cli/` (the `agy` CLI) or `antigravity`/`agy` on `PATH`, Mistral Vibe by `~/.vibe/` or `vibe` on `PATH`, Grok by `~/.grok/` or `grok` on `PATH`, and Muse Code by `~/.config/muse/`, `~/.local/share/muse/` (honouring `XDG_CONFIG_HOME` and `XDG_DATA_HOME`) or `muse` on `PATH`. Cursor, Kiro, Antigravity, OpenCode and Muse Code are also found through their desktop apps on macOS, and Claude, Cursor, Kiro and Antigravity through theirs on Windows. Setup lists what it found, then asks **one** yes/no prompt to install kcap for every detected agent (hooks — or, for Pi/OpenCode/Antigravity, the live-ingest plugin — plus skills, instructions, and MCP) — plus a single shared set of agent skills under `~/.agents/skills/`, installed once when any of Codex, Cursor, Copilot, Gemini, Pi, OpenCode, Mistral Vibe, Grok, or Muse Code is detected (Claude gets its skills from the bundled plugin; AWS Kiro and Google Antigravity read their own skills dirs — `~/.kiro/skills` and `~/.gemini/skills` respectively — so each gets its own copy there instead of the shared tree) — all user-wide. For Codex it also offers to enable **sandbox network access** for kcap (see [Unattended setup](#unattended-setup)) — Codex blocks sandbox network by default, so the kcap skills can't reach the server without it. Each agent's own config-relocation environment variable is honored when set: `CLAUDE_CONFIG_DIR` (Claude), `CODEX_HOME` (Codex), `GEMINI_CLI_HOME` (Gemini — names the parent of `.gemini`), `KIRO_HOME` (Kiro), `COPILOT_HOME` (Copilot), `OPENCODE_CONFIG_DIR` (OpenCode), `PI_CODING_AGENT_DIR` (Pi), `VIBE_HOME` (Vibe — replaces `~/.vibe`), `GROK_HOME` (Grok — replaces `~/.grok`), and `XDG_CONFIG_HOME` / `XDG_DATA_HOME` (Muse — relocate `muse/settings.json` and `muse/sessions`). Cursor's hooks path is fixed at `~/.cursor/hooks.json` and is not relocated. Setup also offers any other Claude Code or Codex config directories it finds, so each is recorded as an account (see [Accounts](#accounts)); with `--no-prompt`, or on a console that cannot prompt, it prints the `kcap accounts add` command instead.
   When it installs any agent's hooks it also adds one entry to `~/.gitconfig`, a git 2.54+ config hook named `kcap` (`kcap plugin install` does the same). After every commit git runs `kcap git-hook`, which files the commit under the session of the agent process above it, so a commit made from a subfolder, a nested repo, a script or any agent lands in the right repository and pull request. Antigravity, Cursor and OpenCode are the exception: one of their processes runs several sessions and your own terminals, so their commits are still read from the agent's shell commands. It runs beside hooks you already have (husky, pre-commit) and never replaces them. `git hook list post-commit` shows it, `git config --global hook.kcap.enabled false` switches it off, and on an older git the server keeps reading commits from the agent's shell commands — setup adds the hook entry, then checks `git --version` and warns when git is older than 2.54 (or when it cannot read the version). When it installs Codex hooks, setup also reminds you that Codex records nothing until you trust them.
5. **Daemon** — name the daemon for remote agent execution; setup then installs it as a background service and starts it, so it runs after a restart and its service manager restarts it if it dies. The name prompt suggests `--daemon-name`, else `KCAP_DAEMON_NAME`, else the profile's daemon name, else this machine's name — and when a daemon on another machine of your account already holds that name, the first free of `name-1`, `name-2`, and so on, so accepting the suggestion never collides. A name you type that is held elsewhere is asked again. When the name differs from the one the profile had, setup removes the old name's service as it installs the new one (macOS and Windows), so the machine keeps one daemon; an old service it cannot remove — one belonging to another profile, one whose daemon is busy, any when the new name already had a service, or any on Linux — is left in place, and setup prints the `kcap daemon service uninstall --name <old>` command for it. Under `--no-prompt` the suggestion is used, except that a `--daemon-name` held elsewhere exits 1 (the daemon verb is `kcap daemon`; `kcap agent` is a separate group that runs coding agents — see [Local agents](#local-agents-kcap-agent)). **After a browser answer this step does not prompt:** it uses the suggested name. The provider API key question (asked when `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` is set) is skipped too and the stored setting kept — change it with `kcap config set use_provider_api_key true|false`.
6. **Import past sessions** — scans every detected agent's history on this machine and prints how many repositories and sessions it found and how many sessions matched no repository, then asks (default yes) `Import past sessions from this machine?` — equivalent to `kcap import --all` under your profile's default visibility (shared only where the repository owner is your workspace's org). The most recent sessions are prioritized: about five import while you watch, and the rest continue in a detached background process that logs to `~/.config/kcap/import-<run>.log`. Setup then offers to open one of your detected coding agents with the prompt `Follow my kcap import` so you can watch the import and its evals from there (or prints the prompt to paste), and finishes when you close it. Your authentication requirements must be satisfied — which includes no-auth servers (auth provider `None`, no token needed); otherwise the step is skipped with the usual `kcap import` hint. Opt out with `--skip-import`; an import chosen on the browser's Import screen runs regardless. **Answered in the browser, this step reports rather than prompts** — the screen there chooses repositories and a history window this prompt cannot express, so re-asking would offer to redo part of what already ran. A browser flow that finished without an import answer skips this step too and names `kcap import --all` for later; handing back to the terminal with **t** keeps the prompt.

- **New tenant:** when signing in via Kurrent's hosted auth and you have no tenant yet, `setup` prompts to create one (organization name + `<slug>.kcap.ai` workspace URL) and waits up to ten minutes for it to answer; if it still does not, setup stops and names the `kcap setup --server-url <url>` to run once it is up. A listed workspace whose address answers 404 — removed, though its sign-in organization remains — is left out, unless it is still being created. Pass `--org "<name>" --slug <slug>` to answer both up front, so the fork raises no prompt, and `--tier team` or `--tier free` for the plan. Omitted, it is Free, or the Team trial while Free cloud workspaces are closed; a named `--tier free` is never moved. An account that cannot have the Team trial (a personal email address, or a domain that has already had one) gets a Free workspace instead, and setup says why. Add `--no-prompt` for a script or a session with redirected input: these two flags only settle the creation questions, and the steps after it still ask. The two are both-or-neither: the slug becomes a permanent public hostname, so kcap will not derive one for you. A slug that is taken or malformed ends the run naming it, rather than asking for another. While creating a workspace is invite-only, setup asks for an invite code (three tries) and names where to request one; with `--org`/`--slug`, pass it as `--invite-code <code>` instead, since that run asks nothing. Only an account with no workspace yet can create one, so if yours already has a different one, setup signs in, stops, and tells you — it will not go on to configure that workspace as if you had asked for it. Without the flags, a non-interactive run exits with guidance.

The closing summary lists, under **Changed on this machine**, every agent config file and skills directory the run installed kcap into, plus the git config that carries the commit hook (`~/.gitconfig`, or `~/.config/git/config` when only that exists).

When setup finishes, `kcap` sends a best-effort POST to the server's `/api/users/me/cli-setup` endpoint so the dashboard can mark your CLI as registered and surface the import-past-sessions hint. The call is capped at 5 seconds and failures are silent — they do not affect setup completion.

Setup closes with a **Next steps** box. Each item opens with a question, because neither step is for
everyone:

- **Did you create this kcap server?** Complete server setup — inviting teammates, and
  optionally Slack and your own AI keys — by following
  [Setup Server](https://capacitor.kurrent.io/docs/getting-started/setup-server/). Always listed:
  `kcap` can't tell whether you own the server, so you self-select.
- **New to kcap?** Prompt **"Start kcap guided tour"** in your coding agent (or, in Claude Code,
  `/kcap:guided-tour`) for seven short cards, one per reply — welcome, capture, investigate,
  collaborate, review, use, reuse — each a few questions to ask your agent. It's a prompt rather than a slash command because
  only Claude Code has slash commands — the skill ships with the plugin and is also installed for
  Codex and the other `~/.agents/skills/` agents (plus Kiro and Antigravity) as `kcap-guided-tour`.
  This item only appears when an agent was detected and one of them carries the skill.

#### Local edition

`kcap setup --local` installs and starts a kcap server on this machine instead of using a hosted workspace, then carries on with the usual login, hooks and daemon steps against it. It is macOS on Apple silicon only for now.

The first version is for a machine with one user: on a shared machine, other OS users can open the web UI and act as its admin. Setup therefore asks you to confirm that (`--single-user`) and to confirm the install itself (`--yes`); `--no-prompt` and `--json` cannot ask, so they require both for an install. Re-running `kcap setup --local` reuses the existing installation, which needs no confirmation beyond `--single-user`.

**Search by meaning.** Finds related information saved in kcap even when your query uses different words. Keyword search matches words and needs no AI model. Setup offers three choices: the built-in model, which setup downloads once (its size is shown first) and the local server runs itself; Ollama; or keyword search only. kcap does not install Ollama. When [Ollama](https://ollama.com) is running on `http://localhost:11434`, setup offers it as the server's embedding provider, suggesting `mxbai-embed-large` (1024 dimensions, about 670 MB). It pulls the model if Ollama does not have it, checks that it embeds text, and writes the choice to `data/embedding.json` under the installation, which the server's service passes to it as `Embedding__Provider=ollama`, `Embedding__Ollama__Model` and `Embedding__Ollama__BaseUrl`. A new installation starts with it; on an existing one a changed model restarts the server. The model fixes the width of the stored vectors, so changing it later means embedding everything again, which the server does in the background.

- `--builtin-embeddings` picks the built-in model, `--embedding-model <model>` an Ollama model, without asking. `--no-embeddings` searches by keyword only, removing a model an earlier setup configured, and is remembered: a later setup without an embedding flag, `--json` included, leaves search by keyword. An embedding provider set in the web UI's settings still applies.
- With no embedding flag, a recorded choice stands, and a recorded built-in model that is missing or damaged is downloaded again. With nothing recorded, setup uses Ollama when it is running and the built-in model otherwise; `--no-prompt` and `--json` take that without asking.
- When neither is available, setup carries on and search works by keyword. A failed download or pull, or a model that cannot embed, is reported the same way and never fails the install.

- A machine that already has a profile for another server is refused for now.
- Every kcap that talks to the local server must be newer than 1.3.0, which does not keep to its rules. Setup asks the `kcap` on `PATH` and the desktop app's bundled one (in `/Applications` or `~/Applications`) for their versions, and stops if either is 1.3.0 or older, or does not report one: update it, or remove the one on `PATH`. The local server refuses an older kcap too, so its hooks and watcher record nothing there.
- Builds of kcap for musl Linux are not supported.
- `--local` cannot be combined with `<tenant>`, `--server-url`, `--discover`, `--org`, `--slug`, `--tier`, `--invite-code`, `--device`, `--github` or `--muse-assistant`, and is refused while `KCAP_URL` is set.

`--json` without `--no-prompt` is for the desktop app. It writes one JSON object per line to stdout, ends with `committed` or `failed`, and runs only the server and login steps. (`--json --no-prompt` reports the whole run instead; see [Unattended setup](#unattended-setup).) It accepts only `--single-user`, `--yes`, `--no-update-check`, `--embedding-model`, `--no-embeddings` and `--builtin-embeddings`, and without one of the last three chooses as `--no-prompt` does. The built-in model reports `embeddings_builtin_downloading` (with `bytes`), `embeddings_builtin_progress` and `embeddings_configured` with `"provider":"builtin"` in place of the Ollama events:

```json
{"v":1,"event":"preflight_passed","plan":"install","version":"0.13.0","root":"/Users/me/.config/kcap/local"}
{"v":1,"event":"port_chosen","port":9473}
{"v":1,"event":"downloading","url":"https://…","bytes":84213760}
{"v":1,"event":"verified","sha256":"…"}
{"v":1,"event":"embeddings_ollama_found","version":"0.12.3","url":"http://localhost:11434"}
{"v":1,"event":"embeddings_model_pulling","model":"mxbai-embed-large"}
{"v":1,"event":"embeddings_model_progress","received":335544320,"bytes":669603840,"model":"mxbai-embed-large"}
{"v":1,"event":"embeddings_model_ready","model":"mxbai-embed-large","dimensions":1024}
{"v":1,"event":"embeddings_configured","provider":"ollama","model":"mxbai-embed-large"}
{"v":1,"event":"starting","unit":"…"}
{"v":1,"event":"ready","server_url":"http://localhost:9473"}
{"v":1,"event":"committed","server_url":"http://localhost:9473","installation_id":"…","port":9473,"version":"0.13.0"}
```

A failed run ends with `{"v":1,"event":"failed","code":"…","message":"…"}`; after `rolling_back` and `rolled_back` the installation is removed, and its `logs/` directory is kept for diagnosis. The exit code is 0 for `committed` and 1 for `failed` (2 when the code is `answer_required` or `invalid_arguments`: nothing was attempted), or 130 when an interrupt (Ctrl+C or SIGTERM) was received: the last line is still `committed`, or `failed` (with code `cancelled` when the interrupt rolled the install back). The other events are `recovering`, `recovered`, `download_progress`, `unpacked`, `reused` and `committing`.

The `embeddings_*` events come after `unpacked` for an install and after `preflight_passed` for a reuse, before `starting`. `embeddings_model_pulling` and `embeddings_model_progress` (one per whole percent) appear only when Ollama lacks the model, and `embeddings_configured` carries `replaced_model` when it replaced a different one. A step that does not end in `embeddings_configured` ends in `{"v":1,"event":"embeddings_skipped","code":"…","message":"…"}`, which never fails the install, unless the run is cancelled: an interrupt, including one during a built-in model download, takes the cancellation and rollback path and emits no `embeddings_skipped`. The skip's `code` is `declined` (`--no-embeddings`, now or in an earlier setup), `ollama_not_found`, `pull_failed`, `model_unusable` (the model cannot embed text), `write_failed`, `builtin_unavailable` (this version offers no built-in model, or its lookup failed), `model_download_failed` (the built-in model could not be downloaded) or `unrecognised_provider` (the recorded provider is one this kcap does not know, so it is left as it is). Unless it is `declined`, a model configured earlier stays configured.

For development and pre-release builds, `KCAP_LOCAL_BUNDLE_BASE_URL` points the download at another location and `KCAP_LOCAL_BUNDLE_VERSION` picks a different version. The manifest must still verify.

#### Removing the local edition

```bash
kcap local uninstall            # stop and remove the server, keep its data
kcap local uninstall --purge    # also delete its data and retire its URL
```

`kcap local uninstall` refuses while a daemon is running agents, unless `--force` ends them with the daemon. It then stops every kcap daemon and watcher and removes the daemon service, stops the local server and verifies that nothing of it is still running, and only then removes the server's service and its `bin/` and `run/` directories. If anything cannot be stopped, nothing is removed. Its data, its identity and your profile stay, so `kcap setup --local` brings the same installation back on the same URL, downloading the version it was running.

`--purge` goes on to retire the installation, then delete its `data/` and `logs/`, the pending recordings and the caches for it in the config directory, and clear the server URL of every profile there that points at it. A retired URL is never sent to again: a watcher still running, or a profile under another `KCAP_CONFIG_DIR`, is refused with an error naming the removed installation, and a later `kcap setup --local` creates a new installation on a new port. A purge that is interrupted is finished by the next `kcap local` command or `kcap setup --local`. `--yes` skips the confirmation.

#### Unattended setup

`kcap setup --no-prompt` is the path for a provisioning script, an agent sandbox or a fleet of developer machines; everyone else is better served by the [desktop app](#desktop-app). It never asks a question: every prompt has a flag, a question with a safe default takes it, and one without (the local edition's consents) is refused with exit code 2 before anything changes.

The local edition, on a Mac:

```bash
kcap setup --local --single-user --yes --no-embeddings --no-prompt
```

The cloud edition, against a workspace that exists, with a [machine credential](#machine-credentials-headless-recording) in the environment:

```bash
export KCAP_CLIENT_ID=client_01ABC… KCAP_CLIENT_SECRET=…
kcap setup --server-url https://my-tenant.kcap.ai --default-visibility org_public --no-prompt
```

Without `--default-visibility`, `--no-prompt` takes the workspace's own default, which is narrower than `org_public` where the workspace names no organization whose repositories are shared. An older server that cannot say keeps `org_public`.

`--no-prompt` requires a workspace (`<tenant>` or `--server-url`), `--local`, or `--org "<name>" --slug <slug>` to create the workspace as part of the run — those two supply the answers the creation prompts would have collected:

```bash
kcap setup --org "Acme" --slug acme --tier team --default-visibility org_public --no-prompt
```

**Signing in with no one at the machine.** Against a server that needs a sign-in, `--no-prompt` tries, in order:

1. A machine credential (`KCAP_CLIENT_ID` and `KCAP_CLIENT_SECRET`, from `kcap machine create`). Setup checks it with the server and signs in no person. If the server refuses it, or does not confirm it (no response, or any status but 2xx), the run fails (exit 1) rather than falling back to a person. A 404 is the exception: a server without the check endpoint cannot be asked, so setup carries on. A daemon service cannot carry the secret, so none is installed: start the daemon with `kcap daemon start -d` where the variables are set.
2. A token already stored for that server, from an earlier `kcap login` on this machine (`kcap login --device` signs in from a browser elsewhere).
3. Otherwise the sign-in as today: a device code (or the browser, on a machine that has one) that someone has to approve.

**Every question and its flag.** Under `--no-prompt` each takes the default shown.

| Question | Flag | `--no-prompt` |
|---|---|---|
| How do you want to run kcap? | `<tenant>`, `--server-url`, `--local`, `--org`/`--slug` | required |
| Re-run setup on a configured machine? | — | yes |
| Workspace to create (name, slug, plan, invite) | `--org`, `--slug`, `--tier`, `--invite-code` | — |
| This machine has one user (local) | `--single-user` | required |
| Install kcap local (local) | `--yes` | required, except to reuse an installation |
| Search by meaning (local) | `--builtin-embeddings`, `--embedding-model <model>`, `--no-embeddings` | the recorded choice; else Ollama when it runs, the built-in model otherwise |
| Default session visibility | `--default-visibility` | the workspace's default (`org_public` from a server that cannot say); `private` with `--local` |
| Install kcap for the detected agents? | `--skip-harnesses`, `--skip-<agent>-hooks` and friends | yes |
| Let Codex's sandbox reach the server? | `--skip-codex-network-access` | yes |
| Record another Claude/Codex config directory? | — (`kcap accounts add` afterwards) | no; the command is printed |
| Keep `ANTHROPIC_API_KEY`/`OPENAI_API_KEY` in headless calls? | `--use-provider-api-key` | the profile's setting |
| Daemon name | `--daemon-name` | the suggestion; a held `--daemon-name` fails |
| Import past sessions? | `--skip-import` | yes |
| Open an agent to follow the import? | — | not offered |

**Exit codes.** `kcap setup` exits:

| Code | Meaning |
|---|---|
| 0 | Done. |
| 1 | Failed: setup did not finish. What it changed before stopping stays, and re-running it carries on. |
| 2 | Refused: nothing was attempted — an argument was wrong, or a run that cannot ask was missing an answer. |
| 130 | Interrupted (Ctrl+C or SIGTERM). |

**Progress as JSON.** `--json` with `--no-prompt` writes one JSON object per line to stdout for the whole run, for a wrapper to render, and moves the usual narration to stderr. Each step opens with `step_started` and closes with `step_completed`, and the run ends with `done` or `failed`:

```json
{"v":1,"event":"step_started","step":"server"}
{"v":1,"event":"step_completed","step":"server","server_url":"https://acme.kcap.ai","provider":"WorkOS"}
{"v":1,"event":"step_started","step":"sign_in"}
{"v":1,"event":"step_completed","step":"sign_in","provider":"WorkOS","credential":"machine"}
{"v":1,"event":"step_started","step":"visibility"}
{"v":1,"event":"step_completed","step":"visibility","visibility":"org_public"}
{"v":1,"event":"step_started","step":"harnesses"}
{"v":1,"event":"step_completed","step":"harnesses","harnesses":["claude","codex"]}
{"v":1,"event":"step_started","step":"daemon"}
{"v":1,"event":"step_completed","step":"daemon","name":"laptop","outcome":"skipped"}
{"v":1,"event":"step_started","step":"import"}
{"v":1,"event":"step_completed","step":"import","outcome":"ran"}
{"v":1,"event":"done","server_url":"https://acme.kcap.ai"}
```

The steps are `server`, `sign_in`, `visibility`, `harnesses`, `daemon` and `import`. `credential` is `machine`, `stored`, `sign_in` or `none`; the daemon's `outcome` is `enabled`, `enabled_unverified`, `already_enabled`, `refused` or `failed` for its service, or `skipped` when none was set up; the import's is `ran` or `skipped`. With `--local`, the [install's own events](#local-edition) come inside the `server` step. A `failed` line names the step it stopped in and carries a `code`: the local install's codes, or `answer_required`, `invalid_arguments`, `server_unreachable`, `workspace_unavailable`, `sign_in_failed`, `machine_credential_rejected`, `daemon_name_held`, `cancelled`, or `setup_failed` for anything else, with the last error setup printed as its `message`. A refusal found before any step starts is a `failed` line alone; the local edition's consents are checked inside the `server` step, so their `answer_required` line follows its `step_started`.

In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks`, `--skip-codex-hooks`, `--skip-cursor-hooks`, `--skip-copilot-hooks`, `--skip-gemini-hooks`, `--skip-kiro-hooks`, `--skip-pi-hooks`, `--skip-opencode-hooks`, `--skip-antigravity-hooks`, `--skip-mistral-vibe-hooks`, `--skip-grok-hooks`, and/or `--skip-muse-hooks` (with `--skip-muse-mcp` to leave Muse's MCP block alone). When Codex hooks are installed, the wizard also enables Codex sandbox network access for your server(s) by default; pass `--skip-codex-network-access` to leave `~/.codex/config.toml` untouched.

To record only one agent, skip the others:

```bash
kcap setup --server-url <url> --no-prompt --skip-codex-hooks --skip-cursor-hooks   # only Claude
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-cursor-hooks  # only Codex
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-codex-hooks   # only Cursor
```

**Muse Assistant.** Muse runs kcap inside its own VM, where the only persistent place is `~` and the image ships a Codex that Muse itself never uses. `kcap setup <tenant> --muse-assistant` is the posture for that machine: it implies `--no-prompt`, `--device` and `--skip-import`, installs nothing for any harness (no Codex hooks, no `~/.codex/config.toml` MCP or network allowlist, no `~/.agents/skills`), defaults visibility to `private` (an explicit `--default-visibility` still wins), and prints — before its first network call — the hosts Muse's egress filter will ask the user to approve in the chat: the server's host and `phog.kurrent.io` first, then, once the server names its sign-in provider, `api.workos.com` and `signin.kcap.ai` (WorkOS) or `github.com` (GitHub). Choose **Always allow this site** on each card. Until a host is approved it resolves to a placeholder address, so a "cannot reach server" from this run means "not yet approved": approve the cards and run the same command again. The whole connection — install under `~/workspace`, this sign-in, the kcap MCP servers as a Muse custom connector, a `kcap` skill and an hourly `kcap status` cron — is driven by the `connect-kcap` skill (`kcap/skills/connect-kcap/SKILL.md`): paste it into Muse, or ask Muse to read it from `https://raw.githubusercontent.com/kurrent-io/kcap-cli/main/kcap/skills/connect-kcap/SKILL.md`, and say "Connect kcap".

```bash
npm install --prefix ~/workspace/kcap @kurrent/kcap
~/workspace/kcap/node_modules/.bin/kcap setup --server-url https://<tenant>.kcap.ai --device --no-prompt --muse-assistant
```

> **`--no-prompt` imports this machine's history.** The step 6 import defaults to yes like every other prompt, so `kcap setup --no-prompt` uploads every session on this machine (not only the current repository's), synchronously and without the agent handoff, when authentication requirements are satisfied. Add `--skip-import` to opt out.

#### After setup

> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kcap plugin install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--mistral-vibe|--muse|--grok]` (omit the flag for the Claude Code plugin), or pair Codex with `--project` for a per-repo install. Every per-vendor install also writes the agent skills to `~/.agents/skills/` (Kiro and Antigravity get their own copies under `~/.kiro/skills` and `~/.gemini/skills`), so `--skills` is only needed to install or refresh them on their own — for instance for an agent kcap has no integration for. Cursor uses user-scope only — `--project` has no effect with `--cursor`. After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). If you only use the Codex desktop app, it never prompts — trust the kcap hooks under Settings → Hooks in the app. After a `--project` install, also run `codex` once in the repo and accept the workspace trust prompt. Re-running after a kcap upgrade is rarely needed for user-scope installs — the npm postinstall hook auto-refreshes them on every `npm install -g @kurrent/kcap`, and `kcap update` refreshes them too (npm 11+ blocks install scripts by default — `kcap update` works regardless, or add `allow-scripts[]=@kurrent/kcap` to `~/.npmrc` to opt the postinstall in once).

> **Want an agent's kcap tools without recording its sessions?**
> Add `--tools-only` to `kcap plugin install --cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--mistral-vibe|--muse`. It writes that agent's MCP servers, skills and steering instructions (the `--skip-<vendor>-mcp`, `--skip-<vendor>-skills` and `--skip-<vendor>-instructions` flags still apply; Mistral Vibe and Muse Code have no instructions file, so only their MCP servers and skills are written) and nothing that records: no hooks, no Pi or OpenCode live-ingest file, no Antigravity capture plugin, no Kiro agent clone, default-agent switch or Kiro Crew hook, and no git hook. Only Pi's MCP bridge still needs `kcap` on `PATH`. Claude Code and Codex ship their tools with capture, and kcap installs no tools for Grok, so `--tools-only` is refused for those three (exit code 1). The upgrade-time `--if-installed` refresh keeps a tools-only install tools-only: it refreshes the tools and never adds capture. `--tools-only --if-installed` refreshes only the tools of any install.

Legacy `--plugin-scope <user|project|skip>` is retained for backwards compatibility:

- `user` — no-op (matches the new default)
- `project` — install the Claude Code plugin into `<repo>/.claude/settings.local.json`. Must be run from inside a git working tree; setup exits with an error otherwise.
- `skip` — alias for `--skip-claude-hooks`

New scripts should prefer `--skip-claude-hooks` / `--skip-codex-hooks` and `kcap plugin install --project` for project scope.

If you run `kcap setup` outside any git working tree, it still completes — hooks install user-scope and fire for every session, and the step 6 import still runs since it scans the whole machine rather than the current repo — but a tip at the end reminds you that sessions recorded from non-repo directories won't capture owner/repo/branch/PR context.

#### Agent-specific notes

> **Kiro Crew.** Kiro Crew runs your installed `kiro-cli` under agent profiles it regenerates at every start, keeping only executable `~/.kiro/hooks/*.sh` scripts as hooks and reading skills only from `~/.kiro/crew/skills`. So when Crew is present (`~/.kiro/crew`, or `KIROCREW_HOME`), `kcap setup` and `kcap plugin install --kiro` also write `~/.kiro/hooks/kcap-spawn.sh` and copy kcap's skills into `~/.kiro/crew/skills`; restart Kiro Crew once afterwards so its agents pick the hook up. A Crew sub-agent is nested under the chat session that spawned it. Because Crew keeps one `kiro-cli` process for a whole chat, a sub-agent's session ends when Crew marks it finished and a chat's session ends when Crew moves the chat to a new one, rather than waiting for the process to exit. When Crew moves a chat to a new Kiro session, the new session is chained after the one it replaced. Crew keeps only a chat's latest move, so `kcap import --kiro` can chain only that one; earlier moves are chained only if they were recorded live. A `kcap-spawn.sh` kcap did not write is left alone. `kcap setup --skip-kiro-hooks` skips the hook (an explicit `plugin install --kiro` always installs it), `--skip-kiro-skills` skips the skills on either command, `kcap plugin remove --kiro` removes both, and the upgrade-time refresh never brings back a hook or skills you deleted. Not on Windows, where Crew cannot run shell hooks.

> **Already-running sessions.** On a *first* `plugin install --kiro`, any Kiro session already running loaded no kcap integration, so it isn't captured live — the install names it and where it is. It is not lost: the agent writes its transcript to disk regardless, so `kcap import --kiro` backfills it once it ends. kcap deliberately does not offer to restart it, which would mean killing an interactive session on a terminal it does not own with no way to relaunch it. Nothing is printed when there is no such session, or when you re-run an install you already had — that session started *with* the integration and is being captured.

> **Pi** has no shell hooks, so live capture uses a shipped Pi extension rather than a hooks file: run `kcap plugin install --pi` (or accept the `kcap setup` prompt) to write `~/.pi/agent/extensions/kcap.ts`, which `pi` auto-loads and streams each session live. Because Pi also ships no built-in MCP, the same command installs an MCP-bridge extension (`~/.pi/agent/extensions/kcap-mcp.ts`, opt out `--skip-pi-mcp`) that exposes the kcap MCP servers as native Pi tools, plus a steering block in `~/.pi/agent/AGENTS.md` (opt out `--skip-pi-instructions`). Historical `kcap import --pi` works with or without any of it.

> **OpenCode** likewise has no shell hooks: live capture uses a shipped OpenCode plugin. Run `kcap plugin install --opencode` (or accept the `kcap setup` prompt) to write `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads and streams each session live (`vendor=opencode`). Subagents (the `task` tool / `@agent`) are captured too — the plugin fetches each child session via the SDK and streams it, so it nests under the parent in the trace. Historical `kcap import --opencode` reads OpenCode's SQLite database (`~/.local/share/opencode/opencode.db`) and imports every transitive descendant session (children, grandchildren, and so on — see [Loading historical sessions](#loading-historical-sessions)), so it backfills sessions from before the plugin was installed.

> **Codex** collab subagents (Codex CLI 0.146+, the `spawn_agent` collaboration tools) are captured too. Each subagent thread writes its own rollout under `~/.codex/sessions/`; the live watcher discovers children by the parent linkage in their rollout header and streams each one nested under the parent session, and `kcap import --codex` does the same for history — a subagent rollout never imports as a separate top-level session (see [Loading historical sessions](#loading-historical-sessions)).

### Local server (`kcap local`)

The server `kcap setup --local` installed runs as a per-user service (a launchd LaunchAgent on macOS) and starts at login. These commands manage it:

```bash
kcap local status          # explain its state and what to do about it
kcap local start           # start it and wait until it is ready
kcap local stop            # stop it, and keep it stopped across logins
kcap local restart
kcap local logs -n 200     # the end of the server's log, which holds Postgres's and KurrentDB's too
kcap local open            # open the web UI in a browser
kcap local upgrade         # move it forward to this kcap's version
kcap local uninstall       # remove it, keeping its data; --purge deletes it (see Removing the local edition)
```

`kcap local status` reports the installation's version, port and root, whether search is configured to work by meaning through Ollama (and with which model) or by keyword only, whether the service runs the server, and whether KurrentDB and Postgres are up. When the server is not ready it says why: stopped, still starting (a first start creates the database), exiting and being restarted, holding no unit, a port another process has taken, stores an earlier server left running, a unit that serves an installation under another `KCAP_LOCAL_ROOT`, or a setup or upgrade that was interrupted. It also says when the stack is older than this `kcap`, and names `kcap local upgrade`, or newer than it. It changes nothing, and exits 0 only when the server is running and ready.

`start`, `stop`, `restart` and `upgrade` wait for any other `kcap` that is changing the installation, and first finish or undo an interrupted setup or upgrade. A stop persists: the server does not come back at the next login until `kcap local start`. The port is part of the installation's identity, so a server whose port another process holds cannot move to another one; stop that process instead. The logs are under `~/.config/kcap/local/logs`.

#### Upgrading the local server

The stack is always at least as new as every `kcap` that uses it, and it only moves forward. `kcap local upgrade` is the only command that changes its version: it moves the stack to this `kcap`'s version when that is newer, and otherwise changes nothing, so an older `kcap` on the same machine (say the desktop app's bundled one) keeps working against a newer stack and never moves it back.

- **npm:** `kcap update` replaces the CLI, then runs the new binary's `kcap local upgrade`. A CLI release can reach npm before its server bundle is published; the update then still succeeds, says so, and the stack stays on its version until `kcap local upgrade` is run again.
- **Script installs:** `kcap update` does the same once the new release is in place.
- **Homebrew and other installs:** update the CLI the way you installed it, then run `kcap local upgrade`. `kcap local status` and the notice after other commands say when the stack is behind.

An upgrade downloads and verifies the new bundle beside the running one, stops the server and checks that nothing of it is left, points `bin/current` at the new version, starts it and waits until it is ready; the first start migrates the data. A server you had stopped is started for that, then stopped again. Upgrades are forward-only: nothing is snapshotted, so nothing is rolled back. If anything fails before the switch, the old version is started again (if it was running). If the new version does not start, it stays selected and stopped, the old bundle is kept in `bin/` for diagnosis, and `kcap local start` tries again once the cause is fixed. An upgrade interrupted at any point is finished or undone by the next `kcap local start`, `stop`, `restart`, `upgrade` or `uninstall`, or by `kcap setup --local`. The update cap described under [`kcap update`](#other-commands) does not apply to the local server.

`--if-installed` prints nothing when there is no installation. `--json` is for the desktop app: one JSON object per line on stdout, in the style of `kcap setup --local --json`:

```json
{"v":1,"event":"upgrade_planned","version":"0.14.0","from_version":"0.13.0"}
{"v":1,"event":"downloading","url":"https://…","bytes":84213760}
{"v":1,"event":"verified","sha256":"…"}
{"v":1,"event":"stopping"}
{"v":1,"event":"stopped"}
{"v":1,"event":"switched","version":"0.14.0"}
{"v":1,"event":"starting","unit":"…"}
{"v":1,"event":"ready","server_url":"http://localhost:9473"}
{"v":1,"event":"upgraded","server_url":"http://localhost:9473","version":"0.14.0","from_version":"0.13.0"}
```

A run that changes nothing ends with `{"v":1,"event":"nothing_to_do","version":"…","installed_version":"…","reason":"…"}`, where `reason` is `current`, `stack_newer` or `not_installed`. A failed run ends with `{"v":1,"event":"failed","code":"…","message":"…","switched":false}`; `switched` says whether the new version is selected. `code` is one of the setup codes, such as `lifecycle_busy` when another `kcap` holds the installation, `download_failed`, `verification_failed`, `start_failed` or `commit_failed`. The other events are `recovering`, `recovered`, `download_progress`, `unpacked` and `restarting`.

| Exit | Meaning |
| -- | -- |
| 0 | Upgraded: the stack runs this `kcap`'s version |
| 1 | Failed before the switch: the old version is selected, and running again if it was |
| 2 | Failed after the switch: the new version is selected and stopped, or its switch is finished by the next command |
| 3 | Nothing to do: already at this version, newer than it, or not installed |
| 4 | No bundle is published for this version yet (`bundle_not_published`); nothing changed |

### Approval policy

Cut down on prompt fatigue by telling kcap what to auto-approve, auto-deny, or still ask about. Drop rules in a **policy file** — `.kcap/approvals.yaml` at the repo root (commit it — it's PR-reviewable, like a CODEOWNERS file) and/or `~/.config/kcap/approvals.yaml` for your own machine (honors `KCAP_CONFIG_DIR`) — and every matching tool call gets one of three outcomes: **allow** (run it, no prompt — on an ACP-routed agent this only fires when it offers a single, unambiguous allow option; otherwise the normal prompt still shows), **deny** (refuse the call — only Claude's `PreToolUse` hook shows your `reason:` to the agent; every other seam answers with a bare refusal, and the reason still lands in kcap's audit trail), or **ask** (force a prompt even where the agent's own auto-mode would normally have skipped one). The same rules, with identical semantics, govern a local Claude Code session (its `PreToolUse` and `PermissionRequest` hooks), a hosted (daemon-launched) Claude session (evaluated before the request reaches the human-approval lane), and ACP-routed agents — Cursor, GitHub Copilot, Kiro, OpenCode, Gemini — (evaluated ahead of kcap's own launch presets). A **rendered** session's local checks only ever tighten: deny and ask still apply, but nothing gets auto-allowed there. Anything the rules don't decide **passes through** unchanged to the agent's native behavior, so turning the policy on never makes a session noisier, and a repo with no file behaves exactly as it does today. Unattended review-flow launches are out of scope: a reviewer runs with no human to prompt, so it stays under its own containment rather than this policy layer.

```yaml
version: 1
rules:
  - match: { kind: shell, command: "git push --force*" }
    outcome: deny
    reason: force-push goes through the PR lane
  - match: { kind: shell, command: ["git status *", "git diff *", "dotnet build *"] }
    outcome: allow           # the trailing * is the visible opt-in to arbitrary extra argv
  - match: { kind: mcp_tool, server: "kcap-*" }
    outcome: allow
  - match: { kind: shell, command: "gh pr merge" }
    outcome: ask
```

`match.kind` is one of `shell`, `file_edit`, `file_read`, `network`, `mcp_tool`, `other`. An **allow** pattern must cover the whole command: `git status` allows only `git status`, and `git status *` allows anything after it purely because of that trailing bare `*` — broad auto-approval has to be visible in the file, never a silent default. **Deny**/**ask** patterns are looser and match a run of tokens anywhere in the command, so `git push --force*` also catches `git push --force origin main`. A shell command kcap can't fully analyze — redirection, expansion, globs, compound syntax like `if`/`for`, a known shell name anywhere in the line (`bash`, `sh`, `pwsh`, `busybox` and friends, wrappers included), or an `env -S`/`--split-string` form that packs a whole command line into one argument — is **never allow-eligible** no matter what it contains; it can still be denied or asked about, just never auto-approved. That list of shell names is maintained, not exhaustive, so a lesser-known interpreter is analyzed like any other program: it's auto-approved exactly when an allow rule already in the file fully covers the command — whether that rule names the interpreter outright, a glob covers its position, or a wrapper rule like `env *`/`sudo *` grants it the argv. A shell name on that maintained list, though, is never auto-approved no matter what rule covers it.

**The judge.** Set `judge: { mode: unmatched, prompt: "…" }` and a call no rule decides in a Claude Code session, local or hosted, is sent to your kcap server's policy judge — an LLM classifier whose model and keys stay on the server — which answers allow, deny or ask. It stands in only where Claude would ask you: it is not consulted before a call in `auto`, `dontAsk`, `bypassPermissions` or `plan` mode (a prompt one of them does raise still reaches it), and never answers a question Claude asks you or a plan it puts to you for approval. It sees your `prompt`, the action, your recent messages in this session and every call you already refused at a prompt, so "push when the tests pass" two turns ago can approve the push, and a push you said no to stays a prompt. An ACP-routed hosted agent (Cursor, Copilot, Kiro, Gemini, OpenCode) consults it too, but has no transcript the server can read, so the judge sees only the calls you refused through the daemon, not your messages. The judge has 2 seconds in a local session and 5 in a hosted one, where the agent is already waiting on a prompt; anything it does not decide in time, an `uncertain` answer, an unreachable server or one without the judge enabled all **pass through** to the agent's native behavior (in a hosted session, the permission card), and every consultation is recorded beside the rule decisions.

**Codex.** The same rules govern Codex wherever it runs: a session you start with `codex`, a daemon-hosted terminal session, and a hosted app-server session. Codex asks for approval only for what its own approval policy sends to you, such as a command that needs the network or writes outside the workspace, and runs everything else unasked. So a **deny** rule is also checked before every tool call through Codex's `PreToolUse` hook, which refuses the call before it runs, while **allow** rules and the judge act only on calls Codex asks about. In a session you start yourself Codex records none of your declines, so the judge there can deny or ask but never allow; rules can still allow. A hosted app-server session sees every decision it forwards, so its judge can allow, using your messages and the calls you refused through the card.

A file that steps outside the supported YAML subset (block/flow mappings and sequences, plain/quoted scalars, literal `|`/`|-` blocks, comments — nothing else) or that sets `caps`/`enforcement` (reserved for centrally-managed policy scopes, invalid in a local file) is never half-applied. It's dropped entirely, and kcap says so loudly at the next session start instead of silently losing your rules:

```
[kcap] approval policy degraded: repo policy at /path/.kcap/approvals.yaml ignored: <reason>
```

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished — followed by a per-turn outline (one prose line per turn) and a `--get-turn <N>` pointer for drilling into any turn. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kcap recap <sessionId>              # summary + per-turn outline (default)
kcap recap --full <sessionId>       # full transcript
kcap recap --chain <sessionId>      # summaries across continuation chain
kcap recap --chain --full <sessionId>  # full transcript across chain
kcap recap --per-turn <sessionId>   # compact per-turn index (prompt, tools, files, tokens, time)
kcap recap --get-turn <N> <sessionId>  # full event transcript for a single turn
kcap recap 3f2a… --continue         # take over the session's work items and plans, then print the recap
kcap recap 3f2a… --continue --force # same, even when the session looks live
```

`--continue` (run inside the session that takes over, with an explicit session id) attaches the current session to the continued session's work items and unfinished plans, then prints the recap. It refuses with exit code 2, and prints no recap, while the continued session may still be running or when no current session can be identified; `--force` overrides that, and should be used only after confirming it is not. It also requests adoption of loose-end claims independently of work-item availability. The report includes each predecessor attempt, outcome and new claim identity. It exits 1, after the recap, when an attachment or claim adoption fails, including partial adoption. An older server without the adoption route is reported as unsupported and exits 1: takeover is incomplete, even when work-item and plan attachment succeeded. `--force` never bypasses server ownership checks.

`--per-turn` prints a one-block-per-turn index — useful for orienting in a long session before drilling into a specific turn with `--get-turn <N>` (the turn number shown in the `--per-turn` index). `--get-turn` takes the turn number as its value; the session id is the usual positional (or comes from the current session), so `kcap recap <sessionId> --get-turn <N>` works too.

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kcap plugin, the session ID is set automatically.

If the kcap plugin is installed, you can also use the `/kcap:recap` skill inside Claude Code, or just ask:

```
Recap session c4de7fbe-cff5-4e2c-bf80-9858d02f58be and propose what should be done next.
```

### Listing sessions on a repository

```bash
kcap sessions                       # running sessions on the current repo, everyone you can see
kcap sessions --all --mine          # your own, running and ended
kcap sessions --repo acme/widgets   # another repository, by owner/name or 16-hex hash
kcap sessions --ended --limit 50   # the last 50 ended sessions
kcap sessions --touching src/Foo    # sessions with an Edit/Write attempt on a matching path
kcap sessions --since 14d --mine    # what you worked on here in the last 14 days
kcap sessions --repo all --since 2026-09-27 --until 2026-09-28   # everyone, every repo, one day
kcap sessions --json                # the raw server response
```

Answers "which session is doing this, and is it still running?" for a checkout, and "what did I, or the team, work on in this period?" with `--since` and `--until`. Rows are visibility-filtered, so a teammate's private session simply does not appear. Each row shows status (`active`, `stale` after an hour of silence, or `ended`), your access level on it, owner, vendor, branch, last activity and title; below full access the branch is blank and `--touching` cannot match the row. Path evidence comes from Edit/Write tool inputs at invocation time, so edits applied through a shell script leave nothing to match. Drill in with `kcap recap --full <session-id>` (full access only).

`--since` and `--until` each take an instant ending in `Z` or an offset (`2026-09-27T09:00:00Z`), a date (`2026-09-27`, the start of that day in this machine's time zone) or a duration back from now (`36h`, `14d`, `2w`). With either one the listing covers running and ended sessions alike, newest start first, and shows a `STARTED` column. A session is in the period when its span from start to end touches it; it is not checked event by event, so a session that ran across the period lists even if it was idle inside it. `--repo all` lists every repository, adds a `REPO` column, and needs no checkout.

When a period holds more rows than one page, the output ends with a `More:` line: the command for the next page. `--cursor` carries the filters of the page it came from, so it combines only with `--repo`, `--limit` and `--json`. Against a server that predates the time filter the command says so and lists nothing, rather than print an unfiltered list.

### Plan validation

Verify that all items in a session's plan were completed.

```bash
kcap validate-plan <sessionId>
```

Fetches the session's discovered plan artifacts (`GET /api/sessions/{id}/plan-artifacts?chain=true`) — the primary artifact plus any other candidates the server found, in an ordered set — and pairs them with the existing `recap` call for the current session's file writes/edits and AI-generated "what's done" summaries. On an older server without the artifacts route (or a non-visible session), `validate-plan` falls back automatically to the previous recap-only behavior. A degraded, truncated, or unavailable plan renders an explicit marker line instead of failing silently; if the primary artifact's content can't be retrieved, the command reports that validation isn't possible and exits with status 2 (0 for a normal render or when no plan is found at all — absence is a valid answer).

When the session's plan was declared through the [plans MCP tools](#plans-mcp-server-for-agents), the declared plan document leads the `## Plan` section, and a `## Tasks` section between the plan and `## What's Done` lists the declared tasks — a progress line (`2 of 5 completed`, or `n completed, total unknown`) followed by one `ordinal. [status] title (source)` line per task, with its note beneath when one was recorded. The section is omitted when nothing was declared, so an older server renders exactly as before.

With the plugin installed, use the `/kcap:validate-plan` skill or ask naturally:

```
Did I finish everything in the plan? Check what's left to do.
```

### Hide session

Mark a session as owner-only so other users no longer see it in the dashboard.

```bash
kcap hide                 # current session
kcap hide <sessionId>     # specific session
```

### Session title

Rename the current session from inside it — the command an agent runs when you ask it to rename the session. It resolves the session only from `KCAP_SESSION_ID` (Claude Code) or `CODEX_THREAD_ID` (Codex) and takes no session ID; titles longer than 120 characters are shortened.

```bash
kcap set-title "Fix the checkout regression"         # rename the session
kcap set-title --auto "Fix the checkout regression"  # name it, unless someone already chose a title
```

A plain title is a rename: it ranks with a rename made in the dashboard, and the later of the two wins. `--auto` is the title the first-prompt hook asks for: it replaces a placeholder or a generated title, but never a title chosen when the agent was started or a rename. `--auto` must come first. A rename exits `0` when the session shows it, and `3` when the server kept another title — it prints the title the session keeps — or is too old to say, in which case the title only lands on a session that has none yet. An `--auto` title that loses prints the same note but exits `0`, since a title someone chose outranking it is the intended outcome.

### Disable recording

Stop the watcher, silence future hooks, and delete server-side data for a session.

```bash
kcap disable                 # current session
kcap disable <sessionId>     # specific session
```

This is irreversible on the server side; the local transcript file is untouched.

### Error extraction

Scan a recorded session for tool call errors — failed bash commands, file read/write errors, agent failures, etc.

```bash
kcap errors <sessionId>              # single session
kcap errors <meta-session-slug>      # meta session
kcap errors --chain <sessionId>      # full continuation chain
```

Useful for post-session review: identify recurring mistakes, discover patterns to avoid, and update project instructions accordingly.

### Session evaluation (LLM-as-judge)

Score a recorded session against safety, plan adherence, quality, and efficiency criteria. Each of 13 questions (e.g. *"Did the agent run destructive commands?"*, *"Did it write tests when appropriate?"*, *"Were there repeated failed attempts at the same operation?"*) is answered by a separate headless Claude judge with **no filesystem or network tools**. Most judges reason from the compacted session trace embedded in the prompt; some questions — and any session whose trace is too large to embed — instead investigate the session on demand through a read-only, session-scoped MCP tool surface (summary, search, transcript, errors, recap, tool results). The embed-vs-tools threshold is tunable via `KCAP_EVAL_TRACE_TOKEN_BUDGET` (default ~200K estimated tokens).

When the server advertises the evidence route, a single-session evaluation (from `kcap eval` or a dashboard-dispatched daemon run) reads this session and its subagent lanes as you are allowed to see them through bounded evidence reads instead of the whole compacted trace: a small session is judged from its complete trace in one prompt, a larger one through eight read-only evidence tools under a tool-call, byte and time budget. Citations are certified by the server, each question records what it read and left unread (`coverage-v2`), and a budget stop is reported rather than hidden. `--chain` keeps the embedded-trace path, and `--threshold` has no effect on the evidence route. With `--baseline-out`, each evidence question's retrieval ledger is also copied to `<path>.ledgers/`.

```bash
kcap eval <sessionId>                      # default: sonnet judge
kcap eval --model opus <sessionId>         # stronger judge
kcap eval --chain <sessionId>              # include the full continuation chain
kcap eval --threshold 5000 <sessionId>     # embedded-trace path: keep more of each tool output
kcap eval --questions safety <sessionId>   # run only the 4 safety judges
kcap eval --skip efficiency <sessionId>    # run everything except efficiency
kcap eval --baseline-out out.json <sessionId>  # also write per-question usage/route/timing
kcap eval --list-questions                 # print the question taxonomy
```

Each question resolves to one of three outcomes: `assessed` (scored 1-5, with a `pass`/`warn`/`fail` verdict, marked `✓`/`!`/`✗`), `insufficient_evidence` (the judge lacked what it needed to answer — marked `?`, no score), or `not_applicable` (nothing the question asks about could have occurred in this session — marked `–`, no score). Category and overall scores are computed from assessed questions only; a category or run with no assessed question prints `Overall: not scored` rather than a fabricated number. A run in which every question failed — its iteration cap, spend budget or time limit ran out, or the judge call errored — is recorded as a failed evaluation: it prints `Evaluation failed: N of N questions not judged` with each failure code and its count, lists the failed questions, and exits 1. Each row shows its finding and evidence, plus — when the judge's retrieval was measurably incomplete — an indented `coverage:` line naming what was truncated, unavailable, or cut short, and a `cites:` line for any evidence reference the judge grounded its finding in.

The aggregate is persisted back to the session's stream as a `SessionEvalCompleted` event, so past evaluations can be queried from the dashboard or used to track quality trends across sessions.

Expect ~1-3 minutes total depending on the model and session size — judges run sequentially.

> **Server requirement:** `kcap eval` fetches its question catalog from the server (`GET /api/eval/catalog`) and posts results to `POST /api/sessions/{id}/evals/v4`. It fails fast with a clear error against a server that doesn't expose the catalog or v4 endpoint, so the server must be running a build that includes them. Upgrade the server if `kcap eval` reports the endpoint is unavailable. An older `kcap` build (predating outcomes and coverage) posts to `/evals/v3` instead — every question scored, no outcome or coverage — and the server accepts both.

### PR review with full context

```bash
kcap review <pr-url-or-owner/repo#number>
```

Accepts a GitHub PR URL (`https://github.com/owner/repo/pull/123`, any host including GitHub Enterprise), a GitLab MR URL (`https://gitlab.com/owner/repo/-/merge_requests/123`, including nested groups such as `https://gitlab.com/group/subgroup/repo/-/merge_requests/123`), or the shorthand `owner/repo#123` (single-level only).

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

The same MCP server (`kcap-review`) is also auto-registered by the kcap plugin and available in any Claude Code session, not just ones launched via `kcap review`. Each PR-scoped tool (`get_pr_summary`, `list_pr_files`, `get_file_context`, `search_context`, `list_sessions`) accepts an optional `pr` argument — pass `"owner/repo#123"` or a GitHub/GitLab URL to review any PR from any branch. When omitted, the server falls back to the PR passed at startup (set by `kcap review <pr>`) or to git auto-detection against the current branch. `get_transcript` keys off `session_id` and doesn't need a `pr` argument.

### MCP servers for agents

The `kcap mcp sessions` stdio server lets coding agents search and recall past kcap sessions without leaving the chat. `kcap setup` **registers it (with `kcap-review`) for Claude Code, Codex CLI, Cursor, GitHub Copilot CLI, Gemini CLI, SST OpenCode, Google Antigravity, and AWS Kiro CLI** — no manual `claude mcp add` or TOML/JSON edit. For Claude Code it's carried by the plugin's `.mcp.json`; for Codex CLI, `kcap setup` / `kcap plugin install --codex` write it into `~/.codex/config.toml`; for Cursor, `kcap setup` / `kcap plugin install --cursor` write it into `~/.cursor/mcp.json` (opt out with `--skip-cursor-mcp`); for Copilot, `kcap setup` / `kcap plugin install --copilot` write it into `~/.copilot/mcp-config.json` (opt out with `--skip-copilot-mcp`); for Gemini, `kcap setup` / `kcap plugin install --gemini` write it into the shared `~/.gemini/settings.json` (opt out with `--skip-gemini-mcp`); for OpenCode, into `~/.config/opencode/opencode.json` (opt out with `--skip-opencode-mcp`); for Antigravity, `kcap setup` / `kcap plugin install --antigravity` write it into `~/.gemini/config/mcp_config.json` (opt out with `--skip-antigravity-mcp`); for Kiro, `kcap setup` / `kcap plugin install --kiro` write it into `~/.kiro/settings/mcp.json` (opt out with `--skip-kiro-mcp`). The server is repo-aware: `cd` into a project before spawning your agent and `search_sessions` defaults to that repo's sessions.

Re-running `kcap setup` or `kcap plugin install`, and the refresh an upgrade runs, points every kcap server in those JSON configs at the kcap binary doing the install, including an entry an older install left on a binary that has since moved or a bare `kcap` that resolves to the npm wrapper. A kcap-named entry you changed in any other way (an added `env`, a disabled server, different args, a command that is not kcap, or a kcap build that is still on disk) is left as it is.

The `kcap mcp flows` stdio server lets agents start and interact with AI-powered agent flows — any flow-definition catalog entry, not just reviews. The plugin **auto-registers it for Claude Code**, and `kcap setup` / the harness-specific plugin installers also register it for Codex, Cursor, Copilot, and Gemini. Codex registration is conservative: existing manual entries are preserved, and uninstall removes only unchanged kcap-owned entries. The Codex `kcap-flows` entry also carries `tool_timeout_sec = 600`, so Codex's own per-tool timeout never cuts a flow call short; an entry an earlier kcap wrote picks it up on the next `kcap setup` or plugin refresh, and an entry you edited is left alone. See the [Flows MCP server](#flows-mcp-server-for-agents) section for details.

The `kcap mcp flow-result` stdio server is the reviewer-side counterpart: the daemon injects it into hosted review-flow reviewer sessions so they can submit their result. It is not meant to be registered or run manually — see [Flow-result MCP server](#flow-result-mcp-server-hosted-reviewers).

The `kcap mcp memory` stdio server lets agents search, save, and update durable team memories — preferences, feedback, project facts, and references scoped to you, your team, or the org. `kcap setup` **auto-registers it for Claude Code** (via the plugin's `.mcp.json`), **Codex CLI** (in `~/.codex/config.toml`, alongside `kcap-sessions` / `kcap-review`), **Cursor** (in `~/.cursor/mcp.json`, alongside the other kcap servers), **GitHub Copilot CLI** (in `~/.copilot/mcp-config.json`, alongside the other kcap servers), **Gemini CLI** (in the shared `~/.gemini/settings.json`, alongside the other kcap servers), **SST OpenCode** (in `~/.config/opencode/opencode.json`, alongside the other kcap servers), **Google Antigravity** (in `~/.gemini/config/mcp_config.json`, alongside the other kcap servers), and **AWS Kiro CLI** (in `~/.kiro/settings/mcp.json`, alongside the other kcap servers); Codex's native plugin loader also picks it up via `.codex-mcp.json`. See the [Memory MCP server](#memory-mcp-server-for-agents) section for details.

Beyond registering the servers, `kcap setup` / `kcap plugin install` also installs a small kcap-owned **agent-instructions block** for harnesses that read a user-level instructions file (GitHub Copilot CLI's `~/.copilot/copilot-instructions.md`, and Gemini CLI's + Google Antigravity's shared `~/.gemini/GEMINI.md` today; more rolling out per harness). It's a marker-delimited, non-destructive note (preserves any instructions you've written) that steers the agent to prefer the kcap tools for "why / history / prior-work" questions over native `git`/GitHub/grep — registration alone doesn't make agents route to the tools. Opt out with `--skip-<harness>-instructions`.

Where a harness exposes a per-server trust knob, registration also marks the kcap servers that only read, or that write only to the session's own kcap record, auto-approved so the agent doesn't stop to ask before every call: **Gemini** marks `kcap-review`, `kcap-sessions`, `kcap-analytics`, `kcap-workitems`, `kcap-plans` and `kcap-handoff` via `"trust": true` in `~/.gemini/settings.json`, and **Codex** marks the same servers via `default_tools_approval_mode = "approve"` in `~/.codex/config.toml`. The work-launching `kcap-flows` (starts a *paid* hosted reviewer), `kcap-memory` (a save or rescope can widen who sees a memory), `kcap-pages` (can widen who may open a page) and `kcap-knowledge` (can change facts and skills) are deliberately not pre-approved. Every kcap tool also advertises MCP annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`), so a harness that decides approval from them — Codex's `auto` mode, for one — runs the reads and the additive writes without a prompt whatever the registration says, and asks only before a tool that removes or overwrites something. **Cursor** and **Copilot** have no per-server auto-approve field in the config we write — auto-approve kcap's read tools there through the harness's own controls instead (Cursor's Auto-run mode or `cursor-agent --approve-mcps`; Copilot's `--allow-tool` / `--allow-all-tools`).

The `kcap mcp workitems` stdio server lets agents attach the current session (and its continuation chain) to a work item — by issue key, PR number, work item id, or a brand-new title — list what a session is already attached to, or record the loose ends a session leaves unfinished. `kcap setup` / `kcap plugin install` **register it for every supported harness** (Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, OpenCode, Antigravity, and Pi). See the [Work items MCP server](#work-items-mcp-server-for-agents) section for details.

The `kcap mcp plans` stdio server lets agents declare the plan, spec or design document a session works from and the plan's task list — `declare_plan_document`, `set_plan_tasks`, `update_plan_task`, `get_plan` — so progress shows in the session view and the list survives context compaction. `kcap setup` / `kcap plugin install` **register it for every supported harness** alongside `kcap-workitems`. See the [Plans MCP server](#plans-mcp-server-for-agents) section for details.

The `kcap mcp handoff` stdio server exposes `continue_session`, which attaches the current session to another session's work items and unfinished plans when that session's agent is gone. It refuses while the other session may still be running unless `force` is passed. The session taking over is the harness session the server runs in; pass `current_session_id` when the harness does not expose it. The server is registered for every supported harness like `kcap-plans`.

The `kcap mcp analytics` stdio server lets agents answer analytics questions about the org's recorded coding sessions — spend, token/tool/model usage, outcomes, commits, PRs, evals — with governed read-only SQL over the server's curated analytics views. `kcap setup` **auto-registers it for Claude Code, Codex CLI, Cursor, GitHub Copilot CLI, Gemini CLI, SST OpenCode, Google Antigravity, and AWS Kiro CLI** — alongside the other repo-aware servers. It's repo-aware: it resolves its scope from the working directory, so `cd` into a project before spawning your agent. See the [Analytics MCP server](#analytics-mcp-server-for-agents) section for details.

### Sessions MCP server (for agents)

```bash
kcap mcp sessions
```

Stdio MCP server that exposes past kcap sessions to coding agents (Claude Code, Codex, Cursor, Copilot, Gemini, Antigravity) so they can search and recall prior work without leaving the chat. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it (alongside `kcap-review`) directly in `~/.codex/config.toml` under `[mcp_servers]`, so there's nothing extra to do — launch Codex from your repo directory so the server resolves the right repo. Enabling the kcap plugin through Codex's native plugin manager (`codex plugin add`) also provides them via the plugin's `.codex-mcp.json` descriptor. **Cursor:** `kcap setup` / `kcap plugin install --cursor` register it (alongside the other three kcap servers) in `~/.cursor/mcp.json`; opt out with `--skip-cursor-mcp`. **GitHub Copilot CLI:** `kcap setup` / `kcap plugin install --copilot` register it (alongside the other three kcap servers) in `~/.copilot/mcp-config.json`; opt out with `--skip-copilot-mcp`. **Gemini CLI:** `kcap setup` / `kcap plugin install --gemini` register it (alongside the other three kcap servers) in the shared `~/.gemini/settings.json`; opt out with `--skip-gemini-mcp`. **Google Antigravity:** `kcap setup` / `kcap plugin install --antigravity` register it (alongside the other three kcap servers) in `~/.gemini/config/mcp_config.json` — Antigravity's own MCP file, not the Gemini CLI's `settings.json`; opt out with `--skip-antigravity-mcp`.

It provides ten tools:

- **`search_sessions`** — keyword search over past sessions (and subagent transcripts). `query` takes one to three keywords or identifiers (a ticket id, a file name, a symbol), not a sentence: the literal lanes need every term inside one event or turn summary, so a sentence gets only embedding neighbours. Each hit carries `hit_kind` — `transcript` and `title` are literal matches, `turn_prose` and `semantic` may be nearest-neighbour hits that never contain the query. Searches the current repo first and automatically widens to every visible repo when results come back thin (the response then carries `widened_to_all_repos: true`, and each hit includes its own repo). Pass `repo: "all"` to search across every repo you can see up front, or `repo: "owner/name"` for a different one — an explicit `repo` (including `"all"`) never auto-widens. Filter by `author` / `author_github_id`. Returns ranked hits with `session_id`, snippet, and (for transcript hits) `hit_event_index` + `agent_id` for drilling in.
- **`list_repo_sessions`** — the sessions you are allowed to see on a repository, or on every repository with `repo: "all"`, with `access_level`, `stale`, `repo`, branch, cwd, last prompt and Edit/Write attempt paths (blank below full access). Without a period: running first, ordered by last activity, and `state` defaults to `active`, so finished sessions are hidden unless you pass `ended` or `all`. With `since` and/or `until`: the sessions worked on in that period, newest start first, `state` defaulting to `all`; each bound takes an instant ending in `Z` or an offset, a date, or a duration back from now (`36h`, `14d`, `2w`). While the response carries `next_cursor`, call again with `cursor` and the same `repo`. `owner` is `me` or a canonical id; `touching_path` matches attempt paths on full-access rows only. An undeclared argument is ignored, not rejected.
- **`list_repo_plans`** — the declared plans on a repository that you can see, unfinished ones by default, with progress, the next open task and the sessions attached to each. Use this to find work a session left behind.
- **`get_declared_plans`** — a plan's documents and full task list, by `plan_id` or for every plan a `session_id` touched.
- **`get_session_evals`** — the eval state of up to 25 sessions by id: not yet ingested, not evaluated, running, completed (overall score, category scores, weakest questions, judge summary) or failed. Unlike `kcap-analytics` it works on every plan; the eval-watch handoff at the end of `kcap setup` follows an import's evals through it.
- **`get_eval_report`** — a session's latest completed eval as Markdown an agent can act on: the overall score, each category, every question's finding, evidence and recommendation, and the retrospective's strengths, issues and suggestions. With no `session_id` it reports on the current repository's most recently evaluated session. Works on every plan.
- **`list_eval_runs`** — a repository's recently evaluated sessions, newest first, with their category scores and mean, to pick one for `get_eval_report`.
- **`get_connection`** — the server URL and kcap profile this MCP server resolved when it started, with no network call. eval-watch checks it against the import it follows before reading any eval.
- **`get_session_summary`** — concise `summary_text` + `plan` for a session, plus `declared_plans`: a progress pointer for each plan the session declared. Use this to orient before reading the transcript.
- **`get_session_transcript`** — speaker-tagged events from a session. Pair `around_event` (and `agent_id` if the hit was in a subagent) with the values returned by `search_sessions` to fetch the exact decision context.
- **`get_turn`** — the full event transcript for one turn (user prompt, tool calls + results, assistant text) by `session_id` + `turn_index`. A turn is one user message and the assistant's full response up to the next user message.
- **`list_turns`** — every turn of a session with its prose summary, prompt, tools, files and token counts; works on running sessions at `activity` access and above.

The server is repo-aware — it resolves the current working directory to a repo hash at startup, and `search_sessions` defaults its `repo` filter to that hash, auto-widening to all repos only when that pinned search comes back thin. **If the current repo can't be resolved** (run outside a git checkout, or a missing/unparseable `origin` remote), `search_sessions` returns an error asking you to pass `repo: "owner/name"` or `repo: "all"` — it will not silently search across all repos. `list_repo_sessions` and `list_repo_plans` fail closed the same way: unresolved, they return an error asking for `repo: "owner/name"` rather than falling back to every repo. `list_repo_sessions` lists every repository only when asked with `repo: "all"`; `list_repo_plans` does not accept it.

### Flows MCP server (for agents)

```bash
kcap mcp flows
```

Stdio MCP server that lets coding agents start and interact with AI-powered agent flows — any entry in the server's flow-definition catalog, not just reviews — directly from within a session. The kcap plugin **auto-registers it for Claude Code** (via `.mcp.json`), so there's nothing to do after `kcap setup` — the flows server derives the target repo from its launch working directory, and Claude Code always runs inside the repo, so one registration works for every repo. It's registered even with no daemon connected; the tools simply stay inert (and `start_flow` returns an error) until a daemon with the repo is available. `kcap setup` / `kcap plugin install --codex` register it for **Codex** in `~/.codex/config.toml`; existing manual/custom entries are never overwritten or claimed, and uninstall removes only unchanged kcap-owned entries. That entry carries `tool_timeout_sec = 600` — above the CLI's own bound on a flow call (see [harness tool timeouts](#harness-tool-timeouts) below), so Codex's default per-tool timeout of 300 s never aborts one; an entry an earlier kcap wrote is healed to include it on the next `kcap setup` / `kcap plugin install --codex`, and an entry you edited (your own timeout included) is preserved as is. The native Codex plugin descriptor also includes it. The corresponding installers register it for **Cursor**, **GitHub Copilot CLI**, and **Gemini CLI** in their normal MCP configuration files. For the harnesses that expose no per-process identity to a long-lived MCP server (Cursor, Copilot, Gemini, Kiro, OpenCode, Antigravity), that registration includes an internal `--driver <vendor>` argument so the flows server can tell which harness is driving and recommend a *different* reviewer; Claude Code and Codex are identified from their own environment and are left unstamped. `--driver` is written by kcap into the registration — it is not a flag you set yourself. Because `kcap-flows` launches paid work, it is deliberately not marked read-only or auto-approved on any harness.

It provides these generic tools:

- **`start_flow`** — start a new flow from either `definition_id` (the server's flow-definition catalog, e.g. `spec-review`, `code-review`, `doc-review`, or a custom definition) **or** `definition_yaml` (an inline dynamic flow definition — the full YAML, same schema as catalog definitions); provide exactly one of the two. Catalog starts use the guarded v2 protocol. A `definition_yaml` flow has extra constraints: every participant must declare `workspace: none` and a concrete, priced model (no `default`), the server clamps `limits`/`mcp` to its own caps rather than trusting the definition, may reject the whole thing with a coded error, and requires a server with dynamic flows enabled. Also provide `target_kind`, `target_ref`, `target_title`, `session_title`, and `context`. `session_title` names the participant's session from its start — a short phrase naming this piece of work — in place of the definition's generic opening instruction. Requester context (session ID, cwd, repo root, owner, name) is resolved automatically from the environment. Returns a `flow_run_id`. A single-participant definition starts its first round eagerly; a multi-participant definition starts **round-less** — the response carries no round, and each declared role's agent launches lazily on its first `send_to_participant`. Optional `mode`: by default, when the daemon runs on the same machine and the selected vendor declares a borrowed-context containment strategy, the participant sees current tracked, dirty, and non-ignored untracked checkout content; the safety boundary is vendor-specific. Cursor runs in a daemon-owned snapshot refreshed before follow-up rounds, while direct borrowing is reserved for runtimes with a native read-only tool clamp. The capability is advertised for whatever build of the vendor CLI is installed — it is not gated on a per-version validation record, since a vendor auto-update would then silently drop the participant back onto a stale committed base. It IS gated per platform where the containment boundary has not been measured: **GitHub Copilot advertises borrowed review only on macOS/ARM64, and only on a daemon that can both enforce an OS sandbox (`sandbox-exec`) and broker a token** (`COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN`) — widening its tool surface enough to read a snapshot also widens what a read tool can be pointed at, so the boundary is an OS sandbox rather than the vendor's own permission prompts, and that sandbox grants neither your keychain nor your Copilot state (see [Borrowed-context Copilot reviews](#borrowed-context-copilot-reviews)). Where any of the three is missing, a Copilot borrowed request returns `vendor_containment_unreadable` naming the daemon and the remedies; `mode="context-only"` works and is the remedy. Pass `mode="context-only"` to opt out. Claude review flows currently use owned worktrees because it declares no borrowed-review containment strategy. Optional `vendor`: for reserved `spec-review`/`code-review`, explicitly selects the reviewer independently of the driver; when omitted, resolution falls through to the definition's authored vendor when it declares one, then to your saved `flows.reviewer_vendor` preference (applied via one automatic retry, with the response saying so), and finally a coded `reviewer_vendor_required` if neither is set — ask the user which reviewer vendor to use, pass it explicitly, and offer to save it with `kcap config set flows.reviewer_vendor <vendor>`. Custom single-participant catalog definitions retain their authored vendor unless explicitly overridden. The server records requested/applied vendor plus selection source, and rejects an unavailable or uncertified vendor without silent fallback. Dynamic (`definition_yaml`) flows reject a top-level vendor override because each participant declares its own vendor. Optional `model`: a per-run reviewer **model** override for a single-participant catalog review — REQUIRES `vendor` (the model is interpreted against that vendor; there is no vendor→model table anywhere in the CLI, so `model` without `vendor` is rejected locally before any request is sent) and is rejected on a `definition_yaml` (dynamic) or multi-participant start (each participant already pins its own model). Pass the vendor's own model id or alias verbatim, case-sensitive — the CLI never translates, canonicalizes, or guesses it. `doc-review` needs no repository: the document travels in `context` and the reviewer runs in an empty directory on one of your daemons that supports it, so it starts from anywhere, a machine with no checkout included. Optional `wait` (boolean, default `true`): `false` returns as soon as the round is accepted, with the `flow_run_id` and the round still running, for a caller whose tool calls are cut short; then poll `get_flow_status`. Such a call also gives up retrying a busy server after 40 seconds, having started nothing, so it can be called again.
- **`send_to_participant`** — send a follow-up message to a participant role declared by the flow definition (single-participant definitions use `"reviewer"`; the server rejects an unknown role, naming the valid ones). One round runs at a time per role — a second send to a busy role is rejected naming the busy round, while other roles remain addressable. Returns the new round's findings, or with `wait: false` returns at once with the round still running.
- **`get_flow_status`** — get the current status (running, waiting, completed, failed) and last result of a flow run. Optional `wait` (boolean, default `false`): when `true`, blocks — via repeated bounded internal checks, never a raw long-poll — until the round is terminal or roughly 3.5 minutes pass, instead of returning the current snapshot immediately. A long-running round is expected, not an error; on that cap this returns the same benign "still running" text an unset/false `wait` already returns on the round-submission path, so simply call it again with `wait: true`. Optional `timeout_s` (1–210) bounds that wait for a caller whose tool calls are cut off sooner. `flow_run_id` is optional: omitted, the newest open flow this session started is read (Cursor, Copilot, Gemini, Kiro, OpenCode and Antigravity give the flows server no session, so there it reads the flows started from the same repository — or working directory — on this machine, which the flows server records in `flow-runs-v1.json` under the config directory) — with several open, the reply lists them to choose from; with none open, the newest settled one, so a failure you were not watching for is still readable — and `session_id` selects another session's flows. The lookup needs a server with the by-session flow route; an older server answers with an error that says to pass the id.
- **`close_flow`** — mark a completed flow run as closed.
- **`list_flow_definitions`** — list the definitions this server can start right now: each one's id (what `start_flow` takes as `definition_id`), version, description, whether it is single- or multi-participant, and its participants' role, authored vendor and model. Read-only. Disabled or deleted definitions are absent, a server that predates the route is reported as unable to list rather than as an error, and a catalog still catching up after a read-model rebuild is a retryable `server_catching_up`. Each entry also carries when to use it and whether agents may offer it unprompted (`offer: proactive`) or run it only on request.
- **`get_flow_definition`** — read one definition's driver guide before starting it: its participants, when to use it, and the authored guidance on what context to submit, how to iterate and when to close. Read-only. An unknown, disabled or deleted id is an error; a server that predates flow guides is reported as publishing none.
- **`list_start_agent_options`** — read before `start_agent`: which of your daemons are connected on this machine (matched by the machine id a daemon has persisted here; nothing is written), how many agent slots each has free, and which harnesses each can start, plus the harness running the calling session. When no daemon runs on this machine it says so and names `kcap daemon start -d`, because `start_agent` would be refused. A daemon with `max_agents` 0 is shown with no limit. Called from a hosted agent, it says that every start runs on the daemon hosting that agent, which ignores `daemon`. Read-only.
- **`start_agent`** — start one separate hosted agent in a git repository on this machine, and return at once. **It does not block and it does not poll**: unlike the flow tools above, it sends one request and answers `requested` when dispatch is confirmed sent, or `pending` for a reserved loose end whose dispatch is unknown or not sent, before the agent registers, with the agent's id and the URL of its page. Required: `cwd` (an absolute directory inside the repository; a path inside a linked worktree resolves to the repository the worktree belongs to), `prompt` (the whole task, at most 16 384 bytes of UTF-8 — the agent shares none of the calling conversation), `title` (a short phrase naming the task, at most 200 characters; it is the session's title from the start, and titles generated later do not replace it) and `work_item` (a `wi:` or `le:` target key from the next-work feed, a work item id, `requester`, or `none`; there is no default). Optional: `vendor` (the harness to start; defaults to the harness running the session), `model`, `daemon` (only when several of your daemons run on this machine) and `session_id` (defaults to the calling session). The agent is an ordinary hosted agent that you supervise from the dashboard — unlike a harness's own subagent, it is not part of the calling session. The server tries once to attach its session to the named work item after that session starts, so nothing is attached when the call returns. A refusal comes back with the server's own reason: no daemon on this machine, the daemon at capacity, the limit of live agents, a start on the same key already in progress. When the server could not confirm that the launch command was sent, or does not answer within 60 seconds, the tool says the agent may or may not have started instead of reporting a failure. A server without the route, or with `Features:AgentStart` off, answers a bare 405 or an uncoded 404, and the tool says that the server cannot start agents.
- **`schedule_wakeup`** — for a running hosted agent: have a prompt delivered to it later as its next input, waking it if it is idle. Give `prompt` (at most 4 KB of UTF-8, delivered exactly as written) and exactly one of `delay_minutes` or `at` (a date and time with an offset, such as `2026-10-24T15:00:00Z`); a wake-up fires between 1 minute and 7 days from now. The agent should end its turn after scheduling rather than wait. A terminal session and a flow participant are refused, with the server's reason. A wake-up is never re-sent: one that ends unconfirmed or dropped shows in `list_wakeups`, and the agent decides whether to schedule again. When the request gets no answer, the tool says the wake-up may or may not have been scheduled.
- **`list_wakeups`** — the calling session's pending wake-ups, and those settled in the last 24 hours with their state and, for a dropped one, the reason. Read-only.
- **`cancel_wakeup`** — cancel a pending wake-up by `wakeup_id`; one already being delivered can no longer be cancelled. All three tools take an optional `session_id` (defaults to the calling session), and against a server without wake-ups, or with `Features:AgentWakeUps` off, they say the server does not support them.

**Offered flows.** When `kcap-flows` is registered for a harness, the SessionStart hook adds a short "Flows you may offer" block listing the catalogue flows marked `offer: proactive`, each with when to use it. The agent offers one at that moment and starts it only after you accept. When the catalogue cannot be fetched in time, the last one the server returned is listed instead. The built-in `spec-review` and `code-review` flows ship as proactive; an admin controls any flow's offer through the `guidance` block of its definition. Against a server that predates flow guidance nothing is listed, and the built-ins are offered only by a server that ships their guidance.

Every flow response reports which **workspace** the reviewer actually used, so you can tell whether it saw your uncommitted work:

```
workspace: borrowed (the reviewer saw your working tree, uncommitted changes included)

workspace: fallback (not_colocated)
  ⚠ Your working tree was NOT borrowed — the reviewer did not see uncommitted work.

workspace: unknown
```

`fallback` carries the server's reason verbatim (`not_colocated`, `daemon_outdated`, `not_allowed`, `no_requesting_cwd`, `context_only_requested`, or any newer one — the CLI never translates or filters them). **`unknown` is not the same as borrowed**: it means no decision was disclosed — an older server, a multi-participant run, or a flow kind other than the reserved `spec-review`/`code-review` aliases. Treat it as "assume the reviewer did not see uncommitted work" and inline anything that matters. The line appears on start, every round, status and close.

Status and polled round-result output also cross-check the reviewer's **vendor**: if the `reviewer` participant's vendor disagrees with the run-level `applied_reviewer_vendor` (or the server flags `reviewer_vendor_mismatch`), the CLI renders a `⚠ reviewer vendor mismatch` warning telling the driver to treat the run's results as suspect, close the flow, and report it. Agreement renders nothing.

A dynamic (`definition_yaml`) flow whose participants can be dollar-metered also reports its **budget enforcement** level, so you know whether every participant's spend counts against `budget_usd`:

```
budget enforcement: full

budget enforcement: partial (unmetered roles: probe, helper)
```

`partial` names the roles whose vendor hosts turns that report no token usage (they're bounded by `max_rounds`/`round_timeout`/`idle_ttl` instead of the dollar budget); `full` means every participant's spend is metered. The line is **absent** for catalog review flows, for runs without a dynamic budget, and against an older server — its absence carries no meaning. It appears on start, every round, status and close.

Responses from these tools may carry **`pending_messages`** — out-of-band notes participants push to the driver via `send_flow_message` (see the flow-result server below). The CLI acknowledges them to the server after rendering the response, so a message is normally shown once — but a failed acknowledgment redelivers it on a later call (at-least-once), so consumers should treat the `message_id` as the dedup key and react to each id only once.

While the server is rebuilding its flows read model (a projection replay after a server upgrade), flow tools can return a coded **`server_catching_up`** error (HTTP 409) with the replay's progress. It is temporary and retryable: wait a few minutes and retry, or ask the user how to proceed. The CLI renders the same guidance on every surface — start/submit, status/close, and the flow-result sidecar tools.

A start or round call can also hit two settlement-layer coded 409s — `flow_settlement_busy` and `reviewer_launch_incarnation_superseded` — raised while the daemon is still settling a prior launch. The CLI transparently auto-retries these on an exponential backoff with jitter (roughly 250–500 ms for the first retry, 5–10 s in steady state), so a short settlement wait is invisible to the caller. The retry budget is an **elapsed-time deadline of 3 minutes** measured from the first attempt — not an attempt count, and it includes each request's own duration, because a settlement-aware server absorbs the wait by holding the request open. Only these two codes retry on every path; every other coded error (`server_catching_up`, `budget_unverifiable`, `client_upgrade_required`, …) and every uncoded failure surfaces on the first attempt.

`submit_review_round` and `send_to_participant` also retry a third coded 409, `participant_unreachable` — a previously-assigned reviewer whose absence isn't durably proven yet (e.g. it idled out between rounds). The server declares it retryable and it converges once that absence is proven, so the CLI auto-retries it the same way, on the same 3-minute deadline. Starts never see it, so `start_review_flow`/`start_flow` don't retry on it.

If the deadline is exhausted the call does **not** hang or silently succeed: the tool returns an explicit error result (`isError: true`) in the usual `Error (code): message` shape, carrying how many attempts were made over how long, plus the last coded error it kept hitting — or, when the deadline cancelled the very first request before any coded response arrived, a note that the code shown is the client's default rather than something the server reported — for example `Error (flow_settlement_busy): gave up after 14 attempts over 3m — the daemon is still settling a prior launch and could not admit this one in time.` That outcome is retryable: try again in a minute, or check whether another flow is already running against the same daemon. The round **poll** loop has its own separate budget and its own graceful-cap message.

The four review tools — **`start_review_flow`**, **`submit_review_round`**, **`get_review_flow_status`**, **`close_review_flow`** — are aliases of the generic tools above: `start_review_flow`'s `kind` maps to `start_flow`'s `definition_id`, and `submit_review_round`'s `context` maps to `send_to_participant`'s `message` with the `reviewer` role targeted implicitly. Current clients always use flow protocol v2 for catalog starts; servers can reject legacy reserved-alias starts with `client_upgrade_required`. New integrations should prefer the generic tools; the review aliases remain convenient for a plain spec/code-review loop.

#### Harness tool timeouts

A start or round call holds the tool call open while the round runs. The CLI bounds it to end under 5 minutes — the shortest per-tool timeout among the harnesses that drive flows — with the round result or the benign `Flow still running for flow_run_id …` text, which is also the only place the driver is handed its `flow_run_id`. If a harness aborts the call anyway (a shorter timeout in its config, or a user-edited one), the flow is still running server-side: the driver should not start it again and should not go looking for it, but call `get_review_flow_status` / `get_flow_status` with `wait: true`, omitting `flow_run_id` when the aborted start never delivered it. The tool descriptions and both flow skills say so up front, because the harness words its own timeout error and kcap cannot.

**Reviewer model override — capability gating, protocol, and semantics:**

- **Capability gating.** A daemon only advertises `SupportsReviewerModelResolution` (per vendor) when that vendor is installed, unattended-certified, AND has a runtime-owned model resolver — today that's **Claude** and **Codex** only. ACP-hosted vendors (Cursor, Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity) advertise `false` for this field and keep their existing vendor-only (no model override) unattended support; the server refuses a `model` override for any vendor that doesn't advertise it, with no silent fallback. A resolver owns its own vendor's aliases/ids entirely — there is deliberately no shared, central vendor→model table anywhere in the CLI or daemon.
- **Protocol v3.** A `model`-bearing `start_review_flow`/`start_flow` call routes to `POST /api/flows/review/start/v3` (`client_flow_protocol_version: 3`) instead of the normal v2 route — exactly one request, never a v2 retry or fallback either way. `model` requires `vendor` and is rejected on a dynamic (`definition_yaml`) flow; both are checked locally, before any request is sent, so a bad pairing never reaches the server.
- **Exact error codes.** `reviewer_model_protocol_required` — the server predates the v3 protocol (404/405 on the v3 route, or an uncoded error body), or it accepted the start but didn't return the required ack fields (`applied_reviewer_model` + `reviewer_model_equivalence_key`); the CLI raises this one locally and, on the missing-ack case, best-effort closes the run it just started rather than leave an unvalidated reviewer running. Everything else is a genuine coded v3 verdict from the server, surfaced **verbatim** (never reformatted or intercepted): `reviewer_model_unavailable` (no advertised resolver on the selected vendor recognizes the model), `model_vendor_mismatch` (the model belongs to a different vendor than the one selected), `reviewer_model_safe_settlement_required` (the server can't prove a heal/relaunch would safely reapply the same model, so it refuses rather than risk a silent drift), and `reviewer_model_unpriceable` (the model has no resolvable pricing to budget-check against).
- **`requested`/`applied`/`resolved` semantics.** A v3 response's audit trail carries `requested_reviewer_model` (what was asked for), `applied_reviewer_model` (the canonical form the server matched), `resolved_reviewer_model` (the exact concrete value the daemon launched with), and `reviewer_model_source` (`"explicit"` for an override). These three legitimately differ as strings — a bare alias (`"opus"`) and the dated concrete id it resolves to are NOT compared textually. Validation is by opaque `reviewer_model_equivalence_key` **equality** end-to-end; the CLI never string-compares requested vs. applied vs. resolved.
- **Codex date-sensitivity.** The daemon always reports the verbatim model string it actually launched the agent with, never a date-suffixed model read back from session metadata. Claude's equivalence key is family-level and absorbs a dated id safely, but Codex's is slug-level and **date-sensitive** — a session-metadata model carrying a date suffix would compute a different equivalence key than the bare slug that was launched, and spuriously fail the server's post-launch echo validation.

Requires `kcap login` **and a running daemon with this repo checked out**, except `doc-review`, which needs only a running daemon that supports a scratch workspace. Discovery filters by the effective reviewer vendor before tie-breaking and requires the daemon's structured per-vendor capability before borrowing. Cursor and GitHub Copilot borrowed-context reviews both use a disposable owned snapshot — Copilot additionally requires macOS/ARM64, `sandbox-exec`, and a [brokered token](#borrowed-context-copilot-reviews); borrowed-checkout Claude review remains disabled, so those reviews use owned worktrees.

### Flow-result MCP server (hosted reviewers)

```bash
kcap mcp flow-result   # launched by the daemon — not meant to be run manually
```

The daemon gives every unattended reviewer this stdio MCP server as its result channel, by whatever mechanism the vendor supports. It exposes two tools: **`submit_review_result`** (`round_token`, `kind: "findings" | "clean"`, `findings`), which posts the participant's round result to the kcap server — the **only** delivery channel: the server does not read the participant's transcript, so ending a reply with `FINDINGS:`/`NO FINDINGS` markers delivers nothing (servers ≥ Flows Phase E-0) — and **`send_flow_message`** (`text`), which pushes a short out-of-band note to the flow driver between rounds (a notable observation, a blocking question); the driver sees it as `pending_messages` on its next flow call, so delivery is not immediate. Messages are retry-safe (client-generated `message_id`, deduplicated server-side) and are NOT a substitute for round results. It is deliberately separate from `kcap mcp flows` so an unattended reviewer can never start a nested flow, and it reads its identity from daemon-provided environment (`KCAP_FLOW_AGENT_ID`); run manually it just exits with an explanation. It's not necessarily the only server a reviewer gets, though: the flow definition's `mcp:` allowlist can additionally grant kcap-owned context servers (e.g. `kcap-sessions`), resolved against the same built-in registry — unknown names are skipped and any flow-starting server is always stripped regardless of listing, so a reviewer still can't start a nested flow.

Cursor reviewers run only in daemon-owned worktrees and launch with Cursor's native `--force --approve-mcps --trust` controls so command, MCP-server, and workspace-trust prompts are suppressed at the source. kcap does not auto-approve or route a fallback interaction to a human: any permission, elicitation, or unknown ACP interaction frame violates the zero-prompt contract and immediately reaps the reviewer. Copilot reviewers require an authenticated Copilot CLI with access to the requested model; the daemon preloads this MCP configuration and clamps Copilot's available tools to the validated flow allowlist. That clamp is exclusive, so an owned-worktree Copilot reviewer has no ambient file or shell tool at all. A **borrowed-context** Copilot reviewer is granted read/search tools (`view`, `grep`, `glob`) so it can read the snapshot, and is confined by an OS sandbox instead of by the clamp — see below. There is nothing to register or configure.

#### Borrowed-context Copilot reviews

A Copilot reviewer can read your working tree — uncommitted and untracked changes included — from a daemon-owned snapshot. Because that requires giving it real read tools, the containment boundary moves below the vendor into an OS sandbox, and the capability is only advertised where all three of these hold:

| Requirement | Why |
|---|---|
| **macOS on ARM64** | The only platform whose Copilot tool surface has been measured. Elsewhere the capability is not advertised. |
| **`sandbox-exec` present** | The reviewer process is confined to the snapshot. A host that cannot enforce that gets no borrowed review rather than an unconfined one. |
| **A brokered token** in the daemon's environment — `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN` | The sandbox deliberately does **not** grant your login keychain, so the reviewer authenticates from a token the daemon passes through. `kcap` never reads, stores, or prompts for one. |

The token must be in the environment of the **daemon process itself** — not merely in the shell you start a review from. For a daemon you run yourself:

```bash
export GH_TOKEN="$(gh auth token)"   # or COPILOT_GITHUB_TOKEN / GITHUB_TOKEN
kcap daemon start -d
```

For a **supervised** daemon (`kcap daemon service install`), set `KCAP_COPILOT_TOKEN_CMD` instead — a command that *prints* a token:

```bash
export KCAP_COPILOT_TOKEN_CMD="gh auth token"
kcap daemon service install
```

The unit file stores the **command**, never a token, and the daemon runs it to obtain a fresh credential each time it launches a borrowed reviewer. That matters twice over: a service unit is a file on disk, so a token in it would be a token at rest; and resolving per launch means a rotated or revoked credential takes effect immediately instead of when the daemon next restarts.

Deliberately *not* supported: putting `GH_TOKEN` itself in the unit. `install` carries over only `PATH`, `KCAP_PROFILE`, `KCAP_URL`, `KCAP_CLAUDE_PATH`, `KCAP_CODEX_PATH` and `KCAP_COPILOT_TOKEN_CMD` from your shell — credentials are excluded by design. (`install` also writes two keys of its own: a generated `KCAP_DAEMON_SUPERVISED` marker, and `KCAP_CONFIG_DIR` set to the configuration directory the install itself resolved.) Unit files are written owner-only (`0600`).

The command runs under your shell, so anything that prints a token works — `gh auth token`, a `security find-generic-password` lookup, a secret-manager fetch. It runs **only when a borrowed review actually needs a token**, never at daemon startup: the daemon does not mint a credential nobody asked for. It is bounded at 10s and to one token-sized line, and its output is never logged, so a command that prints a secret on failure cannot leak it.

A daemon with a command *configured* advertises borrowed review, so a command that is broken fails at launch with the coded `borrowed_review_auth_unavailable` — the same honest rejection an unset variable produces. Verifying it up front would mean running it at every startup, which is the trade this deliberately does not make.

With any of the three requirements missing, a Copilot borrowed request is refused up front with `vendor_containment_unreadable` naming the daemon and the remedies, and `mode="context-only"` still works. Nothing silently falls back to reviewing a stale committed base.

The sandbox also gives the reviewer a **fresh, per-launch `HOME` and `TMPDIR`**, so it never sees your prior Copilot sessions, command history, or caches, and its own state is discarded when the review ends.

### Memory MCP server (for agents)

```bash
kcap mcp memory
```

Stdio MCP server that lets coding agents search, save, and update durable "memories" — short, reusable notes (preferences, feedback, project facts, references) scoped to you, your team, or the whole org, so lessons learned in one session are available in the next. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it in `~/.codex/config.toml` (alongside `kcap-sessions` / `kcap-review`), so there's nothing extra to do; enabling the plugin through Codex's native `codex plugin add` also provides it via the `.codex-mcp.json` descriptor. **Cursor:** `kcap setup` / `kcap plugin install --cursor` register it in `~/.cursor/mcp.json` (alongside the other three kcap servers); opt out with `--skip-cursor-mcp`. **GitHub Copilot CLI:** `kcap setup` / `kcap plugin install --copilot` register it in `~/.copilot/mcp-config.json` (alongside the other three kcap servers); opt out with `--skip-copilot-mcp`. **Gemini CLI:** `kcap setup` / `kcap plugin install --gemini` register it in the shared `~/.gemini/settings.json` (alongside the other three kcap servers); opt out with `--skip-gemini-mcp`.

It provides ten tools:

- **`search_memories`** — hybrid semantic + keyword search over memories visible to you (your own, your teams', and org-wide). Agents are told to call this before saving a new memory.
- **`get_memory`** — fetch a memory's full content by id or slug. On a log, `content` is its header and `entries` holds a bounded window — open entries newest first, then recently settled ones — with `open_next` / `settled_next` cursors for what it leaves out.
- **`save_memory`** — save a new memory with an `audience` (`user`, `team`, `org`, or `project`), `slug`, `description`, `content`, and `kind`. For `audience: "project"` pass `audience_project: <slug>` — that project's members can then see and edit it (you must be a member). Where it lives is a separate axis: the current repo by default; a **project** with `project: <slug>` (it then surfaces across that project's repos — the right home for a learning that spans repos, instead of saving it org-wide); or the whole org with `global: true`. `project` wins over the repo, and like `rescope_memory`'s `project` it sets the place, not the people. Pass `machine_specific: true` (user audience only) to tag a memory to this machine. Pass `form: "log"` to save a **log** — an append-only list of entries, for an inbox or anything else that grows by items — whose `content` is a short header (max 8 KiB). A server that predates logs saves an ordinary memory, and the result says so.
- **`update_memory`** — update an existing memory's description, content, and/or kind. On a log, pass `header` instead of `content`; `content` is refused with a message naming `append_memory_entry`.
- **`rescope_memory`** — change a memory's **audience** (who can see + edit it — promote a personal memory to the team, the org, or a **project's members** with `audience: "project"` + `audience_project: <slug>`), or move its home (where it surfaces) to a project with `project: <slug>`, to a single repo with `repo_hash: <hash>`, or to the whole org with `global: true`. These are orthogonal axes: `audience_project` sets the people, `project` / `repo_hash` / `global` set the place — at most one place target per call. A `project` move is independent of audience and takes precedence over it, so pass `audience` **or** `project` (or both). A `repo_hash` or `global` move must travel alone — no `audience`, `team`, `audience_project` or `project` beside it — so a server too old to know it refuses the call instead of applying only the audience part; change the audience in a separate call.
- **`archive_memory`** — soft-delete a memory.
- **`append_memory_entry`** — append an entry (`title`, one line of at most 200 characters; `body`, max 16 KiB) to a log by id or slug. Appends rewrite nothing and never conflict with another writer; pass `idempotency_key` when retrying, and a retry returns the first call's entry instead of adding another. Against a server without logs, the entry tools say the server does not support them.
- **`settle_memory_entry`** / **`reopen_memory_entry`** — settle an entry (optional one-line `resolution`) or reopen a settled one. Any editor of the log may do either; entries are never edited, so a correction is a new entry plus a settle of the old one.
- **`list_memory_entries`** — page a log's `open` or `settled` entries, newest first, from a `before` cursor (`open_next` / `settled_next` from `get_memory`, then each page's `next_before`).

The server is repo- and machine-aware: it resolves the current working directory to a repo hash and the local persisted machine id at startup, and uses both to scope `save_memory` and to bias `search_memories` / `get_memory` results.

At SessionStart (Claude Code), `kcap` also injects a compact **index** of the memories visible for the current repo/machine into the session context, so the agent starts each session aware of what's saved without a search — see [SessionStart team-memory index](#what-it-records) above. Opt out with `kcap config set disable_memory_index true`.

### Work items MCP server (for agents)

```bash
kcap mcp workitems
```

Stdio MCP server that lets coding agents correlate the current session to the SDLC work item (issue/PR) it belongs to, **declare that work item's structure** — its breakdown into parts and its blocks/blocked-by dependencies — read that structure back, dismiss or restore next-work suggestions, and close or reopen loose ends. Registered for every supported harness by `kcap setup` / `kcap plugin install` (Claude Code reads it from the plugin's bundled `.mcp.json`).

It provides these tools:

- **`declare_work_item`** — attach the current session (and its continuation chain) to a work item. Pass exactly one of `issue_key` (a tracker key such as `"AI-1234"`, an issue number in the session's repository such as `"#123"`, a qualified `"owner/repo#123"`, or a GitHub issue URL), `pr_number`, `work_item_id`, or `new_title` (creates a brand-new work item).
- **`get_session_work_items`** — list the work items the current session is attached to.
- **`get_next_work`** — what the user should work on next: work others are waiting on, the user's own unfinished work (work items, interrupted sessions, loose ends) and new backlog. Rows take turns by the signal behind them, one row per signal per pass, in an order that depends on whether others work alongside the user: if they do, teammates blocked on the user, review requests and interrupted sessions lead; if not, loose ends and unfinished work items lead. Each row carries a because-clause, one line of evidence, and the target_key to pass to `dismiss_next_work`, inside a `<next-work-data>` block whose text is sanitised and marked as data, followed by a freshness line (when the feed was read, how current its tracker state is, and any source that was not current). `repo_hash` defaults to the repository the server runs in; `limit` defaults to 5, max 20. On a server with next-work off it answers "Next-work is not enabled on this server." The server's instructions tell the agent to call it first whenever the user asks what to work on next, or before it proposes new work.
- **`declare_loose_end`** — record one concrete piece of work this session leaves unfinished (`text`), so it appears in the user's next-work loose-ends ledger. Idempotent per session, owner and normalized text; the server refuses none-class text (`"none"`, `"n/a"`, …). An optional `subject` (`PROJ-123`, `#123`, `owner/repo#123`, or a GitHub issue URL) names the issue the end is about; when the server has fetched it and it is already closed, the result tells the agent to check the remote before treating the end as unfinished. The feed files the end under its subject's work item; `work_item_id` names another item instead, and `standalone: true` files it under none (at most one of the two).
- **`declare_work_breakdown`** — declare that a work item is broken into parts (`parent_id` + `part_ids`). Idempotent; a part has at most one parent, and every item must be visible to the caller — a part may live in a different repository than its parent.
- **`retract_work_breakdown`** — detach the named parts from the parent.
- **`declare_work_relation`** — declare a dependency between two items (`from_id`, `to_id`, `relation_kind` `"blocks"` or `"blocked_by"`). Both ends must be visible to the caller and may live in different repositories; no self-relation.
- **`retract_work_relation`** — retract a previously declared dependency.
- **`get_work_item_topology`** — read a work item's parent, parts, and dependencies (scoped to what the caller can see).
- **`merge_work_item`** — merge a duplicate item into another (`work_item_id` → `into_work_item_id`): its sessions and links move to the survivor. Refused when a user marked either item standalone, rejected the pairing, or the items sit in different tracker hierarchies.
- **`detach_work_item`** — detach a session from a work item it was wrongly attached to; durable against automated re-attach, and unable to remove a user-pinned attachment.
- **`declare_work_incomplete`** — declare that the session's work on an item is not finished (`work_item_id`, optional `reason`), so a tracker close that came before it does not mark the item done; a PR merged or issue closed afterwards settles it again. The session must be attached to the item; a repeat is a no-op.
- **`list_work_item_evals`** / **`get_work_item_eval`** — list a work item's evaluation runs you can read and read one run's results, judged requirements and retrospective.
- **`request_work_item_eval`** / **`cancel_work_item_eval`** — queue an evaluation of a work item (`mode` `process` or `root_cause`), or cancel your own run while it is still queued.
- **`dismiss_next_work`** — record that the user turned down a presented next-work suggestion (`target_key`; `repo_hash` defaults to the repository the server runs in, same as `get_next_work`), so it stops being offered. Call it only after the user has said they won't do it; the response's `page_one` is what to offer next, rendered the same sanitised way as `get_next_work`'s rows, and a `not_presented` refusal surfaces as a tool error naming that code.
- **`restore_next_work`** — undo a dismissal so the suggestion can be offered again (`repo_hash` defaults the same way); restoring something not dismissed succeeds and changes nothing.
- **`list_dismissed_next_work`** — list the suggestions the user has dismissed, most recent first, with when and why each was dismissed, rendered the same sanitised way.
- **`list_loose_ends`** — list the user's loose ends with their `loose_end_id`, open ones by default or `status: "closed"`, inside the same sanitised `<next-work-data>` block. `repo_hash` defaults to the repository the server runs in; `session_id` narrows to ends that session sighted; `limit` defaults to 20, max 50; pass the returned `next_cursor` back as `cursor` for the next page. `get_next_work` also names each loose end's `loose_end_id` in its evidence. Lists include admitted claim identities, statuses and workers. `claimed_session_id` filters current ownership independently of the sighting-source `session_id`; `status` remains only open or closed.
- **`claim_loose_end`** — claim a `loose_end_id` before working on it in the current session (or an explicit `session_id`). Work remains open and inspectable, but is withheld from suggestions while owned. Keep the returned `claim_id`; `recorded_catching_up` still means ownership was recorded. Attachment to a work item alone does not claim work.
- **`release_loose_end`** — release the current `claim_id` without closing unfinished work. The working `session_id` defaults to the harness session; an explicit value wins. Stale predecessors cannot release successor claims.
- **`close_loose_end`** — mark finished work done (`loose_end_id`, which also takes the `declaration_id` `declare_loose_end` returned; a server too old to resolve one answers `loose_end_not_found`, so find the end by its text in `list_loose_ends` and retry with its `loose_end_id`). For claimed work, supply the current `claim_id` and working session; unclaimed completed work needs no claim. This is completion, not pickup. A later sighting can reopen the work.
- **`reopen_loose_end`** — undo a close (`loose_end_id`, or a `declaration_id` on the same terms as close), putting the end back in the user's next work.

`declare_work_item` / `get_session_work_items` / `declare_loose_end` / `detach_work_item` / `declare_work_incomplete` default `session_id` to the session the MCP server runs in (Claude Code's `CLAUDE_CODE_SESSION_ID`, else `KCAP_SESSION_ID` or Codex's `CODEX_THREAD_ID`) when omitted. This is the manual path alongside the server's own mechanical and LLM-assisted correlation — use it when an agent already knows which issue or PR a session belongs to, and to record a breakdown/dependency structure the server can't infer (Home's blockers & dependencies and progress figures render only from declared parts and relations).

For hosted execution, `start_agent(work_item: "le:<id>")` reserves ownership before dispatch, including itemless work; do not claim it for the caller first. A `requested`/`sent` response means the command was sent, not that the agent registered. `pending` with `dispatch_state: "unknown"` or `"not_sent"` retains the reservation without confirming dispatch. Report the `loose_end_claim_id` and agent URL; do not start another agent to recover from uncertainty. Claim conflicts, unsupported servers and readiness failures never fall back to closing, dismissing or launching without ownership.

### Plans MCP server (for agents)

```bash
kcap mcp plans
```

Stdio MCP server that lets coding agents keep kcap's record of the plan a session executes: the plan, spec or design document it works from, the ordered task list, and each task's status. Nothing is inferred — the agent declares it, and the session view, the eval judge and `kcap validate-plan` read it back. Registered for every supported harness by `kcap setup` / `kcap plugin install` (Claude Code reads it from the plugin's bundled `.mcp.json`).

It provides four tools:

- **`declare_plan_document`** — declare the document this session works from: `kind` (`plan`, `spec` or `design`) and `path`, plus optional `argues_from` (the spec a plan implements, or the design a spec refines, so both land on one plan), `work_item_id`, `plan_id` (the plan to attach it to, when continuing one from a different checkout) and `workspace_root` (the checkout the document is in, when it is not the project directory). A document in another worktree of the session's repository is accepted by absolute path without naming it; one in another repository needs `workspace_root`. The CLI reads the file, sends its SHA-256 and — at or under 256 KB — its content, and keys the path relative to the git repository root so the declared document and its discovered twin share one identity; a larger file is declared by hash only and the result says so. Returns `plan_id`, `document_key` and `created`; declaring the same file again lands on the same plan and replaces the stored copy with the file's current text, so an agent declares it again after changing it (Claude Code sessions do this automatically for `Write`/`Edit`/`MultiEdit`; see [Declared-document refresh](#what-it-records)).
- **`set_plan_tasks`** — declare the task list as a full ordered snapshot (`title`, optional `task_id`, `status`, `note` per entry), replacing the declared list; entries carrying a known `task_id` keep it, the rest are minted. Returns the tasks with ids and ordinals.
- **`update_plan_task`** — record one transition, by `task_id` or 1-based `ordinal`, to `pending`, `in_progress`, `completed` or `skipped`, with an optional `note`. When no `plan_id` is given the CLI resolves the session's current plan first, so the result always names the plan it acted on, and a session with no plan gets an error naming the fix.
- **`get_plan`** — read a plan back: documents, tasks with status and source, and progress. The recovery call after context compaction; a session with no plan gets an empty result, not an error.

Without `plan_id`, every tool acts on the session's current plan — the one it most recently wrote to; a session on no plan gets one created by `set_plan_tasks`. `session_id` defaults to the session the MCP server runs in (Claude Code's `CLAUDE_CODE_SESSION_ID`, else `KCAP_SESSION_ID` or Codex's `CODEX_THREAD_ID`) when omitted.

### Knowledge MCP server (for agents)

```bash
kcap mcp knowledge
```

Stdio MCP server for reading retained facts and curated skills, then fine-tuning them. `kcap setup` and `kcap plugin install` register it with each supported harness. It offers eight tools:

- **`list_skills`** / **`get_skill`** — browse skills and read one skill with its bodies, members, assignment pins, history and `doc_revision`.
- **`list_facts`** / **`search_facts`** — browse retained facts or search by meaning within a repo, project or org home. Results carry a `curation_key` for writes.
- **`edit_skill_body`** — edit a pending draft.
- **`transition_skill`** — approve, reject, dismiss, revoke or restore a skill.
- **`adjust_skill_members`** — exclude or include members for the next approval, or add, remove or clear assignment pins for the topic sweep to settle.
- **`curate_fact_cluster`** — curate a fact cluster by its `curation_key`.

Reads default to the current repository and refuse to widen when none resolves. List responses include `next_cursor` for the next page. Writes carry an `operation_id`; skill writes use the `doc_revision` from `get_skill` or the last write as `expected_doc_revision`. Server refusals pass through with their codes and any current revision they return so an agent can recover. To move a cluster out of a serving skill, exclude it, approve a body without it, then remove its pin before adding it to the destination skill.

This server can change knowledge, so it is not pre-approved for unattended reviewers. It requires `kcap login` and a server with `/api/knowledge` endpoints.

### Pages MCP server (for agents)

```bash
kcap mcp pages
```

Stdio MCP server that lets a coding agent publish a **self-contained HTML page** to the kcap server and hand back a link — a plan for review, a comparison table, a report someone would rather read as a page than as terminal output. The page is served under a sandbox that cannot reach the network, so every style, script and image must be inlined as a data URI; an external URL renders as nothing. A page is private to its owner until `visibility` says otherwise, and re-publishing with `update_id` revises it without changing the URL, so a link you already shared stays good.

It provides seven tools, kept deliberately narrow — an agent's context pays for every schema it carries whether or not it ever publishes, and version history and takedown live in the web UI:

- **`publish_page`** — publish a page. `title` plus either `html` or a local `path`; optional `description`, `visibility` (`none` / `org` / `scoped`), `grants`, `session_ids`, `response_schema`, and `update_id` to revise an existing page.
- **`await_page_responses`** — block until people have answered. Returns on a respondent count, on a close, or on a timeout — a timeout is a result, not an error.
- **`get_page_results`** — tallies and each person's current answer, without waiting.
- **`close_page_responses`** — freeze a version's answers. `closed: false` reopens, which also clears any deadline.
- **`list_my_pages`** — the pages you can see: id, title, audience, latest version, URL.
- **`get_page`** — read a page you can see, yours or one shared with you, by id or link: its record and its readable text, or its HTML with `format: "html"`. The content is marked as the author's, to be read as data rather than followed.
- **`set_page_visibility`** — replace a page's audience. A grant left out is one being taken away.

**The human checkpoint.** Declaring a `response_schema` at publish makes the page answerable: fields of type `choice`, `multi`, `score` or `text`, which the server validates every answer against and tallies. The agent publishes a plan or a decision, shares it, then blocks in `await_page_responses` until the people who must sign off have answered — reviewable on a phone, by several named people, with the approval recorded next to the page. Who answered is the authenticated viewer, resolved server-side; the page cannot claim it. `results_mode` decides what other viewers see: `owner` (default, only their own), `aggregate` (tallies, never names or free text) or `named`.

The current session is cited automatically from `KCAP_SESSION_ID` when set, so an agent's publish is attributed to the work that produced it without ceremony. Requires `kcap login` and a kcap-server new enough to expose the `/api/artefacts` endpoints.

### Analytics MCP server (for agents)

```bash
kcap mcp analytics
```

Stdio MCP server that lets coding agents answer analytics questions about the org's recorded AI coding sessions — spend, token/tool/model usage, session outcomes, commits, PRs, evals — by writing **governed read-only SQL** against the server's curated analytics views. Every statement is validated server-side (allowlisted views/columns, SELECT-only, row-capped, repo-scope enforced), so the agent gets the same governed data surface as the web UI's Analytics tab. It's repo-aware: it resolves its scope from the process working directory, so it rides the same registration path as `kcap-sessions` and is offered to every harness. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it in `~/.codex/config.toml`; enabling the plugin through Codex's native `codex plugin add` also provides it via the `.codex-mcp.json` descriptor. **Cursor:** in `~/.cursor/mcp.json` (opt out with `--skip-cursor-mcp`). **GitHub Copilot CLI:** in `~/.copilot/mcp-config.json` (`--skip-copilot-mcp`). **Gemini CLI:** in the shared `~/.gemini/settings.json`, marked `"trust": true` (`--skip-gemini-mcp`). **SST OpenCode:** in `~/.config/opencode/opencode.json` (`--skip-opencode-mcp`). **Google Antigravity:** in `~/.gemini/config/mcp_config.json` (`--skip-antigravity-mcp`). **AWS Kiro CLI:** in `~/.kiro/settings/mcp.json` (`--skip-kiro-mcp`).

It provides two tools:

- **`get_analytics_schema`** — the governed schema document: the queryable views and columns, a terminology glossary, SQL rules, and worked examples. Agents are told to call this once before writing SQL.
- **`query_analytics`** — run one governed Postgres SELECT. Defaults to the current repository (resolved from the working directory); pass `scope: "global"` for org-wide questions, and `max_rows` to adjust the row cap. A rejected query returns the validator's reason so the agent can fix the SQL and retry.

Requires `kcap login` and a kcap-server new enough to expose the `/api/analytics` endpoints (older servers return a clear "upgrade kcap-server" message).

### Pages

Publish a self-contained HTML page to the kcap server and get back a link — the same shape as a session share link, but for a page you wrote. The page is served under a sandbox that cannot reach the network, so inline every style, script and image as a data URI; an external URL renders as nothing.

```bash
kcap page publish plan.html --title "Migration plan" --visibility org
kcap page publish report.html --visibility scoped --to team:platform --to user:github:7
kcap page publish plan.html --update art_01J9...   # new version, same URL
kcap page list --mine
kcap page get art_01J9... --html                   # a page you can see, yours or shared
kcap page share art_01J9... --visibility none      # take it back to private
kcap page delete art_01J9...
```

A page is private to its owner until `--visibility` says otherwise. `--to` names one audience member (`user:<id>`, `team:<slug>`, `project:<id>`) and is only read under `scoped`; on `share` it replaces the whole audience, so a grant you leave out is one you are taking away. `--session` cites a session the page came out of and defaults to `KCAP_SESSION_ID`, so an agent's publish is attributed without ceremony. The URL is whatever the server says it is — printed on stdout; refusals go to stderr and name the limit they hit along with its value.

### Import local memories

Claude Code keeps its auto-memory per machine, under `~/.claude/projects/<dir>/memory/`. `kcap memories import` copies those notes into server memories once, so they follow you to another machine and can be shared. Run it on the machine that holds the files.

```bash
kcap memories import --dry-run                 # show the plan for this directory's Claude project
kcap memories import --audience org --global   # save as org-wide memories, after confirming
kcap memories import --from ~/notes --update   # read another folder; overwrite slugs that exist
```

Each note's `name` becomes the slug, `description` the summary and the body the content; type `user` is saved as `preference`. `MEMORY.md` is skipped. The memories land at the current checkout's repository unless `--project <slug>` or `--global` says otherwise — a Claude project folder is only imported from the checkout it was recorded in — with the `user` audience unless `--audience team|org` says otherwise. A slug that already exists is reported and left alone, or overwritten with `--update`, so a re-run is safe; a near-duplicate the server finds is reported beside the save. An `--audience org` import you may not publish directly is queued for an admin's approval; with a `--project` home, re-running it after approval queues it again.

### Curate guidelines

Sync the repo's promoted curation guidelines into its `CLAUDE.md` and/or `AGENTS.md` via a managed block. The server tracks which guidelines have been promoted for the current repo; `curate apply` fetches them and writes (or updates) a `<!-- kcap:curated:start -->…<!-- kcap:curated:end -->` block in the relevant files. Content outside the markers is never touched.

```bash
kcap curate apply             # preview changes and confirm interactively
kcap curate apply --dry-run   # print what would change without writing anything
kcap curate apply --yes       # apply without prompting (CI / scripted)
kcap curate apply -y          # shorthand for --yes
```

### Skills sync

Materialize the current repo's approved, skill-targeted guidance docs into the checkout or
linked worktree the sync runs in — `kcap-<slug>/SKILL.md` under `.claude/skills`, the
agent-agnostic `.agents/skills`, `.kiro/skills`, and `.gemini/skills`, which Gemini CLI documents
but which no harness has been measured reading from a repository. A linked worktree gets its own
copy, synced independently of the main checkout's. The server is the canonical source: each sync
fetches the repo's versioned snapshot per target, writes new or re-approved skills, and prunes ones
revoked centrally. Inside those four directories it touches only its own `kcap-` subdirectories;
outside them it writes two things — the exclusion block described below, and a ledger of what it
owns under the worktree's own git directory (`<git-dir>/kcap/skills/`, which Git removes with the
worktree) — and it deletes the user-global copies earlier versions left. The generated directories
are kept out of Git through the repository's own `.git/info/exclude` rather than a tracked ignore
file, so any ignore rules you've committed stay untouched, and one exclusion block covers the main
checkout and every linked worktree. If this repo's skills already exist under your home directory,
syncing clears them — unless another repository's sync still owns them, in which case they stay
until that repository's sync clears its share.

A skill restricted to one vendor still lands only in that vendor's tree — a Claude-only skill
goes to `.claude/skills` and nowhere else — but Copilot, Cursor, and OpenCode read
`.claude/skills` too, so any of them working in the same checkout can see a skill approved only
for Claude Code. Claude and Kiro are also the only vendors whose tree is fetched under their own
name, so a skill restricted to Codex, Copilot, Cursor, Gemini, OpenCode, Pi, or Antigravity is not
delivered at all: no request names them, and a request that names no vendor keeps every
vendor-restricted doc out of what it returns. Requires `kcap login` and a repo checkout (the
repo is detected from the working directory).

```bash
kcap skills sync              # fetch, write and prune this repo's skills
kcap skills sync --dry-run    # print what would change without writing anything
kcap skills sync --auto       # hook-spawned form: silent, and skipped when synced recently
```

Opt into an automatic background refresh with `kcap config set skills.auto_sync true`: the Claude
session-start hook then spawns a detached, self-throttling sync (at most one network round-trip per
~6 hours per skills tree, so up to four for a checkout that has all four), so centrally revoked or
re-approved skills reach the machine without a manual sync. It writes into the checkout it runs in,
exactly as a manual `kcap skills sync` does — the files land in the repository's own skills trees,
not under your home directory. Off by default.


### Loading historical sessions

Live capture and import use the same bounded secret redaction. Each record and each batch of encoded lines is limited to 4 MiB of UTF-8 content. If a record exceeds the size or processing bounds, the CLI reports the reason and uploads a capture-loss marker at its source line number. Other records continue importing. Install server support for capture-loss markers before rolling out this CLI behavior so session details can display the warning.

#### Targeted capture recovery

```bash
kcap import --session <id> --repair-capture --dry-run
kcap import --session <id> --repair-capture
```

Recovery supports **Claude and Codex** sessions that you own and that have ended.
It scans the local root and discovered child transcripts, redacts each line, and
compares them with existing server streams. It restores provably missing supported
file-tool records and refreshes turn cards, file totals and summaries. Other omitted
records can remain as reported gaps. It creates no new sessions or child streams.

Deploy the server's capture-loss and recovery support **before the CLI**. The CLI
checks support and ownership before reading transcripts. `--dry-run` performs the
same comparison without durable repair writes. `--claude` or `--codex` can narrow
local discovery; normal import scope, filter and action flags such as `--all`,
`--repo`, `--org`, `--reimport`, `--private` and `--since` are rejected in repair mode.
A missing source or a file changing during the scan refuses the operation.

Recovery uploads are capped at **100 lines and 4 MiB of serialized JSON**, including
escaping and coordinates. A single redacted line that cannot fit is refused. Server
staging allows **128 sources, 4,096 candidates and 64 MiB of candidate payload** per
operation and expires after 20 minutes without activity. Interrupted scans restart
from the beginning. Once validated, the operation survives CLI exit and server restart;
retries are idempotent and leave ordinary import cursors unchanged.

The command prints candidate/restored counts, remaining gaps and the repair phase.
Exit **0** means complete accounting with zero gaps (or a clean dry-run preview),
**2** means a completed recovery/preview with residual gaps, and **1** means refusal,
failure or unfinished accounting. The CLI waits up to two minutes for a durable job,
then prints its ID and status; follow the session's capture status while it continues.

#### Normal historical import

Backfill older sessions from every detected coding agent in a single run. Most agents ship per-session `.jsonl` transcripts (`~/.claude/projects/`, `~/.codex/sessions/`, `~/.cursor/projects/<sanitized-workspace>/agent-transcripts/`, `~/.copilot/session-state/`, `~/.gemini/tmp/<project>/chats/`, `~/.kiro/sessions/cli/`, `~/.pi/agent/sessions/`, `~/.local/share/muse/sessions/` honouring `XDG_DATA_HOME`). They're discovered automatically and the command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kcap import --all                            # every discovered session from every agent
kcap import --org EventStore                 # sessions whose git-remote owner is EventStore
kcap import --org                            # pick an owner from discovered repos, then remember it
kcap import --repo owner/repo                # one specific repo
kcap import --repo owner/one --repo owner/two  # several — repeat the flag per repo
kcap import --discover                       # what's on disk, per repo, without importing
kcap import --discover --json                # the same report, machine-readable
kcap import --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kcap import` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` filters by the **git-remote owner** (GitHub org/user) detected on each session — not by your profile name. This makes it independent of how you signed in: under WorkOS the active profile is named after the tenant slug (which is not a GitHub org), so the owner to scope on is taken from the flag value or chosen from your discovered repos instead. Pass it explicitly as `--org <owner>`, or run a bare `kcap import --org` once on an interactive terminal to pick an owner from your discovered repos — the choice is saved to the active profile (`import_org`) and reused by later bare `--org` runs. Non-interactive runs (CI) must pass `--org <owner>` (or have a remembered org).

By default every available agent is imported. Pass one or more vendor filters to restrict the run:

```bash
kcap import --claude --org                   # only Claude transcripts
kcap import --codex --org                    # only Codex rollouts
kcap import --cursor --all                   # only Cursor — every discovered transcript
kcap import --cursor --cwd /path/to/proj     # only Cursor sessions whose workspace folder matches
kcap import --copilot --all                  # only Copilot — every discovered transcript
kcap import --gemini --all                   # only Gemini — every discovered transcript
kcap import --kiro --all                     # only Kiro — every session log under ~/.kiro/sessions/cli
kcap import --pi --all                       # only Pi — every discovered session
kcap import --opencode --all                 # only OpenCode — every session in opencode.db
kcap import --antigravity --all              # only Antigravity — GUI and agy CLI brains
kcap import --grok --all                     # only Grok Build — sessions and their subagents
kcap import --muse --all                     # only Muse Code — every session log under ~/.local/share/muse/sessions
```

Sessions import most-recent-first, so your latest work reaches the dashboard first. Every form is idempotent — safe to run multiple times. Each run ends with `N imported · N skipped · N failed`, then a breakdown of why each session was skipped. Failures never abort the run or change the exit code: everything that could be imported still is, and because the run is idempotent, re-running retries the failures without re-sending anything already on the server.

If your repo directories have been renamed or deleted on disk, the import prints a list of unresolved cwds up front. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) to recover those sessions.

Cursor historical import walks every JSONL transcript under `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and posts each line through the same `POST /hooks/transcript` route the live hook path uses, so live and historical ingest converge on one canonical event stream. The walker resolves each session's working directory by matching its sanitized workspace name against `~/Library/Application Support/Cursor/User/workspaceStorage/*/workspace.json` (on Linux: `~/.config/Cursor/User/...`); sessions whose workspace can't be resolved are still imported, just without `cwd` and git owner/repo enrichment.

Kiro historical import reads each session's append-only log at `~/.kiro/sessions/cli/{id}.jsonl` (plus the sibling `{id}.json` for cwd / model / title) and posts the lines through `POST /hooks/transcript` — the same lines the live watcher tails, so live and historical ingest converge. Set `KIRO_HOME` to point at a non-default location. Kiro persists no token counts, so imported Kiro sessions show no token usage (by design). Re-imports are idempotent — event ids are deterministic over `(session id, message/tool id, kind)`.

Codex historical import walks `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`. Collab subagent rollouts (Codex CLI 0.146+ — the rollout header names the parent thread) are excluded from top-level discovery and imported nested under their parent instead: every transitive descendant (children, grandchildren, and so on, up to a depth of 8) lands as a direct subagent of the top-level root, mirroring the OpenCode import. A rollout whose header can't be read yet (a session actively starting while the import runs) is skipped for that run and picked up by the next one.

OpenCode historical import reads its SQLite database (`~/.local/share/opencode/opencode.db`, honouring `XDG_DATA_HOME`) and reconstructs the same `{info,parts}` lines the live plugin streams — so live and historical ingest converge on one canonical event stream (`vendor=opencode`). Unlike live capture, which only nests direct children, historical import walks every transitive descendant session (children, grandchildren, and so on, up to a depth of 8) and imports each one as a direct subagent of the top-level root (`/hooks/subagent-*`) — flattened because a session's stream key can't express deeper nesting. A descendant beyond the depth cap is never silently dropped: import prints a `[kcap] opencode: root <id> descendants_omitted=N (depth cap 8 exceeded)` diagnostic to stderr, appending `(lower bound — counting ceiling hit)` in the rare case where counting the omitted subtree itself hit an internal safety cap before finishing — the count is then a lower bound, not exact. Because the server exposes no completeness signal, kcap records each fully-imported session in a local ledger (`~/.cache/kcap/opencode-imported.json`, keyed by server) to skip it on re-run — the ledger key is a content fingerprint over the whole descendant tree (including omitted ids), so a newly-reachable descendant invalidates a stale entry; a session interrupted mid-import is repaired on the next run. The ledger trusts local state, so a session deleted server-side after import is wrongly skipped on re-run — pass `--reimport` (see Additional flags below) to bypass the ledger for the selected sessions. Re-imports are idempotent — canonical event ids derive from OpenCode's stable `prt_` part ids.

Claude (`CLAUDE_CONFIG_DIR`), Codex (`CODEX_HOME`), Gemini (`GEMINI_CLI_HOME`, which names the parent of `.gemini`), OpenCode (`OPENCODE_CONFIG_DIR`), and Pi (`PI_CODING_AGENT_DIR`, which names the `~/.pi/agent` leaf) historical/live paths follow each agent's own config-relocation environment variable when it is set, so a relocated config is discovered automatically.

Additional flags:

```bash
kcap import --org EventStore --yes           # skip the confirmation prompt
kcap import --org EventStore --private        # mark every imported session as Only Visible to You
kcap import --org EventStore --since 2026-01-01  # only sessions on or after this date
kcap import --org EventStore --cwd /path/to/project  # filter by working directory
kcap import --org EventStore --session abc123    # single session
kcap import --org EventStore --skip-title    # don't spend your own agent quota on titles
kcap import --opencode --session ses_x --reimport  # force one OpenCode session past its ledger entry
```

`--discover` reports what is on disk and exits without importing anything: sessions and most-recent date per repository, how many could not be matched to one (those are the sessions `--all` includes and any `--repo`/`--org` selection drops — usually a renamed directory, see `kcap remap`), and a total for each `--since` window the CLI offers. It needs no scope flag, since it is what you run to decide which one to use, and it needs no server or login — the whole report comes off local disk.

`--discover --json` emits the same report as JSON on stdout and nothing else, so it can be piped. Each window carries its own `since` (null for "everything") rather than a label, so a consumer reads the boundaries off the report instead of re-deriving them. Each repository carries its own `windows` too, in the same order, so the count for any selection of repositories in any window is a sum rather than a guess. Each vendor is dated the way `--since` dates it — Codex by the rollout's day directory, Claude by the transcript's first timestamp — so a window's count predicts what importing with that `--since` would actually select.

`--reimport` forces OpenCode sessions to re-import even when the local completeness ledger (described above) records them as already loaded — the escape hatch for a session that was deleted server-side (e.g. via `kcap disable`) but is still marked complete locally, which a plain re-run would otherwise skip. Scope it with the usual vendor/`--repo`/`--cwd`/`--session` filters to force just the affected sessions; the re-send is idempotent, and a successful forced import refreshes the ledger entry. It has no effect on other vendors, which already re-classify every run.

Import generates a title for each **Claude and Codex** session it loads by shelling out to your own `claude` / `codex` — once per session, on your subscription, in the background. `--skip-title` turns that off, the same opt-out `kcap watch` takes for the same generator. Sessions are fully searchable without a title; it only affects how recognisable they look to you. The other seven agents never generate one locally, so the flag changes nothing for them. Copilot, Kiro, OpenCode, Cursor and Antigravity forward the title the agent itself stored for the session — Cursor the chat's name (from its `meta.json`, else the IDE's own record), Antigravity its conversation summary title — and so does Codex, from its `session_index.jsonl`, whenever you have named the thread. A Codex session whose name the server accepts gets no generated title, since the agent's own name outranks one; an unnamed Codex session is still titled by the generator. Sessions an earlier import already loaded are re-sent only their stored title, so a later rename in the agent reaches the server on the next run. The import waits up to about a minute for the server to register a session before it gives up on the title with a warning. Gemini and Pi write their titles into the transcript, which the server reads as it ingests it. A server too old to know agent titles keeps whatever title the session already has, and a Codex session there is still offered a generated title.

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete. A server-side tracker deduplicates events on `(stream, eventId)` so previously-imported turns don't get re-appended.

`--private` also covers sessions the run only *revisits*. Agents that record subagents (Cursor, Antigravity, Gemini) can attach a previously-missed subagent to a session that was already fully imported, so for those a `--private` re-run marks every session it touched private — not just the ones with brand-new top-level content. That is deliberate: a session's visibility can't be left to whether a re-run happened to find new content, and re-running with `--private` is the supported way to privatize sessions an earlier non-private import made visible.

After discovery, the import surfaces a one-shot report of any transcript working directories that no longer exist on disk. Sessions whose cwd was an ephemeral worktree (e.g. `~/dev/my-repo/.claude/worktrees/<slug>` or `~/dev/my-repo/.capacitor/worktrees/<slug>`) are transparently attributed to their parent project when that project still exists, so deleted-worktree paths drop out of the missing-cwds list. kcap's own background helper runs (the headless `claude -p` calls behind title generation and "what's done" summaries) record their transcripts in a throwaway temp directory that is removed the moment the run ends; these are never imported, and they're also excluded from the missing-cwds report so its dead temp paths don't drown out real ones. What remains is typically local repo dirs that have been renamed — those won't match an `--org` / `--repo` scope until you tell kcap how their old paths map to the new ones. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) below for the fix.

### Muse Assistant

Muse Assistant (Meta's consumer agent) runs in a Meta-hosted VM with no transcript file, no local gateway and no lifecycle hook; its chat store is reachable only by Muse itself, through `chat.list` and `chat.read_messages`. `kcap muse-assistant` records it from inside that VM with Muse as the recorder: the `kcap-recording` skill (`kcap/muse-assistant/kcap-recording/SKILL.md`, installed by the connect-kcap skill when the user asks to record) states the consent scope, grants it with `kcap muse-assistant scope add` in the user's own turn, and installs a 15-minute cron that pages each granted chat into files and runs `kcap muse-assistant ingest` on them.

- **What is recorded.** `user`, `assistant` and `commentary` items (Muse narrating its own work, shown as thinking), verbatim, as private sessions owned by the signed-in kcap user. `developer` items — runtime instructions — are never sent. Tool calls, browser actions and files are not in `chat.read_messages` and are not recorded. No model or usage is available, so cost stays empty.
- **Sessions.** A new session starts at every gap of two hours or more between recorded items; an idle session is closed only by an ingest whose walk was fetched at least 2 h 30 m after the last item, at that item's time. A VM clock more than 30 minutes ahead of Muse's store can close early, splitting one conversation in two; nothing is lost.
- **Consent is a kcap-side grant.** `scope add` writes a grant bounded in the chat's own order (`--after-seq`, `--empty-chat` or `--include-history`); nothing from an interval without a grant is sent unless a later `--include-history` grant takes in that history, and `scope remove` revokes the grant and attempts to close the open session; if that post fails it exits 1 and `sweep` delivers it later while the chat's state is intact. A chat's watermark outlives its grants, so a re-grant with `--include-history` reaches back only to where the chat's recording last stopped, and a `scope add` whose boundary differs from an active grant (wider, narrower or another kind) exits 2 and changes nothing, so the chat is stopped with `scope remove` before the new boundary is granted. Grants, watermarks and open sessions live under `~/.config/kcap/muse-assistant/`, in a directory per tenant and user, so two accounts never share a file. A chat whose state file is unreadable, malformed or belongs to another account is repaired by `scope remove` (`scope reset` does the same for every such file), which renames the file to `<chat_key>.json.unusable-<epoch ms>` (kept, never read again) so `scope add` can grant it afresh; a listed chat with no state file is unlisted the same way, and both leave the open session, if there was one, open: `sweep` cannot close it once the state is gone, and `scope remove` exits 3 for exactly that reason. `scope reset` prints a "set aside" or "no grant recorded" line per such chat and exits 3, unless another chat's lifecycle post failed, in which case it exits 1 and those lines still say what was set aside; `sweep` names such a chat, or one with no state at all, as `no usable state` on stderr, and that line can appear with exit 1 as well as 3.
- **Nothing lost, nothing twice.** Each walk is validated whole — Muse's own cursor chain, complete pages, every recorded item intact — before anything is posted; the watermark advances only after the server accepts each call; every lifecycle post is recorded as an intent first and replayed by the next run or by `sweep`; a failed ingest keeps its directory for the next tick. One ingest per chat at a time, held by a lock the kernel releases.
- **Exit codes.** `0` done; `1` server, network or sign-in failure, or an unexpected local failure; `2` malformed input or a wrong argument shape; `3` chat out of scope; `4` from `scope remove` or `scope reset` only: recording could not be confirmed stopped (nothing revoked, or an unexpected error), so retry — retrying is safe. A `scope reset` that exits 1 or 4 may also have set chats aside; its stderr says which.
- **Requires** a server that knows the `muse-assistant` vendor: against an older server the ingest exits 1 and advances nothing.

### Daemon

The daemon connects to the kcap server and runs Claude Code, Codex, or Cursor agents in isolated git worktrees, controlled from the dashboard. Hosted Claude and Codex agents run on macOS, Linux, **and Windows**; hosted Cursor (`cursor` vendor) is macOS and Linux only — choose the vendor from the dashboard's launch dialog. At startup the daemon probes `daemon.claude_path`, `daemon.codex_path`, and the Cursor CLI (`cursor-agent`, overridable via `KCAP_CURSOR_PATH` — see [Daemon config settings](#daemon-config-settings)) and advertises only the vendors it can actually spawn, so the launch dialog hides whichever agent isn't installed on the selected daemon. On macOS, when the default `claude` or `codex` is not on `PATH`, the daemon launches the copy the Claude or ChatGPT desktop app bundles instead — the newest Claude Code build the Claude app has downloaded, looked up again at each launch, or the Codex CLI inside `ChatGPT.app` — so a machine with only the desktop apps can host agents. A path you configure is always used as given.

**Permission presets for ACP-hosted agents.** Interactive ACP-hosted agents (Cursor, Copilot, Gemini, Kiro, OpenCode, Grok) prompt for approval on every action by default. The launch dialog offers an optional **permission preset** for them — *Explore freely* (pre-approves requests the agent CLI classifies as reads and searches), *Edit freely* (also pre-approves file edits, moves, and deletes), or *Auto* (approves every permission request, including shell commands, network access and MCP tool calls). For Explore and Edit the classification is the **agent CLI's own**: kcap does not verify it or confine the action to the workspace, and a pre-approved action runs **without your review**. Under those two, requests the CLI classifies as shell or network — and any request it does not classify (e.g. Kiro, which sends no tool kind) — still ask. Under Auto the agent acts without your review on everything it asks permission for; it still asks you its questions, and [approval-policy](#approval-policy) rules are evaluated first, so a `deny` or `ask` rule still applies. Every preset answers with the agent's single-use allow, never an "always allow" grant, so each request stays visible to kcap; one the agent offers no single-use allow for still asks. Each auto-approval is recorded on the session transcript, distinguishable from a human approval. Auto needs a daemon new enough to advertise it; the dialog offers it only then. Presets apply only to interactive ACP launches; reviewer/flow launches run under their own containment.

**Prompts answered in the terminal.** A hosted Claude session's permission prompt (an `AskUserQuestion` included) is offered in the dashboard and the desktop app, and can just as well be answered in the session's own terminal. The daemon cannot see that answer, so the Claude plugin's `PostToolUse`, `PostToolUseFailure`, `Stop` and `SubagentStop` hooks tell it the prompt is moot — by the finished tool's id, or by the finished turn for a deny, which runs no tool — and the request is withdrawn everywhere at once: the daemon's pending set, the dashboard, and every desktop view, with no chat tab open. The notice goes to the daemon's loopback bridge only, never to the server, and costs a session nobody hosts nothing beyond the hook's own start; an older daemon ignores it.

> **Snapshotting a workspace that isn't a git repo:** when an agent targets a directory that is not a git
> repository with commits, the daemon takes a *standalone snapshot* — it copies the directory rather than
> creating a git worktree. Symlinks are recreated as links (never followed) and anything pointing outside
> the workspace is skipped, so the snapshot cannot pull in credentials or other out-of-tree content.
> **That guarantee assumes nothing else writes to the workspace while the agent is starting.** The check
> that classifies an entry and the read that copies it are not a single atomic operation, so a separate
> account or process that can swap a file for a symlink in between can still get the target's contents
> copied in. In practice: don't point a hosted agent at a directory that any account or process other than
> the daemon's own can write to during a launch. A normal single-user checkout is fine; a shared or
> world-writable directory is not.

> **Windows and hosted Codex:** needs Windows 10 1809 (build 17763) or newer — Windows 11 recommended — because that's the floor for Codex's own Windows sandbox. Older builds don't advertise the `codex` vendor at all. The sandbox *implementation* (`elevated`, which needs one-time admin-approved `winget` setup, vs `unelevated`) is whatever your `~/.codex/config.toml` `[windows] sandbox` says; the daemon inherits it rather than overriding it. Codex is found on `PATH` whether installed via `winget` (`codex.exe`) or npm (`codex.cmd`).

```bash
kcap daemon start                   # start in foreground (--name defaults to KCAP_DAEMON_NAME, the profile's, then your OS username)
kcap daemon start -d                # start in background (daemonize)
kcap daemon start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kcap daemon status                  # list all running daemons
kcap daemon status --name laptop    # show status of a specific daemon (incl. its running version and any resuming/interrupted agents)
kcap daemon stop --name laptop      # stop just that one, ending its hosted agents
kcap daemon stop --name laptop --keep-agents   # stop it but keep its agents for the next start to resume
kcap daemon stop --name laptop --direct        # Windows only: ask the daemon itself even when a service manages it (the Windows service's own graceful stop; otherwise use `daemon service stop`)
kcap daemon stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kcap daemon restart --name laptop              # restart now, stopping its agents and resuming them on the new daemon; refuses while an eval, a launch or a flow round is in progress
kcap daemon restart --name laptop --when-idle  # queue the restart until the daemon hosts no agents at all and no eval or launch is in progress
kcap daemon restart --name laptop --force      # restart without waiting for a flow round (a participant mid-round is parked or ended); an eval or a launch still holds it
kcap daemon doctor                  # diagnose lock-file state for every daemon name, and audit the path to the unit directory (on macOS/Linux: group-writable directories, and directories owned by an account other than you or root)
kcap daemon doctor --clean          # also remove a stale entry's pid/marker files, dropping it from the list (held entries are never touched; the inert lock file is left in place)
```

`agent` was this group's name before it was renamed to `daemon`, and it now belongs to a different group — [`kcap agent`](#local-agents-kcap-agent) runs coding agents. The two still share `start` and `stop`, so check which one you mean: `kcap daemon start` starts the daemon, `kcap agent start <vendor>` starts a coding agent. Daemon-only verbs typed against the wrong group (`kcap agent status`, `restart`, `logs`, `doctor`, `service`) answer with a pointer back here.

`KCAP_DAEMON_NAME` overrides the active profile's daemon name (superseded by an explicit `--name` flag).

For a running daemon, `kcap daemon status` reports the **version the daemon process is actually running** (read from a marker the daemon writes at startup, not the CLI's own version) — so you can confirm whether a self-update has taken effect:

```
Daemon 'laptop': running (PID 12345)
  version: 0.8.12
```

A daemon whose process is alive but whose local control socket does not yet answer — it has taken its lock but has not finished binding and connecting — is reported as still starting, so you can tell a fully-up daemon from one that is only part-way through startup:

```
Daemon 'laptop': running (PID 12345, starting — not yet serving)
```

**Updating:** after `kcap update`, a running daemon on macOS/Linux detects the new binary and restarts itself, stopping its hosted agents and **resuming them on the new version** under the same ids where the vendor session can be resumed; where it cannot, or the resume fails, kcap continue brings the agent back as a fresh session under a new id, which `kcap agent ls` shows. It waits until no eval, launch or flow round is in progress and every interactive agent is waiting on input — the last for at most `KCAP_RESTART_TURN_WAIT_SECONDS` (default 300). Service-managed daemons exit so the supervisor relaunches the new binary; background (`-d`) daemons re-spawn themselves. `kcap daemon status` shows the running version (above) plus any pending restart, so you can tell the update apart from an as-yet-unrestarted daemon; `kcap daemon restart` applies it now. On **Windows**, stop the daemon (`kcap daemon stop` / `kcap daemon service stop`) before `kcap update` — a running daemon locks its binary, so the update can't replace it (the launcher detects this and aborts with instructions).

**Installing a coding agent later:** the daemon decides at startup which agents it can host, and checks its own `PATH` for the CLI of each one it is missing. When one appears, it restarts itself once no agents are running, so the new agent becomes available without a manual restart. A service unit's `PATH` is the one captured when it was installed, so a CLI installed into a directory that `PATH` did not include stays invisible. Re-running `kcap setup` (or the app's setup) fixes that: when it finds an agent CLI that the service's `PATH` misses, it gives the unit its current `PATH`, keeping the unit's own directories, and reloads the daemon (launchd), or has it restart once idle (systemd). Windows services are not rewritten; re-run `kcap daemon service install --replace --verify` there.

#### Run it as a service (auto-restart)

`kcap daemon start -d` runs only until the process dies — a crash, or an OS memory-pressure kill (macOS **jetsam** / Linux **OOM killer**) that sends an uncatchable `SIGKILL`. To have the daemon auto-restart and start at login, install it as a **per-user** OS service:

```bash
kcap daemon service install                # launchd (macOS) / systemd --user (Linux) / kcap-service or Scheduled Task (Windows); on macOS/Linux, refuses a unit path through a world-writable directory or one owned by an account other than you or root
kcap daemon service install --system       # Windows: install or update the kcap-service Windows service (one administrator prompt), then hand the daemon to it
kcap daemon service install --name laptop  # a unit per daemon name; kcap-service runs one daemon per user, so there it replaces your enrollment
kcap daemon service install --verify       # install, then verify version/readiness/ownership before exiting 0
kcap daemon service install --replace --verify  # take over a foreign/loaded unit, then verify
kcap daemon service install --replace --verify --retire OLD   # also remove the unit OLD, e.g. after a rename
kcap daemon service status                 # installed / running state
kcap daemon service status --json          # machine-readable status (pids, binary paths, txn-marker state)
kcap daemon service stop                   # stop the running service (stays installed), ending its hosted agents
kcap daemon service stop --keep-agents     # stop it, keeping its agents for the next start to resume
kcap daemon service start                  # start it again
kcap daemon service start --verify         # start, then verify readiness/ownership before exiting 0
kcap daemon service ensure                 # install-or-start from a fresh status read (flow-driven)
kcap daemon service refresh                # bring installed units up to this version and onto a script install's `current` path; a reload stops the daemon with its agents kept and the reloaded daemon resumes them, and waits for the next update while an eval or a launch is in progress (runs after `kcap update`)
kcap daemon service refresh --name N --force   # reload daemon N now, even mid-eval; prints refresh_outcome=<token> on stderr (launchd only; elsewhere `unsupported`, exit 1)
kcap daemon service uninstall              # stop and remove the service, ending its hosted agents
kcap daemon service uninstall --under DIR  # stop and remove every unit whose daemon binary is inside DIR, ending their hosted agents
```

On **Linux**, to run the daemon as a service requires systemd. Without systemd, as in most containers and sandboxes, every `kcap daemon service` verb exits 1 saying so, `kcap daemon status` and `stop` skip the service, and setup tells you to run `kcap daemon start -d`. Nothing starts that daemon again after a restart: run it again, and it resumes the agents it was hosting that were alive within `KCAP_RESUME_MAX_AGE_SECONDS` (default 24 hours). Uninstall leaves a unit installed while systemd ran, and keeps the config directory, until you delete the unit file it names.

`install` pins the active profile via `KCAP_PROFILE` and captures your current `PATH` into the unit, so the supervised daemon resolves the same server URL, `claude`/`codex` binaries, and profile settings it would from your shell. Pass `--profile P` to pin a different profile, `--max-agents N` to bake an override (`0` = unlimited), or `--no-start` to register without starting (`--no-start` cannot be combined with `--verify`, whose whole job is to prove the *started* daemon is ready). The service restarts the daemon on crash/`SIGKILL` but **not** on a clean stop. Over an installed service, a plain `install` stops it and installs it again. On macOS it rewrites the plist only once launchd has unloaded the old job, waiting up to 15 seconds; on Linux and Windows a refused registration puts the previous unit file back. A failed install says whether the service is still loaded or now stopped, which daemon its unit names, and the command that reinstalls it: `install --replace --verify`. `stop` unloads it from the OS supervisor (launchd `bootout` / equivalent; the unit file is retained) rather than merely signaling the process.

#### Windows service

On a machine with the desktop app, the daemon can run under `kcap-service`, a Windows service that starts with Windows and starts each enrolled user's daemon when they sign in, as that user, from their own app install. `kcap daemon service install --system` (or the app's own offer) installs it behind one administrator prompt: the elevated step copies only `kcap-service.exe` into `%ProgramFiles%\kcap\service\`, which only administrators can write, registers the service to restart on failure, and starts it. Once it is installed, `kcap daemon service install` enrolls your daemon with it instead of writing a Scheduled Task, retiring a task the same name already had, and `start`, `stop`, `status` and `uninstall` act on your daemon through the service; another user's daemon is never visible to you. The service restarts a daemon that crashes after five seconds and leaves one that exits 0 stopped; stopping or updating the service itself leaves the daemons running, and it picks them up again when it starts. The daemon keeps updating with the app, without a prompt; only a new `kcap-service.exe` asks again, and `service status --json` reports it as `service_upgrade_available`. Uninstalling the app hands your daemon back, and the service removes itself once no user on the machine is enrolled; `kcap daemon service uninstall --system` removes it on demand. It does not run a daemon before you sign in: the daemon runs as you, and Windows only lends a service your identity while you are signed in. Until `kcap-service` is installed on the machine, every install uses the Scheduled Task described next, whether it came from npm, the script or the app with its prompt declined.

On **Windows** the service is a per-user Scheduled Task scoped to your account, so installing it needs no administrator rights. It runs the daemon in a windowless console (`conhost --headless`) at sign-in, and the task's wrapper restarts the daemon five seconds after any non-zero exit — a crash, or the daemon's own `kcap daemon restart` — the way systemd's `Restart=on-failure` does; an exit 0 is a deliberate stop. `service stop` and `service uninstall` end the task and then the daemon itself, since ending a task leaves its child processes running. Uninstalling the desktop app runs `service uninstall --under` for the app's own folder and takes that folder back off your user PATH, so a task that would otherwise retry the deleted daemon at every sign-in goes with it; a service an npm or standalone install wrote is left alone. The wrapper also carries the installing shell's `HOME` when it is an absolute path, so a Git Bash or redirected home resolves the same daemons directory for the daemon as for the CLI.

`status --json` prints a machine-readable snapshot (service/job/daemon pids, binary paths, and transaction-marker state) instead of the human summary, and exits non-zero if the underlying service state can't be determined — for scripts that need to decide whether to attach, start, or repair a service without parsing human-readable text.

A transaction marker is what an interrupted `install`, `replace` or `start` leaves behind. Only the verified launchd transaction writes one, so this is macOS only. When the service is running, its job is the validated daemon, and its unit is the one the marker recorded writing, `status`, `status --json` and `ensure` treat the interrupted operation as finished and delete the marker. Otherwise `status` names the operation, the phase it stopped at, and the repair command, `install --replace --verify --name N`, and `status --json` carries the same details as `txn_operation`, `txn_phase` and `txn_marked_at`. The desktop app shows that marker as a banner with a Repair button that runs the same reinstall.

`status --json` also carries `loaded_spawn_type`, the word launchd reports for the loaded job (`daemon` for Standard, `adaptive` or `background` for the throttled band). `kcap daemon status` prints a `priority:` line when the job runs in that band, because every hosted agent inherits it and terminals lag under load; `kcap daemon service refresh --name N --force` reloads the job to Standard. The forced run exits 0 for `reloaded` and `current`, 1 for every other token (`not_loaded`, `deferred`, `contended`, `unverified`, `unit_missing`, `unit_unreadable`, `unit_unsupported`, `failed`).

`ensure` is the flow-driven ladder: from a fresh status read it installs when there is no unit (baking the born-`prompt` consent directive — the daemon is installed `prompt`, so nothing runs unattended on someone else's say-so) or starts when the unit is present but stopped, and reports "already enabled" when the daemon is running — on launchd that additionally requires the running service job to own the validated daemon pid, the same ownership the verified start polls for. On macOS/launchd both arms run the verified transaction exactly as an app-managed start does, so a gate refusal exits with the coded verify exit plus one `start_gate_reason=<token>` line, and `ensure` maps the token to a machine-readable `recovery_surface=takeover|reinstall|attention` (never guessed from prose) — the flow can then offer the right next step. A gated-install viability abort with the engine's coded `package_inconsistent` reason routes to `recovery_surface=reinstall`, and an attributed boot refusal (e.g. `consent_seed_unwritable`) routes through its own table (`storage`/`takeover`/`attention`); refused launchd transactions serialize `"verified":true`, since the verified transaction did run. On Windows/Linux the ladder degrades to plain install/start (no gates, no rollback), and `ensure --json` reports `"verified":false` so the flow's copy can say so. On macOS a resolvable profile is required: a unit baked without one could never pass the start gate's identity half, so `ensure` refuses with `no_profile_configured` rather than report a dead-end install. `ensure` never mutates into an ambiguous state: an unreadable probe, an active transaction, an orphaned label (including a running one whose unit file has disappeared) or a stale marker all fail closed to attention with a coded reason — the ambiguity checks precede even the "already enabled" arm.

`start --verify` polls the started service until it answers a well-formed local-socket hello **and** the OS-reported job pid matches the daemon's own validated pid, rolling back (stopping the service again, service definition retained) and exiting non-zero with a coded stderr token (e.g. `verify_readiness_timeout`) if that never happens within the poll budget — useful for scripted installs that need to know the daemon is actually up before proceeding. On Windows the job pid is the daemon the task's own wrapper started, on Linux the unit's `MainPID`; a `--retire` reads the retired unit's profile from its wrapper or unit file.

`install --verify` (fresh installs only — a service that's already installed exits with the coded `verify_contended`, since clearing an existing label is `--replace`'s job) additionally requires the started daemon's reported version, protocol version, and reported name to match the installing CLI's own expectations, and rechecks the unit file on disk against a fingerprint taken at write time — so a foreign writer replacing the file between install and the recheck is detected (`verify_restore_verification`) rather than silently accepted. On any failure it rolls back by uninstalling the unit it just wrote (never a foreign one) and exits with a coded stderr token. On Linux and Windows there is no unit fingerprint recheck, since the registration is replaced outright rather than loaded from a file the supervisor may later re-read.

`install --replace --verify` (requires `--verify`) takes over an existing label/unit instead of refusing on contention: it clears a foreign or already-loaded label, stops a validated live owner if one is running, then installs and verifies as above — one transaction, rolling back to a verified-safe absent state on any failure rather than leaving a half-replaced unit.

`install --replace --verify --retire <id>` additionally removes the unit `<id>` inside the same transaction — the unit a daemon rename leaves behind. The retired unit must be pinned to the same profile as the one being installed (`KCAP_PROFILE` in its plist), or the command refuses with `verify_retire_refused` and touches nothing; an absent unit is a no-op. With `--retire`, a live daemon already running under the *new* name is `verify_contended` rather than taken over. A unit already installed under the new name, even a stopped one, is refused before the old unit is touched (`retire_reason=target_occupied`), as is a new name whose state cannot be read (`target_unknown`); if a unit appears under the new name while the old one is being retired, it is refused rather than cleared (`target_claimed`), and the old unit stays retired. When the old unit's daemon is running, the command first pauses its new work through the daemon's control socket and, once that pause is acknowledged, asks it to end its hosted agents and exit before booting it out: a daemon still running or starting an agent or evaluation refuses with `agents_active`, one too old to pause with `fence_unsupported` (restart it, then retry), and one that cannot be reached or confirmed — or a loaded label with no plist — with `fence_unavailable` or `unit_unreadable`. Each of these refusals happens before either unit is changed.

**App-managed starts are gated; plain terminal use is not.** When the invoking launcher carries the `KCAP_CONSENT_SEED_DEFAULT` environment directive — the desktop supervisor or a self-respawn, never a bare `kcap daemon service start --verify`/`kcap daemon start -d` typed at a terminal — `start --verify` pre-mutation-checks the installed unit's baked directive, binary digest, and identity evidence before touching anything, and exits `28` (`verify_start_gate`) with one `start_gate_reason=<token>` line naming which check failed: `directive_missing`, `directive_invalid`, `identity_mismatch`, `foreign_binary`, `package_inconsistent`, or `evidence_unreadable`. A gate that passes is re-checked immediately before bootstrap; if the evidence changed in between (a foreign writer, a swapped binary), it rolls back the same way a forward-phase failure does and exits `29` (`verify_start_gate_drift`) instead of proceeding on stale authorization. No directive means no gate — this only ever fires for an app-managed start.

A detached (`-d`) start under the same directive separately checks the daemon binary against this CLI build's embedded digest before spawning anything, exiting `43` with `daemon_start_reason=package_inconsistent` on a mismatch. And when a gated boot refuses to come up, a readiness timeout that can attribute the refusal to a specific cause reports `refusal_reason=<token>` on stderr alongside the timeout exit — evidence for the app-side caller, not a different exit code.

What it carries over from your shell is a fixed allowlist — `PATH`, `KCAP_PROFILE`, `KCAP_URL`, `KCAP_CLAUDE_PATH`, `KCAP_CODEX_PATH`, `KCAP_COPILOT_TOKEN_CMD`, plus the Google/Gemini configuration below — and **nothing else from your environment reaches the service**, credentials included, because the unit file lands on disk. (`install` additionally writes two keys that did not come from your shell: a generated `KCAP_DAEMON_SUPERVISED` marker, and `KCAP_CONFIG_DIR` set to the configuration directory the install resolved — so a supervised daemon uses the same directory you installed from rather than re-deriving one from the supervisor's `HOME`.) Unit files are written owner-only (`0600`). `KCAP_COPILOT_TOKEN_CMD` is on the list precisely because it is a *command* rather than a secret — see [borrowed-context Copilot review](#borrowed-context-copilot-reviews).

#### The command-line tool (`kcap` on your terminal PATH)

If your login shell cannot find `kcap` — hooks then run and record nothing, silently — the Agents screen offers to fix it for you. The fix is a CLI verb, driven by name from the flow (the server→CLI lane carries values, never paths or commands):

```bash
kcap daemon shim ensure                    # probe; put this CLI on the terminal PATH if absent
kcap daemon shim ensure --json             # machine-readable outcome for the flow
```

`ensure` probes the PATH a new terminal would get — on macOS and Linux the *interactive login* shell (`$SHELL -lic`, falling back to `-lc`; `/bin/zsh` when `$SHELL` is unset), on Windows the machine and user PATH as the registry holds them — and acts on a positive finding only: kcap already resolving → `already_on_path`, exit 0. Positively absent → on macOS, link `kcap` into the first stable login-PATH folder you can already write to, or else `/usr/local/bin/kcap` behind a one-time admin password prompt (non-forcing symlink). A standard user, who has no admin password to give, gets `~/.local/bin/kcap` instead, and that folder is appended to PATH in the login shell's profile (`~/.zprofile` for zsh; for bash the first of `~/.bash_profile`, `~/.bash_login`, `~/.profile` that exists) between `# >>> kcap PATH >>>` markers. The app shows that line before it is written, a re-run does not repeat it, and nothing else in the profile changes. For unsupported shells or home paths that cannot safely be written to a profile, first create `~/.local/bin` and link `kcap` in that folder to your installed CLI, then add `~/.local/bin` to your shell’s PATH manually; on Linux, the same first stable writable folder, or else `~/.local/bin/kcap` with no prompt; on Windows, add the CLI's folder to your user PATH (no administrator rights). Either way it then **re-probes**, so success is never reported on the change alone. For an npm-global install the target is the npm **launcher** rather than the native binary — `kcap.js` on macOS, the prefix's `kcap.cmd` on Windows — so `kcap update` keeps performing the upgrade through npm; a standalone binary is used directly. An unknown probe (`probe_unknown`), a different filesystem entry already at the destination (`conflict` — never overwritten; re-checked after a failed install so a racing entry still gets the coded row), or a platform with no installer (`unsupported_platform`) all fail closed with the coded reason, exit non-zero, and mutate nothing. `--json` emits the outcome plus coded reason (and the actionable `detail`/`sudo_fallback` on `installed_not_on_path`/`failed`); the flow's copy keys off `outcome`, not the exit code. Exit 0 only when the terminal now resolves `kcap`.

#### Hosted Gemini needs its project in the *daemon's* environment

Hosted Gemini agents use whatever credential you logged `gemini` in with — there is no API key to configure. But a Gemini login that is scoped to a Google Cloud project needs that project **where the daemon can see it**, and a supervised daemon sees none of your shell: launchd passes no shell environment, and a non-interactive shell never reads your profile. So `export GOOGLE_CLOUD_PROJECT=…` in `.zshrc` is invisible to it.

Get this wrong and Gemini reports it as a **tier** problem:

```
IneligibleTierError: This client is no longer supported for Gemini Code Assist for individuals.
```

That message names the wrong cause — the same text appears for a missing project id — so if you see it, check your project configuration before believing anything about your subscription.

`install` therefore captures, when set in the installing shell:

| Carried everywhere | Carried on macOS/Linux only | Never carried |
|---|---|---|
| `GOOGLE_CLOUD_PROJECT`, `GOOGLE_CLOUD_PROJECT_ID`, `GOOGLE_CLOUD_LOCATION`, `GOOGLE_GENAI_USE_VERTEXAI`, `GOOGLE_GENAI_USE_GCA` | `GOOGLE_APPLICATION_CREDENTIALS`, `GOOGLE_GEMINI_BASE_URL`, `GOOGLE_VERTEX_BASE_URL` | `GOOGLE_API_KEY`, `GOOGLE_CREDENTIALS` |

The middle column is secret-*capable* — a credential path says where your credential lives, and a base URL can carry a token in userinfo or a query string. On macOS and Linux that is bounded by a guarantee kcap enforces: unit files are written `0600`, the mode is re-checked on the open handle, and the unit directory is created `0700`. A unit directory that already grants group or world write has those bits removed; `install` refuses only when they cannot be removed, which means the directory belongs to another account.

That guarantee covers the unit and the directory holding it. It does **not** extend to the directories above them, which kcap does not own: write permission on a directory is permission to rename what is inside it, so an account that can write `~/.config` can replace the unit directory whole, whatever mode the unit carries. `install` refuses when something on that path is *world*-writable — no umask produces that and it is never deliberate — but not when it is merely group-writable, because umask 002 against a user-private group produces exactly that for a group of one, and it is the default on Debian and Ubuntu. `kcap daemon doctor` lists the group-writable directories on the path so you can judge whether that group is shared. Both also refuse or report a directory on that path owned by another account other than root, since its owner can rename what it holds whatever its mode. Both walk the path with symlinks resolved. On Windows the wrapper inherits your user profile's ACL, which kcap neither sets nor verifies, so those three are excluded there — the same reason `GH_TOKEN` is never carried. If you need Vertex-with-ADC on a Windows daemon, set it in the service's own environment yourself.

⚠️ **Capture happens at install time.** Exporting the project *after* `kcap daemon service install` leaves a unit without it. Set it first, or re-run `install` afterwards — and restart the daemon.

> Verified against `oauth-personal` (Gemini Code Assist) with a project. The `gemini-api-key`, `vertex-ai` and `gateway` auth methods are not verified; their variables are carried so they *can* work, which is not the same as knowing they do.

Because the service auto-restarts, stop a service-managed daemon with `kcap daemon service stop` (or `uninstall`) rather than `kcap daemon stop` — a raw stop would be relaunched immediately. `kcap daemon status` and `kcap daemon doctor` both report installed services.

Each daemon process holds an exclusive `flock` on `~/.config/kcap/daemons/<name>.lock` for its entire lifetime. The kernel releases the lock automatically when the daemon exits (including `SIGKILL` or power-off), so leftover lock files on disk are never a blocker — only a live process holding the kernel-level lock can prevent another daemon from acquiring the same name.

Two daemons with **different** `--name` values can run side-by-side. Two daemons under the **same name** on the same machine collide on the flock and the second one exits with code 2. Even if that guard is bypassed somehow, the server rejects the second daemon's `DaemonConnect` with a typed error and the second daemon exits with code 3 — no more silent slot-displacement oscillation.

#### Launch consent (`kcap daemon consent`)

Every server-driven launch — a hosted coding agent, a PR review, or a review-flow participant started from the dashboard — is checked against a daemon-owned consent policy (allow / deny / prompt-the-owner, with per-requester/kind/repo/vendor rules) before it runs; `kcap daemon consent` inspects and edits that policy from the CLI. A denial surfaces back to the server (and the requester) as the coded reason `launch_denied_by_owner`.

```bash
kcap daemon consent show                                        # print default, prompt timeout, and numbered rules
kcap daemon consent set-default deny                             # allow (default) | deny | prompt
kcap daemon consent allow --requester user_123 --kind review     # append a rule (at least one flag required)
kcap daemon consent deny --vendor codex --repo '/path/to/repo/*'   # deny by vendor + a repo glob
kcap daemon consent remove 0                                     # remove the rule at the index `show` printed
kcap daemon consent log -n 50                                    # tail consent-decisions.jsonl (works while stopped)
```

`show`/`set-default`/`allow`/`deny`/`remove` mutate the policy over the daemon's local socket and require the target daemon to be running; `log` reads `consent-decisions.jsonl` straight off disk, so it works even with the daemon stopped. All six take `--name <n>` like the rest of `kcap daemon`.

> **`daemon consent` is the gate that authorises an individual launch**, and it defaults to *allow*. The
> per-vendor reviewer switches below are a different thing — whether this daemon may run a given vendor
> unattended **at all** — and they now default to *enabled* too. See the note directly below for why they
> used to be opt-in and no longer are.

#### Unattended reviewers are enabled by default (they used to be opt-in)

Every reviewer vendor your daemon can host is available to a review flow out of the box. Four of them
— Gemini, Kiro, OpenCode and Antigravity — used to require an explicit opt-in. That was removed, because
the opt-in did not do the job it appeared to do:

- **The reviewer vendor is chosen by the caller.** Claude, Codex, Cursor and Copilot have never been gated,
  and each runs with a *full* tool surface — shell and file writes included. So on any daemon that also
  advertises one of those, the gate did not widen the class of capability a requester could reach: one it
  blocked simply asked for an ungated vendor with more capability, not less. (Not quite "stopped nobody" —
  a Gemini reviewer burns *your* Gemini credentials and obeys Gemini's own permission model, which is not a
  subset of "Claude was available anyway".)
  > The exception, since the claim is not universal: on a daemon where you installed **only** a gated
  > vendor's CLI — for hosted work, say — and no ungated one, these variables were the only thing keeping
  > that binary from also serving unattended reviews. If that is your setup and you want it back, set the
  > variable to `0`. `kcap daemon consent` is the better control, because it cannot be sidestepped by
  > naming a different vendor.
- **It was attached to the wrong end of the risk scale.** Two of the four gated vendors (Kiro, OpenCode) run
  *read-only* reviewers. The strictest policy was on the most contained configuration.
- **It taxed the honest path.** A supervised daemon inherits nothing from your shell, so turning a reviewer
  on meant editing a service unit and restarting — to use a feature you had explicitly requested.

What remains is the part that was doing real work: a per-vendor **version floor**. Several of these
reviewers' containment depends on behaviour of the installed vendor build — and for OpenCode it is
environment-based, which fails *silently* if a future build stops honouring it. Your daemon records a
minimum version automatically at startup and refuses anything older. Use `kcap daemon reviewer affirm
--vendor <name>` to move that floor past a build you have found to be bad. It is remediation, not
permission, and it never blocks a first launch.

**To disable a vendor**, set its variable to `0` (or `false`/`no`/`off`) in the **daemon's** environment
(Gemini, Kiro, OpenCode, Antigravity, Pi, Grok, and Muse Code):

```bash
export KCAP_GEMINI_UNATTENDED_REVIEWER=0        # this DAEMON's environment — not a server setting
export KCAP_KIRO_UNATTENDED_REVIEWER=0
export KCAP_OPENCODE_UNATTENDED_REVIEWER=0
export KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0
export KCAP_PI_UNATTENDED_REVIEWER=0
export KCAP_GROK_UNATTENDED_REVIEWER=0
export KCAP_MUSE_UNATTENDED_REVIEWER=0
```

Unset means enabled. A value the daemon cannot read as true or false is treated as **disabled**, and
warned about at startup — because the only reason to set one of these at all is to turn a reviewer off,
so an unreadable value is a failed "off" rather than an ambiguous input. Surrounding quotes are tolerated
(`"0"` works), since a mis-quoted service-unit entry is the usual way that happens.

**On a service-installed daemon, set it before you install.** `kcap daemon service install` copies these
variables into the service unit — on every platform — but a supervised daemon inherits nothing from
your shell afterwards, so its environment is frozen at install time. Exporting an opt-out later has no
effect until you reinstall the service:

```bash
export KCAP_GEMINI_UNATTENDED_REVIEWER=0
kcap daemon service install        # re-run so the unit picks the value up
```

Be aware that disabling one vendor does not stop unattended review on that daemon: a requester can still
name an ungated vendor. If you want no unattended reviews at all, `kcap daemon consent` is the gate that
actually does that — note it **defaults to allow**, so it only helps once you have configured it.

#### What an unattended Gemini review grants

A **hosted** Gemini agent needs nothing beyond the project setting above. A Gemini **reviewer** is worth
understanding before you leave it on, because its posture is the broadest of the set:

**Read this before setting it.** An unattended reviewer runs in a daemon-owned worktree with this daemon's
own `HOME`, so repository content that steers the model into using its tools gets **code execution with your
daemon user's full authority**. Concretely, and not bounded to the review:

- **credentials** — the kcap token store and config directory are readable, and a copied token stays valid
  until it expires or is revoked; reaping the reviewer does not touch it;
- **integrity** — writes reach your *other* worktrees, the daemon's own state, the installed `kcap`, and your
  shell profiles, so one review can alter a later review or your own environment;
- **persistence** — a child process or scheduled job can outlive the review;
- **audit** — local session captures and logs are writable by the same authority, so after a compromise the
  local record is not evidence;
- **verdict** — steered content can simply ask the model to report `clean`. A review result is not
  authenticated review output.

This is a property of unattended review generally, not of Gemini specifically — Gemini widens which vendors
can do it rather than introducing it, and Claude, Codex, Cursor and Copilot have never been gated at all. The one path that does *not* grant this is a sandboxed borrowed
review, which Gemini cannot use yet.

**This is on by default, so read the above as describing what your daemon already does** once `gemini`
resolves. The switch is daemon-local — the person requesting a review is not necessarily you — and it is
now an opt-OUT: `KCAP_GEMINI_UNATTENDED_REVIEWER=0` to turn it off. If your daemon is service-managed, set
it and re-run `kcap daemon service install`, since a supervised daemon's environment is frozen at install.

Two further things it does *not* do:

- it does not bypass the **minimum version**. The reviewer's only containment is Gemini's exact-name MCP
  allowlist, which is a behaviour of the installed build, so the daemon records a minimum `gemini` version
  on the first startup that finds the binary. That recorded version is a **minimum, not an exact match**: any
  build at or above it runs, so **a Gemini upgrade needs no action from you**; an older one is refused, with
  a coded error naming both versions. Run `kcap daemon reviewer affirm --vendor gemini` to move the minimum
  to whatever is installed now — which is how you exclude a build you have found to be broken, and, if you
  run it while an older build is installed, how you deliberately lower the bar again.

  (Two earlier models are worth knowing about if you hit old docs. A maintainer-curated *certified version
  set* took the reviewer offline on every Gemini release until a new kcap shipped — a build one patch ahead
  of the certified one made the feature unreachable. Requiring an exact match to a version you affirmed
  removed the kcap-release coupling but kept the treadmill, just moved onto you. The minimum keeps the
  fail-closed direction for downgrades while letting routine upgrades through untouched.)
- it does not make Gemini a default reviewer. It is only ever reached by an explicit `vendor: "gemini"`.

#### Unattended Kiro reviews

Enabled by default; `KCAP_KIRO_UNATTENDED_REVIEWER=0` in the daemon's environment disables it.

**Everything in the Gemini section above applies**, with one difference in each direction.

*Tighter:* a Kiro reviewer runs with a **scoped** tool set — `fs_read`, `thinking`, and the tools of the MCP
servers the launch itself injects. `fs_write` and `execute_bash` are not trusted, and a permission request that
does not name exactly one of the injected tools ends the round rather than being auto-approved. Gemini's `yolo` approval mode
excludes nothing, so on tool surface Kiro is the narrower of the two.

> Kiro intermittently raises a permission prompt for a tool that *is* in its own trust list (an upstream
> trust-flag leak). The reviewer therefore approves prompts naming the tools this launch injected, and reaps
> on anything else — rather than reaping on every prompt, which would kill an unpredictable share of clean
> rounds on the call that delivers the result.

*Not tighter:* a trusted `fs_read` is **not path-scoped** — measured. It reads anything the daemon user can,
so the credential, integrity-of-*reads*, and verdict bullets above hold in full. Support is therefore limited
to a daemon whose operator and whose review requesters are in **one trust domain**.

A review launch also runs with a daemon-owned, empty `KIRO_HOME`, so your global
`~/.kiro/settings/mcp.json` servers — `kcap-flows` among them — do not reach the reviewer. Your own
interactive Kiro sessions are unaffected, and the file is never modified.

**Minimum version.** That suppression depends on the installed build honouring `KIRO_HOME`, so the daemon
records a minimum `kiro-cli` version on the first startup that finds the binary. It is a **minimum, not an exact
match**: any build at or above it runs, so **a `kiro-cli` upgrade needs no action from you**; a build older
than the recorded minimum is refused.

```bash
kcap daemon reviewer affirm --vendor kiro
```

Run that to move the minimum to whatever is installed now — which is how you exclude a build you have found
to be broken, and, if you run it while an older build is installed, how you deliberately lower the bar
again. The command records the version and nothing else — it does not enable the reviewer. No kcap release
is ever needed; Gemini and Antigravity use the same model and the same command (`--vendor gemini`,
`--vendor antigravity`).

POSIX only: the isolated home holds the reviewer's own transcript, and therefore the review context, and
cannot be created owner-only on Windows.

#### Unattended Antigravity reviews

Enabled by default; `KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0` in the daemon's environment disables it.

The prerequisite is the Antigravity **CLI** — the `agy` binary on `PATH` (or `KCAP_ANTIGRAVITY_PATH`
pointing at it). The Antigravity **IDE** alone is not enough: it ships no `agy`, and a daemon with only the
IDE installed never offers the vendor at all.

**Everything in the Gemini warning above applies.** Reviews run in a daemon-owned worktree under a
per-launch, owner-only `HOME` (removed when the reviewer is disposed), so your own `~/.gemini` config and
the kcap capture plugin inside it do not reach the reviewer, and your interactive Antigravity sessions are
unaffected. POSIX only, for the same reason as Kiro: that home holds the reviewer's own transcript, and
therefore the review context, and cannot be created owner-only on Windows.

That home also carries a small `settings.json` holding `permissions.allow` rules for exactly the MCP tools
the launch injected — the `kcap-flow-result` submit channel, plus any read-only servers the flow definition
allowlisted — named one `server/tool` pair at a time, never a wildcard. `agy -p` has no human to approve a
tool confirmation and auto-denies every one it raises, so without those rules a reviewer reads its context,
reasons, and can never deliver its result. The grant admits those named tools and nothing else;
`--dangerously-skip-permissions`, which *is* the read boundary, stays off this arm.

**Give the daemon durable credentials.** An unattended reviewer's stdin is closed, so it cannot complete an
interactive login — an unauthenticated `agy` fails the launch with a coded
`antigravity_reviewer_auth_unavailable` rather than hanging. Application Default Credentials are the
supported setup, and every CLI-driven daemon start completes them for you — `daemon start`, `-d`, and
`service install` alike:

```bash
gcloud auth application-default login
kcap daemon start -d --name "$(whoami)"          # or: kcap daemon service install --name "$(whoami)"
```

The daemon needs all three of `GOOGLE_CLOUD_PROJECT`, `AGY_ADC_AUTH=1` and
`GOOGLE_APPLICATION_CREDENTIALS`. The CLI captures any you exported and silently derives the rest:
the credential path from ADC's well-known location (only when the file is actually there), `AGY_ADC_AUTH=1`
alongside it, and the project from your environment or gcloud's own active configuration. Exported values
always win. Only the canonical `GOOGLE_CLOUD_PROJECT` counts here — `GOOGLE_CLOUD_PROJECT_ID` is carried
for Gemini, which honours both, and does not satisfy agy's project leg. The explicit path looks redundant — ADC has a well-known default location — but a reviewer
launch redirects `HOME` to a per-launch state directory, so the default location is not visible to the
child. Derivation happens in the CLI, at your command; the daemon itself still never reads a credential
location of its own accord — it only forwards what it was started with.

A daemon already running from before you logged in needs a fresh start to pick the trio up — `kcap daemon
stop` then `kcap daemon start`/`-d` (or re-run `service install`). `kcap daemon restart` reuses the running
process's own environment rather than re-deriving, so it will not.

Deliberately unit-only: do **not** export `AGY_ADC_AUTH=1` in your interactive shell — under ADC auth agy
fires no hooks, so that export would stop kcap capturing your own interactive Antigravity sessions. The
unit's copy reaches only daemon-spawned children.

**Minimum version.** Containment here depends on the installed build honouring `HOME` and reading no other
global config source, so the daemon records a minimum `agy` version at the first startup that finds `agy`
installed — for this vendor that is not conditional on the flag above, because the same minimum also gates
[hosted Antigravity agents](#hosted-antigravity-agents-run-without-permission-prompts).
It is a **minimum, not an exact match**: any build at or above it runs, so **an `agy` upgrade needs no
action from you** — which matters more here than for the other reviewers, since `agy` updates itself
(observed going 1.1.8 → 1.1.10 mid-session). A build older than the recorded minimum, or one whose
`agy --version` cannot be read, is withheld with a coded reason naming both versions.

This vendor's minimum is recorded whenever `agy` first resolves, even on a daemon whose reviewer you have
explicitly turned off — its floor also gates *hosted* Antigravity agents, which are never gated by the
reviewer switch. That leaves one window worth knowing about, now that reviewers are on by default it needs
a deliberate opt-out to reach: install `agy`, run a daemon with `KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0`,
then upgrade `agy` and only afterwards unset that variable — and the recorded minimum is still the older
build you started with, so a later *downgrade* back to it would be admitted. Run the command below once
after re-enabling if you want the minimum to be the build you actually reviewed with.

```bash
kcap daemon reviewer affirm --vendor antigravity
```

Same command and same model as Kiro and Gemini: run it to move the minimum to whatever is installed now —
which is how you exclude a build you have found to be broken, and, if you run it while an older build is
installed, how you deliberately lower the bar again. It records the version and nothing else; it does not
enable the reviewer. No kcap release is ever needed.

Like Gemini and Kiro, this never makes Antigravity a default reviewer — it is only ever reached by an
explicit `vendor: "antigravity"`. Borrowed (in-place) review is not offered; a borrowed request falls back
to a daemon-owned worktree. **PR review (`kcap review <pr>` / the dashboard's Review PR action) is not
supported on this vendor either** — that agent needs the `kcap mcp review` tool surface, which only the
PTY-backed vendors are given, so an Antigravity PR-review launch is refused with
`antigravity_pr_review_unsupported` rather than started without its review tools. Use Claude for a PR
review.

#### Unattended Pi reviews

Enabled by default; `KCAP_PI_UNATTENDED_REVIEWER=0` in the daemon's environment disables it.

A Pi reviewer runs in a daemon-owned worktree with none of your interactive Pi configuration reaching
it — no extensions, no skills, no prompt templates, and no `AGENTS.md`/`SYSTEM.md`; an explicit
system prompt replaces Pi's own. It can inspect the worktree with `read_file`, `list_directory` and
`search_files`, read the repository's history with `git_log`, `git_show` and `git_diff`, and report a
verdict through the injected result channel; it has no shell and cannot
write anywhere, in or out of the worktree — a path outside the worktree is refused by the tool
itself, not merely left untrusted. It runs offline, so a repository cannot trigger a package install
either. The git tools run only git's own diff machinery, never an external diff or textconv
program, and ignore system and global git config. A review of your working tree borrows it as a
daemon-owned copy — tracked, modified and untracked files, rebuilt before every round — so the
reviewer sees your uncommitted work but never reads or runs in the live checkout itself.

It authenticates as **you**: whatever Pi provider credentials and default model your account already
has, or `KCAP_PI_MODEL` to pick a specific one — the same override a hosted Pi agent uses.

**Minimum version.** That containment is a behaviour of the installed `pi` build, so the daemon
refuses a `pi` older than the oldest build its containment was measured on, and older than the build
you first enabled the reviewer with; any newer build is accepted with no action from you.

```bash
kcap daemon reviewer affirm --vendor pi
```

Same command and the same model as Gemini, Kiro and Antigravity: run it to move the recorded minimum
to whatever is installed now. It records the version and nothing else — it does not enable the
reviewer, and no kcap release is ever needed.

POSIX only: the per-launch directory holds the reviewer's own transcript and manifest and cannot be
created owner-only on Windows. Borrowed (in-place) review is not offered; a borrowed request falls
back to a daemon-owned worktree, same as Antigravity.

#### Unattended Muse reviews

Enabled by default; `KCAP_MUSE_UNATTENDED_REVIEWER=0` in the daemon's environment disables it.

A Muse reviewer runs read-only in a daemon-owned worktree or snapshot, with network restricted, and does not trust the workspace: the reviewed repository's Muse hooks, rules, skills and `.mcp.json` servers never load. Your own hooks in `~/.config/muse/settings.json` still run, which is how a reviewer is recorded when kcap's hooks are installed. Muse runs in its `onRequest` approval mode so that every approval reaches the daemon, which decides each one itself: it allows only the kcap tools it injected for the review, under per-launch names a repository cannot shadow, and rejects everything else. A review of your working tree borrows it as an independent snapshot, never your live checkout. It authenticates as **you**, with the daemon's own Muse credential (see [Hosted Muse Code agents](#hosted-muse-code-agents) — a service-installed daemon does not see a key exported in your shell), and `KCAP_MUSE_MODEL` picks its model.

**Minimum version.** The daemon refuses a `muse` older than 1.4.7, the oldest build the reviewer's posture was measured on, and older than the build you first enabled the reviewer with; any newer build is accepted.

```bash
kcap daemon reviewer affirm --vendor muse
```

Same command and model as Pi and Grok: it moves the recorded minimum to whatever is installed now, records nothing else, and does not enable the reviewer. POSIX only, and Linux needs bubblewrap with user namespaces; a host that cannot sandbox refuses with `muse_reviewer_sandbox_unavailable`. A Muse PR review (`kcap review <pr>`) is refused with `muse_pr_review_unsupported`.

#### Hosted Antigravity agents run without permission prompts

The same `agy` binary also backs **hosted** Antigravity agents launched from the dashboard, and there the
posture is the opposite of the reviewer's: the daemon passes `--dangerously-skip-permissions` on every
hosted launch, unconditionally. `agy` soft-denies shell and out-of-workspace operations in headless mode
while still exiting 0, so without it a hosted agent quietly fails to do the work you asked for. This is the
same posture hosted Claude agents already carry (`--permission-mode bypassPermissions`).

**Measured on `agy` 1.1.10, that flag is the read boundary — the worktree is not.** With it, the agent can
read absolute paths outside its worktree, so a daemon-owned worktree does not confine what a hosted agent
sees. A hosted agent runs on your own machine, under your own daemon, at your own request; if you would
rather it asked first, restrict the vendor with [`kcap daemon consent`](#launch-consent-kcap-daemon-consent).
**Review-flow reviewers never get the flag** — they read an owned worktree and nothing else, so the
soft-deny is the posture they want; the one thing they must still be able to call, their own result
channel, is admitted by name instead (above).

Hosted launches otherwise take the same route as the reviewer above — the per-launch isolated `HOME`
included, so your own `~/.gemini` config and the kcap capture plugin inside it never reach a hosted agent,
and its transcript is recorded once rather than twice.

**`KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER` is not required for a hosted launch, and setting it does not
enable one.** Hosted Antigravity is on as soon as `agy` resolves, like every other hosted vendor. That flag
consents specifically to an *unattended review*, whose risk is that it runs under your daemon's authority
and returns what it read to whoever asked for the review — someone who need not be you. A hosted agent has
no such gap: the server only ever launches on a daemon you own, so the person launching it is you.

What a hosted launch *does* share with the reviewer is the **minimum `agy` version** (and POSIX), because
the containment that protects is the isolated `HOME` both shapes rely on. Your daemon records that minimum
at startup whenever `agy` resolves, so there is normally nothing to do; if you installed `agy` after the
daemon started, restart it or run `kcap daemon reviewer affirm --vendor antigravity`.

#### If your daemon runs as a service

The reviewer switches above are read from the **daemon's own environment**, and a supervised daemon (`kcap
daemon service install` — launchd, systemd, or a Windows scheduled task) inherits nothing from the shell you
installed it from. Since the switches now default to *enabled*, this only matters when you want to **disable**
one: exporting it in your shell does nothing for a service-installed daemon until the unit itself carries it,
so export it *first* and then reinstall:

```bash
export KCAP_GEMINI_UNATTENDED_REVIEWER=0
kcap daemon service install --name "$(whoami)"    # captures the setting into the unit
```

The Antigravity ADC variables (`GOOGLE_CLOUD_PROJECT`, `AGY_ADC_AUTH`, `GOOGLE_APPLICATION_CREDENTIALS`)
are captured — and, where you exported nothing, derived from gcloud's own state — by the same install, so a
daemon installed *before* this shipped must be reinstalled to pick them up. The install prints an
`Antigravity:` line naming a complete capture, or the missing pieces of a partial one (a partial trio is
never a working hosted-agy configuration). `KCAP_ANTIGRAVITY_PATH` is **not** captured — like the other vendor path
overrides, set it where the unit can see it if you need a non-default value.

Install prints a `Consent:` line naming each reviewer variable it captured. That freeze is the point to
notice: the unit outlives the shell, so a reviewer you disabled stays disabled for that service until you
reinstall without the variable set — unsetting it in your shell later changes nothing.

If a reviewer is still not offered, the daemon says why in its own log at startup — one line per vendor that
is installed and unattended-capable but withheld, carrying the same text the launch path would have thrown
(consent not set, version unresolved, no minimum recorded, version below the minimum). It logs at `Information`, not
`Warning`: a daemon with Gemini, Kiro or the Antigravity CLI installed purely for interactive use and no
opt-in is in a perfectly normal state, so this is a line to find when you go looking, not an alert.

```bash
grep -i "is NOT offering it" ~/.config/kcap/daemon-*.log
```

Without that line the only symptom is the server's `reviewer_vendor_unavailable`, which reports that no
connected daemon advertises the vendor and cannot say why.

#### Hosted Codex agents

Hosted Codex agents require the Codex hook surface — if you said yes during `kcap setup`, you already have it. Otherwise install it manually:

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kcap reads it the same way it reads `KCAP_SESSION_ID` for Claude sessions — no manual session ID needed for any of the Codex skills (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`).

```bash
kcap plugin install --codex                          # user scope (~/.codex/hooks.json + ~/.agents/skills/)
kcap plugin install --codex --project                # project scope (<repo>/.codex/hooks.json), skills still user-wide
kcap plugin install --skills                         # skills only (~/.agents/skills/), no Codex hooks
kcap plugin install --skills --if-installed          # refresh only if skills were previously installed (used by npm postinstall, harmless to call by hand)
kcap plugin install --codex --if-installed           # refresh Codex hooks and add missing MCP servers, only if previously installed (used by npm postinstall)
kcap plugin install --if-installed                   # refresh Claude plugin registration only if previously installed (used by npm postinstall)
```

Codex rejects a `hooks.json` that starts with a UTF-8 byte-order mark and runs none of its hooks — an editor that saves "UTF-8 with BOM" (PowerShell 5.1's `Set-Content -Encoding utf8` among them) adds one. `kcap status` then shows `Codex ✗` with a line naming the file, and `kcap plugin install --codex` (or its `--if-installed` refresh) rewrites it without the mark.

Installing any vendor that reads the shared tree — `--codex`, `--cursor`, `--copilot`, `--gemini`, `--pi`, `--opencode` — writes these skills under `~/.agents/skills/` (`--skills` installs them alone; Kiro and Antigravity get their own copies, and the bare Claude install uses the plugin bundle). Opt out per vendor with `--skip-<vendor>-skills`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kcap-recap` | `kcap recap` | Session summary / continuation chain / repo history |
| `kcap-errors` | `kcap errors` | Tool-call error extraction |
| `kcap-hide` | `kcap hide` | Mark session owner-only |
| `kcap-disable` | `kcap disable` | Stop recording + delete server data |
| `kcap-validate-plan` | `kcap validate-plan` | Verify plan items were completed |
| `kcap-review-flows` | `kcap mcp flows` | Structured iterative spec/code review loops |
| `kcap-agent-flows` | `kcap mcp flows` | Drive any catalogue flow — named by you or offered from session context — following its driver guide |
| `kcap-work-items` | `kcap mcp workitems` | Declare a work item's breakdown and its blocks / blocked-by dependencies |
| `kcap-plans` | `kcap mcp plans` | Declare the plan document and task list a session executes |
| `kcap-guided-tour` | analytics + sessions + flows MCP | Card-by-card onboarding tour of what kcap does |
| `kcap-start-agents` | `kcap mcp flows` | Hand tasks to separate hosted agents that run on their own: check this machine's daemon, ask which harness, one `start_agent` call each |
| `kcap-pages` | `kcap mcp pages` | Make the artifact, page or report the user asked for as a kcap page: publish it, put the link in the reply, open it in the harness's browser tool when it has one |
| `kcap-connect-kcap` | `kcap setup` / `kcap login` | Install and sign in kcap for the agent asking; on a Muse Assistant VM also registers the kcap MCP servers as a custom connector, writes the `kcap` skill and an hourly `kcap status` cron |
| `kcap-worktrees` | `git` | Relative worktree links for a repository opened from a sandbox and its host: enable, undo, never prune, reinstate a pruned worktree |

The first five (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`) auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session. `kcap-review-flows` and `kcap-agent-flows` work differently — they operate via flow IDs through `kcap mcp flows`; only the two status tools fall back to the session (from `CODEX_THREAD_ID`, or an explicit `session_id`) when no flow ID is passed; see [Flows MCP server (for agents)](#flows-mcp-server-for-agents) for details. `kcap-start-agents` also goes through `kcap mcp flows`; its tools take the calling session from the harness, or from an explicit `session_id`. `kcap-work-items` declares structure through `kcap mcp workitems` and needs no session id for its breakdown and relation tools. `kcap-plans` writes through `kcap mcp plans` and defaults the session the same way `kcap-work-items` does. `kcap-guided-tour` shells out to `kcap whoami` and otherwise reads through the `kcap-analytics`, `kcap-sessions` and `kcap-flows` MCP servers, so it needs those registered (setup does it) rather than a session id. On a plan without Insights its team table falls back to the user's own session count.

> **Codex sandbox network access (AI-794).** The skills shell out to `kcap …`, which talks to the kcap server — but Codex runs the agent's shell tool in a `workspace-write` sandbox that **blocks network by default**, so the skills fail (or demand escalation) until network access is allowed. Both `kcap setup` (one yes/no prompt after the Codex hooks step) and `kcap plugin install --codex` enable it for you. They write a constrained allowlist to `~/.codex/config.toml` rather than opening the network wholesale:
>
> ```toml
> [sandbox_workspace_write]
> network_access = true          # required — the proxy only enforces; it doesn't grant
>
> [features.network_proxy]
> enabled = true
> domains = { "**.kcap.ai" = "allow" }
> ```
>
> The `**.kcap.ai` wildcard covers every SaaS tenant — current and future — plus `auth.kcap.ai`, so switching profiles and adding tenants just work with no per-tenant edits. **Self-hosted** servers are added as exact-host entries, derived from every configured profile's `server_url` and refreshed on each `kcap setup`. Existing config is respected: if you already run a `network_proxy` policy, kcap's hosts are merged into your `domains` (yours preserved); if you've already opened the network (`network_access = true` with no proxy), nothing changes. Opt out with `--skip-codex-network-access` on either command (the npm-postinstall `--if-installed` refresh never touches these keys; its only `config.toml` write is adding missing kcap MCP servers). A localhost dev server additionally needs `allow_local_binding = true`, which kcap does not set. Uninstall leaves these keys in place — they're your security posture, not kcap state.

**Sandbox and approval posture.** By default the daemon starts Codex with `--sandbox workspace-write` and `--ask-for-approval on-request`. This lets Codex edit files in the agent's worktree but escalates sensitive operations (e.g. network calls, shell commands outside the worktree) through the daemon's permission bridge to the dashboard.

The daemon also **accepts** a caller-selected posture for an **interactive** hosted launch (one that runs in a daemon-created worktree). Nothing selects one yet: the dashboard selector ships with a later kcap **server** release, and until then every launch uses the defaults above. The selectable values are:

| Flag | Selectable values |
|---|---|
| `--sandbox` | `read-only`, `workspace-write` (default), `danger-full-access` |
| `--ask-for-approval` | `untrusted`, `on-request` (default), `never` |

Two cases are **not** selectable, because their values are containment guarantees rather than preferences:

- A **borrowed** launch runs in your own real checkout, so it is always `read-only` — anything else would let a hosted agent modify the repo you are working in.
- A **review-flow reviewer** runs unattended, so it is always `--ask-for-approval never` — any prompting mode would wedge the round waiting for an approval no human is there to give.

PR-review launches also keep a fixed posture. A launch that supplies a posture for any of those three cases is **rejected** with a coded error (`codex_posture_not_overridable`) rather than having it silently ignored or silently applied; an unknown value, a half-specified pair, or the upstream-deprecated `on-failure` is rejected as `codex_posture_invalid`.

Two selections weaken the protections you get by default, in different ways:

- `--ask-for-approval never` **disables the permission bridge for that agent** — it never asks the dashboard before acting, so nothing pauses for your approval.
- `--sandbox danger-full-access` **removes the filesystem sandbox** — the agent can read and write outside its worktree. (Approvals are unaffected: with `on-request` it still escalates through the bridge.)

The daemon logs a warning naming the agent whenever an interactive launch resolves to either.

The daemon advertises posture support to the server at connect. A server that offers the selector refuses it against a daemon too old to advertise support, rather than sending a posture that would be quietly dropped — so update kcap on the daemon host if the dashboard says the selector is unavailable. This is all separate from local `kcap agent start codex -- …`, where you pass Codex flags yourself and they are not restricted (see [Local agents](#local-agents-kcap-agent)).

> **Upgrading from an earlier version of kcap?** Run `kcap update` (or `npm install -g @kurrent/kcap`) — the npm postinstall hook, and `kcap update` itself, refresh all user-scope kcap installations, so you always pick up the current CLI version's skills (`~/.agents/skills/kcap-*`), Codex hook commands (`~/.codex/hooks.json`), and Claude plugin registration (`~/.claude/settings.json`). Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kcap plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.
>
> npm 11+ blocks install scripts by default. Add `allow-scripts[]=@kurrent/kcap` to your `~/.npmrc` (or pass `--allow-scripts=@kurrent/kcap` on the install command line) to opt in to the auto-refresh; otherwise re-run the relevant `kcap plugin install ... --if-installed` commands manually after each upgrade. (`npm approve-scripts` does not work for global installs — that's a known npm UX bug.)
>
> **Restart your coding agent after upgrading, too — not just for hooks.** A harness reads each MCP server's tool schema **once**, when it connects, and caches it for the life of that session. Upgrading kcap replaces the binary on disk but cannot reach into a harness process that has already connected, so a session started before the upgrade keeps offering the **old** tool schema — missing any parameter the new version added. kcap deliberately does not try to hot-reload a cached schema; restarting is the supported recovery.
>
> This matters most for review flows. If `start_review_flow` is missing its `vendor` parameter, the agent driving it cannot name a reviewer, and resolution falls through to the flow definition's authored vendor (if it declares one) or the agent's saved `flows.reviewer_vendor` preference (if it doesn't) — so asking for "a review by Claude" can quietly get you a different reviewer. **The server cannot detect this**: an omitted `vendor` looks exactly like a caller who wanted whatever it resolves to, so the request carries no trace of the name you asked for. (The `client_upgrade_required`, `flow_client_protocol_required` and `flow_client_protocol_unsupported` rejections guard a different problem — *protocol* skew — and do not fire here.) The only guard in this window is the kcap flow skills, which instruct agents never to claim a named reviewer ran when they could not send `vendor`, and to tell you to restart instead.
>
> **Recovery:** upgrade kcap, then end the harness session and start a fresh one — or, in harnesses that expose it, reconnect the `kcap-flows` MCP server. To confirm the new schema is live, check that your client lists the server as connected (`claude mcp list`, `codex mcp get kcap-flows`, `gemini mcp list`, `cursor-agent mcp list`, `opencode mcp list`) and that `start_review_flow` now advertises `vendor`.

PR review is supported for hosted Codex agents as well as Claude — the same `kcap-review` MCP context is injected either way.

#### Cursor IDE hooks

Cursor is detected by the presence of `~/.cursor/` — you don't need the `cursor` shell command on `PATH`. If `kcap setup` found Cursor and you said yes, hooks are already in place. Installing also registers the seven kcap MCP servers in `~/.cursor/mcp.json` (non-destructive, idempotent); pass `--skip-cursor-mcp` to opt out. To install or remove later:

```bash
kcap plugin install --cursor                # writes ~/.cursor/hooks.json + agent skills + registers kcap MCP servers
kcap plugin install --cursor --skip-cursor-mcp  # hooks only, skip ~/.cursor/mcp.json
kcap plugin install --cursor --tools-only   # MCP servers + agent skills only, no hooks
kcap plugin remove --cursor                 # remove Cursor hooks + kcap MCP servers
```

**Live capture is watcher-backed (AI-1382).** Every Cursor hook (any of the 8, not just
`sessionStart`) spawns — or heals — a per-session `kcap watch --vendor cursor` background watcher
that tails `~/.cursor/projects/<sanitized>/agent-transcripts/<sid>/<sid>.jsonl` and streams new
lines the moment Cursor writes them, instead of relying solely on the next hook's HTTP backfill.
Hooks are kept as belt-and-braces: `subagent-start`/`subagent-stop`/`session-end` POSTs still
fire, a spooled hook is still retried on the next invocation, and a per-hook backfill still runs
as a fallback — the watcher just makes capture continuous instead of hook-triggered. A subagent
gets its own child watcher, tailing its own transcript file, spawned only once its diverted
`subagent-start` is acknowledged by the server (so no subagent transcript line can ever arrive
before its lifecycle is opened) — a spooled (unacknowledged) start defers the spawn until a later
hook's spool drain delivers it. If Cursor is force-quit mid-turn, the watcher's shutdown drain
still recovers the last (possibly newline-less but complete) transcript line before exiting; if a
session goes idle past `KCAP_CURSOR_IDLE_CEILING_MINUTES` (default `60`) the watcher exits WITHOUT
posting `session-end` itself — end-of-session synthesis for Cursor stays owned by the
`sessionEnd` hook or, as a backstop, a server-side lease-gated sweep — and the next hook for that
session (any of the 8, not just `sessionStart`) reactivates a fresh watcher. A runtime rewrite
guard defends against Cursor ever rewriting a transcript in place (expected append-only, but not
hook-guaranteed): a detected rewrite discards the unsent batch and quarantines the session rather
than risk re-sending stale byte ranges. `kcap cursor-verify-appendonly --path <file>` is a hidden
diagnostic (not listed in `--help`) that samples a live transcript file for a bounded duration and
reports whether it stayed append-only — the empirical evidence behind the watcher promotion, not
something you need to run day to day.

#### GitHub Copilot CLI hooks

Copilot CLI is detected via `~/.copilot/` (created on Copilot's first run) or the `copilot` binary on `PATH`. kcap writes its own hooks file — Copilot merges every `*.json` under `~/.copilot/hooks/`, so your other hook files are never touched. Copilot loads hook config at startup: restart any running `copilot` session after installing.

```bash
kcap plugin install --copilot               # writes ~/.copilot/hooks/kcap.json + agent skills
kcap plugin remove --copilot                # deletes ~/.copilot/hooks/kcap.json
```

Live sessions stream from `~/.copilot/session-state/<session-id>/events.jsonl` (`$COPILOT_HOME` is honoured); historical sessions import via `kcap import --copilot`, which also forwards Copilot's auto-generated session names as titles. Sessions resumed with `copilot --continue` / `--resume` reattach to the same recorded session.

#### Google Gemini CLI hooks

Gemini CLI is detected via `~/.gemini/` (created on Gemini's first run) or the `gemini` binary on `PATH`. Gemini keeps its hooks in the shared `~/.gemini/settings.json`, so kcap **merges** its entries into the `hooks` block and preserves your other settings and any hand-authored hook entries. Gemini loads hook config at startup: restart any running `gemini` session after installing.

```bash
kcap plugin install --gemini                # merges kcap hooks into ~/.gemini/settings.json, writes agent skills
kcap plugin remove --gemini                 # removes only kcap's entries
```

Live sessions stream from the chat-recording JSONL Gemini names in each hook's `transcript_path` (`~/.gemini/tmp/<project>/chats/session-*.jsonl`); historical sessions import via `kcap import --gemini`. Sessions resumed with `gemini --resume` reattach to the same recorded session. (Historical import leaves working-directory / repo enrichment empty — Gemini doesn't record the cwd in a machine-readable header; live capture gets it from the hook payload.)

Spawned subagents are captured too: Gemini records each in a nested `chats/<session>/<subId>.jsonl`. Live capture (a child watcher per subagent) discovers direct children only. Historical `kcap import --gemini` goes further, recursively discovering every transitive descendant — a subagent's own subagents, and so on, up to a depth of 8 — under each descendant's own nested dir, and imports them all as direct subagents of the top-level session (flattened, since a session's stream key can't express deeper nesting). A descendant beyond the depth cap is never silently dropped: import prints a `descendants_omitted=N` diagnostic to stderr, appending `(lower bound — counting ceiling hit)` in the rare case where counting the omitted subtree itself hit an internal safety cap before finishing — the count is then a lower bound, not exact.

#### AWS Kiro CLI hooks

AWS Kiro CLI (the rebranded Amazon Q Developer CLI) is detected via `~/.kiro/` or the `kiro` / `kiro-cli` binary on `PATH`. Kiro hooks fire only for the **active** agent — there is no global hook — so to capture every session transparently, `install --kiro` **clones your current default agent** into `~/.kiro/agents/kcap.json` (preserving its tools; a minimal agent would lose tool access), adds kcap's `agentSpawn` hook, and makes it your default agent (`chat.defaultAgent` in `~/.kiro/settings/cli.json`). This needs `kiro-cli` on `PATH` to perform the clone. Restart any running `kiro` session after installing. `remove --kiro` restores your previous default agent and deletes `kcap.json`.

`install --kiro` (and `kcap setup`) also **registers the seven kcap MCP servers** in Kiro's user-level `~/.kiro/settings/mcp.json` (a plain `mcpServers` merge, non-destructive — preserves your servers and their `disabled`/`autoApprove` fields; kcap leaves `autoApprove` unset). This is **independent of the agent clone**, so it still applies even if `kiro-cli` isn't present to clone the agent. Opt out with `--skip-kiro-mcp`; `remove --kiro` unregisters them.

It further installs the **kcap skills** into `~/.kiro/skills` (as `kcap-<name>/SKILL.md`). Kiro reads its skills from there — not the agent-agnostic `~/.agents/skills` — and the cloned agent's `resources` include `skill:///~/.kiro/skills/*/SKILL.md`, so the skills steer Kiro to prefer the kcap MCP tools for why/history/prior-work/review questions (registration alone doesn't make the model route to them). Opt out with `--skip-kiro-skills`; `remove --kiro` deletes the `kcap-*` skill folders.

```bash
kcap plugin install --kiro                  # clone default agent + add hook, set as default
kcap plugin install --kiro --tools-only     # MCP servers + skills only: no clone, default agent unchanged
kcap plugin remove --kiro                   # restore previous default, delete kcap.json
```

Kiro writes an append-only JSONL log per session at `~/.kiro/sessions/cli/{id}.jsonl` (plus a sibling `{id}.json` for cwd / model / title; honours `KIRO_HOME`), so the kcap watcher tails it like every other vendor. Lifecycle comes from Kiro's `agentSpawn` hook (fires every prompt → deduped server-side); since Kiro has **no session-end trigger**, the watcher synthesizes session-end on `kiro-cli` exit. Historical sessions import via `kcap import --kiro`. Kiro persists no token counts, so Kiro sessions show no token usage by design — but it does report a per-turn **context-fill %**, which the watcher now stamps onto each assistant message live (read from the sibling `{id}.json` at send time, best-effort). It's captured for a turn when that turn's metadata is present at send time — the common case, since the assistant message is a turn's last line. This mirrors the import path, which has always carried it; a session imported without ever being watched live gets it fully.

#### Pi extension

Pi (`badlogic/pi-mono`) is detected via `~/.pi/agent/` or the `pi` binary on `PATH`. Pi has **no shell hooks** and **no built-in MCP** — it exposes an in-process extension API — so `install --pi` writes dependency-free TypeScript extensions that `pi` auto-loads at startup:

- **`~/.pi/agent/extensions/kcap.ts`** — the live-ingest extension (shells out to `kcap hook --pi` on session start/end).
- **`~/.pi/agent/extensions/kcap-mcp.ts`** — the MCP bridge (opt out `--skip-pi-mcp`). At load it spawns each `kcap mcp <name>` server as a stdio subprocess, performs the MCP handshake, and registers every advertised tool as a native Pi tool `kcap_<server>_<tool>`, torn down on session exit.
- **`~/.pi/agent/AGENTS.md`** — a kcap-owned, marker-delimited steering block (opt out `--skip-pi-instructions`, non-destructive — your own instructions are preserved) nudging Pi to prefer the kcap tools.

`kcap` must be on `PATH` (both extensions shell out to it). Restart any running `pi` session after installing.

```bash
kcap plugin install --pi                    # write kcap.ts + kcap-mcp.ts + AGENTS.md block + agent skills
kcap plugin install --pi --skip-pi-mcp      # ingest + steering only, no MCP bridge
kcap plugin install --pi --tools-only       # MCP bridge + steering + agent skills, no live-ingest extension
kcap plugin remove --pi                     # delete both extensions + strip the AGENTS.md block
```

Live sessions stream from `~/.pi/agent/sessions/` (honours `PI_CODING_AGENT_DIR`); historical sessions import via `kcap import --pi`.

#### SST OpenCode plugin

SST OpenCode is detected via `~/.config/opencode/` (or `~/.local/share/opencode/`) or the `opencode` binary on `PATH`. OpenCode has **no shell hooks** — it exposes an in-process plugin API — so `install --opencode` writes a dependency-free plugin to `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads at startup. Restart any running `opencode` session after installing.

```bash
kcap plugin install --opencode              # write ~/.config/opencode/plugins/kcap.ts + agent skills
kcap plugin remove --opencode               # delete it
```

Beyond the capture plugin, `install --opencode` (and `kcap setup`) also **registers the seven kcap MCP servers** in `~/.config/opencode/opencode.json` — OpenCode's `mcp` block, each entry `type: "local"` with `command` as an array and `enabled: true` (non-destructive/idempotent, preserving `$schema` and any user servers; opt out `--skip-opencode-mcp`) — and installs a kcap-owned **steering block** into `~/.config/opencode/AGENTS.md` (opt out `--skip-opencode-instructions`). `remove --opencode` reverses all three. (OpenCode reads the agent-agnostic `~/.agents/skills/`, and `install --opencode` writes it, so no separate `--skills` run is needed.)

On `session.created` the plugin runs `kcap hook --opencode` (POSTs lifecycle + spawns the watcher); on each `session.idle` it fetches the session's full messages via OpenCode's in-process SDK and appends them as native `{info, parts}` JSONL to a file the watcher tails (`vendor=opencode`) — so kcap must be on `PATH`. Since OpenCode has **no session-end event**, the watcher synthesizes session-end when the `opencode` process exits. OpenCode records per-message tokens/cost, so those flow through. Historical `kcap import --opencode` reads the SQLite db directly — see [Loading historical sessions](#loading-historical-sessions).

#### Google Antigravity plugin

Google Antigravity is one vendor over two surfaces — the GUI IDE and the `agy` CLI — detected via `~/.gemini/antigravity/` (GUI) or `~/.gemini/antigravity-cli/` (the `agy` CLI); it shares the `~/.gemini` home with the Gemini CLI, honouring `GEMINI_CLI_HOME`; or the `antigravity`/`agy` binary on `PATH`. Detecting the CLI root (or `agy`) matters because an agy-only machine has neither the GUI data root nor an `antigravity` binary — without it the shared capture plugin would never install. Antigravity has **no shell hooks** — it runs *control hooks* configured by a **plugin** — so `install --antigravity` installs the kcap capture plugin to `~/.gemini/config/plugins/kcap/`: a `plugin.json` manifest (which the GUI requires to load the directory) plus a `hooks.json` `kcap` block (preserving any hook blocks you authored). The GUI only reads plugins under its config root — the `~/.gemini/antigravity-cli/` dir is the `agy` CLI's config and is invisible to the IDE. Restart Antigravity after installing so it reloads the plugin.

```bash
kcap plugin install --antigravity           # install the kcap plugin to ~/.gemini/config/plugins/kcap/
kcap plugin remove --antigravity            # remove the kcap plugin
```

Beyond the capture plugin, `install --antigravity` (and `kcap setup`) also **registers the seven kcap MCP servers** in Antigravity's own `~/.gemini/config/mcp_config.json` — its OWN MCP file, not the Gemini CLI's `settings.json` (opt out `--skip-antigravity-mcp`); **installs the kcap steering block** into the shared `~/.gemini/GEMINI.md` (opt out `--skip-antigravity-instructions`); and **copies the kcap skills** into `~/.gemini/skills` — where Antigravity reads them, **not** `~/.agents/skills` (opt out `--skip-antigravity-skills`). All three are non-destructive and idempotent. `remove --antigravity` reverses them, but leaves the shared `~/.gemini/GEMINI.md` block in place when the Gemini CLI integration is still installed (that block is shared; `remove --gemini` owns it then).

Antigravity fires a distinct control hook per lifecycle/tool event; kcap acts on the first `PreInvocation` of a conversation (POSTs lifecycle + spawns a watcher tailing that conversation's `transcript_full.jsonl`, `vendor=antigravity`) — so kcap must be on `PATH`. Antigravity is a GUI whose process outlives any one conversation (like the Codex desktop app), so there is no per-conversation exit signal: the watcher ends a session after it goes idle (default 60 min; override with `KCAP_ANTIGRAVITY_IDLE_MINUTES`), and a later turn reactivates it. Token/model usage lives in each conversation's sibling SQLite db (`conversations/<id>.db`), not the JSONL, so the watcher decodes it and streams the per-generation cost (priced on read; cost is never stored). **Subagents** (Antigravity's nested agents) are separate conversations; both *live* capture and historical `kcap import --antigravity` nest them under the parent, derived from the `INVOKE_SUBAGENT` step in the parent's `transcript_full.jsonl` (the spawn-time linkage signal). Live capture POSTs a subagent-link as each child is spawned; import reads the same `INVOKE_SUBAGENT` steps across all conversations on disk. Historical import reads both product roots' brains — `~/.gemini/antigravity/brain/*/…/transcript_full.jsonl` (GUI) and `~/.gemini/antigravity-cli/brain/*/…/transcript_full.jsonl` (the `agy` CLI) — and backfills sessions from before the hooks were installed; it's watermark-idempotent (safe to re-run) and leaves the working dir empty (Antigravity records no machine-readable cwd in the transcript — live capture gets it from the hook payload). Imported sessions carry cost as well as content — import decodes the same `gen_metadata` db and posts synthetic usage lines, on re-import too, so a session imported before injection shipped gains its cost on a bare re-import. Imported **subagents** are the exception: each child is its own conversation with its own db, and import sends child content without a usage pass, so they carry content but not cost. To import one conversation, `kcap import --antigravity --session <id>` accepts the id in **either** form — the dashed brain-dir conversation id or its dashless canonical form (the id kcap shows for the session) — because import canonicalizes to the same dashless id that live capture uses.

#### Mistral Vibe hooks

Mistral Vibe is detected via `~/.vibe/` or `vibe` on `PATH`, honouring `VIBE_HOME`. Its config is **TOML**, not JSON: `install --mistral-vibe` (and `kcap setup`) merges a kcap entry per lifecycle type — `pre_tool`, `post_tool`, `post_agent` — into the shared `~/.vibe/hooks.toml` (preserving any hooks you authored), **registers the kcap MCP servers** as `[[mcp_servers]]` tables in `~/.vibe/config.toml` (opt out `--skip-mistral-vibe-mcp`), and installs the kcap skills to `~/.agents/skills` (opt out `--skip-mistral-vibe-skills`). Each hook runs the bare `kcap hook --mistral-vibe`, so kcap must be on `PATH`. Vibe loads hooks at startup — restart any running `vibe` session after install.

```bash
kcap plugin install --mistral-vibe          # merge kcap hooks into ~/.vibe/hooks.toml + register MCP
kcap plugin remove --mistral-vibe           # remove kcap hooks + MCP from ~/.vibe
```

Vibe has **no session-start or session-end hook** — only the three tool/turn hooks — so kcap treats the first hook it sees for a session as the start and posts session-start; the session ends when the `vibe` process exits. In Vibe's default unified-harness mode a hook's `transcript_path` is the session's store directory (`~/.vibe/logs/session/unified/<id>/`), so kcap keeps an append-only JSONL copy of its finished entries under its config directory and the watcher (`vendor=mistral-vibe`) tails that copy, deleting it when the session ends. Subagents run in their own `child-<id>` stores with no hooks of their own; the watcher records each as a sub-session of its parent. Token usage comes from Vibe's running session total, and the model from `active_model` in `~/.vibe/config.toml` when a `[[models]]` entry defines it. The hook always exits 0 with empty stdout, a passthrough under Vibe's hook contract, so recording never gates a tool call. Historical `kcap import --mistral-vibe` reads both stores under `~/.vibe/logs/session/`: the unified-harness store and the legacy `session_<ts>/messages.jsonl`.

#### Grok hooks

Grok Build is detected via `~/.grok/` or `grok` on `PATH`, honouring `GROK_HOME`. `install --grok` (and `kcap setup`) writes kcap's own `~/.grok/hooks/kcap.json`, which Grok loads alongside any other `*.json` in that directory, and installs the kcap skills to `~/.agents/skills` (`kcap plugin install --grok --skip-grok-skills` opts out). Each hook runs the bare `kcap hook --grok`, so kcap must be on `PATH`. User-level hooks need no `/hooks-trust`. Grok loads hooks at startup — restart any running `grok` session after install.

```bash
kcap plugin install --grok                  # write ~/.grok/hooks/kcap.json + agent skills
kcap plugin remove --grok                   # delete ~/.grok/hooks/kcap.json
```

kcap hooks Grok's `SessionStart` and `Stop`. Session start posts the session and starts the watcher (`vendor=grok`) on the session's `~/.grok/sessions/<percent-encoded cwd>/<session-id>/updates.jsonl`; `Stop` restarts a watcher that has gone. The session ends when the `grok` process exits, after the watcher's final drain. A subagent runs as its own session beside its parent's, with no hook of its own; the parent watcher registers each one when the parent's `updates.jsonl` records it spawning, streams its transcript as a sub-session, and ends it when the parent records it finishing (or when the parent exits). Historical `kcap import --grok` reads `~/.grok/sessions/` and imports each subagent through its parent. The hook always exits 0 with empty stdout. Grok also runs the hooks Claude Code's and Cursor's configs declare; a `kcap hook --claude` or `--cursor` that Grok runs with its own payload does nothing, so a machine set up for those agents records Grok sessions once, as Grok. Grok has no tools of its own from kcap: it loads the MCP servers set up for Claude Code, and they act on the Grok session, named by the `GROK_SESSION_ID` Grok gives them, even when Grok was started from a Claude Code shell.

#### Muse Code hooks

Muse Code is detected via `muse` on `PATH` or `~/.config/muse/` / `~/.local/share/muse/`, honouring `XDG_CONFIG_HOME` and `XDG_DATA_HOME`. Its config is JSON: `install --muse` (and `kcap setup`) merges one `hooks` group per lifecycle event — `SessionStart`, `UserPromptSubmit`, `SubagentStart`, `SubagentStop`, `Stop`, `SessionEnd` — into `~/.config/muse/settings.json`, preserving every other setting and any hooks you authored. **A `settings.json` that does not parse is left untouched**: the install fails with a message to fix the file rather than rewrite it, because a broken `settings.json` stops every `muse` command. It also **registers the kcap MCP servers** under `mcp_servers` with `mode: optional`, so a stopped daemon never aborts a Muse run (opt out `--skip-muse-mcp`), and installs the kcap skills to `~/.agents/skills` (opt out `--skip-muse-skills`). Each hook runs `kcap hook --muse --no-update-check`, so kcap must be on `PATH`. Muse runs hooks in a cleared environment — no `XDG_*`, `KCAP_CONFIG_DIR`, `KCAP_PROFILE` or `KCAP_SKIP` — so live recording uses the Muse roots resolved when `kcap setup` or `kcap plugin install --muse` ran, saved in `~/.config/kcap/muse-roots.json` when they are not the defaults; re-run the install after changing `XDG_DATA_HOME` or `XDG_CONFIG_HOME`. Muse loads hooks at startup — restart any running `muse` session after install.

```bash
kcap plugin install --muse          # merge kcap hooks into ~/.config/muse/settings.json + register MCP
kcap plugin remove --muse           # remove kcap hooks + MCP from settings.json (your own entries stay)
```

Muse's hook payloads carry no transcript path, so kcap finds the session's log at `~/.local/share/muse/sessions/YYYY/MM/DD/<id>/session.jsonl` and the watcher (`vendor=muse`) tails it. Worker subagents and the `skill-reminder`, `goal-reminder` and `verify-reminder` child sessions keep their own logs under `subagent/`; the watcher records each as a labelled subagent of its parent. A Muse that crashes without firing `SessionEnd` is finalized when its `muse-bin-<version>` process exits. `kcap status` lists any lifecycle event whose hook is missing. Removing every kcap hook from `settings.json` by hand turns capture off: `kcap status` reports Muse as not configured, and `kcap refresh` leaves the hooks out until `kcap plugin install --muse` runs again. Historical `kcap import --muse` reads every session log under `~/.local/share/muse/sessions/` along with its subagent logs.

Cursor uses a single user-scope `hooks.json`; there is no project-scope variant.

`kcap setup` writes all 8 supported Cursor hook entries. Use `--skip-cursor-hooks` to opt out during setup:

```bash
kcap setup --server-url <url> --no-prompt --skip-cursor-hooks
```

#### Daemon config settings

Use `kcap config set` to configure the daemon. The values are stored in the active profile. The binary paths take effect the next time the daemon starts; `daemon.max_agents` is also applied to the running daemon.

```bash
kcap config set daemon.claude_path /opt/claude/bin/claude
kcap config set daemon.codex_path  /opt/codex/bin/codex
```

| Key | Default | Description |
|-----|---------|-------------|
| `daemon.claude_path` | `"claude"` | Path to the Claude CLI binary. Resolved via `PATH` when not an absolute path; on macOS the default falls back to the Claude desktop app's bundled Claude Code when `PATH` has none. |
| `daemon.codex_path`  | `"codex"`  | Path to the Codex CLI binary. Resolved via `PATH` when not an absolute path; on macOS the default falls back to the Codex CLI bundled in `ChatGPT.app` when `PATH` has none. |
| `daemon.auto_resume` | `true`     | Resume hosted agents when the daemon starts after a restart. `false` leaves them interrupted in `kcap agent ls`, from where `kcap agent resume <id>` retries one. Read at daemon start. The desktop app's **Settings…** sets it too. |
| `daemon.max_agents`  | `5`        | Maximum concurrent hosted coding agents. `0` means unlimited. Also settable per launch with `--max-agents` or the `KCAP_MAX_AGENTS` env var, which take precedence at startup. `kcap config set daemon.max_agents` and the desktop app's **Settings…** hand the value to the running daemon, which applies it without a restart. |

You can also override these at runtime with environment variables (take precedence over the profile):

```bash
KCAP_CLAUDE_PATH=/opt/claude/bin/claude kcap daemon
KCAP_CODEX_PATH=/opt/codex/bin/codex  kcap daemon
```

##### Codex app-server transport

Hosted Codex agents (review-flow reviewers and interactive agents) can run over the `codex app-server` JSON-RPC protocol instead of the interactive terminal. It is an experimental feature, off by default, and is switched per profile — the one `KCAP_PROFILE` names, or else the active profile:

```bash
kcap experimental                          # name the profile, then list features, whether each is on, and whether the daemon applied it
kcap experimental enable codex-app-server  # opt in
kcap experimental disable codex-app-server # roll back
```

- The running daemon applies the change to its next launch without a restart, and `kcap experimental enable` waits a few seconds for it to confirm. Agents already running keep the transport they started with.
- It takes effect only when the installed Codex meets the minimum version (**0.146.0**); otherwise the feature is saved but reported as not active, with the reason, and new launches take the terminal. A launch the server already pinned to app-server is refused instead, since it is never moved to another transport
- PR reviews (`kcap review`) and `kcap agent start` local spawns always use the terminal.
- The former `KCAP_CODEX_TRANSPORT` environment variable is removed; a daemon that still sees it logs a warning naming the command above.

The Cursor CLI path (`cursor-agent` by default, used to spawn the `cursor` hosted-agent vendor) is env-only for now — there is no `daemon.cursor_path` profile key yet, so set it per-launch:

```bash
KCAP_CURSOR_PATH=/opt/cursor/bin/cursor-agent kcap daemon
```

`KCAP_CURSOR_MODEL` overrides the model a `cursor` hosted agent runs (default `claude-sonnet-4-5`; the per-launch model from the dashboard takes precedence). It is matched against the models the Cursor account actually offers, so a family name like `claude-sonnet-4-5` resolves to the exact available variant; an unrecognized value falls back to Cursor's own default.

```bash
KCAP_CURSOR_MODEL=claude-opus-4-8 kcap daemon
```

`KCAP_COPILOT_PATH` overrides the `copilot` binary the daemon spawns for **GitHub Copilot hosted agents** (`copilot --acp --stdio`), mirroring `KCAP_CURSOR_PATH` — the daemon hosts Claude, Codex, Cursor, Copilot, Kiro, Gemini, Pi, OpenCode, Antigravity, Grok and Muse Code. `KCAP_GEMINI_PATH` overrides the `gemini` binary the same way (`gemini --experimental-acp`), and applies to both hosted Gemini agents and the [unattended Gemini reviewer](#unattended-reviewers-are-enabled-by-default-they-used-to-be-opt-in) (enabled by default) — whose build-affirmation check reads whichever binary it names. `KCAP_OPENCODE_PATH` overrides the `opencode` binary the daemon spawns for **OpenCode hosted agents** (`opencode acp`) — no longer reserved; see [Hosted OpenCode agents](#hosted-opencode-agents) below. `KCAP_GROK_PATH` overrides the `grok` binary the daemon spawns for **Grok Build hosted agents** (`grok agent --no-leader stdio`); see [Hosted Grok agents](#hosted-grok-agents) below. `KCAP_PI_PATH` overrides the `pi` binary the daemon spawns for **Pi hosted agents** (`pi --mode rpc`) — interactive hosting only in this release; see [Hosted Pi agents](#hosted-pi-agents) below. `KCAP_MUSE_PATH` overrides the `muse` binary the daemon spawns for **Muse Code hosted agents** (`muse serve`); see [Hosted Muse Code agents](#hosted-muse-code-agents) below.

```bash
KCAP_COPILOT_PATH=/opt/copilot/bin/copilot kcap daemon
```

`KCAP_KIRO_PATH` overrides the AWS Kiro CLI binary the daemon spawns for **Kiro hosted agents**
(`kiro-cli acp`). It defaults to `kiro-cli` — the name a standard install puts on `PATH` — and only
that one name is probed, so point this at your binary if yours is named differently. As with Cursor,
if the daemon can't resolve it the `kiro` vendor is simply hidden from the launch dialog rather than
failing at launch.

```bash
KCAP_KIRO_PATH=/opt/kiro/bin/kiro-cli kcap daemon
```

`KCAP_KIRO_MODEL` overrides the model a `kiro` hosted agent runs, mirroring `KCAP_CURSOR_MODEL` —
with one deliberate difference: there is **no built-in default**, so with nothing set (and no
per-launch model from the dashboard, which takes precedence) a Kiro hosted agent runs whatever
Kiro's own default model is and kcap reports none. The value is matched against the models the Kiro
account actually offers (Kiro's ids are bare names like `claude-haiku-4.5`); an unrecognized value
falls back to Kiro's own default with none reported. Applied over ACP `session/set_model` —
verified to take effect at the turn level, not just accepted — because Kiro does not implement the
`session/set_config_option` call Cursor uses.

```bash
KCAP_KIRO_MODEL=claude-haiku-4.5 kcap daemon
```

One limit is worth knowing before you pick Kiro:

- **Interactive hosting only.** Kiro cannot yet be selected as an unattended review-flow reviewer.
  Kiro inherits the MCP servers from your global `~/.kiro/settings/mcp.json` into every ACP session,
  which is exactly what you want for a session you are driving yourself, but means an unattended
  reviewer would be handed the flow-starting `kcap-flows` server. Containment for that is tracked
  separately. (This also means a *pinned reviewer* model never reaches Kiro today — reviewer model
  overrides remain gated on the vendors that advertise resolver support.)

### Hosted OpenCode agents

`KCAP_OPENCODE_MODEL` overrides the model an `opencode` hosted agent runs, mirroring
`KCAP_KIRO_MODEL` — including the same deliberate absence of a built-in default: with nothing set
(and no per-launch model from the dashboard, which takes precedence) the agent runs whatever
OpenCode's own configured default is and kcap reports none. The value is matched against the models
your OpenCode account actually offers, whose ids are `provider/model`; a display label works too, so
`opencode/deepseek-v4-flash-free`, the `opencode/deepseek` prefix and `DeepSeek V4 Flash Free` all
resolve to the same model. An unrecognized value falls back to OpenCode's own default with none
reported.

```bash
KCAP_OPENCODE_MODEL=opencode/big-pickle kcap daemon
```

Two things are worth knowing before you pick OpenCode:

- **Your OpenCode plugins do not load in a hosted agent.** The daemon spawns the child with
  `OPENCODE_PURE=1`. This is not a preference: OpenCode is the one vendor where kcap has *two* capture
  paths, and kcap's own live-ingest plugin (`~/.config/opencode/plugins/kcap.ts`) loads inside the
  hosted child too — where it would start a second, independent recording of the very session the
  daemon is already recording, so the run would show up twice. Suppressing external plugins in the
  hosted child is what keeps a daemon-hosted session to exactly one recording. Sessions you start
  yourself are untouched: the plugin keeps its whole job there.
- **Unattended review works out of the box** — see below.

#### OpenCode as an unattended review-flow reviewer

`start_review_flow(vendor="opencode")` works on any daemon with `opencode` installed. To turn it off,
set `KCAP_OPENCODE_UNATTENDED_REVIEWER=0` in the daemon's environment.

The reviewer's tool surface is deliberately narrow — the narrowest of any reviewer kcap offers. It gets
`read`, `grep`, `glob` and `list` plus the one MCP channel it reports results through — **no shell, no
write, no edit, no network** — enforced by OpenCode's own permission table rather than by asking the
model nicely.

Worth knowing about the part that narrow surface does *not* cover: **those read tools are not
path-scoped.** They are whole-filesystem read primitives running as the daemon user, so a review can read
any file that user can read — credentials included — and a reviewer's findings text goes back to whoever
requested the review. That is true of *every* reviewer, including Claude, Codex, Cursor and Copilot, which
can additionally write files and run shell commands. So the decision worth making is whether to allow
unattended reviews on this daemon at all (`kcap daemon consent`), not which vendor serves them.

Two further things the launch does, which are worth knowing because they change what the reviewer
sees:

- **The reviewed branch's own configuration is ignored** (`OPENCODE_DISABLE_PROJECT_CONFIG`). A
  contributor-authored `opencode.json` / `.opencode/` and the repo's `AGENTS.md`/`CLAUDE.md` are
  inputs *from the thing being reviewed into the reviewer judging it*, so they are suppressed. The
  cost is that the reviewer does not see your repo's guidance documents.
- **Your global MCP servers are absent** (an empty per-launch `OPENCODE_CONFIG_DIR`). Otherwise a
  reviewer would inherit `kcap-flows` and could start review flows of its own.

Being on by default does **not** bypass the build check. The containment above is behaviour of the
installed `opencode` build, so the daemon records a **minimum** version and refuses anything older.
That minimum is seeded on the first startup that finds the binary, from whatever is installed then, so
a later upgrade needs no action from you; to move the floor to the currently-installed build (after a
bad release, say), run:

```bash
kcap daemon reviewer affirm --vendor opencode
```

POSIX only: the containment is an *empty* config directory, which cannot be made owner-only on
Windows.

**Hosted Cursor agents run over ACP.** The `cursor` vendor is launched by the daemon as
`cursor-agent acp` (Cursor's Agent Client Protocol server) in an isolated worktree, driven from the
dashboard. This is **distinct from the Cursor recording hooks** (`~/.cursor/hooks.json`, which capture
sessions you run yourself in Cursor) — a hosted ACP agent is one the daemon *starts and supervises*.
It requires:

- The Cursor CLI (`cursor-agent`) installed and on `PATH`, or pointed at via `KCAP_CURSOR_PATH`. If the
  daemon can't find it, the `cursor` vendor is simply hidden from the launch dialog.
- A **logged-in Cursor account on a Team-tier (paid) subscription** — run `cursor-agent login` first.
  On the Free tier the agent refuses to run turns (it returns "Upgrade your plan to continue"); a
  logged-out CLI fails the handshake.

**Current limitations.** A hosted Cursor agent renders its transcript (messages, thinking, tool
calls/results) but does **not** support interactive local-attach input, special keys, or terminal
resize, and it emits no separate terminal-output stream — Cursor runs shell commands itself in the
worktree and they surface as `execute` tool results in the transcript. kcap advertises **no**
client filesystem or terminal capabilities to the agent (it performs those operations itself).

**Troubleshooting.**

- *`cursor` missing from the launch dialog* — the daemon didn't find `cursor-agent`; install it or set
  `KCAP_CURSOR_PATH`, then restart the daemon.
- *Agent connects but every turn fails with "Upgrade your plan to continue"* — the Cursor account is on
  the Free tier; hosted turns require a **Team-tier** subscription. (The handshake succeeds; the refusal
  is returned per-turn.)
- *Agent fails at startup / never establishes a session* — the CLI is likely logged out (`cursor-agent
  login`) or the binary is broken; the daemon logs the handshake failure.
- *Model not applied* — `KCAP_CURSOR_MODEL` (or the per-launch model) is matched against the models the
  account actually offers; an unrecognized value falls back to Cursor's default.

**Data handling.** A hosted Cursor agent runs as a local child process of the daemon with the daemon's
own filesystem/process access — the same unsandboxed-in-a-worktree posture as every hosted agent — and
its transcript (prompt text, tool arguments, tool results) is forwarded to the kcap server
verbatim, exactly like every other agent, with no Cursor/ACP-specific redaction.

**Codex session-end tuning.** Because Codex has no session-end hook, the watcher owns session-end via two triggers: parent `codex` process exit, and rollout-file idle timeout. The idle trigger is particularly important for the Codex desktop app, whose shared `codex app-server` process never exits per-conversation.

| Environment variable | Default | Description |
|----------------------|---------|-------------|
| `KCAP_CODEX_IDLE_MINUTES` | `60` | How long a Codex rollout file may be idle (no new rollout lines and no Codex tool call in flight) before the `kcap watch` background watcher ends the session (`reason: idle_timeout`). Increase for very long thinking/compute turns; decrease for faster cleanup of abandoned sessions. Invalid or non-positive values fall back to the 60-minute default. |
| `KCAP_CODEX_SUBAGENT_IDLE_MINUTES` | `5` | Idle grace between a Codex collab subagent finishing its turn (its rollout's `task_complete`, no tool call in flight) and the child watcher marking it completed on the server, so the subagent's chat card stops showing "in progress" while the parent session keeps running. The grace absorbs quick same-round re-engagements by the parent; `0` marks completion immediately at turn end. The completion is one-shot — a subagent the parent re-engages after the grace keeps streaming into its transcript but stays marked completed. Invalid or negative values fall back to the 5-minute default. |
| `KCAP_CODEX_SUBAGENT_REAP_MINUTES` | `360` | How long a Codex collab **subagent** watcher may go idle before it reaps itself (see below). Applies only to subagent watchers — the parent session watcher's idle behavior is tuned by `KCAP_CODEX_IDLE_MINUTES`. Deliberately generous (6h): this is a leak backstop, not an end-of-conversation signal. A subagent with a tool call still in flight is never reaped. Invalid or non-positive values fall back to 360 minutes. |
| `KCAP_PARENT_DEAD_CEILING_MINUTES` | `360` | Staged recovery ceiling for a watcher whose parent coding-agent PID was already dead at startup (a resolution glitch) and can't be re-resolved. The watcher first periodically re-resolves and re-arms the parent-exit watchdog; only if that keeps failing AND the transcript makes no progress for this long does it post `session-end` (`reason: parent_dead_ceiling`). Deliberately far above the idle timeout so a user parked at a Kiro/OpenCode prompt is never ended prematurely. Invalid or non-positive values fall back to 360 minutes (6h). |
| `KCAP_CURSOR_IDLE_CEILING_MINUTES` | `60` | How long a Cursor session's transcript watcher may go idle before it exits (AI-1382). Unlike Codex/Antigravity, this exit does NOT itself POST `session-end` — Cursor's end-of-session synthesis stays owned by the `sessionEnd` hook or, as a backstop, a server-side lease-gated sweep; the next hook for that session reactivates a fresh watcher. Invalid or non-positive values fall back to the 60-minute default. |
| `KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES` | `360` | How long a Claude **subagent** watcher may go idle before it reaps itself (see below). Applies only to subagent watchers — a Claude *session* watcher has no idle ceiling and is unaffected. Deliberately generous (6h): this is a leak backstop, not an end-of-conversation signal. Invalid or non-positive values fall back to 360 minutes. |

**Claude subagent watcher reaping.** A `kcap watch` subagent watcher normally exits when the
`SubagentStop` hook fires. If that hook is disrupted, the watcher used to survive until the entire
parent `claude` process exited — accumulating one leaked ~40 MB process per missed stop for the
life of the session (issue #514). Subagent watchers now also self-reap after
`KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES` of transcript silence. A subagent with a tool still in flight
(a `tool_use` with no matching `tool_result`) is never reaped no matter how long it is quiet, so a
long build or test run cannot be cut short. Like Cursor's ceiling, this exit posts no
`session-end` — a subagent watcher never owned that.

**Codex subagent watcher reaping.** Codex collab subagent watchers had no exit path at all (issue
#550): they relied on the parent session watcher's teardown, which finalized their server-side
records but never stopped the local processes — so each collab subagent leaked one watcher process
that reconnected to the server indefinitely. The parent session watcher now stops every child
watcher it spawned as part of its own exit (SIGTERM-first on macOS/Linux so each child runs its
final drain; on Windows the stop is forceful and any undelivered tail is recovered by the
spool/import paths), and as a backstop for a hard-killed parent, a Codex subagent watcher self-reaps after
`KCAP_CODEX_SUBAGENT_REAP_MINUTES` of rollout silence. A reaped subagent that the parent later
re-engages is respawned by the parent's rollout scan as soon as its rollout grows again, resuming
from the server's frontier so no content is lost. Like the Claude ceiling, the reap exit posts no
`session-end`.

### Hosted Grok agents

The daemon hosts Grok Build over ACP (`grok agent --no-leader stdio`) when `grok` is on `PATH` (or `KCAP_GROK_PATH` names it). A per-launch model is applied through Grok's `model` session option; with none, Grok runs its own default.

- **A hosted Grok asks before it acts, even if you run Grok with always-approve.** Grok applies `permission_mode = "always-approve"` from `~/.grok/config.toml` to agent sessions too, which would let it run every tool without asking the daemon. The daemon turns that off for the sessions it hosts, so approvals and the launch dialog's permission presets apply.
- **It runs as its own process,** never through a shared `grok leader`, so stopping the agent stops Grok.
- **It has every kcap tool.** The daemon hands a hosted Grok all of kcap's MCP servers, run by the `kcap` on the daemon's `PATH`, and Grok runs them in place of the same-named servers it imports from your Cursor config, so a registration left on a removed binary cannot take a tool away. They act on the hosted session. A private agent is handed none of them, so, like a `kcap launch --private` run, it has only the MCP servers Grok loads from your own config, kcap's included if you registered them there.
- **It is recorded once.** kcap's own Grok hook loads inside the hosted child and stands down there, because the daemon already records the session. A `grok` the hosted agent starts itself is still recorded.
- **Its usage is recorded per turn.** The daemon reads each finished turn's per-model token usage from Grok's `_x.ai/session_notification` extension, so a hosted Grok session is costed like one you run yourself. Usage on `grok-4.7-build-fast` is recorded but shows no cost, since xAI bills it at a different rate.
- **It can ask you questions.** Each question Grok asks appears on the agent's page as a question card, and your answer goes back to Grok. A question that is cancelled tells Grok you declined, and it carries on without the answer.
- **Its subagents are recorded as subagents.** Each subagent Grok starts lands on its own sub-session of the hosted session, with its start and finish recorded on the parent.

#### Grok as an unattended review-flow reviewer

Enabled by default; `KCAP_GROK_UNATTENDED_REVIEWER=0` in the daemon's environment disables it. Like Claude, Grok declares no containment, so it reviews in an owned worktree only, never a borrowed checkout.

- **Whatever Grok asks about, the daemon refuses unless it is a call on a server it injected for the review.** That includes its result channel. Any other request ends the round rather than being answered. Grok asks before an edit or a command it does not class as read-only.
- **The reviewer gets reads, search and the shell, and no edit tools.** It is launched with an agent profile that removes `search_replace` and `write`, subagents and web search.
- **Your Grok configuration still applies.** Permission rules you set in `~/.grok` approve matching calls without asking, and your Grok hooks and MCP servers run as they do in your own sessions. Grok has no per-process switch for them. Your Claude Code and Cursor MCP servers, hooks, skills and rules are not loaded.
- **Version floor:** the daemon records the installed `grok` at startup and refuses an older build. `kcap daemon reviewer affirm --vendor grok` moves it.

### Hosted Muse Code agents

The daemon hosts Muse Code over `muse serve` (chat, no terminal) when `muse` is on `PATH` (or `KCAP_MUSE_PATH` names it). `KCAP_MUSE_MODEL` overrides the model, mirroring `KCAP_PI_MODEL`; a per-launch model from the dashboard takes precedence, and with neither set Muse runs its own default.

```bash
KCAP_MUSE_MODEL=<model> kcap daemon
```

- **Credentials stay on the daemon host.** The daemon never looks for them, and one installed as a service inherits no `META_API_KEY` exported in your shell: run `muse auth set` as the user the daemon runs as, or set `META_API_KEY` in the service unit's own environment. On Linux this is required; a macOS `auth.json` does not transfer.
- **Linux needs bubblewrap with user namespaces.** Without them a reviewer launch is refused, and an interactive agent runs without Muse's sandbox.
- **Always in a daemon-owned worktree,** never your own checkout, because Muse's sandbox does not confine reads. The worktree is trusted, so its Muse hooks and MCP servers load; new network hosts ask for approval.
- **Reminder child sessions run and bill on every launch.** Muse does not let `serve` disable them; each is recorded as a subagent of its parent.
- **It is recorded once,** from Muse's own session log. Reviews: [Unattended Muse reviews](#unattended-muse-reviews).

### Hosted Pi agents

`KCAP_PI_MODEL` overrides the model a `pi` hosted agent runs, mirroring `KCAP_OPENCODE_MODEL` —
including the same deliberate absence of a built-in default: with nothing set (and no per-launch
model from the dashboard, which takes precedence) the agent runs whatever Pi's own configured
default is and kcap reports none. The value is passed verbatim as `--model <value>` on the spawned
`pi --mode rpc` child's argv; kcap does not query or validate it against Pi's available models, so
an unrecognized value is Pi's own error to report.

```bash
KCAP_PI_MODEL=claude-opus-4-5 kcap daemon
```

Two things are worth knowing before you pick Pi:

- **kcap's capture extension stands down in a hosted agent; the MCP-bridge extension does not.**
  kcap's global Pi live-ingest extension (`~/.pi/agent/extensions/kcap.ts`) auto-loads inside every
  `pi` process on the machine, hosted or not, so the daemon spawns the hosted child with
  `KCAP_PI_PURE=1` — read by that extension at the top of its exported function, which then returns
  immediately and registers no handlers, since a hosted session is already captured over the RPC
  wire this runtime speaks and would otherwise be captured twice. The MCP-bridge extension
  (`~/.pi/agent/extensions/kcap-mcp.ts`) is unaffected by that gate and still loads, so the kcap MCP
  servers it exposes remain available as native Pi tools. Sessions you start yourself are untouched
  either way: both extensions keep their whole job there.
- **Interactive hosting and unattended review flows, in a daemon-owned worktree only.**
  `start_review_flow(vendor="pi")` launches a real reviewer — see [Unattended Pi
  reviews](#unattended-pi-reviews) below. A Pi PR review (`kcap review <pr>` / the dashboard's
  Review PR action) is still refused, because that surface needs the `kcap mcp review` tool set
  only PTY-backed vendors are given. There is also no borrowed-workspace containment for Pi, so a
  hosted Pi launch — interactive or reviewer — always runs in a daemon-owned worktree, never your
  own checkout.

#### Review-flow reviewer backstops & crash-survivor reaping

Hosted review-flow reviewers are *unattended* and count against the daemon's `--max-agents` budget. To keep a stuck or abandoned reviewer from holding a slot forever, the daemon defends its own capacity in layers: OS-level containment at spawn time (this section) plus a managed record/scan/quarantine backstop for everything containment can't reach.

- **Lifetime / idle backstop.** The heartbeat reaps a review-flow reviewer that has run past a maximum lifetime or gone idle too long (the driver vanished, or its run went terminal on the server without the daemon hearing about it). Interactive agents are never touched by these bounds.
- **OS-level containment at spawn (native, immediate where the OS supports it).**
  - **Windows** — every hosted PTY is created already bound to a `KILL_ON_JOB_CLOSE` Job Object (no breakaway allowed): the OS itself kills the agent **and every descendant** the instant the job handle's last reference closes — clean shutdown, daemon crash, or an external kill, no exceptions. There is no survivor class to reap on Windows; the PID-record layer becomes pure bookkeeping.
  - **Linux** — a native spawn shim (`libpty_shim`) forks the agent, arms `PR_SET_PDEATHSIG`, and execs via `execveat` on a dedicated daemon-lifetime thread, so the agent **leader** dies immediately if the daemon dies — for a launch the shim proved was contained at initial exec (a non-privileged, non-deep-shebang binary on a kernel ≥ 3.19). Descendants and any uncontained-classified launch (a privileged binary, an old kernel, an unresolvable shebang) fall back to the crash-survivor record/scan reaping below.
  - **macOS** — there is no OS primitive for this at all (no PDEATHSIG, no job objects); a crash-surviving child is recovered *eventually*, at the next daemon boot/heartbeat, via the record layer below.
- **Crash-survivor reaping (the managed backstop — covers everything containment doesn't).** Each hosted child's pid + an exact OS-native start-identity is written to a durable per-daemon record under `{state-dir}/{name}/agents/` at spawn (Linux: kernel `boot_id`+`starttime`; macOS: a kernel-assigned, boot-scoped unique process id + boot-session UUID; Windows: an absolute start timestamp, moot once containment ships since there's no survivor to reap), and every child is stamped with `KCAP_AGENT_ID` / `KCAP_DAEMON_ID` / `KCAP_DAEMON_EPOCH` env markers. On the next boot (and on the heartbeat) the daemon reaps any child that outlived a **prior** incarnation of itself — matched by exact `(pid, start-identity)` from the record, or, for a recordless survivor, by the env markers (same daemon id, older epoch). A process is killed only when its identity is *proven*; anything ambiguous is spared (never a wrong kill). On Linux the env checks read `/proc/{pid}/environ`; on macOS process env is redacted from other processes entirely, so the record-based path is the effective mechanism there, and a record whose identity couldn't be captured (a rare private-ABI hiccup) is retained and logged each sweep rather than silently dropped — resolved automatically on Linux (the env-marker scan can still confirm it) or by a manual kill on macOS. Note: `kcap launch`'s private local-attach path spawns through the same OS-containment layer but writes no durable record at all (no server-side ownership to protect there), so it has no crash-survivor backstop beyond whatever OS containment applies.

| Environment variable | Default | Description |
|----------------------|---------|-------------|
| `KCAP_REVIEWER_MAX_LIFETIME` | `6h` (`21600`) | Max wall-clock lifetime, **in seconds**, for a hosted review-flow reviewer before the heartbeat reaps it. `0` disables the bound. |
| `KCAP_REVIEWER_IDLE_TIMEOUT` | `2h` (`7200`)  | Max time, **in seconds**, a reviewer may go without output before the heartbeat reaps it. `0` disables the bound. |
| `KCAP_REVIEWER_RESUMABLE_IDLE_TIMEOUT` | `10m` (`600`) | Max time, **in seconds**, a **resumable** (Codex app-server) reviewer may go idle before the heartbeat **parks** it — freeing its slot while keeping its thread alive for a later resume — instead of reaping it at `KCAP_REVIEWER_IDLE_TIMEOUT`. Applies only to resume-capable reviewers; all others are unaffected. `0` disables parking, so a resumable reviewer falls through to the ordinary idle reap. |

#### Daemon log verbosity

The daemon logs at `Information` by default. Raise the level for transport diagnostics — for example, per-tick `DaemonPing` round-trip times (logged at `Debug`) are useful for telling whether SignalR reconnects are caused by network/proxy latency. Set it either way:

```bash
kcap daemon start --log-level debug        # foreground or with -d; forwarded to the daemon
KCAP_DAEMON_LOG_LEVEL=debug kcap daemon    # env var; read directly, works in any launch mode (service, container)
```

Accepted values: `trace`, `debug`, `information` (default), `warning`, `error`, `critical`, `none`. The `--log-level` flag wins over the env var when both are set. `Debug` is verbose — it also enables the SignalR client's framework logs — so use it for a diagnostic window rather than steady state.

**Full ACP frame logging (`KCAP_ACP_DEBUG_FRAMES`).** Off by default. When set to `1`/`true`, a hosted Cursor (ACP) session logs full inbound/outbound JSON-RPC frames, raw unrecognized `session/update` payloads, and `cursor-agent` stderr at `Debug` (length-capped); with it off, only their shape (kind + length) is logged. **These frames can contain prompts, tool arguments, and file contents** — enable it only for a diagnostic window, never in a shared or persistently-logged environment. It needs `Debug` logging on to be visible:

```bash
KCAP_ACP_DEBUG_FRAMES=1 KCAP_DAEMON_LOG_LEVEL=debug kcap daemon
```

#### ACP crash reconnect (`KCAP_ACP_RECONNECT`)

When a hosted ACP agent's child process dies mid-session (a crash, an OOM kill — not a stop you
asked for), the daemon transparently resumes the session where the vendor supports it: it relaunches
the agent binary and restores the same session via ACP `session/load`, keeping the dashboard
session, transcript, and agent slot intact. A note appears in the transcript ("Agent process
restarted; the session was resumed"), and if a message was in flight at the crash it asks you to
resend it rather than guessing. Resume is attempted only for vendors verified to support it across a
crashed process (currently `cursor` and `copilot`; Kiro and Gemini refuse a crashed session's load,
so their agents end as before), and a session that keeps crashing stops being resumed after 5
recoveries. Set `KCAP_ACP_RECONNECT=0` to disable reconnect entirely — a child death then ends the
session immediately, the pre-reconnect behavior:

```bash
KCAP_ACP_RECONNECT=0 kcap daemon
```

#### Diagnosing a hard death

A daemon killed by an uncatchable `SIGKILL` (macOS **jetsam** / Linux **OOM**, `kill -9`, power loss) or a hard native crash can't log its own exit — the process is gone before any handler runs. Two things help tell those apart from a normal stop:

- **`~/.config/kcap/daemon.out.log`** — a background (`-d`) daemon reopens its stdout/stderr onto this file, so a runtime "Fatal error." dump or native crash message (which bypasses the normal `daemon.log` pipeline) is captured here. (`kcap daemon start -d` wires this up automatically by passing the daemon a `--stderr-file` flag; you don't set it yourself.) A service-managed daemon captures the same output via its service log. An empty file means nothing was written to stderr — i.e. a `SIGKILL`, not a crash.
- **Startup breadcrumb** — when a daemon starts and finds the previous instance's lock was left for the kernel to release (the signature of an uncatchable kill), it logs a `warning` to `daemon.log` naming the dead PID. If you see this recur, run the daemon as a service (`kcap daemon service install`) so it auto-restarts instead of staying down.

### Local agents (`kcap agent`)

Start a coding agent from your own terminal that the daemon hosts for you. Because the daemon owns the agent (not your terminal), you can **detach and the agent keeps running**, then **re-attach later** — like `tmux` for your coding agent.

```bash
kcap agent start claude                       # start Claude in the current directory, attached
kcap agent start claude -- --model opus       # everything after `--` is passed to the agent CLI verbatim
kcap agent start codex --worktree -- -m gpt-5 # run in an isolated git worktree instead of in place
kcap agent start claude -d                    # start without attaching; prints the agent id
kcap agent start claude --title "Fix login" -- "fix the login redirect"  # name the session
kcap agent start claude --account ~/.claude-work  # run under a registered account (id prefix or directory)
```

- **`--` boundary:** flags before `--` are kcap's; everything after `--` is forwarded to the `claude`/`codex` CLI unchanged. kcap flags: `--worktree`, `--private`, `--title <text>`, `--account <id|dir>`, `--daemon <name>`, `-d`/`--detach`.
- **Claude session id:** kcap passes Claude a fresh `--session-id` ahead of your args, so the daemon can tell the agent's transcript from your own Claude sessions in the same folder. Args that choose the session themselves (`-c`, `--resume`, `--session-id`, `--fork-session`, …) are left alone; in place, such an agent is linked to its transcript only when they name the session outright: `--resume <uuid>` or `--session-id <uuid>`, not directly after another flag (which could be taking it as its value).
- **Visibility:** by default the agent is **registered with the server**, so it appears in your own web UI immediately and you can drive it from the browser — start in the terminal, continue from anywhere. It is **visible only to you** until you share it. Pass `--private` to keep it purely local: unregistered, not streamed to the server, and not shown in the web UI.
- **Session title:** `--title <text>` (or `--title=<text>`) names the agent's session, and that name sticks until it is renamed. Without it, the session starts titled after the prompt — the last argument after `--`, when neither it nor the argument before it is a flag — and a generated title replaces that once one lands. `--private` agents are never registered, so a title has no effect on them.
- **Account:** `--account <id|dir>` (or `--account=<id|dir>`) runs a Claude or Codex agent under one of your [accounts](#accounts): an id prefix of at least 4 characters, or the account's directory (`~` is expanded). kcap refuses and starts nothing when the value matches no account of that vendor, when a prefix matches more than one, or when the daemon is too old to choose an account. Without the flag the agent uses the vendor's default configuration directory, as before.
- **Work location:** by default the agent runs **in place in your current directory** (it edits your real files). Pass `--worktree` to run in a throwaway git worktree instead.

- **What a worktree deliberately does NOT inherit:** an agent worktree is a checkout of whatever branch is
  being worked on, so anything committed there is content the agent's own author may not control. These are
  therefore neutralised when kcap creates one:

  - **Workspace MCP config is removed** — `.mcp.json`, `.cursor/mcp.json`, `.gemini/settings.json`,
    `.kiro/settings/mcp.json`, `.vscode/mcp.json`, `.github/copilot/mcp.json`, `.copilot/mcp.json`,
    `.codex/config.toml`. Some vendors execute the `command` these declare at session start — measured for
    Kiro, which spawns it with no prompt and no model involvement — which would be branch-controlled code
    running as the daemon user. Removals are logged. If your repo legitimately ships one, it will not apply
    inside an agent worktree; configure it for the daemon instead.
    Borrowed review snapshots keep those paths non-executable but do not hide the change from the reviewer:
    kcap supplies a private, read-only review-context tool containing the committed/staged **Git index**
    bytes. Unstaged and untracked MCP config is deliberately omitted and never read. The tool labels every
    returned path and content value as untrusted branch-authored evidence. A config too large to ship in
    full is declared to the reviewer by path, size and hash rather than failing the launch, so an oversized
    file can neither hide from the review nor block it. If kcap cannot extract, bound, or deliver that
    context safely, the borrowed reviewer fails closed instead of reporting a blind clean review.
  - **Git hooks are disabled for the creation commands** (`core.hooksPath=/dev/null`). With a relative
    `core.hooksPath` such as `.githooks`, the hook scripts are themselves branch content and git would run
    `post-checkout` during `worktree add`. This applies only to kcap's own creation commands; hooks in the
    agent's own later commits are unaffected.
  - **Clean/smudge filters are disabled for those same commands — all of them, including `lfs`.**
    `.gitattributes` is branch content and selects which filter driver applies, so a driver whose command is
    relative — `filter.x.smudge=./tools/f` — has the branch supply the executable. No command is inspected
    and no driver is exempt: four narrower designs were each defeated at their exemption, so there is
    deliberately nothing left to parse, resolve or impersonate. **No custom filter driver runs during the git
    commands kcap uses to create and populate a worktree** — that is the window in which branch content is
    first materialised, before an agent is running. The overrides are per-command, not persistent: git
    operations the agent itself runs inside the worktree afterwards use the repository's own configuration.
    What the disabling means for file contents depends on how the worktree is built:
    - *Owned worktrees* check out through git, so LFS-tracked files appear as pointer text.
    - *Standalone snapshots* copy the source directory's bytes and re-commit them with the clean filter
      disabled, so whatever the source already held — smudged content included — is what you get.
    - *Borrowed review snapshots* are rebuilt from the source working tree and carry its real content; they
      refuse to build if the source itself holds unsmudged LFS pointers.

    Disabled drivers are logged per worktree so the effect is visible rather than mysterious.

    Both the hook and the filter overrides are carried in git's environment (`GIT_CONFIG_COUNT` /
    `GIT_CONFIG_KEY_n`) rather than on the command line, so a config key that legally contains `=` — a filter
    driver named `evil=x` — stays intact instead of being cut at the `=` and left live. kcap measures at
    launch that the git it found honours those variables and refuses to build the worktree if not, rather
    than reporting containment it does not have: **creating an agent worktree needs git 2.31 or newer.**

- **Detach** without stopping the agent with the prefix key **`Ctrl-Q` then `d`**. The agent keeps running in the daemon.
- **Permissions:** for a registered agent, permission prompts appear in the web UI (the same dialog as hosted agents); with `--private`, prompts are answered natively in your terminal.

```bash
kcap agent                 # no subcommand — same as `kcap agent ls`
kcap agent ls              # list daemon-hosted agents (id, status, state, kind, repo, title)
kcap agent attach ab12     # re-attach your terminal (any unique id prefix works)
kcap agent resume ab12     # retry an agent a daemon restart left interrupted
kcap agent stop ab12       # graceful /exit, then terminate
kcap agent stop --all -y   # stop the listed non-review agents, no prompt
```

Agent ids are long, so `attach`, `stop` and `resume` accept **any unique prefix** — an ambiguous one lists the candidates instead of guessing. `stop --all` includes `--private` agents and prompts for confirmation unless you pass `--yes`/`-y`. It stops only the agents in the displayed list; agents started while you read the prompt remain running. Agents that finish before their stop request count as already stopped. The same snapshot rule applies with `--force` and `--yes`/`-y`. A stop that cannot be confirmed prints a per-agent failure line and exits non-zero.

**Daemon restarts.** A daemon restart — `kcap daemon restart`, a self-update, or `kcap daemon stop --keep-agents` followed by a start — stops the hosted agents and the new daemon resumes them under the same ids where the vendor session can be resumed; where it cannot, or the resume fails, kcap continue brings the agent back as a fresh session under a new id, which `kcap agent ls` shows. Claude, Codex, Pi, Muse, Antigravity, Cursor, Copilot and OpenCode agents resume their vendor session; Gemini, Grok and Kiro agents come back through kcap continue. A Claude review-flow reviewer in a daemon-owned worktree resumes the same way, under its id and with its earlier rounds, and one stopped mid-round (by `--force`, or once the turn wait runs out) gets its round again; a Codex app-server reviewer parks for its next round, and any other reviewer ends, so its flow starts a fresh one. `kcap agent ls` shows a `STATE` column: `running`, `resuming` while the new daemon brings an agent back, or `interrupted (<reason>)` when it could not; `kcap agent resume <id>` retries an interrupted one (refused, with the reason, while its previous process could not be proven gone or a newer daemon wrote its entry), `kcap agent stop <id>` discards it, and `stop --all` leaves them out; one left alone for 30 days is discarded at the next daemon start. An attached terminal rides out the restart: it prints `daemon restarting; waiting for <id> to resume…`, re-attaches by id once the agent is back (up to `KCAP_ATTACH_RECONNECT_SECONDS`, default 90) and replays its screen, or prints why the agent was not resumed and exits 1. The desktop app does the same: its rows show **Resuming** and **Interrupted** with the reason, and an open terminal tab re-attaches once the agent is back or says why it was not. A starting daemon resumes at most `KCAP_RESUME_CONCURRENCY` agents at once (default 2), leaves one last alive more than `KCAP_RESUME_MAX_AGE_SECONDS` ago (default 86400; 0 removes the bound) `interrupted (too_old)`, and counts a resume as failed when the agent does not stay up for `KCAP_RESUME_READINESS_SECONDS` (default 10). `kcap config set daemon.auto_resume false` turns the resume off.

**Agents that aren't yours.** `kcap agent ls` shows a `KIND` column: `agent` for ones you started, `review` for PR-review agents, and `review-flow` for review-flow participants (with their flow run ID and role). The daemon protects the latter two:

- `kcap agent attach` on a `review-flow` participant is **read-only** — it is driven by the flow protocol, so you see its output, your keystrokes are not delivered, and your terminal size is not applied to it. A `review` agent is a dialogue, so attaching to one is read-write.
- `kcap agent stop` on either is **refused** unless you pass `--force`, and `stop --all` skips them and says how many it skipped.

**Agents with no terminal.** Some hosted vendors (Antigravity's `agy`, and the ACP-backed ones — Cursor, Copilot, Kiro, Gemini) never produce terminal output at all: their stdout is protocol traffic, not a screen. `kcap agent attach` on one of those is **refused by name**, telling you which vendor it is and to drive the agent from the dashboard, rather than attaching you to a blank window that never repaints. They still appear in `kcap agent ls`, and `kcap agent stop` works on them normally.

Enforcement lives in the daemon, so a current CLI can't bypass it by skipping the check or lying about `--force`. That guarantee has two version-skew exceptions: an old `kcap` sends the legacy `Stop` request, which has no `--force` concept — the daemon treats that as `--force`, so an old client can silently force-stop a review or review-flow agent. And against an old daemon, `ls`/`attach` degrade silently (no kind reported, every agent reads as `agent`), but `stop` doesn't degrade — it sends a newer request format the old daemon can't decode, so the connection closes and the CLI tells you to restart the daemon instead of stopping anything.

`agent start` auto-starts the daemon if one isn't already running, and needs a configured server for the daemon to record to. `ls`, `attach`, and `stop` only talk to the local socket, so they work without one. A locally-started agent appears in **your own** web UI (owner-only until you share it from the web UI); use `--private` to opt out of registration entirely. Unix only for now.

### Repository paths

Manage known repo paths for the agent launch dialog. Repos are automatically added when agents are launched, but you can also manage the list manually:

```bash
kcap repos                    # list known repos (sorted by last used)
kcap repos add .              # add current directory
kcap repos add ~/dev/project  # add a specific path
kcap repos remove ~/dev/old   # remove a path
```

Known repos are persisted to `~/.config/kcap/repos.json` and reported to the server when the daemon connects, so the launch dialog always shows previously-used repos even after restarts. A running daemon watches the file and re-reports within a few seconds of an `add` or `remove`, so no restart is needed for the launch dialog to pick up the change. If `repos.json` exists but cannot be read, `add` and `remove` exit 1 and leave it untouched rather than overwrite the saved list; a file that reads but is not a valid list is kept beside it as `repos.json.corrupt-<timestamp>-<id>` and a fresh list is started.

### Projects

List and inspect projects — a Team/Enterprise-plan grouping of repos and members that sessions can be scoped to (see `default_visibility project` below).

```bash
kcap projects            # table: slug, name, repos, members, your role
kcap project <slug>      # metadata, repo list, member list, and (owner/admin) pending join requests
```

Requires the Team or Enterprise plan — the server 403s on Free with a message telling you so.

### Accounts

If you run more than one Claude Code config directory (`CLAUDE_CONFIG_DIR`) or Codex home (`CODEX_HOME`), for example one for work and one for personal use, kcap records each one as an *account*. An account is a vendor config directory, not a kcap server login: it is distinct from a profile (`kcap profile`), which selects a server.

```bash
kcap accounts                                  # list accounts, their recording state, and directories found but not added
kcap accounts add claude ~/.claude-work        # add a directory and install recording into it
kcap accounts rename ~/.claude-work Work       # change the label
kcap accounts rewire [<id|dir>]                # reinstall recording into one or every account
kcap accounts refresh [<id|dir>]               # ask the vendor who is signed in (and Codex's usage limits)
kcap accounts remove <id|dir>                  # remove kcap's recording and forget the account
```

`<id|dir>` is an account id prefix (at least 4 characters) or its directory. `kcap agent start <claude|codex> --account <id|dir>` starts a [local agent](#local-agents-kcap-agent) under one. `kcap setup` offers the directories it discovers in your home folder. `remove` leaves the directory and its login untouched. Codex asks you to trust the kcap hooks once per home, the next time you start it with that `CODEX_HOME`. For a Codex account, `add` and `rewire` also enable Codex sandbox network access for your server(s), as `kcap plugin install --codex` does; pass `--skip-codex-network-access` to leave that home's `config.toml` network keys untouched.

`refresh` asks each account's vendor CLI who is signed in to that directory: `claude auth status --json` with its `CLAUDE_CONFIG_DIR`, or `codex app-server` with its `CODEX_HOME` (`account/read`, plus `account/rateLimits/read` for the usage limits it records as the account's reading). kcap never reads the vendors' credential files. It records the sign-in state, email, organization and plan on the account, and an account still labelled with its directory name takes the email as its label (one you set with `rename` is kept). When the vendor reports a different login than the one recorded, the account's earlier usage reading is discarded. Each probe times out after 10 seconds; a failed step is recorded on the account and leaves what was known before. `refresh` prints one line per account and exits 1 when any step failed. `add` refreshes the account after wiring it; a refresh failure there is a warning and does not fail the add.

`add`, `remove`, `rename`, `rewire` and `refresh` accept `--json`: stdout then carries exactly one JSON line (`action`, `account_id`, `succeeded`, `steps` with `name`/`succeeded`/`detail`, `revision`, `error`) and errors still go to stderr. `revision` is the account registry revision the command committed after its wiring attempt (`null` when it committed nothing), so a reader that sees a snapshot at or past it knows the snapshot follows that attempt. `add` and `rewire` commit one even when wiring finds nothing to change, and so does every `refresh`; an `add`'s revision and steps include its refresh (an `identity` step, and a `limits` step for Codex), and an `add` whose wiring fails still registers the account and records the failure. A `remove` that cannot unwire leaves the account registered, records the failure on it and commits a revision too, since it may have unwired part of the account. The exit code is 0 only when `succeeded` is true. `list` ignores the flag.

#### Claude status line (`kcap statusline`)

Wiring a Claude account sets its `settings.json` `statusLine.command` to `kcap statusline --account <id>`, guarded so that a missing `kcap` still runs your own command. The guard needs a POSIX shell (Git Bash on Windows); without one, kcap writes the plain `kcap statusline --account <id>`, and a missing `kcap` leaves the status line blank. It is internal and not meant to be run by hand. On each status line update it reads the JSON Claude Code pipes in and, when that carries `rate_limits` (Pro and Max plans), records the account's 5-hour and 7-day usage in a local reading file beside the account registry. It then runs the status line command it replaced, through `sh -c` (PowerShell on Windows without Git Bash), with the same stdin, environment and working directory, and passes its output and exit code through; with no earlier command it prints nothing. It makes no network call, and a failure to record never changes the status line; recording runs before your command and gives up after at most 500 ms, so a stalled disk delays the status line by no more than that. kcap owns only `statusLine.command` and its `type`: `padding` and the other properties stay yours, and `kcap accounts remove` puts the original command back from the backup kcap saved when it wired the account. If that backup is missing, `remove` takes out kcap's `command` and `type` and reports the missing backup, leaving you to set your status line again. A project's own `statusLine` overrides the account's, so sessions in that project record no usage.

### Profiles

Profiles let you work with multiple kcap servers — for example, a company server for work repos and a separate one for open-source projects. Each profile stores its own server URL, visibility settings, and daemon configuration.

```bash
kcap profile add work --server-url https://my-other-tenant.kcap.ai
kcap profile add oss --server-url https://cap.oss.dev --remote "github.com/myorg/*"
kcap profile list
kcap profile show work
kcap profile remove work
```

`kcap profile remove` also deletes the profile's saved sign-in (`~/.config/kcap/tokens/<name>.json`). The active profile cannot be removed — select another with `kcap use <name> --global` first.

Discovery (`kcap setup`, `kcap login --discover`) saves each workspace under the profile that already points at its server — the active one when several do — so a profile you named yourself keeps its name. Where several workspaces share a server, only a profile named after one of them is reused, since kcap cannot tell which of the others belongs to which. A workspace no profile points at gets a profile named after its slug, with a numbered suffix if another workspace in the same sign-in already took that name.

The `--remote` flag associates a profile with git remote patterns. When you open a repo whose remote matches a pattern, that profile activates automatically.

#### Switching profiles

```bash
kcap use work                  # bind 'work' profile to current repo/directory
kcap use work --global         # set 'work' as the global default
kcap use oss --save            # bind and write .kcap.json for team sharing
```

Without `--global`, `use` binds the profile to the current git repo root (or the current directory if not in a repo). With `--save`, it writes a `.kcap.json` file that can be committed so the whole team uses the same profile.

#### Profile resolution order

The CLI resolves which profile to use in this order:

1. `--server-url` CLI flag
2. `KCAP_URL` environment variable
3. `KCAP_PROFILE` environment variable
4. `.kcap.json` in the repo root (or current directory if not in a repo)
5. Git remote pattern matching from `--remote` flags
6. Directory binding from `kcap use`
7. Global active profile (or `default`)

### Machine credentials (headless recording)

A **machine** records sessions where no person can sign in — CI runners, ephemeral
agent sandboxes, anywhere a browser login is impossible. It records like a user but
is never an administrator and never a member of a project.

Creating one requires the **owner or admin** role in your organization, and a server
with machine credentials enabled.

```bash
kcap machine create ci-runner            # create; prints the secret ONCE
kcap machine list                        # this org's machines
kcap machine revoke service:9e96…        # stop one authenticating
```

**The secret is shown once and is never stored** — not by this CLI, not by kcap,
and WorkOS will not show it again. That is deliberate: a secret nobody stores is a
secret nobody can leak. It goes to stdout while everything else goes to stderr, so it
can be piped straight into a secret store without touching disk:

```bash
kcap machine create ci-runner --visibility org_public \
  2>/dev/null | gh secret set KCAP_CLIENT_SECRET
```

The runner then needs both variables in its environment:

| Variable | |
|---|---|
| `KCAP_CLIENT_ID` | public — safe to commit |
| `KCAP_CLIENT_SECRET` | a secret — use your CI's secret store |

Finally, choose what its sessions are visible to, **on the machine itself**:

```bash
kcap config set default_visibility org_public
```

Visibility is the machine's own setting, exactly as it is for a person — a machine
records with the `default_visibility` of the profile it runs under, and with no
profile it records `org_public`. It is **not** steered to private. The `--visibility`
flag on `create` only selects the value printed in the setup instructions (defaulting
to your own profile's default, so `create` shows you the value your machine will
actually use) — it does not configure the runner for you.

> **Heads up:** `KCAP_CLIENT_ID`/`KCAP_CLIENT_SECRET` in your environment divert **all**
> of this CLI's auth onto the machine credential, so those variables belong on a runner,
> not in an interactive shell. `kcap status` prints a line naming them when it detects
> them, so a shell that has accidentally inherited them is easy to spot.

Revoking stops a machine authenticating from its next request. A token it already
holds stays valid until it expires (up to an hour) but is no longer honoured. To cut
it off at the source as well, delete the application in the WorkOS dashboard.

Run `kcap machine --help` for the full sequence.

### Configuration

```bash
kcap config show    # show current configuration
kcap config set <key> <value>
```

**Default session visibility** controls how your sessions appear to other users in the same kcap account. Set during `kcap setup` or change at any time:

```bash
kcap config set default_visibility private      # only you can see your sessions
kcap config set default_visibility project      # visible to the repo's project members (see `kcap projects`)
kcap config set default_visibility org_public   # org repos visible, others private (default)
kcap config set default_visibility public       # all sessions visible to others in your account
```

**Repository exclusions** prevent specific repos from sending any data to the server — hooks are silently skipped, no session is recorded:

```bash
kcap config set excluded_repos "myorg/secret-project,personal/diary"
```

**Repository allowances** are the same gate the other way round. Empty (the default) allows every repo; set it and only those repos are captured:

```bash
kcap config set allowed_repos "myorg/service,myorg/web"
kcap config set allowed_repos ""      # back to allowing every repo
```

Two consequences worth knowing before you set it. A session whose repo kcap cannot resolve is **not** captured — that covers detection failing or timing out, and also **any work outside a git repo at all**, so scratch directories stop being recorded. And if you set both `allowed_repos` and `allowed_paths`, they are independent gates: a session must be admitted by *both*, mirroring the way either denylist alone can exclude one.

**Path exclusions** silently skip any session whose working directory is, or sits inside, a configured path — useful for ignoring scratch dirs, worktrees, or monorepo subtrees regardless of git remote:

```bash
kcap ignore .                       # ignore the current directory
kcap ignore ~/code/secret-project   # ignore a specific tree
kcap ignore --list                  # show all ignored paths
kcap ignore --remove ~/code/secret-project
```

Entries are stored on the **active profile**, so switching profiles with `kcap use` switches the ignore list too. Symlinks are resolved on both the stored entry and the session's reported cwd, so a worktree symlink and its target match.

**Path allowances** turn that around. While the allow list is empty every path is capturable, which is the default. Add a root and kcap captures only sessions under it — useful when work and personal projects share a machine and you would rather name the few directories to record than chase the ones not to:

```bash
kcap allow ~/dev                    # capture only work under ~/dev
kcap allow --list                   # show all allowed paths
kcap allow --remove ~/dev           # removing the last entry re-admits everything
```

**Antigravity is the exception**, because its transcripts record no working directory. `kcap import` cannot place those sessions, so `excluded_paths` and `excluded_repos` never match them — and because an allow list does not admit what it cannot place, setting one skips every Antigravity session. Gemini records its workspace in the `<session_context>` block that opens a recording, so import scopes it like the rest; a recording that names none is skipped the same way an Antigravity one is. Live hooks are unaffected throughout: every harness scopes them from the working directory the running agent reports.

The two lists compose: the allow list decides what is capturable, and `kcap ignore` still subtracts within it. So `kcap allow ~/dev` with `kcap ignore ~/dev/client-x` records everything under `~/dev` except that one subtree.

One asymmetry is deliberate. A session whose working directory kcap cannot determine is **not** captured once an allow list is set, where with only an ignore list it would be — an allow list that admitted sessions it could not place would not be restricting anything. The same rule applies to `allowed_repos` above.

**Agents kcap runs for you are scoped too, but only the unattended ones.** A review flow's reviewer and a PR review run wherever the flow points them, with nobody choosing that directory, so the lists apply and a launch outside them is refused instead of reported. An agent you asked for is never gated this way — choosing the directory *is* the opt-in, so a launch from the UI or from `kcap` in a terminal runs where you told it to whatever the lists say.

**Provider API keys for headless calls.** Title generation, summaries, and judges shell out to `claude -p` / `codex exec` in the background. By default kcap scrubs `ANTHROPIC_API_KEY` and `OPENAI_API_KEY` from those spawns so your subscription login (claude.ai / ChatGPT account) is used — a globally-set key would otherwise override subscription auth and fail the call. If you intentionally authenticate via API key (PAYG), opt back in:

```bash
kcap config set use_provider_api_key true     # keep keys in headless spawns
KCAP_USE_PROVIDER_API_KEY=1 kcap recap …      # one-off override
```

`kcap setup` also prompts for this when it detects either key in the current environment. The env var (`1`/`true`/`yes`/`on` or `0`/`false`/`no`/`off`) wins over the profile setting.

#### Renamed repo directories (`kcap remap`)

Historic transcripts record the absolute working directory they ran in. If you've since renamed or moved that directory on disk (e.g. `~/dev/foo-cli → ~/dev/bar-cli`), `kcap import --org` / `--repo` can't resolve those sessions to a GitHub repo any more and silently drops them from the matched count.

Manage the rewrites with `kcap remap`:

```bash
kcap remap ~/dev/eventstore/foo-cli ~/dev/eventstore/bar-cli   # add or replace a mapping
kcap remap '~/dev/my-repo/worktrees/*' ~/dev/my-repo           # one rule for a family of worktrees
kcap remap --list                                              # show all mappings
kcap remap --remove ~/dev/eventstore/foo-cli                   # drop one
```

Entries are stored at the top of `~/.config/kcap/config.json` under `cwd_remap` (a top-level JSON array of `{ "from": ..., "to": ... }` objects) — you can also edit the file directly for bulk changes.

Semantics:

- `from` / `to` are **path-prefix** rewrites with `~` expanding to the current user's home directory (`~\` is also accepted on Windows). The match requires a path boundary (`from` exactly equal, or `from` followed by `/` — or `\` on Windows), so `from: "~/dev/foo"` will **not** spuriously rewrite `~/dev/foo-cli`.
- `from` may contain a single `*` standing as a **whole path segment**, matching exactly one segment of the cwd. It can sit anywhere in the path (`'~/dev/worktrees/*/my-repo'` works as well as a trailing one), and everything past it is preserved, so `'~/dev/my-repo/worktrees/*' → ~/dev/my-repo` rewrites `~/dev/my-repo/worktrees/ai-2441/src` to `~/dev/my-repo/src`. There is no `**`. Quote the pattern — an unquoted `*` is expanded by your shell before kcap sees it.
- Comparisons follow the host filesystem's case policy: case-insensitive on Windows, case-sensitive elsewhere.
- When multiple rules could apply to the same transcript cwd, the one matching the **most** of it wins, and a literal `from` beats a wildcard `from` reaching just as far — so a single path can be pointed elsewhere without dropping the family rule.
- Rules are applied once (no chaining), so the result of one rule isn't fed into another.
- Rules are **unconditional** — they rewrite whether or not the original path still exists. Harmless for a worktree pattern, since a live worktree rewritten to its project root resolves to the same repository, but worth knowing before pointing one at an unrelated directory.
- `--list` and `--remove` take the pattern verbatim, exactly as typed.
- Remaps are global, not per-profile — same rename affects all profiles' imports.

After adding a remap, re-run `kcap import --org` (or whichever scope you use). The missing-cwd report at the top of the import will show what's still unresolved. Ephemeral worktree paths under `<project>/.<anything>/worktrees/<slug>` are auto-attributed to `<project>` when it still exists on disk, so deleted-worktree cwds don't need a remap entry — a `worktrees/` directory you configured yourself carries no dot segment, so that one does, and the missing-cwd report prints the wildcard rule for it when it sees more than one dead sibling under a repository that still exists:

```
17 sessions under ~/dev/my-repo/worktrees/ (4 paths) belong to ~/dev/my-repo, which still exists:
  kcap remap '~/dev/my-repo/worktrees/*' ~/dev/my-repo
```

Worktrees kept *outside* the project (`~/dev/worktrees/<project>/<tree>`) can't be suggested — the directory above them is not a repository, so only you know which project they belong to — but the same wildcard covers them: `kcap remap '~/dev/worktrees/my-repo/*' ~/dev/my-repo`.

### Telemetry

kcap reports pseudonymous usage data so we can see which commands people use and where setup goes wrong. It records **command and flag names, exit codes, durations, MCP tool names, and setup-funnel steps.** It never records argument values, file paths, repo names or URLs, session ids, transcript content, environment variable values, usernames, or email addresses.

What identifies an installation, stated plainly: a random device id generated once and stored on this machine, so events can be tied to one install over time without naming a person. For a hosted `*.kcap.ai` workspace, every event additionally carries that workspace's slug — as an `org` property and an `organization` group — so usage rolls up per workspace; self-hosted installs never send it, and the slug names a workspace, not a user.

**Signing in also links this install to your browser.** `kcap setup` and `kcap login` mint a `join_id` — a random, single-use key held in memory for that one run and never written to disk — and stamp it on that run's events and on the two signup requests. After the browser finishes signing you in, the closing page navigates once through `kurrent.io` and back, which lets us pair that key with your website analytics identity and with the workspace the run created. What comes back and is stored on CLI events is `web_device_id_capacitor` / `web_device_id_www` — the site's own random analytics ids, never an email address, name, or URL — plus `site_variant`. Together these mean CLI usage **can be associated with a workspace and with the person who created it**, which is why this data is pseudonymous rather than anonymous. Opting out suppresses all of it: no key, no navigation, no extra properties, and the post-login page behaves exactly as it did before.

The first time you run a reportable command, kcap prints a one-time notice to stderr; it never prints again on that machine.

Turn it off in any of three ways:

```bash
kcap config set telemetry off   # persisted, machine-wide
export KCAP_TELEMETRY=0         # this shell only
export DO_NOT_TRACK=1           # the cross-tool convention, honoured by kcap too
```

`telemetry` is deliberately machine-wide rather than per-profile: consent is a property of the machine, and having it flip when you switch profiles would be surprising.

Precedence, highest first: `KCAP_TELEMETRY` > `DO_NOT_TRACK` > the persisted `telemetry` setting > on by default. `KCAP_TELEMETRY` wins over `DO_NOT_TRACK` **in both directions** — it's the kcap-specific, deliberate statement, and the only way to opt back in on a machine whose shell profile sets a blanket `DO_NOT_TRACK`:

```bash
DO_NOT_TRACK=1 KCAP_TELEMETRY=1 kcap status   # reports anyway — the explicit setting wins
```

`kcap config show` reports the effective state and which setting decided it, e.g. `Telemetry: off (source: DO_NOT_TRACK)`.

### Uninstalling

To remove kcap from this machine, run:

```bash
kcap uninstall                  # interactive, user-scope removal
kcap uninstall --yes            # non-interactive
kcap uninstall --yes --purge-local  # non-interactive, deleting a local server's sessions
kcap uninstall --yes --force    # also end a daemon that is running agents
kcap uninstall --project --yes  # also strip project-scope hooks in cwd's repo
kcap uninstall --keep-config    # remove integrations, keep ~/.config/kcap
```

When a local server is installed (`kcap setup --local`), `uninstall` removes it first, as `kcap local uninstall --purge` does: its service, binaries, data and logs go, with every session it recorded. The prompt names the server and the deletion; `--yes` alone is not consent to it, so a non-interactive run refuses unless you pass `--purge-local` (or `--keep-config`, which removes the server but keeps its data). An interrupted purge needs `--purge-local` even with `--keep-config`: recovering it finishes deleting the data. If the local server cannot be removed, or its registry cannot be read, the local server is left as it is and `~/.config/kcap/` is kept; integrations are still removed, and the uninstall is reported incomplete so a re-run can finish once the registry is repaired.

When a local server was ever set up on this machine, cleanup holds its lifecycle lock through integration and configuration removal, and the local `install.json` retired-URL registry and `lifecycle.lock` remain as safety metadata; they contain no recordings. This stops another setup from starting during cleanup, and stops stale profiles from recording to a retired URL after a reinstall. A machine where no local setup ever started has no such metadata, and `~/.config/kcap/` is deleted whole. A local installation that appears after you confirm, or replaces the one you confirmed, stops the uninstall with nothing removed, `--keep-config` included; re-run to review it. Only `--purge-local` covers it.

Uninstall also removes Mistral Vibe hooks and MCP entries, kcap's `~/.cache/kcap` cache, legacy Cursor pending recordings, and retired `kcap-*` skills in Kiro, Kiro Crew and Antigravity skill trees. Malformed Antigravity hooks, unreadable Kiro settings and a global git config that git cannot edit are reported as incomplete cleanup; retry information is kept. Without a git that runs, the git hook is left in place and not counted as a failure, since nothing reads it. The macOS app removes its per-app Velopack update cache, scratch files and logs alongside its other support files.

`uninstall` also removes the `kcap` command links on your PATH that point at this install (macOS and Linux). A link it cannot delete, such as one in a root-owned folder, is reported with a `sudo rm <path>` command to run yourself, and the uninstall counts as incomplete. It also removes the marked PATH line kcap added to your shell profile, unless a `kcap` link that belongs to another install is still in `~/.local/bin`. A modified, unreadable, or busy profile is reported as incomplete cleanup and left for retry or manual review.

`uninstall` covers every supported agent: it stops running daemons and watcher processes, strips kcap entries from user-level Claude Code, Codex CLI, Cursor, and Copilot CLI hook files (preserving any non-kcap entries), deletes the Pi extensions (`~/.pi/agent/extensions/kcap.ts` + the `kcap-mcp.ts` bridge) and strips kcap's block from `~/.pi/agent/AGENTS.md`, deletes the OpenCode plugin (`~/.config/opencode/plugins/kcap.ts`), removes the kcap capture plugin from Antigravity's `~/.gemini/config/plugins/kcap/`, removes agent skills under `~/.agents/skills/` (plus the legacy `~/.codex/skills/kcap-*` folders), removes the `kcap` git hook from `~/.gitconfig`, and removes configuration from `~/.config/kcap/`, retaining local lifecycle safety metadata when a local server was ever set up. Every registered vendor account (see `kcap accounts`) is unwired too, and the account registry (`~/.config/kcap/accounts/`) and the daemon state directory (`~/.config/kcap/daemons/`, which `KCAP_CONFIG_DIR` never moves; kept whole while a daemon or daemon command still holds one of its locks, and left alone when `KCAP_DAEMONS_DIR` moved it or it is a link) are deleted last, only when no step failed — neither `--keep-config` nor `--project` spares them. A registry kcap could not read or lock is kept and the uninstall reported incomplete, since it is the only record of which accounts still record; check them with `kcap accounts`, then re-run `kcap uninstall`.

`--project` additionally cleans up `<repo>/.claude/settings.local.json` and `<repo>/.codex/hooks.json` in the current git working tree (errors if you're not inside one). Cursor only has a user-scope `hooks.json`, so `--project` does not affect it. Project-scope hooks in other repos are not touched — re-run from each repo that has them.

Restart coding agents that were open during uninstall to reload their hook configuration. An already-running agent may keep invoking a loaded `kcap` hook after the executable has been removed and report a command-not-found error (exit code 127). If that continues after restarting, check for project-scoped hooks in the repository where the agent is running.

`uninstall` refuses, changing nothing, while a daemon is running or starting agents; stop them, or pass `--force` to end them. Use `--keep-config` to preserve profiles, tokens, and ignore lists when you plan to reinstall. Per-agent selective cleanup is not exposed here — use `kcap plugin remove [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--mistral-vibe|--grok|--muse|--skills]` for finer-grained removal.

### New-harness detection

`kcap setup` only sees the coding agents installed **at the time you run it**. If you install
another agent later — say you set kcap up while using Claude Code, then later add Antigravity —
kcap notices and offers to wire that agent in too (hooks, skills, MCP), so sessions there start
being recorded. Detection is filesystem-cheap and check-on-occasion (no watcher), and kcap asks
about a given agent at most once a week and remembers a dismissal, so it never nags.

You'll see the offer on whichever surface reaches you:

- **Inside a session** in an already-configured agent, kcap tells that agent to offer to run the
  install for you.
- **On the command line**, running an interactive `kcap` command prints a one-line notice to
  stderr (never in scripts or pipes), like the "update available" notice.
- **`kcap status`** always lists any installed-but-unconfigured agent with the command to fix it —
  even one you've dismissed (status tells the whole truth).
- **`kcap status`** also shows whether each wired agent's hooks are actually running: when it last
  ran a kcap hook on this machine, or that it has not yet (start a new session; Codex runs no hook
  until you trust kcap's). A hook running proves the integration loaded, not that a session reached
  the server. `--json` carries the time as `last_hook_event`, and a git older than 2.54,
  which ignores the commit hook, gets its own warning line.
- **`kcap status`** shows each running daemon's version, and warns, naming the fix, when a daemon
  runs a different version than the CLI or its service runs a binary outside the CLI's own install
  (one `kcap update` does not reach, such as an older npm install's). `--json` carries it as each
  daemon's `version`.

Manage the nudges with `kcap harness`:

```bash
kcap harness list                     # detected / kcap-wired / dismissed, per agent
kcap harness list --json              # the same report, machine-readable
kcap harness dismiss antigravity      # stop asking about one agent
kcap harness dismiss --all            # stop asking about every currently-detected agent
kcap harness reset antigravity        # ask again (undo a dismissal)
```

`list --json` emits one JSON document on stdout and nothing else, so it can be piped — the same
contract as [`kcap import --discover --json`](#loading-historical-sessions). Every harness this build
knows is listed, present or not, so a consumer can tell "unsupported" from "not on this machine"
without carrying its own vendor list:

```json
{"harnesses":[{"vendor":"claude","label":"Claude Code","binary_on_path":true,
               "config_found":false,"wired":false,"desktop_app":false,"dismissed":false}]}
```

`vendor` is the stable key — the id `dismiss` and `reset` take. The detection signals stay
apart: `binary_on_path` is the vendor's CLI on your search path, `config_found` files it writes on
disk, `desktop_app` its desktop app or the data that app keeps, and any one means installed. A tool choosing which agents to set up wants each,
since it can then say which signal it saw; one that only needs "is it here" ORs them.

To turn off the nudges entirely (both the in-session and command-line surfaces), set
`kcap config set disable_harness_nudge true`. Dismissing is per-agent; a brand-new agent installed
after a `--all` dismiss is still offered once.

### Other commands

```bash
kcap status         # server, auth, hooks (and when each agent last ran one), daemon and its version
kcap status --json  # the same report, machine-readable
kcap whoami         # show current identity + ask the server if it accepts your token
kcap login          # authenticate via OAuth (browser flow by default)
kcap login --device # skip the browser, sign in with a device code instead
kcap update         # upgrade the CLI and refresh agent plugins (npm-global and script installs)
kcap update --beta  # switch to the beta channel and update to the latest beta
kcap update --stable # switch back to the stable channel (the default)
kcap refresh        # re-run the agent-integration refresh an install or update runs
kcap logout         # delete stored tokens
kcap feedback --bug -m "the daemon crashed on stop"   # file a bug report
kcap feedback --feedback                               # send feedback; prompts for the message on a TTY
```

> `kcap feedback (--bug | --feedback) [-m|--message <text>]` files a report through the
> server's support-intake pipeline (when the tenant has it configured). Exactly one of
> `--bug`/`--feedback` is required. `-m`/`--message` is required when stdin isn't a TTY
> (scripts, CI); on an interactive terminal without it, the command prompts for a
> multi-line message ended by an empty line. On success, it prints:
> `✓ Sent to Kurrent support as {email} — replies will reach you by email.`
> Against the local server, which has no support intake, it opens a prefilled email to
> team@kcap.ai in your mail app and copies the full report to your clipboard; a report too
> long for a mail link is cut short in the draft. When the clipboard copy fails it also prints
> the report, and when no mail handler could be launched either it exits 1.

> `kcap update` is the one-step upgrade for npm-global installs: it checks the
> registry, runs `npm install -g @kurrent/kcap@<tag>`, then refreshes your
> opted-in agent plugins — so it picks up new skills/hooks even when your package
> manager blocks install scripts. It exits early if you're already up to date,
> and tells you what to run instead for other installs (e.g. Homebrew). On the
> stable channel it stops at your connected server's version when the server
> trails npm, so it never installs a CLI newer than the server it talks to; a
> [local server](#upgrading-the-local-server) does not cap it, and after the CLI
> moves `kcap update` runs the new binary's `kcap local upgrade` to move the local
> stack with it. Use `kcap update --check` for a machine-readable
> `{current, latest, newer}` probe.
>
> **Script installs** (`curl -fsSL https://www.kurrent.io/install | bash`, or
> `install.ps1` on Windows) update without npm: `kcap update` reads the
> installer's channel manifest, downloads this platform's archive from GitHub
> Releases and checks its sha256 against the manifest, installing nothing on a
> mismatch. Each release gets its own `versions/<version>` directory and the
> `current` link is switched to it, so running processes keep the files they
> started from; then the new binary runs `kcap refresh`. The version it replaced
> stays; every other version is deleted unless something still runs from it.
> macOS cannot tell, so there it is deleted anyway, and a process already running
> from it keeps working. A daemon started from a script install restarts on the new version once it is idle,
> Windows included, provided it was started on a release that watches for updates
> there; restart an older Windows daemon once with `kcap daemon restart`. A daemon service
> installed from a script install is moved onto the `current` path, so it
> follows updates. Switching from npm, a daemon service installed under npm keeps
> running the npm binary: run `kcap daemon service install` again once the
> script install is on your `PATH`.
>
> **The snap** ships `kcap` with the desktop app, and snapd keeps both up to date:
> there `kcap update` changes nothing and names `sudo snap refresh kcap` to update
> now, and `kcap update --check` reports `newer: false` with `install_tag: "snap"`.
> A snap installed from a file (`snap install --dangerous`) is never refreshed by
> snapd, so `kcap update` points it at a newer `.snap` file instead.
> The paths kcap writes — the daemon service unit, MCP registrations, git hooks
> and the Claude Code plugin — name `/snap/kcap/current/…`, so they follow each
> refresh.
>
> `kcap refresh` is the agent-integration refresh every installer runs: skills,
> per-vendor hooks and plugins, the Claude Code plugin and installed daemon
> service units, each only if you installed it before. Run it yourself when an
> update reports a step that failed.
>
> **Windows:** the update works even while Claude Code sessions (whose kcap MCP
> servers keep the binary locked) or the daemon are running — the old executable
> is moved aside so npm can replace it, and the leftover is cleaned up
> automatically later. Running processes keep the old version until they
> restart; a running daemon reports this and applies the new version on
> `kcap daemon restart`. A plain `npm install -g` / `npm update -g` does **not**
> do this and fails with `EBUSY` while any kcap process is running — prefer
> `kcap update`.
>
> **Beta channel (opt-in):** `kcap update --beta` switches the active profile to
> the beta release channel (npm dist-tag `beta`) and updates to the latest beta
> immediately. The choice is **persisted per profile**, so subsequent `kcap
> update` runs — and the passive stderr "update available" hint — keep tracking
> beta until you run `kcap update --stable`. A fresh config defaults to the
> **stable** channel (npm dist-tag `latest`); beta is strictly opt-in. Beta
> releases correspond to server versions rolled out to internal tenants first,
> so most users should stay on stable.
>
> **Staying current, and turning it all off.** Every human-facing `kcap`
> command prints an "Update available" notice after it finishes if a newer
> version exists, and `kcap status`'s **Version** line shows the same thing
> inline. Every request `kcap` sends also carries its version (and, if update
> checks are off, an explicit opt-out marker) to your server, which is how the
> web dashboard's own out-of-date banner and notification-centre entry — and
> the in-agent nudge Claude Code sessions can see (above) — know to show up.
> `kcap config set update_check false` is the full, persisted opt-out: it
> disables the notice and the `kcap status` annotation, makes the server
> suppress its own banner/notification for you via the transmitted opt-out
> marker (the version is still sent — the opt-out is signaled, not omitted),
> and drops the in-agent nudge — everywhere, until you turn it back on.
> `--no-update-check` is narrower and one-shot: it
> only suppresses the notice and the `kcap status` annotation for that single
> invocation — it doesn't change what gets sent to the server, so the banner,
> notification, and in-agent nudge keep following whatever `update_check` is
> persisted to. It defaults to on.

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kcap` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kcap config set server_url https://my-tenant.kcap.ai

Or remove the config file and re-run setup:

    rm ~/.config/kcap/config.json
    kcap setup

## Troubleshooting

### Sessions are not recorded

**Restart your coding agent for live recording to begin.** Hooks only load at session start, so a session that was already running when you ran setup keeps running without them and won't stream live. Start a new session (or `claude --continue`) to pick the hooks up — setup prints this reminder when it installs any hooks. A manual `kcap import` of the in-progress session only yields a frozen snapshot.

**Need at least one agent to capture sessions:** the setup wizard runs to completion without a coding agent installed (it still configures your profile, auth, and daemon), but kcap only records work once a [supported agent](#requirements) is installed and kcap is installed into it — re-run `kcap setup`, or `kcap plugin install` for that agent.

After installing Codex hooks, the next `codex` launch asks you to trust them; until you do, they are installed but inert. See [After setup](#after-setup).

### Recording stopped, or checking your connection

Verify with `kcap whoami` and `kcap status`. `kcap whoami` prints your identity and the profile it
resolved, then asks the server whether it actually accepts your token — it exits non-zero if the
server rejects it, or if the token was issued by a different server than the profile now targets
(re-run `kcap login`). It exits 2 if no workspace answers at the configured address at all, as
after the workspace is removed (re-run `kcap setup`). If the server can't be reached it says so and
still exits 0, so it stays usable offline. If the server rejects your token while a session is running, Claude Code's hook
says so as an in-session notice — `[kcap] The server rejected your credentials (HTTP 401) —
session recording is paused. Run 'kcap login' to resume.` — instead of surfacing an opaque hook
error, so you need not run `kcap whoami` to work out why recording stopped. The lifecycle
event that hit the rejection is not lost: it is spooled like an outage and re-sent on the next hook
once you have logged in. Other agents' hooks print the same advice to stderr instead of an
in-session notice, since not every agent surfaces hook output in its UI. `kcap status` prints its own
**Version** line — the installed CLI version, with an inline `(update available: …)` annotation
when a newer one is out (capped at your connected server's version, marked `(…, server version)` when
your tenant trails npm) — see [`kcap update`](#other-commands) for the full opt-out story.

### Commits land on the wrong session or pull request

kcap files each commit under the agent session that made it through a git config hook, which needs git 2.54 or later. Setup installs the hook without checking your git version, and an older git ignores it without a word: commits are then read from the agent's shell commands, so one made from a subfolder, a nested repo or a script can be missed or misfiled. Check `git --version` and upgrade git; `git hook list post-commit` lists `kcap` once the hook is active.

### Setup does not find your workspace

SSO discovery lists only workspaces that use org SSO. A workspace whose members sign in with the GitHub App shows up as "no tenant": choose **I already have a workspace**, re-run with `--github`, or name it with `kcap setup <tenant>`.

### npm installs and upgrades

A script install upgrades with `kcap update` or by running the install script again; either refreshes your agent integrations. The rest of this section is about npm.

**npm 11+ blocks install scripts by default.** You'll see a warning like
`1 package has install scripts not yet covered by allowScripts`. The `kcap`
binary works without the script; it only refreshes already-installed agent
plugins (Claude / Codex / Cursor / Copilot / Gemini / Kiro / Pi) on upgrade. The warning suggests
`npm approve-scripts @kurrent/kcap`, but that command rejects global installs
(`EGLOBAL`) — a known npm UX bug. Instead, opt in one of two ways:

```bash
# one-off
npm install -g @kurrent/kcap --allow-scripts=@kurrent/kcap
```

Or persistent — add this to `~/.npmrc` so every future `npm install -g`
runs postinstall automatically:

```
allow-scripts[]=@kurrent/kcap
```

Without either, upgrade with **`kcap update`** instead of `npm install -g` — it
runs the global npm upgrade and then refreshes your agent plugins itself, so it
works regardless of the install-script gate. (You can also re-run `kcap plugin
install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--mistral-vibe|--grok|--muse|--skills] --if-installed` manually.)

**Internal-tenant testers:** opt into pre-release builds with `kcap update
--beta`; everyone else should stay on the default stable channel. See
[`kcap update`](#other-commands).

### The daemon stops

**Keep the daemon running:** `kcap daemon start -d` stops when the process dies (a crash, or an OS memory-pressure kill — macOS jetsam / Linux OOM). To auto-restart it and start it at login, install it as a per-user service: `kcap daemon service install`. See [Daemon](#daemon).

### Sessions are not tagged with a pull request

**PR/MR auto-tagging is best-effort:** sessions on a branch with an open pull/merge request are automatically tagged with it, using the provider's own CLI — `gh` for GitHub and GitHub Enterprise, `glab` for GitLab. Neither is required to use kcap; if the matching CLI isn't installed or authenticated for the repo's host, the session is simply left untagged (no error, no retry).

## License

[Kurrent License v1](LICENSE.md)
