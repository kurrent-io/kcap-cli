# AI-613 — History import scope: explicit opt-in

## Problem

`kapacitor history` discovers every transcript under `~/.claude/projects` (and Codex sessions) and uploads them by default. The only filter is `excluded_repos` (a denylist with a per-repo y/N prompt on TTY, auto-skip on non-TTY). Users with personal projects mixed in with work repos can accidentally leak private sessions on the first import. The CLI already warns about this, but the warning is easy to miss and the consequences (sensitive content on a shared server) are hard to undo.

AI-613 wants the default to flip: the user explicitly chooses what to import, and gets a confirmation prompt before anything moves to the server.

## Goal

Make `kapacitor history` require an explicit scope choice — either via a flag or an interactive picker — and always show a confirmation summary before any session is sent.

## Out of scope

- Server-side visibility-on-private-repo auto-flagging. AI-613's second bullet ("mark as Only Visible to You") is honoured here through the existing `default_visibility` config plus a new `--private` flag, not through automatic per-repo inference. Keeping defaults consistent across modes avoids surprising users who have already configured `default_visibility`.
- Identity verification via `claude auth status` / `codex auth`. Considered and rejected: Claude reports a Claude.ai identity (email, org name), not a GitHub identity that maps to repo ownership, and Codex doesn't expose an equivalent. Active profile already names the GitHub org.

## Design

### Scope flags

History gains four mutually-exclusive scope flags. Exactly one must resolve before classify/import runs:

| Flag | Behaviour |
|------|-----------|
| `--all` | Every discovered session. |
| `--org` | Only sessions whose detected repo `owner` matches the active profile's GitHub org (i.e. `ProfileConfig.ActiveProfile`). |
| `--repo <owner/name>` | Only sessions whose detected repo matches that exact `owner/name`. |
| `--repo .` (alias `--repo current`) | Only sessions whose detected repo matches the repo at the current working directory (resolved via `RepositoryDetection.DetectRepositoryAsync`). |

Sessions without a detectable repo (cwd outside a git working tree, no `origin` remote, parse failure) are only included under `--all`.

### Plus

- `--yes` / `-y` — skip the confirmation prompt.
- `--private` — after import, PUT `visibility=none` for every session imported by this run (same endpoint as `kapacitor hide`). Overrides `default_visibility`.

### Interactive picker

When stdin and stdout are both interactive and no scope flag is passed, present a Spectre `SelectionPrompt`:

1. **Everything** — every discovered session.
2. **Org repos only (`<ActiveProfile>`)** — display the resolved org name.
3. **Specific repository** — opens a sub-picker.

The sub-picker is built from:
- The current cwd's repo (if any), marked `(current)` and pinned to the top.
- The union of distinct `owner/name` values detected across discovered transcripts (alphabetised after the current entry).

### Confirmation step

Once scope is resolved (flag or picker), always print a summary block and prompt y/N — unless `--yes` is set:

```
About to import:
  scope:   org repos only (EventStore)
  matched: 47 sessions across 6 repos
  repos:   EventStore/kapacitor, EventStore/kurrentdb, …
  visibility: org_public (from profile)        # or 'private (--private)'
Continue? [y/N]
```

`--yes` prints the same summary, skips the prompt.

### Non-interactive handling

| TTY? | Scope flag? | `--yes`? | Behaviour |
|------|-------------|----------|-----------|
| yes | no | — | Show picker, then confirmation prompt |
| yes | yes | no | Show confirmation prompt |
| yes | yes | yes | Print summary, proceed |
| no | yes | yes | Print summary, proceed |
| no | yes | no | **Error**: `--yes is required for non-interactive use` |
| no | no | — | **Error**: `--all, --org, or --repo <owner/name> is required for non-interactive use` |

Error exit code is 1. Erring rather than defaulting to a safe scope is intentional: AI-613 is about *eliminating* accidental imports, and a default scope in CI/scripts can drift over time.

### Composition with existing filters

These continue to work unchanged and are applied **before** scope filtering:

