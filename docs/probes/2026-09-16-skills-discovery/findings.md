# Repo-local skill discovery probes — 2026-09-16

**Subject:** Claude Code 2.1.273 (native installer, `~/.local/bin/claude`), codex-cli 0.154.0 (brew cask),
Gemini CLI 0.60.0 (npm `@google/gemini-cli`), Pi 0.85.1 (npm `@earendil-works/pi-coding-agent`),
GitHub Copilot CLI 1.0.85 (npm `@github/copilot`), kiro-cli 2.21.4 (brew cask), OpenCode 1.18.31 (npm
`opencode-ai`) and OpenCode 2.0.4 (npm `@opencode/cli`, prefix `~/.local/opencode-v2`); `cursor-agent`
and `agy` were not yet installed when this document was written. macOS 26.6.2 / arm64.
**Driver:** `probe.py` (scenarios S0–S4, two runs per arm, a third on disagreement, one single-run
confirmation per root the multi prompt missed); `report.py` renders `capability-matrix.md` from
`matrix.json`; `selftest.py` (67 tests, no vendor binary needed) covers the kit.
**Cost:** the free phase issues zero model requests. Pass 1 recorded about 160 model turns across the
measured entries, plus roughly 30 spent on re-measurements while the kit was being corrected.

Every verdict below is the starting session's own reply. A skill counts as *discovered* when the model
names it and as *loaded* when the reply carries the token that exists only in the skill body. A file on
disk, a hook exit code or a listing command never decides a row. Print mode is the vendor's headless
single-prompt launch; daemon mode is the launch kcap's daemon uses (ACP for Copilot, Gemini, Kiro,
Cursor and OpenCode, `codex app-server`, `pi --mode rpc`); interactive TUI launches are pass 2.

## How to reproduce

```
python3 docs/probes/2026-09-16-skills-discovery/selftest.py          # kit self-tests, no vendors
python3 docs/probes/2026-09-16-skills-discovery/probe.py --harness claude --mode print          # free phase
python3 docs/probes/2026-09-16-skills-discovery/probe.py --harness claude --mode print --turn   # full sweep
python3 docs/probes/2026-09-16-skills-discovery/probe.py --emit && python3 docs/probes/2026-09-16-skills-discovery/report.py
```

A sweep resumes: arms with settled rows are not re-spent, partial arms are topped up, and an arm with an
`untested` row is measured again. `--rerun` clears an entry's arms first. Environment switches:
`KCAP_PROBE_CLAUDE_REAL_CONFIG=0|1` forces the Claude config-root mode, `GEMINI_API_KEY` makes Gemini
runnable on an account whose OAuth tier is refused, `KIRO_API_KEY` is passed through for Kiro's
headless mode, `KCAP_PROBE_OPENCODE_MODEL` pins the OpenCode model, `KCAP_OPENCODE_V2_PATH` names the V2
binary. Logins that open a browser are the operator's: `cursor-agent login`, `kiro-cli login`,
`opencode auth login`, `agy` (first launch).

