# Pi unattended-reviewer probe — 2026-09-21

**Subject:** `pi` 0.85.1 (Homebrew), macOS / arm64, `pi --mode rpc`.
**Driver:** `probe.py`, 36 arms, results in `summary.json`. Extensions under `ext/`.
**Cost:** zero model requests. Every arm points Pi at a scripted OpenAI-compatible provider on
loopback, through a scratch `PI_CODING_AGENT_DIR` and a scratch `HOME`. No real credential is read.

**Version reading.** 0.85.1 is the lowest version these behaviours were measured on, so it is the
**floor**: a reviewer lane may rely on them at or above it. Nothing here is a claim about one exact
build, and a newer `pi` needs no re-probe to stay eligible.

**Why a scripted provider.** It records the request Pi actually sends, so the tool surface, the
system prompt and the model id are read off the wire rather than from a model's account of itself.
It can also order a tool call no model would volunteer, which is the only way to measure "the tool
is absent": a model declining to call `bash` proves nothing about whether `bash` would have run.

**Two kinds of claim.** §1–§11, §13 and §14 are measured by an arm. §12 is read from the installed 0.85.1
package's source and docs and was not executed; it says so per item.

The scratch agent directory is a probe convenience and is not under test. Most arms also set
`PI_OFFLINE=1`; §8 is the pair that does not, because offline mode is what it measures.

---

## 1. Extension suppression and injection

| Arm | Argv (after `--mode rpc`) | Planted | Tools in the first request | Load marker |
|---|---|---|---|---|
| `discovery-default` | — | agent-dir `extensions/` canary | defaults + `agentcanary_tool` | written |
| `no-extensions` | `--no-extensions` | same | defaults only | absent |
| `no-extensions-plus-e` | `--no-extensions -e result.ts` | same | defaults + `submit_review_result` | only `result-loaded` |
| `settings-package-default` | — | `settings.json` `packages` canary | defaults + `pkgcanary_tool` | written |
| `settings-package-no-extensions` | `--no-extensions` | same | defaults only | absent |
| `project-default` | — | `<cwd>/.pi/extensions/` canary | defaults only | absent |
| `project-approve` | `--approve` | same | defaults + `projcanary_tool` | written |
| `project-approve-no-extensions` | `--approve --no-extensions` | same | defaults only | absent |

"Defaults" are the four tools active with no `--tools`: `bash`, `edit`, `read`, `write`.

- **`--no-extensions` suppresses every extension discovery source measured**: the agent
  directory's `extensions/`, a `settings.json` `packages` entry (what `pi install <path>` writes),
  and the project-local `.pi/extensions/`. It wins over `--approve`.
- **An explicit `-e <path>` still loads under `--no-extensions`.**
- **Project-local extensions do not load in `--mode rpc` by default.** `--approve` loads them, and
  that arm is the positive control: the planted file is loadable, so its absence elsewhere is
  suppression and not a broken plant.
- **The injected tool's wire name is the registered name, unprefixed**: `submit_review_result`.
- The extension runs inside Pi's process and inherits its environment. `result.ts` read
  `KCAP_FLOW_AGENT_ID` from `process.env` and recorded the value the driver set.

## 2. The `--tools` allowlist, at call level

Both arms use `--no-approve --no-extensions -e result.ts`, with both canaries planted, and the same
scripted calls. The second adds `--tools read,submit_review_result`.

| Scripted call | No allowlist (negative control) | With the allowlist |
|---|---|---|
| `bash` `touch BASH_RAN` | ran; `BASH_RAN` exists | `Tool bash not found`, `isError: true`, no file |
| `write` `WRITE_RAN` | ran; `WRITE_RAN` exists | `Tool write not found`, `isError: true`, no file |
| `edit` `inside.txt` | ran; `Successfully replaced 1 block(s)` | `Tool edit not found`, `isError: true` |
| `read` a file **outside** the cwd | returned its content | returned its content |
| `submit_review_result` | executed; arguments recorded | executed; arguments recorded |