- `--cwd <path>` — path filter at discovery.
- `--session <id>` — exact id filter; bypasses the picker but still subject to confirmation (so the user sees what's about to happen).
- `--min-lines <n>` — transcript length floor.
- `--since YYYY-MM-DD` — date filter.

`excluded_repos` (profile config) continues to apply as an **additional denylist** after scope filtering. Rationale: it composes well — a user with `--org` who wants to skip one specific org repo can still do so via `excluded_repos`, and profile-scoped denylists already work today.

### Resolving "your org"

`ProfileConfig.ActiveProfile` is the org's GitHub login (set by `TenantDiscovery.MergeProfiles` at setup time). `--org` uses it directly.

If the active profile is `"default"` (no tenant discovery has run), `--org` errors:

```
--org requires a tenant-bound profile. Run `kapacitor setup` first,
or use --all / --repo <owner/name>.
```

### Visibility behaviour

- Default path: no change. `default_visibility` from the active profile flows through `session-start` hooks today and applies to imported sessions identically.
- `--private` path: after the import phase completes (and before background title/summary work), iterate over the successfully-imported `SessionId` list and `PUT /api/sessions/{id}/visibility {"visibility":"none"}` for each. Reuse the same retry/auth client used by `Program.cs`'s `hide` case.
- Failures here are logged inline (one line per session) but do not fail the run — the import already succeeded and the user can re-run `kapacitor hide` if needed.

### Setup-time touchpoint

`kapacitor setup` does **not** change its step list. AI-613 mentions "as part of setup OR history" — handling it in `history` is sufficient because (a) setup runs once but history runs repeatedly, and (b) a setup-time choice can't reflect repos discovered after the fact. We add a one-line nudge at the end of setup:

```
Tip: run `kapacitor history --org` to import sessions from your org's repos.
```

## Implementation outline

### File changes

- `src/kapacitor/Program.cs` — extend the `history` argument parser; add `--all`, `--org`, `--repo <value>`, `--yes`/`-y`, `--private`. Validate mutual exclusivity. Pass through to `HandleHistory`.
- `src/kapacitor/Commands/HistoryCommand.cs` — extend `HandleHistory` signature with `ImportScope scope`, `bool yes`, `bool forcePrivate`. Insert two new pipeline steps after discovery + pre-filters: (1) scope resolution (picker or flag), (2) confirmation. Wire a post-import visibility loop for `--private`.
- `src/kapacitor/Commands/HistoryScopePrompt.cs` (new) — Spectre picker for scope selection and the "specific repository" sub-picker.
- `src/kapacitor/Commands/SetupCommand.cs` — append the one-line tip after setup completes.
- `test/kapacitor.Tests.Unit/Commands/HistoryScopeTests.cs` (new) — pure-function tests for the scope filter and flag validation.
- `test/kapacitor.Tests.Integration/HistoryImportPrivateTests.cs` (new) — WireMock test exercising the `--private` post-import visibility loop.

### New types

```csharp
internal abstract record ImportScope {
    public sealed record All  : ImportScope;
    public sealed record Org  (string OrgLogin) : ImportScope;
    public sealed record Repo (string Owner, string Name) : ImportScope;
}
```

`ImportScope.Org`'s `OrgLogin` is resolved once at flag-parse time from `ProfileConfig.ActiveProfile`.

### Scope filter (pure function)

```csharp
internal static List<(string SessionId, string FilePath, string EncodedCwd)>
    ApplyScopeFilter(
        List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
        ImportScope scope,
        Func<string, Task<(string? Owner, string? Name)>> resolveRepo)
```

Returns the filtered list. `resolveRepo` is injected so unit tests can stub it; production wires it to `RepositoryDetection.DetectRepositoryAsync` over the transcript's cwd.

### Argument parsing

Mutual exclusivity check runs in `Program.cs` before `HandleHistory` is called. Order of resolution:

1. Parse `--all`, `--org`, `--repo <value>`. Count true/non-null among the three; if >1 error.
2. If 0 and stdin is a TTY → leave `scope = null`, picker runs in `HandleHistory`.
3. If 0 and stdin is not a TTY → error.
4. If `--repo` value is `.` or `current` → resolve cwd's repo synchronously; if no repo, error with "cwd is not a git repo with an origin remote".
5. If `--org` → check `ProfileConfig.ActiveProfile != "default"`; error otherwise.

### Confirmation

Shared `PromptConfirm(scope, count, repoSamples, visibility)` writes the summary to stderr (so stdout redirection doesn't hide it), then reads from stdin if `--yes` is not set.

### `--private` post-import

After `ImportChainsAsync` returns:

```csharp
if (forcePrivate) {
    foreach (var sessionId in importedSessionIds) {
        var payload = new JsonObject { ["visibility"] = "none" };
        await httpClient.PutWithRetryAsync(
            $"{baseUrl}/api/sessions/{sessionId}/visibility",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
    }
}
```

Failures are caught per-session and logged, never thrown.

## Testing

### Unit

- `ApplyScopeFilter` exhaustive cases:
  - `All` includes everything (including sessions without detectable repo).
  - `Org("EventStore")` includes EventStore-owned, excludes others.
  - `Repo("EventStore", "kapacitor")` includes exact match only.
  - `Org` excludes sessions where `resolveRepo` returns `(null, null)`.
- Flag-parse mutual exclusivity (every conflicting pair errors).
- `--repo .` with cwd outside a git repo errors with the expected message.
- `--org` with `ActiveProfile == "default"` errors.

### Integration

- WireMock fixture: 3 imported sessions + `PUT /api/sessions/{id}/visibility` returns 200 for each. Verify the loop posts exactly the right ids with the right body.
- WireMock fixture: visibility PUT returns 500 for one session — run still completes with exit 0, the failure is logged.

### Manual smoke

- Run `kapacitor history` in a directory containing both `EventStore/kapacitor` sessions and personal sessions; pick "Org repos only", verify only EventStore sessions appear in the matched list.
- Run `kapacitor history --repo . --yes --private` inside `EventStore/kapacitor`; verify only this repo's sessions import and all show `visibility: none` on the server.
- Run `kapacitor history --org` in CI (no TTY, no `--yes`) → expect error and exit code 1.

## Migration / compatibility

- Existing callers that script `kapacitor history` without flags on a TTY will now see the picker. This is a behaviour change but matches the issue's intent.
- Existing CI/automation calling `kapacitor history` without flags on non-TTY will start erroring. Document this prominently in the release notes; suggest `--all --yes` for the previous behaviour.
- `excluded_repos` config is preserved.
- `default_visibility` config is preserved.
- No config schema changes.

## Open implementation questions

- Confirmation summary's `repos:` line — cap at 5 with `, …+N more` after to avoid wall-of-text for large imports?
- Should `--private` be the default when `--repo <single>` resolves to a repo outside the active profile's org? Probably no (consistency with rule b), but worth a re-read once the patch is up.
