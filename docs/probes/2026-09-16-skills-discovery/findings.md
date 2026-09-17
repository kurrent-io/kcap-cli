# Repo-local skill discovery probes — 2026-09-16

**Subject:** Claude Code 2.1.273 (native installer, `~/.local/bin/claude`), codex-cli 0.154.0 (brew cask),
Gemini CLI 0.60.0 (npm `@google/gemini-cli`), Pi 0.85.1 (npm `@earendil-works/pi-coding-agent`),
GitHub Copilot CLI 1.0.85 (npm `@github/copilot`), kiro-cli 2.21.4 (brew cask), OpenCode 1.18.31 (npm
`opencode-ai`), OpenCode 2.0.4 (npm `@opencode/cli`, prefix `~/.local/opencode-v2`), cursor-agent
2026.09.15-d2fe57e (vendor installer), agy 1.2.4 (vendor installer). macOS 26.6.2 / arm64.
**Driver:** `probe.py` (scenarios S0–S4, two runs per arm, a third on disagreement, one single-run
confirmation per root the multi prompt missed); `report.py` renders `capability-matrix.md` from
`matrix.json`; `selftest.py` covers the kit with no vendor binary.
**Cost:** the free phase issues zero model requests. The recorded matrix holds 803 model turns across
both passes; re-measurements while adapters were being corrected added roughly a hundred more.

Every verdict below is the starting session's own reply. A skill counts as *discovered* when the model
names it and as *loaded* when the reply carries the token that exists only in the skill body. A file on
disk, a hook exit code or a listing command never decides a row. Print mode is the vendor's headless
single-prompt launch; daemon mode is the launch kcap's daemon uses (ACP for Copilot, Gemini, Kiro,
Cursor and OpenCode, `codex app-server`, `pi --mode rpc`); interactive launches drive the vendor's own
UI on a pseudo-terminal and are measured in pass 2, below.

## How to reproduce

```
python3 docs/probes/2026-09-16-skills-discovery/selftest.py          # kit self-tests, no vendors
python3 docs/probes/2026-09-16-skills-discovery/probe.py --harness claude --mode print          # free phase
python3 docs/probes/2026-09-16-skills-discovery/probe.py --harness claude --mode print --turn   # full sweep
python3 docs/probes/2026-09-16-skills-discovery/probe.py --emit && python3 docs/probes/2026-09-16-skills-discovery/report.py
```

A sweep resumes: arms with settled rows are not re-spent, partial arms are topped up, and an arm with an
`untested` row is measured again. `--rerun` clears an entry's arms first. Run one vendor at a time:
two concurrent `cursor-agent` processes made its print mode fail mid-turn. Environment switches:
`KCAP_PROBE_CLAUDE_REAL_CONFIG=0|1` forces the Claude config-root mode, `GEMINI_API_KEY` makes Gemini
runnable on an account whose OAuth tier is refused, `KIRO_API_KEY` is passed through for Kiro,
`KCAP_PROBE_OPENCODE_MODEL` pins the OpenCode model, `KCAP_OPENCODE_V2_PATH` names the V2 binary.
Logins that open a browser are the operator's: `cursor-agent login`, `kiro-cli login`,
`opencode auth login`, `agy` (first launch).