- **The tools offered are exactly the allowlisted ones.** A call to any other tool is answered by Pi
  with a not-found tool result and never reaches an executor. The control arm proves the scripted
  calls do execute when the tool exists, so this is containment and not a driver artefact.
- **The allowlist covers extension tools too.** `clamp-omits-result-tool` passes `--tools read` with
  `result.ts` loaded: the extension loads and its tool is still absent from the request.
- **A name the allowlist gets wrong is dropped without a word.** `allowlist-typo` names
  `submit_review_resullt`: Pi starts, exits 0, and offers `read_file` alone. Nothing on stdout or
  stderr says a name matched nothing. See §10 for how a host can detect it.
- **A built-in is live exactly when the allowlist names it, and `--no-builtin-tools` changes
  nothing once `--tools` is present.** `no-builtin-tools-is-inert` passes
  `--no-builtin-tools --tools read,submit_review_result` and `read` is still offered.
  `no-builtins-clamped` offers no built-in only because its allowlist names none. "No Pi built-in in
  reach" is therefore a property of the allowlist's contents, not of a flag.
- **Pi has list and search built-ins that are inactive by default.** `inactive-builtins` names
  `read,grep,find,ls` and all four are offered. With no `--tools` they are absent, and `read` alone
  cannot list a directory (`read-directory`: `EISDIR: illegal operation on a directory, read`).
- **`read` is not path-scoped.** It returned a file outside the working directory in both arms. The
  Kiro reviewer's `fs_read` and the OpenCode reviewer's read family were measured the same way, so
  this is an existing reviewer posture. Path scope was not measured for `grep`, `find` or `ls`.

## 3. No approval frame exists

Across every arm that started, the frame types Pi emitted were `agent_start`, `agent_end`,
`agent_settled`, `turn_start`, `turn_end`, `message_start`, `message_update`, `message_end`,
`tool_execution_start`, `tool_execution_update`, `tool_execution_end` and `response`. The control
arm ran `bash`, `write` and `edit` with **no permission, approval or `extension_ui_request` frame**
between `tool_execution_start` and `tool_execution_end`.

Nothing needs pre-granting for an unattended launch. The other half of that fact: there is no
second line of defence. The allowlist is the whole boundary between a reviewer and a shell.

## 4. What a reviewer's prompt inherits

`prompt-inheritance` plants a distinct token in each source and looks for it in the first request.

| Source | Inherited under `--no-approve --no-extensions` | With `--no-context-files --no-skills --no-prompt-templates` |
|---|---|---|
| agent-dir `AGENTS.md` (operator-global) | **yes** | no |
| agent-dir `skills/<name>/SKILL.md` (operator-global) | **yes** | no |
| project `AGENTS.md` | **yes** | no |
| project `CLAUDE.md` (alongside an `AGENTS.md`) | no | no |
| project `.pi/skills/<name>/SKILL.md` | no (`--no-approve`) | no |
| agent-dir `prompts/*.md` | no (templates are not in the system prompt) | no |

No flag separates the project's `AGENTS.md` from the operator's: `--no-context-files` drops both.

## 5. The operator's `SYSTEM.md` and `APPEND_SYSTEM.md` survive all of those flags

Planted in the agent directory, under §11's argv without its last two flags:

| Arm | Extra argv | Tokens in the first request |
|---|---|---|
| `system-files-default` | — | both files' tokens |
| `system-files-append-empty` | `--append-system-prompt ""` | `SYSTEM.md`'s token only |
| `system-files-explicit` | `--system-prompt <text> --append-system-prompt <text>` | the two argv texts; neither file |

`--no-context-files` does not reach these two files. `SYSTEM.md` replaces Pi's whole base prompt.
Only an explicit `--system-prompt` displaces it, which means owning the reviewer's system prompt
outright; an empty `--append-system-prompt` displaces the append file alone.

## 6. Model selection, at effect level

`model-rpc` runs one turn, changes the model over RPC, and runs another. The provider's recorded
`model` field went `m1` → `m2`.

