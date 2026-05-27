# Wider skill installation via `~/.agents/skills/`

Linear: [AI-698](https://linear.app/kurrent/issue/AI-698/wider-installation-of-skills)

## Problem

Today the kapacitor plugin ships two near-duplicate skill sets:

- `kapacitor/skills/` — loaded by the Claude Code plugin. Folder names are
  `session-recap`, `session-errors`, `session-disable`, `session-hide`,
  `validate-plan`. SKILL.md content mentions `KAPACITOR_SESSION_ID` and the
  `kapacitor-sessions` MCP server.
- `kapacitor/codex-skills/` — copied by `kapacitor plugin install --codex`
  into `~/.codex/skills/`. Folder names are `kapacitor-recap`,
  `kapacitor-errors`, `kapacitor-disable`, `kapacitor-hide`,
  `kapacitor-validate-plan`. SKILL.md content mentions `CODEX_THREAD_ID`.

The content diverges only in two places: env-var name, and an MCP hint. The
two folder name schemes exist for cosmetic reasons. Maintaining both has
already caused drift in skill descriptions (different trigger phrases,
different headings) and will keep doing so.

Meanwhile both Codex (0.81+) and Cursor have converged on a shared
filesystem convention for skills:

- Project: `.agents/skills/`
- User: `~/.agents/skills/`

Cursor also reads `~/.codex/skills/` for legacy compatibility, so a user
who has previously run `kapacitor plugin install --codex` will see our
old `kapacitor-*` skills under Cursor — alongside any new skills we ship
under `~/.agents/skills/`, producing duplicates.

## Goal

Install kapacitor skills once, to `~/.agents/skills/`, so every agent that
honors the `.agents/skills` convention (Codex, Cursor, future) picks them
up. Keep agent-specific concerns (Codex hooks, MCP registration) where
they belong.

## Non-goals

- Project-level (`./.agents/skills/`) skill installation. The existing
  `--project` flag continues to affect hooks only, matching today's
  documented behaviour.
- Renaming `KAPACITOR_SESSION_ID` or `CODEX_THREAD_ID`. The Codex CLI
  controls the latter; renaming the former unifies nothing.
- Cursor-specific hooks or MCP registration. Cursor adoption of the
  shared `.agents/skills/` directory is the entire point of this work;
  going further is out of scope.

## Design

### Single skill source

Delete `kapacitor/codex-skills/`. The only skill source becomes
`kapacitor/skills/`. Source folder names drop the `session-` prefix so
they match the `name:` frontmatter and require no rewriting when copied
into a Claude plugin:

| Today (`kapacitor/skills/`) | After |
|---|---|
| `session-recap` | `recap` |
| `session-errors` | `errors` |
| `session-disable` | `disable` |
| `session-hide` | `hide` |
| `validate-plan` | `validate-plan` (unchanged) |

The Claude Code plugin loads from `kapacitor/skills/` natively; users
invoke skills as `kapacitor:recap`, `kapacitor:errors`, etc. (the
`kapacitor:` namespace is supplied by the plugin manifest, not the folder
name). Existing trigger phrases in SKILL.md frontmatter are unchanged —
only folder names and `name:` field shift.

### Agent-agnostic skill content

Two rules apply to every SKILL.md:

1. No session-id env-var names appear in the body (no
   `KAPACITOR_SESSION_ID`, no `CODEX_THREAD_ID`). Other env vars that
   are agent-agnostic and useful to document — e.g. `KAPACITOR_URL` —
   remain.
2. No agent names appear in the body (no Claude, no Codex, no Cursor).

Each skill keeps its own CLI command (`kapacitor recap`, `kapacitor
errors`, `kapacitor validate-plan`, `kapacitor hide`,
`kapacitor disable`). The session-id paragraph in every skill uses this
shape, substituting the skill's own command:

> Run `kapacitor <command>`. It resolves the current session id from
> the environment when the host agent CLI exposes one. If no session
> id is available, pass it explicitly: `kapacitor <command>
> <sessionId>`.

The CLI accepts `[sessionId]` as a positional argument for every one of
these subcommands today — no flag change. `kapacitor recap` and
friends already resolve session id from `KAPACITOR_SESSION_ID` or
`CODEX_THREAD_ID` (commit `2ecaa53b8`); no CLI change required.

The MCP hint currently in `kapacitor/skills/session-recap/SKILL.md`
("when the `kapacitor-sessions` MCP server is available, prefer its
tools…") is preserved — it is true for any agent that has the MCP
registered, not Claude-specific.

### Install target

Skills install to `~/.agents/skills/kapacitor-{recap,errors,disable,hide,validate-plan}/`.

The `kapacitor-` prefix on the **install path** prevents collision in a
flat directory shared with other tools' skills. The prefix is applied at
copy time:

1. Read `kapacitor/skills/recap/SKILL.md`.
2. Write to `~/.agents/skills/kapacitor-recap/SKILL.md`, rewriting the
   `name:` frontmatter from `recap` to `kapacitor-recap`. Other
   frontmatter and body content are copied verbatim.
3. Repeat for every regular file under the source folder
   (e.g. `references/*`, `scripts/*`) — only `SKILL.md`'s `name:`
   field is rewritten.

User-level only. `--project` does not write skills anywhere; the today's
help text rule ("Skills are always user-wide; --project only affects
hooks") continues to hold.

### CLI surface

MCP server registration is **out of scope** for this work. It is already
handled by the Codex plugin manifest's `mcpServers` field
(`kapacitor/.codex-plugin/plugin.json` → `./.codex-mcp.json`,
auto-registered per commit `3dac8f6d5`). Our installer continues to write
only hooks + skills, plus the new agents-skills target.

| Command | Effect |
|---|---|
| `kapacitor plugin install --skills` | New. Installs only `~/.agents/skills/kapacitor-*`. Agent-agnostic. Runs legacy cleanup after a successful install. |
| `kapacitor plugin install --codex` | Installs Codex hooks (`~/.codex/hooks.json` or project) and `~/.agents/skills/kapacitor-*`. Single bundled action. Runs legacy cleanup after a successful install. MCP registration is not touched (handled by the Codex plugin manifest). |
| `kapacitor plugin install` (default, Claude) | Installs the Claude Code plugin only. Does **not** write to `~/.agents/skills/` — the Claude plugin loads its own copy from `kapacitor/skills/`. |
| `kapacitor plugin remove --skills` | Removes `~/.agents/skills/kapacitor-*` and legacy `~/.codex/skills/kapacitor-*`. |
| `kapacitor plugin remove --codex` | Removes Codex hooks, `~/.agents/skills/kapacitor-*`, and legacy `~/.codex/skills/kapacitor-*`. `--codex` is the inverse of `install --codex`: it removes exactly what that install wrote. |

### Bootstrap (`CodingAgentsStep`) UX

If Codex CLI is detected, one Y/N prompt covers everything the Codex
install path writes: hooks + agent skills. Prompt wording:

> Install Codex CLI hooks and kapacitor agent skills?

No additional prompt is shown for skills alone. Users who only want
skills (no Codex) can run `kapacitor plugin install --skills` directly.
MCP registration is independent and handled by the Codex plugin
manifest, not the bootstrap flow.

### Legacy cleanup

On `--codex` or `--skills` install, the new `~/.agents/skills/kapacitor-*`
folders are written **first**. Only after every target install
succeeds does the installer remove each of these legacy directories if
present:

- `~/.codex/skills/kapacitor-recap`
- `~/.codex/skills/kapacitor-errors`
- `~/.codex/skills/kapacitor-disable`
- `~/.codex/skills/kapacitor-hide`
- `~/.codex/skills/kapacitor-validate-plan`

This ordering means a partial install never leaves the user with
neither the old nor the new skills available. If the new install fails
for any reason, the legacy folders are untouched.

One log line per removed folder. The list is fixed (we own those
names); no globbing, no risk of touching user-authored skills. If
`~/.codex/skills/` ends up empty after removal, it is also removed.

`kapacitor plugin remove --codex` and `kapacitor plugin remove --skills`
run the same legacy cleanup unconditionally (no new install to gate on).

### New / renamed modules

- **New:** `AgentsPaths` in `Kapacitor.Cli.Core`. Mirrors `CodexPaths`.
  Exposes `Home` (defaults to `$HOME`, overrideable via the same env-var
  isolation pattern as `CodexPaths` for tests) and
  `UserSkillsDir => Path.Combine(Home, ".agents", "skills")`.
- **New:** `AgentsSkillsInstaller` in `Kapacitor.Cli.Core`. Responsible
  for the copy + prefix + frontmatter rewrite, and for the legacy
  `~/.codex/skills/` cleanup. Extracted from `PluginCommand` so it can
  be unit-tested without going through the command-line surface.
- **Remove:** `CodexPaths.UserSkillsDir`. The Codex install path no
  longer writes there. `CodexPaths` retains `Home`, `Sessions`, and
  `UserHooksJson`.
- **Refactor:** `PluginCommand.InstallCodexSkills` / `RemoveCodexSkills`
  become `AgentsSkillsInstaller.Install(sourceDir, targetDir)` /
  `.Remove(targetDir)`. `sourceDir` is resolved by the caller (today's
  `Path.Combine(pluginPath, "codex-skills")`; after this change,
  `Path.Combine(pluginPath, "skills")`). The legacy `~/.codex/skills/`
  cleanup is a separate static method (`CleanLegacyCodexSkills()`)
  invoked by both install and remove paths.

### Codex plugin manifest

`kapacitor/.codex-plugin/plugin.json` currently declares:

```json
{
  "skills": "./codex-skills/",
  "mcpServers": "./.codex-mcp.json"
}
```

Deleting `kapacitor/codex-skills/` would break this manifest for any
user who installs via Codex's native plugin loader (e.g. `codex plugin
add <path>`) rather than `kapacitor plugin install --codex`.

The `skills` field is **dropped** from the manifest. After the change
it declares only `mcpServers`. Rationale:

- If `skills` pointed at `./skills/` instead, Codex's plugin loader
  would expose those skills under their unprefixed names (`recap`,
  `errors`, …). A user who additionally runs `kapacitor plugin install
  --skills` would then have the same skills present twice — once
  unprefixed inside the Codex plugin scope, once prefixed under
  `~/.agents/skills/kapacitor-*`. Codex skill discovery treats those
  as distinct skills.
- Keeping skills out of the manifest means there is exactly one path
  to install kapacitor skills (`kapacitor plugin install --skills`,
  bundled by `--codex`). The Codex plugin manifest owns MCP server
  registration; the CLI installer owns hooks; `~/.agents/skills/`
  owns skills. Clean separation.

Hooks are not loaded from the Codex plugin manifest — per
[openai/codex#17331](https://github.com/openai/codex/issues/17331), hook
configuration is read from `hooks.json` written by our installer, not
from `plugin.json`. The manifest's role is limited to MCP server
registration. Users who install via Codex's native plugin loader
therefore get MCP only; they must run `kapacitor plugin install --codex`
(for hooks) and/or `kapacitor plugin install --skills` (for skills)
separately. This is called out in the help text and README.

### Source folder rename impact

Renaming `kapacitor/skills/session-recap/` → `kapacitor/skills/recap/`
(etc.) is a folder rename in source. Code references to the old names:

- `src/Kapacitor.Cli/Commands/PluginCommand.cs` line 19 — the
  `KnownSkillFolders` list (currently `kapacitor-*`). Becomes the list of
  source folder names (`recap`, `errors`, …) with prefix applied at
  install time.
- `src/Kapacitor.Cli/Commands/CodingAgentsStep.cs` line 68 — log line
  listing skill names. Updates to new names.
- `kapacitor/README.md` skills table.
- `src/Kapacitor.Cli.Core/Resources/help-plugin.txt`.
- Top-level `README.md`.
- `kapacitor/.codex-plugin/plugin.json` — drop the `skills` field.
- `../kapacitor-web` getting-started and commands pages (per
  ref_kapacitor_web memory).

Existing Claude users who had `kapacitor:session-recap` muscle memory
must learn `kapacitor:recap`. Five names, contained, mirrors the AI-693
precedent (`history` → `import`).

## Backwards compatibility

- Users on the prior version who ran `kapacitor plugin install --codex`
  have `~/.codex/skills/kapacitor-*`. The next install or remove
  (either `--codex` or `--skills`) cleans these up automatically.
- Cursor users who never ran `kapacitor plugin install --codex` are
  unaffected until they run `kapacitor plugin install --skills` (or
  `--codex`).
- The Codex CLI continues to find skills because Codex reads
  `~/.agents/skills/` in addition to `~/.codex/skills/`.

### Known sharp edge

A developer who uses both Claude Code and Cursor (and not Codex) must
run two commands: `kapacitor plugin install` for the Claude plugin and
`kapacitor plugin install --skills` for `~/.agents/skills/`. Auto-firing
`--skills` from the default Claude install is rejected because (a) the
Claude plugin already provides those skills inside Claude, so the extra
copy is wasted for Claude-only users, and (b) we cannot detect Cursor's
presence reliably. If this pairing becomes common, a future change can
add a `--also-skills` flag to the default install or detect Cursor via
`~/.cursor/` config presence — out of scope here.

## Testing

New tests:

- `AgentsPathsTests` — `UserSkillsDir` resolution, env-var isolation.
- `AgentsSkillsInstallerTests`:
  - Copy applies `kapacitor-` prefix to folder name.
  - `SKILL.md` `name:` frontmatter is rewritten; body and other
    frontmatter fields are untouched.
  - Nested files (e.g. `references/*.md`) are copied verbatim.
  - Existing kapacitor-owned folders in the target are overwritten on
    re-install.
  - User-authored folders in `~/.agents/skills/` are left alone.
  - Legacy cleanup removes only the five known
    `~/.codex/skills/kapacitor-*` folders and never anything else.
  - Empty `~/.codex/skills/` is removed after cleanup; non-empty is
    preserved.
  - When the `~/.agents/skills/` install step fails (e.g. permission
    error, missing source folder), legacy `~/.codex/skills/kapacitor-*`
    folders are **not** removed. Verified by injecting a failing
    target write and asserting legacy folders survive untouched.
- `PluginCommandSkillsTests` — `kapacitor plugin install --skills` and
  `kapacitor plugin remove --skills` end-to-end against a fake home.

Updated tests:

- `PluginCommandCodexTests` — Codex install writes hooks and
  `~/.agents/skills/kapacitor-*` (not `~/.codex/skills/`). Codex install
  does **not** write MCP config (that path was never exercised by our
  installer). Codex remove cleans hooks + `~/.agents/skills/kapacitor-*`
  + legacy `~/.codex/skills/kapacitor-*`.
- `CodingAgentsStepTests` — single-prompt bootstrap still produces a
  successful Codex install plus agent-skills install.
- `CodexPathsTests` — drop `UserSkillsDir` assertions.

## Docs to update in the same PR

- `kapacitor/README.md` — skills section, install paths.
- `src/Kapacitor.Cli.Core/Resources/help-plugin.txt` — `--skills` flag,
  new install paths, updated examples.
- Top-level `README.md` — getting-started flow and per-command CLI
  surface (per CLAUDE.md README-sync rule).
- `../kapacitor-web` — getting-started and `commands.md` (per
  ref_kapacitor_web auto-memory).

## Rollout

Single PR. No feature flag — the install command is one-shot and the
legacy cleanup is automatic, so upgrading users self-heal on their next
`kapacitor plugin install --codex` or first `--skills` run. The Claude
plugin folder rename (`session-recap` → `recap`) ships in the same PR;
no two-step migration.