Every run leaves `out/<entry>/<mode>/<scenario>/<arm>/run<N>.json` (the record), `run<N>.raw.txt`
(the vendor's full event stream) and `run<N>.<name>.stderr.log`; `out/` is git-ignored, `matrix.json`
is the committed roll-up.

Three entries cannot be isolated in a private home because their login lives in the OS keyring keyed by
the real one: Claude (config directory), Cursor and agy (HOME). They run against the real home with
`KCAP_SKIP=1` so kcap's own hooks stand down, with the probe hook placed in the sandbox repository
(project-level `.cursor/hooks.json`, workspace `.agents/plugins/probe/`) or supplied by flag
(`--settings`); the two variants that must write into the real home (`cursor-userhooks`,
`agy-clidir`) back the file up and restore it after the turn.

## Claude Code 2.1.273 (print mode; daemon launches use the same `-p`)

- **Isolation.** The OAuth login lives in the macOS keychain keyed by the config root: an isolated
  `CLAUDE_CONFIG_DIR` reports `loggedIn: false`, and so does naming the default root explicitly. The
  adapter runs against the real config root with `CLAUDE_CONFIG_DIR` unset, supplies the probe hook
  through `--settings <file>`, limits sources with `--setting-sources project` so no user-level plugin
  loads, and sets `KCAP_SKIP=1`. `--allowedTools Skill` leaves the model no way to read files, so a
  token can only arrive through the Skill tool.
- **S1** `visible_first_turn` ×2.
- **S2** `hook-creates-root` `not_visible` ×2 and `hook-adds-skill` `not_visible` ×2. The SessionStart
  hook fired about 0.7 s after launch and the file existed when the reply was written; the model answered
  `NO-SKILL`, and with a pre-existing sibling it listed only that sibling. Skills are indexed before
  SessionStart hooks run, and the documented watched-root refresh does not reach the first request.
- **S3** both exclusions `visible_first_turn` ×2; `git status` stays clean under both.
- **S4** consumes `.claude/skills` only.
- **Limitation.** No registration or reload mechanism for a headless launch. `.claude/skills` is also
  read by Copilot, Cursor and OpenCode, so it is not a vendor-isolated destination.

## Codex 0.154.0 (print `codex exec`; daemon `codex app-server`)

- **Isolation.** `CODEX_HOME` with `auth.json` copied keeps the ChatGPT login; `config.toml` carries a
  `[projects."<repo>"] trust_level = "trusted"` entry. `exec` runs with
  `--dangerously-bypass-hook-trust`; the app-server driver reads `hooks/list` (hooks are grouped under
  `result.data[].hooks`) and restarts with `-c hooks.state={…trusted_hash…}` to seed trust, as kcap's
  daemon does.
- **Native load is a file read.** Codex lists a skill with its path and the model runs
  `cat <path>/SKILL.md`. A prompt forbidding "any tool" made Codex answer `NO-SKILL` for a listed skill,
  so the prompt forbids searching instead and the adapter records `skill_reads` apart from `searches`.
- **S1** `visible_first_turn` ×2 in both modes, `skill_reads=1 searches=0`.
- **S2** both hook arms `not_visible` ×2 in both modes; the hook fired (`hook_trust=seeded` in daemon
  mode) and the file existed, the first request did not list it.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.agents/skills` and repo-local `.codex/skills`; the latter is read by Cursor too, so
  Codex has no vendor-isolated destination either.

## Pi 0.85.1 (print `pi -p --mode json`; daemon `pi --mode rpc`)

- **Isolation.** `PI_CODING_AGENT_DIR` with `auth.json`, `models.json`, `settings.json` copied; auth
  through the `openai-codex` provider. `--approve` trusts the sandbox repo's project resources. The
  native load is a `read` of the listed SKILL.md path (`skill_reads`).
- **S1** `visible_first_turn` ×2 in both modes.
- **S2** `hook-creates-root` and `hook-adds-skill` (an extension whose `session_start` runs the hook
  script) `not_visible` ×2 in both modes. **`registration` `visible_after_reload` ×2 in both modes:** an
  extension that writes the skill in `session_start` and returns the skills root from
  `resources_discover` (`{skillPaths: [<root>]}`) makes the skill part of the first request.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.pi/skills` and `.agents/skills`; `.pi/skills` is Pi's vendor-isolated destination.

## GitHub Copilot CLI 1.0.85 (print `copilot -p`; daemon `copilot --acp --stdio`)

- **Isolation.** `COPILOT_HOME` with `COPILOT_GITHUB_TOKEN` from `gh auth token` (an OAuth `gho_`
  token). The native load is Copilot's own `skill` tool (`skill_loads`).
- **S1** `visible_first_turn` ×2 in both modes; `copilot skill list --json` lists the probe skill too.
- **S2** both hook arms `not_visible` ×2 in both modes. The `sessionStart` hook in
  `$COPILOT_HOME/hooks/*.json` fired about 3 s after launch; the skill was not in the first request.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.github/skills`, `.agents/skills` and `.claude/skills`, all documented.
  `.github/skills` is Copilot's vendor-isolated destination.
- **Limitation.** `/skills reload` is interactive only; no headless reload or registration path.

## Kiro CLI 2.21.4 (print `kiro-cli chat --no-interactive`; daemon `kiro-cli acp`)

- **Isolation.** `KIRO_HOME`; the IAM Identity Center login lives outside it and survives. The brew cask
  ships the launcher only: `kiro-cli-chat` and `kiro-cli-term` had to be linked from the app bundle
  into `~/.local/bin`. This build is the 2.x hook generation, so the hook is `hooks.agentSpawn` inside a
  custom agent that is made the default; that agent must be cloned from `kiro_default`
  (`kiro-cli agent create <name> --from kiro_default`, as kcap's installer does), because a minimal agent
  definition has no tools and the model then cannot read the listed file ("I don't have the ability to
  actually invoke tools"). The native load is a `read` of the listed SKILL.md path.
- **S1** `visible_first_turn` ×2 in both modes.
- **S2 `hook-creates-root` and `hook-adds-skill` `visible_first_turn` ×2 in both modes.** `agentSpawn`
  fires when the session is created, about 2 s before the first prompt in ACP mode, and the skill
  catalogue is built afterwards: a plain file drop from the startup hook reaches the first request.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.kiro/skills` only; it is Kiro's vendor-isolated destination.
- **Custom agents.** `kiro-agent-bare` (cloned default agent with `resources: []`) and
  `kiro-agent-skills` (cloned agent declaring `skill://.kiro/skills/*/SKILL.md`) both `visible_first_turn`
  ×2 in both modes: a cloned agent inherits the default skills whether or not it declares them.
- **Headless without a login** opens a browser sign-in instead of answering; the adapter checks
  `kiro-cli whoami` first.

## OpenCode 1.18.31 (print `opencode run --format json`; daemon `opencode acp`)

- **Isolation.** `OPENCODE_CONFIG_DIR` plus `XDG_DATA_HOME` pointed at the sandbox, with
  `~/.local/share/opencode/auth.json` (the `github-copilot` provider) copied. The native load is the
  `skill` tool (`skill_loads`); in ACP mode the tool call is titled `Loaded skill: <name>`.
- **S1** `visible_first_turn` ×2 in both modes.
- **S2 print:** `hook-adds-skill` **`visible_first_turn` ×2** — a skill written by the plugin's
  `session.created` handler into an existing root is in the first request; `hook-creates-root`
  `not_visible` ×2 — a root created by the same handler is not; `registration`
  (`experimental.chat.system.transform` running the script) `not_visible` ×2.
- **S2 daemon:** all three arms `not_visible` ×2 (one run timed out and was re-measured). The ACP session
  builds its catalogue before the plugin's write lands.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.opencode/skills`, `.agents/skills` and `.claude/skills`, all documented.

## OpenCode 2.0.4 (print `opencode run --standalone`; daemon `opencode acp`)

- **Launch.** `--standalone` is accepted only after `run` (before it, the CLI prints its usage; the kit
  treats printed usage as a failed run) and not at all for `acp`, which uses the background service; the
  adapter stops that service after every turn. Auth and config isolation as for 1.x.
- **S1** `visible_first_turn` ×2 in both modes through the `skill` tool.
- **S2** untested: no local plugin placement loaded in this build — a `Plugin.define` module in the
  config directory's `plugins/`, in the project's `.opencode/plugins/`, listed under `plugins` in either
  `opencode.json`, or with a `package.json`, never ran its `setup` (a marker-writing plugin left no
  marker at service start or during a `run`), and `opencode plugin add <local dir>` fails with a stack
  trace. The `setup`, `skill.reload()` and `prompt`-hook arms wait for a working local plugin route.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.opencode/skills`, `.agents/skills` and `.claude/skills`, as 1.x does; the multi
  prompt was inconsistent across its three passes and every root was settled by its own confirmation.
- The `opencode-v2-prompt` entry (an awaited `prompt` hook plus `ctx.skill.reload()`) has no rows: it
  cannot run until a local plugin loads.

## Cursor CLI 2026.09.15 (print `cursor-agent -p`; daemon `cursor-agent acp`)

- **Isolation.** The login is keyring-held and not found from a private HOME, so the CLI runs against
  the real home with `KCAP_SKIP=1`; the probe hook is the sandbox repository's `.cursor/hooks.json`
  (`cursor`) or a merge into the real `~/.cursor/hooks.json` restored after the turn
  (`cursor-userhooks`). Print mode ends every tool-using turn with `RetriableError: WritableIterable is
  closed` and exit 1 after three failed stream reconnects; the `json` and `text` formats then print
  nothing, so the adapter reads the answer from `--output-format stream-json`, which has already streamed
  it. The native load is a `readToolCall` of the listed SKILL.md path.
- **S1** `visible_first_turn` ×2 in both modes.
- **S2 print:** project-level `sessionStart` fires; `hook-creates-root` `not_visible` ×2 and
  `hook-adds-skill` `not_visible` ×2. With the user-level file (`cursor-userhooks`) `hook-adds-skill`
  `not_visible` ×2 and **`hook-creates-root` `not_visible` ×2 then `visible_first_turn` once**: a
  fire-and-forget hook races Cursor's indexing, and the order decides. `workspaceOpen` does fire in
  print mode and its `pluginPaths` route delivers, from the project file and the user file alike:
  `registration` is `visible_after_reload` ×2 for both, the hook writing the skill into a plugin
  directory it returns. Over ACP it stays `untested`: no Cursor hook fires there at all.
- **S2 daemon:** no hook fires in ACP mode, from the project file or the user file; `sessionStart` and
  `workspaceOpen` alike (`untested`, hook never fired).
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.cursor/skills`, `.agents/skills`, `.claude/skills` and `.codex/skills`, all
  documented; `.cursor/skills` is Cursor's vendor-isolated destination.

## Antigravity CLI 1.2.4 (`agy -p`; no daemon mode)

- **Launch.** Skills are slash commands that expand in print mode, so the adapter leads the prompt with
  `/<skill-name>`; without the slash the model never sees a skill body. The current directory alone is
  not the workspace: `--add-dir <repo>` is required before repo-local skills load. The OAuth session is
  keyring-held and starts a new browser sign-in from any private HOME, so agy runs against the real home
  with `KCAP_SKIP=1`.
- **Layouts.** `.agents/skills/<name>/SKILL.md` loads (`agy-dirlayout`, and `agy-clidir`); the flat
  `.agents/skills/<name>.md` layout the CLI documentation describes does not (`agy` entry, S1
  `not_visible` ×2), and a flat file under the documented global `~/.gemini/antigravity-cli/skills/`
  does not either while a `<name>/SKILL.md` directory there does.
- **S1** `visible_first_turn` ×2 (directory layout).
- **S2** `hook-creates-root` and `hook-adds-skill` `not_visible` ×2: the workspace plugin
  `.agents/plugins/probe/hooks.json` `PreInvocation` hook fired about 2 s after launch and the skill was
  not listed. The same hook placed in the CLI docs' global `~/.gemini/antigravity-cli/plugins/` never
  fired (`agy-clidir`, `untested`).
- **S3** both exclusions `visible_first_turn` ×2.
- **S4** consumes `.agents/skills` and legacy `.agent/skills`, both documented.

## Gemini CLI 0.60.0 — untested (credential)

The copied OAuth credential is refused: `IneligibleTierError: This client is no longer supported for
Gemini Code Assist for individuals. To continue using Gemini, please migrate to the Antigravity suite`
(`out/gemini/print/S1/S1_native/run1.gemini.stderr.log`). Print mode exits 1 before any model call and
ACP mode rejects `session/new` with "Gemini API key is missing or not configured". Every turn arm is
`untested` with that reason. The adapter is complete (hooks in `.gemini/settings.json`,
`security.auth.selectedType`, folder trust off, `--approval-mode yolo`, `gemini skills list` as the
catalogue check) and runs as soon as `GEMINI_API_KEY` is exported.

## Cross-vendor consumption (S4)

| Root | Claude | Codex | Pi | Copilot | Kiro | OpenCode | Cursor | agy |
| -- | -- | -- | -- | -- | -- | -- | -- | -- |
| `.claude/skills` | yes | no | no | yes | no | yes | yes | no |
| `.agents/skills` | no | yes | yes | yes | no | yes | yes | yes |
| `.codex/skills` | no | yes | no | no | no | no | yes | no |
| `.pi/skills` | no | no | yes | no | no | no | no | no |
| `.github/skills` | no | no | no | yes | no | no | no | no |
| `.kiro/skills` | no | no | no | no | yes | no | no | no |
| `.opencode/skills` | no | no | no | no | no | yes | no | no |
| `.cursor/skills` | no | no | no | no | no | no | yes | no |
| `.agent/skills` | no | no | no | no | no | no | no | yes |
| `.gemini/skills` | no | no | no | no | no | no | no | no |

Every "no" was confirmed with a single-root turn after the multi-prompt run missed it. Vendor-isolated
destinations: `.pi/skills`, `.github/skills`, `.kiro/skills`, `.opencode/skills`, `.cursor/skills`.
Claude and Codex have none: `.claude/skills` is read by three others, `.codex/skills` by Cursor.

## Git exclusion (S3)

Every measured entry loads a skill under both `.gitignore` and `info/exclude`, in every mode, with
`git status --porcelain` empty for the skill path and non-empty in the untracked control. Both
mechanisms keep generated skills out of the index without hiding them from any harness measured.

## Startup ordering (S2)

| Entry | Hook fires | Hook-written skill in first request | Registration route |
| -- | -- | -- | -- |
| Claude | yes | no | none |
| Codex (print, app-server) | yes | no | none |
| Pi (print, rpc) | yes | no | `resources_discover` skillPaths: yes |
| Copilot (print, acp) | yes | no | none |
| Kiro (print, acp) | yes, at session creation | **yes** | not needed |
| OpenCode 1.x print | yes | only into an existing root | `chat.system.transform`: no |
| OpenCode 1.x acp | yes | no | no |
| OpenCode 2.x | no plugin loads | untested | untested |
| Cursor print | `sessionStart` yes, `workspaceOpen` yes | race (1 of 3) | `workspaceOpen` `pluginPaths` |
| Cursor acp | no | untested | untested |
| agy | workspace `PreInvocation` yes, global plugin no | no | none |

## Consequences for #778 and #962

- A file drop from a startup hook reaches the first request only on Kiro (whose `agentSpawn` runs at
  session creation, before the catalogue is built) and, for an existing root, on OpenCode 1.x print
  mode. Everywhere else skills are indexed first, and on Cursor the two race.
- Two vendors take a skill handed to them at startup rather than found on disk: Pi, through an
  extension that writes in `session_start` and returns the directory from `resources_discover`, and
  Cursor in print mode, through a `workspaceOpen` hook that returns `pluginPaths`. Both are
  `visible_after_reload`. No headless registration exists for Claude, Codex, Copilot or agy, so their
  delivery must happen before launch.
- `info/exclude` and `.gitignore` are both safe for every measured harness.
- Vendor-isolated destinations exist for Pi, Copilot, Kiro, OpenCode, Cursor and Antigravity
  (`.agent/skills`); not for Claude or Codex. Two entries of one vendor count as one consumer.
- Native loading differs: Claude, Copilot and OpenCode load through a tool of their own; Codex, Pi,
  Kiro and Cursor read the listed file; agy expands a slash command. A prompt that forbids tool use
  defeats the file readers, so the kit forbids searching instead and records listed reads apart from
  searches.
- Layout matters for agy: only `<name>/SKILL.md` loads, the documented flat file does not; and the
  repository must be added to the workspace.

## Manual GUI procedures

For Cursor desktop, Antigravity IDE and Kiro IDE, which this kit cannot launch:

1. Create a fresh git repository with one commit and open it in the IDE with the vendor's startup hook
   configured to run `probe-hook.sh` (generate one with `lib/hook_script.py`, or copy the shape from the
   matching adapter's `install_startup_hook`).
2. In the first prompt of the first session, paste the single prompt from `lib/probe_skill.py`
   (`single_prompt`) for the skill the hook writes (for Antigravity, lead with `/<skill-name>`).
3. The reply decides: the token means `visible_first_turn`; the skill's name without the token means
   `catalogue_only`; `NO-SKILL` means `not_visible`. Repeat once. Record the IDE version and the reply
   under `out/<entry>/interactive/` by hand.

## Kit traps found during pass 1

- Claude's keychain login is keyed by the config root; any explicit `CLAUDE_CONFIG_DIR` loses it.
  Cursor's and agy's are keyed by HOME, and a signed-out agy starts a browser sign-in on every launch.
- A prompt that says "do not run any tool" makes Codex, Pi, Kiro and Cursor answer `NO-SKILL` for a
  listed skill.
- The app-server's `hooks/list` nests hooks under `result.data[].hooks`.
- Gemini re-execs itself, and asyncio's `Process.wait()` never returns while the grandchild holds
  stdout: children run in their own session and the session is killed on the graceful-exit timeout.
- A vendor that exits without a reply (Gemini's tier refusal, Cursor's stream error) must be `untested`,
  never `not_visible`; a printed usage text is a failed run, not a negative control.
- A reply that echoes another skill's name beside `NO-SKILL` is a negative, not `catalogue_only`; a
  Kiro agent without tools names the listed skill and cannot read it, which is a tool-access confound.
- Two `cursor-agent` processes at once make print mode fail mid-turn.

# Pass 2 — lifecycle and interactive mode (2026-09-17)

**Subject:** the same machine and binaries as pass 1, with Claude Code 2.1.274 and agy 1.2.5 (both
auto-updated mid-run, so the matrix carries a row per version). macOS 26.6.2 / arm64.
**Driver:** the same kit with three launch modes. `--mode tui` drives the vendor's interactive UI on a
pseudo-terminal: the reader applies the cursor movements to a grid, because a coding agent streams its
answer beside a spinner that redraws in place and the concatenated bytes interleave the two.

Pass 1 settled that discovery does not depend on the launch mode: exclusion and root verdicts agreed
between print and daemon mode in 135 of 139 measured pairs. So the discovery-only scenarios stay where
pass 1 measured them, and pass 2 adds the scenarios that need a second turn or a different tree:

| Scenario | Question | Modes |
| -- | -- | -- |
| S5 live catalogue | does a session see a skill added, rewritten or deleted while it is open? | daemon, tui |
| S6 startup rewrite | does a startup hook that rewrites or deletes an existing skill reach the first request? | print, daemon |
| S7 resume | does a resumed session see what changed after the first one ended? | print |
| S8 nested cwd | launched from a subdirectory, is the repository root's skill found? | print |
| S9 worktrees | in a linked worktree, whose skills are found, under `info/exclude`? | print |
| S10 peer hook | does one session see the skill another session's startup hook wrote? | daemon |

A two-turn row is decided by the second turn; the first turn's verdict travels in the notes as
`turn1=`. A later turn asks for the token the skill carries *now* and carries a mark of its own, so a
session answering from its own context, or a UI redrawing the earlier answer, is wrong rather than
lucky. New verdicts: `visible_live` (the change reached an open session), `stale` (the old body
survived), `revoked` (`NO-SKILL` after a delete).

## What a live session sees (S5, S10)

| Harness | add | rewrite | delete | another session's hook |
| -- | -- | -- | -- | -- |
| Codex | visible_live | visible_live | revoked | visible_live |
| Kiro | visible_live | visible_live | revoked | visible_live |
| Cursor | not visible | visible_live | revoked | not visible |
| Pi | not visible | visible_live | not visible | not visible |
| Copilot | not visible | stale | stale | not visible |
| OpenCode 1.x | not visible | stale | stale | not visible |
| OpenCode 2.x | not visible | stale | revoked | untested (no plugin loads) |

Two behaviours separate the field. Codex and Kiro re-enumerate the skills directory on every turn, so
a materializer can write into a session that is already running, including from another process. Pi,
Cursor and OpenCode fix the catalogue at session start but read the listed file when the skill is used,
which is why a rewrite lands while an addition does not. Copilot caches both: an open session serves
the body it read at startup.

In interactive mode the same split holds, with two differences: OpenCode 2.x does see an addition live
(`visible_live`), and Copilot has the only working reload command. `/skills reload` moves its
interactive session from `NO-SKILL` to the token, recorded as `visible_after_reload`. No other harness
has such a command: Codex's palette is `/model /fast /ide /permissions /keymap /vim /experimental
/approve`, Pi exposes skills as `skill:<name>` entries with no reload, Cursor and Antigravity only list.

## What a startup hook can still change (S6)

Rewriting an existing skill's body from the startup hook reaches the first request on every harness
that could run the arm except Claude, which keeps serving the body it indexed (`stale`). OpenCode 2.x
is excluded from that statement: its rewrite rows are untested in both modes, because no local plugin
loads. Deleting the skill is honoured
everywhere except Antigravity, which still answers from the deleted skill, and Codex's daemon mode,
which serves the body while the file is already gone. Cursor's daemon mode records `untested`: no
Cursor hook fires over ACP, as pass 1 also found. OpenCode 2.x records `untested` throughout, because
no local plugin loads from any placement.

Interactive mode changes Claude's answer to the pass 1 question. A skill written by its `SessionStart`
hook *is* visible to the first interactive request (`visible_first_turn`), while the same hook in print
mode is not. Kiro is visible in both, as its `agentSpawn` timing predicts. Codex, Pi, Copilot, Cursor
and Antigravity are not visible in either.

## Resuming a session (S7)

Every harness that resumes at all sees a skill added after the first session ended, except Cursor,
which does not. A rewritten body is picked up by all of them except Copilot, which serves the old one.

Claude is the exception worth naming: its resumed session returns `stop_reason: refusal` with zero
output tokens, twice, for the rewrite question, while the same resume route answers the addition
question normally. The row is `untested` with that reason, not a negative. Kiro has no session id in
its non-interactive output at all, so its resume is per launch directory (`chat --resume`).

## Where a harness looks (S8, S9)

Launched from `<repo>/sub/dir`, Claude, Codex, Copilot, OpenCode and Antigravity find a skill in the
repository root as well as in the subdirectory. Pi, Kiro and Cursor find only the subdirectory's own.
A materializer that writes to the repository root is therefore invisible to three of nine harnesses
whenever the session starts deeper in the tree.

In a linked worktree, every harness finds the worktree's own skills under `info/exclude` and none of
them reads the main checkout's. Git resolves a linked worktree's `info/exclude` to the shared common
directory, so one exclusion covers every worktree.

## Consequences for #778 and #962

- **Writing into a live session works only on Codex and Kiro**, and for a rewrite also on Pi, Cursor
  and OpenCode. For Copilot the only live route is its reload command, and that exists in the
  interactive UI alone. A materializer that must reach an already-running session has to know which of
  these three groups the vendor is in.
- **A startup adapter cannot rely on rewriting in place.** Claude serves the old body, so an update
  has to land before the process starts. Deleting is safer than rewriting everywhere except
  Antigravity and Codex's daemon mode.
- **Resume is the cheapest delivery route** for a skill added between sessions: eight of nine harnesses
  pick it up. Claude's refusal makes the rewrite case unmeasurable there, and Cursor ignores additions.
- **Anchor on the launch directory, not the repository root**, or three harnesses will not see the
  skill when a session starts in a subdirectory.
- **One `info/exclude` covers a repository and all its worktrees**, and no harness reads a sibling
  worktree's skills, so per-worktree materialization is safe and necessary.

## What the interactive mode cost to drive

Every one of these was found by replaying the screen the driver keeps beside a failed run:

- The kit's hook script captured stdin for two seconds to survive a vendor that never closes it. On a
  terminal that input belongs to the UI, and OpenCode sat waiting for a keystroke the hook had eaten.
  The hook now leaves a tty alone.
- Claude repaints its trust dialog after first drawing it, so an arrow key and the Enter behind it must
  be a redraw apart, and the dialog must not be answered until the screen settles. Claude then raises a
  browser-tools dialog and, between turns, an auto-mode wizard that takes the next prompt as its input.
- Cursor repaints its whole transcript, so the previous turn's reply is on screen after a clear, and
  its composer swallows an Enter sent straight behind the text.
- Codex needs the same hook-trust bypass its headless modes pass, or its startup hook silently does not
  run and the arm reads as a harness with no hook at all.
- OpenCode 2.x draws nothing whatsoever while a background service from an earlier run is alive, and
  would otherwise serve that run's catalogue.
- OpenCode 1.x's interactive UI never renders with the probe plugin installed: a blank screen for 300
  seconds, twice, while the same driver renders it fine without the plugin. A startup plugin can take
  the interactive UI down.
- Cursor's second interactive turn is unmeasurable: it answered once and then twice produced no marked
  reply within 300 seconds, so its interactive live-catalogue row stays `untested`. Its daemon mode
  answers the same question.
- A run that produces no answer at all is recorded `untested`, never `not_visible`. Claude's refusals
  and Codex's rejected resume flags both landed as "the skill was not there" until that rule existed.