| Command | Response |
|---|---|
| `get_available_models` | `data.models[]`, each with `provider` and `id` |
| `set_model {"model": "m2"}` | `success: false`, `Model not found: undefined/undefined` |
| `set_model {"model": "probe/m2"}` | `success: false`, `Model not found: undefined/undefined` |
| `set_model {"provider": "probe", "modelId": "m2"}` | `success: true`; next request's `model` is `m2`; `get_state.data.model` is `probe/m2` |
| `set_model {"provider": "probe", "modelId": "nope"}` | `success: false`, `Model not found: probe/nope` |

`set_model` takes `{provider, modelId}` and nothing else, the change is real, and an unknown model
fails loudly. `--model provider/id` on argv also takes effect (`model-argv`) and accepts the
combined string `set_model` refuses, so a launch-fixed model never needs `set_model`.

## 7. Session files, and a repository that redirects them

| Arm | `get_state.data.sessionId` | `sessionFile` |
|---|---|---|
| `session-default` | present | under `<agent dir>/sessions/<cwd-slug>/` |
| `--no-session` | present | `null` |
| `--session-dir <dir>` | present | under `<dir>/` |

`hostile-project-settings` commits what a reviewed repository could: a `.pi/settings.json` naming a
`sessionDir`, a `shellPath` and a `packages` entry, plus a `.pi/commands/` directory.

| Arm | `sessionFile` | Attacker-named directory |
|---|---|---|
| full §11 argv, which includes `--session-dir` | under the launch's own directory | not created |
| the same argv **without** `--session-dir` | **under the attacker-named directory** | **created** |

- **`--no-approve` does not stop a repository's `sessionDir` from taking effect.** Pi reads the
  project settings file once through a settings manager that is not told the project is untrusted,
  and the session path is resolved from it. **`--session-dir` outranks it**, so that flag is part of
  the containment boundary and not a tidiness measure.
- **Pi writes into the worktree at startup regardless of trust.** In both arms `.pi/commands/` was
  renamed to `.pi/prompts/`. The renamed templates do not load under `--no-prompt-templates`. This
  is harmless in a disposable owned worktree and is one more reason a borrowed checkout is refused.

## 8. Startup package install, and what offline mode stops

The agent directory's `settings.json` names an npm package that is not installed, and an
`npmCommand` that records its arguments and fails. The argv is `--no-approve --no-extensions -e
result.ts` in both arms.

| Arm | `npmCommand` invocations | Outcome |
|---|---|---|
| `startup-package-install` | `root -g`, then `install <pkg> --prefix <agent dir>/npm --legacy-peer-deps` | **Pi exits 1 before serving RPC** when the install fails |
| `startup-package-install-offline` (adds `--offline`) | `root -g` only | Pi starts normally |

- **`--no-extensions` does not stop Pi installing the operator's configured packages at startup.**
  The install runs before the extension filter applies, and its argv carries no `--ignore-scripts`.
  A reviewer launch without offline mode can therefore run npm lifecycle scripts, reach the network,
  and fail outright on a transient registry error.
- **`--offline` stops the install.** The operator's `npmCommand` is still spawned once for
  `root -g`.

## 9. An unknown flag fails the launch

`unknown-flag` passes `--not-a-real-flag`. Pi prints `Error: Unknown option: --not-a-real-flag` and
exits before answering `get_state`; no request is sent. A build too old to know a containment flag
refuses to start rather than starting uncontained. That covers a *missing* flag only. A build that
knows a flag but gives it a weaker meaning would start normally, which is what a version floor is
for.

## 10. Extension lifecycle: what `get_state` answering proves

`ext/ready.ts` has an async factory that can delay or throw, registers two tools, and on
`session_start` writes `pi.getActiveTools()` and `pi.getAllTools()` to a file. The driver reads that
file at the instant `get_state` answers.