Every run leaves `out/<entry>/<mode>/<scenario>/<arm>/run<N>.json` (the record), `run<N>.raw.txt`
(the vendor's full event stream) and `run<N>.<name>.stderr.log`; `out/` is git-ignored, `matrix.json`
is the committed roll-up.

## Claude Code 2.1.273 (print mode; daemon launches use the same `-p`)

- **Isolation.** The OAuth login lives in the macOS keychain keyed by the config root: an isolated
  `CLAUDE_CONFIG_DIR` reports `loggedIn: false`, and so does naming the default root explicitly. The
  adapter therefore runs against the real config root with `CLAUDE_CONFIG_DIR` unset, supplies the probe
  hook through `--settings <file>`, limits sources with `--setting-sources project` so no user-level
  plugin loads, and sets `KCAP_SKIP=1` so kcap's own hooks stand down. `--allowedTools Skill` leaves the
  model no way to read files, so a token can only arrive through the Skill tool.
- **S1** `visible_first_turn` ×2 — `out/claude/print/S1/S1_native/run1.json`.
- **S2** `hook-creates-root` `not_visible` ×2 and `hook-adds-skill` `not_visible` ×2. The SessionStart hook
  fired about 0.7 s after launch and the skill file existed when the reply was written, yet the model
  answered `NO-SKILL`; with a pre-existing sibling skill it listed only that sibling ("The skill listed is
  kcap-probe-…, not …"). Skills are indexed before SessionStart hooks run, and the watched-root refresh
  the documentation describes does not reach the first request in print mode.
- **S3** `gitignore` `visible_first_turn` ×2, `info-exclude` `visible_first_turn` ×2: neither exclusion
  hides a skill, and `git status` stays clean under both.
- **S4** consumes `.claude/skills` only; every other root `not_visible`, each confirmed with its own turn.
- **Limitation.** No registration or reload mechanism exists for a headless launch, so there is no
  automatic path that makes a hook-written skill visible to the first request. `.claude/skills` is
  also read by Copilot, so it is not a vendor-isolated destination.

## Codex 0.154.0 (print `codex exec`; daemon `codex app-server`)

- **Isolation.** `CODEX_HOME` with `auth.json` copied keeps the ChatGPT login; `config.toml` carries a
  `[projects."<repo>"] trust_level = "trusted"` entry. `exec` runs with
  `--dangerously-bypass-hook-trust`; the app-server driver reads `hooks/list` (hooks are grouped under
  `result.data[].hooks`) and restarts with `-c hooks.state={…trusted_hash…}` to seed trust, exactly as
  kcap's daemon does.
- **Native load is a file read.** Codex lists a skill with its path and the model runs
  `cat <path>/SKILL.md` to load it. The first probe prompt forbade "any tool" and Codex answered
  `NO-SKILL` although the skill was listed; the prompt now forbids searching, not reading the listed
  file, and the adapter records `skill_reads` (a `cat` of a SKILL.md path) apart from `searches`.
- **S1** `visible_first_turn` ×2 in both modes, `skill_reads=1 searches=0`.
- **S2** both hook arms `not_visible` ×2 in both modes; the hook fired (`hook_trust=seeded` in daemon
  mode) and the file existed, the first request did not list it.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.agents/skills` and repo-local `.codex/skills`; the latter is read by no other
  measured harness, so it is Codex's vendor-isolated destination.
- **Limitation.** No registration or reload mechanism for a headless launch.

## Pi 0.85.1 (print `pi -p --mode json`; daemon `pi --mode rpc`)

- **Isolation.** `PI_CODING_AGENT_DIR` with `auth.json`, `models.json`, `settings.json` copied; auth
  through the `openai-codex` provider (`pi auth check`). `--approve` trusts the sandbox repo's project
  resources. The native load is a `read` of the listed SKILL.md path, classified as `skill_reads`.
- **S1** `visible_first_turn` ×2 in both modes.
- **S2** `hook-creates-root` and `hook-adds-skill` (an extension whose `session_start` runs the hook
  script) `not_visible` ×2 in both modes. **`registration` `visible_after_reload` ×2 in both modes:** an
  extension that writes the skill in `session_start` and returns the skills root from
  `resources_discover` (`{skillPaths: [<root>]}`) makes the skill part of the first request. This is the
  one automatic integration path pass 1 proved.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.pi/skills` and `.agents/skills`; `.pi/skills` is Pi's vendor-isolated destination.

## GitHub Copilot CLI 1.0.85 (print `copilot -p`; daemon `copilot --acp --stdio`)

- **Isolation.** `COPILOT_HOME` with `COPILOT_GITHUB_TOKEN` from `gh auth token` (an OAuth `gho_`
  token); no credential file is needed. The native load is Copilot's own `skill` tool ("Skill … loaded
  successfully"), classified as `skill_loads`.
- **S1** `visible_first_turn` ×2 in both modes; `copilot skill list --json` also lists the probe skill in
  the free phase.
- **S2** both hook arms `not_visible` ×2 in both modes. The `sessionStart` hook in
  `$COPILOT_HOME/hooks/*.json` fired about 3 s after launch; the skill was not in the first request.
- **S3** both exclusions `visible_first_turn` ×2 in both modes.
- **S4** consumes `.github/skills`, `.agents/skills` and `.claude/skills`, all documented; no undocumented
  root. `.github/skills` is Copilot's vendor-isolated destination.
- **Limitation.** `/skills reload` is interactive only; no headless reload or registration path.

## Gemini CLI 0.60.0 — untested (credential)

The copied OAuth credential is refused: `IneligibleTierError: This client is no longer supported for
Gemini Code Assist for individuals. To continue using Gemini, please migrate to the Antigravity suite`
(`out/gemini/print/S1/S1_native/run1.gemini.stderr.log`). Print mode exits 1 before any model call and
ACP mode rejects `session/new` with "Gemini API key is missing or not configured". Every turn arm is
`untested` with that reason. The adapter is complete (hooks in `.gemini/settings.json`,
`security.auth.selectedType`, folder trust off, `--approval-mode yolo`, `gemini skills list` as the
catalogue check) and runs as soon as `GEMINI_API_KEY` is exported.

## Kiro CLI 2.21.4 — untested (login)

`kiro-cli whoami` reports no login, so the turn arms are `untested`. Before the auth check existed, an
unauthenticated `kiro-cli chat --no-interactive` opened a browser login instead of answering; that S0
row was voided. The installed build is the 2.x hook generation, so the adapter writes
`hooks.agentSpawn` inside a custom agent definition and makes it the default agent; a 3.x build would
get `.kiro/hooks/*.json` with `trigger: SessionStart`. Two extra entries answer the custom-agent
inheritance question once a login exists: `kiro-agent-bare` (no resources) and `kiro-agent-skills`
(`skill://.kiro/skills/*/SKILL.md`). The brew cask ships the launcher only; `kiro-cli-chat` and
`kiro-cli-term` had to be linked from the app bundle into `~/.local/bin`.

## OpenCode 1.18.31 and 2.0.4 — untested (login)

No `auth.json` exists on this machine, so both versions record `auth_ok: false` and their arms are
`untested`. Adapters: V1 loads a plugin from `$OPENCODE_CONFIG_DIR/plugins/` (a `session.created`
handler as the startup hook, an `experimental.chat.system.transform` handler as the registration arm);
V2 runs `--standalone` (a private server per run) with a `Plugin.define` module whose `setup` runs the
hook (`opencode-v2`) or `setup` plus `ctx.skill.reload()` and the awaited `prompt` hook plus reload
(`opencode-v2-prompt`). `opencode auth login` unblocks them.

## Cursor and Antigravity CLI — untested (not installed)

Both install through `curl | bash` scripts that this session could not run. The Cursor adapter drives
`cursor-agent -p --output-format json --trust --force` and `cursor-agent acp --trust`, writes
`~/.cursor/hooks.json` (`sessionStart`), and has a `workspaceOpen` registration arm returning
`pluginPaths` for a plugin directory holding `skills/<name>/SKILL.md`. The agy adapter drives
`agy -p --output-format stream-json --dangerously-skip-permissions`, writes a `PreInvocation` hook as a
plugin under `~/.gemini/config/plugins/probe/` (`agy`, kcap's location) or
`~/.gemini/antigravity-cli/plugins/probe/` (`agy-clidir`, the CLI docs' location), and probes both the
flat `.agents/skills/<name>.md` layout (`agy`) and the `<name>/SKILL.md` layout (`agy-dirlayout`).

## Cross-vendor consumption (S4)

| Root | Claude | Codex | Pi | Copilot |
| -- | -- | -- | -- | -- |
| `.claude/skills` | yes | no | no | yes |
| `.agents/skills` | no | yes | yes | yes |
| `.codex/skills` | no | yes | no | no |
| `.pi/skills` | no | no | yes | no |
| `.github/skills` | no | no | no | yes |
| `.cursor/skills`, `.gemini/skills`, `.kiro/skills`, `.opencode/skills`, `.agent/skills` | no | no | no | no |

Every "no" was confirmed with a single-root turn after the multi-prompt run missed it.

## Git exclusion (S3)

| Entry | `.gitignore` | `info/exclude` |
| -- | -- | -- |
| Claude (print) | loads | loads |
| Codex (print, daemon) | loads | loads |
| Pi (print, daemon) | loads | loads |
| Copilot (print, daemon) | loads | loads |

`git status --porcelain` was empty for the skill path in every exclusion arm, and non-empty in the
untracked control, so both mechanisms keep generated skills out of the index without hiding them.

## Consequences for #778 and #962

- A file drop from a SessionStart-style hook reaches the first request in none of the four measured
  harnesses; skills are indexed before the hook runs. Delivery must happen before launch, or through a
  registration path.
- Pi has such a path: an extension that syncs in `session_start` and returns the directory from
  `resources_discover`. Claude, Codex and Copilot have none for headless launches.
- `info/exclude` and `.gitignore` are both safe for the four measured harnesses.
- Vendor-isolated destinations exist for Codex (`.codex/skills`), Pi (`.pi/skills`) and Copilot
  (`.github/skills`); Claude has none, because Copilot reads `.claude/skills`, and `.agents/skills` is
  shared by Codex, Pi and Copilot.
- Native loading differs: Claude and Copilot load through a tool of their own, Codex and Pi by reading
  the listed file. A prompt that forbids tool use defeats the latter two; the kit forbids searching
  instead and records reads of the listed file separately from searches.

## Manual GUI procedures

For Cursor desktop, Antigravity IDE and Kiro IDE, which this kit cannot launch:

1. Create a fresh git repository with one commit and open it in the IDE with the vendor's startup hook
   configured to run `probe-hook.sh` (generate one with `lib/hook_script.py`, or copy the shape from the
   matching adapter's `install_startup_hook`).
2. In the first prompt of the first session, paste the single prompt from `lib/probe_skill.py`
   (`single_prompt`) for the skill the hook writes.
3. The reply decides: the token means `visible_first_turn`; the skill's name without the token means
   `catalogue_only`; `NO-SKILL` means `not_visible`. Repeat once. Record the IDE version and the reply
   under `out/<entry>/interactive/` by hand.

## Kit traps found during pass 1

- Claude's keychain login is keyed by the config root; any explicit `CLAUDE_CONFIG_DIR` loses it.
- A prompt that says "do not run any tool" makes Codex and Pi answer `NO-SKILL` for a listed skill.
- The app-server's `hooks/list` nests hooks under `result.data[].hooks`.
- Gemini re-execs itself, and asyncio's `Process.wait()` never returns while the grandchild holds
  stdout: children run in their own session and the session is killed on the graceful-exit timeout.
- A vendor that exits without a reply (Gemini's tier refusal) must be `untested`, never `not_visible`.
- A reply that echoes another skill's name beside `NO-SKILL` is a negative, not `catalogue_only`.
