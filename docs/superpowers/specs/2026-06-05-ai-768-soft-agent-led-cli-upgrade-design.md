# AI-768 — Soft, agent-led CLI upgrade nudge at session start

Linear: [AI-768](https://linear.app/kurrent/issue/AI-768/check-cli-vs-server-version-at-session-start)

## Problem

When the user's local `kcap` CLI is older than the Kurrent Capacitor server it talks to, nothing in a Claude Code session tells them. The existing stderr update hint (`UpdateCommand.PrintUpdateHintIfAvailable`) hits the npm registry directly and prints on bare `kcap …` invocations, but it never reaches the coding agent's context — so the agent can't proactively offer to upgrade on the user's behalf.

We want a *soft, agent-led* nudge: when the CLI processes a `SessionStart` hook, if the server reports a newer version than the local CLI, inject additional context into Claude telling it a newer kcap is available and to offer the user to install it.

Not a hard version floor. Not a blocker. Not a stderr line. The agent decides whether to surface it; the user decides whether to act.

## Server contract

The existing `POST /hooks/session-start` response body gains one optional field:

```json
{
  "slug": "...",
  "top_clusters": [...],
  "version": "0.6.5"
}
```

- **Field name:** `version` (string, optional).
- **Meaning:** the server's own version. Server and CLI are released hand-in-hand, so by convention this is also the matching CLI version. The server does not query npm or speculate about CLI releases — it reports what it knows about itself.
- **Absent:** older servers that don't ship this field. The CLI silently does nothing. Backward compatible.

Server-side implementation is out of scope for this spec — this document defines the contract the CLI consumes.

## CLI behaviour

On every `SessionStart` (including `resume` and `compact` sources), after the existing `SessionGuidelinesEmitter` step:

1. Read `response.version`.
2. Compare against `CapacitorVersion.Current()` using the same prerelease-stripping, build-metadata-stripping, strict-greater semver comparison the existing stderr hint uses.
3. If `server.version` is strictly newer, emit a second `SessionStart` `hookSpecificOutput` JSON line to stdout whose `additionalContext` field nudges the agent to offer an upgrade.
4. In every other case (absent field, unparseable, equal, CLI ahead, current CLI is `"unknown"`), emit nothing.

The nudge fires on every session-start where versions differ. No caching, no throttle. If procrastinated upgrades become a context-window problem in practice, we can add a cache or opt-out later — not in v1.

The existing `UpdateCommand.PrintUpdateHintIfAvailable` (npm-registry-based stderr hint) is untouched. It serves a different surface (humans running `kcap` directly) and the two can disagree harmlessly across short windows.

## Components

### New: `src/Capacitor.Cli/VersionNudgeEmitter.cs`

Pure-function emitter mirroring the shape of `SessionGuidelinesEmitter`:

```csharp
static class VersionNudgeEmitter {
    /// <summary>
    /// Returns the SessionStart hookSpecificOutput JSON envelope nudging the
    /// user to upgrade kcap, or null when no nudge is needed (server didn't
    /// supply a version, CLI is current, CLI is ahead, versions unparseable,
    /// or current CLI is "unknown").
    /// </summary>
    public static string? BuildAdditionalContext(JsonNode? responseNode, string currentCliVersion);
}
```

The emitted `additionalContext` string is two short lines:

```
A newer kcap version is available: <current> → <server>.
Offer the user to upgrade by running: npm install -g @kurrent/kcap
```

Phrased to invite the agent to *propose* the install command, not to silently run it — the agent's normal tool-permission flow still applies.

### Shared semver helper

`UpdateCommand.IsNewer` and its prerelease/build-metadata stripping logic move to a `public static` helper that both `UpdateCommand` and `VersionNudgeEmitter` call. Likely a small class such as `Capacitor.Cli.Core.SemverCompare` (in Core, since both call sites can reach Core), or static methods on `CapacitorVersion`. Existing stderr-hint behaviour is unchanged — same comparison, just relocated.

### Call site: `ClaudeHookCommand` session-start case

In `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`, inside the existing `case "session-start":` branch, immediately after the `SessionGuidelinesEmitter.BuildAdditionalContext(...)` emit:

```csharp
try {
    var nudge = VersionNudgeEmitter.BuildAdditionalContext(responseNode, CapacitorVersion.Current());
    if (nudge is not null) Console.WriteLine(nudge);
} catch {
    // Best effort — never break session capture for an upgrade nudge.
}
```

Wrapped in try/catch like every other best-effort emit in this case.

## Testing

### New unit tests — `test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs`

Pure-function tests on `BuildAdditionalContext`:

- Returns `null` when response has no `version` field.
- Returns `null` when `version` is empty / whitespace / unparseable.
- Returns `null` when `currentCliVersion` equals `server.version`.
- Returns `null` when `currentCliVersion` is strictly newer than `server.version` (dev-build / prerelease case).
- Returns `null` when `currentCliVersion` is `"unknown"`.
- Returns `null` when prerelease suffixes make the cores equal (`0.6.5-alpha.1` vs `0.6.5`).
- Returns `null` when build metadata makes the cores equal (`0.6.5+sha` vs `0.6.5`).
- Returns a JSON string for a `SessionStart` `hookSpecificOutput` envelope when server is strictly newer.
- The emitted `additionalContext` contains both versions and the literal `npm install -g @kurrent/kcap`.
- Emitted JSON is parseable and `hookEventName == "SessionStart"`.

### Shared semver helper tests

If `IsNewer` moves out of `UpdateCommand`, relocate any existing comparison tests to `SemverCompareTests.cs` (or equivalent). No new edge cases.

### Integration test — `test/Capacitor.Cli.Tests.Integration/HookRoundTripTests.cs`

Two symmetric cases that exercise the full session-start round-trip against the existing WireMock harness:

- Server returns a `version` strictly newer than `CapacitorVersion.Current()` → CLI stdout contains a `SessionStart` `hookSpecificOutput` line referencing the upgrade.
- Server returns a `version` equal to `CapacitorVersion.Current()` (or omits the field) → no nudge line emitted.

## Docs

- `README.md` — brief mention under the auto-capture description that the agent may surface an upgrade prompt when a newer kcap is available. No new commands or flags, so no quick-start or `## CLI commands` changes.
- No `Resources/help-*.txt` changes.

## Edge cases and behaviour matrix

| `server.version` | `cliCurrent`            | Outcome              |
|------------------|-------------------------|----------------------|
| absent           | any                     | no nudge             |
| empty / garbage  | any                     | no nudge             |
| `"0.6.5"`        | `"0.6.3"`               | **nudge**            |
| `"0.6.5"`        | `"0.6.5"`               | no nudge             |
| `"0.6.5"`        | `"0.6.5-alpha.1"`       | no nudge             |
| `"0.6.5"`        | `"0.6.5+abc"`           | no nudge             |
| `"0.6.5"`        | `"0.7.0"`               | no nudge (CLI ahead) |
| `"0.6.5"`        | `"unknown"`             | no nudge             |
| `"0.6.5"`        | unparseable             | no nudge             |

## Out of scope

- Server-side implementation of the `version` field (separate ticket, separate repo).
- Changes to `UpdateCommand.PrintUpdateHintIfAvailable` — coexists unchanged.
- User-facing toggle to suppress the in-agent nudge (can be added later if needed).
- Branching by install method (brew, manual). `npm install -g` is the only documented channel.
- Hard version floors or blocking behaviour. Explicitly soft by design.
- Caching or throttling. Explicitly fire every session-start; revisit only if we hear complaints.

## Risks

- **Context-window cost.** The nudge fires every session-start while a user is behind. Mitigated by keeping the prompt to two short lines; revisitable if it becomes a real complaint.
- **Agent ignoring the nudge.** Working as intended — "soft, agent-led" means the agent may judge it off-topic in some sessions.
- **AOT trimming.** No new patterns introduced; `JsonNode` parsing is already used by the existing emitter. No new IL3050/IL2026 risk.