| Arm | `get_state` | Report on disk when it answered | Tools in the first request |
|---|---|---|---|
| `factory-awaited` (factory sleeps 2.5 s) | answered after 2.9 s | present; `active` = both tools | both tools |
| `factory-throws` | never answered; Pi exited 1 in 0.4 s; stderr `Failed to load extension: probe-factory-refused` | — | no request |
| `allowlist-typo` | answered | present; `active` = `read_file` only | `read_file` only |

- **Pi awaits the async factory and the `session_start` handlers before it reads a single RPC
  command.** `get_state` answering therefore proves both completed.
- **A factory that throws ends the process before RPC is served.** An extension that must not run
  degraded can fail the whole launch by throwing, and the host sees a failed handshake.
- **`pi.getActiveTools()` in `session_start` is the tool set Pi goes on to offer.** It matched the
  first request in both arms, including the arm where the allowlist silently dropped a name. No RPC
  command lists tools, so a report written by the extension is the only way for a host to verify the
  live surface before sending a prompt.

## 11. The full argv, accepted together

```
--mode rpc --no-approve --no-extensions -e <file> --tools <allowlist>
--no-context-files --no-skills --no-prompt-templates --no-themes --offline --session-dir <dir>
--system-prompt <file> --append-system-prompt ""
```

Every arm built on it started and answered `get_state`, so the flags are mutually compatible on
0.85.1. `--system-prompt` was given a **file path** and the file's content is what reached the
first request, so Pi's "an existing path, else literal text" reading of that flag behaves as its
source says.

## 12. Read from source, not executed

From the installed package (`dist/` and `docs/`), 0.85.1. Each is a reason for a design choice, not
a measurement.

- **The RPC `bash` command runs a shell regardless of `--tools`.** `rpc-mode.js` routes it to
  `AgentSession.executeBash`, which never consults the tool registry. It is host-initiated, so it is
  reachable only if a host sends it. `export_html` likewise writes a host-named path.
- **A `prompt` whose message begins with `/` runs an extension command or expands a template.**
  Under §11 the only command present is `/llama`, from a built-in extension that always loads,
  `--no-extensions` notwithstanding. It registers that command and no tool.
- **`models.json` and `auth.json` values beginning with `!` are shell commands**, run on the first
  model request to resolve a key or header. They live only in the agent directory; there is no
  project-level `models.json`.
- **`grep` and `find` download `rg` and `fd` from GitHub on first use** when neither is on `PATH`
  nor in the agent directory's `bin/`, unless offline.
- **What `--no-approve` gates** is a fixed list: `.pi/settings.json`, `.pi/extensions`,
  `.pi/skills`, `.pi/prompts`, `.pi/themes`, `.pi/SYSTEM.md`, `.pi/APPEND_SYSTEM.md`, and
  `.agents/skills` in the cwd and its ancestors. It does not gate `AGENTS.md` or `CLAUDE.md`, and §7
  shows the one read of `.pi/settings.json` that escapes it.
- **Themes are JSON, schema-validated, and never reach the model.** `--no-themes` is hygiene.
- **Environment that repoints Pi:** `PI_CODING_AGENT_DIR` (the whole agent directory),
  `PI_PACKAGE_DIR`, `PI_CODING_AGENT_SESSION_DIR` (outranked by `--session-dir`), and jiti's
  `JITI_*` variables, which configure the transpiler that loads an `-e` file.

## 13. Every discovery source at once: control and suppression

One canary per source, each separately named. The control run is `--approve` with no suppression
flag; the suppressed run is §11's argv. Observables differ by kind: an extension shows as a tool
and a load marker, prompt-bearing text as a token in the first request, and skills and templates
also through the `get_commands` RPC, which lists both.

