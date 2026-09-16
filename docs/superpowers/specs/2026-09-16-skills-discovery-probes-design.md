# Repo-local skill discovery probes across harnesses

GitHub: [#961](https://github.com/kurrent-io/kcap-cli/issues/961). Linear: AI-2829, under the
umbrella AI-2828. Research input: the Linear document *Approved skill delivery: harness paths,
startup ordering and compatibility research* (a documentation and source audit, not a runtime
certification).

## Problem

Automatic skill delivery (#778 materialization, #962 startup adapters) needs a runtime contract per
harness: where a repo-local skill must land, which startup mechanism makes it visible to the
session that is starting, which Git exclusion keeps it untracked without hiding it from the
harness, and which other harnesses read the same directory. The audit found ordering differences
and left version and ignore behaviour unresolved. A file existing on disk proves none of this; only
the starting session's own catalogue and a loaded skill body do.

This spec designs a reproducible probe kit that measures those properties for the nine supported
harnesses, and the capability matrix the materializer and startup adapters consume.

## Non-goals

- No production code changes: `SkillsCommand`, the materializer, hook installers and paths stay as
  they are. The kit hand-writes vendor hook configuration so a verdict describes the vendor.
- No server changes and no projection rebuild.
- No CI execution. The kit needs real vendor binaries and real credentials; it runs on a developer
  machine and records the versions it ran against.

## Kit layout

```
docs/probes/2026-09-16-skills-discovery/
  probe.py                 orchestrator
  selftest.py              stdlib unittest over lib/ with a fake adapter
  lib/
    isolation.py           per-run sandbox: fresh repo + private vendor config root
    probe_skill.py         nonce skill writer, prompt builder, reply parser
    git_exclusion.py       none / .gitignore / info/exclude, untracked assertion
    recorder.py            per-arm JSON summary, matrix.json emission
    verdict.py             verdict rules, repetition policy
    hook_script.py         the startup-hook payload written for a run
  harness/
    claude.py codex.py cursor.py copilot.py gemini.py kiro.py pi.py
    opencode_v1.py opencode_v2.py agy.py
  out/                     git-ignored run output
  .gitignore               out/ and *.stderr.log
  findings.md              per-harness narrative, versions, limitations, manual GUI procedures
  capability-matrix.md     the consumable table for #778 and #962
  matrix.json              one row per (entry, mode, scenario, arm)
```

The ACP client is imported from `docs/probes/2026-08-04-acp-reconnect-c0/acp_c0_probe.py`, as the
OpenCode ACP probe already does.

`probe.py` arguments: `--harness <entry>` (repeatable, default all installed), `--mode
print|daemon|interactive`, `--scenario S0..S4` (repeatable), `--turn` (without it only the free
phase runs), `--runs N` (default 2), `--outdir` (default `out/`), `--emit` (rebuild `matrix.json`
from `out/`).

## Shared library

### Isolation

One sandbox per arm, torn down after the summary is written:

- A fresh git repository under the scratch directory with one commit, a `README.md`, and `user.name`
  and `user.email` set locally. The path is resolved through `realpath` before use so vendors that
  refuse symlinked components (macOS `/var` to `/private`) see the same path Git reports.
- A private vendor config root through the vendor's own lever. `CLAUDE_CONFIG_DIR`, `CODEX_HOME`,
  `COPILOT_HOME`, `GEMINI_CLI_HOME`, `KIRO_HOME`, `PI_CODING_AGENT_DIR`, `OPENCODE_CONFIG_DIR` (with
  `XDG_CONFIG_HOME` and `XDG_DATA_HOME` beside it). Cursor and agy have no narrower lever, so they get a
  full `HOME` override.
- Credentials copied in from the real root by an explicit per-adapter file list, never a whole
  directory. A free-phase arm records whether authentication survived isolation (`auth_ok`). A
  vendor whose credential lives in the macOS keychain rather than a file may not survive; the
  adapter then falls back to the documented alternative (for Claude, the real config root with
  hooks supplied via `--settings <file>` and `--setting-sources`) and the summary names the lever
  actually used.
- The environment is built from a minimal allow-list (`PATH`, `HOME` or its override, `TERM`,
  locale, the vendor lever, the credential variables the adapter names). Nothing from the
  developer's `.envrc` reaches a vendor.

### Probe skill

`kcap-probe-<nonce>/SKILL.md` where `<nonce>` is six hex characters:

```
---
name: kcap-probe-<nonce>
description: Answers a question about the probe token for this repository.
---
When asked for the probe token, reply with exactly this token and nothing else:
PROBE-BODY-<token>
```

`<token>` is twelve hex characters and appears only in the body. The description is deliberately
bland so a model cannot answer from the catalogue alone. `probe_skill.py` also builds the prompts:

- single: `You have a skill named kcap-probe-<nonce>. Use it and reply with only the probe token
  it contains. If no such skill is available to you, reply with exactly NO-SKILL.`
- multi (S4): `List every skill available to you whose name starts with kcap-probe-. For each,
  reply with <name>=<token> on its own line, reading the token from the skill body. If there are
  none, reply with exactly NO-SKILL.`

The reply parser returns the set of tokens found and whether a skill name was mentioned, so
`catalogue_only` is distinguishable from `not_visible`.

### Git exclusion

Three arms: `none` (untracked), `gitignore` (a line in the repo's `.gitignore`, committed), and
`info-exclude` (a line in the file `git rev-parse --git-path info/exclude` names, so a worktree's
own exclude file is used, not the main checkout's). After the skill is written, the arm asserts
`git status --porcelain` is empty for the skill path. In the `none` arm the assertion is inverted:
the file must show as untracked, proving the path is inside the repo Git sees.

### Recorder and verdicts

Every arm writes `out/<entry>/<mode>/<scenario>/<arm>/run<N>.json`:

```
{ "entry", "harness", "binary", "version", "os", "mode", "argv", "isolation_lever",
  "credential_files", "auth_ok", "scenario", "arm", "root", "exclusion",
  "hook": { "mechanism", "config_path", "fired_at" },
  "first_request_at", "reply", "tokens_found", "skill_named", "stderr_path",
  "verdict", "duration_ms", "notes" }
```

Verdicts:

| Verdict | Meaning |
| -- | -- |
| `visible_first_turn` | the expected token is in the first reply |
| `visible_after_reload` | the token appears only after the named reload or registration mechanism |
| `catalogue_only` | the skill is named but its token is absent (discovered, body not loaded) |
| `not_visible` | no skill named, no token |
| `leaked` | S4 only: a token from a root this harness is not documented to read |
| `untested` | the arm could not run; `notes` carries the reason (no binary, no credential, mode unsupported) |

Repetition: each arm runs twice. Two agreeing runs settle it. A split runs a third time; the
majority is the verdict and the row is flagged `flaky: true`. S0 must never produce the token; one
occurrence fails the whole entry's prompt design and is reported as such, never averaged away.

Ordering evidence: the hook payload writes `<config_root>/probe-hook-fired.json` with a wall clock
and the hook's own input if the vendor supplies one. The adapter stamps `first_request_at`
immediately before the prompt is sent (ACP `session/prompt`, RPC prompt, or process start for print
mode where the prompt is an argument). The reply is the verdict; the timestamps corroborate that
the hook ran before the request rather than after it.

### Hook payload

`hook_script.py` writes an executable shell script into the config root. It takes the target root
and skill contents from environment variables the adapter sets in the hook configuration, creates
the directory tree, writes `SKILL.md`, stamps the timestamp file, and exits 0 with no stdout. For
harnesses whose integration point is an extension or plugin (Pi, OpenCode V1 and V2), the payload is
a small TypeScript or JavaScript module generated from a template with the same paths baked in.

## Harness adapters

Each adapter implements four primitives and declares its metadata:

- `install_startup_hook(payload)`: writes the vendor's hook or extension configuration into the
  isolated user-level root, at the location kcap's own installer for that vendor uses
  (`ClaudePluginInstaller`, `CodexHooksInstaller`, `CursorHooksInstaller`, `CopilotHooksInstaller`,
  `GeminiHooksInstaller`, `KiroHooksInstaller`, `PiExtensionInstaller`, `OpenCodeExtensionInstaller`,
  `AntigravityHooksInstaller`). Matching the installer's location keeps the probe faithful to the
  production delivery path without calling production code.
- `launch(mode, cwd)`: starts the vendor in the requested mode with the isolated environment.
- `ask(prompt)`: sends the first prompt and returns the reply text plus the raw event stream.
- `list_catalogue()` (optional): a vendor listing command run in the same environment, for
  corroboration only. It never decides a verdict, because a listing process is not the session.

| Entry | Binary | Print mode | Daemon mode | Native root | Catalogue listing |
| -- | -- | -- | -- | -- | -- |
| claude | `claude` | `-p --output-format stream-json` | same as print | `.claude/skills` | none |
| codex | `codex` | `exec --json` | `app-server` | `.agents/skills` | none |
| gemini | `gemini` | `-p -o stream-json` | `--experimental-acp` | `.gemini/skills`, `.agents/skills` | `gemini skills list` if present |
| pi | `pi` | `-p --mode json` | `--mode rpc` | `.pi/skills`, `.agents/skills` | none |
| cursor | `cursor-agent` | `-p` | `acp` | `.cursor/skills`, `.agents/skills` | none |
| copilot | `copilot` | `-p` | `--acp --stdio` | `.github/skills`, `.agents/skills` | `copilot skill list --json` |
| kiro | `kiro-cli` | `chat --no-interactive` | `acp` | `.kiro/skills` | none |
| opencode-v1 | `opencode` (1.x) | `run` | `acp` | `.opencode/skills` | none |
| opencode-v2 | `opencode` (2.x, separate prefix) | `run` | `acp` | `.opencode/skills` | HTTP catalogue if exposed |
| agy | `agy` | `-p` | same as print | `.agents/skills` (flat `<name>.md` per CLI docs; both layouts probed) | none |

Trust and approval flags a mode needs to run headless (Pi `--approve`, Gemini trusted-folder
setting, Codex hook trust, Kiro trust) are part of the adapter and are recorded in `argv`, because
a production launch needs the same ones.

Kiro's hook configuration differs between CLI generations: `.kiro/hooks/*.json` keyed
`SessionStart` for CLI 3.x, an `agentSpawn` hook inside the agent definition before that. The
adapter reads `kiro-cli --version` and writes the matching form; the summary records which one ran.
The custom-agent resource inheritance question (`chat.disableInheritingDefaultResources`) is an S1
arm with a custom agent selected, since a default agent alone would not exercise it.

Interactive mode (a PTY-driven TUI) is pass 2 for every entry. Cursor desktop, Antigravity IDE and
Kiro IDE are GUI launches with no scriptable entry point from this kit; they get a manual procedure
in `findings.md` and `untested` rows until someone runs it.

## Scenarios

Each scenario runs per entry and per mode. `root` defaults to the entry's native root.

### Pass 1

**S0 negative control.** No skill anywhere. Single prompt. Pass: no token, and either `NO-SKILL` or
any reply without the token. Guards the prompt design and the reply parser.

**S1 positive control.** Skill written into the native root before launch, exclusion `none`. Single
prompt. Pass: `visible_first_turn`. A failing S1 marks every later scenario for that entry and mode
`untested` with reason `S1 failed`, so the report never attributes a mode problem to ordering.

**S2 root created by the startup hook.** Arms:

- `hook-creates-root`: no skills root exists at launch; the hook creates root and skill.
- `hook-adds-skill`: the root exists with an unrelated skill; the hook adds the probe skill.
- `registration`: only for entries with a documented registration path. Pi: an extension that
  awaits `session_start`, writes the skill, and returns the directory from `resources_discover`.
  OpenCode V2: a plugin whose awaited `prompt` hook writes the skill and calls `ctx.skill.reload()`.
  Cursor: `workspaceOpen` returning `pluginPaths`. Gemini: the hook writes the skill and the adapter
  checks whether any hook output or configuration triggers a rescan before the first request.

Pass: `visible_first_turn`. A `visible_after_reload` verdict names the mechanism. The matrix column
"startup mechanism proven" is filled from whichever arm passed.

**S3 Git exclusion.** Skill pre-written; arms `gitignore` and `info-exclude` (`none` is S1). Pass:
untracked assertion holds and `visible_first_turn`. A `catalogue_only` here is the case the
Antigravity Strict Mode note predicts and is reported as its own finding.

**S4 cross-vendor consumption.** Ten roots each hold a probe skill with its own token:
`.claude/skills`, `.agents/skills`, `.codex/skills`, `.cursor/skills`, `.github/skills`,
`.gemini/skills`, `.kiro/skills`, `.pi/skills`, `.opencode/skills`, `.agent/skills`. Multi prompt,
one turn per entry. Every root whose token is absent is confirmed with a single-root S1-style rerun
before it is recorded as not consumed, so the consumption table has no false negatives from a model
that stopped enumerating early. Rows: `visible_first_turn` for a documented root, `leaked` for an
undocumented one, `not_visible` for a confirmed non-consumption.

### Pass 2 (same kit, later PR)

- update: the hook rewrites an existing skill body with a new token; new session must see the new
  token, and a running session is asked again after the rewrite.
- revocation: the hook deletes the skill; a new session must answer `NO-SKILL`.
- resume: a skill added after the first session ended; `--resume` or `-c` asked for the token.
- nested cwd: launch from a subdirectory; ancestor discovery of the repo root's skills.
- worktrees: a skill in worktree A; sessions in A and B; `info/exclude` resolved per worktree.
- concurrent sessions: two sessions in one checkout; a hook write from one while the other starts.
- interactive mode: PTY-driven TUI for every entry, including `/skills reload` style commands.

## Outputs

**`matrix.json`** is rebuilt from `out/` by `probe.py --emit`. One row per (entry, mode, scenario,
arm, root, exclusion) with `version`, `os`, `verdict`, `mechanism`, `flaky`, `runs`, `evidence`
(the run file paths) and `notes`. Rows for entries that could not run are present with `untested`.

**`findings.md`** follows the existing probe style: subject line with versions and OS, driver,
cost, then one section per entry: isolation lever and credential handling, hook mechanism and its
config path, results per scenario with the reply excerpts that decided them, limitations and the
minimum version the finding applies to, and the manual procedure for any GUI mode.

**`capability-matrix.md`** is the table #778 and #962 consume:

| Column | Content |
| -- | -- |
| Entry, version tested | binary and version the row is proven against |
| Native root | where the materializer writes |
| Roots consumed | every root this entry loaded in S4 |
| Startup mechanism proven | the S2 arm that passed, or none |
| Exclusion preserving load | which of `gitignore`, `info-exclude` passed S3 |
| Vendor-isolated destination | whether any root is read by this entry alone |
| Reload path | the mechanism behind any `visible_after_reload` |
| GUI status | tested, untested, or not applicable |
| Minimum version | the earliest version the finding is claimed for (the tested one unless docs say otherwise) |

## Installs and credentials

Installed on this machine before running: cursor-agent (vendor install script), Copilot CLI (npm
`@github/copilot`), Kiro CLI (brew cask `kiro-cli`), OpenCode V1 (npm `opencode-ai`), OpenCode V2
(vendor installer into a separate prefix so both versions coexist), agy (vendor install), Gemini
updated to latest, Pi updated to latest. Claude and Codex are current.

Logins that need a browser are run by the developer when the kit reports `auth_ok: false` for an
entry: Cursor, Kiro, OpenCode, agy, and Copilot if the stored GitHub token does not carry. An entry
without a credential runs its free phase and records `untested` for every turn arm.

## Self-tests

`selftest.py` runs with `python3 -m unittest selftest` and needs no vendor binary. It covers, with
a fake adapter: isolation creates a repo and config root and passes only allow-listed environment;
the token never appears in the description or the prompts; `info/exclude` resolves inside a
worktree; the untracked assertion in both polarities; the reply parser on token, name-only, and
`NO-SKILL` replies; verdict rules including the split-run rule and the S0 hard failure; matrix
emission from run files.

## Delivery

Branch from `main` in the current worktree. This spec is committed first. The implementation plan
follows the writing-plans skill.

Pass 1 PR: kit, self-tests, results for every CLI entry in print and daemon modes, `findings.md`,
`capability-matrix.md`, `matrix.json`. References #961 and AI-2829 without closing.

Pass 2 PR: lifecycle scenarios and interactive mode, matrix rows added, closes #961.

## Risks

- A model may decline to reveal a skill body verbatim. S1 is the guard: if the positive control
  fails for a model, the adapter's prompt is adjusted before any ordering claim is made, and the
  adjustment is recorded.
- Version drift during the run: every row carries the version it ran against, and `--emit` never
  merges rows from different versions of one entry.
- Cost: pass 1 is roughly 40 to 60 short turns across the developer's accounts.