| Source | Scope | Control | Under §11's argv |
|---|---|---|---|
| agent-dir `extensions/` | operator | tool + marker | gone |
| `settings.json` `extensions[]` | operator | tool + marker | gone |
| `settings.json` `packages[]` (local path) | operator | tool + marker | gone |
| agent-dir `skills/` | operator | token + command | gone |
| `~/.agents/skills/` | operator | token + command | gone |
| `settings.json` `skills[]` | operator | token + command | gone |
| agent-dir `prompts/` | operator | command | gone |
| agent-dir `AGENTS.md` | operator | token | gone |
| agent-dir `APPEND_SYSTEM.md` | operator | shadowed by the repository's in this run; measured on its own in §5 | gone |
| `.pi/extensions/` | repository | tool + marker | gone |
| `.pi/skills/` | repository | token + command | gone |
| `.agents/skills/` in the cwd | repository | token + command | gone |
| `.agents/skills/` in an **ancestor** of the cwd | repository | token + command | gone |
| `.pi/prompts/` | repository | command | gone |
| `AGENTS.md` in the cwd | repository | token | gone |
| `CLAUDE.md` in an **ancestor** of the cwd | repository | token | gone |
| `.pi/SYSTEM.md` | repository | token | gone |
| `.pi/APPEND_SYSTEM.md` | repository | token | gone |

Control: 4 canary tools, 11 tokens, 8 canary commands. Suppressed: the one injected tool, **no
token**, and `llama` as the only command. The control is what makes the second column evidence:
every plant was live.

Not covered here, and why: themes have no observable in `--mode rpc` (§12); a `packages` entry that
needs installing is §8; a repository's `sessionDir` is §7.

## 14. Two prompts in one busy period

`queued-prompt` sends a second `prompt` (`streamingBehavior: "followUp"`) 0.4 s into the first
one's turn. Lifecycle frames, in order:

```
response[1 ok] agent_start turn_start user_echo response[2 ok] turn_end
turn_start user_echo turn_end agent_end agent_settled
```

For comparison, one prompt that makes five tool calls (`reviewer-argv-clamped`):

```
agent_start turn_start user_echo turn_end (turn_start turn_end) ×5 agent_end agent_settled
```

- **`agent_start` … `agent_settled` brackets the whole busy period, not one prompt.** A prompt
  queued mid-turn is accepted at once, runs after the current one, and produces no `agent_start` or
  `agent_settled` of its own. `agent_settled` means the queue is empty.
- **`turn_start` / `turn_end` bracket one model call**, so a single prompt with tool use emits
  several pairs. They do not delimit a prompt.
- **What marks a prompt's round beginning is its user-message echo** (`message_end`, role `user`):
  exactly one per accepted prompt, in the order accepted. No frame carries the prompt's `id`, so
  order is the only correlation there is.

---

## What this settles for the reviewer lane

1. Contain with argv, using §11's set. §13 shows it clears every discovery source at once, so an
   isolated agent directory is not needed and no credential is copied.
2. `--session-dir` and `--offline` are containment, not hygiene (§7, §8).
3. The allowlist must name every injected tool and no built-in the lane does not intend. Build it
   from the same list the extension registers from, and have the extension report
   `pi.getActiveTools()` so the host can confirm the two agree before the first prompt (§2, §10).
4. Do everything that can fail inside the extension's async factory and throw on failure: Pi then
   exits before serving RPC, and the launch fails closed (§10).
5. No permission document and no approval bridge are needed. The allowlist is the only boundary, so
   it is the thing to pin in tests and to certify live (§3).
6. A host that drives a reviewer must never send the RPC `bash` or `export_html` commands, and never
   a prompt that begins with `/` (§12).
7. The operator's `SYSTEM.md` reaches a reviewer unless the launch supplies its own system prompt
   (§5).
8. Model: `--model provider/id` on argv. `set_model {provider, modelId}` is verified if a per-round
   change is ever wanted (§6).
9. A per-round time limit must be armed from a prompt's user echo and cleared only on
   `agent_settled`; neither `turn_end` nor a settle event can be matched to one prompt (§14).
10. Borrowed workspaces stay refused: `read` is host-wide and Pi writes into the cwd at startup (§2,
   §7).

## Re-running

```bash
python3 probe.py
```

Pass `--pi <path>` to target another build and `--keep` to retain the scratch directory. A run on a
newer `pi` that still matches this document leaves the floor where it is. A run that diverges is the
finding; record which behaviour changed and at what version.
